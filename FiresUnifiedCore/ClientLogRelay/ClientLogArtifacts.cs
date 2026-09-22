using System;
using System.Collections.Generic;
using FiresLogAnalysis;

namespace FiresCore.ClientLogRelay
{
    /// <summary>
    /// What every consumer receives once a client's BepInEx log and mod list have arrived on the server. The
    /// wire transport that owns the protocol builds it and hands it to ClientLogRelay.ReportArtifacts, which
    /// parses, persists and fans it out. Treat every field as read-only: consumers run one after another over
    /// the same instance.
    /// </summary>
    public sealed class ClientLogArtifacts
    {
        public string PlatformId   { get; }
        public string PlayerName   { get; }
        public byte[] LogBytes     { get; }
        public IReadOnlyDictionary<string, string> ModList { get; }
        public IReadOnlyDictionary<string, string> ServerMods { get; }
        public string BrandLabel   { get; }
        public DateTime CapturedUtc { get; }

        /// <summary>
        /// Populated by <see cref="ClientLogRelay.AnalyzeLog"/>. Null when the analyzer could not read the log.
        /// </summary>
        public LogAnalysis LogAnalysis { get; internal set; }

        /// <summary>
        /// The <see cref="ReportWriter"/> report for <see cref="LogAnalysis"/>, or why the analysis failed.
        /// </summary>
        public string ErrorsWarningsReport { get; internal set; }

        /// <summary>
        /// Populated by <see cref="ClientLogRelay.ReportArtifacts"/> when <see cref="ServerMods"/> is non-null.
        /// </summary>
        public ModListDiff.Result ModDiff { get; internal set; }

        public ClientLogArtifacts(string platformId, string playerName,
            byte[] logBytes, IReadOnlyDictionary<string, string> modList,
            IReadOnlyDictionary<string, string> serverMods = null,
            string brandLabel = null)
        {
            PlatformId = platformId ?? string.Empty;
            PlayerName = string.IsNullOrEmpty(playerName) ? "unknown" : playerName;
            LogBytes   = logBytes   ?? Array.Empty<byte>();
            ModList    = modList    ?? new Dictionary<string, string>(0);
            ServerMods = serverMods;
            BrandLabel = brandLabel;
            CapturedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Filesystem-safe version of the platform ID (no colons or slashes).
        /// </summary>
        public string SafePlatformId
            => (PlatformId ?? "unknown").Replace(":", "_").Replace("/", "_").Replace("\\", "_");

        /// <summary>
        /// Filesystem-safe version of the player name.
        /// </summary>
        public string SafePlayerName
        {
            get
            {
                if (string.IsNullOrEmpty(PlayerName)) return "unknown";
                var invalid = System.IO.Path.GetInvalidFileNameChars();
                return string.Concat(PlayerName.Split(invalid));
            }
        }

        /// <summary>
        /// Default subdirectory name <see cref="ClientLogArtifactWriter"/> writes this client's
        /// artifacts under. Format: <c>{SafePlayerName}_{SafePlatformId}</c>.
        /// </summary>
        public string DefaultFolderName => $"{SafePlayerName}_{SafePlatformId}";
    }
}
