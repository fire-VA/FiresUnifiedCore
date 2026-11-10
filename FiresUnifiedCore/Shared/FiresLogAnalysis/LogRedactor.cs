using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>
/// Masks Steam IDs, IP addresses and user folder names before anything is posted.
/// A bare dotted quad is masked only where the surrounding text says it is an address, because
/// <c>5.4.23.3</c> is both a valid BepInEx version and a valid IPv4 address.
/// </summary>
public static class LogRedactor
{
    private const int VisibleSteamIdDigits = 4;
    private const string MaskedEndpoint = "[IP address]";
    private const string MaskedUserName = "<user>";

    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";
    private const string DottedQuad = "(?:" + Octet + @"\.){3}" + Octet;

    // Words that mean the value after them is a network address. "to" alone is excluded on purpose:
    // "upgraded to 1.2.3.4" is a version, and "connected to host:port" is already an endpoint.
    private const string AddressWordBefore =
        @"(?:IP|IPs|IPv4|address|addresses|addr|endpoint|peer|peers|host|hostname|gateway|server|" +
        @"connected\s+to|connecting\s+to|connection\s+from|from|resolved\s+to|bound\s+to|listening\s+on)";

    // Words that mean the value before them is a network address.
    private const string AddressWordAfter = @"(?:disconnected|connected|timed\s+out|reachable|unreachable)";

    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    private static readonly Regex SteamId = new Regex(@"(?<!\d)7656119\d{10}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex IpEndpoint = new Regex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}:\d{1,5}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex AddressBeforeQuad =
        new Regex(@"(?<prefix>\b" + AddressWordBefore + @"\b[\s:=]{1,3}\(?)(?<quad>" + DottedQuad + @")(?![\d.])", Options);
    private static readonly Regex QuadBeforeAddressWord =
        new Regex(@"(?<![\w.])(?<quad>" + DottedQuad + @")(?<suffix>\)?\s{1,3}\b" + AddressWordAfter + @"\b)", Options);
    private static readonly Regex WindowsUserFolder = new Regex(@"(?<prefix>\b[A-Za-z]:[\\/]+Users[\\/]+)[^\\/\s'""<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LinuxHomeFolder = new Regex(@"(?<prefix>(?:^|[\s'""(=:\[])/?home/)[^/\s'""<>]+", RegexOptions.Compiled);

    public static string Redact(string text)
    {
        var redacted = SteamId.Replace(text, match => "[Steam ID ..." + match.Value.Substring(match.Value.Length - VisibleSteamIdDigits) + "]");
        redacted = IpEndpoint.Replace(redacted, MaskedEndpoint);
        redacted = AddressBeforeQuad.Replace(redacted, match => match.Groups["prefix"].Value + MaskedEndpoint);
        redacted = QuadBeforeAddressWord.Replace(redacted, match => MaskedEndpoint + match.Groups["suffix"].Value);
        redacted = WindowsUserFolder.Replace(redacted, match => match.Groups["prefix"].Value + MaskedUserName);
        return LinuxHomeFolder.Replace(redacted, match => match.Groups["prefix"].Value + MaskedUserName);
    }
}
