// Fires UI Shell, Core step 0/1: the in-game IShellHost (FIRES_UI_SHELL.md S6). Fonts are the game's already-loaded Averia
// Font objects (Resources.FindObjectsOfTypeAll<Font>), never a bundled font asset: the ripped Averia TMP asset drags ~24 MB of
// Noto CJK fallbacks with it. KitText turns these into runtime TMP SDF assets, or falls back to
// legacy Text per role when CreateFontAsset fails.
// Created only while [UI] Fires UI override is ON (ShellRuntime).
using System;
using System.Collections.Generic;
using FiresCore.UI.Shell.Kit;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    internal sealed class GameShellHost : IShellHost
    {
        private readonly Dictionary<FontRole, Font> _fonts = new Dictionary<FontRole, Font>();
        private bool _scanned;

        public Font GetFont(FontRole role)
        {
            if (!_scanned) Scan();
            return _fonts.TryGetValue(role, out var f) ? f : null;
        }

        public float Time => UnityEngine.Time.unscaledTime;

        public Vector2 MouseNormalized
        {
            get
            {
                var p = UnityEngine.Input.mousePosition;
                float w = Mathf.Max(1f, Screen.width), h = Mathf.Max(1f, Screen.height);
                return new Vector2(Mathf.Clamp((p.x / w) * 2f - 1f, -1f, 1f), Mathf.Clamp((p.y / h) * 2f - 1f, -1f, 1f));
            }
        }

        public bool IsGamepad
        {
            get { try { return ZInput.IsGamepadActive(); } catch { return false; } }
        }

        /// <summary>Forget the font lookup (the shell is shutting down, or a scene swapped the loaded fonts).</summary>
        public void Reset() { _fonts.Clear(); _scanned = false; }

        private void Scan()
        {
            _scanned = true;
            Font[] all;
            try { all = Resources.FindObjectsOfTypeAll<Font>(); }
            catch (Exception e) { ShellLog.Warn("font scan failed: " + e.Message); return; }

            Pick(all, FontRole.SerifRegular, FiresTokens.SerifRegularName, "AveriaSerifLibre", false);
            Pick(all, FontRole.SerifBold, FiresTokens.SerifBoldName, "AveriaSerifLibre", true);
            Pick(all, FontRole.SansRegular, FiresTokens.SansRegularName, "AveriaSansLibre", false);
            Pick(all, FontRole.SansBold, FiresTokens.SansBoldName, "AveriaSansLibre", true);

            // Fallbacks: a missing weight uses the other weight of the family, then the other family.
            Fill(FontRole.SerifBold, FontRole.SerifRegular);
            Fill(FontRole.SerifRegular, FontRole.SerifBold);
            Fill(FontRole.SansBold, FontRole.SansRegular);
            Fill(FontRole.SansRegular, FontRole.SansBold);
            Fill(FontRole.SerifRegular, FontRole.SansRegular);
            Fill(FontRole.SerifBold, FontRole.SansBold);
            Fill(FontRole.SansRegular, FontRole.SerifRegular);
            Fill(FontRole.SansBold, FontRole.SerifBold);

            foreach (FontRole r in Enum.GetValues(typeof(FontRole)))
                ShellLog.Debug("font " + r + " = " + (_fonts.TryGetValue(r, out var f) && f != null ? f.name + (f.dynamic ? " (dynamic)" : " (static)") : "MISSING"));
        }

        private void Pick(Font[] all, FontRole role, string exact, string family, bool bold)
        {
            Font best = null;
            foreach (var f in all)
                if (f != null && string.Equals(f.name, exact, StringComparison.OrdinalIgnoreCase)) { best = f; break; }
            if (best == null)
            {
                foreach (var f in all)
                {
                    if (f == null || f.name.IndexOf(family, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool isBold = f.name.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isItalic = f.name.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isItalic || isBold != bold) continue;
                    best = f; break;
                }
            }
            if (best != null) _fonts[role] = best;
        }

        private void Fill(FontRole missing, FontRole from)
        {
            if (_fonts.ContainsKey(missing)) return;
            if (_fonts.TryGetValue(from, out var f) && f != null) _fonts[missing] = f;
        }
    }
}
