// Fires UI Shell, Core step 0/1: window memory (FIRES_UI_SHELL.md S5).
// One file, BepInEx/cache/FiresCore/ui_windows.json (per machine: config/ is copied by r2modman profile export).
// Newtonsoft (JsonUtility drops nested collections silently), written on close only to a temp file then renamed, and read
// back after the save. Rects are clamped back on screen at load. Used only while [UI] Fires UI override is ON.
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    public static class ShellWindows
    {
        public sealed class WindowState
        {
            public float X, Y, W, H;     // anchoredPosition (centre-anchored) + size, UI units
            public float Scale = 1f;     // canvas scale factor it was saved at
            public string Tab;           // hub only: the last tab
        }

        private const string FileName = "ui_windows.json";
        private static Dictionary<string, WindowState> s_states;

        private static string Dir => Path.Combine(Paths.CachePath, "FiresCore");
        private static string FilePath => Path.Combine(Dir, FileName);

        private static Dictionary<string, WindowState> States
        {
            get
            {
                if (s_states != null) return s_states;
                s_states = new Dictionary<string, WindowState>(StringComparer.Ordinal);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var loaded = JsonConvert.DeserializeObject<Dictionary<string, WindowState>>(File.ReadAllText(FilePath));
                        if (loaded != null) foreach (var kv in loaded) if (kv.Value != null) s_states[kv.Key] = kv.Value;
                        ShellLog.Debug("window memory: loaded " + s_states.Count + " window(s) from " + FilePath);
                    }
                }
                catch (Exception e) { ShellLog.Warn("window memory: could not read " + FilePath + " (" + e.GetType().Name + ": " + e.Message + "); starting empty"); }
                return s_states;
            }
        }

        /// <summary>The saved state for a window, with its rect clamped to 'area' (UI units); null when none is saved.</summary>
        public static WindowState Restore(string id, Vector2 area, Vector2 minSize)
        {
            if (string.IsNullOrEmpty(id) || !States.TryGetValue(id, out var s)) return null;
            var c = new WindowState { X = s.X, Y = s.Y, W = s.W, H = s.H, Scale = s.Scale, Tab = s.Tab };
            c.W = Mathf.Clamp(c.W, minSize.x, Mathf.Max(minSize.x, area.x));
            c.H = Mathf.Clamp(c.H, minSize.y, Mathf.Max(minSize.y, area.y));
            float maxX = Mathf.Max(0f, (area.x - c.W) * 0.5f), maxY = Mathf.Max(0f, (area.y - c.H) * 0.5f);
            c.X = Mathf.Clamp(c.X, -maxX, maxX);
            c.Y = Mathf.Clamp(c.Y, -maxY, maxY);
            return c;
        }

        /// <summary>Stores a window's state and writes the file (temp + rename + read-back).</summary>
        public static void Save(string id, WindowState state)
        {
            if (string.IsNullOrEmpty(id) || state == null) return;
            States[id] = state;
            Write();
        }

        /// <summary>Forgets every window (the future "Reset window positions" button).</summary>
        public static void ResetAll()
        {
            States.Clear();
            Write();
        }

        private static void Write()
        {
            string tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonConvert.SerializeObject(States, Formatting.Indented);
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);

                // read back: the file must parse and hold every window we just wrote
                var back = JsonConvert.DeserializeObject<Dictionary<string, WindowState>>(File.ReadAllText(FilePath));
                int n = back != null ? back.Count : -1;
                if (n != States.Count) ShellLog.Warn("window memory: read-back has " + n + " window(s), wrote " + States.Count);
                else ShellLog.Debug("window memory: saved " + n + " window(s) to " + FilePath + " (read back OK)");
            }
            catch (Exception e)
            {
                ShellLog.Warn("window memory: save failed (" + e.GetType().Name + ": " + e.Message + ")");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
