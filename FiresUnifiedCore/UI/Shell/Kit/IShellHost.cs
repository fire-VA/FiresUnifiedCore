// Fires UI kit, step 0. The game-only needs of the kit (FIRES_UI_SHELL.md S6). Core implements it in game
// (loaded Averia Font objects, Time.unscaledTime, ZInput); the Unity mockup uses a stub with the rip's TTFs and a
// fixed time. Byte-identical copy in ValheimRip1.0/Assets/FiresUIKit/Runtime.
// ASCII-only (10-14): non-ASCII characters are backslash-u escapes in strings and ASCII words in comments. Core UI/Shell/Kit and
// ValheimRip1.0/Assets/FiresUIKit/Runtime hold byte-identical copies (check with cmp).
using UnityEngine;

namespace FiresCore.UI.Shell.Kit
{
    public interface IShellHost
    {
        /// <summary>The loaded Font for a role (Serif/Sans, regular/bold). Never a bundled font asset.</summary>
        Font GetFont(FontRole role);

        /// <summary>Unscaled seconds; drives the parallax drift. A mockup returns a fixed value.</summary>
        float Time { get; }

        /// <summary>Mouse position relative to the screen centre, each axis in -1..1.</summary>
        Vector2 MouseNormalized { get; }

        /// <summary>True when the last input came from a gamepad (footer hints switch to pad glyph names).</summary>
        bool IsGamepad { get; }
    }
}
