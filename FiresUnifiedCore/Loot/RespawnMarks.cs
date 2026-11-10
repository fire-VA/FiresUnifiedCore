using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.UI.ContextMenu;

namespace FiresCore.Loot
{
    /// <summary>
    /// Admin "Respawn when destroyed" marks (Fire 10-24: "give us admin context menu right click on minerock or other resources and set
    /// them to respawn when they are destroyed/removed"). It rides on Core's one respawn service (LocationLootRefresh / LootRespawn):
    /// a marked object that is destroyed registers a tracked record there, and the server's ServerTick respawns it after the mark's time.
    ///
    /// Authority stays on the SERVER ([lead] 10-24). The server keeps its own world-saved list of marks (prefab + position), written only
    /// by the admin-checked <see cref="MarkRpc"/> handler (AdminSyncing.IsAdmin(sender), the 787 rule; the sender comes from the
    /// connection). LocationLootRefresh accepts an "admin" record only when this list has the mark, and the respawn time comes from the
    /// list. The ZDO keys (fires_respawn_min / fires_respawn_by) are only a copy for the admins' hover text and to carry across a respawn.
    /// </summary>
    public static class RespawnMarks
    {
        internal const string Kind = "admin";
        private const string MarkRpc = "FiresCore_RespawnMark";
        internal static readonly int[] Minutes = { 10, 30, 60, 120, 240 };
        private const int MaxMarks = 5000;
        private const float MatchRadius = 1.5f, MarkReach = 64f;
        internal static readonly int MinKey = "fires_respawn_min".GetStableHashCode();
        internal static readonly int ByKey = "fires_respawn_by".GetStableHashCode();
        internal static readonly int SeedKey = "fires_mineral_seed".GetStableHashCode();
        private const string Tag = "[Respawn] ";

        internal sealed class Mark { public string Prefab, By, Kind = "admin"; public Vector3 Pos; public int Minutes; }   // Kind: "admin" (794) or "seed" (MineralSeeds, 797)
        private static readonly List<Mark> s_marks = new List<Mark>();
        private static string s_world;
        private static bool s_providerRegistered;

        // ---- what can be marked ------------------------------------------------------------------------------------------
        /// <summary>A world resource: a rock / deposit, a destructible, a tree or a world pickable; never a creature or a player piece.</summary>
        internal static bool IsMarkable(GameObject go)
        {
            if (go == null || go.GetComponentInParent<Character>() != null) return false;
            var piece = go.GetComponentInParent<Piece>();
            if (piece != null && piece.GetCreator() != 0L) return false;
            return go.GetComponentInParent<MineRock5>() != null || go.GetComponentInParent<MineRock>() != null || go.GetComponentInParent<Destructible>() != null
                || go.GetComponentInParent<TreeBase>() != null || go.GetComponentInParent<Pickable>() != null || go.GetComponentInParent<PickableItem>() != null;
        }

        private static ZNetView ViewOf(GameObject go)
        {
            if (go == null) return null;
            foreach (var v in go.GetComponentsInParent<ZNetView>(true)) if (v != null && v.IsValid()) return v;
            return null;
        }

        // ---- server registry -------------------------------------------------------------------------------------------
        /// <summary>Server: the mark for this prefab near this spot.</summary>
        internal static bool TryGetMark(string prefab, Vector3 pos, out Mark mark)
        {
            Load();
            mark = s_marks.FirstOrDefault(m => m.Prefab == prefab && (m.Pos - pos).sqrMagnitude < MatchRadius * MatchRadius);
            return mark != null;
        }

        private static string PathFor()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
            return Path.Combine(BepInEx.Paths.ConfigPath, "FiresUnifiedCore", "LootRefresh", world + ".respawnmarks.txt");
        }

        private static void Load()
        {
            string path = PathFor();
            if (path == null || path == s_world) return;
            s_world = path; s_marks.Clear();
            if (!File.Exists(path)) return;
            var inv = CultureInfo.InvariantCulture;
            foreach (string line in File.ReadAllLines(path))
            {
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                try
                {
                    int min = int.Parse(f[4], inv);
                    string kind = f.Length > 6 ? f[6] : "admin";
                    if (kind == "admin" && !Minutes.Contains(min)) continue;
                    s_marks.Add(new Mark { Prefab = f[0], Pos = new Vector3(float.Parse(f[1], inv), float.Parse(f[2], inv), float.Parse(f[3], inv)), Minutes = min, By = f[5], Kind = kind });
                }
                catch { }
                if (s_marks.Count >= MaxMarks) break;
            }
        }

