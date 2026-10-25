// the layout-mode look.
// One screen-space overlay canvas above the HUD draws, for every shown HUD-layer element: a dashed gold outline on a dark backing
// with rounded corners, and its name tag (rounded well pill) placed on the first clear spot; docked groups get a shared outline plus
// the group grip on the far right edge; the element being dragged is outlined bright. A top-centre banner explains the controls and
// carries Reset layout / Done. The canvas has no raycaster except on the banner, so drags still reach the elements' own hit areas.
// Built only while HUD layout mode is on; nodes are reused and rebuilt only when an element's size changes.
using System.Collections.Generic;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;
using T = FiresCore.UI.Shell.Kit.FiresTokens;
using W = FiresCore.UI.Shell.Kit.Widgets;

namespace FiresCore.UI.Dock
{
    internal static class DockLayoutOverlay
    {
        private const float Dash = 8f, Gap = 5f, Rad = 10f, TagH = 20f;
        private static Color Dark => T.WithAlpha(T.Backdrop, 0.88f);
        private static Color OutlineCol => T.WithAlpha(T.Gold, 0.6f);

        private sealed class Node { public RectTransform Root, Outline, Tag; public Vector2 Size; public bool Bright; public float TagW; }

        private static GameObject s_canvasGo;
        private static RectTransform s_root, s_chrome;
        private static readonly Dictionary<string, Node> s_nodes = new Dictionary<string, Node>();
        private static readonly Dictionary<string, RectTransform> s_groups = new Dictionary<string, RectTransform>();
        private static readonly List<Rect> s_taken = new List<Rect>();
        private static bool s_wasOn;

        private static float Sf => Screen.height > 0 ? Screen.height / 1080f : 1f;   // canvas units = 1080p reference

        internal static void Tick()
        {
            TickPill();
            // the inventory editor gets the same outlines / tags / handles (layer "inventory"); banner + legend stay HUD-only
            string layer = !FiresCore.UI.Shell.FiresShell.OverrideOn ? null : DockService.HudEditMode ? "hud" : DockService.InventoryEditMode ? "inventory" : DockService.ScreenEditLayer;   // + a Fires window own layer
            bool on = layer != null;
            if (!on)
            {
                if (s_wasOn && s_canvasGo != null) s_canvasGo.SetActive(false);
                s_legendDragged.x = float.NaN; s_legendSig = null;   // the next editor session places the legend afresh
                s_wasOn = false;
                return;
            }
            Ensure();
            if (!s_wasOn)
            {
                s_canvasGo.SetActive(true); s_wasOn = true; s_selfTestAt = Time.unscaledTime + 0.6f;
                // the HUD editor draws corner grips on the selected element only: the self-test selects one shown element to have grips to test
                if (DockService.Selected == null)
                    foreach (var se in DockService.All)
                        if (se.O.Layer == layer && !se.O.Passive && se.O.Rect != null && DockService.IsShownPublic(se)) { DockService.Selected = se.O.Id; break; }
            }
            if (s_selfTestAt > 0f && Time.unscaledTime >= s_selfTestAt) { s_selfTestAt = -1f; GripSelfTest(); }
            if (UnityEngine.Input.GetMouseButtonDown(0)) RouteBlockedClick();
            bool hudLayer = layer == "hud";
            if (s_chrome.gameObject.activeSelf != hudLayer) s_chrome.gameObject.SetActive(hudLayer);

            float sf = Sf;
            var dragged = DockService.DraggedElement;
            var seen = new HashSet<string>();
            var groupRects = new Dictionary<string, Rect>(); var groupCount = new Dictionary<string, int>();
            var shown = new List<(DockService.Element e, Rect r)>();
            foreach (var e in DockService.All)
            {
                if ((e.O.Layer != layer && !(layer == "inventory" && e.O.Layer == "hud" && e.O.AlsoInInventoryLayout)) || (e.O.Passive && !e.O.OutlineOnly) || e.O.Rect == null || !DockService.IsShownPublic(e)) continue;   // + the HUD elements movable from the inventory session
                var px = DockService.VisibleScreenRect(e);   // the outline follows what is actually drawn, live (also while dragging)
                var r = ClampScreen(new Rect(px.x / sf, px.y / sf, Mathf.Max(8f, px.width / sf), Mathf.Max(8f, px.height / sf)));   // outlines stay on screen
                shown.Add((e, r));
                if (e.Group != null)
                {
                    groupRects[e.Group] = groupCount.TryGetValue(e.Group, out int n) && n > 0 ? Union(groupRects[e.Group], r) : r;
                    groupCount[e.Group] = (n) + 1;
                }
            }

            // outlines first, then tags (tags avoid every outline line and earlier tags)
            foreach (var (e, r) in shown)
            {
                seen.Add(e.O.Id);
                if (!s_nodes.TryGetValue(e.O.Id, out var node)) { node = new Node { Root = W.Node(s_root, "El_" + e.O.Id) }; s_nodes[e.O.Id] = node; }
                node.Root.gameObject.SetActive(true);
                bool bright = dragged == e || e.O.Id == DockService.Selected;
                if (bright) node.Root.SetAsLastSibling();
                if (node.Outline == null || Mathf.Abs(node.Size.x - r.width) > 1f || Mathf.Abs(node.Size.y - r.height) > 1f || node.Bright != bright)
                {
                    if (node.Outline != null) Object.Destroy(node.Outline.gameObject);
                    node.Outline = DashedRect(node.Root, "Outline", r.size, bright ? T.GoldBright : OutlineCol, bright ? 2f : 1.5f, Rad);
                    node.Size = r.size; node.Bright = bright;
                }
                W.Place(node.Root, r.x, r.y, r.width, r.height);
            }
            // tags keep off grips, the selected element's corner handles, the
            // legend and the banner / buttons, as well as outlines and earlier tags.
            if (hudLayer) TickLegend(); else if (s_legend != null) s_legend.gameObject.SetActive(false);
            s_taken.Clear(); s_groupLines.Clear();
            float uiWk = Screen.width / sf;
            if (hudLayer) s_taken.Add(new Rect(uiWk * 0.5f - 210f, 44f, 420f, 82f));   // banner (y 48, 32 high) + Reset / Cancel / Save (y 90, 32 high)
            if (hudLayer && s_legend != null) { var lp = s_legend.anchoredPosition; s_taken.Add(new Rect(lp.x - 4f, -lp.y - 4f, s_legend.sizeDelta.x + 8f, s_legend.sizeDelta.y + 8f)); }
            foreach (var kv in groupCount)
            {
                if (kv.Value < 2) continue;
                var gu = groupRects[kv.Key]; var g0 = ClampScreen(new Rect(gu.x - 6f, gu.y - 6f, gu.width + 12f, gu.height + 12f));
                s_taken.Add(new Rect(Mathf.Min(g0.xMax - 3f, uiWk - 8f) - 4f, g0.center.y - 20f, 14f, 40f));
                s_groupLines.Add(g0);   // tags keep off the shared group outline too
            }
            // handles: the selected / dragged element on the HUD; every panel in the inventory editor (each has corner grips there)
            var handleRects = new List<Rect>(); var handleEls = new List<DockService.Element>();
            foreach (var (e, r) in shown) if (!hudLayer || e.O.Id == DockService.Selected || dragged == e || e.O.Id == DockService.Picked) if (!e.O.Passive) { handleRects.Add(r); handleEls.Add(e); }
            foreach (var hr in handleRects) for (int i = 0; i < 4; i++) s_taken.Add(new Rect((i % 2 == 0 ? hr.x : hr.xMax) - 8f, (i < 2 ? hr.y : hr.yMax) - 8f, 16f, 16f));
            foreach (var (e, r) in shown)
            {
                var node = s_nodes[e.O.Id];
                if (node.Tag == null) { node.Tag = NameTag(s_root, "Tag_" + e.O.Id, e.O.Label ?? e.O.Id, out node.TagW); string id = e.O.Id; Clickable(node.Tag, () => DockService.Selected = id); }
                Rect? grp = null;   // the member's group outline (2+ shown members), for the outside-the-group spots
                if (e.Group != null && groupCount.TryGetValue(e.Group, out int gc) && gc >= 2) { var gu2 = groupRects[e.Group]; grp = ClampScreen(new Rect(gu2.x - 6f, gu2.y - 6f, gu2.width + 12f, gu2.height + 12f)); }
                bool hasHandles = !hudLayer || e.O.Id == DockService.Selected || dragged == e || e.O.Id == DockService.Picked;
                var spot = TagSpot(r, node.TagW, shown, grp, hasHandles);
                W.Place(node.Tag, spot.x, spot.y, node.TagW, TagH);
                node.Tag.gameObject.SetActive(true);
                s_taken.Add(spot);
            }
            foreach (var kv in s_nodes)
                if (!seen.Contains(kv.Key)) { kv.Value.Root.gameObject.SetActive(false); if (kv.Value.Tag != null) kv.Value.Tag.gameObject.SetActive(false); }

            // docked groups (2+ shown members): shared outline 6 px out, group grip on the far right edge
            var liveGroups = new HashSet<string>();
            foreach (var kv in groupCount)
            {
                if (kv.Value < 2) continue;
                liveGroups.Add(kv.Key);
                var u = groupRects[kv.Key];
                var gr = ClampScreen(new Rect(u.x - 6f, u.y - 6f, u.width + 12f, u.height + 12f));
                if (s_groups.TryGetValue(kv.Key, out var g) && g != null && Vector2.Distance(g.sizeDelta, gr.size) > 1f) { Object.Destroy(g.gameObject); g = null; }
                if (g == null)
                {
                    g = W.Node(s_root, "Group_" + kv.Key);
                    var o = DashedRect(g, "Outline", gr.size, T.WithAlpha(T.GoldBright, 0.55f), 1f, 14f);
                    var gb = W.Box(g, "GripBack", KitSprites.RoundedFill(5f), Dark); W.Place(gb.rectTransform, gr.width - 7f, gr.height * 0.5f - 20f, 14f, 40f);
                    // the group grip drags the whole group (its root carries the
                    // move, the members follow by the reflow), through the dock's own move handler; the outline line itself stays click-through
                    gb.raycastTarget = true; gb.gameObject.AddComponent<DockDragHandle>();
                    var gp = W.Box(g, "Grip", KitSprites.RoundedFill(3f), T.GoldBright); W.Place(gp.rectTransform, gr.width - 3f, gr.height * 0.5f - 16f, 6f, 32f);
                    s_groups[kv.Key] = g;
                }
                g.gameObject.SetActive(true);
                W.Place(g, gr.x, gr.y, gr.width, gr.height);
                var gdh = g.GetComponentInChildren<DockDragHandle>(true);
                if (gdh != null) { DockService.Element root = null; foreach (var (me, _) in shown) if (me.Group == kv.Key && (root == null || me.Anchor == null)) root = me; gdh.Id = root != null ? root.O.Id : null; }
                g.SetAsFirstSibling();
            }
            foreach (var kv in s_groups) if (kv.Value != null && !liveGroups.Contains(kv.Key)) kv.Value.gameObject.SetActive(false);
            Handles(handleRects, handleEls);
            Links(shown);
            s_chrome.SetAsLastSibling();
        }

