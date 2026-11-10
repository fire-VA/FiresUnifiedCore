using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Opt-in hard enforcement of the movement single-writer rule. Off by default, the SetMoveDir prefix only blocks
    /// kinematic bodies and frozen states; on, it also drops any companion SetMoveDir that didn't come from
    /// <see cref="UnifiedMovementAuthority"/>, with vanilla MoveTo pathfinding let through. Verify in game before
    /// relying on it; turning it off reverts instantly.
    /// </summary>
    public static class MovementGateConfig
    {
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound and synced by FiresCompanions; Core reads the owner's
        // value (old default OFF without it). Same member name; .Value still reads (type is now OwnedSetting<bool>).
        public static readonly FiresCore.Config.OwnedSetting<bool> EnforceSingleWriterMovement = new FiresCore.Config.OwnedSetting<bool>("Companions", "EnforceSingleWriterMovement", true);

        public static void Initialize(ConfigFile config) { }

        public static void BindToSync(ConfigSync configSync) { }

        /// <summary>Hot-path read (cached while the owner's key has a change event).</summary>
        public static bool Enabled => EnforceSingleWriterMovement.Value;

        // 0.2.965 (lead 10-28, proof table): while the gate is on, one Debug line a minute with the companion SetMoveDir writes it let
        // through and dropped, and the callers behind the drops (a stack sampled at most 4 times a second, so the count is exact and the
        // writer list is a sample).
        private const float ProofSeconds = 60f, SampleSeconds = 0.25f;
        private static int s_allowed, s_dropped, s_sampled;
        private static float s_proofAt = -1f, s_sampleAt;
        private static readonly System.Collections.Generic.Dictionary<string, int> s_writers = new System.Collections.Generic.Dictionary<string, int>();

        internal static void NoteWrite(bool dropped)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (s_proofAt < 0f) s_proofAt = now + ProofSeconds;
            if (dropped)
            {
                s_dropped++;
                if (now >= s_sampleAt) { s_sampleAt = now + SampleSeconds; s_sampled++; string w = Writer(); s_writers.TryGetValue(w, out int n); s_writers[w] = n + 1; }
            }
            else s_allowed++;
            if (now < s_proofAt) return;
            s_proofAt = now + ProofSeconds;
            var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(s_writers);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < top.Count && i < 3; i++) names.Add($"{top[i].Key} ({top[i].Value})");
            FiresCore.Logging.FiresLogger.LogDebug($"[MovementGate] single-writer last 60 s: {s_allowed} companion move write(s) allowed, {s_dropped} dropped (external while an owner held movement)"
                + (s_dropped > 0 ? $"; top dropped writer(s) of {s_sampled} sampled: {string.Join(", ", names.ToArray())}" : ""));
            s_allowed = 0; s_dropped = 0; s_sampled = 0; s_writers.Clear();
        }

        // the first caller outside Character.SetMoveDir, its Harmony wrapper and this gate
        private static string Writer()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames == null) return "?";
                foreach (var f in frames)
                {
                    var m = f.GetMethod();
                    if (m == null) continue;
                    string type = m.DeclaringType != null ? m.DeclaringType.Name : "";
                    if (m.Name.Contains("SetMoveDir") || type == nameof(MovementGateConfig) || type.StartsWith("CompanionPatches")) continue;
                    return $"{type}.{m.Name}";
                }
            }
            catch { }
            return "?";
        }
    }
}
