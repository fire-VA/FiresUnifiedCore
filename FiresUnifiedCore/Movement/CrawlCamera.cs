using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Movement
{
    /// <summary>
    /// First person while crawling (Core 0.2.303, Fire 10-06: "When crawling in caves the camera should be put into first person
    /// mode and moved down to the height of the head to prevent the clipping issues we see"). Local player only.
    /// While Core holds the player in a crawl (Crawl.Active) or a low-ceiling crouch (Headroom.Holding), the camera eases from
    /// wherever vanilla (and Headroom's guard) put it to the head (Character.GetHeadPoint, the head bone), and the body's meshes cast shadows only,
    /// so the view isn't filled with the inside of the head. Standing up eases it back to the player's own mode and distance.
    /// The camera's rotation is vanilla's (the look direction) throughout; only its position moves.
    /// Stands down (eases back) while a FiresValcast camera mode or possession is active, and while First Person POV
    /// (jpvalheim.firstpersonpov) is itself in first person (its static _firstPersonFrame, read by reflection): the player is
    /// already at the head, and that mod owns the body and head hiding. In its third person ours applies as normal. It has no
    /// clean toggle to drive (its first person is the camera zoomed in to its distance epsilon), so ours is used instead.
    /// Debug lines: "[CrawlCamera] first person (crawl)" / "(low ceiling)" and "[CrawlCamera] back to the player's camera (…)".
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Movement, FiresCore.Config.CoreFeatures.Crawling)]
    public static class CrawlCamera
    {
        public const string FirstPersonPovGuid = "jpvalheim.firstpersonpov";
        /// <summary>Seconds for the camera to ease in or out.</summary>
        private const float BlendSeconds = 0.35f;
        /// <summary>The eye sits this far in front of the head bone along the look direction (m), past the face.</summary>
        private const float EyeForward = 0.12f;
        /// <summary>Above this blend the body is drawn as shadow only.</summary>
        private const float HideBodyAbove = 0.6f;

        private static ConfigEntry<bool> s_enabled;
        private static float s_blend;
        private static bool s_want;
        private static string s_reason;
        private static int s_lastFrame = -1;
        private static bool? s_povInstalled;
        private static readonly List<(Renderer renderer, ShadowCastingMode mode)> s_hidden = new List<(Renderer, ShadowCastingMode)>();

        public static bool Enabled => s_enabled == null || s_enabled.Value;

        // 0.2.308 ([ghost] for Fire's FirstPerson territory flag): other mods ask for first person by owner, local player only.
        // A request is FULLY forced: it ignores "First person when crawling", and scroll-zoom can't change the third-person distance
        // while it holds. Valcast camera modes / possession still win (first person resumes when they end); First Person POV in its
        // own first person also stands this down, since the player already looks from the head.
        private static readonly Dictionary<string, string> s_requests = new Dictionary<string, string>();
        private static readonly AccessTools.FieldRef<GameCamera, float> CameraDistance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
        private static float s_pinnedDistance = -1f;

        /// <summary>Ask for (on) or release (off) first person for the local player. <paramref name="owner"/> names the asker, e.g. "FiresRPGmaker.Territory"; <paramref name="reason"/> goes to the Debug line.</summary>
        public static void Request(string owner, bool on, string reason = null)
        {
            if (string.IsNullOrEmpty(owner)) return;
            bool had = s_requests.ContainsKey(owner);
            if (on) s_requests[owner] = reason ?? owner;
            else s_requests.Remove(owner);
            if (had != on || (on && had))
                FiresCore.Logging.FiresLogger.LogDebug(on ? $"[CrawlCamera] first person requested by {owner} ({reason ?? "no reason given"})" : $"[CrawlCamera] {owner} released its first-person request");
        }

        /// <summary>True while <paramref name="owner"/> holds a first-person request.</summary>
        public static bool IsRequested(string owner) => !string.IsNullOrEmpty(owner) && s_requests.ContainsKey(owner);

        private static string FirstRequest()
        {
            foreach (var kv in s_requests) return $"{kv.Value}, {kv.Key}";
            return null;
        }

        /// <summary>True while the camera is (partly) at the head.</summary>
        public static bool Active => s_blend > 0f;

        internal static void Bind(ConfigFile config)
        {
            s_enabled = config.Bind("Movement", "First person when crawling", true,
                "While a low ceiling holds the player in a crawl or crouch, the camera moves to the head (first person) so it can't clip through the rock, and eases back on standing up. Off: the third-person camera stays (pulled in by 'Low ceilings').");
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Movement, false, s_enabled);   // 0.2.387: FAT only
        }

        private static FieldInfo s_povFirstPerson;

        // True while First Person POV is in its first person this frame.
        private static bool PovFirstPerson()
        {
            if (s_povInstalled == null)
            {
                s_povInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(FirstPersonPovGuid, out var info) && info.Instance != null;
                if (s_povInstalled.Value)
                {
                    s_povFirstPerson = AccessTools.Field(info.Instance.GetType(), "_firstPersonFrame");
                    if (s_povFirstPerson == null || !s_povFirstPerson.IsStatic || s_povFirstPerson.FieldType != typeof(bool)) s_povFirstPerson = null;
                    FiresCore.Logging.FiresLogger.LogDebug(s_povFirstPerson != null
                        ? "[CrawlCamera] First Person POV found: standing down while it is in first person"
                        : "[CrawlCamera] First Person POV found but its first-person state is unreadable: crawl camera stays on");
                }
            }
            if (s_povFirstPerson == null) return false;
            try { return (bool)s_povFirstPerson.GetValue(null); }
            catch { return false; }
        }

        // ── FiresValcast's camera modes (by reflection: no reference either way) ──────────────────────────────────────────────
        private static bool s_valcastLooked;
        private static readonly List<PropertyInfo> s_valcastFlags = new List<PropertyInfo>();

        private static string ValcastOwner()
        {
            if (!s_valcastLooked)
            {
                s_valcastLooked = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "FiresValcast") continue;
                    foreach (var (type, flag) in new[] { ("FreeCam", "IsActive"), ("CreaturePossession", "IsActive"), ("CameraPresets", "IsActive"),
                                                         ("CameraAutoMotion", "IsActive"), ("CameraPath", "IsPlaying") })
                    {
                        var p = assembly.GetType("VerdantsAscent.Modules." + type, false)?.GetProperty(flag, BindingFlags.Public | BindingFlags.Static);
                        if (p != null && p.PropertyType == typeof(bool)) s_valcastFlags.Add(p);
                    }
                    break;
                }
            }
            foreach (var p in s_valcastFlags)
            {
                try { if ((bool)p.GetValue(null)) return $"FiresValcast {p.DeclaringType.Name}"; }
                catch { }
            }
            return null;
        }

        private static void Step(Player player)
        {
            if (Time.frameCount == s_lastFrame) return;
            s_lastFrame = Time.frameCount;

            bool requested = s_requests.Count > 0;
            string why = !Enabled && !requested ? "the setting is off"
                : player.IsDead() ? "dead"
                : PovFirstPerson() ? "First Person POV is in first person"
                : ValcastOwner();
            bool held = Crawl.Active || Headroom.Holding || requested;
            bool want = why == null && held;
            if (want != s_want)
            {
                s_want = want;
                string reason = want ? (Crawl.Active ? "crawl" : Headroom.Holding ? "low ceiling" : FirstRequest()) : (why ?? "standing");
                FiresCore.Logging.FiresLogger.LogDebug(want ? $"[CrawlCamera] first person ({reason})" : $"[CrawlCamera] back to the player's camera ({reason})");
                s_reason = reason;
            }
            s_blend = Mathf.MoveTowards(s_blend, want ? 1f : 0f, Time.unscaledDeltaTime / BlendSeconds);
            ShowBody(player, s_blend < HideBodyAbove);
        }

        private static void ShowBody(Player player, bool show)
        {
            if (show)
            {
                if (s_hidden.Count == 0) return;
                foreach (var (renderer, mode) in s_hidden) if (renderer != null) renderer.shadowCastingMode = mode;
                s_hidden.Clear();
                return;
            }
            if (s_hidden.Count > 0) return;
            foreach (var renderer in player.GetComponentsInChildren<Renderer>())
            {
                if (renderer is ParticleSystemRenderer || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                s_hidden.Add((renderer, renderer.shadowCastingMode));
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }

        // After vanilla's solve and Headroom's guard: ease the camera to the head.
        [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        private static class GameCamera_GetCameraPosition_CrawlCamera
        {
            [HarmonyPriority(Priority.VeryLow)]
            private static void Postfix(ref Vector3 pos)
            {
                try
                {
                    Player player = Player.m_localPlayer;
                    if (player == null || player.InIntro()) return;
                    Step(player);
                    if (s_blend <= 0f) return;
                    if (player.m_eye == null) return;
                    Headroom.LogPullIn("CrawlCamera first person (own feature, not under the toggle)");
                    Vector3 look = player.GetLookDir();
                    Vector3 eye = player.GetHeadPoint() + look * EyeForward;
                    float t = s_blend * s_blend * (3f - 2f * s_blend);   // smoothstep: no snap at either end
                    pos = Vector3.Lerp(pos, eye, t);
                }
                catch (Exception ex) { Debug.LogWarning($"[CrawlCamera] {ex.Message}"); }
            }
        }

        // While a request forces first person, vanilla's scroll-zoom must not move the third-person distance: it is pinned at the value
        // it had when the force began, so leaving returns the player's own distance.
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static class GameCamera_UpdateCamera_CrawlCamera
        {
            private static void Prefix(GameCamera __instance)
            {
                bool forced = s_want && s_requests.Count > 0;
                if (!forced) { s_pinnedDistance = -1f; return; }
                if (s_pinnedDistance < 0f) s_pinnedDistance = CameraDistance(__instance);
            }

            private static void Postfix(GameCamera __instance)
            {
                if (s_pinnedDistance >= 0f) CameraDistance(__instance) = s_pinnedDistance;
            }
        }

        // A body left hidden would stay hidden: show it again when the player goes.
        [HarmonyPatch(typeof(Player), "OnDestroy")]
        private static class Player_OnDestroy_CrawlCamera
        {
            private static void Prefix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                s_hidden.Clear();
                s_blend = 0f;
                s_want = false;
            }
        }
    }
}