        private static void Save()
        {
            string path = PathFor();
            if (path == null) return;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, s_marks.Select(m => string.Join("\t", m.Prefab, m.Pos.x.ToString("R", inv), m.Pos.y.ToString("R", inv), m.Pos.z.ToString("R", inv), m.Minutes.ToString(inv), (m.By ?? "").Replace("\t", " "), m.Kind ?? "admin")).ToArray());
            }
            catch (Exception e) { Debug.LogWarning(Tag + "save: " + e.Message); }
        }

        // ---- the admin's request ---------------------------------------------------------------------------------------
        internal static void RequestMark(GameObject target, int minutes)
        {
            var view = ViewOf(target);
            if (view == null || ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage();
            pkg.Write(view.GetZDO().m_uid);
            pkg.Write(minutes);   // 0 = unmark
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), MarkRpc, pkg);
        }

        private static void RPC_Mark(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                ZDOID uid = pkg.ReadZDOID();
                int minutes = pkg.ReadInt();
                if (!FiresCore.Sync.AdminSyncing.IsAdmin(sender)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused mark from {sender}: not an admin"); return; }
                if (minutes != 0 && !Minutes.Contains(minutes)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused mark from {sender}: {minutes} min is not one of {string.Join("/", Minutes)}"); return; }
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                GameObject prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                if (prefab == null || !IsMarkable(prefab)) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused mark from {sender}: not a world resource"); return; }
                Vector3 pos = zdo.GetPosition();
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                Vector3 from = peer != null ? peer.m_refPos : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : pos + Vector3.one * 1e6f);
                if (Vector3.Distance(from, pos) > MarkReach) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused mark from {sender}: {prefab.name} is {Vector3.Distance(from, pos):0} m away"); return; }
                string by = peer != null ? peer.m_playerName : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : sender.ToString());
                Load();
                s_marks.RemoveAll(m => m.Prefab == prefab.name && (m.Pos - pos).sqrMagnitude < MatchRadius * MatchRadius);
                if (minutes > 0)
                {
                    if (s_marks.Count >= MaxMarks) { FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused mark from {by}: {MaxMarks} marks already"); return; }
                    s_marks.Add(new Mark { Prefab = prefab.name, Pos = pos, Minutes = minutes, By = by });
                }
                Save();
                // the display / carry copy on the object (never the authority)
                zdo.Set(MinKey, minutes);
                zdo.Set(ByKey, minutes > 0 ? by : "");
                ZDOMan.instance.ForceSendZDO(uid);
                FiresCore.Logging.FiresLogger.LogDebug(minutes > 0
                    ? $"{Tag}{prefab.name} at ({pos.x:0},{pos.z:0}): marked by {by} (respawn {minutes} min)"
                    : $"{Tag}{prefab.name} at ({pos.x:0},{pos.z:0}): unmarked by {by}");
            }
            catch (Exception e) { Debug.LogWarning(Tag + "mark: " + e.Message); }
        }

        /// <summary>Server: an "admin" record is accepted only with the server's own mark; the time and carried keys come from it.</summary>
        internal static bool ServerAccept(LocationLootRefresh.Record r, long sender)
        {
            if (!TryGetMark(r.Prefab, r.Pos, out var m))
            {
                FiresCore.Logging.FiresLogger.LogDebug($"{Tag}refused register from {sender}: {r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}) not admin-marked");
                return false;
            }
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            bool seed = m.Kind == "seed";
            // a seed deposit sits at the player's own base, where a player is always near: no player-near rule, but it only comes back
            // into a clear spot (LocationLootRefresh.ServerTick's duplicate check + no character within 2 m)
            r.Tracked = true; r.DueSeconds = now + m.Minutes * 60.0; r.Radius = MatchRadius; r.Defaults = true; r.PlayerRange = seed ? 0f : -1f;
            r.Carry.Clear();
            r.Carry.Add("fires_respawn_min\ti\t" + m.Minutes.ToString(CultureInfo.InvariantCulture));
            r.Carry.Add("fires_respawn_by\ts\t" + (m.By ?? ""));
            if (seed) r.Carry.Add("fires_mineral_seed\ti\t1");
            FiresCore.Logging.FiresLogger.LogDebug(seed
                ? $"[MineralSeeds] {r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}): mined out, regrows in {m.Minutes / 60f / (DayMinutes / 60f):0.#} day(s)"
                : $"{Tag}{r.Prefab} at ({r.Pos.x:0},{r.Pos.z:0}): destroyed, respawns in {m.Minutes} min");
            return true;
        }

        internal static float DayMinutes => (EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f) / 60f;

        /// <summary>Server: a seed deposit's mark (MineralSeeds), written only by its server-checked grow handler.</summary>
        internal static bool AddSeedMark(string prefab, Vector3 pos, int minutes, string by)
        {
            Load();
            if (s_marks.Count >= MaxMarks) return false;
            s_marks.RemoveAll(m => m.Prefab == prefab && (m.Pos - pos).sqrMagnitude < MatchRadius * MatchRadius);
            s_marks.Add(new Mark { Prefab = prefab, Pos = pos, Minutes = Mathf.Max(1, minutes), By = by, Kind = "seed" });
            Save();
            return true;
        }

        internal static int CountSeedMarksNear(Vector3 pos, float radius) { Load(); return s_marks.Count(m => m.Kind == "seed" && Vector3.Distance(m.Pos, pos) <= radius); }
        internal static bool IsSeedMark(string prefab, Vector3 pos) => TryGetMark(prefab, pos, out var m) && m.Kind == "seed";

        // ---- the trigger: a marked object removed on its owner -------------------------------------------------------------
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
        private static class ZNetScene_Destroy_RespawnMark
        {
            private static void Prefix(GameObject go)
            {
                try
                {
                    var view = go != null ? go.GetComponent<ZNetView>() : null;
                    if (view == null || !view.IsValid() || !view.IsOwner()) return;
                    ZDO zdo = view.GetZDO();
                    if (zdo.GetInt(MinKey, 0) <= 0) return;   // the copy says "marked"; the server decides
                    string prefab = LocationLootRefresh.PrefabName(view);
                    LocationLootRefresh.SendRegister(new LocationLootRefresh.Record
                    {
                        Prefab = prefab, Pos = go.transform.position, Rot = go.transform.rotation, Day = LocationLootRefresh.Today,
                        Kind = Kind, Location = "", Tracked = true, Radius = MatchRadius,
                    });
                }
                catch (Exception e) { Debug.LogWarning(Tag + "destroy: " + e.Message); }
            }
        }

        // ---- the admin's context rows + hover ---------------------------------------------------------------------------
        private sealed class Provider : IContextMenuProvider
        {
            public string TitleFor(ContextTarget target) => null;

            public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
            {
                if (target?.GameObject == null || !FiresCore.Sync.AdminSyncing.IsLocalAdmin() || !IsMarkable(target.GameObject)) return null;
                var view = ViewOf(target.GameObject);
                if (view == null) return null;
                var go = target.GameObject;
                int now = view.GetZDO().GetInt(MinKey, 0);
                var rows = new List<ContextMenuItem>();
                if (now > 0) rows.Add(ContextMenuItem.Row($"Respawn when destroyed: ON ({now} min) - turn off", () => RequestMark(go, 0)));
                foreach (int m in Minutes)
                    if (m != now) rows.Add(ContextMenuItem.Row($"Respawn when destroyed: {m} min", () => RequestMark(go, m)));
                return rows;
            }
        }

        [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
        private static class Hud_UpdateCrosshair_RespawnMark
        {
            private static void Postfix(Hud __instance, Player player)
            {
                try
                {
                    if (player == null || __instance.m_hoverName == null) return;
                    var view = ViewOf(player.GetHoverObject());
                    if (view == null) return;
                    int min = view.GetZDO().GetInt(MinKey, 0);
                    if (min <= 0) return;
                    bool seed = view.GetZDO().GetInt(SeedKey, 0) == 1;
                    if (!seed && !FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return;   // admin marks show to admins; seed deposits to everyone
                    string line = seed ? $"\n<size=80%>Mineral seed deposit · regrows {min / DayMinutes:0.#} day(s) after it's mined out</size>"
                                       : $"\n<size=80%>Respawns: on, {min} min</size>";
                    if (!__instance.m_hoverName.text.EndsWith(line)) __instance.m_hoverName.text += line;
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_RespawnMarks
        {
            private static void Postfix()
            {
                s_world = null; s_marks.Clear();
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.Register<ZPackage>(MarkRpc, RPC_Mark);
                if (!s_providerRegistered) { s_providerRegistered = true; FiresContextMenu.Register(new Provider()); }
            }
        }
    }
}
