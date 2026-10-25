// Fires UI Shell, Core (mockup Tools\golden\ui_overhaul\minimap,
// approved): the round small minimap. ONLY the small on-screen map (Minimap.m_smallRoot); the large map gets its own override later.
// Driven from ShellRuntime.LateUpdate, so it exists only while [UI] Fires UI override is ON: no Harmony patch, nothing runs while
// OFF. Restore (master OFF, or a new Minimap instance) re-parents the vanilla map image + pins back, puts every vanilla value back
// and destroys what it added, so OFF -> ON -> OFF ends exactly at vanilla.
// The stray square backdrop stays RPGmaker's always-on fix: Core leaves that Image alone; the round mask
// and rim cover the map area either way.
using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static partial class ShellMinimap
    {
        private const string FrameName = "FiresMinimapFrame";

        // timing (unscaled seconds)
        private const float VisibilityCheckInterval = 0.5f, SelfHealAfterHiddenSeconds = 1f, PlateRefreshInterval = 0.25f, CopiedNoticeSeconds = 1.5f;
        // a CanvasGroup alpha / scale / graphic alpha at or under this counts as invisible
        private const float InvisibleEpsilon = 0.01f;
        // "Minimap size" config clamp
        private const float MinMapSizeScale = 0.5f, MaxMapSizeScale = 2f;
        // layout, in px at column scale 1
        private const float ColumnGapPx = 14f, StatusIconGapPx = 8f, PillGapPx = 8f, TitleExtraWidthPx = 8f, PillLabelPadPx = 8f;
        private const float DataTextSize = 11f, DataLineHeight = 14f, DataPillVerticalPad = 6f;
        private const float DataPillHiddenShare = 0.22f;       // share of the map diameter the data pill tucks under the circle
        private const float DataPillDropShare = 0.92f;         // the pill's bottom sits this share of the radius below the map centre
        private const float LayoutSignatureStepPx = 8f;        // sizes are logged rounded to this step
        // compass badges / ticks / zoom buttons
        private const float RimOutsetPx = 4f, MinBadgeSize = 18f, BadgeSizeShare = 0.11f, MinBadgeFontSize = 11f, BadgeFontShare = 0.07f;
        private const float BadgeOffsetShare = 0.62f, PlateBelowBadgeShare = 1.12f, PlateBelowBadgeGapPx = 8f, DefaultPlateHeight = 60f;
        private const float TickLength = 8f, TickThickness = 2f;
        private const float ZoomStep = 1.5f, ZoomInAngle = 340f, ZoomOutAngle = 315f, ZoomButtonOffsetShare = 0.15f, ZoomGlyphScale = 1.1f;
        // the ring sprite
        private const int RingTextureSize = 256;
        private const float RingThicknessShare = 0.022f;

        private static readonly Color PanelFillColor = new Color(0.086f, 0.067f, 0.047f, 0.92f);
        private static readonly Color BadgeFillColor = new Color(0.086f, 0.067f, 0.047f, 0.82f);
        private static readonly Color PillBackgroundColor = new Color(0.14f, 0.10f, 0.07f, 0.95f);
        private static readonly Color ZoomButtonColor = new Color(0.09f, 0.07f, 0.04f, 0.95f);
        // the plate's own children (everything else under it belongs to another mod)
        private static readonly HashSet<string> OwnPlateChildren = new HashSet<string> { "Fill", "Edge", "Title", "Weather", "Megingjord" };

        private static Minimap s_mm;
        private static RectTransform s_rect;
        private static Vector3 s_origScale = Vector3.one;
        private static GameObject s_frame;
        private static bool s_logged;

        // mask: the vanilla map image + pin root move under it (back on Restore)
        private static RectTransform s_mask;
        private static readonly List<(Transform t, Transform parent, int index)> s_moved = new List<(Transform, Transform, int)>();

        // plate
        private static RectTransform s_plate;
        private static KitLabel s_title, s_weather, s_belt;
        private static RectTransform s_weatherPill, s_beltPill;
        private static float s_nextPlate;

        private static FieldInfo s_zoomField;

        /// <summary>The small minimap's RectTransform while the override is ON and the map exists; null otherwise.</summary>
        internal static RectTransform Rect => s_rect;

        /// <summary>The info plate under the round map (null while OFF): HUD boxes that hang under the minimap anchor below this.</summary>
        internal static RectTransform Plate => s_plate;

        internal static event Action<RectTransform> Changed;

        // ---- Escaping out of a UI could leave the minimap hidden while the small root stayed active (something else hid it).
        // Every 0.5 s: is the round map EFFECTIVELY visible (active in the
        // hierarchy, its canvas on, no parent CanvasGroup at alpha 0, a real scale, on screen)? Each change logs what failed and on which
        // object, with the open UIs and the HUD-hide holders. Self-heal: no UI open, map mode Small, the HUD not hidden by the player or a
        // holder, and not visible for > 1 s -> the failing thing is put back and "[Shell] minimap restored (was hidden by <who>)" logged.
        private static float s_visCheckAt, s_hiddenSince = -1f;
        private static string s_lastVisWhy = "";

        private static string WhyNotVisible(Minimap mm, out GameObject culprit, out CanvasGroup groupCulprit)
        {
            culprit = null; groupCulprit = null;
            GameObject root = mm.m_smallRoot;
            if (!root.activeInHierarchy)
            {
                Transform t = root.transform; while (t != null && t.gameObject.activeSelf) t = t.parent;
                culprit = t != null ? t.gameObject : root;
                return $"'{culprit.name}' inactive{(FiresCore.UI.Dock.DockService.HiddenByHudHide(culprit) ? " (HideHudWhile)" : "")}";
            }
            for (Transform t = root.transform; t != null; t = t.parent)
            {
                var cg = t.GetComponent<CanvasGroup>();
                if (cg != null && cg.alpha < InvisibleEpsilon) { groupCulprit = cg; return $"CanvasGroup alpha 0 on '{t.name}'"; }
                if (t.localScale.x < InvisibleEpsilon || t.localScale.y < InvisibleEpsilon) { culprit = t.gameObject; return $"scale {t.localScale.x:0.00} on '{t.name}'"; }
            }
            var canvas = root.GetComponentInParent<Canvas>();
            if (canvas != null && !canvas.enabled) { culprit = canvas.gameObject; return $"canvas '{canvas.name}' disabled"; }
            if (root.transform is RectTransform rt)
            {
                var corners = new Vector3[4]; rt.GetWorldCorners(corners);
                if (corners[2].x < 0f || corners[0].x > Screen.width || corners[2].y < 0f || corners[0].y > Screen.height) return $"off screen ({corners[0].x:0},{corners[0].y:0})-({corners[2].x:0},{corners[2].y:0})";
            }
            return null;
        }

        private static string OpenUis()
        {
            var open = new System.Collections.Generic.List<string>();
            if (InventoryGui.IsVisible()) open.Add("inventory");
            if (Menu.IsVisible()) open.Add("menu");
            if (StoreGui.IsVisible()) open.Add("store");
            if (TextInput.IsVisible()) open.Add("text input");
            if (Minimap.IsOpen()) open.Add("large map");
            try { if (FiresCore.Bridge.ModUiRegistry.IsAnyOpen()) open.Add("a mod window"); } catch { }
            if (FiresCore.UI.InputBlock.IsBlocked) open.Add("input block");
            return open.Count == 0 ? "none" : string.Join(", ", open.ToArray());
        }

        private static void WatchVisible(Minimap mm)
        {
            if (Time.unscaledTime < s_visCheckAt) return;
            s_visCheckAt = Time.unscaledTime + VisibilityCheckInterval;
            string why = WhyNotVisible(mm, out GameObject culprit, out CanvasGroup cg);
            bool userHidden = Hud.instance != null && Hud.instance.m_userHidden;
            string ui = OpenUis();
            if ((why ?? "") != s_lastVisWhy)
            {
                ShellLog.Info($"minimap: {(why == null ? "visible" : "NOT visible: " + why)} (map mode {mm.m_mode}, open UI: {ui}, HUD hide holders: {FiresCore.UI.Dock.DockService.HudHolders}, HUD hidden by player {userHidden})");
                s_lastVisWhy = why ?? "";
            }
            if (why == null) { s_hiddenSince = -1f; return; }
            bool shouldShow = mm.m_mode == Minimap.MapMode.Small && ui == "none" && !userHidden && !FiresCore.UI.Dock.DockService.HudHeldHidden;
            if (!shouldShow) { s_hiddenSince = -1f; return; }
            if (s_hiddenSince < 0f) { s_hiddenSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - s_hiddenSince < SelfHealAfterHiddenSeconds) return;
            s_hiddenSince = -1f;
            if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = true; }
            else if (culprit != null && !culprit.activeSelf) culprit.SetActive(true);
            else if (culprit != null && (culprit.transform.localScale.x < InvisibleEpsilon || culprit.transform.localScale.y < InvisibleEpsilon)) culprit.transform.localScale = Vector3.one;
            else if (culprit != null && culprit.GetComponent<Canvas>() is Canvas cv) cv.enabled = true;
            ShellLog.Info($"minimap restored (was hidden by {why})");
        }

        /// <summary>Per frame from ShellRuntime.LateUpdate (master ON only).</summary>
        internal static void Tick()
        {
            Minimap mm = Minimap.instance;
            if (mm != s_mm) { Restore(); s_mm = mm; }
            if (mm == null || mm.m_smallRoot == null) return;

            if (s_rect == null)
            {
                s_rect = mm.m_smallRoot.transform as RectTransform;
                if (s_rect == null) return;
                s_origScale = s_rect.localScale;
                s_frame = new GameObject(FrameName, typeof(RectTransform));
                var fr = (RectTransform)s_frame.transform;
                fr.SetParent(s_rect, false);
                fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero;
                fr.SetAsLastSibling();
                try { BuildArt(mm, fr); }
                catch (Exception e) { ShellLog.Warn("minimap art failed (" + e.GetType().Name + ": " + e.Message + "); restoring vanilla"); Restore(); return; }
                if (!s_logged)
                {
                    s_logged = true;
                    ShellLog.Debug($"minimap: round small map (size {s_rect.rect.size}, scale {s_origScale}, moved {s_moved.Count} vanilla node(s) under the mask)");
                }
                RaiseChanged();
            }

            float size = FiresShell.CfgMinimapSize != null ? Mathf.Clamp(FiresShell.CfgMinimapSize.Value, MinMapSizeScale, MaxMapSizeScale) : 1f;
            Vector3 want = s_origScale * size;
            if (s_rect.localScale != want) { s_rect.localScale = want; RaiseChanged(); }

            // every visibility change is logged with what
            // the game says at that moment (vanilla map mode, HUD hidden by the player, small root active). FDT's clean screen calls
            // SetMapMode(None) while a test shoots (TestScreen), which shows up here as "mode None".
            bool visible = mm.m_smallRoot.activeInHierarchy;
            if (visible != s_lastVisible)
            {
                s_lastVisible = visible;
                bool hudHidden = Hud.instance != null && Hud.instance.m_userHidden;
                ShellLog.Info("minimap: " + (visible ? "shown" : "hidden") + " (vanilla map mode " + mm.m_mode + ", HUD hidden by player " + hudHidden
                    + ", small root active " + mm.m_smallRoot.activeSelf + (visible ? "" : mm.m_mode == Minimap.MapMode.None ? "; something called SetMapMode(None), e.g. FDT's clean screen while a test shoots" : "") + ")");
            }

            WatchVisible(mm);

            // hidden while the inventory / crafting is open. Alpha, not
            // SetActive, so the column layout that reads the plate's rect keeps working.
            if (s_plate != null)
            {
                bool invOpen = InventoryGui.instance != null && InventoryGui.IsVisible();
                var plateGroup = s_plate.GetComponent<CanvasGroup>();
                if (plateGroup == null) plateGroup = s_plate.gameObject.AddComponent<CanvasGroup>();
                float plateAlpha = invOpen ? 0f : 1f;
                if (plateGroup.alpha != plateAlpha) { plateGroup.alpha = plateAlpha; plateGroup.blocksRaycasts = !invOpen; ShellLog.Debug("minimap: day/time plate " + (invOpen ? "hidden (inventory open)" : "shown")); }
            }

            SyncMaskedMapMaterial(mm);
            UpdateWatch(mm);   // glass retry, sun/moon, wind, night tint, above the Esc menu
            // The vanilla biome label (other mods append "zone: …" / "x: …, z: …, y: …" lines to the same text) is hidden while ON;
            // its text is read every refresh into the map data pill, and the label comes back on OFF.
            if (mm.m_biomeNameSmall != null && mm.m_biomeNameSmall.gameObject.activeSelf)
            {
                if (s_biomeLabel == null) { s_biomeLabel = mm.m_biomeNameSmall.gameObject; ShellLog.Debug("minimap: vanilla biome label hidden (its lines go to the map data pill)"); }
                mm.m_biomeNameSmall.gameObject.SetActive(false);
            }

            if (Time.unscaledTime >= s_nextPlate) { s_nextPlate = Time.unscaledTime + PlateRefreshInterval; RefreshPlate(); LayoutColumn(); }
        }

        private static GameObject s_biomeLabel;
        private static bool s_lastVisible = true;

        // ---- the right column -------------------------------------
        // map -> plate -> registered boxes in order (territory 10, quest tracker 20, Core's group HUD 30), each at the previous one's
        // real bottom - 8 px, centred on the map's x, clamped on screen. Positions are put back when a box unregisters or on OFF.
        private sealed class ColumnBox { public string Id; public RectTransform Rt; public int Order; public Vector3 OrigPos; public bool Placed; }
        private static readonly List<ColumnBox> s_column = new List<ColumnBox>();
        private static string s_columnSig;

        internal static void Register(string id, RectTransform rt, int order)
        {
            if (string.IsNullOrEmpty(id) || rt == null) return;
            Unregister(id);
            s_column.Add(new ColumnBox { Id = id, Rt = rt, Order = order });
            s_column.Sort((a, b) => a.Order.CompareTo(b.Order));
            ShellLog.Debug("minimap column: registered '" + id + "' (order " + order + ")");
        }

        /// <summary>true when <paramref name="rt"/> is a registered minimap column box.</summary>
        internal static bool IsColumnBox(RectTransform rt) { if (rt == null) return false; foreach (var b in s_column) if (b.Rt == rt) return true; return false; }

        internal static void Unregister(string id)
        {
            for (int i = s_column.Count - 1; i >= 0; i--)
            {
                var b = s_column[i];
                if (b.Id != id) continue;
                if (b.Placed && b.Rt != null) b.Rt.position = b.OrigPos;
                s_column.RemoveAt(i);
            }
        }

        private static void LayoutColumn()
        {
            if (s_rect == null || s_plate == null) return;
            // Core's own group HUD joins the column while the override is ON
            var gh = FiresCore.UI.GroupHud.GroupHudController.Instance;
            var ghRt = gh != null ? gh.RootRect : null;
            if (ghRt != null && !s_groupHudDocked && !s_column.Exists(b => b.Id == "core.grouphud")) Register("core.grouphud", ghRt, 30);   // until it is its own dock element
            if (gh != null) gh.SetShellWidth(s_plate.rect.width);   // the group HUD matches the plate (width + rounded style)

            Vector3[] c = new Vector3[4];
            s_mask.GetWorldCorners(c);
            float mapTop = c[1].y, mapBottom = c[0].y, cx = (c[0].x + c[2].x) * 0.5f;
            ClampOnScreen(s_plate, cx);
            s_plate.GetWorldCorners(c);
            float plateTop = c[1].y, bottom = c[0].y;
            // the boxes below share the PLATE's centre line (the map's,
            // unless the plate had to move off it at a screen edge) and sit a fixed 14 px apart
            cx = (c[0].x + c[2].x) * 0.5f;
            float gap = ColumnGapPx * (s_rect.lossyScale.y > 0f ? s_rect.lossyScale.y : 1f);
            var parts = new List<string> { $"map {mapBottom:0}-{mapTop:0} (dia {s_dataDia:0} local)", $"plate {bottom:0}-{plateTop:0}" };
            int overlaps = 0; bool onScreen = true;
            float prevBottom = bottom;
            // RPGmaker 0.0.594 re-parents its territory box UNDER the plate (before RegisterMinimapColumn existed): its bottom counts too.
            for (int i = 0; i < s_plate.childCount; i++)
            {
                var child = s_plate.GetChild(i) as RectTransform;
                if (child == null || OwnPlateChildren.Contains(child.name) || !child.gameObject.activeInHierarchy) continue;
                DrawnCorners(child, c);   // drawn edges
                if (c[0].y < prevBottom) { prevBottom = c[0].y; parts.Add($"{child.name} (in plate) {c[0].y:0}-{c[1].y:0}"); }
            }
            for (int i = s_column.Count - 1; i >= 0; i--) if (s_column[i].Rt == null) s_column.RemoveAt(i);
            foreach (var b in s_column)
            {
                if (!b.Rt.gameObject.activeInHierarchy) continue;
                // a member the player dragged out of the
                // column is the dock's to place (its own saved spot) until Reset puts it back
                bool keepsOffset = b.Id == GroupHudColumnId;
                if (keepsOffset && FiresCore.UI.Dock.DockService.DetachedFromColumn(b.Rt)) FiresCore.UI.Dock.DockService.MigrateDetachedToColumnOffset(GroupHudDockId);
                if (FiresCore.UI.Dock.DockService.DetachedFromColumn(b.Rt)) { b.Placed = false; parts.Add($"{b.Id} detached (own spot)"); continue; }
                if (!b.Placed) { b.OrigPos = b.Rt.position; b.Placed = true; }
                // the gap is
                // measured between DRAWN edges (every visible graphic under the member: border, chips), not the RectTransform, which the
                // territory box's chips/border draw past
                DrawnCorners(b.Rt, c);
                float top = c[1].y, midX = (c[0].x + c[2].x) * 0.5f;
                float boxGap = keepsOffset ? GroupHudGapPx * colScale : gap;   // the player's image-2 gap above the Group HUD
                var slot = b.Rt.position + new Vector3(cx - midX, (prevBottom - boxGap) - top, 0f);
                if (keepsOffset)
                {
                    // the slot is remembered for the drop; while the player drags it the column leaves it alone
                    s_lastSlot[b.Id] = slot;
                    if (FiresCore.UI.Dock.DockService.IsDragging(b.Rt)) { DrawnCorners(b.Rt, c); parts.Add($"{b.Id} dragging"); prevBottom = Mathf.Min(prevBottom, c[0].y); continue; }
                    if (ColumnOffsets.TryGetValue(b.Id, out var off)) slot += new Vector3(off.x * colScale, off.y * colScale, 0f);
                }
                b.Rt.position = slot;
                DrawnCorners(b.Rt, c);
                if (c[0].y < 0f || c[2].x > Screen.width || c[0].x < 0f) onScreen = false;
                if (c[1].y > prevBottom + 0.5f) overlaps++;
                parts.Add($"{b.Id} {c[0].y:0}-{c[1].y:0}");
                prevBottom = c[0].y;
            }
            string sig = string.Join(", ", parts.ToArray()) + $"; on-screen {(onScreen ? "yes" : "no")}; overlaps {overlaps}; text past plate {TextPastPlates()}";
            if (sig != s_columnSig) { s_columnSig = sig; ShellLog.Debug("minimap column: " + sig); }
        }

        // ---- the Group HUD stays in the column; a drag is kept as an offset from its slot --------
        private const string GroupHudColumnId = "core.grouphud", GroupHudDockId = "hud.grouphud";
        /// <summary>Drawn px (at column scale 1) between the box above and the Group HUD: the player's dragged image-2 spot.</summary>
        private const float GroupHudGapPx = 10f;
        private static readonly Dictionary<string, Vector3> s_lastSlot = new Dictionary<string, Vector3>();
        private static Dictionary<string, Vector2> s_colOffsets;
        private static bool s_offsetHooked;
        private static string OffsetPath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", "column_offsets.txt");
        private static Dictionary<string, Vector2> ColumnOffsets
        {
            get
            {
                if (s_colOffsets != null) return s_colOffsets;
                s_colOffsets = new Dictionary<string, Vector2>(StringComparer.Ordinal);
                try
                {
                    if (System.IO.File.Exists(OffsetPath))
                        foreach (var l in System.IO.File.ReadAllLines(OffsetPath))
                        {
                            var f = l.Split('|');
                            if (f.Length == 3 && float.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                                && float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                                s_colOffsets[f[0]] = new Vector2(x, y);
                        }
                }
                catch { }
                if (!s_offsetHooked) { s_offsetHooked = true; FiresCore.UI.Dock.DockService.Moved += OnColumnMemberMoved; }
                return s_colOffsets;
            }
        }
        private static float colScale => s_rect != null && s_rect.lossyScale.y > 0f ? s_rect.lossyScale.y : 1f;
        private static void SaveColumnOffsets()
        {
            if (FiresCore.UI.Dock.DockService.TestWritesBlocked) { ShellLog.Info("minimap column: offsets kept for this run only (a -firestest round writes nothing into the layout folder)"); return; }
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OffsetPath));
                var lines = new List<string>();
                foreach (var kv in s_colOffsets) lines.Add(kv.Key + "|" + kv.Value.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "|" + kv.Value.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                System.IO.File.WriteAllLines(OffsetPath, lines.ToArray());
            }
            catch (Exception ex) { ShellLog.Info("minimap column: offsets not saved: " + ex.Message); }
        }
        private static void OnColumnMemberMoved(string id, Rect r)
        {
            if (id != GroupHudDockId) return;
            var b = s_column.Find(x => x.Id == GroupHudColumnId);
            if (b == null || b.Rt == null || !s_lastSlot.TryGetValue(b.Id, out var slot)) return;
            var d = (b.Rt.position - slot) / colScale;
            ColumnOffsets[b.Id] = new Vector2(d.x, d.y);
            SaveColumnOffsets();
            ShellLog.Info($"minimap column: {b.Id} dropped {d.x:0},{d.y:0} from its column slot - kept as an offset (it still moves with the boxes above it)");
        }
        /// <summary>Reset puts a column member back on its slot (no offset).</summary>
        internal static void ResetColumnOffset(RectTransform rt)
        {
            var b = s_column.Find(x => x.Rt == rt);
            if (b == null || !ColumnOffsets.Remove(b.Id)) return;
            SaveColumnOffsets();
            ShellLog.Info($"minimap column: {b.Id} offset cleared (reset)");
        }

        // world corners (GetWorldCorners order: BL, TL, TR, BR) of what is DRAWN under rt - the union of its own rect and every
        // active, enabled, visible Graphic below it (borders and chips that draw past the rect). Falls back to the rect alone.
        private static readonly List<UnityEngine.UI.Graphic> s_drawnGraphics = new List<UnityEngine.UI.Graphic>();
        private static readonly Vector3[] s_drawnTmp = new Vector3[4];
        private static void DrawnCorners(RectTransform rt, Vector3[] c)
        {
            rt.GetWorldCorners(c);
            float x0 = c[0].x, y0 = c[0].y, x1 = c[2].x, y1 = c[1].y;
            s_drawnGraphics.Clear();
            rt.GetComponentsInChildren(false, s_drawnGraphics);
            foreach (var g in s_drawnGraphics)
            {
                if (g == null || !g.enabled || g.color.a <= InvisibleEpsilon || g.canvasRenderer == null || g.canvasRenderer.GetAlpha() <= InvisibleEpsilon) continue;
                g.rectTransform.GetWorldCorners(s_drawnTmp);
                x0 = Mathf.Min(x0, s_drawnTmp[0].x); y0 = Mathf.Min(y0, s_drawnTmp[0].y); x1 = Mathf.Max(x1, s_drawnTmp[2].x); y1 = Mathf.Max(y1, s_drawnTmp[1].y);
            }
            c[0] = new Vector3(x0, y0, 0f); c[1] = new Vector3(x0, y1, 0f); c[2] = new Vector3(x1, y1, 0f); c[3] = new Vector3(x1, y0, 0f);
        }

        // how many labels' measured text is wider than the room their plate gives them
        // (the title + each pill label on the plate, the data pill's lines in its visible part). 0 = nothing clipped.
        private static int TextPastPlates()
        {
            int clipped = 0;
            if (s_plate != null && s_title != null)
            {
                float room = s_plate.rect.width - 2f * PlatePad;
                if (s_title.PreferredWidth > room + 1f) clipped++;
                foreach (var (pill, label) in new[] { (s_weatherPill, s_weather), (s_beltPill, s_belt) })
                    if (pill != null && label != null && pill.gameObject.activeSelf && label.PreferredWidth > pill.rect.width - PillLabelPadPx + 1f) clipped++;
            }
            if (s_dataPill != null && s_dataText != null && s_dataPill.gameObject.activeSelf)
            {
                float visible = s_dataPill.rect.width - s_dataDia * DataPillHiddenShare - 2f * PlatePad;
                if (s_dataText.PreferredWidth > visible + 1f) clipped++;
            }
            return clipped;
        }

        // The plate stays centred on the map's x but is pushed fully inside the screen.
        private static void ClampOnScreen(RectTransform rt, float cx)
        {
            // measured in SCREEN pixels through the canvas
            // camera (world corners are only pixels on an overlay canvas), 16 px from the edge; too wide for the room = it grows LEFT
            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);
            var cam = FiresCore.UI.Dock.DockService.Cam(rt);
            Vector2 s0 = RectTransformUtility.WorldToScreenPoint(cam, c[0]), s2 = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            Vector2 sc = RectTransformUtility.WorldToScreenPoint(cam, new Vector3(cx, c[0].y, c[0].z));
            float worldW = c[2].x - c[0].x, pxW = s2.x - s0.x;
            if (worldW <= 0f || pxW <= 0f) return;
            float worldPerPx = worldW / pxW;
            const float margin = 16f;
            float dpx = sc.x - (s0.x + s2.x) * 0.5f;   // re-centre on the map
            float right = s2.x + dpx, left = s0.x + dpx;
            if (right > Screen.width - margin) dpx -= right - (Screen.width - margin);
            left = s0.x + dpx;
            if (left < margin) dpx += margin - left;
            if (Mathf.Abs(dpx) > 0.01f) rt.position += new Vector3(dpx * worldPerPx, 0f, 0f);
        }

        // under a uGUI Mask the map RawImage renders with a STENCIL COPY of
        // vanilla's minimap material, and vanilla writes the explored-map / fog textures and zoom onto the ORIGINAL material every frame,
        // so the copy drew nothing. Each LateUpdate (after vanilla's Update) the copy takes the original's properties, keeping its stencil.
        private static readonly string[] StencilProps = { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask" };
        private static bool s_syncLogged;
        private static void SyncMaskedMapMaterial(Minimap mm)
        {
            var img = mm.m_mapImageSmall;
            if (img == null || s_mask == null) return;
            Material baseMat = img.material, drawn = img.materialForRendering;
            if (baseMat == null || drawn == null || drawn == baseMat) return;
            var saved = new float[StencilProps.Length];
            for (int i = 0; i < StencilProps.Length; i++) saved[i] = drawn.HasProperty(StencilProps[i]) ? drawn.GetFloat(StencilProps[i]) : 0f;
            drawn.CopyPropertiesFromMaterial(baseMat);
            for (int i = 0; i < StencilProps.Length; i++) if (drawn.HasProperty(StencilProps[i])) drawn.SetFloat(StencilProps[i], saved[i]);
            if (!s_syncLogged)
            {
                s_syncLogged = true;
                var tex = baseMat.mainTexture;
                ShellLog.Debug("minimap: map material synced into the masked copy (shader " + baseMat.shader.name + ", main texture "
                    + (tex != null ? tex.name + " " + tex.width + "x" + tex.height : "none") + ", RawImage texture "
                    + (img.texture != null ? img.texture.name + " " + img.texture.width + "x" + img.texture.height : "none")
                    + ", rect in mask " + img.rectTransform.rect.size + ")");
            }
        }

        // ---- art ---------------------------------------------------------------------------------------

        private static void BuildArt(Minimap mm, RectTransform frame)
        {
            var map = mm.m_mapImageSmall != null ? mm.m_mapImageSmall.rectTransform : null;
            if (map == null) return;
            // A map shader without stencil support ignores the Mask: then no mask at all (a square map inside the rim), never a blank map.
            bool maskable = mm.m_mapImageSmall.material != null && mm.m_mapImageSmall.material.HasProperty("_Stencil");
            if (!maskable) ShellLog.Warn("minimap: the map shader '" + (mm.m_mapImageSmall.material != null ? mm.m_mapImageSmall.material.shader.name : "none") + "' has no stencil; the map is not masked round");

            // the round mask, sitting where the map image sat, with the same rect
            var parent = map.parent;
            var maskGo = new GameObject("FiresMinimapMask", typeof(RectTransform));
            s_mask = (RectTransform)maskGo.transform;
            s_mask.SetParent(parent, false);
            s_mask.anchorMin = map.anchorMin; s_mask.anchorMax = map.anchorMax; s_mask.pivot = map.pivot;
            s_mask.anchoredPosition = map.anchoredPosition; s_mask.sizeDelta = map.sizeDelta;
            s_mask.SetSiblingIndex(map.GetSiblingIndex());
            var maskImg = maskGo.AddComponent<Image>(); maskImg.sprite = KitSprites.Circle(); maskImg.raycastTarget = false;
            maskGo.AddComponent<Mask>().showMaskGraphic = false;
            if (maskable)
            {
                Move(map, s_mask);
                // the pin root too, wherever it sits (R-E6 moved only the map: the pin root has another parent)
                if (mm.m_pinRootSmall != null && !mm.m_pinRootSmall.IsChildOf(s_mask)) Move(mm.m_pinRootSmall, s_mask);
            }

            // the rim + ticks + zoom buttons live in our frame, aligned on the map rect
            Vector2 d = s_mask.rect.size;
            float dia = Mathf.Min(d.x, d.y);
            var art = Widgets.Node(frame, "Round");
            art.anchorMin = art.anchorMax = art.pivot = new Vector2(0.5f, 0.5f);
            art.position = s_mask.TransformPoint(s_mask.rect.center);
            art.sizeDelta = new Vector2(dia, dia);

            // POCKET WATCH:
            // glass over the map (the HUD globes' Custom/FiresGlobeGlass when RPGmaker has loaded it, else a Core highlight), a brass
            // case rim, N/E/S/W OUTSIDE the rim, the sun and moon riding the ring in front of everything.
            BuildGlass(art);
            var rim = Widgets.Box(art, "Rim", Ring(), Brass, false);
            Widgets.Stretch(rim.rectTransform, -RimOutsetPx, -RimOutsetPx, -RimOutsetPx, -RimOutsetPx);
            rim.raycastTarget = true;   // the case is the drag handle (only answers while the cursor is free)
            CopperRim(rim);             // the bronze armour's texture + sun-facing shading
            var inner = Widgets.Box(art, "RimInner", Ring(), BrassDark, false);
            Widgets.Stretch(inner.rectTransform, 1f, 1f, 1f, 1f);
            inner.raycastTarget = false;
            AttachDrag(rim.rectTransform);

            float r = dia * 0.5f, badge = Mathf.Max(MinBadgeSize, dia * BadgeSizeShare), fs = Mathf.Max(MinBadgeFontSize, dia * BadgeFontShare);
            foreach (var (letter, ang) in new[] { ("N", 90f), ("E", 0f), ("S", 270f), ("W", 180f) })
            {
                Vector2 dir = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad));
                var tick = Widgets.Box(art, "Tick" + letter, KitSprites.White(), FiresTokens.Gold, false);
                var tr = tick.rectTransform; tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0.5f, 0.5f);
                tr.anchoredPosition = dir * (r + 1f);
                tr.sizeDelta = Mathf.Abs(dir.x) > 0.5f ? new Vector2(TickLength, TickThickness) : new Vector2(TickThickness, TickLength);
                tick.raycastTarget = false;

                var disc = Widgets.Box(art, "Badge" + letter, KitSprites.Circle(), BadgeFillColor, false);
                var dr = disc.rectTransform; dr.anchorMin = dr.anchorMax = dr.pivot = new Vector2(0.5f, 0.5f);
                dr.anchoredPosition = dir * (r + RimOutsetPx + badge * BadgeOffsetShare); dr.sizeDelta = new Vector2(badge, badge);   // outside the rim
                if (letter == "S") s_badgeS = dr;   // the plate starts below this badge's real bottom
                disc.raycastTarget = false;
                var l = KitText.Create(dr, "L", letter, FontRole.SerifBold, fs, letter == "N" ? FiresTokens.Gold : FiresTokens.Text, KitAlign.Center);
                Widgets.Stretch(l.Rect, 0, 0, 0, 0);
                l.Graphic.raycastTarget = false;
            }

            ZoomButton(art, "+", r, ZoomInAngle, badge, fs, 1f / ZoomStep);
            ZoomButton(art, "-", r, ZoomOutAngle, badge, fs, ZoomStep);
            BuildClock(art, r);                    // last: in front of the letters and the +/- (they only pass over them briefly)
            BuildWindPill(dia);

            // the plate under the map: biome / day / time, then the weather + Megingjord pills. (R-E6 "the text is all fucked"):
            // fixed type sizes (no auto-size), and the plate is sized from its measured text in RefreshPlate, never from the map.
            s_plate = Widgets.Node(frame, "Plate");
            s_plate.anchorMin = s_plate.anchorMax = s_plate.pivot = new Vector2(0.5f, 1f);
            // the compass badges sit OUTSIDE the rim (centre r + 4 + 0.62 badge,
            // half a badge tall), so the plate starts below the S badge, not 10 below the map
            s_plateTopLocal = frame.InverseTransformPoint(s_mask.TransformPoint(new Vector2(s_mask.rect.center.x, s_mask.rect.yMin))) + new Vector3(0f, -(RimOutsetPx + badge * PlateBelowBadgeShare + PlateBelowBadgeGapPx), 0f);
            s_plate.localPosition = s_plateTopLocal;
            s_plateMinWidth = dia;
            s_plate.sizeDelta = new Vector2(dia, DefaultPlateHeight);
            // rounded panels, matching all our UIs
            s_title = PlateBox(s_plate, true);
            s_plate.gameObject.AddComponent<PlateClick>();
            // the pill row comes from the plate pill registry (ShellPlatePills.cs)
            BuildDataPill(dia);
            RefreshPlate();
        }

        // a rounded pill off the map's LEFT-BOTTOM, BEHIND the round map (the circle covers its right end), with the
        // biome / zone / coordinate lines left-aligned in the part that shows.
        private static RectTransform s_dataPill;
        private static KitLabel s_dataText;
        private static float s_dataDia;
        private static void BuildDataPill(float dia)
        {
            s_dataDia = dia;
            s_dataPill = Widgets.Node(s_rect, "FiresMinimapDataPill");
            s_dataPill.SetSiblingIndex(s_mask.GetSiblingIndex());   // behind the map (drawn before the mask)
            s_dataPill.anchorMin = s_dataPill.anchorMax = new Vector2(0.5f, 0.5f); s_dataPill.pivot = new Vector2(1f, 0f);
            var fill = Widgets.Box(s_dataPill, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), PanelFillColor, true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = true;   // a click copies the coords
            var edge = Widgets.Box(s_dataPill, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            s_dataText = KitText.Create(s_dataPill, "Text", "", FontRole.SansBold, DataTextSize, FiresTokens.Text, KitAlign.Left, 0f, false, false);
            s_dataText.Graphic.raycastTarget = false;
            s_dataPill.gameObject.AddComponent<CoordsClick>();
            s_dataPill.gameObject.SetActive(false);
        }

        // LEFT click copies "x,z,y" (integers, no spaces, e.g. "430,-1432,56", for scripts and spawner data); RIGHT click
        // copies the vanilla goto form "x z".
        // Clickable whenever the cursor is free (Esc menu: the column is raised above it). The pill shows "Copied" for a moment.
        private static float s_copiedUntil;
        private static string s_copied;
        internal static void CopyCoords(bool gotoForm)
        {
            var p = Player.m_localPlayer;
            if (p == null) return;
            Vector3 pos = p.transform.position;
            int ix = Mathf.RoundToInt(pos.x), iz = Mathf.RoundToInt(pos.z), iy = Mathf.RoundToInt(pos.y);
            s_copied = gotoForm ? ix + " " + iz : ix + "," + iz + "," + iy;
            s_toastNoPrefix = false;
            GUIUtility.systemCopyBuffer = s_copied;
            s_copiedUntil = Time.unscaledTime + CopiedNoticeSeconds;
            ShellLog.Info("minimap: copied coords '" + s_copied + "' (" + (gotoForm ? "goto form, right click" : "x,z,y, left click") + ")");
            LayoutDataPill();
        }

        private static string s_dataSig, s_statusSig;
        private static void LayoutDataPill()
        {
            var mm = Minimap.instance;
            if (s_dataPill == null || mm == null) return;
            string text = mm.m_biomeNameSmall != null ? (mm.m_biomeNameSmall.text ?? "").Trim() : "";
            // an untranslated biome token (no biome yet / unknown) is dropped, never shown.
            var kept = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.IndexOf("biome_", StringComparison.OrdinalIgnoreCase) >= 0 && (trimmed.StartsWith("[") || trimmed.StartsWith("$"))) continue;
                kept.Add(trimmed);
            }
            // vanilla's label line 1 is the
            // biome vanilla read itself (not through Player.GetCurrentBiome); it is replaced with the plate's own name, so both always agree
            if (kept.Count > 0 && Player.m_localPlayer != null)
            {
                string shown = FiresCore.World.BiomeDisplay.CurrentName(Player.m_localPlayer);
                if (!string.IsNullOrEmpty(shown) && kept[0].IndexOf(':') < 0) kept[0] = shown;
            }
            text = string.Join("\n", kept.ToArray());
            // the notice line (a copy, or a toast) sits on top and is not a click target
            s_noticeLine = Time.unscaledTime < s_copiedUntil && kept.Count > 0 ? (s_toastNoPrefix ? s_copied : "Copied " + s_copied) : null;
            if (s_noticeLine != null) text = s_noticeLine + "\n" + text;
            s_dataLines = string.IsNullOrEmpty(text) ? new string[0] : text.Split('\n');
            if (string.IsNullOrEmpty(text)) { s_dataPill.gameObject.SetActive(false); return; }
            s_dataText.Value = text;
            s_dataPill.gameObject.SetActive(true);
            int lines = text.Split('\n').Length;
            float textW = s_dataText.PreferredWidth, textH = lines * DataLineHeight;
            float hidden = s_dataDia * DataPillHiddenShare;                   // the part under the circle
            float w = textW + 2f * PlatePad + hidden + CopyIconW, h = textH + 2f * DataPillVerticalPad;   // room for the coords copy icon
            s_dataPill.sizeDelta = new Vector2(w, h);
            // right end tucked under the map's lower-left: pivot (1,0) placed inside the circle, near its bottom-left
            Vector3 mapCentre = s_rect.InverseTransformPoint(s_mask.TransformPoint(s_mask.rect.center));
            float r = s_dataDia * 0.5f;
            s_dataPill.localPosition = mapCentre + new Vector3(-r + hidden, -r * DataPillDropShare, 0f);
            // the vanilla status-effect icons (top, left of the map) wrap to 2+ rows toward this pill. Measure their real
            // bottom every refresh and keep the pill's top 8 px below it.
            int rows = 0; float statusBottom = float.MaxValue;
            var root = Hud.instance != null ? Hud.instance.m_statusEffectListRoot : null;
            if (root != null)
            {
                var ys = new HashSet<int>(); Vector3[] sc = new Vector3[4];
                foreach (RectTransform icon in root)
                {
                    if (icon == null || !icon.gameObject.activeInHierarchy) continue;
                    icon.GetWorldCorners(sc);
                    ys.Add(Mathf.RoundToInt(sc[1].y));
                    statusBottom = Mathf.Min(statusBottom, sc[0].y);
                }
                rows = ys.Count;
            }
            Vector3[] pc = new Vector3[4];
            s_dataPill.GetWorldCorners(pc);
            float gapW = StatusIconGapPx * (s_rect.lossyScale.y > 0f ? s_rect.lossyScale.y : 1f);
            if (rows > 0 && pc[1].y > statusBottom - gapW)
            {
                float down = pc[1].y - (statusBottom - gapW);
                s_dataPill.position -= new Vector3(0f, down, 0f);
                s_dataPill.GetWorldCorners(pc);
            }
            int overlaps = rows > 0 && pc[1].y > statusBottom - 0.5f ? 1 : 0;
            string ssig = rows + "/" + Mathf.RoundToInt(statusBottom) + "/" + Mathf.RoundToInt(pc[1].y);
            if (ssig != s_statusSig) { s_statusSig = ssig; ShellLog.Debug($"minimap column: status rows {rows}, status bottom {(rows > 0 ? statusBottom.ToString("0") : "-")}, pill top {pc[1].y:0}, overlaps {overlaps}"); }

            var tr = s_dataText.Rect;
            tr.anchorMin = new Vector2(0f, 0f); tr.anchorMax = new Vector2(0f, 1f); tr.pivot = new Vector2(0f, 0.5f);
            tr.anchoredPosition = new Vector2(PlatePad, 0f); tr.sizeDelta = new Vector2(textW + 4f, -12f);
            PlaceCopyIcon(PlatePad + textW + 6f, h);
            string sig = lines + "/" + (Mathf.RoundToInt(w / LayoutSignatureStepPx) * 8);   // not the coordinates (they change every step)
            if (sig != s_dataSig)
            {
                s_dataSig = sig;
                bool zone = text.Contains("zone");
                ShellLog.Debug($"minimap data pill: '{text.Replace("\n", " | ")}' ({lines} line(s), text width {textW:0}, pill {w:0}x{h:0}){(zone ? "" : "; no zone line in the vanilla label (the mod that adds it is not installed or puts it elsewhere)")}");
            }
        }

        private const float TitleSize = 15f, PillSize = 12f, PlatePad = 10f, TitleRow = 22f, PillRow = 22f, RowGap = 6f;
        private static Vector3 s_plateTopLocal;
        private static float s_plateMinWidth;

        private static RectTransform PillNode(RectTransform plate, string name, out KitLabel label, Color color)
        {
            var pill = Widgets.Node(plate, name);
            pill.anchorMin = pill.anchorMax = pill.pivot = new Vector2(0.5f, 1f);
            var bg = Widgets.Box(pill, "Bg", KitSprites.Pill(), PillBackgroundColor, true);
            Widgets.Stretch(bg.rectTransform, 0, 0, 0, 0); bg.raycastTarget = false;
            label = KitText.Create(pill, "L", "", FontRole.SansBold, PillSize, color, KitAlign.Center, 0f, false, false);
            Widgets.Stretch(label.Rect, 0, 0, 0, 0); label.Graphic.raycastTarget = false;
            return pill;
        }

        // Lays the plate out from its measured text: title row, then the visible pills on one row when they fit, else one per row.
        private static void LayoutPlate()
        {
            float titleW = s_title.PreferredWidth;
            var pills = PlatePillRow();
            float rowW = 0f; foreach (var p in pills) rowW += p.w; rowW += Mathf.Max(0, pills.Count - 1) * PillGapPx;
            float width = Mathf.Max(s_plateMinWidth, titleW + 2f * PlatePad + TitleExtraWidthPx, rowW + 2f * PlatePad);
            bool oneRow = rowW + 2f * PlatePad <= width;
            float y = PlatePad;
            s_title.Rect.anchoredPosition = new Vector2(0f, -y); s_title.Rect.sizeDelta = new Vector2(width - 2f * PlatePad, TitleRow);
            y += TitleRow + RowGap;
            if (oneRow && pills.Count > 0)
            {
                float x = -rowW * 0.5f;
                foreach (var p in pills) { p.rt.sizeDelta = new Vector2(p.w, PillRow); p.rt.anchoredPosition = new Vector2(x + p.w * 0.5f, -y); x += p.w + PillGapPx; }
                y += PillRow + RowGap;
            }
            else foreach (var p in pills) { p.rt.sizeDelta = new Vector2(p.w, PillRow); p.rt.anchoredPosition = new Vector2(0f, -y); y += PillRow + RowGap; }
            s_plate.sizeDelta = new Vector2(width, y - RowGap + PlatePad);
            // the default top is 6 px
            // below the S badge's real bottom, measured in world space (the badge sizes are in the art's units, not the frame's); a spot the
            // player dragged it to is kept as an offset from that default (saved) and never overwritten; no re-placing during a drag
            if (!PlateDragging())
            {
                var basePos = s_plateTopLocal;
                if (s_badgeS != null && s_plate.parent != null)
                {
                    var bc = new Vector3[4]; s_badgeS.GetWorldCorners(bc);
                    float badgeBottom = s_plate.parent.InverseTransformPoint(bc[0]).y;
                    // 14 screen px under the S badge,
                    // the same gap the column keeps below the plate (Group HUD), so all three are evenly spaced. Default only: a saved offset is kept.
                    float gap = ColumnGapPx / Mathf.Max(0.0001f, s_plate.parent.lossyScale.y);   // 14 screen px in the parent's units (overlay canvas)
                    if (basePos.y > badgeBottom - gap) basePos.y = badgeBottom - gap;
                }
                s_plateBase = basePos;
                s_plate.localPosition = basePos + (Vector3)PlateUserOffset;
            }
            // clamp in the same call as the resize, so a wider plate never draws off screen for a frame (default spot only)
            if (s_mask != null && PlateUserOffset == Vector2.zero && !PlateDragging()) { var mc = new Vector3[4]; s_mask.GetWorldCorners(mc); ClampOnScreen(s_plate, (mc[0].x + mc[2].x) * 0.5f); }
            // the clock text changes the title width every minute; log only a real change of
            // the plate's SIZE (rounded to 8 px) or of its pill rows.
            string sig = (Mathf.RoundToInt(width / LayoutSignatureStepPx) * 8) + "x" + (Mathf.RoundToInt(s_plate.sizeDelta.y / LayoutSignatureStepPx) * 8) + "/" + pills.Count + (oneRow ? "r" : "s");
            if (sig != s_plateSig)
            {
                s_plateSig = sig;
                ShellLog.Debug($"minimap plate: title width {titleW:0} + pills row {rowW:0} -> plate {width:0}x{s_plate.sizeDelta.y:0} ({(oneRow ? "one pill row" : "pills stacked")})");
            }
        }
        private static string s_plateSig;

        // ---- the player's own spot for the Day/Time capsule (an offset from its default under the map), saved per machine ----
        private static RectTransform s_badgeS;
        private static Vector3 s_plateBase;
        private static Vector2? s_plateOffset;
        private static string PlateOffsetPath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", "plate_offset.txt");
        private static Vector2 PlateUserOffset
        {
            get
            {
                if (!s_plateOffset.HasValue)
                {
                    s_plateOffset = Vector2.zero;
                    try
                    {
                        if (System.IO.File.Exists(PlateOffsetPath))
                        {
                            var p = System.IO.File.ReadAllText(PlateOffsetPath).Split(',');
                            if (p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                                && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)) s_plateOffset = new Vector2(x, y);
                        }
                    }
                    catch { }
                }
                return s_plateOffset.Value;
            }
        }
        private static bool PlateDragging() => FiresCore.UI.Dock.DockService.DraggedElement?.O.Id == "hud.plates";
        /// <summary>The dock moved the capsule (a player drag, a nudge, or a reset): keep that spot as the offset from the default.</summary>
        internal static void OnPlateMoved()
        {
            if (s_plate == null) return;
            var off = (Vector2)(s_plate.localPosition - s_plateBase);
            if (off.sqrMagnitude < 1f) off = Vector2.zero;
            s_plateOffset = off;
            if (!FiresCore.UI.Dock.DockService.TestWritesBlocked)   // a -firestest round never writes into the layout folder
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlateOffsetPath));
                System.IO.File.WriteAllText(PlateOffsetPath, off.x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "," + off.y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
            ShellLog.Info(off == Vector2.zero ? "minimap plate: back at its default spot under the map" : $"minimap plate: placed by the player, offset {off.x:0},{off.y:0} from its default (kept)");
        }
        internal static void ResetPlateOffset() { s_plateOffset = Vector2.zero; OnPlateMovedReset(); }
        private static void OnPlateMovedReset()
        {
            if (!FiresCore.UI.Dock.DockService.TestWritesBlocked) try { if (System.IO.File.Exists(PlateOffsetPath)) System.IO.File.WriteAllText(PlateOffsetPath, "0,0"); } catch { }
            ShellLog.Info("minimap plate: reset to its default spot under the map");
        }

        private static void ZoomButton(RectTransform art, string glyph, float r, float angle, float size, float fs, float factor)
        {
            Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
            var disc = Widgets.Box(art, "Zoom" + (glyph == "+" ? "In" : "Out"), KitSprites.Circle(), ZoomButtonColor, false);
            var dr = disc.rectTransform; dr.anchorMin = dr.anchorMax = dr.pivot = new Vector2(0.5f, 0.5f);
            dr.anchoredPosition = dir * (r + size * ZoomButtonOffsetShare); dr.sizeDelta = new Vector2(size, size);
            var ring = Widgets.Box(dr, "Ring", Ring(), FiresTokens.Edge, false);
            Widgets.Stretch(ring.rectTransform, 0, 0, 0, 0); ring.raycastTarget = false;
            var l = KitText.Create(dr, "L", glyph, FontRole.SansBold, fs * ZoomGlyphScale, FiresTokens.Gold, KitAlign.Center);
            Widgets.Stretch(l.Rect, 0, 0, 0, 0); l.Graphic.raycastTarget = false;
            ShellWidgets.Clickable(dr, () => Zoom(factor));
        }

        // Vanilla keeps the small map's zoom in a private field; the buttons scale it inside vanilla's own range.
        private static void Zoom(float factor)
        {
            var mm = Minimap.instance;
            if (mm == null) return;
            if (s_zoomField == null) s_zoomField = typeof(Minimap).GetField("m_smallZoom", BindingFlags.Instance | BindingFlags.NonPublic);
            if (s_zoomField == null) return;
            float oldZoom = (float)s_zoomField.GetValue(mm);
            float newZoom = Mathf.Clamp(oldZoom * factor, mm.m_minZoom, mm.m_maxZoom);
            s_zoomField.SetValue(mm, newZoom);
            ShellLog.Info("minimap: zoom " + oldZoom.ToString("0.000") + " -> " + newZoom.ToString("0.000"));   // Info (proof the button reached the map)
        }

        /// <summary>The plate's box: rounded fill + edge + the serif title, top-anchored. One look for the unified plate and the
        /// non-unified day / time box (VanillaDayClock, Fire 10-24: "same box we use on the unified one same style and everything").</summary>
        internal static KitLabel PlateBox(RectTransform plate, bool fillCatchesClicks)
        {
            var fill = Widgets.Box(plate, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), PanelFillColor, true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = fillCatchesClicks;   // unified: right-click = the pill picker
            var edge = Widgets.Box(plate, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            var title = KitText.Create(plate, "Title", "", FontRole.SerifBold, TitleSize, FiresTokens.Text, KitAlign.Center, 0f, false, false);
            title.Rect.anchorMin = title.Rect.anchorMax = title.Rect.pivot = new Vector2(0.5f, 1f);
            return title;
        }

        /// <summary>The plate's title line: "biome · Day N · HH:MM".</summary>
        internal static string PlateTitleText()
        {
            var p = Player.m_localPlayer;
            string biome = p != null ? FiresCore.World.BiomeDisplay.CurrentName(p) : "";   // + the alt biome ("Plains - Heath")
            string day = EnvMan.instance != null && ZNet.instance != null ? "Day " + EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()) : "";
            string time = "";
            if (EnvMan.instance != null)
            {
                float dayFraction = EnvMan.instance.GetDayFraction();
                int mins = Mathf.FloorToInt(dayFraction * 24f * 60f);
                time = (mins / 60).ToString("00") + ":" + (mins % 60).ToString("00");
            }
            var parts = new List<string>();
            foreach (var s in new[] { biome, day, time }) if (!string.IsNullOrEmpty(s)) parts.Add(s);
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>The plate's title-row metrics (pad, row height, extra width) for a box that holds only the title.</summary>
        internal static Vector2 PlateTitleBoxSize(KitLabel title, float minWidth)
        {
            float width = Mathf.Max(minWidth, title.PreferredWidth + 2f * PlatePad + TitleExtraWidthPx);
            title.Rect.anchoredPosition = new Vector2(0f, -PlatePad); title.Rect.sizeDelta = new Vector2(width - 2f * PlatePad, TitleRow);
            return new Vector2(width, TitleRow + 2f * PlatePad);
        }

        private static void RefreshPlate()
        {
            if (s_title == null) return;
            try
            {
                s_title.Value = PlateTitleText();

                LayoutPlate();
                LayoutDataPill();
            }
            catch (Exception e) { ShellLog.Debug("minimap plate refresh: " + e.Message); }
        }

        private static void Move(Transform t, Transform to)
        {
            ShellLog.Debug("minimap: moved '" + t.name + "' (from '" + (t.parent != null ? t.parent.name : "-") + "') under the round mask");
            s_moved.Add((t, t.parent, t.GetSiblingIndex()));
            t.SetParent(to, true);
        }

        private static Sprite s_ring;
        private static Sprite Ring()
        {
            if (s_ring != null) return s_ring;
            const int N = RingTextureSize;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave, name = "FiresMinimapRing" };
            var px = new Color32[N * N];
            float c = (N - 1) * 0.5f, outer = c, inner = c - N * RingThicknessShare;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dist = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(outer - dist + 0.5f) * Mathf.Clamp01(dist - inner + 0.5f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply(false);
            s_ring = Sprite.Create(tex, new UnityEngine.Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            s_ring.hideFlags = HideFlags.DontSave;
            return s_ring;
        }

        /// <summary>Puts every vanilla value back and destroys what the shell added (master OFF / a new Minimap).</summary>
        internal static void Restore()
        {
            bool had = s_rect != null;
            try
            {
                for (int i = s_moved.Count - 1; i >= 0; i--)
                {
                    var (t, parent, index) = s_moved[i];
                    if (t == null || parent == null) continue;
                    t.SetParent(parent, true);
                    t.SetSiblingIndex(Mathf.Min(index, parent.childCount - 1));
                }
                s_moved.Clear();
                RestoreWatch();   // map tint, Esc canvas, dragged position, wind pill
                if (s_biomeLabel != null) { s_biomeLabel.SetActive(true); s_biomeLabel = null; }
                if (s_dataPill != null) { UnityEngine.Object.Destroy(s_dataPill.gameObject); s_dataPill = null; s_dataText = null; s_dataSig = null; s_copyIcon = null; }
                foreach (var b in s_column) if (b.Placed && b.Rt != null) { b.Rt.position = b.OrigPos; b.Placed = false; }
                s_column.RemoveAll(b => b.Id == "core.grouphud");
                FiresCore.UI.GroupHud.GroupHudController.Instance?.SetShellWidth(0f);   // its own size + style again
                s_columnSig = null; s_plateSig = null; s_syncLogged = false;
                // Other mods parent their boxes under the plate (RPGmaker's territory box, by reflection on MinimapPlateRect): hand
                // anything that is not ours back to the vanilla small root before our nodes are destroyed.
                if (s_plate != null && s_rect != null)
                {
                    for (int i = s_plate.childCount - 1; i >= 0; i--)
                    {
                        var child = s_plate.GetChild(i);
                        if (OwnPlateChildren.Contains(child.name)) continue;
                        child.SetParent(s_rect, true);
                        ShellLog.Debug("minimap restore: handed '" + child.name + "' back to the vanilla small map");
                    }
                }
                if (s_mask != null)
                {
                    for (int i = s_mask.childCount - 1; i >= 0; i--) s_mask.GetChild(i).SetParent(s_mask.parent, true);   // anything else parented there
                    UnityEngine.Object.Destroy(s_mask.gameObject);
                }
                if (s_rect != null && s_rect.localScale != s_origScale) s_rect.localScale = s_origScale;
                if (s_frame != null) UnityEngine.Object.Destroy(s_frame);
            }
            catch (Exception e) { ShellLog.Warn("minimap restore threw " + e.GetType().Name + ": " + e.Message); }
            s_frame = null; s_rect = null; s_mm = null; s_mask = null; s_plate = null;
            s_title = s_weather = s_belt = null; s_weatherPill = s_beltPill = null;
            RestorePlatePills();
            if (had) { ShellLog.Debug("minimap: restored to vanilla"); RaiseChanged(); }
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(s_rect); }
            catch (Exception e) { ShellLog.Warn("MinimapChanged handler threw " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
