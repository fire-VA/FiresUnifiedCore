using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Summon
{
    /// <summary>
    /// The local side of summons: spawning, the list for UI, the mode (follow/guard/attack) on the summons this client owns,
    /// the save with the character (Player.m_customData "fires_summons", kept current so every profile save holds it), and the
    /// despawn + re-spawn around logout, login and portals. Death despawns without saving.
    /// </summary>
    internal static class SummonClient
    {
        private const string SaveKey = "fires_summons";
        private const string SaveVersion = "v1";
        private const float ScanEvery = 1f;
        private const float RespawnDelay = 3f;

        internal static readonly List<SummonInfo> Mine = new List<SummonInfo>();
        internal static int LastCap = 2;

        private static float s_nextScan;
        private static bool s_ringLogged;
        private static float s_respawnAt = -1f;
        private static string s_respawnList;
        private static bool s_teleportPending;
        // True while a saved list waits to be re-spawned (logout, portal, login): the save must not be overwritten meanwhile.
        private static bool s_holdSave;
        private static readonly HashSet<ZDOID> s_lastIds = new HashSet<ZDOID>();

        private static long MyId => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;

        // ── spawning ─────────────────────────────────────────────────────────────────────────────────────────────────

        internal static ZDOID Spawn(string prefabName, SummonOptions o, out string reason)
            => Spawn(prefabName, o, o.Duration, out reason);

        private static ZDOID Spawn(string prefabName, SummonOptions o, float seconds, out string reason)
        {
            reason = null;
            Player p = Player.m_localPlayer;
            if (p == null || ZNetScene.instance == null) { reason = "no player"; return ZDOID.None; }
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null || prefab.GetComponent<Character>() == null) { reason = "unknown creature '" + prefabName + "'"; return ZDOID.None; }

            Vector3 pos = p.transform.position + p.transform.forward * 2f + UnityEngine.Random.insideUnitSphere * 0.5f;
            Quaternion rot;
            if (FiresCore.World.GravityUp.InField(pos, out Vector3 ringUp))
            {
                // halo ring: stand the summon on the wall floor along the local up, upright in the local frame (no flat heightmap)
                if (FiresCore.World.Surface.Point(pos, out Vector3 floor, out _)) pos = floor + ringUp * 0.2f;
                rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(p.transform.forward, ringUp), ringUp);
                if (!s_ringLogged) { s_ringLogged = true; FiresCore.Logging.FiresLogger.LogDebug($"[Summon] halo ring: '{prefabName}' placed on the wall floor at {pos:F0}, up {ringUp:F2}"); }
            }
            else
            {
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(pos, out float h)) pos.y = h + 0.2f;
                rot = Quaternion.LookRotation(p.transform.forward);
            }
            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            Character c = go.GetComponent<Character>();
            ZNetView nv = go.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) { UnityEngine.Object.Destroy(go); reason = "not networked"; return ZDOID.None; }

            ZDO zdo = nv.GetZDO();
            long now = SummonKeys.NowMs;
            zdo.Set(SummonKeys.Owner, p.GetPlayerID());
            zdo.Set(SummonKeys.Made, now);
            zdo.Set(SummonKeys.Expiry, seconds > 0f ? now + (long)(seconds * 1000f) : 0L);
            zdo.Set(SummonKeys.Mode, (int)o.Mode);
            zdo.Set(SummonKeys.Group, o.CapGroup ?? "");
            zdo.Set(SummonKeys.Source, o.SourceTag ?? "");
            zdo.Set(SummonKeys.Level, Mathf.Max(1, o.Level));
            zdo.Set(SummonKeys.Scale, o.Scale > 0f ? o.Scale : 1f);
            if (o.Level > 1) c.SetLevel(o.Level);
            if (o.Scale > 0f && !Mathf.Approximately(o.Scale, 1f)) nv.SetLocalScale(prefab.transform.localScale * o.Scale);
            c.SetTamed(true);
            ApplyMode(c, o.Mode, p);

            LastCap = Mathf.Clamp(o.Cap, 1, 6);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), SummonServer.RegisterRpc, zdo.m_uid, o.CapGroup ?? "", LastCap);
            FiresLogger.LogDebug(SummonKeys.Tag + "summoned " + prefabName + " " + zdo.m_uid + " (lvl " + o.Level + ", scale " + o.Scale + ", " + (seconds > 0f ? seconds.ToString("0") + " s" : "no timer") + ", " + o.Mode + ", " + o.SourceTag + ")");
            Scan();
            var info = Mine.Find(i => i.Id == zdo.m_uid);
            if (info != null) SummonApi.RaiseSummoned(info);
            Save();
            return zdo.m_uid;
        }

        internal static void Dismiss(ZDOID id)
        {
            if (id.IsNone() || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), SummonServer.DismissRpc, id);
        }

        /// <summary>Removes every summon of the local player; with save, the list is written to the character first.</summary>
        internal static void DismissAll(bool save)
        {
            if (save) { if (Mine.Count > 0) Save(); s_holdSave = true; }
            else { s_holdSave = false; ClearSave(); }
            SendDismissAll();
        }

        private static void SendDismissAll()
        {
            if (MyId == 0L || ZRoutedRpc.instance == null) return;
            // The ones this client owns go at once (also covers a server that restarted and lost its registry).
            foreach (Character c in new List<Character>(Character.GetAllCharacters()))
            {
                ZNetView nv = c != null ? c.GetComponent<ZNetView>() : null;
                if (nv != null && nv.IsValid() && nv.IsOwner() && nv.GetZDO().GetLong(SummonKeys.Owner, 0L) == MyId) ZNetScene.instance.Destroy(c.gameObject);
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), SummonServer.DismissAllRpc, MyId);
        }

        internal static void SetMode(ZDOID id, SummonMode mode)
        {
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            ZNetView nv = go != null ? go.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid() || nv.GetZDO().GetLong(SummonKeys.Owner, 0L) != MyId) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();
            nv.GetZDO().Set(SummonKeys.Mode, (int)mode);
            ApplyMode(go.GetComponent<Character>(), mode, Player.m_localPlayer);
            Scan();
            Save();
        }

        private static void ApplyMode(Character c, SummonMode mode, Player owner)
        {
            if (c == null || !(c.GetBaseAI() is MonsterAI ai)) return;
            GameObject want = mode == SummonMode.Follow && owner != null ? owner.gameObject : null;
            if (ai.GetFollowTarget() != want) ai.SetFollowTarget(want);
        }

        // ── per-frame ────────────────────────────────────────────────────────────────────────────────────────────────

        internal static void Update()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;

            if (s_teleportPending && !p.IsTeleporting())
            {
                s_teleportPending = false;
                s_respawnList = ReadSave();
                s_respawnAt = Time.time + 1f;
            }
            if (s_respawnAt > 0f && Time.time >= s_respawnAt)
            {
                s_respawnAt = -1f;
                s_holdSave = false;
                RespawnFrom(s_respawnList);
                s_respawnList = null;
            }
            if (Time.time < s_nextScan) return;
            s_nextScan = Time.time + ScanEvery;
            Scan();
        }

        /// <summary>Rebuilds Mine from the loaded characters, keeps the owned ones in their mode, and fires Changed/Despawned.</summary>
        private static void Scan()
        {
            long me = MyId;
            Player p = Player.m_localPlayer;
            Mine.Clear();
            var ids = new HashSet<ZDOID>();
            if (me != 0L)
            {
                long now = SummonKeys.NowMs;
                foreach (Character c in Character.GetAllCharacters())
                {
                    ZNetView nv = c != null ? c.GetComponent<ZNetView>() : null;
                    if (nv == null || !nv.IsValid()) continue;
                    ZDO zdo = nv.GetZDO();
                    if (zdo.GetLong(SummonKeys.Owner, 0L) != me) continue;
                    long exp = zdo.GetLong(SummonKeys.Expiry, 0L);
                    if (exp > 0L && now >= exp && nv.IsOwner()) { ZNetScene.instance.Destroy(c.gameObject); continue; }
                    var mode = (SummonMode)zdo.GetInt(SummonKeys.Mode, 0);
                    if (nv.IsOwner()) ApplyMode(c, mode, p);
                    ids.Add(zdo.m_uid);
                    Mine.Add(new SummonInfo
                    {
                        Id = zdo.m_uid,
                        Prefab = Utils.GetPrefabName(c.gameObject),
                        DisplayName = Localization.instance != null ? Localization.instance.Localize(c.m_name) : c.m_name,
                        Health = c.GetHealth(),
                        HealthMax = c.GetMaxHealth(),
                        Remaining = exp > 0L ? Mathf.Max(0f, (exp - now) / 1000f) : -1f,
                        Mode = mode,
                        SourceTag = zdo.GetString(SummonKeys.Source, ""),
                    });
                }
            }
            bool changed = ids.Count != s_lastIds.Count;
            foreach (var id in s_lastIds)
            {
                if (ids.Contains(id)) continue;
                changed = true;
                SummonApi.RaiseDespawned(id);
            }
            if (!changed) foreach (var id in ids) if (!s_lastIds.Contains(id)) { changed = true; break; }
            s_lastIds.Clear();
            foreach (var id in ids) s_lastIds.Add(id);
            if (changed) { Save(); SummonApi.RaiseChanged(); }
        }

        // ── save with the character ──────────────────────────────────────────────────────────────────────────────────
        // "v1;" then entries "prefab,level,scale,mode,remaining,tag,group,cap" joined by '|'. Remaining -1 = no timer.

        private static void Save()
        {
            if (s_holdSave) return;
            Player p = Player.m_localPlayer;
            if (p == null) return;
            if (Mine.Count == 0) { ClearSave(); return; }
            var sb = new StringBuilder(SaveVersion).Append(';');
            for (int i = 0; i < Mine.Count; i++)
            {
                SummonInfo s = Mine[i];
                GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(s.Id) : null;
                ZDO zdo = go != null && go.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid() ? nv.GetZDO() : null;
                if (zdo == null) continue;
                if (sb[sb.Length - 1] != ';') sb.Append('|');
                sb.Append(Clean(s.Prefab)).Append(',')
                  .Append(zdo.GetInt(SummonKeys.Level, 1)).Append(',')
                  .Append(zdo.GetFloat(SummonKeys.Scale, 1f).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append((int)s.Mode).Append(',')
                  .Append(s.Remaining.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(Clean(s.SourceTag)).Append(',')
                  .Append(Clean(zdo.GetString(SummonKeys.Group, ""))).Append(',')
                  .Append(LastCap);
            }
            p.m_customData[SaveKey] = sb.ToString();
        }

        private static void ClearSave()
        {
            Player p = Player.m_localPlayer;
            if (p != null) p.m_customData.Remove(SaveKey);
        }

        private static string ReadSave()
        {
            Player p = Player.m_localPlayer;
            return p != null && p.m_customData.TryGetValue(SaveKey, out string s) ? s : null;
        }

        private static string Clean(string s) => (s ?? "").Replace(",", "").Replace("|", "").Replace(";", "");

        private static void RespawnFrom(string saved)
        {
            if (string.IsNullOrEmpty(saved)) return;
            int semi = saved.IndexOf(';');
            if (semi < 0 || saved.Substring(0, semi) != SaveVersion) { FiresLogger.LogWarning(SummonKeys.Tag + "unknown save format, summons not restored"); return; }
            string[] entries = saved.Substring(semi + 1).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            int restored = 0;
            foreach (string entry in entries)
            {
                string[] f = entry.Split(',');
                if (f.Length < 8) continue;
                if (ZNetScene.instance.GetPrefab(f[0]) == null)
                {
                    FiresLogger.LogWarning(SummonKeys.Tag + "skipped a saved summon: '" + f[0] + "' no longer exists (a removed mod?)");
                    continue;
                }
                float remaining = ParseF(f[4], -1f);
                if (remaining == 0f) continue;
                var o = new SummonOptions
                {
                    Level = (int)ParseF(f[1], 1f),
                    Scale = ParseF(f[2], 1f),
                    Mode = (SummonMode)(int)ParseF(f[3], 0f),
                    SourceTag = f[5],
                    CapGroup = f[6],
                    Cap = (int)ParseF(f[7], 2f),
                };
                // The cap is enforced again by the server as each one registers.
                if (Spawn(f[0], o, remaining > 0f ? remaining : 0f, out string why) != ZDOID.None) restored++;
                else FiresLogger.LogWarning(SummonKeys.Tag + "couldn't restore '" + f[0] + "': " + why);
            }
            FiresLogger.LogDebug(SummonKeys.Tag + "restored " + restored + " of " + entries.Length + " saved summons");
        }

        private static float ParseF(string s, float def) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        // ── lifecycle hooks ──────────────────────────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_SummonRestore
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                // Crash safety: whatever of ours is still in the world is replaced by the saved list.
                s_respawnList = ReadSave();
                if (string.IsNullOrEmpty(s_respawnList)) return;
                s_holdSave = true;
                SendDismissAll();
                s_respawnAt = Time.time + RespawnDelay;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Player_TeleportTo_SummonsFollow
        {
            private static void Postfix(Player __instance, bool __result)
            {
                if (!__result || __instance != Player.m_localPlayer || Mine.Count == 0) return;
                DismissAll(save: true);
                s_teleportPending = true;
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        private static class Game_Logout_SummonsSave
        {
            private static void Prefix()
            {
                if (Player.m_localPlayer != null && Mine.Count > 0) DismissAll(save: true);
            }
        }

        [HarmonyPatch(typeof(Player), "OnDeath")]
        private static class Player_OnDeath_SummonsGo
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) DismissAll(save: false);
            }
        }

        // ── PvP under the owner's rules ──────────────────────────────────────────────────────────────────────────────
        // A summon's hit on a player counts as its owner's, so vanilla's PvP checks (both sides' PvP flags) decide it.
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_SummonAsOwner
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || !(__instance is Player) || hit.m_attacker.IsNone()) return;
                ZDO attacker = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(hit.m_attacker) : null;
                long owner = attacker != null ? attacker.GetLong(SummonKeys.Owner, 0L) : 0L;
                if (owner == 0L) return;
                Player ownerPlayer = FindPlayer(owner);
                if (ownerPlayer != null) hit.m_attacker = ownerPlayer.GetZDOID();
            }
        }

        // Tamed creatures never treat players as enemies; a summon does when both it's owner and that player have PvP on.
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) })]
        private static class BaseAI_IsEnemy_SummonPvp
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                if (__result || a == null || b == null) return;
                Player other = a as Player ?? b as Player;
                Character summon = other == a ? b : a;
                if (other == null || summon == null || summon is Player) return;
                ZNetView nv = summon.GetComponent<ZNetView>();
                long owner = nv != null && nv.IsValid() ? nv.GetZDO().GetLong(SummonKeys.Owner, 0L) : 0L;
                if (owner == 0L || other.GetPlayerID() == owner) return;
                Player ownerPlayer = FindPlayer(owner);
                if (ownerPlayer != null && ownerPlayer.IsPVPEnabled() && other.IsPVPEnabled()) __result = true;
            }
        }

        internal static Player FindPlayer(long playerId)
        {
            foreach (Player p in Player.GetAllPlayers()) if (p != null && p.GetPlayerID() == playerId) return p;
            return null;
        }
    }
}