        // ---- corner handles on the selected element (the mock's Handles: 10 px gold, dark back, backdrop edge) ----
        private static readonly List<RectTransform> s_handleSets = new List<RectTransform>();

        /// <summary> (test hook): screen point (y up) of the drawn corner square <paramref name="corner"/> of <paramref name="id"/>,
        /// or (-1,-1) when the editor isn't drawing it.</summary>
        internal static Vector2 CornerPoint(string id, int corner)
        {
            foreach (var set in s_handleSets)
            {
                if (set == null || !set.gameObject.activeInHierarchy || corner < 0 || corner >= set.childCount) continue;
                var w = set.GetChild(corner).GetComponentInChildren<FiresCore.UI.Shell.ShellWindowDrag>(true);
                if (w == null || w.Key != id) continue;
                var rt = (RectTransform)w.transform; var c = new Vector3[4]; rt.GetWorldCorners(c);
                return (RectTransformUtility.WorldToScreenPoint(null, c[0]) + RectTransformUtility.WorldToScreenPoint(null, c[2])) * 0.5f;
            }
            return new Vector2(-1f, -1f);
        }
        // the grips that took the drag
        // were hidden hit areas INSIDE each element, on the element's own canvas (the HUD camera canvas / panel canvases), while the
        // squares the player sees are drawn here, on this overlay canvas. Two objects for one handle, on different canvases, and the visible one
        // took no input. Now the DRAWN corner square IS the grip: its 16 px back plate is the hit area, on this one overlay canvas
        // (ScreenSpaceOverlay, sorting 31000, its own GraphicRaycaster, ranked above every camera canvas), with the full pointer chain.
        private static void Handles(List<Rect> rects, List<DockService.Element> els)
        {
            const float s = 10f;
            while (s_handleSets.Count < rects.Count)
            {
                var set = W.Node(s_root, "Handles" + s_handleSets.Count);
                for (int i = 0; i < 4; i++)
                {
                    var c = W.Node(set, "Corner" + i);
                    var hb = W.Box(c, "HandleBack", KitSprites.RoundedFill(4f), Dark); W.Place(hb.rectTransform, -3f, -3f, s + 6f, s + 6f);
                    hb.raycastTarget = true;   // the 16 px hit area
                    DockGrip.Wire(hb.gameObject, null, i % 2 == 1 ? 1f : -1f, null, i < 2 ? -1f : 1f);   // + top corners grow upwards // Core's ShellWindowDrag (Mode.Scale)
                    var b = W.Box(c, "Handle", KitSprites.RoundedFill(2.5f), T.GoldBright); W.Place(b.rectTransform, 0f, 0f, s, s);
                    var e = W.Box(c, "HandleEdge", KitSprites.RoundedOutline(2.5f), T.Backdrop); W.Place(e.rectTransform, 0f, 0f, s, s);
                }
                s_handleSets.Add(set);
            }
            for (int k = 0; k < s_handleSets.Count; k++)
            {
                var set = s_handleSets[k];
                bool use = k < rects.Count;
                if (set.gameObject.activeSelf != use) set.gameObject.SetActive(use);
                if (!use) continue;
                var rr = rects[k];
                W.Place(set, rr.x, rr.y, rr.width, rr.height);
                for (int i = 0; i < 4; i++) { var gh = set.GetChild(i).GetComponentInChildren<FiresCore.UI.Shell.ShellWindowDrag>(true); if (gh != null) gh.Key = els[k].O.Id; }
                for (int i = 0; i < 4; i++)
                    W.Place((RectTransform)set.GetChild(i), (i % 2 == 0 ? 0f : rr.width) - s * 0.5f, (i < 2 ? 0f : rr.height) - s * 0.5f, s, s);
                set.SetAsLastSibling();
            }
        }

