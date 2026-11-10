using System.Threading;

namespace FiresLogAnalysis;

/// <summary>The newest FiresGhettoNetworking release. The log bot refreshes it from Thunderstore; every other host of this
/// library (Core's log relay) uses the version it was built with.</summary>
public static class FgnReleases
{
    public const string ThunderstoreNamespace = "VerdantsAscent";
    public const string ThunderstorePackage = "FiresGhettoNetworking";
    public const string VersionShippedWithThisBuild = "1.4.31";

    private static string latestReleasedVersion = VersionShippedWithThisBuild;

    public static string LatestReleasedVersion
    {
        get => Volatile.Read(ref latestReleasedVersion);
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                Volatile.Write(ref latestReleasedVersion, value.Trim());
        }
    }
}
