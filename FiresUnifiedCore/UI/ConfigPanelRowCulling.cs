using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// 0.2.832 (Fire 10-26, F1 FPS): the body is a row model, built once per view and drawn virtualised. The model is the
    /// flat list of what the body shows (section bars, search group labels and rows) with each item's height; it is
    /// rebuilt only when the view changes (another mod, a search, a filter, a section opened or closed, new discovery)
    /// or once a second for tags and "changed" colours, never per IMGUI event. Each Layout event finds the items inside
    /// the scroll view (plus a margin) by binary search over the running heights and keeps that range for the events
    /// that follow it, as IMGUI needs the same controls in both. Everything above and below the range is ONE space each,
    /// so a mod with hundreds of settings lays out about twenty controls, not hundreds. A drawn item's height is measured
    /// at Repaint; an item not drawn yet uses an estimate until it has been.
    /// </summary>
    public partial class ConfigPanel
    {
        private const float RowCullMarginBase = 240f;
        private const float EstimatedRowHeightBase = 46f;
        private const float EstimatedBarHeightBase = 30f;
        private const float ModelRefreshSeconds = 1f;

        private enum BodyItemKind : byte { Row, SectionBar, GroupLabel }

        private struct BodyItem
        {
            public BodyItemKind Kind;
            public CfgDescriptor Descriptor;   // Row
            public string Text;                // SectionBar / GroupLabel: the built label
            public string Key;                 // SectionBar: the collapse key
            public float Height;               // measured (or estimated) height, incl. a bar's 3 px gap
            public bool Measured;
        }

        private readonly List<BodyItem> _items = new List<BodyItem>();
        private float[] _itemTops = new float[64];   // _itemTops[i] = y of item i; _itemTops[Count] = total height
        private bool _topsDirty = true, _modelDirty = true;
        private string _rowViewKey;
        private float _modelBuiltAt = -10f;
        private float _rowViewHeight;
        private int _visFirst, _visEnd;   // [first, end) drawn this Layout and the events after it
        private readonly Dictionary<CfgDescriptor, float> _knownHeights = new Dictionary<CfgDescriptor, float>();
        private readonly Dictionary<string, float> _knownBarHeights = new Dictionary<string, float>();

        // At the top of the body's scroll view, every event. In a Layout event: the model is rebuilt if the view changed
        // (or its refresh is due), and the visible range is chosen.
        private void BeginRowCulling(float viewHeight)
        {
            _rowViewHeight = viewHeight;
            if (Event.current.type != EventType.Layout) return;
            string viewKey = (IsSearching ? "?" + _search : "m" + _mod) + (FiresConfigUI.ShowAdvanced ? "|a" : "|") + (KeybindsOnly ? "k" : "");
            if (viewKey != _rowViewKey) { _rowViewKey = viewKey; _modelDirty = true; }
            if (_modelDirty || Time.unscaledTime - _modelBuiltAt >= ModelRefreshSeconds) BuildModel();
            if (_topsDirty) BuildTops();
            if (_pendingJump != null) JumpToBar(_pendingJump);
            ChooseRange();
        }

        private void InvalidateRowPositions() => _modelDirty = true;

        private void ForgetRowSizes()
        {
            _knownHeights.Clear();
            _knownBarHeights.Clear();
            _rowNames.Clear();
            _items.Clear();
            _rowViewKey = null;
            _modelDirty = _topsDirty = true;
            _visFirst = _visEnd = 0;
        }

        // ---------------------------------------------------------------- model
        private void BuildModel()
        {
            _modelDirty = false;
            _modelBuiltAt = Time.unscaledTime;
            _items.Clear();
            if (IsSearching) BuildSearchModel();
            else if (_mod != null) BuildModModel();
            _topsDirty = true;
        }

        private void BuildModModel()
        {
            var sections = CfgDiscovery.SectionsOf(_mod);
            for (int i = 0; i < sections.Count; i++)
            {
                string section = sections[i];
                var rows = CfgDiscovery.RowsOf(_mod, section);
                foreach (var descriptor in rows) descriptor.RefreshTags();
                if (!HasVisibleRows(rows)) continue;

                string key = CollapseKey(_mod, section);
                if (_seenSections.Add(key)) _collapsed.Add(key);
                bool collapsed = _collapsed.Contains(key) && string.IsNullOrEmpty(_search);   // while searching, every section shows its matches
                string barText = (collapsed ? "[+]  " : "[-]  ") + "<b>" + (i + 1) + ". " + CfgDiscovery.CleanSection(section) + "</b>";
                barText = "<color=" + (collapsed && HasChangedRows(rows) ? ChangedColor : SectionColor) + ">" + barText + "</color>";
                AddBar(BodyItemKind.SectionBar, barText, key);
                if (collapsed) continue;
                foreach (var descriptor in rows)
                    if (!IsRowHidden(descriptor)) AddRow(descriptor);
            }
        }

        private void BuildSearchModel()
        {
            _searchResults.Clear();
            string query = _search.ToLowerInvariant();
            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                descriptor.RefreshTags();
                if (IsRowHidden(descriptor)) continue;
                if (Matches(descriptor, query)) _searchResults.Add(descriptor);
            }
            string lastGroup = null;
            foreach (var descriptor in _searchResults)
            {
                string group = descriptor.ModName + "  -  " + descriptor.SectionDisplay;
                if (group != lastGroup)
                {
                    lastGroup = group;
                    AddBar(BodyItemKind.GroupLabel, "<b>" + group + "</b>", group);
                }
                AddRow(descriptor);
            }
        }

        private void AddBar(BodyItemKind kind, string text, string key)
        {
            bool known = _knownBarHeights.TryGetValue(key, out float h);
            _items.Add(new BodyItem { Kind = kind, Text = text, Key = key, Height = known ? h : Px(EstimatedBarHeightBase), Measured = known });
        }

        private void AddRow(CfgDescriptor descriptor)
        {
            bool known = _knownHeights.TryGetValue(descriptor, out float h);
            _items.Add(new BodyItem { Kind = BodyItemKind.Row, Descriptor = descriptor, Height = known ? h : Px(EstimatedRowHeightBase), Measured = known });
        }

        private void BuildTops()
        {
            _topsDirty = false;
            int n = _items.Count;
            if (_itemTops.Length < n + 1) _itemTops = new float[Mathf.NextPowerOfTwo(n + 1)];
            float y = 0f;
            for (int i = 0; i < n; i++) { _itemTops[i] = y; y += _items[i].Height; }
            _itemTops[n] = y;
        }

        private void JumpToBar(string key)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Kind != BodyItemKind.SectionBar || _items[i].Key != key) continue;
                _bodyScroll.y = Mathf.Max(0f, _itemTops[i] - 6f);
                break;
            }
            _pendingJump = null;
        }

        // Binary search for the first item ending below the view's top (less the margin), then walk to the bottom.
        private void ChooseRange()
        {
            int n = _items.Count;
            float margin = Px(RowCullMarginBase);
            float top = _bodyScroll.y - margin, bottom = _bodyScroll.y + _rowViewHeight + margin;
            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_itemTops[mid + 1] < top) lo = mid + 1; else hi = mid;
            }
            int end = lo;
            while (end < n && _itemTops[end] <= bottom) end++;
            _visFirst = lo;
            _visEnd = end;
        }

        // ---------------------------------------------------------------- draw
        // The model's visible range between two spaces. Returns the number of items in the model.
        private int DrawModelBody()
        {
            var type = Event.current.type;
            int n = _items.Count;
            int first = Mathf.Min(_visFirst, n), end = Mathf.Min(_visEnd, n);
            ProbeRows(end - first, n - (end - first));   // [CfgWin] Debug line
            if (_itemTops[first] > 0f) GUILayout.Space(_itemTops[first]);
            for (int i = first; i < end; i++)
            {
                var item = _items[i];
                GUILayout.BeginVertical();
                if (item.Kind == BodyItemKind.Row) DrawRow(item.Descriptor);
                else
                {
                    GUILayout.Space(3f);
                    if (item.Kind == BodyItemKind.GroupLabel) GUILayout.Label(item.Text, ConfigSkin.SectionBar);
                    else if (GUILayout.Button(item.Text, ConfigSkin.SectionBar))
                    {
                        if (!_collapsed.Remove(item.Key)) _collapsed.Add(item.Key);
                        InvalidateRowPositions();
                    }
                }
                GUILayout.EndVertical();
                if (type == EventType.Repaint) Measure(i, GUILayoutUtility.GetLastRect().height);
            }
            float below = _itemTops[n] - _itemTops[end];
            if (below > 0f) GUILayout.Space(below);
            return n;
        }

        private void Measure(int i, float height)
        {
            if (height <= 0f) return;
            var item = _items[i];
            if (item.Measured && Mathf.Abs(item.Height - height) < 0.5f) return;
            item.Height = height;
            item.Measured = true;
            _items[i] = item;
            if (item.Kind == BodyItemKind.Row) _knownHeights[item.Descriptor] = height;
            else _knownBarHeights[item.Key] = height;
            _topsDirty = true;
        }
    }
}