        // ---- link marks between docked neighbours that face each other (the mock's LinkMark: two gold rounded links) ----
        private static RectTransform s_links; private static string s_linkSig;
        private static void Links(List<(DockService.Element e, Rect r)> shown)
        {
            float gapU = DockService.GapPx + 2f;
            var marks = new List<(float cx, float cy, bool vertical)>();
            for (int i = 0; i < shown.Count; i++)
                for (int j = i + 1; j < shown.Count; j++)
                {
                    var (ea, a) = shown[i]; var (eb, b) = shown[j];
                    if (ea.Group == null || ea.Group != eb.Group) continue;
                    float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    if (ox > 0f && Mathf.Abs(a.yMax < b.yMin ? b.yMin - a.yMax : a.yMin - b.yMax) <= gapU)
                        marks.Add((Mathf.Max(a.xMin, b.xMin) + ox * 0.5f, a.yMax < b.yMin ? (a.yMax + b.yMin) * 0.5f : (b.yMax + a.yMin) * 0.5f, true));
                    else if (oy > 0f && Mathf.Abs(a.xMax < b.xMin ? b.xMin - a.xMax : a.xMin - b.xMax) <= gapU)
                        marks.Add((a.xMax < b.xMin ? (a.xMax + b.xMin) * 0.5f : (b.xMax + a.xMin) * 0.5f, Mathf.Max(a.yMin, b.yMin) + oy * 0.5f, false));
                }
            var sb = new System.Text.StringBuilder();
            foreach (var m in marks) sb.Append((int)m.cx).Append(',').Append((int)m.cy).Append(m.vertical ? 'v' : 'h').Append(';');
            string sig = sb.ToString();
            if (sig == s_linkSig && s_links != null) { s_links.SetAsLastSibling(); return; }
            s_linkSig = sig;
            if (s_links != null) Object.Destroy(s_links.gameObject);
            s_links = W.Stretch(W.Node(s_root, "Links"), 0, 0, 0, 0);
            foreach (var (cx, cy, vertical) in marks)
            {
                var lkb = W.Box(s_links, "LinkBack", KitSprites.RoundedFill(5f), Dark);
                if (vertical) W.Place(lkb.rectTransform, cx - 5.5f, cy - 12f, 11f, 24f); else W.Place(lkb.rectTransform, cx - 12f, cy - 5.5f, 24f, 11f);
                for (int k = 0; k < 2; k++)
                {
                    var lk = W.Box(s_links, "Link", KitSprites.RoundedOutline(3.5f), T.GoldBright);
                    if (vertical) W.Place(lk.rectTransform, cx - 3.5f, cy - 10f + k * 8f, 7f, 12f); else W.Place(lk.rectTransform, cx - 10f + k * 8f, cy - 3.5f, 12f, 7f);
                }
            }
        }

        // ---- the opt-in pill row: "Edit layout" (or "Save layout" while the inventory is edited), "Reset layout"
        // (asks first) and "Cancel" (inventory editing). Placed on the first spot that overlaps no dock element (the top-right
        // pill sat on the pocket-watch minimap): top centre, bottom centre, then top left.
        private static GameObject s_pillGo; private static RectTransform s_pillRow, s_editPill, s_resetPill, s_cancelPill;
        private static KitLabel s_pillLabel; private static string s_pillMode, s_pillLayer; private static Vector2 s_pillSpot = new Vector2(float.NaN, 0f);
        internal static Rect PillScreenRect => s_pillRow != null && s_pillGo != null && s_pillGo.activeSelf ? DockService.ScreenRect(s_pillRow) : Rect.zero;

        private static void TickPill()
        {
            string mode = null;
            if (FiresCore.UI.Shell.FiresShell.OverrideOn && DockService.InWorld && !DockService.HudEditMode)
            {
                if (InventoryGui.IsVisible()) mode = DockService.InventoryEditMode ? "inv-save" : "inv-edit";
                // a Fires window whose panels are registered
                else if ((s_pillLayer = DockService.ScreenEditLayer ?? DockService.OpenScreenLayer()) != null) mode = DockService.ScreenEditLayer != null ? "scr-save" : "scr-edit";
                // only the REAL Esc pause dialog, never another Menu.IsVisible popup
                else if (Menu.instance != null && Menu.instance.m_menuDialog != null && Menu.instance.m_menuDialog.gameObject.activeInHierarchy) mode = "hud-edit";
                // the pills show only while the modifier is held (default Left Alt, read live); an inventory edit
                // already under way keeps its Save / Cancel pills so it can be finished.
                var key = FiresCore.UI.Shell.FiresShell.CfgLayoutPillKey != null ? FiresCore.UI.Shell.FiresShell.CfgLayoutPillKey.Value : KeyCode.LeftAlt;
                if (mode != null && mode != "inv-save" && mode != "scr-save" && key != KeyCode.None && !FiresCore.Input.FiresKeys.GetKey(key)) mode = null;
            }
            if (mode == null) { if (s_pillGo != null && s_pillGo.activeSelf) s_pillGo.SetActive(false); s_pillMode = null; return; }
            if (s_pillGo == null)
            {
                s_pillGo = new GameObject("FiresDockEditPill", typeof(RectTransform));
                Object.DontDestroyOnLoad(s_pillGo); s_pillGo.layer = 5;
                var cv = s_pillGo.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 31002;
                var cs = s_pillGo.AddComponent<CanvasScaler>(); cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = new Vector2(1920f, 1080f); cs.matchWidthOrHeight = 1f;
                s_pillGo.AddComponent<GraphicRaycaster>();
                s_pillRow = W.Node(s_pillGo.transform, "PillRow");
                s_pillRow.anchorMin = s_pillRow.anchorMax = new Vector2(0.5f, 1f); s_pillRow.pivot = new Vector2(0.5f, 1f); s_pillRow.sizeDelta = new Vector2(130f * 3 + 16f, 32f);
                s_editPill = Button(s_pillRow, "EditLayoutPill", "Edit layout", 0f, OnPill); s_pillLabel = s_lastLabel;
                s_resetPill = Button(s_pillRow, "ResetLayoutPill", "Reset layout", 0f, OnResetPill);
                s_cancelPill = Button(s_pillRow, "CancelLayoutPill", "Cancel", 0f, () => DockService.CancelEdit(s_pillMode == "scr-save" && s_pillLayer != null ? s_pillLayer : "inventory"));
                foreach (var b in new[] { s_editPill, s_resetPill, s_cancelPill }) { b.anchorMin = b.anchorMax = new Vector2(0f, 1f); b.pivot = new Vector2(0f, 1f); }
            }
            if (!s_pillGo.activeSelf) { s_pillGo.SetActive(true); s_pillSpot.x = float.NaN; }
            bool cancel = mode == "inv-save" || mode == "scr-save";
            if (s_cancelPill.gameObject.activeSelf != cancel) s_cancelPill.gameObject.SetActive(cancel);
            s_editPill.anchoredPosition = new Vector2(0f, 0f);
            s_resetPill.anchoredPosition = new Vector2(138f, 0f);
            s_cancelPill.anchoredPosition = new Vector2(276f, 0f);
            float rowW = cancel ? 130f * 3 + 16f : 130f * 2 + 8f;
            if (s_pillMode != mode || float.IsNaN(s_pillSpot.x))
            {
                s_pillMode = mode;
                if (s_pillLabel != null) s_pillLabel.Value = mode == "inv-save" || mode == "scr-save" ? "Save layout" : "Edit layout";
                PlacePillRow(rowW);
            }
        }

