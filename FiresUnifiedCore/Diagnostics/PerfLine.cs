using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// With Debug "PerfLog" ON, a client logs every 10 s
    /// "[Perf] fps <avg> (worst frame <ms> ms, avg <ms> ms) over 10 s, <n> frames". Off by default; a test round turns it on through -PluginCfg.
    /// </summary>
    [HarmonyPatch(typeof(Game), "Update")]
    internal static class PerfLine
    {
        private const float Window = 10f;
        private static float s_start = -1f, s_worst;
        private static int s_frames;

        private static void Postfix()
        {
            var cfg = FiresCore.Config.ConfigManager.Instance?.configPerfLog;
            if (cfg == null || !cfg.Value || Application.isBatchMode) { s_start = -1f; return; }
            float now = Time.realtimeSinceStartup, dt = Time.unscaledDeltaTime;
            if (s_start < 0f) { s_start = now; s_frames = 0; s_worst = 0f; return; }
            s_frames++;
            if (dt > s_worst) s_worst = dt;
            float span = now - s_start;
            if (span < Window) return;
            Debug.Log($"[Perf] fps {s_frames / span:0.0} (worst frame {s_worst * 1000f:0.0} ms, avg {span * 1000f / Mathf.Max(1, s_frames):0.0} ms) over {span:0} s, {s_frames} frames");
            s_start = now; s_frames = 0; s_worst = 0f;
        }
    }
}
