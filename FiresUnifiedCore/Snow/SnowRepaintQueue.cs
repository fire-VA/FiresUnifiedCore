using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1126 (Fire: "the cracks dont start until the snow begins to buildup the neighboring zones need to build up together"): snow repaints spread over frames. Poke's number is
    /// no frame delay: vanilla's m_doLateUpdate fires only at 1 or 2 (Heightmap.cs:270-282) and nothing counts it down, so Poke(1 + n) from 0.2.955 left every chunk from the 3rd on
    /// un-repainted, whole chunks a step behind their neighbours. Chunks wait here and one is poked per frame, nearest first, so all loaded zones catch up within a fraction of a second.
    /// Runs only when something was queued. A full rebuild wins over a paint-only one.
    /// </summary>
    internal static class SnowRepaintQueue
    {
        private sealed class Job { public Heightmap Hm; public bool Full; public float At; }
        private static readonly List<Job> s_jobs = new List<Job>();
        private static int s_queued, s_done; private static float s_longest, s_sayAt;

        internal static void Add(Heightmap hm, bool full)
        {
            if (hm == null) return;
            s_queued++;
            foreach (var j in s_jobs) if (j.Hm == hm) { j.Full |= full; return; }
            s_jobs.Add(new Job { Hm = hm, Full = full, At = Time.unscaledTime });
        }

        internal static void Tick()
        {
            if (s_jobs.Count > 0)
            {
                Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
                int best = -1; float bestD = float.MaxValue;
                for (int i = s_jobs.Count - 1; i >= 0; i--)
                {
                    var h = s_jobs[i].Hm;
                    if (h == null) { s_jobs.RemoveAt(i); if (best > i) best--; continue; }
                    float d = (h.transform.position - me).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0)
                {
                    var job = s_jobs[best];
                    s_jobs.RemoveAt(best);
                    job.Hm.Poke(1, paintOnly: !job.Full);
                    s_done++;
                    float waited = Time.unscaledTime - job.At;
                    if (waited > s_longest) s_longest = waited;
                }
            }
            if (Time.unscaledTime >= s_sayAt)
            {
                if (s_sayAt > 0f && s_queued > 0) FiresLogger.LogDebug($"[Snow] repaint queue last 10 s: {s_queued} queued, {s_done} done, longest wait {s_longest:0.00} s, {s_jobs.Count} waiting");
                s_sayAt = Time.unscaledTime + 10f; s_queued = s_done = 0; s_longest = 0f;
            }
        }
    }
}