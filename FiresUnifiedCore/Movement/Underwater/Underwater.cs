using System;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// The underwater view, part of Core's Swimming feature (Fire 11-08, via [visual] / [lead]: "the bubbles and underwater env should move
    /// into advanced player movement with the rest of the swimming code", "if apm owns swimming then it needs to own the shaders and all
    /// the underwater logic", "without swimming nothing underwater would render anyways"). The crawl pattern: the code lives here, APM binds
    /// the keys (UnderwaterSettings), Core reads them.
    ///
    /// Live = a client with a graphics device AND CoreFeatures.Swimming live (Galaxies or APM installed) AND no older FiresTossinShade that
    /// still draws its own underwater (its VerdantsAscent.Water.Core.FiresUnderwaterEnv type present). So Core + APM ship first and change
    /// nothing; the FTS build that removes its copy is the switch-over, by itself.
    /// While live: one hidden root with the parts (env driver, bubbles, the fog pass, the caustics pass), the murk tick and the shared
    /// globals every frame. On stop the parts go and the globals are written 0 ONCE (FTS's surface water then reads "above water").
    /// Info (once each): "[Underwater] live (Swimming owners: ...)", "[Underwater] stood down: ...", "[Underwater] off: ...".
    /// </summary>
    [DefaultExecutionOrder(-100)]   // before the parts' LateUpdates: the murk and the shared globals are set first each frame
    public sealed class Underwater : MonoBehaviour
    {
        private static Underwater s_instance;
        private static GameObject s_parts;
        private static FiresCausticsPass s_caustics;
        private static bool s_oldFtsLooked, s_oldFts;
        private static string s_state;   // the last Info state said
        private float _checkAt;

        /// <summary>True while Core draws the underwater view (the parts gate on it).</summary>
        public static bool Live { get; private set; }

        private static readonly int P_PlayerSubmerged = Shader.PropertyToID("_FiresPlayerSubmerged"),
            P_Dapple = Shader.PropertyToID("_FiresUnderwaterDapple"),
            P_UWScale = Shader.PropertyToID("_FiresUWCausticScale"), P_UWSharp = Shader.PropertyToID("_FiresUWCausticSharp"),
            P_UWStrength = Shader.PropertyToID("_FiresUWCausticStrength"), P_UWSpeed = Shader.PropertyToID("_FiresUWCausticSpeed"),
            P_UWWarp = Shader.PropertyToID("_FiresUWCausticWarp");
        private const float DappleBaseStrength = 0.6f;   // FTS FiresWaterTuning.UnderwaterDappleBaseStrength

        /// <summary>Called once from Core's plugin Awake. Creates the hidden checker; nothing draws until Live.</summary>
        public static void Initialize()
        {
            if (s_instance != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var go = new GameObject("FiresCore_Underwater") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<Underwater>();
        }

        private static bool OldFtsDrawsUnderwater()
        {
            if (s_oldFtsLooked) return s_oldFts;
            s_oldFtsLooked = true;
            try { s_oldFts = Type.GetType("VerdantsAscent.Water.Core.FiresUnderwaterEnv, FiresTossinShade", false) != null; } catch { s_oldFts = false; }
            return s_oldFts;
        }

        private void Update()
        {
            if (Time.unscaledTime < _checkAt) return;
            _checkAt = Time.unscaledTime + 1f;
            bool swimming = FiresCore.Config.CoreFeatures.IsLive(FiresCore.Config.CoreFeatures.Swimming);
            bool oldFts = OldFtsDrawsUnderwater();
            bool want = swimming && !oldFts;
            if (want && !Live) Begin();
            else if (!want && Live) End();
            string state = want ? "live" : oldFts ? "stood down: this FiresTossinShade still draws its own underwater view (Core takes over when FTS drops it)"
                                                  : "off: Swimming is not live (no Galaxies or Advanced Player Movement installed)";
            if (state != s_state)
            {
                s_state = state;
                FiresLogger.LogInfo(want
                    ? $"[Underwater] live (Swimming owners: {string.Join(", ", FiresCore.Config.CoreFeatures.OwnersOf(FiresCore.Config.CoreFeatures.Swimming))}); bundle {(UnderwaterAssets.Bundle != null ? "loaded" : "missing: " + UnderwaterAssets.Why)}"
                    : "[Underwater] " + state);
            }
        }

        private void LateUpdate()
        {
            if (!Live) return;
            FiresUnderwaterMurk.Tick(true);
            Shader.SetGlobalFloat(P_Dapple, UnderwaterSettings.Dapple.Value ? DappleBaseStrength : 0f);
            Shader.SetGlobalFloat(P_UWScale, UnderwaterSettings.UwScale.Value);
            Shader.SetGlobalFloat(P_UWSharp, UnderwaterSettings.UwSharp.Value);
            Shader.SetGlobalFloat(P_UWStrength, UnderwaterSettings.UwStrength.Value);
            Shader.SetGlobalFloat(P_UWSpeed, UnderwaterSettings.UwSpeed.Value);
            Shader.SetGlobalFloat(P_UWWarp, UnderwaterSettings.UwWarp.Value);
        }

        private static void Begin()
        {
            Live = true;
            UnderwaterSettings.Apply();
            s_parts = new GameObject("FiresCore_UnderwaterParts") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(s_parts);
            s_parts.AddComponent<FiresUnderwaterEnvDriver>();
            s_parts.AddComponent<FiresUnderwaterBubbles>();
            s_parts.AddComponent<FiresUnderwaterFogPass>();   // before the caustics: its command buffer goes in first
            s_caustics = s_parts.AddComponent<FiresCausticsPass>();
        }

        private static void End()
        {
            Live = false;
            if (s_parts != null) { Destroy(s_parts); s_parts = null; }
            s_caustics = null;
            try { FiresUnderwaterEnv.Release(); } catch { }
            // Written ONCE, so FTS's surface water and fog read "above water" from now on.
            Shader.SetGlobalFloat(P_PlayerSubmerged, 0f);
            Shader.SetGlobalFloat(P_Dapple, 0f);
            FiresUnderwaterFogPass.ClearGlobals();
        }

        /// <summary>The fog pass hooked a camera: the caustics re-hook behind it on their next frame (they composite last).</summary>
        internal static void ReorderCaustics()
        {
            if (s_caustics != null) s_caustics.Teardown();
        }
    }
}
