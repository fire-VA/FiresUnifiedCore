using System;
using UnityEngine;

namespace FiresCore.Logging
{
    // Per-mod tagged logger engine. Each consuming mod constructs one instance with its own
    // bracket tag and a verbose-enabled accessor, then exposes it through its own thin static
    // wrapper. Verbose lines gate on the accessor so diagnostic spam stays opt-in. Console
    // coloring and rate-limited suppression are handled centrally by FiresLogColorPatch and the
    // suppression patches, which key off the bracket tag this writes.
    public sealed class FiresLog
    {
        private readonly string _prefix;
        private readonly Func<bool> _verboseEnabled;

        public FiresLog(string modTag, Func<bool> verboseEnabled)
        {
            _prefix = $"[{modTag}]";
            _verboseEnabled = verboseEnabled ?? (() => false);
        }

        public bool VerboseEnabled => ReadVerboseSafely();

        public void Info(string message) => Debug.Log($"{_prefix} {message}");

        public void Verbose(string message)
        {
            if (!ReadVerboseSafely()) return;
            Debug.Log($"{_prefix} {message}");
        }

        public void Warning(string message) => Debug.LogWarning($"{_prefix} {message}");

        public void Error(string message) => Debug.LogError($"{_prefix} {message}");

        private bool ReadVerboseSafely()
        {
            try { return _verboseEnabled(); }
            catch { return false; }
        }
    }
}
