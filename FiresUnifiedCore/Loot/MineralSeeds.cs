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
        private const string RollRpc = "FiresCore_MineralSeedRoll", GrownRpc = "FiresCore_MineralGrown", ReplyRpc = "FiresCore_MineralSeedReply";   // 0.2.893: + the full-rock why-line back to the asking peer
        private const string SeedPrefix = "MineralSeed_", SaplingPrefix = "MineralSapling_";
        private static readonly string[] Ores = { "CopperOre", "TinOre", "IronScrap", "SilverOre", "Obsidian", "FlametalOreNew", "FlametalOre", "BlackMetalScrap" };
        // 1086 ([generator], Fire 11-01: configs live in their own mods): [MineralSeeds] are FiresLootOverhaul's, read on use
        private static readonly FiresCore.Config.LiveSetting<bool> s_enabled = new FiresCore.Config.LiveSetting<bool>("MineralSeeds", "Enabled", false);
        private static readonly FiresCore.Config.LiveSetting<float> s_chance = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Drop chance", 0.002f), s_seedChance = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Seed deposit drop chance", 0.0002f), s_growDays = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Grow days", 3f), s_regrowDays = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Regrow days", 3f);
        private static readonly FiresCore.Config.LiveSetting<int> s_perWard = new FiresCore.Config.LiveSetting<int>("MineralSeeds", "Max per ward", 20);

        // ore item -> the rock that drops it, rock -> its seed item, seed -> sapling piece
        private static readonly Dictionary<string, string> s_rockOfOre = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> s_seedOfRock = new Dictionary<string, string>();
        private static readonly HashSet<string> s_seedRocks = new HashSet<string>();
        private static readonly Dictionary<long, float> s_nextRoll = new Dictionary<long, float>();
        private static readonly HashSet<string> s_rolledRocks = new HashSet<string>();
        private static readonly HashSet<string> s_once = new HashSet<string>();
        private static void Once(string key, string line) { if (s_once.Add(key)) FiresCore.Logging.FiresLogger.LogInfo(line); }
        private static GameObject s_root;
        private const string Tag = "[MineralSeeds] ";

        internal static void BindConfig(ConfigFile cfg)
        {
            // 0.2.889 (Fire 10-27: "as mining skill goes up, mineral seed rarity should go down ... at least one seed for every full minerock broken")
        }

        private static readonly Dictionary<int, (Character who, float at)> s_lastMiner = new Dictionary<int, (Character, float)>();   // 0.2.896
        private static readonly FiresCore.Config.LiveSetting<bool> s_guarantee = new FiresCore.Config.LiveSetting<bool>("MineralSeeds", "Skill guarantee", true);
        private static readonly FiresCore.Config.LiveSetting<float> s_guaranteeStart = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Skill guarantee starts at", 20f), s_guaranteeFull = new FiresCore.Config.LiveSetting<float>("MineralSeeds", "Skill guarantee full at", 80f);
        private static readonly HashSet<string> s_fullRolled = new HashSet<string>();

        // p(skill) = clamp01((skill - start) / (full - start)); full is kept above start.
        private static float GuaranteeChance(float skill)
        {
            if (s_guarantee == null || !s_guarantee.Value) return 0f;
            float start = s_guaranteeStart.Value, full = Mathf.Max(start + 1f, s_guaranteeFull.Value);
            return Mathf.Clamp01((skill - start) / (full - start));
        }

        // The miner's Pickaxes level where the hit happens: a player's own skills, a companion's CompanionSkills; anything else 0.
        private static float MinerSkill(Character attacker)
        {
            try
            {
                if (attacker is Player p && p.GetSkills() != null) return p.GetSkills().GetSkillLevel(Skills.SkillType.Pickaxes);
                var cs = attacker != null ? attacker.GetComponent<FiresCore.Npc.CompanionSkills>() : null;
                if (cs != null) return cs.GetSkillLevel(Skills.SkillType.Pickaxes);
            }
            catch { }
            return 0f;
        }

        // Server: a companion's Pickaxes level from its own ZDO ("companion_skills" = "Type:level:acc;..."), the only level the server can see.
        private static bool ServerCompanionSkill(ZDOID id, out float level)
        {
            level = 0f;
            if (id.IsNone() || ZDOMan.instance == null) return false;
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) return false;
            string data = zdo.GetString("companion_skills", "");
            foreach (var pair in data.Split(';'))
            {
                var parts = pair.Split(':');
                if (parts.Length < 2 || parts[0] != nameof(Skills.SkillType.Pickaxes)) continue;
                if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out level)
                    || float.TryParse(parts[1], out level)) return true;
            }
            return true;   // a companion ZDO with no Pickaxes entry is level 0
        }

        private static bool Enabled => s_enabled != null && s_enabled.Value;
        internal static bool EnabledForTest => Enabled;   // 0.2.892: mineral_seed_skill_test reads it (the server's value on a host)
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

        // 0.2.864 (Fire 10-27 "we never get any gemstones or mineral seeds"; his log had no [MineralSeeds] line at all): ZNetScene.Awake
        // runs BEFORE ObjectDB.Awake, so the build met a null ObjectDB and returned silently, the seed table stayed empty and every roll
        // request stopped at "not a seed rock". It now also runs from ObjectDB.Awake (whichever comes second has both) and says so.
        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDB_Awake_MineralSeeds
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix()
            {
                try { if (ZNetScene.instance != null) Build(ZNetScene.instance); } catch (Exception e) { Debug.LogWarning(Tag + "build: " + e); }
            }
        }

        private const string LootOverhaulGuid = "com.Fire.FiresLootOverhaul";

        private static void Build(ZNetScene zs)
        {
            var odb = ObjectDB.instance;
            if (zs == null || odb == null) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}build deferred: {(zs == null ? "ZNetScene" : "ObjectDB")} not ready yet (retried from the other Awake)"); return; }
            // 0.2.1048 (Fire 10-30: "Core should not actually unlock or make available any of its pieces unless the mods that use them are
            // installed. So mineral seeds should only be unlockable or usable if you have FLO installed"): no FiresLootOverhaul = no seed items,
            // no seed saplings on the Cultivator, no drops (the roll finds no seed rock)
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LootOverhaulGuid))
            {
                Once("noflo", $"{Tag}not registered: FiresLootOverhaul is not installed (mineral seeds are its content)");
                return;
            }
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
            // (Fire 11-02: "they take up less room in caves and around the homestead") copper and iron seeds grow vanilla's small vein, not the big deposit.
            foreach (var small in new[] { new KeyValuePair<string, string>("CopperOre", "MineRock_Copper"), new KeyValuePair<string, string>("IronScrap", "MineRock_Iron") })
            {
                if (zs.GetPrefab(small.Value) == null) continue;
                s_rockOfOre[small.Key] = small.Value;
                s_seedOfRock[small.Value] = SeedPrefix + small.Key;
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
        private static void AskRoll(Component rock, Vector3 at, HitData hit, bool fullRock = false)
        {
            if (rock == null || ZRoutedRpc.instance == null) return;
            var view = rock.GetComponent<ZNetView>();
            string prefab = view != null && view.IsValid() ? LocationLootRefresh.PrefabName(view) : Utils.GetPrefabName(rock.gameObject);
            if (!s_seedOfRock.ContainsKey(prefab)) { if (s_seedOfRock.Count == 0) Once("empty", $"{Tag}{prefab} broke but the seed table is empty (build never ran: see the build line)"); return; }
            Character attacker = hit != null ? hit.GetAttacker() : null;
            // 0.2.896 (round #20: the second rock's full-rock ask said "broken by ? (mining 0" at Pickaxes 100): vanilla MineRock5 breaks
            // its last unsupported areas itself with a HitData that has no attacker, so the hit that FINISHES a rock often has none. The
            // last real attacker on this rock (within 30 s) is remembered and used for those.
            int rid = rock.GetInstanceID();
            if (attacker != null) { if (s_lastMiner.Count > 256) s_lastMiner.Clear(); s_lastMiner[rid] = (attacker, Time.time); }
            else if (s_lastMiner.TryGetValue(rid, out var lm) && lm.who != null && Time.time - lm.at < 30f) attacker = lm.who;
            string who = attacker == null ? "?" : attacker is Player pl ? pl.GetPlayerName() : (attacker.GetHoverName() + " (" + Utils.GetPrefabName(attacker.gameObject) + ")");
            var pkg = new ZPackage();
            pkg.Write(prefab); pkg.Write(at); pkg.Write(rock.transform.position); pkg.Write(who);
            // 0.2.889 add-only: the miner's Pickaxes level, whether this hit finished the whole rock, and a companion's ZDOID (the server
            // reads a companion's level from its ZDO and ignores the sent one); an older package without them is skill 0 / not full.
            var cnv = attacker != null && !(attacker is Player) ? attacker.GetComponent<ZNetView>() : null;
            pkg.Write(MinerSkill(attacker)); pkg.Write(fullRock); pkg.Write(cnv != null && cnv.IsValid() ? cnv.GetZDO().m_uid : ZDOID.None);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RollRpc, pkg);
        }

        private static void RPC_Roll(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null || !Enabled) return;
            try
            {
                string prefab = pkg.ReadString(); Vector3 at = pkg.ReadVector3(); Vector3 rockPos = pkg.ReadVector3();
                string who = pkg.GetPos() < pkg.Size() ? pkg.ReadString() : "?";
                float skill = 0f; bool full = false; ZDOID companion = ZDOID.None;
                if (pkg.GetPos() < pkg.Size()) { skill = pkg.ReadSingle(); full = pkg.ReadBool(); companion = pkg.ReadZDOID(); }
                if (!s_seedOfRock.TryGetValue(prefab, out string seedName)) return;
                // the full-rock ask is never eaten by the per-area rate cap (it is deduped per rock below instead)
                if (!full && s_nextRoll.TryGetValue(sender, out float next) && Time.time < next) return;   // rate cap per peer
                if (!full) s_nextRoll[sender] = Time.time + 0.25f;
                // 0.2.864: a rock nobody is near is owned by the server, which then asks itself; there is no peer, so its own ask is trusted
                // (companions mining away from the player). A client still has to stand within 64 m of the hit.
                bool self = sender == ZNet.GetUID() || (ZRoutedRpc.instance != null && sender == ZRoutedRpc.instance.GetServerPeerID() && ZNet.GetUID() == sender);
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (!self)
                {
                    Vector3 from = peer != null ? peer.m_refPos : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : at + Vector3.one * 1e6f);
                    if (Vector3.Distance(from, at) > 64f) { Once("far:" + sender, $"{Tag}roll from {sender} refused: the hit is {Vector3.Distance(from, at):0} m from that peer"); return; }
                }
                if (Vector3.Distance(at, rockPos) > 12f) return;
                bool seedDeposit = RespawnMarks.IsSeedMark(prefab, rockPos);   // the server's own mark, never the client's word
                float chance = seedDeposit ? s_seedChance.Value : s_chance.Value;
                float rolled = UnityEngine.Random.value;
                bool drop = rolled < chance;
                string rockKey = prefab + "@" + Mathf.RoundToInt(rockPos.x) + "," + Mathf.RoundToInt(rockPos.z);
                // 0.2.889: the full-rock guarantee, once per rock (server dedupe); a companion's level only from its ZDO, a player's clamped.
                if (full && s_fullRolled.Add(rockKey + "@" + (long)(ZNet.instance.GetTimeSeconds() / 60.0)))
                {
                    if (s_fullRolled.Count > 4096) s_fullRolled.Clear();
                    string src = "sent";
                    if (!companion.IsNone()) { if (ServerCompanionSkill(companion, out float zl)) { skill = zl; src = "companion ZDO"; } else { skill = 0f; src = "companion ZDO missing"; } }
                    skill = Mathf.Clamp(skill, 0f, 100f);
                    float p = GuaranteeChance(skill);
                    float g = UnityEngine.Random.value;
                    bool gDrop = p > 0f && g < p;
                    string why = $"{Tag}full rock {prefab} at ({rockPos.x:0},{rockPos.z:0}) broken by {who} (mining {skill:0}, {src}): guarantee p {p:0.##} -> {(gDrop ? "drop" : "none")} ({(seedDeposit ? "seed deposit" : "wild rock")})";
                    FiresCore.Logging.FiresLogger.LogDebug(why);
                    // 0.2.893 (lead: the rig always runs a dedi, so the client test must see the result): the why-line goes back to the asking peer
                    if (!self && ZRoutedRpc.instance != null) { var rp = new ZPackage(); rp.Write(why); ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyRpc, rp); }
                    if (gDrop)
                    {
                        var gseed = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(seedName) : null;
                        if (gseed != null) ItemDrop.OnCreateNew(UnityEngine.Object.Instantiate(gseed, at + Vector3.up * 0.3f, Quaternion.identity));
                    }
                }
                if (drop || s_rolledRocks.Add(rockKey))
                    FiresCore.Logging.FiresLogger.LogDebug($"{Tag}roll {prefab} at ({rockPos.x:0},{rockPos.z:0}) by {who}: chance {chance:0.####}, rolled {rolled:0.####} -> {(drop ? "drop" : "none")} (first roll on this rock{(drop ? " or a drop" : "")}; {(seedDeposit ? "seed deposit" : "wild rock")})");
                if (!drop) return;
                var seed = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(seedName) : null;
                if (seed == null) return;
                ItemDrop.OnCreateNew(UnityEngine.Object.Instantiate(seed, at + Vector3.up * 0.3f, Quaternion.identity));
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}a {seedName.Substring(SeedPrefix.Length)} seed dropped from {prefab} at ({at.x:0},{at.z:0}) ({(seedDeposit ? "seed deposit" : "wild rock")}, chance {chance * 100f:0.###} %)");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "roll: " + e.Message); }
        }

        [HarmonyPatch(typeof(MineRock5), "DamageArea")]
        private static class MineRock5_DamageArea_Seed
        {
            private static void Postfix(MineRock5 __instance, HitData hit, bool __result) { if (__result && hit != null) AskRoll(__instance, hit.m_point, hit, AllAreasBroken5(__instance)); }
        }

        // 0.2.889: a MineRock5 is fully broken when every hit area's health is at or below 0 (read after DamageArea broke one).
        private static System.Reflection.FieldInfo s_areaHealth;
        private static bool AllAreasBroken5(MineRock5 rock)
        {
            try
            {
                var list = Traverse.Create(rock).Field("m_hitAreas").GetValue() as System.Collections.IList;
                if (list == null || list.Count == 0) return false;
                foreach (var a in list)
                {
                    if (a == null) continue;
                    if (s_areaHealth == null) s_areaHealth = AccessTools.Field(a.GetType(), "m_health");
                    if (s_areaHealth == null) return false;
                    if ((float)s_areaHealth.GetValue(a) > 0f) return false;
                }
                return true;
            }
            catch { return false; }
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
                // 0.2.889: the last area to go inactive finishes the whole rock
                bool full = broke && areas != null && areas.All(c => c == null || !c.gameObject.activeInHierarchy);
                if (broke) AskRoll(__instance, hit.m_point, hit, full);
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
                if (have >= cap) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} at ({pos.x:0},{pos.z:0}): grew but won't regrow ({have}/{cap} seed deposits in this {(ward != null ? "ward" : "32 m")})"); return; }
                int minutes = Mathf.Max(1, Mathf.RoundToInt((s_regrowDays != null ? s_regrowDays.Value : 3f) * DaySeconds / 60f));
                string by = peer != null ? peer.m_playerName : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : sender.ToString());
                if (!RespawnMarks.AddSeedMark(prefab, pos, minutes, by)) return;
                zdo.Set(RespawnMarks.MinKey, minutes); zdo.Set(RespawnMarks.ByKey, by); zdo.Set(RespawnMarks.SeedKey, 1);
                ZDOMan.instance.ForceSendZDO(uid);
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} grew at ({pos.x:0},{pos.z:0}) for {by}: a seed deposit, regrows {minutes / (DaySeconds / 60f):0.#} day(s) after it's mined out ({have + 1}/{cap})");
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
                    }, isCheat: true /* 0.2.1159 Fire rule */);
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

        // 0.2.893: a client logs the server's full-rock why-line it asked for, marked "(server)"; only the server's reply counts, capped in size.
        private static void RPC_Reply(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID() || pkg == null) return;
                string line = pkg.ReadString();
                if (string.IsNullOrEmpty(line) || line.Length > 400 || !line.StartsWith(Tag + "full rock ")) return;
                FiresCore.Logging.FiresLogger.LogDebug("(server) " + line);
            }
            catch { }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_MineralSeeds
        {
            private static void Postfix()
            {
                s_nextRoll.Clear(); s_rolledRocks.Clear();
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>(RollRpc, RPC_Roll);
                ZRoutedRpc.instance.Register<ZPackage>(GrownRpc, RPC_Grown);
                ZRoutedRpc.instance.Register<ZPackage>(GiveRpc, RPC_Give);
                ZRoutedRpc.instance.Register<ZPackage>(ReplyRpc, RPC_Reply);
            }
        }
    }
}
