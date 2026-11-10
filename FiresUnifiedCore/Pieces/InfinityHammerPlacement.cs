using System;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Pieces
{
    /// <summary>
    /// Infinity Hammer's own after-placement step for objects a Fires mod places itself (FAP pieces, FDM generator placements), so
    /// they come out as IH's placements do. (Fire 10-26: "fap placed pieces (dungeon generator placed pieces as well) should respect
    /// our infinity hammer settings so if we're set for infinite health it is placed with it".)
    ///
    /// On a player's game: IH's public static InfinityHammer.BaseSelection.PostProcessPlaced(GameObject), the method IH runs on its
    /// own placements: health override (infinite / "Overwrite health"), "No creator", "No target", "No physics", "No remove", scale
    /// sync. On a dedicated server (FDM generates zones there; IH is installed on it): only IH's health step,
    /// InfinityHammer.CustomHealth.SetHealth(ZNetView, false), with the SERVER's own IH config. PostProcessPlaced is not called
    /// there because its creator branch reads the local player's profile and platform user, which a headless server lacks.
    /// Both found by name once (IH source: GitHub JereKuusela/valheim-infinity_hammer); Core doesn't reference InfinityHammer.dll.
    /// IH absent or renamed = nothing happens. Nothing here is reachable from another peer (no RPC).
    ///
    /// Add-only API: Apply(GameObject) and IsAvailable.
    /// </summary>
    public static class InfinityHammerPlacement
    {
        private static bool s_looked;
        private static Action<GameObject> s_post;
        private static Func<ZNetView, bool, float> s_health;
        private static bool s_serverLogged;
        private static bool s_failLogged;

        /// <summary>True when Infinity Hammer is loaded and its placement step was found.</summary>
        public static bool IsAvailable { get { Look(); return s_post != null || s_health != null; } }

        /// <summary>
        /// Applies IH's placement settings to a just-placed object. Call it right after the object exists, where its ZNetView is
        /// valid and owned. With a local player: IH's full after-placement step. On a dedicated server: IH's health override only.
        /// Returns true when IH ran. IH's full step also starts a DungeonGenerator that has no rooms yet, so a mod that generates
        /// its own rooms calls this after generating (or skips the generator object).
        /// </summary>
        public static bool Apply(GameObject placed)
        {
            if (placed == null) return false;
            var view = placed.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            Look();
            try
            {
                if (Player.m_localPlayer != null)
                {
                    if (s_post == null) return false;
                    s_post(placed);
                    return true;
                }
                if (ZNet.instance == null || !ZNet.instance.IsDedicated() || s_health == null) return false;
                s_health(view, false);
                if (!s_serverLogged)
                {
                    s_serverLogged = true;
                    FiresCore.Logging.FiresLogger.LogInfo($"[IHPlacement] dedicated server: Infinity Hammer's health setting applied to server-placed '{placed.name}' (first of this session; the server's own IH config).");
                }
                return true;
            }
            catch (Exception ex)
            {
                if (!s_failLogged)
                {
                    s_failLogged = true;
                    FiresCore.Logging.FiresLogger.LogWarning($"[IHPlacement] Infinity Hammer's placement step threw on '{placed.name}' ({ex.GetType().Name}: {ex.Message}); placed as is (logged once).");
                }
                return false;
            }
        }

        private static void Look()
        {
            if (s_looked) return;
            s_looked = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var sel = asm.GetType("InfinityHammer.BaseSelection", false);
                    if (sel == null) continue;
                    const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                    var post = sel.GetMethod("PostProcessPlaced", S, null, new[] { typeof(GameObject) }, null);
                    if (post != null) s_post = (Action<GameObject>)Delegate.CreateDelegate(typeof(Action<GameObject>), post);
                    var health = asm.GetType("InfinityHammer.CustomHealth", false)?.GetMethod("SetHealth", S, null, new[] { typeof(ZNetView), typeof(bool) }, null);
                    if (health != null && health.ReturnType == typeof(float))
                        s_health = (Func<ZNetView, bool, float>)Delegate.CreateDelegate(typeof(Func<ZNetView, bool, float>), health);
                    FiresCore.Logging.FiresLogger.LogInfo($"[IHPlacement] Infinity Hammer found: placement step {(s_post != null ? "bound" : "MISSING")}, health step {(s_health != null ? "bound" : "MISSING")}.");
                    break;
                }
            }
            catch (Exception) { s_post = null; s_health = null; }
        }
    }
}
