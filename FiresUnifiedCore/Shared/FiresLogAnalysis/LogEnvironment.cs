using System;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

public sealed class LogEnvironment
{
    private const string DedicatedServerProcessName = "valheim_server";

    private static readonly Regex MajorVersionNumber = new Regex(@"(?<major>\d+)\.\d+", RegexOptions.Compiled);

    public string ProcessName { get; internal set; }
    public string BepInExVersion { get; internal set; }
    public string BepInExPackVersion { get; internal set; }
    public string OperatingSystem { get; internal set; }
    public string UnityVersion { get; internal set; }
    public string GameVersion { get; internal set; }
    public string NetworkVersion { get; internal set; }
    public DateTime? FirstTimestamp { get; internal set; }
    public DateTime? LastTimestamp { get; internal set; }
    public string WorldName { get; internal set; }
    public string WorldSaveNumber { get; internal set; }
    public string ObjectsAtWorldLoad { get; internal set; }
    public int? ObjectsAtLastCount { get; internal set; }
    public int? PeakConnections { get; internal set; }
    public bool HasBepInExLines { get; internal set; }

    public bool IsDedicatedServer => string.Equals(ProcessName, DedicatedServerProcessName, StringComparison.OrdinalIgnoreCase);

    public bool IsRecognizedValheimLog => BepInExVersion != null || GameVersion != null;

    public int? GameMajorVersion
    {
        get
        {
            if (GameVersion == null)
                return null;
            var match = MajorVersionNumber.Match(GameVersion);
            return match.Success && LoggedNumbers.TryParseInt(match.Groups["major"].Value, out var major) ? major : (int?)null;
        }
    }

    public TimeSpan? SessionLength => FirstTimestamp.HasValue && LastTimestamp.HasValue && LastTimestamp >= FirstTimestamp
        ? LastTimestamp.Value - FirstTimestamp.Value
        : (TimeSpan?)null;
}
