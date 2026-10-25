using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Loot
{
    /// <summary>
    /// Locations, dungeons and caves refresh their loot after a set number of in-game days. Off by default.
    /// Only what the WORLD placed there counts: a Container or a one-shot Pickable whose ZDO has no creator, inside a Location or
    /// dungeon (Location.GetLocation(pos, true)); never player-built chests, tombstones, carts, ships, player-planted crops, or
    /// pickables vanilla already respawns (m_respawnTimeMinutes > 0).
    ///   Partly looted chest: stamped on the day an item first leaves it; after N days with no player within the set range the owner
    ///     empties it and runs vanilla's own AddDefaultItems (a fresh roll).
    ///   Fully emptied chest: registered with the server, then vanilla's m_autoDestroyEmpty path removes it (as FAP's preset chests
    ///     do); the server respawns the prefab at the same spot after N days once its zone is loaded and no player is near. A fresh
    ///     Container runs vanilla's AddDefaultItems itself.
    ///   One-shot pickable that hides when picked: stamped when picked; after N days the owner unpicks it through vanilla's
    ///     RPC_SetPicked(false). One that vanilla destroys when picked: registered and respawned by the server like a chest.
    /// The server's registry is one file per world under BepInEx\config\FiresUnifiedCore\LootRefresh.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Loot)]
    public static class LocationLootRefresh
    {
        private const string Tag = "[LootRefresh] ";
        private const string RegisterRpc = "FiresCore_LootRefresh_Register";
        private const float DuplicateRadius = 1f, ServerTickSeconds = 30f, OwnerTickSeconds = 10f, StandTickSeconds = 0.5f;

        private static readonly int FullKey = "fires_loot_full".GetStableHashCode();
        private static readonly int LootedDayKey = "fires_loot_looted_day".GetStableHashCode();
        private static readonly int RegisteredKey = "fires_loot_registered".GetStableHashCode();
        internal static int RegisteredKeyPublic => RegisteredKey;
        private static readonly int PickedDayKey = "fires_loot_picked_day".GetStableHashCode();

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<int> s_days;
        private static ConfigEntry<float> s_range;

        public static bool Enabled => s_enabled != null && s_enabled.Value;
        public static int Days => s_days != null ? Mathf.Max(1, s_days.Value) : 7;
        public static float Range => s_range != null ? Mathf.Max(0f, s_range.Value) : 40f;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_enabled = config.Bind("Loot", "Location loot refresh", false,
                "SERVER. Chests and one-shot pickables that the world placed in locations, dungeons and caves come back after 'Refresh after days': "
                + "a partly looted chest is refilled with a fresh roll, an emptied one removes itself and respawns, a picked item reappears, a broken mud pile or location spawner (crypt draugr piles and the like) is rebuilt. "
                + "Never player-built chests, tombstones, carts, ships or planted crops.");
            s_days = config.Bind("Loot", "Refresh after days", 7, "SERVER. In-game days before looted location loot comes back.");
            s_range = config.Bind("Loot", "No refresh within metres", 40f, "SERVER. Nothing is refilled or respawned while a player is this close.");
            configSync?.AddConfigEntry(s_enabled);
            configSync?.AddConfigEntry(s_days);
            configSync?.AddConfigEntry(s_range);
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Loot, true, s_enabled, s_days, s_range);   // FAP or FAT
        }

        // ── time and players ───────────────────────────────────────────────────────────────────────────────────────────────────

        public static int Today
        {
            get
            {
                if (ZNet.instance == null) return 0;
                long dayLength = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1200L;
                return (int)(ZNet.instance.GetTimeSeconds() / dayLength);
            }
        }

        internal static bool PlayerNear(Vector3 p) => PlayerNear(p, Range);

        // A per-object range (a FAP stand preset's own distance); 0 or less = no player check at all.
        internal static bool PlayerNear(Vector3 p, float range)
        {
            if (range <= 0f) return false;
            float r2 = range * range;
            if (ZNet.instance != null && ZNet.instance.IsServer())
                foreach (ZNetPeer peer in ZNet.instance.GetPeers())
                    if (peer != null && (peer.m_refPos - p).sqrMagnitude <= r2) return true;
            return Player.IsPlayerInRange(p, range);
        }

        // ── what counts ────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static HashSet<string> s_grown;

        // Every prefab a vanilla Plant grows into: a crop grown from a player's planting is never location loot.
        private static bool IsGrownCrop(string prefab)
        {
            if (s_grown == null && ZNetScene.instance != null)
            {
                s_grown = new HashSet<string>(StringComparer.Ordinal);
                foreach (GameObject go in ZNetScene.instance.m_prefabs)
                {
                    var plant = go != null ? go.GetComponent<Plant>() : null;
                    if (plant == null || plant.m_grownPrefabs == null) continue;
                    foreach (GameObject grown in plant.m_grownPrefabs) if (grown != null) s_grown.Add(grown.name);
                }
            }
            return s_grown != null && s_grown.Contains(prefab);
        }

        private static string LocationOf(Vector3 p)
        {
            Location loc = Location.GetLocation(p, true);
            return loc != null ? loc.name.Replace("(Clone)", "") : null;
        }

        private static bool WorldPlaced(ZNetView view) => view != null && view.IsValid() && view.GetZDO().GetLong(ZDOVars.s_creator, 0L) == 0L;

        internal static string PrefabName(ZNetView view) =>
            ZNetScene.instance?.GetPrefab(view.GetZDO().GetPrefab())?.name ?? view.gameObject.name.Replace("(Clone)", "");

        private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Pickable, ZNetView> PickableView = AccessTools.FieldRefAccess<Pickable, ZNetView>("m_nview");
        private static readonly System.Reflection.MethodInfo AddDefaultItems = AccessTools.Method(typeof(Container), "AddDefaultItems");

        // A FAP preset chest handed to EWP carries "va_chest" = 1 (FAP 1.1.214; 0, and 2 from older FAP, are the Core route). EWP owns its respawn, so Core never
        // counts it as location loot and never tracks it: no double handling.
        private static readonly int FapEwpChestKey = "va_chest".GetStableHashCode();
        internal static bool OwnedByEwp(ZDO zdo) => zdo != null && zdo.GetInt(FapEwpChestKey, 0) == 1;   // 1 = EWP; 0 and FAP<1.1.214's 2 = Core route

        private static bool Counts(Container c, ZNetView view, out string location)
        {
            location = null;
            bool stamped = FiresCore.World.LocationReset.Stamped(view?.GetZDO(), out string resetKind);   // An FDM room's child
            if (!stamped && !WorldPlaced(view)) return false;
            if (OwnedByEwp(view.GetZDO())) return false;
            if (c.m_defaultItems == null || c.m_defaultItems.m_drops == null || c.m_defaultItems.m_drops.Count == 0) return false;
            if (c.m_autoDestroyEmpty && !view.GetZDO().GetBool(RegisteredKey)) return false;   // vanilla's own crates (cargo) stay vanilla's
            if (c.m_wagon != null || c.GetComponentInParent<Ship>() != null || c.GetComponent<TombStone>() != null) return false;
            location = stamped ? resetKind : LocationOf(c.transform.position);
            return location != null;
        }

        private static bool Counts(Pickable p, ZNetView view, out string location)
        {
            location = null;
            bool stamped = FiresCore.World.LocationReset.Stamped(view?.GetZDO(), out string resetKind);   // An FDM room's child
            if (p.m_respawnTimeMinutes > 0f || (!stamped && !WorldPlaced(view))) return false;
            if (IsGrownCrop(PrefabName(view))) return false;
            location = stamped ? resetKind : LocationOf(p.transform.position);
            return location != null;
        }

        // ── containers (the owner) ─────────────────────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Container), "CheckForChanges")]
        private static class Container_CheckForChanges_LootRefresh
        {
            private static void Postfix(Container __instance)
            {
                if (__instance == null) return;
                try
                {
                    ZNetView view = ContainerView(__instance);
                    if (view == null || !view.IsValid() || !view.IsOwner()) return;
                    // A chest another mod tracked (LootRespawn.Track: FAP's preset chests) respawns whatever the toggle says.
                    if (view.GetZDO().GetInt(LootRespawn.TrackKey, 0) == 1) { LootRespawn.TrackedContainerTick(__instance, view); return; }
                    if (!Enabled) return;
                    if (!Counts(__instance, view, out string location)) return;
                    ZDO zdo = view.GetZDO();
                    Inventory inv = __instance.GetInventory();
                    if (inv == null) return;
                    int n = inv.NrOfItems(), full = zdo.GetInt(FullKey, 0), looted = zdo.GetInt(LootedDayKey, -1);
                    Vector3 p = __instance.transform.position;
                    string prefab = PrefabName(view);

                    if (full == 0 && n > 0) { zdo.Set(FullKey, n); full = n; }
                    if (looted < 0 && n < full)
                    {
                        looted = Today;
                        zdo.Set(LootedDayKey, looted);
                        FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} at ({p.x:0},{p.z:0}) in {location} looted on day {looted} ({n}/{full} left)");
                    }

                    // Registered earlier but reloaded before vanilla removed it (the prefab's own m_autoDestroyEmpty is false): remove it now.
                    if (n == 0 && zdo.GetBool(RegisteredKey)) { __instance.m_autoDestroyEmpty = true; return; }
                    if (n == 0 && !__instance.IsInUse() && !zdo.GetBool(RegisteredKey))
                    {
                        zdo.Set(RegisteredKey, true);
                        SendRegister(prefab, p, __instance.transform.rotation, looted >= 0 ? looted : Today, "chest", location, zdo);
                        __instance.m_autoDestroyEmpty = true;   // vanilla's CheckForChanges removes it on its next poll (FAP's path)
                        return;
                    }

                    if (looted >= 0 && n > 0 && Today - looted >= Days && !__instance.IsInUse() && !PlayerNear(p))
                    {
                        inv.RemoveAll();
                        AddDefaultItems?.Invoke(__instance, null);
                        zdo.Set(ZDOVars.s_addedDefaultItems, true);
                        zdo.Set(FullKey, inv.NrOfItems());
                        zdo.Set(LootedDayKey, -1);
                        FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} at ({p.x:0},{p.z:0}) in {location} refilled after {Today - looted} days ({inv.NrOfItems()} items)");
                    }
                }
                catch (Exception ex) { Debug.LogWarning(Tag + ex.Message); }
            }
        }

        // ── pickables (the owner) ──────────────────────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<Pickable> s_picked = new HashSet<Pickable>();

        [HarmonyPatch(typeof(Pickable), "SetPicked")]
        private static class Pickable_SetPicked_LootRefresh
        {
            private static void Prefix(Pickable __instance, bool picked)
            {
                if (!Enabled || !picked || __instance == null) return;
                try
                {
                    ZNetView view = PickableView(__instance);
                    if (view == null || !view.IsValid() || !view.IsOwner()) return;
                    if (!Counts(__instance, view, out string location)) return;
                    Vector3 p = __instance.transform.position;
                    string prefab = PrefabName(view);
                    if (__instance.m_hideWhenPicked != null)
                    {
                        view.GetZDO().Set(PickedDayKey, Today);
                        s_picked.Add(__instance);
                        FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} at ({p.x:0},{p.z:0}) in {location} picked on day {Today}");
                    }
                    else SendRegister(prefab, p, __instance.transform.rotation, Today, "pickable", location, view.GetZDO());   // vanilla destroys it next
                }
                catch (Exception ex) { Debug.LogWarning(Tag + ex.Message); }
            }
        }

        // ── broken location resources and spawners (the owner) ─────────────────────────────────────────────────────────────
        // Crypt mud piles (mudpile, mudpile2, mudpile_beacon, mudpile_old; Destructible) and the spawners
        // players break in locations and dungeons (a Destructible carrying a SpawnArea or CreatureSpawner, e.g. Spawner_DraugrPile)
        // come back like location chests and pickables: the original prefab at its original spot and rotation (interiors at
        // y > 5000 too), after 'Refresh after days', never with a player inside 'No refresh within metres'. Vegetation nests
        // outside any location stay vanilla's (LocationOf finds no location for them).
        private static readonly AccessTools.FieldRef<Destructible, ZNetView> DestructibleView = AccessTools.FieldRefAccess<Destructible, ZNetView>("m_nview");

        internal static bool CountsDestructible(Destructible d, ZNetView view, out string location)
        {
            location = null;
            bool stamped = FiresCore.World.LocationReset.Stamped(view?.GetZDO(), out string resetKind);   // An FDM room's child
            if (d == null || (!stamped && !WorldPlaced(view))) return false;
            string prefab = PrefabName(view);
            bool mudPile = prefab.StartsWith("mudpile", StringComparison.OrdinalIgnoreCase) && !prefab.EndsWith("_frac", StringComparison.OrdinalIgnoreCase);
            bool spawner = d.GetComponent<SpawnArea>() != null || d.GetComponent<CreatureSpawner>() != null;
            if (!mudPile && !spawner) return false;
            location = stamped ? resetKind : LocationOf(d.transform.position);
            return location != null;
        }

        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Destroy))]
        private static class Destructible_Destroy_LootRefresh
        {
            private static void Prefix(Destructible __instance)
            {
                if (!Enabled || __instance == null) return;
                try
                {
                    ZNetView view = DestructibleView(__instance);
                    if (view == null || !view.IsValid() || !view.IsOwner()) return;
                    if (!CountsDestructible(__instance, view, out string location)) return;
                    Vector3 p = __instance.transform.position;
                    string prefab = PrefabName(view);
                    FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{prefab} broken at ({p.x:0},{p.y:0},{p.z:0}) in {location} on day {Today} (zdo {view.GetZDO().m_uid})");
                    SendRegister(prefab, p, __instance.transform.rotation, Today, "destructible", location, view.GetZDO());
                }
                catch (Exception ex) { Debug.LogWarning(Tag + ex.Message); }
            }
        }

        // Picked (hide-type) location pickables met when their zone loads: tracked for the owner tick.
        [HarmonyPatch(typeof(Pickable), "Awake")]
        private static class Pickable_Awake_LootRefresh
        {
            private static void Postfix(Pickable __instance)
            {
                if (!Enabled || __instance == null || __instance.m_hideWhenPicked == null || !__instance.GetPicked()) return;
                ZNetView view = PickableView(__instance);
                if (view != null && view.IsValid() && Counts(__instance, view, out _)) s_picked.Add(__instance);
            }
        }

        private static void OwnerTick()
        {
            if (s_picked.Count == 0) return;
            foreach (Pickable p in s_picked.ToList())
            {
                ZNetView view = p != null ? PickableView(p) : null;
                if (view == null || !view.IsValid() || !p.GetPicked()) { s_picked.Remove(p); continue; }
                if (!view.IsOwner()) continue;
                ZDO zdo = view.GetZDO();
                int day = zdo.GetInt(PickedDayKey, -1);
                if (day < 0) { zdo.Set(PickedDayKey, Today); continue; }   // picked before the feature was on: counts from today
                if (Today - day < Days || PlayerNear(p.transform.position)) continue;
                zdo.Set(PickedDayKey, -1);
                view.InvokeRPC(ZNetView.Everybody, "RPC_SetPicked", false);
                s_picked.Remove(p);
                Vector3 at = p.transform.position;
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{PrefabName(view)} at ({at.x:0},{at.z:0}) back after {Today - day} days");
            }
        }

        // ── the server's respawn registry ──────────────────────────────────────────────────────────────────────────────────────

        internal sealed class Record
        {
            public string Prefab, Kind, Location;
            public Vector3 Pos;
            public Quaternion Rot;
            public int Day;
            // A LootRespawn.Track record: due at an in-game second, its own duplicate radius, whether Core rolls the
            // default loot, and the tracking owner's ZDO keys to carry onto the new object ("name\ttype\tvalue" each).
            public bool Tracked;
            public double DueSeconds = -1;
            public float Radius = DuplicateRadius;
            public bool Defaults = true;
            public List<string> Carry = new List<string>();
            // A tracked chest's own "no player within" metres, from
            // its "fires_loot_range" ZDO key. 0 = no player check; -1 = the [Loot] "No refresh within metres" setting.
            public float PlayerRange = -1f;
        }

        private static readonly List<Record> s_records = new List<Record>();
        private const float RegisterReach = 256f;           // a client reports loot within its loaded area
        private const int MaxRegistersPerPeer = 4000, MaxRecords = 50000;
        private static readonly Dictionary<long, int> s_registeredBy = new Dictionary<long, int>();
        private static string s_registryWorld;

        private static void SendRegister(string prefab, Vector3 pos, Quaternion rot, int day, string kind, string location)
            => SendRegister(new Record { Prefab = prefab, Pos = pos, Rot = rot, Day = day, Kind = kind, Location = location });

        // An FDM room child's reset stamp rides on the record, so the respawned copy is still that room's.
        private static void SendRegister(string prefab, Vector3 pos, Quaternion rot, int day, string kind, string location, ZDO source)
        {
            var r = new Record { Prefab = prefab, Pos = pos, Rot = rot, Day = day, Kind = kind, Location = location };
            string carry = FiresCore.World.LocationReset.CarryEntry(source);
            if (carry != null) r.Carry.Add(carry);
            SendRegister(r);
        }

        internal static void SendRegister(Record r)
        {
            if (ZNet.instance == null) return;
            if (ZNet.instance.IsServer())
            {
                if (r.Kind == RespawnMarks.Kind && !RespawnMarks.ServerAccept(r, ZDOMan.GetSessionID())) return;   // the host's own path checks the mark too
                AddRecord(r);
                return;
            }
            if (ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage();
            pkg.Write(r.Prefab); pkg.Write(r.Pos); pkg.Write(r.Rot); pkg.Write(r.Day); pkg.Write(r.Kind); pkg.Write(r.Location ?? "");
            pkg.Write(r.Tracked); pkg.Write(r.DueSeconds); pkg.Write(r.Radius); pkg.Write(r.Defaults);   // tracked-record tail
            pkg.Write(r.Carry.Count);
            foreach (string c in r.Carry) pkg.Write(c);
            pkg.Write(r.PlayerRange);   // player-range tail
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RegisterRpc, pkg);
        }

        private static void RPC_Register(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                var r = new Record { Prefab = pkg.ReadString(), Pos = pkg.ReadVector3(), Rot = pkg.ReadQuaternion(), Day = pkg.ReadInt(), Kind = pkg.ReadString(), Location = pkg.ReadString() };
                if (pkg.GetPos() < pkg.Size())   // a sender with the tracked-record tail
                {
                    r.Tracked = pkg.ReadBool(); r.DueSeconds = pkg.ReadDouble(); r.Radius = pkg.ReadSingle(); r.Defaults = pkg.ReadBool();
                    int n = pkg.ReadInt();
                    for (int i = 0; i < n && i < 64; i++) r.Carry.Add(pkg.ReadString());
                    if (pkg.GetPos() < pkg.Size()) r.PlayerRange = pkg.ReadSingle();   // a sender with the player-range tail; older ones stay -1
                }
                GameObject known = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(r.Prefab) : null;
                if (known == null) return;   // only known prefabs
                // An admin "respawn when destroyed" record: only with the server's OWN mark (RespawnMarks), which also sets its time
                // and carried keys. The ZDO keys a client owns are never trusted for this.
                if (r.Kind == RespawnMarks.Kind)
                {
                    ZNetPeer markPeer = ZNet.instance.GetPeer(sender);
                    Vector3 markFrom = markPeer != null ? markPeer.m_refPos : r.Pos + Vector3.one * 1e6f;
                    if (Mathf.Max(Mathf.Abs(markFrom.x - r.Pos.x), Mathf.Abs(markFrom.z - r.Pos.z)) > RegisterReach) { FiresCore.Logging.FiresLogger.LogDebug($"[Respawn] refused register from {sender}: {r.Prefab} is {Vector3.Distance(markFrom, r.Pos):0} m from that player"); return; }
                    if (!RespawnMarks.ServerAccept(r, sender)) return;
                    AddRecord(r);
                    return;
                }
                // only what this system refreshes: chests, pickables and destructibles, never creatures or player pieces
                if ((known.GetComponent<Container>() == null && known.GetComponent<Pickable>() == null && known.GetComponent<Destructible>() == null && known.GetComponent<PickableItem>() == null)
                    || known.GetComponent<Character>() != null)
                { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}register from {sender} dropped: {r.Prefab} is not a refreshable chest / pickable / destructible"); return; }
                r.Carry.RemoveAll(c => { string k = c.Split('\t')[0]; return !k.StartsWith("fires_", StringComparison.Ordinal); });   // only our own ZDO keys ride along
                // a client registers what it sees around itself, and only so many per session; the registry has a total cap too
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 from = peer != null ? peer.m_refPos : (sender == ZDOMan.GetSessionID() && Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : r.Pos + Vector3.one * 1e6f);
                if (Mathf.Max(Mathf.Abs(from.x - r.Pos.x), Mathf.Abs(from.z - r.Pos.z)) > RegisterReach) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}register from {sender} dropped: {r.Prefab} is {Vector3.Distance(from, r.Pos):0} m from that player"); return; }
                s_registeredBy.TryGetValue(sender, out int sent);
                if (sent >= MaxRegistersPerPeer || s_records.Count >= MaxRecords) { if (sent == MaxRegistersPerPeer) FiresCore.Logging.FiresLogger.LogDebug($"{Tag}register from {sender} dropped: cap reached ({MaxRegistersPerPeer} per session, {MaxRecords} total)"); s_registeredBy[sender] = sent + 1; return; }
                s_registeredBy[sender] = sent + 1;
                AddRecord(r);
            }
            catch (Exception ex) { Debug.LogWarning(Tag + "register: " + ex.Message); }
        }

        private static void AddRecord(Record r)
        {
            LoadRegistry();
            float radius = Mathf.Max(0.1f, r.Radius);
            if (s_records.Any(x => x.Prefab == r.Prefab && (x.Pos - r.Pos).sqrMagnitude < radius * radius)) return;
            s_records.Add(r);
            SaveRegistry();
            FiresCore.Logging.FiresLogger.LogDebug(r.Tracked
                ? $"{Tag}tracked {r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}) emptied: respawns at in-game second {r.DueSeconds:0} ({r.Carry.Count} key(s) carried)"
                : $"{Tag}{r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}) in {r.Location} emptied on day {r.Day}: respawns after day {r.Day + Days}");
        }

        private static string RegistryPath()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
            return Path.Combine(BepInEx.Paths.ConfigPath, "FiresUnifiedCore", "LootRefresh", world + ".txt");
        }

        private static void LoadRegistry()
        {
            string path = RegistryPath();
            if (path == null || path == s_registryWorld) return;
            s_registryWorld = path;
            s_records.Clear();
            if (!File.Exists(path)) return;
            var inv = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] f = line.Split('|');
                if (f.Length < 11) continue;
                try
                {
                    s_records.Add(new Record
                    {
                        Prefab = f[0],
                        Pos = new Vector3(float.Parse(f[1], inv), float.Parse(f[2], inv), float.Parse(f[3], inv)),
                        Rot = new Quaternion(float.Parse(f[4], inv), float.Parse(f[5], inv), float.Parse(f[6], inv), float.Parse(f[7], inv)),
                        Day = int.Parse(f[8], inv), Kind = f[9], Location = f[10],
                        // Tracked-record tail (older lines have none): tracked | due second | radius | defaults | carried keys (\u001e between)
                        Tracked = f.Length > 11 && f[11] == "1",
                        DueSeconds = f.Length > 12 ? double.Parse(f[12], inv) : -1,
                        Radius = f.Length > 13 ? float.Parse(f[13], inv) : DuplicateRadius,
                        Defaults = f.Length <= 14 || f[14] == "1",
                        Carry = f.Length > 15 && f[15].Length > 0
                            ? f[15].Split('\u001e').Select(b => Encoding.UTF8.GetString(Convert.FromBase64String(b))).ToList() : new List<string>(),
                        PlayerRange = f.Length > 16 ? float.Parse(f[16], inv) : -1f,   // older lines: the setting
                    });
                }
                catch { }
            }
            FiresCore.Logging.FiresLogger.LogDebug($"{Tag}registry: {s_records.Count} waiting respawn(s) for {Path.GetFileNameWithoutExtension(path)}");
        }

        private static void SaveRegistry()
        {
            string path = RegistryPath();
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                foreach (Record r in s_records)
                    sb.Append(string.Join("|", new[] { r.Prefab, r.Pos.x.ToString("R", inv), r.Pos.y.ToString("R", inv), r.Pos.z.ToString("R", inv),
                        r.Rot.x.ToString("R", inv), r.Rot.y.ToString("R", inv), r.Rot.z.ToString("R", inv), r.Rot.w.ToString("R", inv),
                        r.Day.ToString(inv), r.Kind, (r.Location ?? "").Replace("|", "/"),
                        r.Tracked ? "1" : "0", r.DueSeconds.ToString("R", inv), r.Radius.ToString("R", inv), r.Defaults ? "1" : "0",
                        // carried values are a mod's own data (FAP's loot JSON): base64, so no '|' or line break can break the line
                        string.Join("\u001e", r.Carry.Select(c => Convert.ToBase64String(Encoding.UTF8.GetBytes(c))).ToArray()),
                        r.PlayerRange.ToString("R", inv) })).Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex) { Debug.LogWarning(Tag + "registry save failed: " + ex.Message); }
        }

        // Test SHORTCUT: `fires_lootrefresh_due` (admin) treats every waiting record as due for the next 120 s, so a
        // test needn't wait in-game days. The player range, the zone check and the toggle still apply. Logged as SHORTCUT.
        internal const string DueRpc = "FiresCore_LootRefresh_Due";
        private static float s_dueUntil = -1f;

        private static void RPC_Due(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!FiresCore.Possession.PossessionServer.IsAdmin(sender)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}fires_lootrefresh_due refused: peer {sender} is not an admin"); return; }
            s_dueUntil = Time.time + 120f;
            LoadRegistry();
            FiresCore.Logging.FiresLogger.LogDebug($"{Tag}SHORTCUT fires_lootrefresh_due: {s_records.Count} waiting record(s) count as due for 120 s (player range and zone rules still apply)");
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class LootRefresh_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("fires_lootrefresh_due",
                    "[FiresUnifiedCore] fires_lootrefresh_due - TEST SHORTCUT (admin): every waiting location/tracked loot record counts as due for 120 s.",
                    args =>
                    {
                        if (ZRoutedRpc.instance == null) { args.Context?.AddString("fires_lootrefresh_due: not in a world"); return; }
                        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), DueRpc, new ZPackage());
                        args.Context?.AddString("fires_lootrefresh_due: sent to the server (see the server log for the SHORTCUT line)");
                    });
            }
        }

        private static void ServerTick()
        {
            LoadRegistry();
            if (s_records.Count == 0 || ZoneSystem.instance == null || ZNetScene.instance == null) return;
            bool changed = false;
            int today = Today;
            double nowSeconds = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            foreach (Record r in s_records.ToList())
            {
                bool dueNow = Time.time < s_dueUntil;   // test SHORTCUT
                if (!dueNow && (r.Tracked ? nowSeconds < r.DueSeconds : today - r.Day < Days)) continue;
                if (!r.Tracked && !Enabled) continue;   // world location loot waits while the toggle is off; tracked ones never do
                // A tracked chest uses its own range when its owner mod set one (0 = no check); location loot stays on [Loot].
                float near = r.Tracked && r.PlayerRange >= 0f ? r.PlayerRange : Range;
                if (!ZoneSystem.instance.IsZoneLoaded(r.Pos) || PlayerNear(r.Pos, near)) continue;
                // a seed deposit (no player-near rule) only regrows into a clear spot: no character standing in it
                if (r.Kind == RespawnMarks.Kind && r.PlayerRange == 0f && Character.GetAllCharacters().Any(ch => ch != null && !ch.IsDead() && Vector3.Distance(ch.transform.position, r.Pos) < 2f)) continue;
                GameObject prefab = ZNetScene.instance.GetPrefab(r.Prefab);
                s_records.Remove(r);
                changed = true;
                if (prefab == null) continue;
                if (DuplicateAt(r)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}{r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}) already there: record dropped"); continue; }
                Respawn(r);
                if (r.Kind == RespawnMarks.Kind) FiresCore.Logging.FiresLogger.LogDebug($"[Respawn] {r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}): respawned");
                FiresCore.Logging.FiresLogger.LogDebug(r.Tracked
                    ? $"{Tag}tracked {r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}) respawned ({r.Carry.Count} key(s) carried, default loot {(r.Defaults ? "rolled" : "left to its owner mod")})"
                    : $"{Tag}{r.Prefab} ({r.Kind}) at ({r.Pos.x:0},{r.Pos.y:0},{r.Pos.z:0}) in {r.Location} respawned after {today - r.Day} days");
            }
            if (changed) SaveRegistry();
        }

        // Vanilla's own order (ZNetScene.CreateObject): the ZDO first, its fields set, then ZNetScene's normal pass
        // instantiates it, so every Awake already sees the carried keys. A tracked chest that leaves the loot to its owner mod
        // gets addedDefaultItems set first, so vanilla's AddDefaultItems doesn't roll on top of it.
        private static void Respawn(Record r)
        {
            ZDO zdo = ZDOMan.instance.CreateNewZDO(r.Pos, r.Prefab.GetStableHashCode());
            zdo.SetPrefab(r.Prefab.GetStableHashCode());
            zdo.SetRotation(r.Rot);
            zdo.Persistent = true;
            if (r.Tracked)
            {
                foreach (string c in r.Carry) LootRespawn.ApplyCarried(zdo, c);
                if (!r.Defaults) zdo.Set(ZDOVars.s_addedDefaultItems, true);
                LootRespawn.StampTrack(zdo, r);
            }
            else foreach (string c in r.Carry) LootRespawn.ApplyCarried(zdo, c);   // An untracked record's carried keys (the FDM room stamp)
        }

        private static bool DuplicateAt(Record r)
        {
            foreach (Collider c in Physics.OverlapSphere(r.Pos, Mathf.Max(0.1f, r.Radius)))
            {
                ZNetView view = c != null ? c.GetComponentInParent<ZNetView>() : null;
                if (view != null && view.IsValid() && PrefabName(view) == r.Prefab) return true;
            }
            return false;
        }

        // ── wiring ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_LootRefresh
        {
            private static void Postfix()
            {
                s_registryWorld = null;
                s_records.Clear();
                s_picked.Clear();
                if (ZRoutedRpc.instance != null) { ZRoutedRpc.instance.Register<ZPackage>(RegisterRpc, RPC_Register); ZRoutedRpc.instance.Register<ZPackage>(DueRpc, RPC_Due); }
                if (Runner.Instance == null) new GameObject("FiresLootRefresh").AddComponent<Runner>();
            }
        }

        private sealed class Runner : MonoBehaviour
        {
            internal static Runner Instance;
            private float _nextServer, _nextOwner, _nextStand;

            private void Awake() { Instance = this; DontDestroyOnLoad(gameObject); }
            private void OnDestroy() { if (Instance == this) Instance = null; }

            private void Update()
            {
                if (ZNet.instance == null) return;   // Tracked objects (LootRespawn) tick whatever the location-loot toggle says
                float now = Time.time;
                try
                {
                    if (now >= _nextOwner) { _nextOwner = now + OwnerTickSeconds; if (Enabled) OwnerTick(); }
                    // Tracked stands get their own fast tick, for emptiness AND due time.
                    if (now >= _nextStand) { _nextStand = now + StandTickSeconds; LootRespawn.OwnerTick(); }
                    if (ZNet.instance.IsServer() && now >= _nextServer) { _nextServer = now + ServerTickSeconds; ServerTick(); }
                }
                catch (Exception ex) { Debug.LogWarning(Tag + ex.Message); }
            }
        }
    }

    /// <summary>
    /// Core's respawn service for objects another mod places, e.g. FAP's preset chests and item
    /// stands. Always on when Core is installed; the [Loot] "Location loot refresh" toggle governs only world location loot. Delays are
    /// in-game SECONDS (ZNet.GetTimeSeconds), from the caller's own presets. The "No refresh within metres" rule applies.
    ///   Track: a chest. Emptied and closed, it is recorded on the server and removed by vanilla's m_autoDestroyEmpty; after the
    ///     delay (a random time between delay and max), with its zone loaded and no player near, the server makes a new ZDO of the same
    ///     prefab, writes the carried keys and the tracking back onto it, and ZNetScene builds it. Core rolls vanilla's default loot
    ///     only when asked (refillWithDefaults); otherwise the owner mod fills it (FAP's applier sees its preset key).
    ///   TrackRefill: a stand that is emptied, never destroyed (ItemStand's "item" key goes 0). After the delay with no player near,
    ///     the owner raises RefillDue(zdo) once; the owner mod hangs the item back.
    /// Call both on the ZDO's owner.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Loot)]
    public static class LootRespawn
    {
        internal static readonly int TrackKey = "fires_loot_track".GetStableHashCode();
        private static readonly int DelayKey = "fires_loot_delay".GetStableHashCode();
        private static readonly int DelayMaxKey = "fires_loot_delay_max".GetStableHashCode();
        private static readonly int RadiusKey = "fires_loot_radius".GetStableHashCode();
        private static readonly int DefaultsKey = "fires_loot_defaults".GetStableHashCode();
        private static readonly int CarryKey = "fires_loot_carry".GetStableHashCode();
        private static readonly int RefillKey = "fires_loot_refill".GetStableHashCode();
        private static readonly int EmptySinceKey = "fires_loot_empty_since_ms".GetStableHashCode();
        private static readonly int RefillPendingKey = "fires_loot_refill_pending".GetStableHashCode();
        private static readonly int DueDelayKey = "fires_loot_due_delay".GetStableHashCode();
        private static bool s_noHandlerLogged;
        private static readonly HashSet<ZDOID> s_skipLogged = new HashSet<ZDOID>();   // one "waits" line per empty spell
        private const string Tag = "[LootRefresh] ";

        /// <summary>True while Core's respawn service runs (always, with Core installed); false outside a world.</summary>
        public static bool Active => ZNet.instance != null;

        /// <summary>Raised on the stand's owner when a TrackRefill stand has been empty for its delay with no player near.</summary>
        public static event Action<ZDO> RefillDue;

        public static void Track(ZDO zdo, float delaySeconds, float delaySecondsMax = 0f, float preventRadius = 1f, bool refillWithDefaults = false, params string[] carryKeys)
        {
            if (zdo == null) return;
            zdo.Set(TrackKey, 1);
            zdo.Set(DelayKey, Mathf.Max(0f, delaySeconds));
            zdo.Set(DelayMaxKey, Mathf.Max(0f, delaySecondsMax));
            zdo.Set(RadiusKey, Mathf.Max(0.1f, preventRadius));
            zdo.Set(DefaultsKey, refillWithDefaults);
            zdo.Set(CarryKey, carryKeys != null ? string.Join("\n", carryKeys.Where(k => !string.IsNullOrEmpty(k)).ToArray()) : "");
            FiresCore.Logging.FiresLogger.LogDebug($"{Tag}tracking a chest: respawn {delaySeconds:0}{(delaySecondsMax > delaySeconds ? "-" + delaySecondsMax.ToString("0") : "")} s after it empties, {carryKeys?.Length ?? 0} key(s) carried");
        }

        public static void TrackRefill(ZDO zdo, float delaySeconds, float delaySecondsMax = 0f)
        {
            if (zdo == null) return;
            zdo.Set(RefillKey, 1);
            zdo.Set(DelayKey, Mathf.Max(0f, delaySeconds));
            zdo.Set(DelayMaxKey, Mathf.Max(0f, delaySecondsMax));
            // FAP tracks a stand right AFTER it was
            // placed, when its Awake (where stands join the owner tick) has already run, so the live stand joins here too.
            bool live = false;
            try
            {
                GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo.m_uid) : null;
                ItemStand stand = go != null ? go.GetComponentInChildren<ItemStand>() : null;
                if (stand != null) { s_stands.Add(stand); live = true; }
            }
            catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"{Tag}tracking a stand: refill due {delaySeconds:0}{(delaySecondsMax > delaySeconds ? "-" + delaySecondsMax.ToString("0") : "")} s after it empties{(live ? "" : " (not loaded here: joins when its zone loads)")}");
        }

        /// <summary>TrackRefill with the stand's own "no player within" distance. 0 = it refills even with players
        /// right at it; more than 0 = that many metres. Stamped on the ZDO, so it holds across reloads. The 3-argument TrackRefill and chests
        /// keep [Loot] "No refresh within metres".</summary>
        public static void TrackRefill(ZDO zdo, float delaySeconds, float delaySecondsMax, float playerRange)
        {
            if (zdo == null) return;
            zdo.Set(RangeKey, Mathf.Max(0f, playerRange));
            TrackRefill(zdo, delaySeconds, delaySecondsMax);
        }

        // The stand's own range: Core's key (the 4-argument TrackRefill), else FAP's stand-preset key, else the [Loot] setting.
        private static readonly int RangeKey = "fires_loot_range".GetStableHashCode();
        private static readonly int FapRangeKey = "va_stand_refill_clear_m".GetStableHashCode();

        private static float StandRange(ZDO zdo, out string from)
        {
            float r = zdo.GetFloat(RangeKey, -1f);
            if (r >= 0f) { from = "this stand"; return r; }
            r = zdo.GetFloat(FapRangeKey, -1f);
            if (r >= 0f) { from = "this stand's preset"; return r; }
            from = "[Loot] No refresh within metres";
            return LocationLootRefresh.Range;
        }

        private static double Now => ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;

        private static float DelayOf(ZDO zdo)
        {
            float d = zdo.GetFloat(DelayKey, 0f), max = zdo.GetFloat(DelayMaxKey, 0f);
            return max > d ? UnityEngine.Random.Range(d, max) : d;
        }

        // ── chests: called from the Container poll on the owner ────────────────────────────────────────────────────────────

        private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");

        internal static void TrackedContainerTick(Container c, ZNetView view)
        {
            ZDO zdo = view.GetZDO();
            if (LocationLootRefresh.OwnedByEwp(zdo)) return;   // EWP's chest, never Core's
            Inventory inv = c.GetInventory();
            if (inv == null || inv.NrOfItems() > 0) return;
            if (zdo.GetBool(LocationLootRefresh.RegisteredKeyPublic)) { c.m_autoDestroyEmpty = true; return; }
            if (c.IsInUse()) return;
            zdo.Set(LocationLootRefresh.RegisteredKeyPublic, true);
            var r = new LocationLootRefresh.Record
            {
                Prefab = LocationLootRefresh.PrefabName(view), Pos = c.transform.position, Rot = c.transform.rotation, Day = LocationLootRefresh.Today,
                Kind = "tracked", Location = "", Tracked = true, DueSeconds = Now + DelayOf(zdo),
                Radius = zdo.GetFloat(RadiusKey, 1f), Defaults = zdo.GetBool(DefaultsKey, false),
                PlayerRange = zdo.GetFloat(RangeKey, -1f),   // FAP sets it before Track (and carries it), -1 = the setting
            };
            foreach (string name in zdo.GetString(CarryKey, "").Split('\n'))
            {
                if (string.IsNullOrEmpty(name)) continue;
                string carried = Read(zdo, name);
                if (carried != null) r.Carry.Add(carried);
            }
            r.Carry.Add("fires_loot_delay\tf\t" + zdo.GetFloat(DelayKey, 0f).ToString("R", CultureInfo.InvariantCulture));
            r.Carry.Add("fires_loot_delay_max\tf\t" + zdo.GetFloat(DelayMaxKey, 0f).ToString("R", CultureInfo.InvariantCulture));
            r.Carry.Add("fires_loot_carry\ts\t" + zdo.GetString(CarryKey, "").Replace("\n", "\u001f"));
            LocationLootRefresh.SendRegister(r);
            c.m_autoDestroyEmpty = true;   // vanilla's CheckForChanges removes it on its next poll
        }

        // "name\ttype\tvalue": the first type the ZDO holds the name as (string, int, float); an int also covers a bool.
        private static string Read(ZDO zdo, string name)
        {
            var inv = CultureInfo.InvariantCulture;
            if (zdo.GetString(name, out string s)) return $"{name}\ts\t{s}";
            if (zdo.GetInt(name, out int i)) return $"{name}\ti\t{i.ToString(inv)}";
            if (zdo.GetFloat(name, out float f)) return $"{name}\tf\t{f.ToString("R", inv)}";
            return null;
        }

        internal static void ApplyCarried(ZDO zdo, string carried)
        {
            string[] p = carried.Split(new[] { '\t' }, 3);
            if (p.Length < 3) return;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                switch (p[1])
                {
                    case "s": zdo.Set(p[0], p[0] == "fires_loot_carry" ? p[2].Replace('\u001f', '\n') : p[2]); break;
                    case "i": zdo.Set(p[0], int.Parse(p[2], inv)); break;
                    case "f": zdo.Set(p[0], float.Parse(p[2], inv)); break;
                }
            }
            catch { }
        }

        // The tracking itself back on the new ZDO (radius and defaults from the record; delays and the carry list ride as carried keys).
        internal static void StampTrack(ZDO zdo, LocationLootRefresh.Record r)
        {
            zdo.Set(TrackKey, 1);
            zdo.Set(RadiusKey, r.Radius);
            zdo.Set(DefaultsKey, r.Defaults);
        }

        // ── stands: the owner tick ──────────────────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<ItemStand> s_stands = new HashSet<ItemStand>();
        private static readonly AccessTools.FieldRef<ItemStand, ZNetView> StandView = AccessTools.FieldRefAccess<ItemStand, ZNetView>("m_nview");

        [HarmonyPatch(typeof(ItemStand), "Awake")]
        private static class ItemStand_Awake_LootRespawn
        {
            private static void Postfix(ItemStand __instance)
            {
                ZNetView view = __instance != null ? StandView(__instance) : null;
                if (view != null && view.IsValid() && view.GetZDO().GetInt(RefillKey, 0) == 1) s_stands.Add(__instance);
            }
        }

        internal static void OwnerTick()
        {
            if (s_stands.Count == 0) return;
            foreach (ItemStand stand in s_stands.ToList())
            {
                ZNetView view = stand != null ? StandView(stand) : null;
                if (view == null || !view.IsValid()) { s_stands.Remove(stand); continue; }
                if (!view.IsOwner()) continue;
                ZDO zdo = view.GetZDO();
                if (zdo.GetInt(RefillKey, 0) != 1) { s_stands.Remove(stand); continue; }
                long since = zdo.GetLong(EmptySinceKey, 0L);
                int itemHash = zdo.GetInt(ZDOVars.s_item, 0);
                if (itemHash != 0)
                {
                    if (since != 0L) zdo.Set(EmptySinceKey, 0L);
                    // The round trip made provable: after a RefillDue, the item coming back is logged.
                    if (zdo.GetBool(RefillPendingKey))
                    {
                        zdo.Set(RefillPendingKey, false);
                        Vector3 hp = stand.transform.position;
                        string item = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash)?.name : null;
                        FiresCore.Logging.FiresLogger.LogDebug($"{Tag}stand {LocationLootRefresh.PrefabName(view)} at ({hp.x:0},{hp.z:0}) re-hung: {item ?? itemHash.ToString()}");
                    }
                    continue;
                }
                long nowMs = (long)(Now * 1000.0);
                Vector3 sp = stand.transform.position;
                if (since == 0L)
                {
                    // The delay is rolled ONCE when the stand empties (it was re-rolled every tick between min and max).
                    float delay = DelayOf(zdo);
                    zdo.Set(EmptySinceKey, nowMs);
                    zdo.Set(DueDelayKey, delay);
                    s_skipLogged.Remove(zdo.m_uid);
                    FiresCore.Logging.FiresLogger.LogDebug($"{Tag}stand {LocationLootRefresh.PrefabName(view)} at ({sp.x:0},{sp.z:0}) emptied: refill timer started, {delay:0.#} s");
                    continue;
                }
                if ((nowMs - since) / 1000.0 < zdo.GetFloat(DueDelayKey, DelayOf(zdo))) continue;
                float range = StandRange(zdo, out string rangeFrom);
                if (LocationLootRefresh.PlayerNear(sp, range))
                {
                    if (s_skipLogged.Add(zdo.m_uid))
                        FiresCore.Logging.FiresLogger.LogDebug($"{Tag}stand {LocationLootRefresh.PrefabName(view)} at ({sp.x:0},{sp.z:0}) refill waits: a player is within {range:0.#} m ({rangeFrom})");
                    continue;
                }
                s_skipLogged.Remove(zdo.m_uid);
                zdo.Set(EmptySinceKey, 0L);
                Vector3 p = stand.transform.position;
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}stand {LocationLootRefresh.PrefabName(view)} at ({p.x:0},{p.z:0}) refill due (set {zdo.GetFloat(DueDelayKey, 0f):0.#} s, actual {(nowMs - since) / 1000.0:0.0} s since emptied)");
                zdo.Set(RefillPendingKey, true);
                if (RefillDue == null && !s_noHandlerLogged)
                {
                    s_noHandlerLogged = true;   // once a session: the stand re-arms and comes due again until a mod handles it
                    FiresCore.Logging.FiresLogger.LogDebug($"{Tag}a stand's refill is due but no mod handles LootRespawn.RefillDue (FAP not subscribed yet?); it stays due and is offered again");
                }
                try { RefillDue?.Invoke(zdo); }
                catch (Exception ex) { Debug.LogWarning($"{Tag}a RefillDue handler threw: {ex.Message}"); }
            }
        }
    }
}