        private static void PlacePillRow(float rowW)
        {
            float sf = Sf, uiW = Screen.width / sf;
            var cands = new[] { new Vector2((uiW - rowW) * 0.5f, 16f), new Vector2((uiW - rowW) * 0.5f, 1080f - 16f - 32f), new Vector2(16f, 16f), new Vector2(16f, 1080f - 48f),
                                new Vector2(uiW - rowW - 16f, 1080f - 48f), new Vector2((uiW - rowW) * 0.25f, 16f), new Vector2((uiW - rowW) * 0.75f, 16f) };
            var blockers = new List<Rect>();
            foreach (var e in DockService.All)
            {
                if (e.O.Rect == null || !DockService.IsShownPublic(e)) continue;
                var px = DockService.ScreenRect(e.O.Rect);
                blockers.Add(new Rect(px.x / sf, px.y / sf, px.width / sf, px.height / sf));
            }
            // the menu dialog itself is a blocker too; the first clear spot wins, else the one overlapping the least area
            if (Menu.instance != null && Menu.instance.m_menuDialog is RectTransform md && md.gameObject.activeInHierarchy)
            { var px = DockService.ScreenRect(md); blockers.Add(new Rect(px.x / sf, px.y / sf, px.width / sf, px.height / sf)); }
            Vector2 pick = cands[0]; float least = float.MaxValue;
            foreach (var c in cands)
            {
                var r = new Rect(c.x - 8f, c.y - 8f, rowW + 16f, 48f);
                float area = 0f;
                foreach (var b in blockers) { float ow = Mathf.Max(0f, Mathf.Min(r.xMax, b.xMax) - Mathf.Max(r.xMin, b.xMin)), oh = Mathf.Max(0f, Mathf.Min(r.yMax, b.yMax) - Mathf.Max(r.yMin, b.yMin)); area += ow * oh; }
                if (area < least) { least = area; pick = c; }
                if (area <= 0f) break;
            }
            DockService.Log($"pill row at {pick.x:0},{pick.y:0} (overlap {least:0} px2)");
            s_pillSpot = pick;
            s_pillRow.anchorMin = s_pillRow.anchorMax = new Vector2(0f, 1f); s_pillRow.pivot = new Vector2(0f, 1f);
            s_pillRow.anchoredPosition = new Vector2(pick.x, -pick.y); s_pillRow.sizeDelta = new Vector2(rowW, 32f);
        }

        private static void OnPill()
        {
            if (s_pillMode == "hud-edit") DockService.SetHudEditMode(true);
            else if (s_pillMode == "inv-edit") DockService.SetEditMode("inventory", true);
            else if (s_pillMode == "inv-save") DockService.SetEditMode("inventory", false);
            else if (s_pillMode == "scr-edit" && s_pillLayer != null) DockService.SetEditMode(s_pillLayer, true);
            else if (s_pillMode == "scr-save" && s_pillLayer != null) DockService.SetEditMode(s_pillLayer, false);
        }

        // asks first, then restores this screen's layer defaults.
        private static void OnResetPill()
        {
            string layer = s_pillMode == "hud-edit" ? "hud" : (s_pillMode == "scr-edit" || s_pillMode == "scr-save") && s_pillLayer != null ? s_pillLayer : "inventory";
            ConfirmReset(layer);
        }

        internal static void ConfirmReset(string layer)
        {
            UnifiedPopup.Push(new YesNoPopup("Reset layout", "Reset this layout? Every " + (layer == "hud" ? "HUD" : layer == "inventory" ? "inventory" : "window") + " element goes back to its default spot.",
                () => { UnifiedPopup.Pop(); DockService.SetAllHidden(layer, false); DockService.ResetLayer(layer); FiresCore.UI.Kit.KitWindow.ForgetAllSaved(); /* defaults land as their owners define them, no clamp */ }, () => UnifiedPopup.Pop(), false));
        }
        // ---- the legend (every HUD element, Hide / Show while editing; the name selects it) ----
        private static RectTransform s_legend; private static string s_legendSig;
        // first spot covering no shown HUD
        // element, else the one covering the least area; the player can drag it by its title afterwards (kept until editing ends).
        // eight candidate spots; the first fully free one wins.
        // When none is free the legend COLLAPSES to its title bar, placed where the bar alone covers nothing (else least area); a click on
        // the title expands it there (the player's choice), another collapses it. Dragged by its title (kept until editing ends).
        private static Vector2 s_legendDragged = new Vector2(float.NaN, 0f);
        private static bool s_legendOpen, s_legendCollapsed, s_legendForceCollapsed;   // folded by its title
        private const float LegendBarH = 34f;

        private static Vector2 ChooseLegendSpot(float w, float h, out float area, out string name, out string covers)
        {
            float sf = Sf, uiW = Screen.width / sf;
            var spots = new[]
            {
                ("right-centre", new Vector2(uiW - 16f - w, (1080f - h) * 0.5f)), ("left-centre", new Vector2(16f, (1080f - h) * 0.5f)),
                ("right-bottom", new Vector2(uiW - 16f - w, 1080f - 16f - h)), ("left-bottom", new Vector2(16f, 1080f - 16f - h)),
                // top-left = 16 px under the vanilla hotbar row (y 124 without one), top-right = 16 px under the minimap element,
                // centre-left / centre-right = 16 px either side of the screen centre, vertically centred
                ("top-left", new Vector2(16f, BottomOf("hud.hotbar.vanilla", 108f) + 16f)), ("top-right", new Vector2(uiW - 16f - w, BottomOf("hud.minimap", 404f) + 16f)),
                ("centre-left", new Vector2(uiW * 0.5f - 16f - w, (1080f - h) * 0.5f)), ("centre-right", new Vector2(uiW * 0.5f + 16f, (1080f - h) * 0.5f)),
            };
            area = float.MaxValue; name = spots[0].Item1; covers = ""; Vector2 best = spots[0].Item2;
            foreach (var (n, p0) in spots)
            {
                var p = new Vector2(Mathf.Clamp(p0.x, 4f, uiW - 4f - w), Mathf.Clamp(p0.y, 4f, 1076f - h));
                var r = new Rect(p.x, p.y, w, h); float a = 0f; var cv = new List<string>();
                foreach (var e in DockService.All)
                {
                    if (e.O.Layer != "hud" || e.O.Rect == null || !DockService.IsShownPublic(e)) continue;
                    var px = DockService.VisibleScreenRect(e); var er = new Rect(px.x / sf, px.y / sf, px.width / sf, px.height / sf);
                    float ow = Mathf.Max(0f, Mathf.Min(r.xMax, er.xMax) - Mathf.Max(r.xMin, er.xMin)), oh = Mathf.Max(0f, Mathf.Min(r.yMax, er.yMax) - Mathf.Max(r.yMin, er.yMin));
                    if (ow * oh > 0f) { a += ow * oh; cv.Add(e.O.Id); }
                }
                if (a < area) { area = a; name = n; best = p; covers = string.Join(",", cv.ToArray()); }
                if (a <= 0f) break;
            }
            return best;
        }

        // the bottom edge (1080p units) of a shown element's drawn content, or the fallback
        private static float BottomOf(string id, float fallback)
        {
            foreach (var e in DockService.All)
                if (e.O.Id == id && e.O.Rect != null && DockService.IsShownPublic(e)) { var r = DockService.VisibleScreenRect(e); return r.yMax / Sf; }
            return fallback;
        }

        private static void PlaceLegend(float w, float fullH, out bool collapsed, out float h)
        {
            ChooseLegendSpot(w, fullH, out float fullArea, out _, out _);
            collapsed = fullArea > 0f && !s_legendOpen;
            h = collapsed ? LegendBarH : fullH;
            s_legend.anchorMin = s_legend.anchorMax = new Vector2(0f, 1f); s_legend.pivot = new Vector2(0f, 1f); s_legend.sizeDelta = new Vector2(w, h);
            if (!float.IsNaN(s_legendDragged.x)) { s_legend.anchoredPosition = s_legendDragged; return; }
            var pos = ChooseLegendSpot(w, h, out _, out string name, out string covers);
            s_legend.anchoredPosition = new Vector2(pos.x, -pos.y);
            string line = $"legend at {name} ({(collapsed ? "collapsed" : "open")}), covers {(covers.Length == 0 ? "nothing" : covers)}";
            if (line != s_legendLogged) { s_legendLogged = line; DockService.Log(line); }
        }
        private static string s_legendLogged;

