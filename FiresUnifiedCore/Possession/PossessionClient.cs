using System;
using FiresCore.Input;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// The local side: the request/grant handshake, the admin Possess key, the form health pool, damage scaling, and the
    /// release on portal use and logout. The engine (camera, drive, attacks, body styles, hover) is PossessionEngineBridge.
    /// </summary>
    internal static class PossessionClient
    {
        private const float GrantTimeout = 4f;

        private static PossessHandle s_pending;
        private static ZDOID s_pendingId;
        private static float s_pendingSince;
        private static bool s_granted;
        private static ZDOID s_currentId;
        // When the current possession began, on KeyTaps' clock (Time.unscaledTime). A G tap releases only if it was
        // pressed after this, so the press that started it (or one made during the grant handshake) can't also end it.
        private static float s_beganUnscaled;

        internal static bool Busy => s_pending != null || (PossessionApi.Current != null && !PossessionApi.Current.Ended);

        internal static PossessHandle Request(Character target, PossessOptions o)
        {
            var h = new PossessHandle { Creature = target, Player = Player.m_localPlayer, Options = o };
            if (!PossessionRules.CanPossessLocal(Player.m_localPlayer, target, o, out string why))
            {
                Refuse(h, why);
                return h;
            }
            if (!PossessionEngineBridge.Linked)
            {
                Refuse(h, "the possession engine is not linked in this Core build");
                return h;
            }
            // Refused here, before any grant: a grant + release round trip would clear the key another mod set.
            string busy = PossessionEngineBridge.Busy();
            if (busy != null)
            {
                Refuse(h, busy);
                return h;
            }
            ZNetView nv = target.GetComponent<ZNetView>();
            s_pending = h;
            s_pendingId = nv.GetZDO().m_uid;
            s_pendingSince = Time.time;
            s_granted = false;
            float range = o.SourceTag == PossessionRules.AdminTag ? PossessionRules.AdminMaxRange.Value : Mathf.Max(2f, o.MaxRange);
            FiresLogger.LogDebug(PossessionRules.Tag + "requesting " + target.m_name + " " + s_pendingId + " (" + o.SourceTag + ")");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), PossessionServer.RequestRpc, s_pendingId, o.SourceTag ?? "", range);
            return h;
        }

        private static void Refuse(PossessHandle h, string why)
        {
            h.Ended = true;
            FiresLogger.LogDebug(PossessionRules.Tag + "refused: " + why);
            if (Player.m_localPlayer != null && h.Options != null && h.Options.SourceTag == PossessionRules.AdminTag)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Can't possess: " + why);
            PossessionApi.RaiseEnded(h, PossessEndReason.Revoked);
        }

        internal static void RegisterRpcs()
        {
            ZRoutedRpc.instance.Register<ZDOID, bool, string>(PossessionServer.ReplyRpc, RPC_Reply);
            ZRoutedRpc.instance.Register<ZDOID, string>(PossessionServer.RevokeRpc, RPC_Revoke);
        }

        private static void RPC_Reply(long sender, ZDOID id, bool ok, string why)
        {
            if (s_pending == null || id != s_pendingId) return;
            if (!ok)
            {
                var h = s_pending;
                s_pending = null;
                Refuse(h, why);
                return;
            }
            s_granted = true;   // Begin runs in Update once the ownership has arrived
        }

        private static void RPC_Revoke(long sender, ZDOID id, string why)
        {
            var h = PossessionApi.Current;
            if (h != null && !h.Ended && id == s_currentId) EndLocal(h, PossessEndReason.Revoked, notifyServer: false);
        }

        /// <summary>Called every frame by the runner.</summary>
        internal static void Update()
        {
            Player local = Player.m_localPlayer;
            // The possess key's single tap, read every frame before any early return (KeyTaps, G shared with the guild screen).
            bool possessTap = PossessionRules.PossessTapped();
            if (s_pending != null) UpdatePending();
            RavenPossession.Tick();

            var cur = PossessionApi.Current;
            if (cur != null && !cur.Ended)
            {
                if (cur.Creature == null) { EndLocal(cur, PossessEndReason.Gone, true, "the creature is gone"); return; }
                if (PossessionRules.Pressed(PossessionRules.ReleaseKey)) { EndLocal(cur, PossessEndReason.Released, true, "the release key " + PossessionRules.ReleaseKey.Value); return; }
                if (cur.SourceTag == PossessionRules.AdminTag && possessTap)
                {
                    float pressed = KeyTaps.SinglePressTime(PossessionRules.PossessKey.Value);
                    if (pressed > s_beganUnscaled) { EndLocal(cur, PossessEndReason.Released, true, $"a {PossessionRules.PossessKey.Value} tap {pressed - s_beganUnscaled:0.0} s after the start"); return; }
                    FiresLogger.LogDebug(PossessionRules.Tag + $"{PossessionRules.PossessKey.Value} tap ignored: pressed {s_beganUnscaled - pressed:0.0} s before the possession began, not a release");
                }
                PossessionEngineBridge.SyncAttacks(cur);
                return;
            }

            if (local == null || s_pending != null) return;
            // PossessTapped is false unless admin possession is on AND this player is an admin (then single G stays the guild key).
            if (possessTap)
            {
                Character target = PossessionRules.PickUnderCrosshair(PossessionRules.AdminMaxRange.Value, out string pickWhy);
                FiresLogger.LogDebug(PossessionRules.Tag + "admin pick: " + (target != null ? target.m_name : "nothing") + " (" + pickWhy + ")");
                // No creature -> a vanilla tutorial raven, possessed through a RavenPrefabs flyer body.
                if (target == null && RavenPossession.Allowed)
                {
                    Raven raven = RavenPossession.PickRaven(PossessionRules.AdminMaxRange.Value, out string ravenWhy);
                    FiresLogger.LogDebug(PossessionRules.Tag + "raven pick: " + (raven != null ? raven.gameObject.name : "nothing") + " (" + ravenWhy + ")");
                    if (raven != null) { RavenPossession.TryPossess(raven, AdminOptions()); return; }
                }
                Request(target, AdminOptions());
            }
        }

        private static PossessOptions AdminOptions() => new PossessOptions
        {
            Body = PossessBody.Frozen,
            PlayerInvulnerable = true,
            BodyUntouchable = true,
            ChannelLock = true,
            MaxRange = 0f,
            SourceTag = PossessionRules.AdminTag,
        };

        private static void UpdatePending()
        {
            var h = s_pending;
            if (h.Creature == null) { s_pending = null; SendRelease(s_pendingId); Refuse(h, "the creature is gone"); return; }
            if (Time.time - s_pendingSince > GrantTimeout)
            {
                s_pending = null;
                SendRelease(s_pendingId);
                Refuse(h, s_granted ? "ownership never arrived" : "the server didn't answer");
                return;
            }
            if (!s_granted) return;
            ZNetView nv = h.Creature.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;

            s_pending = null;
            if (!PossessionEngineBridge.Begin(h, out string why))
            {
                SendRelease(s_pendingId);
                Refuse(h, why);
                return;
            }
            h.StartTime = Time.time;
            s_beganUnscaled = Time.unscaledTime;
            h.FormHealthMax = h.Options.FormHealth > 0f ? h.Options.FormHealth : 0f;
            h.FormHealth = h.FormHealthMax;
            s_currentId = s_pendingId;
            PossessionApi.Current = h;
            PossessionEngineBridge.SyncAttacks(h);
            FiresLogger.LogDebug(PossessionRules.Tag + "began " + h.Creature.m_name + " (" + h.SourceTag + ", body " + h.Options.Body + ", form hp " + h.FormHealthMax + ")");
            PossessionApi.RaiseStarted(h);
        }

        internal static void EndLocal(PossessHandle h, PossessEndReason reason) => EndLocal(h, reason, notifyServer: true);

        internal static void EndLocal(PossessHandle h, PossessEndReason reason, bool notifyServer) => EndLocal(h, reason, notifyServer, null);

        /// <summary><paramref name="cause"/> names what ended it in the log line ("Released" alone didn't say G tap or Escape).</summary>
        internal static void EndLocal(PossessHandle h, PossessEndReason reason, bool notifyServer, string cause)
        {
            if (h == null || h.Ended) return;
            if (h == s_pending)
            {
                s_pending = null;
                SendRelease(s_pendingId);
                h.Ended = true;
                PossessionApi.RaiseEnded(h, reason);
                return;
            }
            h.Ended = true;
            PossessionEngineBridge.End(ReasonString(reason));
            if (notifyServer) SendRelease(s_currentId);
            if (PossessionApi.Current == h) PossessionApi.Current = null;
            FiresLogger.LogDebug(PossessionRules.Tag + "ended " + (h.Creature != null ? h.Creature.m_name : "?") + ": " + reason + (string.IsNullOrEmpty(cause) ? "" : " (" + cause + ")") + " after " + h.Elapsed.ToString("0.0") + " s");
            PossessionApi.RaiseEnded(h, reason);
        }

        /// <summary>The engine ended it on its own (death, range, duration, ContinueWhile, creature gone).</summary>
        internal static void OnEngineEnded(string reason)
        {
            var h = PossessionApi.Current;
            if (h == null || h.Ended) return;
            h.Ended = true;
            SendRelease(s_currentId);
            PossessionApi.Current = null;
            var r = ReasonFrom(reason);
            FiresLogger.LogDebug(PossessionRules.Tag + "engine ended " + (h.Creature != null ? h.Creature.m_name : "?") + ": " + reason);
            PossessionApi.RaiseEnded(h, r);
        }

        private static void SendRelease(ZDOID id)
        {
            if (id.IsNone() || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), PossessionServer.ReleaseRpc, id);
        }

        internal static string ReasonString(PossessEndReason r) => r.ToString().ToLowerInvariant();

        internal static PossessEndReason ReasonFrom(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "death": return PossessEndReason.Death;
                case "range": return PossessEndReason.Range;
                case "duration": return PossessEndReason.Duration;
                case "logout": return PossessEndReason.Logout;
                case "gone": return PossessEndReason.Gone;
                case "released": return PossessEndReason.Released;
                case "revoked": return PossessEndReason.Revoked;
                case "portal": return PossessEndReason.Portal;
                default: return PossessEndReason.Error;
            }
        }

        // ── form health pool (Druid) ───────────────────────────────────────────────────────────────────────────────────
        // The possessor owns the creature, so its RPC_Damage -> ApplyDamage runs here. With a pool the hit drains the pool
        // instead of the creature; past zero the overflow is dealt to the player and the form ends (death).
        [HarmonyPatch(typeof(Character), "ApplyDamage")]
        private static class Character_ApplyDamage_FormPool
        {
            private static bool Prefix(Character __instance, HitData hit)
            {
                var h = PossessionApi.Current;
                if (h == null || h.Ended || h.FormHealthMax <= 0f || __instance != h.Creature || hit == null) return true;
                float dmg = hit.GetTotalDamage();
                float poolBefore = h.FormHealth;
                h.FormHealth -= dmg;
                // The raw hit on the form, who dealt it and the pool before it.
                FiresLogger.LogDebug(PossessionRules.Tag + $"form hit {dmg:0.0} (blunt {hit.m_damage.m_blunt:0.0} slash {hit.m_damage.m_slash:0.0} pierce {hit.m_damage.m_pierce:0.0} "
                    + $"plain {hit.m_damage.m_damage:0.0} elemental {hit.m_damage.m_fire + hit.m_damage.m_frost + hit.m_damage.m_lightning + hit.m_damage.m_poison + hit.m_damage.m_spirit:0.0}) "
                    + $"by {AttackerName(hit)}; pool {poolBefore:0.0} -> {Mathf.Max(0f, h.FormHealth):0.0} of {h.FormHealthMax:0.0}");
                if (h.FormHealth > 0f) return false;
                float overflow = -h.FormHealth;
                h.FormHealth = 0f;
                Player p = h.Player;
                EndLocal(h, PossessEndReason.Death);
                // The leftover reaches the player as a normal hit of the same damage types, so armour
                // and resistances apply, and at most the form's max health. Before, the whole overkill went through as true damage (one-shots).
                float carried = Mathf.Min(overflow, h.FormHealthMax);
                FiresLogger.LogDebug(PossessionRules.Tag + $"form pool empty; overflow {overflow:0.0}, {carried:0.0} carried to the player as a normal hit (cap {h.FormHealthMax:0.0}, armour applies)");
                if (p != null && carried > 0f && dmg > 0f)
                {
                    HitData spill = hit.Clone();
                    spill.m_damage.Modify(carried / dmg);
                    spill.m_point = p.transform.position;
                    spill.m_dir = (p.transform.position - __instance.transform.position).normalized;
                    spill.m_pushForce = 0f;
                    p.Damage(spill);
                }
                return false;
            }
        }

        private static string AttackerName(HitData hit)
        {
            Character who = hit.GetAttacker();
            if (who == null) return hit.m_attacker.IsNone() ? "no attacker" : "an unloaded attacker";
            return (who is Player pl ? pl.GetPlayerName() : who.m_name) + $" ({who.GetLevel() - 1}*{(who.IsTamed() ? ", tamed" : "")})";
        }

        // ── damage scaling ─────────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_PossessScale
        {
            private static void Prefix(HitData hit)
            {
                var h = PossessionApi.Current;
                if (h == null || h.Ended || hit == null || h.Options == null || Mathf.Approximately(h.Options.DamageScale, 1f)) return;
                ZDOID creature = h.Creature != null && h.Creature.GetZDOID() != ZDOID.None ? h.Creature.GetZDOID() : ZDOID.None;
                ZDOID player = h.Player != null ? h.Player.GetZDOID() : ZDOID.None;
                if (hit.m_attacker.IsNone() || (hit.m_attacker != creature && hit.m_attacker != player)) return;
                hit.m_damage.Modify(Mathf.Max(0f, h.Options.DamageScale));
            }
        }

        // ── release on portal use and logout (section 6 item 7) ───────────────────────────────────────────────────────
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Player_TeleportTo_PossessRelease
        {
            private static void Prefix(Player __instance)
            {
                var h = PossessionApi.Current;
                if (h != null && !h.Ended && __instance == Player.m_localPlayer) EndLocal(h, PossessEndReason.Portal);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Logout))]
        private static class Game_Logout_PossessRelease
        {
            private static void Prefix()
            {
                var h = PossessionApi.Current;
                if (h != null && !h.Ended) EndLocal(h, PossessEndReason.Logout);
                else if (s_pending != null) EndLocal(s_pending, PossessEndReason.Logout);
            }
        }
    }
}
