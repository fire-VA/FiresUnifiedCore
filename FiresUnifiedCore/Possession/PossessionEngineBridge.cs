using System;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// The one file that talks to the shared engine (Shared\FiresPossession, namespace FiresPossession, public in Core
    /// through FIRES_POSSESSION_PUBLIC). Without FIRES_POSSESSION_ENGINE defined (the sources not linked yet) every
    /// possession is refused with a clear reason, so the public API above compiles and is safe to call.
    /// </summary>
    internal static class PossessionEngineBridge
    {
#if FIRES_POSSESSION_ENGINE
        internal static bool Linked => true;
        private static bool s_hooked;
        private static int s_lastAttack = -2;

        private sealed class Host : FiresPossession.IPossessionHost
        {
            public void CameraOn(Character c) => PossessionCamera.On(c);
            public void CameraOff() => PossessionCamera.Off();
            // The engine reads "FlightPreset" each flight step (0 = Auto, resolved per creature), so admin possession honours
            // [Flight] Preset too.
            public float Config(string key, float def) => key == "FlightPreset" ? (float)(int)FiresCore.Flight.FlightKeys.Preset : def;
            public bool Config(string key, bool def) => def;
            public void Say(string msg)
            {
                if (Player.m_localPlayer != null && !string.IsNullOrEmpty(msg)) Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, msg);
            }
            public void Log(string line) => FiresLogger.LogDebug(PossessionRules.Tag + line);
        }

        private static readonly Host s_host = new Host();

        private static bool Install() => FiresPossession.PossessionEngine.Install(global::FiresCore.FiresUnifiedCore.PluginGUID + ".possession");

        /// <summary>
        /// The engine's patches go in at ZNet.Start on every client, not on the first possession: the first engine
        /// copy in the process hides OTHER players' Hidden (proxied) bodies, so a Core-only client must have it installed to
        /// see a possessor's body vanish. Idle cost: a static check per character update. Not on a dedicated
        /// server (no camera, no local player).
        /// </summary>
        internal static void InstallEarly()
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            bool ok = Install();
            FiresLogger.LogDebug(PossessionRules.Tag + (ok ? "engine installed" : "engine install failed (see the warning above)"));
        }

        /// <summary>Why a possession can't start right now on this client (null = free): another mod's engine copy holds it.</summary>
        internal static string Busy()
        {
            object holder = AppDomain.CurrentDomain.GetData(FiresPossession.PossessionEngine.TokenKey);
            if (holder != null && !FiresPossession.PossessionEngine.IsPossessing) return "another mod is possessing a creature";
            return null;
        }

        internal static bool Begin(PossessHandle h, out string why)
        {
            if (!s_hooked)
            {
                s_hooked = true;
                Install();   // normally done at ZNet.Start already (InstallEarly); idempotent
                FiresPossession.PossessionEngine.Ended += (creature, reason) => { BodyCarrier.Stop(reason); PossessionClient.OnEngineEnded(reason); };
            }
            PossessOptions o = h.Options;
            var e = new FiresPossession.PossessOptions
            {
                Body = o.Body == PossessBody.Hidden ? FiresPossession.BodyStyle.Hidden : FiresPossession.BodyStyle.Frozen,
                PlayerInvulnerable = o.PlayerInvulnerable,
                BodyUntouchable = o.BodyUntouchable,
                ChannelLock = o.ChannelLock && !o.CarryHiddenBody,   // A carried body is never pinned
                MaxDuration = o.MaxDuration,
                MaxRange = o.CarryHiddenBody ? 0f : o.MaxRange,       // The body travels with the creature, no leash
                ReturnToAnchor = o.ReturnToAnchor && !o.ConsumeTarget,
                ConsumeTarget = o.ConsumeTarget,
                ContinueWhile = o.ContinueWhile,
                ChannelHover = o.ChannelHover,
                HoverRise = o.HoverRise,
                HoverBob = o.HoverBob,
                HoverPeriod = o.HoverPeriod,
                HoverDescent = o.HoverDescent,
                ChannelFxStart = o.ChannelFxStart,
                ChannelFxStop = o.ChannelFxStop,
                ChannelPose = o.ChannelPose,
                SourceTag = o.SourceTag,
                Host = s_host,
            };
            s_lastAttack = -2;
            bool ok = FiresPossession.PossessionEngine.Begin(h.Player, h.Creature, e, out why);
            if (ok && o.CarryHiddenBody && !FiresPossession.PossessionEngine.BodyHidden) BodyCarrier.Start(h.Player, h.Creature, o.SourceTag, o.BodyVanishFx, o.CarryEndAtCreature, o.BodyRiseBeforeVanish, o.BodyRiseSeconds);
            return ok;
        }

        internal static void End(string reason)
        {
            if (FiresPossession.PossessionEngine.IsPossessing) FiresPossession.PossessionEngine.End(reason);
        }

        internal static void SyncAttacks(PossessHandle h)
        {
            var list = FiresPossession.PossessionEngine.Attacks;
            if (list == null) return;
            if (h.AttackList.Count != list.Count)
            {
                h.AttackList.Clear();
                h.MainIndex = -1;
                h.SpecialIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    string kind = a.Kind.ToString().ToLowerInvariant();
                    h.AttackList.Add(new PossessAttackInfo { Name = a.Name, Kind = kind, Ranged = a.Ranged });
                    if (kind == "main" && h.MainIndex < 0) h.MainIndex = i;
                    if (kind == "special" && h.SpecialIndex < 0) h.SpecialIndex = i;
                }
            }
            int cur = FiresPossession.PossessionEngine.CurrentAttack;
            h.CurrentIndex = cur;
            if (cur != s_lastAttack)
            {
                bool first = s_lastAttack == -2;
                s_lastAttack = cur;
                if (!first) PossessionApi.RaiseAttackChanged(h);
            }
        }
#else
        internal static bool Linked => false;

        internal static void InstallEarly() { }

        internal static string Busy() => null;

        internal static bool Begin(PossessHandle h, out string why)
        {
            why = "the possession engine is not linked in this Core build";
            return false;
        }

        internal static void End(string reason) { }

        internal static void SyncAttacks(PossessHandle h) { }
#endif
    }
}
