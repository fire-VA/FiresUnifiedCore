using System;
using System.Collections.Generic;

namespace FiresLogAnalysis;

public enum HarmlessScope
{
    AnyLog,
    DedicatedServerOnly,
}

public sealed class HarmlessMessage
{
    public HarmlessMessage(string messageFragment, string reason, HarmlessScope scope)
    {
        MessageFragment = messageFragment;
        Reason = reason;
        Scope = scope;
    }

    public string MessageFragment { get; }
    public string Reason { get; }
    public HarmlessScope Scope { get; }

    public bool Matches(LogEntry entry, LogEnvironment environment)
    {
        if (Scope == HarmlessScope.DedicatedServerOnly && !environment.IsDedicatedServer)
            return false;
        if (entry.Message.IndexOf(MessageFragment, StringComparison.Ordinal) >= 0)
            return true;
        foreach (var line in entry.ContinuationLines)
        {
            if (line.IndexOf(MessageFragment, StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }
}

/// <summary>Warnings and errors that show up on healthy installs. Each entry was seen in real logs and its cause checked.</summary>
public static class KnownHarmlessMessages
{
    private const string NoGraphicsOnServer = "A dedicated server has no graphics card or display, so Unity turns rendering features off.";
    private const string NoPartyLibraryOnLinux = "The PlayFab Party voice library isn't shipped with the Linux dedicated server.";
    private const string UnityMemoryWarning = "Unity warns when temporary memory lives longer than a few frames, usually during a loading hitch.";

    public static readonly IReadOnlyList<HarmlessMessage> All = new[]
    {
        new HarmlessMessage("Could not find video decode shader pass", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("Could not find material Hidden/VideoDecode", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("Could not find material Hidden/VideoComposite", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("This custom render path shader needs to have at least", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("HDR Render Texture not supported, disabling HDR on reflection probe.", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("[AmplifyOcclusion]", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("has been disabled as it's not supported on the current platform.", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("Failed to play intro cinematic", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("AsyncResourceUpload failed", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        // TextMeshPro's default font isn't loaded on a server, which draws no text.
        new HarmlessMessage("The LiberationSans SDF Font Asset was not found.", NoGraphicsOnServer, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("DllNotFoundException: libParty.so", NoPartyLibraryOnLinux, HarmlessScope.DedicatedServerOnly),
        new HarmlessMessage("Failed to find expected binary shader data", "Unity startup warning about precompiled shader data. It appears on normal installs and the game still renders.", HarmlessScope.AnyLog),
        new HarmlessMessage("Ambiguous asset name for path.", "Valheim ships several assets with the same name. Jotunn warns and keeps the first one.", HarmlessScope.AnyLog),
        new HarmlessMessage("Multiple pieces with the same m_name but different resource requirements", "The vanilla Yule gift pieces share one name. PlanBuild warns about it.", HarmlessScope.AnyLog),
        new HarmlessMessage("Tried to remove non-existent peer", "Better Networking logs this when a player disconnects.", HarmlessScope.AnyLog),
        new HarmlessMessage("Missing audio clip in music ", "A vanilla music entry has no audio clip, so the game skips it.", HarmlessScope.AnyLog),
        new HarmlessMessage("GetBiome error ", "Vanilla compares two biome lookups for the player's position every second. They disagree near biome borders.", HarmlessScope.AnyLog),
        new HarmlessMessage("Local player destroyed", "Vanilla logs this when the player logs out or quits.", HarmlessScope.AnyLog),
        new HarmlessMessage("No player data","The character has no saved data on disk yet, for example a new character.", HarmlessScope.AnyLog),
        new HarmlessMessage("because of process filters", "The mod is meant for the other game process (client-only or server-only), so BepInEx skips it on purpose.", HarmlessScope.AnyLog),
        new HarmlessMessage("AccessTools.GetTypesFromAssembly: assembly", "A mod's optional support for another mod couldn't load because that mod isn't installed. Only matters if you expected that feature.", HarmlessScope.AnyLog),
        new HarmlessMessage("Internal: deleting an allocation that is older than its permitted lifetime", UnityMemoryWarning, HarmlessScope.AnyLog),
        new HarmlessMessage("JobTempAlloc has allocations that are more than the maximum lifespan", UnityMemoryWarning, HarmlessScope.AnyLog),
        new HarmlessMessage("To Debug, run app with -diag-job-temp-memory-leak-validation", UnityMemoryWarning, HarmlessScope.AnyLog),
        new HarmlessMessage("Only custom filters can be played. Please add a custom filter or an audioclip to the audiosource", "Unity audio warning from the vanilla main menu ambience.", HarmlessScope.AnyLog),
    };
}