        private sealed class LegendDrag : MonoBehaviour, UnityEngine.EventSystems.IDragHandler, UnityEngine.EventSystems.IPointerClickHandler
        {
            public void OnDrag(UnityEngine.EventSystems.PointerEventData ev)
            {
                if (s_legend == null) return;
                s_legend.anchoredPosition += new Vector2(ev.delta.x, ev.delta.y) / Sf;
                s_legendDragged = s_legend.anchoredPosition;
            }
            // a click (not a drag) on the title expands / collapses the legend
            public void OnPointerClick(UnityEngine.EventSystems.PointerEventData ev)
            {
                if (ev.dragging) return;
                // the title ALWAYS folds / unfolds the list (it used to do nothing
                // while the list fitted open)
                if (s_legendForceCollapsed) { s_legendForceCollapsed = false; s_legendSig = null; DockService.Log("legend: opened by its title"); return; }
                if (!s_legendCollapsed && !s_legendOpen) { s_legendForceCollapsed = true; s_legendSig = null; DockService.Log("legend: collapsed by its title"); return; }
                s_legendOpen = !s_legendOpen; s_legendSig = null;
            }
        }
        private static void TickLegend()
        {
            var els = new List<DockService.Element>();
            foreach (var e in DockService.All) if (e.O.Layer == "hud" && (!e.O.Passive || e.O.OutlineOnly) && e.O.Rect != null) els.Add(e);
            // grouped by category (fixed order), vanilla hotbar first among the hotbars, then by label
            els.Sort((a, b) =>
            {
                int ca = CategoryOf(a.O.Id), cb = CategoryOf(b.O.Id);
                if (ca != cb) return ca.CompareTo(cb);
                int pa = a.O.Id == "hud.hotbar.vanilla" ? 0 : 1, pb = b.O.Id == "hud.hotbar.vanilla" ? 0 : 1;
                if (pa != pb) return pa.CompareTo(pb);
                return string.Compare(a.O.Label ?? a.O.Id, b.O.Label ?? b.O.Id, System.StringComparison.OrdinalIgnoreCase);
            });
            var sb = new System.Text.StringBuilder();
            foreach (var k in s_catFolded) sb.Append("f").Append(k).Append(";");
            foreach (var e in els) sb.Append(e.O.Id).Append(DockService.EditHidden.Contains(e.O.Id) ? "-" : "+").Append(e.O.Id == DockService.Selected ? "*" : "").Append(";");
            sb.Append(s_legendOpen ? "|open" : "|auto").Append(s_legendForceCollapsed ? "|folded" : "");
            string sig = sb.ToString();
            if (sig == s_legendSig && s_legend != null) return;
            s_legendSig = sig;
            if (s_legend != null) Object.Destroy(s_legend.gameObject);
            const float rowH = 22f, w = 230f;
            int cats = 0, shownRows = 0, lastCat = -1;
            foreach (var e in els) { int ci = CategoryOf(e.O.Id); if (ci != lastCat) { cats++; lastCat = ci; } if (!s_catFolded.Contains(ci)) shownRows++; }
            float h = 40f + 28f + (cats + shownRows) * rowH + 8f;   // + the Hide all / Show all row; + category headers
            s_legend = W.Node(s_chrome, "Legend");
            PlaceLegend(w, s_legendForceCollapsed ? LegendBarH : Mathf.Min(h, 1000f), out bool collapsed, out float placedH);
            if (s_legendForceCollapsed) collapsed = true;
            s_legendCollapsed = collapsed;
            bool toggleable = true;   // the title always folds / unfolds
            var dback = W.Box(s_legend, "DarkBack", KitSprites.RoundedFill(T.Radius + 2f), Dark); W.Stretch(dback.rectTransform, -2, -2, -2, -2);
            var fill = W.Box(s_legend, "Fill", KitSprites.RoundedFill(T.Radius), T.Well); W.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = true;   // SOLID plate
            var edge = W.Box(s_legend, "Outline", KitSprites.RoundedOutline(T.Radius), OutlineCol); W.Stretch(edge.rectTransform, 0, 0, 0, 0);
            var title = KitText.Create(s_legend, "Title", toggleable ? (collapsed ? "HUD elements  v" : "HUD elements  ^") : "HUD elements", FontRole.SerifBold, 15f, T.Gold, KitAlign.Left, 0f, false);
            W.Place(title.Rect, 12f, 8f, w - 24f, 22f);
            var strip = W.Box(s_legend, "DragStrip", KitSprites.White(), new Color(0f, 0f, 0f, 0f), false); W.Place(strip.rectTransform, 0f, 0f, w, 34f);
            strip.raycastTarget = true; strip.gameObject.AddComponent<LegendDrag>();   // drag the legend by its title
            float y = 40f;
            // "Hide all", then turn on only what to see and adjust; "Show all" brings everything back. Saved with the layout.
            if (!collapsed)
            {
                float bw = (w - 16f - 6f) * 0.5f;
                LegendButton("HideAll", "Hide all", 8f, y, bw, () => DockService.SetAllHidden("hud", true));
                LegendButton("ShowAll", "Show all", 8f + bw + 6f, y, bw, () => DockService.SetAllHidden("hud", false));
                y += 28f;
            }
            int curCat = -1;
            foreach (var e in els)
            {
                if (collapsed) break;   // title bar only
                if (y + rowH > 1000f - 8f) break;
                int cat = CategoryOf(e.O.Id);
                if (cat != curCat)
                {
                    curCat = cat; int ck = cat; bool folded = s_catFolded.Contains(cat);
                    var hdr = W.Node(s_legend, "Cat_" + Categories[cat]); W.Place(hdr, 8f, y, w - 16f, rowH - 2f);
                    var hb = W.Box(hdr, "Fill", KitSprites.RoundedFill(T.RadiusSmall), T.WithAlpha(T.PanelRaised, 0.95f)); W.Stretch(hb.rectTransform, 0, 0, 0, 0);
                    var hl = KitText.Create(hdr, "Label", (folded ? "+  " : "-  ") + Categories[cat], FontRole.SansBold, 12f, T.GoldBright, KitAlign.Left, 0f, false); W.Stretch(hl.Rect, 6, 0, 0, 0);
                    Clickable(hdr, () => { if (!s_catFolded.Remove(ck)) s_catFolded.Add(ck); s_legendSig = null; });
                    y += rowH;
                }
                if (s_catFolded.Contains(cat)) continue;
                if (y + rowH > 1000f - 8f) break;
                string id = e.O.Id; bool hidden = DockService.EditHidden.Contains(id); bool sel = id == DockService.Selected;
                var row = W.Node(s_legend, "Row_" + id); W.Place(row, 8f, y, w - 16f, rowH - 2f);
                var nameBg = W.Box(row, "NameFill", KitSprites.RoundedFill(T.RadiusSmall), sel ? T.WithAlpha(T.GoldBright, 0.25f) : T.WithAlpha(T.Well, 0.0f));
                W.Place(nameBg.rectTransform, 0f, 0f, w - 16f - 62f, rowH - 2f);
                var nm = KitText.Create(row, "Name", e.O.Label ?? id, FontRole.SansBold, 12f, hidden ? T.TextMuted : T.Gold, KitAlign.Left, 0f, true);
                W.Place(nm.Rect, 6f, 0f, w - 16f - 72f, rowH - 2f);
                Clickable(nameBg.rectTransform, () => DockService.Selected = id);
                var eye = W.Node(row, "Eye"); W.Place(eye, w - 16f - 58f, 1f, 56f, rowH - 4f);
                var eb = W.Box(eye, "Fill", KitSprites.RoundedFill(T.RadiusSmall), T.WithAlpha(T.Well, 0.95f)); W.Stretch(eb.rectTransform, 0, 0, 0, 0);
                var eo = W.Box(eye, "Outline", KitSprites.RoundedOutline(T.RadiusSmall), OutlineCol); W.Stretch(eo.rectTransform, 0, 0, 0, 0);
                var et = KitText.Create(eye, "Label", hidden ? "Show" : "Hide", FontRole.SansBold, 11f, T.GoldBright, KitAlign.Center, 0f, false); W.Stretch(et.Rect, 0, 0, 0, 0);
                Clickable(eye, () => DockService.SetUserHidden(id, !DockService.IsUserHidden(id)));
                y += rowH;
            }
        }

