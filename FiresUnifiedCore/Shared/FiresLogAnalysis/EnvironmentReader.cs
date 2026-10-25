using System;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

internal sealed class EnvironmentReader
{
    private const string BepInExSource = "BepInEx";
    private const string UnknownUnityVersion = "Unknown";

    private static readonly Regex BepInExBanner = new Regex(@"^BepInEx (?<version>\S+) - (?<process>.+?) \(", RegexOptions.Compiled);
    private static readonly Regex BepInExPack = new Regex(@"^User is running BepInExPack Valheim version (?<version>\S+) from Thunderstore", RegexOptions.Compiled);
    private static readonly Regex SystemPlatform = new Regex(@"^System platform: [^,]+, (?<os>.+)$", RegexOptions.Compiled);
    private static readonly Regex DetectedUnityVersion = new Regex(@"^Detected Unity version: v(?<version>\S+)$", RegexOptions.Compiled);
    private static readonly Regex RunningUnityVersion = new Regex(@"^Running under Unity v(?<version>\S+)", RegexOptions.Compiled);
    private static readonly Regex GameVersion = new Regex(@"^Valheim version: (?<version>.+?)(?: \(network version (?<network>\d+)\))?$", RegexOptions.Compiled);
    private static readonly Regex WorldLoad = new Regex(@"^ZNet\.LoadWorld: (?<world>.+?) \(.*\), save number (?<save>\d+)$", RegexOptions.Compiled);
    private static readonly Regex ChunkedWorldLoad = new Regex(@"^ZDOMan\.LoadChunks - Starting to load (?<objects>[\d,]+) zdos", RegexOptions.Compiled);
    private static readonly Regex ConnectionStats = new Regex(@"^\s*Connections (?<connections>\d+) ZDOS:(?<objects>\d+)", RegexOptions.Compiled);

    private readonly LogEnvironment environment;

    public EnvironmentReader(LogEnvironment environment)
    {
        this.environment = environment;
    }

    public void Read(LogEntry entry)
    {
        if (entry.HasBepInExHeader)
            environment.HasBepInExLines = true;
        if (entry.Timestamp.HasValue)
        {
            if (!environment.FirstTimestamp.HasValue)
                environment.FirstTimestamp = entry.Timestamp;
            environment.LastTimestamp = entry.Timestamp;
        }

        if (entry.Source == BepInExSource)
            ReadBepInExLine(entry.Message);
        else
            ReadGameLine(entry.Message);
    }

    private void ReadBepInExLine(string message)
    {
        Match match;
        if (environment.BepInExVersion == null && (match = BepInExBanner.Match(message)).Success)
        {
            environment.BepInExVersion = match.Groups["version"].Value;
            environment.ProcessName = match.Groups["process"].Value;
        }
        else if ((match = BepInExPack.Match(message)).Success)
        {
            environment.BepInExPackVersion = match.Groups["version"].Value;
        }
        else if ((match = SystemPlatform.Match(message)).Success)
        {
            environment.OperatingSystem = match.Groups["os"].Value;
        }
        else if ((match = DetectedUnityVersion.Match(message)).Success)
        {
            environment.UnityVersion = match.Groups["version"].Value;
        }
        else if ((match = RunningUnityVersion.Match(message)).Success && match.Groups["version"].Value != UnknownUnityVersion)
        {
            environment.UnityVersion = match.Groups["version"].Value;
        }
    }

    private void ReadGameLine(string message)
    {
        Match match;
        if ((match = ConnectionStats.Match(message)).Success)
        {
            if (LoggedNumbers.TryParseInt(match.Groups["connections"].Value, out var connections))
                environment.PeakConnections = Math.Max(environment.PeakConnections ?? 0, connections);
            if (LoggedNumbers.TryParseInt(match.Groups["objects"].Value, out var objects))
                environment.ObjectsAtLastCount = objects;
        }
        else if ((match = GameVersion.Match(message)).Success)
        {
            environment.GameVersion = match.Groups["version"].Value;
            if (match.Groups["network"].Success)
                environment.NetworkVersion = match.Groups["network"].Value;
        }
        else if ((match = WorldLoad.Match(message)).Success)
        {
            environment.WorldName = match.Groups["world"].Value;
            environment.WorldSaveNumber = match.Groups["save"].Value;
        }
        else if ((match = ChunkedWorldLoad.Match(message)).Success)
        {
            environment.ObjectsAtWorldLoad = match.Groups["objects"].Value;
        }
    }
}
