using System.Globalization;

namespace FiresLogAnalysis;

/// <summary>Regex \d+ also captures non-ASCII digits and numbers past int.MaxValue, so a logged number can fail to parse and then counts as not logged.</summary>
internal static class LoggedNumbers
{
    public static bool TryParseInt(string digits, out int value) =>
        int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
