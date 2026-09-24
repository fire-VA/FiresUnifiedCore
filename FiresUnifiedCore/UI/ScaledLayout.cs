using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Cached GUILayout options for the config windows.
    ///
    /// <see cref="GUILayout.Width"/> returns a freshly allocated GUILayoutOption, and every GUILayout call that
    /// takes options allocates the params array to carry them, so writing them inline costs a throwaway object
    /// per option per IMGUI event — and IMGUI raises several events per frame. A Unity Profiler session with the
    /// panel open measured 172 MB in 3,553,228 allocations from one row-drawing method alone.
    ///
    /// Keyed on the FINAL pixel value, so a call site passes exactly what it already passed to GUILayout and a
    /// window-scale change simply starts using different keys. **Only for sizes that are constant at a given
    /// scale**: keying on a live window dimension would add an entry per pixel of a resize drag, so the handful
    /// of call sites that size themselves from the window still build their option inline.
    /// </summary>
    internal static class ScaledLayout
    {
        private static readonly Dictionary<float, GUILayoutOption[]> _widths =
            new Dictionary<float, GUILayoutOption[]>();
        private static readonly Dictionary<float, GUILayoutOption[]> _heights =
            new Dictionary<float, GUILayoutOption[]>();

        private static GUILayoutOption[] _expandedWidth;

        public static GUILayoutOption[] ExpandedWidth =>
            _expandedWidth ?? (_expandedWidth = new[] { GUILayout.ExpandWidth(true) });

        public static GUILayoutOption[] Width(float pixels)
        {
            if (_widths.TryGetValue(pixels, out var option)) return option;
            option = new[] { GUILayout.Width(pixels) };
            _widths[pixels] = option;
            return option;
        }

        public static GUILayoutOption[] Height(float pixels)
        {
            if (_heights.TryGetValue(pixels, out var option)) return option;
            option = new[] { GUILayout.Height(pixels) };
            _heights[pixels] = option;
            return option;
        }
    }
}