        // the HUD elements list categories (fixed order) and the id rules that file each element; unknown ids land in Misc.
        private static readonly string[] Categories = { "Hotbars", "Vitals", "Map & world", "Chat & messages", "Combat / boss", "Building", "Misc" };
        private static readonly HashSet<int> s_catFolded = new HashSet<int>();
        private static int CategoryOf(string id)
        {
            if (id == null) return 6;
            if (id.StartsWith("hud.hotbar") || id.StartsWith("vainv.hotbar") || id == "rpgclasses.bars") return 0;
            if (id == "hud.health" || id == "hud.stamina" || id == "hud.eitr" || id == "rpgmaker.globes" || id == "hud.adrenaline" || id == "hud.stagger" || id == "hud.food" || id == "hud.status") return 1;
            if (id == "hud.minimap" || id == "hud.plates" || id == "hud.msg.biome" || id == "hud.power" || id == "hud.event" || id == "hud.saveicon" || id == "hud.connection" || id.StartsWith("hud.map")) return 2;
            if (id == "hud.chat" || id.Contains("chat") || id.StartsWith("hud.msg") || id == "hud.keyhints" || id == "hud.hover") return 3;
            if (id == "hud.boss" || id == "hud.crosshair" || id == "hud.stealth" || id == "hud.grouphud" || id.StartsWith("rpgclasses.")) return 4;
            if (id.StartsWith("hud.build") || id == "hud.piecehealth") return 5;
            return 6;
        }

        private static void LegendButton(string name, string text, float x, float y, float width, System.Action click)
        {
            var b = W.Node(s_legend, name); W.Place(b, x, y, width, 24f);
            var f = W.Box(b, "Fill", KitSprites.RoundedFill(T.RadiusSmall), T.WithAlpha(T.Well, 0.95f)); W.Stretch(f.rectTransform, 0, 0, 0, 0);
            var o = W.Box(b, "Outline", KitSprites.RoundedOutline(T.RadiusSmall), T.Gold); W.Stretch(o.rectTransform, 0, 0, 0, 0);
            var l = KitText.Create(b, "Label", text, FontRole.SansBold, 12f, T.GoldBright, KitAlign.Center, 0f, false); W.Stretch(l.Rect, 0, 0, 0, 0);
            Clickable(b, click);
        }

