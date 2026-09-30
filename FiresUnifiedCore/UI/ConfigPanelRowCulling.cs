using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Only the rows near the body's scroll view are drawn ([perf], 2026-09-28: the window's first open cost 2 s).
    /// A mod holds up to hundreds of settings, and every IMGUI event laid out every one of them, wrapped description
    /// and all, so the first open measured and generated text for all of them at once. A row far from the view is
    /// laid out as space of its last measured height (an estimate until it has been drawn once). Which rows are drawn
    /// is decided in each Layout event and kept for the event that follows it, as IMGUI needs the same controls in both.
    /// A row's measured position counts only if it was recorded after the view last changed (another mod, a search, a
    /// filter, a section opened or closed); until then the running total of the rows above it stands in.
    /// </summary>
    public partial class ConfigPanel
    {
        private const float RowCullMarginBase = 240f;
        private const float EstimatedRowHeightBase = 46f;

        private readonly Dictionary<CfgDescriptor, Rect> _rowRects = new Dictionary<CfgDescriptor, Rect>();
        private readonly Dictionary<CfgDescriptor, int> _rowSeenFrames = new Dictionary<CfgDescriptor, int>();
        private readonly HashSet<CfgDescriptor> _culledRows = new HashSet<CfgDescriptor>();
        private float _rowViewHeight;
        private float _rowCursor;
        private string _rowViewKey;
        private int _rowPositionsFrom;

        // At the top of the body's scroll view, every event: the view's height and what it shows. In a Layout event the
        // running content y restarts.
        private void BeginRowCulling(float viewHeight)
        {
            _rowViewHeight = viewHeight;
            if (Event.current.type != EventType.Layout) return;
            _rowCursor = 0f;
            string viewKey = (IsSearching ? "?" + _search : "m" + _mod) + (FiresConfigUI.ShowAdvanced ? "|a" : "|") + (KeybindsOnly ? "k" : "");
            if (viewKey == _rowViewKey) return;
            _rowViewKey = viewKey;
            InvalidateRowPositions();
        }

        private void InvalidateRowPositions() => _rowPositionsFrom = Time.frameCount;

        private void DrawRowCulled(CfgDescriptor descriptor)
        {
            var type = Event.current.type;
            if (type == EventType.Layout) DecideRow(descriptor);
            if (_culledRows.Contains(descriptor))
            {
                GUILayout.Space(RowHeight(descriptor));
                if (type == EventType.Repaint) RememberRow(descriptor, GUILayoutUtility.GetLastRect(), keepHeight: true);
                return;
            }
            GUILayout.BeginVertical();
            DrawRow(descriptor);
            GUILayout.EndVertical();
            if (type == EventType.Repaint) RememberRow(descriptor, GUILayoutUtility.GetLastRect(), keepHeight: false);
        }

        // Where the row stood at a repaint since the view last changed (or else just after the row before it), against
        // the view and a margin above and below it.
        private void DecideRow(CfgDescriptor descriptor)
        {
            if (_rowRects.TryGetValue(descriptor, out var known) && _rowSeenFrames.TryGetValue(descriptor, out int seen)
                && seen >= _rowPositionsFrom)
                _rowCursor = known.y;
            float height = RowHeight(descriptor), margin = Px(RowCullMarginBase);
            bool near = _rowCursor + height >= _bodyScroll.y - margin && _rowCursor <= _bodyScroll.y + _rowViewHeight + margin;
            if (near) _culledRows.Remove(descriptor);
            else _culledRows.Add(descriptor);
            _rowCursor += height;
        }

        private float RowHeight(CfgDescriptor descriptor)
            => _rowRects.TryGetValue(descriptor, out var known) && known.height > 0f ? known.height : Px(EstimatedRowHeightBase);

        private void RememberRow(CfgDescriptor descriptor, Rect rect, bool keepHeight)
        {
            if (keepHeight && _rowRects.TryGetValue(descriptor, out var known)) rect.height = known.height;
            else if (keepHeight) rect.height = 0f;
            _rowRects[descriptor] = rect;
            _rowSeenFrames[descriptor] = Time.frameCount;
        }

        private void ForgetRowSizes()
        {
            _rowRects.Clear();
            _rowSeenFrames.Clear();
            _culledRows.Clear();
            _rowViewKey = null;
        }
    }
}
