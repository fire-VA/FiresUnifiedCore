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

        public void Info(string message) => Debug.Log(Compose(message));

        public void Verbose(string message)
        {
            if (!ReadVerboseSafely()) return;
            Debug.Log(Compose(message));
        }

        public void Warning(string message) => Debug.LogWarning(Compose(message));

        public void Error(string message) => Debug.LogError(Compose(message));

        // Folded to the console's width here rather than at each call site, so every mod on this logger gets it and
        // no summary line has to be written short by hand. The tag stays on the first row for the colour patch, the
        // suppression patches and grep.
        private string Compose(string message)
            => _prefix + " " + ConsoleWrap.Fit(message, ConsoleWrap.UnityLogPrefixColumns + _prefix.Length + 1);

        private bool ReadVerboseSafely()
        {
            try { return _verboseEnabled(); }
            catch { return false; }
        }
    }
}