        // Valheim's HUD canvas is a camera canvas, and the EventSystem orders
        // raycasters by camera before sorting order, so an element's grab area on the HUD canvas can win over this overlay's tags and legend
        // (an overlay canvas) where they overlap (the hotbar column right under "Flight hints"). On a press whose top hit is NOT ours, this
        // overlay's own raycasters are asked; if one of our clickables is under the pointer it gets the click, and the route is logged.
        private static readonly List<UnityEngine.EventSystems.RaycastResult> s_routeHits = new List<UnityEngine.EventSystems.RaycastResult>();
        private static void RouteBlockedClick()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null || s_canvasGo == null) return;
            var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = UnityEngine.Input.mousePosition, button = UnityEngine.EventSystems.PointerEventData.InputButton.Left, clickCount = 1 };
            s_routeHits.Clear();
            es.RaycastAll(ped, s_routeHits);
            if (s_routeHits.Count == 0 || s_routeHits[0].gameObject == null || s_routeHits[0].gameObject.transform.IsChildOf(s_canvasGo.transform)) return;   // ours already wins: the normal path clicks it
            string blocker = s_routeHits[0].gameObject.name;
            foreach (var hit in s_routeHits)
            {
                if (hit.gameObject == null || !hit.gameObject.transform.IsChildOf(s_canvasGo.transform)) continue;
                var target = UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hit.gameObject);
                if (target == null) return;
                ped.pointerPress = target; ped.rawPointerPress = hit.gameObject; ped.pointerCurrentRaycast = hit;
                UnityEngine.EventSystems.ExecuteEvents.Execute(target, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                DockService.Log($"layout click routed to {target.name} (the top hit was {blocker}, on another canvas)");
                return;
            }
        }

        // 0.6 s after any layout editor opens, every SHOWN corner grip is
        // raycast through the EventSystem at its centre; PASS = the grip itself is the top hit. Logged with the overlay canvas's state.
        private static float s_selfTestAt = -1f;
        private static void GripSelfTest()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null || s_canvasGo == null) { DockService.Log("grip self-test: no EventSystem / overlay"); return; }
            var cv = s_canvasGo.GetComponent<Canvas>(); var gr = s_canvasGo.GetComponent<GraphicRaycaster>();
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            int pass = 0, fail = 0;
            foreach (var set in s_handleSets)
            {
                if (set == null || !set.gameObject.activeInHierarchy) continue;
                foreach (var gh in set.GetComponentsInChildren<FiresCore.UI.Shell.ShellWindowDrag>(false))
                {
                    var rt = (RectTransform)gh.transform;
                    var c = new Vector3[4]; rt.GetWorldCorners(c);
                    Vector2 centre = (RectTransformUtility.WorldToScreenPoint(null, c[0]) + RectTransformUtility.WorldToScreenPoint(null, c[2])) * 0.5f;
                    hits.Clear();
                    es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = centre }, hits);
                    bool ok = hits.Count > 0 && hits[0].gameObject == gh.gameObject;
                    if (ok) pass++; else fail++;
                    var top = new List<string>();
                    for (int i = 0; i < hits.Count && i < 3; i++) top.Add(hits[i].gameObject != null ? hits[i].gameObject.name + "@" + (hits[i].module != null ? hits[i].module.name : "?") : "null");
                    DockService.Log($"grip self-test {(ok ? "PASS" : "FAIL")} {gh.Key} {gh.transform.parent.name} at {centre.x:0},{centre.y:0}: top {string.Join(" > ", top.ToArray())}");
                }
            }
            DockService.Log($"grip self-test: {pass} PASS, {fail} FAIL (overlay canvas sorting {(cv != null ? cv.sortingOrder : -1)}, mode {(cv != null ? cv.renderMode.ToString() : "?")}, raycaster {(gr != null && gr.isActiveAndEnabled)})");
        }

        // "fires_dock_probe" raycasts through the EventSystem at the
        // centre of every name tag and the legend title and logs the top hits (PASS when the plate itself is first), plus each HUD
        // element's visibility state (player-hidden, activeInHierarchy, culled).
        internal static void Probe()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) { DockService.Log("probe: no EventSystem"); return; }
            var hits = new List<UnityEngine.EventSystems.RaycastResult>();
            int pass = 0, fail = 0;
            void At(string what, RectTransform rt)
            {
                if (rt == null || !rt.gameObject.activeInHierarchy) return;
                var c = new Vector3[4]; rt.GetWorldCorners(c);
                var cam = DockService.Cam(rt);   // HUD rects live on a camera canvas
                Vector2 centre = (RectTransformUtility.WorldToScreenPoint(cam, c[0]) + RectTransformUtility.WorldToScreenPoint(cam, c[2])) * 0.5f;
                hits.Clear();
                es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = centre }, hits);
                bool ok = hits.Count > 0 && hits[0].gameObject != null && hits[0].gameObject.transform.IsChildOf(rt);
                if (ok) pass++; else fail++;
                var top = new List<string>();
                for (int i = 0; i < hits.Count && i < 3; i++) top.Add(hits[i].gameObject != null ? hits[i].gameObject.name + " (" + (hits[i].module != null ? hits[i].module.name : "?") + ")" : "null");
                DockService.Log($"probe {(ok ? "PASS" : "FAIL")} {what} at {centre.x:0},{centre.y:0}: top {string.Join(" > ", top.ToArray())}");
            }
            foreach (var kv in s_nodes) At("tag " + kv.Key, kv.Value.Tag);
            // the four corner grips of every shown element (grips are live only on the picked / selected element: the probe
            // selects each in turn for one frame's worth of layout, so their hit areas exist where the corner squares are drawn)
            foreach (var e in DockService.All)
            {
                if (e.O.Rect == null || e.Grips.Count == 0 || !DockService.ElementActive(e) || !DockService.IsShownPublic(e)) continue;
                string was = DockService.Selected; DockService.Selected = e.O.Id;
                DockService.TickOverlays();
                for (int i = 0; i < e.Grips.Count; i++) At($"grip {e.O.Id}#{i}", e.Grips[i]);
                DockService.Selected = was;
            }
            DockService.TickOverlays();
            if (s_legend != null) { var title = s_legend.Find("DragStrip") as RectTransform; At("legend title", title); }
            foreach (var e in DockService.All)
            {
                if (e.O.Layer != "hud" || e.O.Rect == null) continue;
                if (!e.O.Id.StartsWith("vainv.hotbar", System.StringComparison.Ordinal) && !DockService.IsUserHidden(e.O.Id)) continue;
                DockService.Log($"probe {e.O.Id}: player-hidden {DockService.IsUserHidden(e.O.Id)}, activeInHierarchy {e.O.Rect.gameObject.activeInHierarchy}");
            }
            DockService.Log($"probe: {pass} PASS, {fail} FAIL (raycast top hit = the plate)");
        }

        private static void Clickable(RectTransform rt, System.Action click)
        {
            var img = rt.GetComponent<Image>() ?? rt.GetComponentInChildren<Image>();
            if (img != null) img.raycastTarget = true;
            var btn = rt.gameObject.AddComponent<UnityEngine.UI.Button>(); btn.targetGraphic = img; btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { try { click(); } catch (System.Exception ex) { DockService.Log("layout click threw " + ex.Message); } });
        }

        // outlines / group outlines kept on screen, 4 px from the edge
        private static Rect ClampScreen(Rect r)
        {
            float uiW = Screen.width / Sf, uiH = 1080f;
            const float m = 4f;
            float w = Mathf.Min(r.width, uiW - 2f * m), h = Mathf.Min(r.height, uiH - 2f * m);
            float x = Mathf.Clamp(r.x, m, uiW - m - w), y = Mathf.Clamp(r.y, m, uiH - m - h);
            return new Rect(x, y, w, h);
        }

        private static Rect Union(Rect a, Rect b) => Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        // First clear spot: above (left end, right end), outside left / right at the top, below (left, right), inside top-left.
        // a docked member's 6 own spots usually straddle the group line or land on a neighbour (the 16 px dock gap is
        // narrower than a 20 px tag): next, spots just OUTSIDE the group outline on the member's row (left, then right, 6 px clear); then
        // the inside corners (top-left, top-right, bottom-left, bottom-right), the first clear of handles / grips / tags; top-left last.
        // an element with corner handles (selected / picked on the HUD, every inventory panel) also tries its 6 own
        // spots shifted 14 px in from the corners (the handles reach 8 px past the outline), right after the plain 6.
        private static Rect TagSpot(Rect r, float w, List<(DockService.Element e, Rect r)> shown, Rect? grp = null, bool hasHandles = false)
        {
            float uiW = Screen.width / Sf, uiH = 1080f, p = 4f;
            var list = new List<Rect>
            {
                new Rect(r.x, r.y - TagH - p, w, TagH), new Rect(r.xMax - w, r.y - TagH - p, w, TagH),
                new Rect(r.x - w - p - 2f, r.y, w, TagH), new Rect(r.xMax + p + 2f, r.y, w, TagH),
                new Rect(r.x, r.yMax + p, w, TagH), new Rect(r.xMax - w, r.yMax + p, w, TagH),
            };
            if (hasHandles)
            {
                const float k = 14f;
                list.Add(new Rect(r.x + k, r.y - TagH - p, w, TagH)); list.Add(new Rect(r.xMax - w - k, r.y - TagH - p, w, TagH));
                list.Add(new Rect(r.x - w - p - 2f, r.y + k, w, TagH)); list.Add(new Rect(r.xMax + p + 2f, r.y + k, w, TagH));
                list.Add(new Rect(r.x + k, r.yMax + p, w, TagH)); list.Add(new Rect(r.xMax - w - k, r.yMax + p, w, TagH));
            }
            if (grp != null) { var g = grp.Value; list.Add(new Rect(g.x - w - 6f, r.y, w, TagH)); list.Add(new Rect(g.xMax + 6f, r.y, w, TagH)); }
            foreach (var c in list)
            {
                if (c.xMin < 0f || c.yMin < 0f || c.xMax > uiW || c.yMax > uiH) continue;
                bool clear = true;
                foreach (var t in s_taken) if (t.Overlaps(Grow(c, 2f))) { clear = false; break; }
                if (clear) foreach (var (_, o) in shown) if (HitsOutline(c, o)) { clear = false; break; }
                if (clear) foreach (var g in s_groupLines) if (CrossesLine(c, g)) { clear = false; break; }
                if (clear) return c;
            }
            var inside = new[] { new Rect(r.x + 6f, r.y + 6f, w, TagH), new Rect(r.xMax - 6f - w, r.y + 6f, w, TagH), new Rect(r.x + 6f, r.yMax - 6f - TagH, w, TagH), new Rect(r.xMax - 6f - w, r.yMax - 6f - TagH, w, TagH) };
            foreach (var c in inside)
            {
                var cc = new Rect(Mathf.Clamp(c.x, 0f, uiW - w), Mathf.Clamp(c.y, 0f, uiH - TagH), w, TagH);
                bool clear = true;
                foreach (var t in s_taken) if (t.Overlaps(Grow(cc, 2f))) { clear = false; break; }   // never under a handle / grip / other tag
                if (clear) return cc;
            }
            return new Rect(Mathf.Clamp(r.x + 6f, 0f, uiW - w), Mathf.Clamp(r.y + 6f, 0f, uiH - TagH), w, TagH);   // inside top-left, last resort
        }

        private static readonly List<Rect> s_groupLines = new List<Rect>();
        // on the outline band of g: overlaps g grown 2 px but is not fully inside g shrunk 2 px (inside or outside the group is fine)
        private static bool CrossesLine(Rect c, Rect g)
        {
            if (!Grow(g, 2f).Overlaps(c)) return false;                       // fully outside the group
            var inner = Grow(g, -2f);
            bool fullyInside = inner.Contains(c.min) && inner.Contains(c.max);
            return !fullyInside;                                             // straddles the line
        }

        private static Rect Grow(Rect r, float d) => new Rect(r.x - d, r.y - d, r.width + 2 * d, r.height + 2 * d);

        // Touches the outline band (2 px) of o, or sits inside o.
        private static bool HitsOutline(Rect c, Rect o) => Grow(o, 2f).Overlaps(c);

        // ---- pieces (same maths as FiresHudLayoutMockup.DashedRect / NameTag) -------------------------------------------------
        private static RectTransform DashedRect(RectTransform parent, string name, Vector2 size, Color c, float th, float rad)
        {
            var n = W.Stretch(W.Node(parent, name), 0, 0, 0, 0);
            float wdt = size.x, hgt = size.y;
            rad = Mathf.Min(rad, Mathf.Min(wdt, hgt) * 0.5f);
            void Run(bool horiz, float fixedPos, float from, float to)
            {
                for (float q = from; q < to - 0.5f; q += Dash + Gap)
                {
                    float len = Mathf.Min(Dash, to - q);
                    var bk = W.Box(n, "DashBack", KitSprites.White(), Dark, false);
                    if (horiz) W.Place(bk.rectTransform, q - 1f, fixedPos - 1f, len + 2f, th + 2f); else W.Place(bk.rectTransform, fixedPos - 1f, q - 1f, th + 2f, len + 2f);
                    var im = W.Box(n, "Dash", KitSprites.White(), c, false);
                    if (horiz) W.Place(im.rectTransform, q, fixedPos, len, th); else W.Place(im.rectTransform, fixedPos, q, th, len);
                }
            }
            Run(true, 0f, rad, wdt - rad);
            Run(true, hgt - th, rad, wdt - rad);
            Run(false, 0f, rad, hgt - rad);
            Run(false, wdt - th, rad, hgt - rad);
            for (int i = 0; i < 4; i++)
            {
                bool right = i % 2 == 1, bottom = i >= 2;
                var clip = W.Node(n, "Corner" + i);
                W.Place(clip, right ? wdt - rad : 0f, bottom ? hgt - rad : 0f, rad, rad);
                clip.gameObject.AddComponent<RectMask2D>();
                var bo = W.Box(clip, "ArcBackOut", KitSprites.RoundedOutline(rad + 1f), Dark);
                W.Place(bo.rectTransform, (right ? -rad : 0f) - 1f, (bottom ? -rad : 0f) - 1f, rad * 2f + 2f, rad * 2f + 2f);
                var bi = W.Box(clip, "ArcBackIn", KitSprites.RoundedOutline(Mathf.Max(1f, rad - 1f)), Dark);
                W.Place(bi.rectTransform, (right ? -rad : 0f) + 1f, (bottom ? -rad : 0f) + 1f, rad * 2f - 2f, rad * 2f - 2f);
                var ring = W.Box(clip, "Arc", KitSprites.RoundedOutline(rad), c);
                W.Place(ring.rectTransform, right ? -rad : 0f, bottom ? -rad : 0f, rad * 2f, rad * 2f);
            }
            return n;
        }

        private static RectTransform NameTag(RectTransform parent, string name, string text, out float width)
        {
            var n = W.Node(parent, name);
            var lb = KitText.Create(n, "Label", text, FontRole.SansBold, 12f, T.Gold, KitAlign.Center, 0f, false);
            width = Mathf.Ceil(lb.PreferredWidth) + 18f;
            W.Place(n, 0f, 0f, width, TagH);
            var f = W.Box(n, "Fill", KitSprites.RoundedFill(10f), T.WithAlpha(T.Well, 0.92f)); f.transform.SetAsFirstSibling(); W.Stretch(f.rectTransform, 0, 0, 0, 0);
            var db = W.Box(n, "DarkBack", KitSprites.RoundedFill(12f), Dark); db.transform.SetAsFirstSibling(); W.Stretch(db.rectTransform, -2, -2, -2, -2);
            var o = W.Box(n, "Outline", KitSprites.RoundedOutline(10f), OutlineCol); o.transform.SetSiblingIndex(2); W.Stretch(o.rectTransform, 0, 0, 0, 0);
            W.Stretch(lb.Rect, 0, 0, 0, 0);
            return n;
        }

        private static void Ensure()
        {
            if (s_canvasGo != null) return;
            s_canvasGo = new GameObject("FiresDockLayoutOverlay", typeof(RectTransform));
            Object.DontDestroyOnLoad(s_canvasGo);
            s_canvasGo.layer = 5;
            var cv = s_canvasGo.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 31000;
            var cs = s_canvasGo.AddComponent<CanvasScaler>(); cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1920f, 1080f); cs.matchWidthOrHeight = 1f;
            s_canvasGo.AddComponent<GraphicRaycaster>();   // name tags are clickable (select); dashes / fills stay non-raycast
            s_root = W.Stretch(W.Node(s_canvasGo.transform, "Elements"), 0, 0, 0, 0);

            // banner + Reset layout / Done, top centre (the only raycast-able part)
            var chromeGo = new GameObject("LayoutChrome", typeof(RectTransform));
            s_chrome = (RectTransform)chromeGo.transform; s_chrome.SetParent(s_canvasGo.transform, false);
            W.Stretch(s_chrome, 0, 0, 0, 0);
            var sub = chromeGo.AddComponent<Canvas>(); sub.overrideSorting = true; sub.sortingOrder = 31001;
            chromeGo.AddComponent<GraphicRaycaster>();
            string msg = "HUD layout" + T.GlyphSep + "drag to move" + T.GlyphSep + "corner to resize" + T.GlyphSep + "Alt: undock" + T.GlyphSep + "Esc: cancel" + T.GlyphSep + "Save when done";
            var banner = W.Node(s_chrome, "Banner");
            var bl = KitText.Create(banner, "Label", msg, FontRole.SansBold, 15f, T.Gold, KitAlign.Center, 0f, false);
            float bw = Mathf.Ceil(bl.PreferredWidth) + 32f;
            banner.anchorMin = banner.anchorMax = new Vector2(0.5f, 1f); banner.pivot = new Vector2(0.5f, 1f);
            banner.anchoredPosition = new Vector2(0f, -48f); banner.sizeDelta = new Vector2(bw, 32f);
            var bf = W.Box(banner, "Fill", KitSprites.RoundedFill(T.Radius), T.WithAlpha(T.Well, 0.92f)); bf.transform.SetAsFirstSibling(); W.Stretch(bf.rectTransform, 0, 0, 0, 0);
            var bo = W.Box(banner, "Outline", KitSprites.RoundedOutline(T.Radius), OutlineCol); W.Stretch(bo.rectTransform, 0, 0, 0, 0);
            W.Stretch(bl.Rect, 0, 0, 0, 0);
            Button(s_chrome, "ResetLayout", "Reset layout", -207f, () => ConfirmReset("hud"));
            Button(s_chrome, "Cancel", "Cancel", -65f, () => DockService.CancelEdit("hud"));   // back as it was when the editor opened
            Button(s_chrome, "Save", "Save", 77f, () => DockService.SetHudEditMode(false));   // Save closes the editor (and saves)
            s_canvasGo.SetActive(false);
        }

        private static KitLabel s_lastLabel;
        private static RectTransform Button(RectTransform parent, string name, string text, float xFromCentre, System.Action click)
        {
            var b = W.Node(parent, name);
            b.anchorMin = b.anchorMax = new Vector2(0.5f, 1f); b.pivot = new Vector2(0f, 1f);
            b.anchoredPosition = new Vector2(xFromCentre, -90f); b.sizeDelta = new Vector2(130f, 32f);
            var bg = b.gameObject.AddComponent<Image>(); bg.sprite = KitSprites.RoundedFill(T.Radius); bg.type = Image.Type.Sliced; bg.color = T.WithAlpha(T.Well, 0.95f); bg.raycastTarget = true;
            var ol = W.Box(b, "Outline", KitSprites.RoundedOutline(T.Radius), T.Gold); W.Stretch(ol.rectTransform, 0, 0, 0, 0);
            var lb = KitText.Create(b, "Label", text, FontRole.SansBold, 14f, T.GoldBright, KitAlign.Center, 0f, false); W.Stretch(lb.Rect, 0, 0, 0, 0); s_lastLabel = lb;
            var btn = b.gameObject.AddComponent<UnityEngine.UI.Button>(); btn.targetGraphic = bg;
            btn.onClick.AddListener(() => { try { click(); } catch (System.Exception e) { DockService.Log(name + " threw " + e.Message); } });
            return b;
        }
    }
}
