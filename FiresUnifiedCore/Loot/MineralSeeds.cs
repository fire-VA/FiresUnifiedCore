using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Loot
{
    /// <summary>
    /// Mineral seeds (Fire 10-24: "a super rare drop from minerocks of a 'mineral seed'... copper seeds, iron seeds... to 'grow' mineable
    /// resources at base for automation... auto regrow its mineral after it is depleted after a set amount of days").
    ///   Which: every MineRock / MineRock5 prefab's own drop table is read at load; each vanilla ore it drops gets a seed (copper, tin,
    ///     iron scrap, silver, obsidian, flametal...). No hard-coded rock list, so modded rocks that drop a vanilla ore work too.
    ///   Drops: when a rock area breaks, its owner only ASKS the server; the server checks the rock and the sender's distance, rate-caps
    ///     the peer and rolls [MineralSeeds] Drop chance (wild rock) or Seed deposit drop chance (a seed deposit, by the server's own mark),
    ///     then makes the seed drop itself. A client never mints a seed.
    ///   Planting: the Cultivator gets one "&lt;ore&gt; seed" piece per seed, a runtime clone of a vanilla sapling whose Plant grows into the
    ///     matching vanilla deposit after Grow days (no cultivated soil needed; vanilla's own grow space applies). No new asset files.
    ///   Regrowing: when it grows, the server checks the spot (the planter's own ward or none, Max per ward) and writes a "seed" mark into
    ///     Core's respawn service (RespawnMarks / LootRespawn): mined out, the same deposit comes back after Regrow days, into a clear spot.
    /// Off by default ([MineralSeeds] Enabled); the server's values decide.
    /// </summary>
    public static class MineralSeeds
    {
        private const string RollRpc = "FiresCore_MineralSeedRoll", GrownRpc = "FiresCore_MineralGrown";
        private const string SeedPrefix = "MineralSeed_", SaplingPrefix = "MineralSapling_";
        private static readonly string[] Ores = { "CopperOre", "TinOre", "IronScrap", "SilverOre", "Obsidian", "FlametalOreNew", "FlametalOre", "BlackMetalScrap" };
        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_chance, s_seedChance, s_growDays, s_regrowDays;
        private static ConfigEntry<int> s_perWard;

        // ore item -> the rock that drops it, rock -> its seed item, seed -> sapling piece
        private static readonly Dictionary<string, string> s_rockOfOre = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> s_seedOfRock = new Dictionary<string, string>();
        private static readonly HashSet<string> s_seedRocks = new HashSet<string>();
        private static readonly Dictionary<long, float> s_nextRoll = new Dictionary<long, float>();
        private static GameObject s_root;
        private const string Tag = "[MineralSeeds] ";

        internal static void BindConfig(ConfigFile cfg)
        {
            s_enabled = cfg.Bind("MineralSeeds", "Enabled", false, "Rare mineral seeds drop from ore rocks; planted with the Cultivator they grow into a deposit that regrows after it is mined out. The server's setting decides.");
            s_chance = cfg.Bind("MineralSeeds", "Drop chance", 0.002f, new ConfigDescription("Chance per broken ore-rock area that a seed drops (0.002 = 0.2 %).", new AcceptableValueRange<float>(0f, 1f)));
            s_seedChance = cfg.Bind("MineralSeeds", "Seed deposit drop chance", 0.0002f, new ConfigDescription("The same chance on a deposit grown from a seed (rarer: 0.0002 = 0.02 %).", new AcceptableValueRange<float>(0f, 1f)));
            s_growDays = cfg.Bind("MineralSeeds", "Grow days", 3f, new ConfigDescription("In-game days from planting until the deposit stands.", new AcceptableValueRange<float>(0.1f, 60f)));
            s_regrowDays = cfg.Bind("MineralSeeds", "Regrow days", 3f, new ConfigDescription("In-game days after a seed deposit is mined out until it is back.", new AcceptableValueRange<float>(0.1f, 60f)));
            s_perWard = cfg.Bind("MineralSeeds", "Max per ward", 20, new ConfigDescription("Seed deposits per ward (or per 32 m without one).", new AcceptableValueRange<int>(1, 500)));
        }

        private static bool Enabled => s_enabled != null && s_enabled.Value;
        private static float DaySeconds => EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;

        // ---- runtime prefabs (every peer builds the same ones, by name) ---------------------------------------------------------
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_MineralSeeds
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(ZNetScene __instance)
            {
                try { Build(__instance); } catch (Exception e) { Debug.LogWarning(Tag + "build: " + e); }
            }
        }

        private static void Build(ZNetScene zs)
        {
            var odb = ObjectDB.instance;
            if (zs == null || odb == null) return;
            s_rockOfOre.Clear(); s_seedOfRock.Clear(); s_seedRocks.Clear();
            if (s_root == null) { s_root = new GameObject("FiresMineralSeedPrefabs"); s_root.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(s_root); }
            foreach (var go in zs.m_prefabs.Where(p => p != null).OrderBy(p => p.name, StringComparer.Ordinal))
            {
                DropTable table = go.GetComponent<MineRock5>()?.m_dropItems ?? go.GetComponent<MineRock>()?.m_dropItems;
                if (table?.m_drops == null) continue;
                foreach (var d in table.m_drops)
                {
                    string ore = d.m_item != null ? d.m_item.name : null;
                    if (ore == null || !Ores.Contains(ore)) continue;
                    if (!s_rockOfOre.ContainsKey(ore) || go.GetComponent<MineRock5>() != null && zs.GetPrefab(s_rockOfOre[ore])?.GetComponent<MineRock5>() == null) s_rockOfOre[ore] = go.name;
                    if (!s_seedOfRock.ContainsKey(go.name)) s_seedOfRock[go.name] = SeedPrefix + ore;
                }
            }
            var named = Traverse.Create(zs).Field("m_namedPrefabs").GetValue<Dictionary<int, GameObject>>();
            var cultivator = odb.GetItemPrefab("Cultivator")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            var saplingBase = zs.GetPrefab("sapling_carrot") ?? zs.GetPrefab("sapling_turnip");
            int made = 0;
            foreach (var kv in s_rockOfOre)
            {
                string ore = kv.Key, rock = kv.Value;
                var oreItem = odb.GetItemPrefab(ore);
                var rockPrefab = zs.GetPrefab(rock);
                if (oreItem == null || rockPrefab == null || saplingBase == null) continue;
                s_seedRocks.Add(rock);
                string niceOre = Localization.instance != null ? Localization.instance.Localize(oreItem.GetComponent<ItemDrop>().m_itemData.m_shared.m_name) : ore;
                // the seed item: a clone of the ore item with its own shared data
                string seedName = SeedPrefix + ore;
                GameObject seed = zs.GetPrefab(seedName) ?? s_root.transform.Find(seedName)?.gameObject;
                if (seed != null) Register(zs, named, odb, seed, true);   // a world reload: same clone, registered again
                if (seed == null)
                {
                    seed = UnityEngine.Object.Instantiate(oreItem, s_root.transform, false); seed.name = seedName;
                    var idrop = seed.GetComponent<ItemDrop>();
                    var shared = (ItemDrop.ItemData.SharedData)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(idrop.m_itemData.m_shared, null);
                    shared.m_name = niceOre + " seed"; shared.m_description = $"A rare mineral seed. Plant it with the Cultivator and a {niceOre} deposit grows from it; mined out, it regrows.";
                    shared.m_maxStackSize = 50; shared.m_weight = 0.1f; shared.m_teleportable = true;
                    idrop.m_itemData = idrop.m_itemData.Clone(); idrop.m_itemData.m_shared = shared; idrop.m_itemData.m_dropPrefab = seed;
                    Register(zs, named, odb, seed, true);
                }
                // the sapling piece: a vanilla sapling clone that grows into the deposit
                string sapName = SaplingPrefix + ore;
                GameObject sap = zs.GetPrefab(sapName) ?? s_root.transform.Find(sapName)?.gameObject;
                if (sap != null) Register(zs, named, odb, sap, false);
                if (sap == null)
                {
                    sap = UnityEngine.Object.Instantiate(saplingBase, s_root.transform, false); sap.name = sapName;
                    var plant = sap.GetComponent<Plant>();
                    plant.m_name = niceOre + " sprout"; plant.m_grownPrefabs = new[] { rockPrefab };
                    plant.m_growTime = plant.m_growTimeMax = (s_growDays != null ? s_growDays.Value : 3f) * 1800f;
                    plant.m_needCultivatedGround = false; plant.m_destroyIfCantGrow = false; plant.m_minScale = plant.m_maxScale = 1f;
                    plant.m_biome = (Heightmap.Biome)0x7FFF; plant.m_tolerateCold = true; plant.m_tolerateHeat = true; plant.m_growRadius = Mathf.Max(plant.m_growRadius, 1.5f);
                    var piece = sap.GetComponent<Piece>();
                    piece.m_name = niceOre + " seed"; piece.m_description = $"Grows into a {niceOre} deposit in {(s_growDays != null ? s_growDays.Value : 3f):0.#} day(s); mined out, it regrows.";
                    piece.m_icon = seed.GetComponent<ItemDrop>().m_itemData.GetIcon();
                    piece.m_resources = new[] { new Piece.Requirement { m_resItem = seed.GetComponent<ItemDrop>(), m_amount = 1, m_recover = true } };
                    piece.m_cultivatedGroundOnly = false;
                    Register(zs, named, odb, sap, false);
                }
                if (cultivator != null && !cultivator.m_pieces.Contains(sap)) cultivator.m_pieces.Add(sap);
                made++;
            }
            FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{made} seed(s): " + string.Join(", ", s_rockOfOre.Select(kv => $"{kv.Key}<-{kv.Value}")) + $"; {s_seedOfRock.Count} rock prefab(s) can drop one");
        }

        private static void Register(ZNetScene zs, Dictionary<int, GameObject> named, ObjectDB odb, GameObject go, bool item)
        {
            if (!zs.m_prefabs.Contains(go)) zs.m_prefabs.Add(go);
            if (named != null) named[go.name.GetStableHashCode()] = go;
            if (item && !odb.m_items.Contains(go))
            {
                odb.m_items.Add(go);
                var byHash = Traverse.Create(odb).Field("m_itemByHash").GetValue<Dictionary<int, GameObject>>();
                if (byHash != null) byHash[go.name.GetStableHashCode()] = go;
            }
        }

        // ---- drops: the rock's owner asks, the server rolls ------------------------------------------------------------------
        private static void AskRoll(Component rock, Vector3 at)
        {
            if (rock == null || ZRoutedRpc.instance == null) return;
            var view = rock.GetComponent<ZNetView>();
            string prefab = view != null && view.IsValid() ? LocationLootRefresh.PrefabName(view) : Utils.GetPrefabName(rock.gameObject);
            if (!s_seedOfRock.ContainsKey(prefab)) return;
            var pkg = new ZPackage();
            pkg.Write(prefab); pkg.Write(at); pkg.Write(rock.transform.position);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RollRpc, pkg);
        }

        private static void RPC_Roll(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null || !Enabled) return;
            try
            {
                string prefab = pkg.ReadString(); Vector3 at = pkg.ReadVector3(); Vector3 rockPos = pkg.ReadVector3();
                if (!s_seedOfRock.TryGetValue(prefab, out string seedName)) return;
                if (s_nextRoll.TryGetValue(sender, out float next) && Time.time < next) return;   // rate cap per peer
                s_nextRoll[sender] = Time.time + 0.25f;
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 from = peer != null ? peer.m_refPos : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : at + Vector3.one * 1e6f);
                if (Vector3.Distance(from, at) > 64f || Vector3.Distance(at, rockPos) > 12f) return;
                bool seedDeposit = RespawnMarks.IsSeedMark(prefab, rockPos);   // the server's own mark, never the client's word
                float chance = seedDeposit ? s_seedChance.Value : s_chance.Value;
                if (UnityEngine.Random.value >= chance) return;
                var seed = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(seedName) : null;
                if (seed == null) return;
                ItemDrop.OnCreateNew(UnityEngine.Object.Instantiate(seed, at + Vector3.up * 0.3f, Quaternion.identity));
                FiresCore.Logging.FiresLogger.LogInfo($"{Tag}a {seedName.Substring(SeedPrefix.Length)} seed dropped from {prefab} at ({at.x:0},{at.z:0}) ({(seedDeposit ? "seed deposit" : "wild rock")}, chance {chance * 100f:0.###} %)");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "roll: " + e.Message); }
        }

        [HarmonyPatch(typeof(MineRock5), "DamageArea")]
        private static class MineRock5_DamageArea_Seed
        {
            private static void Postfix(MineRock5 __instance, HitData hit, bool __result) { if (__result && hit != null) AskRoll(__instance, hit.m_point); }
        }

        [HarmonyPatch(typeof(MineRock), "RPC_Hit")]
        private static class MineRock_RPC_Hit_Seed
        {
            private static void Prefix(MineRock __instance, int hitAreaIndex, out bool __state)
            {
                __state = false;
                var areas = Traverse.Create(__instance).Field("m_hitAreas").GetValue<Collider[]>();
                if (areas != null && hitAreaIndex >= 0 && hitAreaIndex < areas.Length && areas[hitAreaIndex] != null) __state = areas[hitAreaIndex].gameObject.activeInHierarchy;
            }
            private static void Postfix(MineRock __instance, HitData hit, int hitAreaIndex, bool __state)
            {
                if (!__state || hit == null) return;
                var areas = Traverse.Create(__instance).Field("m_hitAreas").GetValue<Collider[]>();
                bool broke = areas == null || hitAreaIndex >= areas.Length || areas[hitAreaIndex] == null || !areas[hitAreaIndex].gameObject.activeInHierarchy;
                if (broke) AskRoll(__instance, hit.m_point);
            }
        }

        // ---- a seed grows: the server checks the spot and writes the regrow mark ----------------------------------------------
        [HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
        private static class Plant_Grow_Seed
        {
            private static void Postfix(Plant __instance, GameObject __result)
            {
                try
                {
                    if (__result == null || __instance == null || !Utils.GetPrefabName(__instance.gameObject).StartsWith(SaplingPrefix)) return;
                    var view = __result.GetComponent<ZNetView>();
                    if (view == null || !view.IsValid() || ZRoutedRpc.instance == null) return;
                    var pkg = new ZPackage();
                    pkg.Write(view.GetZDO().m_uid); pkg.Write(Utils.GetPrefabName(__result));
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), GrownRpc, pkg);
                }
                catch (Exception e) { Debug.LogWarning(Tag + "grow: " + e.Message); }
            }
        }

        private static void RPC_Grown(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                ZDOID uid = pkg.ReadZDOID(); string prefab = pkg.ReadString();
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo == null || !s_seedRocks.Contains(prefab) || ZNetScene.instance.GetPrefab(zdo.GetPrefab())?.name != prefab) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}grow from {sender} refused: not a seed deposit"); return; }
                Vector3 pos = zdo.GetPosition();
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 from = peer != null ? peer.m_refPos : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : pos + Vector3.one * 1e6f);
                if (Vector3.Distance(from, pos) > 128f) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}grow from {sender} refused: {prefab} is {Vector3.Distance(from, pos):0} m away"); return; }
                long playerId = peer != null ? peer.m_characterID.UserID : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L);
                // the planter's own ward or none; the cap counts deposits in that ward (or 32 m without one)
                PrivateArea ward = PrivateArea.m_allAreas.FirstOrDefault(a => a != null && a.IsEnabled() && a.IsInside(pos, 0f));
                if (ward != null && playerId != 0L && !ward.IsPermitted(playerId) && ward.m_piece != null && ward.m_piece.GetCreator() != playerId) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} at ({pos.x:0},{pos.z:0}): not regrowing (someone else's ward)"); return; }
                float radius = ward != null ? ward.m_radius : 32f;
                Vector3 centre = ward != null ? ward.transform.position : pos;
                int have = RespawnMarks.CountSeedMarksNear(centre, radius);
                int cap = s_perWard != null ? s_perWard.Value : 20;
                if (have >= cap) { FiresCore.Logging.FiresLogger.LogInfo($"{Tag}{prefab} at ({pos.x:0},{pos.z:0}): grew but won't regrow ({have}/{cap} seed deposits in this {(ward != null ? "ward" : "32 m")})"); return; }
                int minutes = Mathf.Max(1, Mathf.RoundToInt((s_regrowDays != null ? s_regrowDays.Value : 3f) * DaySeconds / 60f));
                string by = peer != null ? peer.m_playerName : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : sender.ToString());
                if (!RespawnMarks.AddSeedMark(prefab, pos, minutes, by)) return;
                zdo.Set(RespawnMarks.MinKey, minutes); zdo.Set(RespawnMarks.ByKey, by); zdo.Set(RespawnMarks.SeedKey, 1);
                ZDOMan.instance.ForceSendZDO(uid);
                FiresCore.Logging.FiresLogger.LogInfo($"{Tag}{prefab} grew at ({pos.x:0},{pos.z:0}) for {by}: a seed deposit, regrows {minutes / (DaySeconds / 60f):0.#} day(s) after it's mined out ({have + 1}/{cap})");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "grown: " + e.Message); }
        }

        // ---- admin test command: mineralseed give <ore> [count] ------------------------------------------------------------------
        // ([lead] 10-24: a quick check without editing a drop chance mid-test.) The server checks the sender is an admin (787 rule) and
        // drops the seeds at the sender; the client only asks.
        private const string GiveRpc = "FiresCore_MineralSeedGive";

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class MineralSeeds_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("mineralseed", "[FiresUnifiedCore] mineralseed give <ore> [count] - (admin) drops mineral seeds at you, e.g. mineralseed give Copper 2. 'mineralseed list' shows the ores.",
                    args =>
                    {
                        if (args.Args.Length > 1 && args.Args[1].ToLowerInvariant() == "list") { args.Context?.AddString("mineral seeds: " + string.Join(", ", s_rockOfOre.Select(kv => $"{kv.Key} (from {kv.Value})"))); return; }
                        if (args.Args.Length < 3 || args.Args[1].ToLowerInvariant() != "give") { args.Context?.AddString("usage: mineralseed give <ore> [count]  |  mineralseed list"); return; }
                        int count = args.Args.Length > 3 && int.TryParse(args.Args[3], out int n) ? Mathf.Clamp(n, 1, 20) : 1;
                        var pkg = new ZPackage(); pkg.Write(args.Args[2]); pkg.Write(count);
                        ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), GiveRpc, pkg);
                        args.Context?.AddString($"mineralseed: asked the server for {count} {args.Args[2]} seed(s) (admins only)");
                    });
            }
        }

        private static void RPC_Give(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                string want = pkg.ReadString(); int count = Mathf.Clamp(pkg.ReadInt(), 1, 20);
                if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}give from {sender} refused: not an admin"); return; }
                string ore = s_rockOfOre.Keys.FirstOrDefault(o => o.Equals(want, StringComparison.OrdinalIgnoreCase) || o.StartsWith(want, StringComparison.OrdinalIgnoreCase));
                var seed = ore != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(SeedPrefix + ore) : null;
                if (seed == null) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}give from {sender}: no mineral seed for '{want}'"); return; }
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 at = peer != null ? peer.m_refPos : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
                for (int i = 0; i < count; i++) ItemDrop.OnCreateNew(UnityEngine.Object.Instantiate(seed, at + Vector3.up * 1f + UnityEngine.Random.insideUnitSphere * 0.4f, Quaternion.identity));
                FiresCore.Logging.FiresLogger.LogInfo($"{Tag}admin {(peer != null ? peer.m_playerName : sender.ToString())} took {count} {ore} seed(s) (mineralseed give)");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "give: " + e.Message); }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_MineralSeeds
        {
            private static void Postfix()
            {
                s_nextRoll.Clear();
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>(RollRpc, RPC_Roll);
                ZRoutedRpc.instance.Register<ZPackage>(GrownRpc, RPC_Grown);
                ZRoutedRpc.instance.Register<ZPackage>(GiveRpc, RPC_Give);
            }
        }
    }
}
