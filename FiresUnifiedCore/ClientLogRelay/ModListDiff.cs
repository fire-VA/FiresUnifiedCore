using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FiresCore.ClientLogRelay
{
    /// <summary>
    /// Pure, dependency-free helper that produces a side-by-side diff between the client's
    /// and the server's BepInEx plugin lists. The output is a summary + a GUID-sorted list
    /// suitable for a .txt attachment or Discord upload.
    /// </summary>
    public static class ModListDiff
    {
        public sealed class VersionMismatch
        {
            public string Guid;
            public string ClientVersion;
            public string ServerVersion;
        }

        public sealed class Result
        {
            public List<string>           ClientOnly       = new List<string>();
            public List<string>           ServerOnly       = new List<string>();
            public List<VersionMismatch>  VersionMismatches = new List<VersionMismatch>();
            public int                    SharedMatching;
            public string                 Report; // rendered .txt-ready text
        }

        public static Result Compute(
            IReadOnlyDictionary<string, string> clientMods,
            IReadOnlyDictionary<string, string> serverMods,
            string playerName,
            string platformId,
            string brandLabel,
            DateTime capturedUtc)
        {
            var result = new Result();
            if (clientMods == null) clientMods = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
            if (serverMods == null) serverMods = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

            // Normalise key comparison: case-insensitive on GUID.
            var client = new Dictionary<string, string>(clientMods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in clientMods) client[kv.Key] = kv.Value;
            var server = new Dictionary<string, string>(serverMods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in serverMods) server[kv.Key] = kv.Value;

            foreach (var kv in client)
            {
                if (!server.TryGetValue(kv.Key, out var srvVer))
                {
                    result.ClientOnly.Add(kv.Key);
                    continue;
                }
                if (!string.Equals(kv.Value ?? string.Empty, srvVer ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    result.VersionMismatches.Add(new VersionMismatch
                    {
                        Guid = kv.Key,
                        ClientVersion = kv.Value ?? string.Empty,
                        ServerVersion = srvVer ?? string.Empty,
                    });
                }
                else
                {
                    result.SharedMatching++;
                }
            }
            foreach (var kv in server)
            {
                if (!client.ContainsKey(kv.Key)) result.ServerOnly.Add(kv.Key);
            }

            result.ClientOnly.Sort(StringComparer.OrdinalIgnoreCase);
            result.ServerOnly.Sort(StringComparer.OrdinalIgnoreCase);
            result.VersionMismatches = result.VersionMismatches
                .OrderBy(v => v.Guid, StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.Report = Render(result, client, server, playerName, platformId, brandLabel, capturedUtc);
            return result;
        }

        private static string Render(Result result,
            IReadOnlyDictionary<string, string> client,
            IReadOnlyDictionary<string, string> server,
            string playerName, string platformId, string brandLabel, DateTime capturedUtc)
        {
            var sb = new StringBuilder();
            string label = string.IsNullOrEmpty(brandLabel) ? "ClientLogRelay" : brandLabel;
            sb.AppendLine($"# {label} - Client vs Server Mod List Diff");
            sb.AppendLine($"# Player:          {playerName ?? "unknown"}");
            sb.AppendLine($"# PlatformID:      {platformId ?? "unknown"}");
            sb.AppendLine($"# Captured:        {capturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"# Client mods:     {client.Count}");
            sb.AppendLine($"# Server mods:     {server.Count}");
            sb.AppendLine($"# Shared matching: {result.SharedMatching}");
            sb.AppendLine($"# Client-only:     {result.ClientOnly.Count}");
            sb.AppendLine($"# Server-only:     {result.ServerOnly.Count}");
            sb.AppendLine($"# Version mismatches: {result.VersionMismatches.Count}");
            sb.AppendLine();

            if (result.VersionMismatches.Count > 0)
            {
                sb.AppendLine("## Version mismatches (client vs server)");
                foreach (var mismatch in result.VersionMismatches)
                    sb.AppendLine($"  {mismatch.Guid,-55} client={mismatch.ClientVersion}  server={mismatch.ServerVersion}");
                sb.AppendLine();
            }

            if (result.ClientOnly.Count > 0)
            {
                sb.AppendLine("## Client-only (installed on client, missing on server)");
                foreach (var guid in result.ClientOnly)
                    sb.AppendLine($"  {guid,-55} version={SafeLookup(client, guid)}");
                sb.AppendLine();
            }

            if (result.ServerOnly.Count > 0)
            {
                sb.AppendLine("## Server-only (installed on server, missing on client)");
                foreach (var guid in result.ServerOnly)
                    sb.AppendLine($"  {guid,-55} version={SafeLookup(server, guid)}");
                sb.AppendLine();
            }

            sb.AppendLine("## All client mods");
            foreach (var kv in client.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("  C ").Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine();

            sb.AppendLine("## All server mods");
            foreach (var kv in server.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("  S ").Append(kv.Key).Append('=').AppendLine(kv.Value);

            return sb.ToString();
        }

        private static string SafeLookup(IReadOnlyDictionary<string, string> lookup, string key)
        {
            if (lookup == null) return string.Empty;
            return lookup.TryGetValue(key, out var found) ? (found ?? string.Empty) : string.Empty;
        }
    }
}
