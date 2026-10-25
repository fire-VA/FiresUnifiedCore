using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>
/// Reads both stack trace styles found in Valheim logs: Unity's "Type.Method (args) (at &lt;hash&gt;:0)"
/// and Mono's "  at Type.Method (args) [0x00000] in &lt;hash&gt;:0".
/// </summary>
public static class StackTraceLines
{
    private const string StackTraceHeader = "Stack trace:";

    private static readonly string[] RuntimeFramePrefixes =
    {
        "System.",
        "UnityEngine.",
        "Mono.",
        "MonoMod.",
        "HarmonyLib.",
        "BepInEx.",
        "(wrapper managed-to-native)",
        "(wrapper runtime-invoke)",
        "(wrapper delegate-invoke)",
        "(wrapper remoting-invoke",
        // A MonoMod DynamicMethodDefinition, which is what Harmony builds when it patches a method.
        // Its name is the PATCHED method - "(wrapper dynamic-method) ZDOMan.DMD<ZDOMan::FindObjects>"
        // is vanilla's ZDOMan, not the mod that patched it. Treating it as a mod frame made the bot
        // print a vanilla type under the advice "Update the mod shown here", naming nothing.
        "(wrapper dynamic-method)",
    };

    /// <summary>
    /// The vanilla method a Harmony patch was applied to, read from a MonoMod DMD frame such as
    /// "(wrapper dynamic-method) ZDOMan.DMD&lt;ZDOMan::FindObjects&gt;(...)" -> "ZDOMan.FindObjects".
    /// This does not name the mod - nothing in the trace can - but it names the patch site, which is
    /// the only actionable thing in a stack made entirely of vanilla frames.
    /// </summary>
    private static readonly Regex DynamicMethodFrame = new Regex(
        @"^(?:at )?\(wrapper dynamic-method\) \S*?DMD<(?<type>[^:<>]+)::(?<method>[^:<>]+)>",
        RegexOptions.Compiled);

    public static string ReadPatchedMethod(IReadOnlyList<string> continuationLines)
    {
        foreach (var line in continuationLines)
        {
            var match = DynamicMethodFrame.Match(line.Trim());
            if (match.Success)
                return match.Groups["type"].Value + "." + match.Groups["method"].Value;
        }
        return null;
    }

    /// <summary>Libraries that mods copy into their own DLL, so a frame inside them doesn't say which mod failed.</summary>
    private static readonly string[] BundledLibraryPrefixes =
    {
        "ServerSync.",
        "LocalizationManager.",
        "ItemManager.",
        "PieceManager.",
        "CreatureManager.",
        "SkillManager.",
        "StatusEffectManager.",
        "LocationManager.",
    };

    private static readonly Regex MonoFrame = new Regex(
        @"^\s*at (?<frame>(?:\(wrapper [^)]*\) )?[^\s(]+ ?\(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex UnityFrame = new Regex(
        @"^(?<frame>(?:\(wrapper [^)]*\) )?[A-Za-z_<][^\s(]*[.:][^\s(]+ ?\([^)]*\))(?: \(at [^)]*\))?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FrameLocationSuffix = new Regex(
        @"\s*(?:\[0x[0-9a-fA-F]+\] in <[^>]*>:\d+|in <[^>]*>:\d+|\(at [^)]*\))\s*$",
        RegexOptions.Compiled);

    public static bool IsTraceLine(string line) =>
        line.StartsWith(StackTraceHeader, StringComparison.Ordinal)
        || MonoFrame.IsMatch(line)
        || UnityFrame.IsMatch(line);

    public static List<string> ReadModFrames(IReadOnlyList<string> continuationLines, int maxFrames)
    {
        var frames = new List<string>();
        foreach (var line in continuationLines)
        {
            if (frames.Count == maxFrames)
                break;

            var frame = ReadFrame(line);
            if (frame != null && !IsRuntimeFrame(frame))
                frames.Add(frame);
        }
        return frames;
    }

    /// <summary>The first frame that belongs to a mod's own code, skipping runtime and bundled library frames.</summary>
    public static string ReadOwningModFrame(IReadOnlyList<string> continuationLines)
    {
        string firstModFrame = null;
        foreach (var line in continuationLines)
        {
            var frame = ReadFrame(line);
            if (frame == null || IsRuntimeFrame(frame))
                continue;
            if (!StartsWithAny(frame, BundledLibraryPrefixes))
                return frame;
            if (firstModFrame == null)
                firstModFrame = frame;
        }
        return firstModFrame;
    }

    private static string ReadFrame(string line)
    {
        var monoMatch = MonoFrame.Match(line);
        if (monoMatch.Success)
            return FrameLocationSuffix.Replace(monoMatch.Groups["frame"].Value, string.Empty);

        var unityMatch = UnityFrame.Match(line);
        if (unityMatch.Success)
            return FrameLocationSuffix.Replace(unityMatch.Groups["frame"].Value, string.Empty);

        return null;
    }

    private static bool IsRuntimeFrame(string frame) => StartsWithAny(frame, RuntimeFramePrefixes) || IsGameFrame(frame);

    /// <summary>
    /// True for Valheim's own code. Every type in assembly_valheim is in the GLOBAL namespace -
    /// ZDOMan, ZNetScene, Player, ZoneSystem - so its frames read "Type.Method(...)" with exactly
    /// one dot before the method. Mod code is namespaced, so its frames carry at least two.
    ///
    /// This exists because a stack can consist entirely of game frames while still being caused by
    /// a mod: Harmony patches a vanilla method, the patch calls game code that no longer exists, and
    /// nothing belonging to the mod ever appears. The bot used to print "ZDOMan.FindSectorObjects"
    /// under the advice "Update the mod shown here", which sends the reader hunting for a mod that
    /// was never named. Recognising game frames is what lets the caller say so honestly.
    ///
    /// A mod that puts types in the global namespace would be misread as game code - but such a
    /// frame could not identify its mod anyway, so the honest "cannot tell" answer is still right.
    /// </summary>
    private static bool IsGameFrame(string frame)
    {
        var paren = frame.IndexOf('(');
        var qualified = (paren >= 0 ? frame.Substring(0, paren) : frame).Trim();
        if (qualified.Length == 0)
            return false;

        var lastDot = qualified.LastIndexOf('.');
        if (lastDot <= 0)
            return false;               // no Type.Method shape at all; leave it to the caller

        var type = qualified.Substring(0, lastDot);
        return type.IndexOf('.') < 0;   // global-namespace type => Valheim's own code
    }

    private static bool StartsWithAny(string frame, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (frame.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
