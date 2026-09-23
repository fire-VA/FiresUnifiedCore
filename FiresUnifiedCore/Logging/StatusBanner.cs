using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Logging
{
    // One periodic status box for the whole Fires family. A mod registers a line provider instead of printing its own
    // periodic summary; every interval Core asks each provider for its line on the main thread and prints the non-empty
    // ones as one box, skipping the box when nothing changed since the last one. [General] StatusBanner turns it off and
    // StatusBannerSeconds sets the interval; both are per machine and read live.
    public static class StatusBanner
    {
        internal const string TitleEmoji = "\U0001F4CA";
        private const string TitlePrefix = TitleEmoji + " STATUS";
        // BepInEx already prints "[Info   :FiresUnifiedCore] " in front of every line, so repeating the mod name
        // here cost 19 columns for nothing.
        private const string Tag = "[LoadSummary]";
        private const string SourceGap = "  ";
        // The BepInEx console is 100 columns wide. It spends 27 of them on its own source prefix before this box
        // prints anything, the Tag above spends its own length plus a space, and the box borders take 4 - so this is
        // the widest inner line that still fits on one console row. It was a flat 110, which rendered at 157 columns
        // and wrapped every row of every box.
        private const int ConsoleColumns = 100;
        private const int BepInExPrefixColumns = 27;
        private const int BoxBorderColumns = 4;
        private static readonly int MaxInnerWidth =
            ConsoleColumns - BepInExPrefixColumns - (Tag.Length + 1) - BoxBorderColumns;
        private const int TitleMargin = 8;

        private const string ConfigSection = "General";
        private const string EnabledKey = "StatusBanner";
        private const string SecondsKey = "StatusBannerSeconds";
        private const float SecondsDefault = 60f;
        private const float SecondsMin = 10f;
        private const float SecondsMax = 600f;
        private const string EnabledDescription =
            "Print one combined status box from every Fires mod that reports one, instead of each mod printing its own " +
            "periodic lines. A box that would repeat the last one is skipped. This machine only.";
        private const string SecondsDescription = "Seconds between status boxes.";

        private static readonly List<KeyValuePair<string, Func<string>>> Providers = new List<KeyValuePair<string, Func<string>>>();
        private static readonly List<string> Lines = new List<string>();
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<float> _seconds;
        private static string _lastPrinted = string.Empty;
        private static float _nextEmitAt;

        // Core binds these from its own config file at setup; nothing else should.
        internal static void BindConfig(ConfigFile config)
        {
            _enabled = config.Bind(ConfigSection, EnabledKey, true, EnabledDescription);
            _seconds = config.Bind(ConfigSection, SecondsKey, SecondsDefault,
                new ConfigDescription(SecondsDescription, new AcceptableValueRange<float>(SecondsMin, SecondsMax)));
        }

        // Adds or replaces the line provider for a source (for example "EBM"). The provider runs on the main thread when
        // the box prints; returning null or an empty string leaves that source out of this box.
        public static void Register(string source, Func<string> line)
        {
            if (string.IsNullOrEmpty(source) || line == null) return;
            Unregister(source);
            Providers.Add(new KeyValuePair<string, Func<string>>(source, line));
            StatusBannerDriver.Ensure();
        }

        public static void Unregister(string source)
        {
            Providers.RemoveAll(provider => provider.Key == source);
        }

        internal static void Tick()
        {
            if (_enabled == null || !_enabled.Value) return;

            float now = Time.unscaledTime;
            if (now < _nextEmitAt) return;
            _nextEmitAt = now + _seconds.Value;
            EmitIfChanged();
        }

        private static void EmitIfChanged()
        {
            int sourceWidth = 0;
            foreach (var provider in Providers) sourceWidth = Math.Max(sourceWidth, provider.Key.Length);

            Lines.Clear();
            foreach (var provider in Providers)
            {
                string text;
                try { text = provider.Value(); }
                catch (Exception ex) { text = $"status line failed: {ex.Message}"; }
                if (string.IsNullOrEmpty(text)) continue;
                AddWrapped(provider.Key.PadRight(sourceWidth) + SourceGap, text);
            }
            if (Lines.Count == 0) return;

            string joined = string.Join("\n", Lines);
            if (joined == _lastPrinted) return;
            _lastPrinted = joined;

            string title = $"{TitlePrefix} {FormatUptime(Time.realtimeSinceStartup)}";
            int innerWidth = title.Length + TitleMargin;
            foreach (string line in Lines) innerWidth = Math.Max(innerWidth, line.Length);
            LoadSummary.EmitBox(title, Lines.ToArray(), Tag, Math.Min(innerWidth, MaxInnerWidth));
        }

        // A line wider than the box continues on the next rows, indented under its text, instead of being cut off at the edge.
        private static void AddWrapped(string prefix, string text)
        {
            // A provider name long enough to eat the whole row would leave room at zero or negative, and LastIndexOf
            // would throw on that. Keep at least this much text per row so a status box can never become an exception.
            const int MinRoom = 12;
            int room = Math.Max(MinRoom, MaxInnerWidth - prefix.Length);
            string indent = new string(' ', prefix.Length);
            while (text.Length > room)
            {
                int cut = text.LastIndexOf(' ', room);
                if (cut <= 0) cut = room;
                Lines.Add(prefix + text.Substring(0, cut).TrimEnd());
                text = text.Substring(cut).TrimStart();
                prefix = indent;
            }
            Lines.Add(prefix + text);
        }

        private static string FormatUptime(float seconds)
        {
            var uptime = TimeSpan.FromSeconds(seconds);
            return uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}:{uptime.Minutes:00}:{uptime.Seconds:00}"
                : $"{uptime.Minutes}:{uptime.Seconds:00}";
        }
    }

    internal sealed class StatusBannerDriver : MonoBehaviour
    {
        private static StatusBannerDriver _instance;

        internal static void Ensure()
        {
            if (_instance != null) return;
            var host = new GameObject("FiresStatusBanner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<StatusBannerDriver>();
        }

        private void Update() => StatusBanner.Tick();
    }
}
