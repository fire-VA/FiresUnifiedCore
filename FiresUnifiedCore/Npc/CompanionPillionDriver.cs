using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using FiresCore.Mounts;
using FiresCore.Npc.Interactions;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Core brain for pillion riding. On the local player's client, every 0.5 s: when the player rides a
    /// Sadle mount with no pillion, asks the mount mod (PillionApi.TryBoard) to seat the nearest following companion
    /// within 12 m. Backstop: a pillion whose owner no longer rides its mount is asked to Leave, and Core unseats it
    /// itself 2 s later if the provider did not. Ticked from CompanionController.Update.
    /// </summary>
    internal static class CompanionPillionDriver
    {
        private const float TickInterval = 0.5f;
        private const float BoardRadius = 12f;
        private const float RetrySameReason = 5f;
        private const float LeaveGrace = 2f;
        private const float SetDownSide = 1.5f;

        private static float _nextTick;
        // Auto-boarding sits behind a synced toggle (default ON). Backstop still runs when off.
        private static ConfigEntry<bool> s_autoBoard;
        internal static bool AutoBoard => s_autoBoard == null || s_autoBoard.Value;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_autoBoard = config.Bind("Companions", "Pillion auto-board", true,
                "SERVER. When the owner rides a saddled mount, the nearest following companion within 12 m climbs on behind them.");
            configSync?.AddConfigEntry(s_autoBoard);
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Companions, true, s_autoBoard);   // Companions or RPGmaker
            FiresLogger.LogDebug($"[Pillion] auto-board {(s_autoBoard.Value ? "on" : "off")}");
            s_autoBoard.SettingChanged += (_, __) => FiresLogger.LogDebug($"[Pillion] auto-board {(s_autoBoard.Value ? "on" : "off")} (changed)");
        }
        private static string _lastFailReason;
        private static float _lastFailTime = -999f;
        // Companion -> time Leave was requested by the backstop.
        private static readonly Dictionary<Character, float> _leaveRequested = new Dictionary<Character, float>();
        // Seated companion -> time its body was first seen not attached while the owner still rides.
        private static readonly Dictionary<Character, float> _unattachedSince = new Dictionary<Character, float>();

        internal static void Tick()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            try
            {
                Backstop(player);
                if (AutoBoard) TryBoardNearest(player);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[Pillion] driver tick threw: {ex.Message}");
            }
        }

        private static Character RiddenMount(Player player)
        {
            if (player == null || player.IsDead()) return null;
            return player.GetDoodadController() is Sadle s ? s.GetCharacter() : null;
        }

        private static void TryBoardNearest(Player player)
        {
            Character mount = RiddenMount(player);
            if (mount == null) return;
            if (PillionApi.Seated(mount) != null || PillionApi.HasPillionOn(mount)) return;
            if (_lastFailReason != null && Time.time - _lastFailTime < RetrySameReason) return;

            CompanionController best = null;
            float bestDist = BoardRadius;
            foreach (var cc in CompanionController.AllCompanions)
            {
                if (cc == null || !cc.IsOwner(player) || !cc.ShouldBeFollowing) continue;
                Character body = cc.GetCharacter();
                if (body == null || body.IsDead() || PillionApi.IsPillion(body)) continue;
                var interaction = cc.GetComponent<CompanionInteractionBehavior>();
                if (interaction != null && interaction.IsAttached) continue;
                if (cc.GetComponent<CompanionHaulRunner>() != null) continue;
                float d = Vector3.Distance(body.transform.position, player.transform.position);
                if (d < bestDist) { bestDist = d; best = cc; }
            }
            if (best == null) return;

            Character companion = best.GetCharacter();
            if (PillionApi.TryBoard(companion, mount, out string reason))
            {
                _lastFailReason = null;
                FiresLogger.LogDebug($"[Pillion] board requested for {best.companionName} on {mount.name}");
                return;
            }

            reason = string.IsNullOrEmpty(reason) ? "unknown" : reason;
            // One line per distinct reason; after a failure the next attempt waits 5 s (gate above).
            bool repeat = reason == _lastFailReason;
            _lastFailReason = reason;
            _lastFailTime = Time.time;
            if (!repeat) FiresLogger.LogDebug($"[Pillion] board refused: {reason}");
        }

        private static void Backstop(Player player)
        {
            foreach (Character companion in PillionApi.SeatedCompanions())
            {
                if (!CompanionController.TryGet(companion, out var cc) || cc == null || !cc.IsOwner(player)) continue;

                Character mount = PillionApi.MountOf(companion);
                var interaction = cc.GetComponent<CompanionInteractionBehavior>();
                bool ownerRiding = mount != null && RiddenMount(player) == mount;
                bool bodyAttached = interaction != null && interaction.IsAttached;

                if (ownerRiding && bodyAttached)
                {
                    _leaveRequested.Remove(companion);
                    _unattachedSince.Remove(companion);
                    continue;
                }
                if (ownerRiding)
                {
                    // The attach coroutine runs a frame or two after Seat; only a body detached for 2 s counts as lost
                    // (e.g. an absolute-priority player command stood it up).
                    if (!_unattachedSince.TryGetValue(companion, out float t0)) { _unattachedSince[companion] = Time.time; continue; }
                    if (Time.time - t0 < LeaveGrace) continue;
                }

                if (!_leaveRequested.TryGetValue(companion, out float since))
                {
                    _leaveRequested[companion] = Time.time;
                    PillionApi.Leave(companion, ownerRiding ? "body detached" : "owner not riding");
                    continue;
                }
                if (Time.time - since < LeaveGrace) continue;

                _leaveRequested.Remove(companion);
                _unattachedSince.Remove(companion);
                PillionApi.Unseat(companion, SetDownPoint(player));
            }

            // Forget companions that are no longer pillions.
            Forget(_leaveRequested);
            Forget(_unattachedSince);
        }

        private static void Forget(Dictionary<Character, float> map)
        {
            if (map.Count == 0) return;
            List<Character> gone = null;
            foreach (var c in map.Keys)
                if (c == null || !PillionApi.IsPillion(c)) (gone ?? (gone = new List<Character>())).Add(c);
            if (gone != null) foreach (var c in gone) map.Remove(c);
        }

        private static Vector3 SetDownPoint(Player player)
        {
            Vector3 p = player.transform.position + player.transform.right * SetDownSide;
            if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f,
                    LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.1f;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(p, out float h))
                p.y = h + 0.1f;
            return p;
        }
    }
}
