// ONE sticky-dock engine for the HUD and the inventory: any element can be repositioned and docked to the nearest UI,
// fully customizable. Rules + API: Tools\CORE_DOCK_ENGINE_SPEC.md and
// Tools\CORE_HUD_LAYOUT_PLAN.md. Add-only contract.
//
// Rules: snap within 24 px (1080p reference, canvas-scaled) between facing edges, docked gap 16 px, MAGNET preview (the dragged
// element slides to its docked spot while in range), any edge (side by side aligned top or bottom, stacked aligned left or right),
// docking unions groups, dragging a member moves its group, the grip of a docked member scales the whole group, a floating element
// keeps its own size and grip, Alt+drag undocks one member, user scale 0.5..1.5 on top of the element's base scale, clamped on
// screen, double-click a grip = reset, per-resolution save (HUD: per character too), unknown ids in the file kept.
// A docked element keeps an ANCHOR (the element it docked to, the edge, the alignment) and is re-placed from the anchor's LIVE rect
// every LateUpdate, so a member that grows (the grid when a chest opens) pushes the ones docked to it, and a hidden member's
// dependents take its place (they anchor through it to the next shown element). Core sets ONLY position + localScale.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Dock
{
    public enum DockEdge { None = 0, Left = 1, Right = 2, Top = 3, Bottom = 4 }

    /// <summary>How an element joins the dock engine. Add-only.</summary>
    public sealed class DockElementOptions
    {
        public string Id;
        public string Owner;
        public string Layer = "hud";          // "hud" | "inventory" (docking only within a layer)
        public RectTransform Rect;            // Core sets ONLY its position + localScale
        public RectTransform DragHandle;      // null = the whole rect (with an edit-mode overlay on the HUD layer)
        public bool ShowGrip = true;          // corner grip (scale)
        public Func<Rect> DefaultRect;        // optional: default top-left (x, y screen px, y down); size ignored
        public string DefaultDockTo;          // optional: dock to this id by default ...
        public DockEdge DefaultEdge;          // ... on this edge of it (the element goes on that side of the target)
        public Func<bool> Visible;            // optional: hidden = its dependents take its place; rejoins when shown
        public string Label;                  // name shown in layout mode (default = Id)
        /// <summary>The element's base localScale (0 = its localScale at Register). The user / group scale multiplies it.</summary>
        public float BaseScale;
        /// <summary>A passive element is only a reference (e.g. a FollowId target such as a grid row): no handles,
        /// never dragged, not a snap target, not saved; its owner (or its parent transform) positions it.</summary>
        public bool Passive;
        /// <summary>Keep a fixed offset to another element's LIVE rect (any layer) while that element is shown, e.g. the vanilla
        /// hotbar following the inventory grid's 1-8 row. Not docking (no snap / group). Dragging the follower sets a new offset.</summary>
        public string FollowId;
        /// <summary>The follower's top-left minus the target's top-left, screen px (y down). NaN = the offset at the first frame both exist.</summary>
        public Vector2 FollowOffset = new Vector2(float.NaN, float.NaN);
        /// <summary>An element that is often hidden (build info, ship controls, messages ...)
        /// is shown while HUD layout mode is on so it can be placed; its owner hides it again as usual once layout mode ends.</summary>
        public bool ShowInLayout;
        /// <summary>A locked element (e.g. the crosshair): Passive in the engine (no drag, not saved, not a snap target) but
        /// outlined and named in layout mode so the player sees it is part of the HUD.</summary>
        public bool OutlineOnly;
        /// <summary>A HUD element (Layer "hud") that is also movable in the INVENTORY layout session (health, hotbars). Its spot
        /// stays the HUD layout's (one source of truth); it snaps / docks only with HUD elements. Add-only.</summary>
        public bool AlsoInInventoryLayout;
        /// <summary>A LIVE panel, movable and resizable at any time, no editor: drag its <see cref="DragHandle"/> (header / frame) to move
        /// (docks gently on release; Shift = free), its bottom-right handle to resize. No full-rect grab area, so its contents stay
        /// clickable. Requires DragHandle. The Alt editor still adds its extra options. Add-only.</summary>
        public bool LiveEdit;
        /// <summary>SIZE resize instead of scale. When set, the grips change the rect's sizeDelta (top-left
        /// stays put; the hub's ShellWindowDrag resize, Core's standard) and call this with the new size (canvas units) so the owner can
        /// re-lay its content (e.g. grid columns/rows). The size is the owner's to save and re-apply; Core still saves position. Add-only.</summary>
        public Action<Vector2> SizeChanged;
        /// <summary>The smallest size (canvas units) a SizeChanged resize may reach. Add-only.</summary>
        public Vector2 MinSize = new Vector2(40f, 40f);
        /// <summary>Called on a reset so the owner clears its saved size and re-lays at its
        /// default. Unset = Core puts the rect back to its size at Register and calls SizeChanged (an owner that applies a saved size
        /// before registering should set SizeReset). Add-only.</summary>
        public Action SizeReset;
        /// <summary>With the cursor free, a double-click on the element
        /// reveals its grab area + resize handle (move / resize directly, no editor) until a click elsewhere or Esc. Add-only.</summary>
        public bool RevealOnDoubleClick;
        /// <summary>Never docked: no magnet for others, never docks when dragged, and a saved dock relation is dropped on load (its
        /// saved top-left is kept). For a panel whose SIZE changes per use (a chest: small / reinforced / big), so the player's spot is
        /// its TOP-LEFT corner and a bigger one grows down / right from it (Fire 10-23). Screen-edge snap still applies. Add-only.</summary>
        public bool NoDock;
        /// <summary>The grip scales the element PER AXIS. A mostly
        /// horizontal drag changes only localScale.x, a mostly vertical one only localScale.y, a diagonal one both; 0.3..2.5 per axis;
        /// saved as x + y; Reset = 1,1 x BaseScale. Not sizeDelta (no SizeChanged needed). Add-only.</summary>
        public bool ScalePerAxis;
        /// <summary>The rect that IS the element's art (hit area, outline and grips use exactly it) when the graphic walk over
        /// its children would be wrong (hidden / transparent placeholders far from the art). Null = the walk. Add-only.</summary>
        public RectTransform HitBounds;
        /// <summary>A HUD element with a SECOND spot used only while
        /// the inventory is open. Its HUD spot is untouched. Default while the inventory is open: its drawn part centred in the free gap
        /// between vainv.character and vainv.crafting, same height (only when it fits; otherwise it stays at its HUD spot). Dragging it in
        /// the inventory layout saves that spot ("&lt;id&gt;@inventory" in dock_inventory_*). Never a clamp or dodge. Add-only.</summary>
        public bool InventorySpot;
    }

    public static class DockService
    {
        // Magnet range 24 -> 10 px (gentle, never a yank); holding Shift while dragging = no snap at all
        // Scale range 0.5-1.5 -> 0.25-3
        public const float SnapPx = 10f, GapPx = 16f, UndockPx = 80f, MinScale = 0.25f, MaxScale = 3f;

        internal sealed class Element
        {
            public DockElementOptions O;
            public string Group;              // null = floating
            public float Base = 1f;           // base localScale
            public float User = 1f;           // user x group scale, 0.5..1.5 (localScale = Base x User)
            public float Aspect = 1f;         // Height / width user scale (localScale.y = Base x User x Aspect)
            public Vector2 DefaultSize;       // The rect size a reset returns a SizeChanged element to
            public Vector2 DefaultTopLeft;
            public string Anchor; public DockEdge Edge; public bool AlignEnd;   // docked to Anchor on Edge; AlignEnd = bottom / right aligned
            // The dock a pulled member had
            public bool HasLastDock; public string LastAnchor, LastGroup; public DockEdge LastEdge; public bool LastAlignEnd; public Vector2 LastOffset, LastSpot;
            public bool DragStartDocked; public string DragStartAnchor, DragStartGroup; public DockEdge DragStartEdge; public bool DragStartAlignEnd; public Vector2 DragStartOffset, DragStartSpot;
            public GameObject Overlay;
            public RectTransform Grip;
            public readonly List<RectTransform> Grips = new List<RectTransform>();   // All four corners
            public Vector2 Committed; public bool HasCommit;
            public Vector2 InvCommitted; public bool HasInvCommit;
            public bool FromEmbedded;   // Its spot came from the built-in default table (not a player file)   // The InventorySpot spot the player set (none = the computed default)
            public Vector2 FollowOffset; public bool HasFollow;
            public bool OwnerDefault;
            public int ShownFrame = -1; public bool ShownVal;   // IsShown once per frame
            public bool OwnerPlaced;   // MarkOwnerPlaced (OwnerPositions stays set)
            public int FightFrames; public bool OwnerPositions;   // Its owner rewrites its position every frame; Core stands down
            public Vector2 DockOffset;          // Docked member's offset from the dock rule's spot (keeps an owner default exact)
            public Vector2? PendingOwnerSpot;   // Set by Reset; the reflow turns it into DockOffset once the anchor is placed   // The default spot came from the owner (DefaultRect / SetDefault), not from the register-time spot   // The committed top-left of a root / floating element
        }

        private static readonly Dictionary<string, Element> s_els = new Dictionary<string, Element>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> s_baseById = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, SavedEl> s_saved = new Dictionary<string, SavedEl>(StringComparer.Ordinal);
        internal const float MinAspect = 0.25f, MaxAspect = 4f;
        internal const float AxisMin = 0.3f, AxisMax = 2.5f;
        internal static float ClampUser(Element e, float v) => e != null && e.O.ScalePerAxis ? Mathf.Clamp(v, AxisMin, AxisMax) : Mathf.Clamp(v, MinScale, MaxScale);
        internal static float ClampAspect(Element e, float v) => e != null && e.O.ScalePerAxis ? Mathf.Clamp(v, AxisMin / AxisMax, AxisMax / AxisMin) : Mathf.Clamp(v, MinAspect, MaxAspect);
        private sealed class SavedEl { public float X, Y, User = 1f, OX, OY, Aspect = 1f; public string Group, Layer, Anchor; public DockEdge Edge; public bool AlignEnd; public bool Hidden; public bool Embedded; }
        private static string s_loadedFor;
        private static DockTicker s_ticker;
        private static DragState s_dragging;

        /// <summary>(id, new screen rect: x, y from the top-left, w, h) after a committed move / dock / scale.</summary>
        public static event Action<string, Rect> Moved;

        /// <summary>True while HUD-layer elements take drags. Menu.IsVisible() is also true for any popup (hub, NPC book,
        /// loading screen), so it is not used: ONLY the "Edit layout"
        /// pill turns it on, and only in the world with a local player.</summary>
        public static bool HudEditMode => s_hudToggle && InWorld;   // The Esc menu no longer opens the editor (see EscEditMode)
        private static bool s_hudToggle;
        // While the REAL Esc pause dialog (Menu.m_menuDialog, never any other popup) is open, the HUD layer takes drags and grips; it is saved when the
        // dialog closes.
        // Esc shows the plain pause menu, no outlines /
        // tags / toolbar / list. HUD elements are simply dragged where they sit and resized from a bottom-right handle shown on hover;
        // the editor stays behind the layout pill.
        private static bool s_escEdit;
        /// <summary>True while the Esc pause menu is open in the world (direct drag / hover resize; no editor chrome). Add-only.</summary>
        public static bool EscEditMode => s_escEdit && !s_hudToggle && InWorld;
        // A press over one of the pause menu's own buttons is the menu's (no element drag starts there)
        // The menu's buttons are collected ONCE per menu opening (no array
        // allocated every frame), and the answer is recomputed only when the pointer moves or the menu opens / closes
        private static readonly List<Selectable> s_menuSel = new List<Selectable>();
        private static readonly HashSet<string> s_menuHitLogged = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_menuSelFor, s_overMenuLast; private static Vector2 s_overMenuAt = new Vector2(-1f, -1f);
        private static bool PointerOverMenuButton()
        {
            try
            {
                var d = Menu.instance != null ? Menu.instance.m_menuDialog : null;
                bool open = d != null && d.gameObject.activeInHierarchy;
                if (!open) { if (s_menuSelFor) { s_menuSel.Clear(); s_menuSelFor = false; s_overMenuAt = new Vector2(-1f, -1f); } return s_overMenuLast = false; }
                if (!s_menuSelFor) { s_menuSel.Clear(); d.GetComponentsInChildren(false, s_menuSel); s_menuSelFor = true; s_overMenuAt = new Vector2(-1f, -1f); }
                Vector2 mp = UnityEngine.Input.mousePosition;
                if (mp == s_overMenuAt) return s_overMenuLast;
                s_overMenuAt = mp; s_overMenuLast = false;
                foreach (var b in s_menuSel)
                {
                    if (b == null || !b.isActiveAndEnabled) continue;
                    var rt = b.transform as RectTransform;
                    if (rt == null) continue;
                    // A full-screen Selectable read as "on a menu button" would switch every dock raycaster off: a Selectable covering a large part of the screen (a full-screen
                    // blocker / background) is not a button
                    var sr = ScreenRect(rt);
                    if (sr.width * sr.height > Screen.width * Screen.height * 0.15f) continue;
                    if (RectTransformUtility.RectangleContainsScreenPoint(rt, mp, Cam(rt))) { if (s_menuHitLogged.Add(b.name)) FiresCore.Logging.FiresLogger.LogInfo($"[Dock] pause-menu button under the pointer: {b.name} {sr.width:0}x{sr.height:0}"); return s_overMenuLast = true; }
                }
            }
            catch { }
            return s_overMenuLast;
        }
        private static bool EscDialogOpen()
        {
            try { return FiresCore.UI.Shell.FiresShell.OverrideOn && Menu.instance != null && Menu.instance.m_menuDialog != null && Menu.instance.m_menuDialog.gameObject.activeInHierarchy; }
            catch { return false; }
        }
        private static bool SafeVisible(Element e) { try { return e.O.Visible == null || e.O.Visible(); } catch { return true; } }

        /// <summary>The inventory layer (grid rows, crafting dock ...) is laid out only after its "Edit layout" pill too.</summary>
        public static bool InventoryEditMode => s_invToggle && InWorld && InventoryGui.IsVisible();
        private static bool s_invToggle;

        // a SCREEN layer per Fires window. An owner registers its window's panels with Layer = ScreenLayer("<its ModUi key>")
        // ("screen.<key>"); while any of them is on screen, Alt shows the Edit layout pill, and that screen's panels get the inventory
        // editor's tools (outline, drag, corner grips, dock, Reset, Cancel, Esc). Saved per screen + resolution (dock_screen.<key>_WxH).
        public const string ScreenLayerPrefix = "screen.";
        public static string ScreenLayer(string screenKey) => ScreenLayerPrefix + (screenKey ?? "");
        internal static bool IsScreenLayer(string layer) => layer != null && layer.StartsWith(ScreenLayerPrefix, StringComparison.Ordinal);
        private static string s_screenEdit;

        /// <summary>The screen layer whose panels are on screen right now (the first found), or null.</summary>
        internal static string OpenScreenLayer()
        {
            if (!InWorld) return null;
            foreach (var e in s_els.Values)
                if (IsScreenLayer(e.O.Layer) && e.O.Rect != null && e.O.Rect.gameObject.activeInHierarchy && !e.O.Passive) return e.O.Layer;
            return null;
        }

        internal static bool ScreenOpen(string layer)
        {
            foreach (var e in s_els.Values)
                if (e.O.Layer == layer && e.O.Rect != null && e.O.Rect.gameObject.activeInHierarchy) return true;
            return false;
        }

        /// <summary>The screen layer being edited, or null.</summary>
        public static string ScreenEditLayer => s_screenEdit != null && InWorld && ScreenOpen(s_screenEdit) ? s_screenEdit : null;

        internal static bool InWorld => Player.m_localPlayer != null && Hud.instance != null && !IsHudHidden
            && (Hud.instance.m_loadingProgress == null || !Hud.instance.m_loadingProgress.activeInHierarchy);

        /// <summary>The editor's selected element (name pill or click); it wins the pick under the cursor.</summary>
        internal static string Selected;
        /// <summary>Elements hidden from view while editing (the legend's Hide toggle); shown again when editing ends.</summary>
        internal static readonly HashSet<string> EditHidden = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<GameObject> s_hiddenByEditor = new HashSet<GameObject>();

        /// <summary>Opens / closes the layout editor for a layer ("hud" or "inventory"). Closing saves.</summary>
        // The layer as it was when its editor opened; Cancel (or Esc) puts it back exactly.
        private sealed class Snap { public Vector2 TL; public float User, Aspect = 1f; public string Group, Anchor; public DockEdge Edge; public bool AlignEnd, HasFollow; public Vector2 Follow; }
        private static readonly Dictionary<string, Dictionary<string, Snap>> s_editSnap = new Dictionary<string, Dictionary<string, Snap>>();

        private static void TakeSnapshot(string layer)
        {
            var d = new Dictionary<string, Snap>(StringComparer.Ordinal);
            foreach (var e in s_els.Values)
                if ((e.O.Layer == layer || (layer == "inventory" && e.O.Layer == "hud" && e.O.AlsoInInventoryLayout)) && e.O.Rect != null && !e.O.Passive)   // + the HUD elements editable here
                    d[e.O.Id] = new Snap { TL = TopLeft(e.O.Rect), User = e.User, Aspect = e.Aspect, Group = e.Group, Anchor = e.Anchor, Edge = e.Edge, AlignEnd = e.AlignEnd, HasFollow = e.HasFollow, Follow = e.FollowOffset };
            s_editSnap[layer] = d;
            var hid = new HashSet<string>(StringComparer.Ordinal);   // Cancel restores the visibility too
            foreach (var e in s_els.Values) if (e.O.Layer == layer && EditHidden.Contains(e.O.Id)) hid.Add(e.O.Id);
            s_hiddenSnap[layer] = hid;
        }
        private static readonly Dictionary<string, HashSet<string>> s_hiddenSnap = new Dictionary<string, HashSet<string>>();

        /// <summary>Cancel: the layer back exactly as it was when its editor opened, saved that way, editor closed.</summary>
        public static void CancelEdit(string layer)
        {
            if (s_editSnap.TryGetValue(layer, out var d))
            {
                foreach (var kv in d)
                {
                    if (!s_els.TryGetValue(kv.Key, out var e) || e.O.Rect == null) continue;
                    var sn = kv.Value;
                    e.User = sn.User; e.Aspect = sn.Aspect; e.Group = sn.Group; e.Anchor = sn.Anchor; e.Edge = sn.Edge; e.AlignEnd = sn.AlignEnd; e.HasFollow = sn.HasFollow; e.FollowOffset = sn.Follow;
                    Place(e.O.Rect, e.Base * e.User, sn.TL, e.Aspect);
                }
                CommitAll(layer);   // Commit the restored spots before the reflow
                if (layer == "inventory") CommitAll("hud");   // The HUD elements moved in this session
                Reflow();
                CommitAll(layer);
                if (layer == "inventory") CommitAll("hud");
                Log("edit cancelled: " + layer + " back as it was");
            }
            if (s_hiddenSnap.TryGetValue(layer, out var hid))
            {
                foreach (var e in s_els.Values) if (e.O.Layer == layer) { if (hid.Contains(e.O.Id)) EditHidden.Add(e.O.Id); else EditHidden.Remove(e.O.Id); }
                s_hiddenSnap.Remove(layer);
            }
            s_editSnap.Remove(layer);
            if (layer == "inventory") { s_invToggle = false; Save("inventory"); if (AnyCrossLayer()) Save("hud"); }
            else if (IsScreenLayer(layer)) { if (s_screenEdit == layer) s_screenEdit = null; Save(layer); }
            else if (s_hudToggle) SetHudEditMode(false);
        }

        private static bool AnyCrossLayer() { foreach (var e in s_els.Values) if (e.O.Layer == "hud" && e.O.AlsoInInventoryLayout) return true; return false; }

        public static void SetEditMode(string layer, bool on)
        {
            if (IsScreenLayer(layer))
            {
                if (on == (s_screenEdit == layer)) return;
                if (on) { if (s_screenEdit != null) SetEditMode(s_screenEdit, false); s_screenEdit = layer; TakeSnapshot(layer); }
                else { s_screenEdit = null; Save(layer); }
                Log(layer + " edit mode " + (on ? "on" : "off (saved)"));
                return;
            }
            if (layer == "inventory")
            {
                if (s_invToggle == on) return;
                s_invToggle = on;
                if (on) TakeSnapshot("inventory");
                if (!on) { Save("inventory"); if (AnyCrossLayer()) Save("hud"); }   // HUD spots moved here live in dock_hud
                Log("inventory edit mode " + (on ? "on" : "off (saved)"));
                return;
            }
            SetHudEditMode(on);
        }

        /// <summary>Turns HUD layout mode on/off outside the Esc menu (the hub toggle).</summary>
        public static void SetHudEditMode(bool on)
        {
            if (s_hudToggle == on) return;
            s_hudToggle = on;
            if (on) TakeSnapshot("hud");
            try
            {
                if (on && Menu.instance != null && Menu.IsVisible()) Menu.instance.Hide();   // the editor replaces the Esc menu
                FiresCore.UI.InputBlock.Block(on);   // free cursor, no player input / camera while editing
            }
            catch (Exception e) { Log("edit mode switch: " + e.Message); }
            if (!on) { Save("hud"); Selected = null; }
            Log("hud edit mode " + (on ? "on" : "off (saved)"));
        }

        // Only ONE element's
        // hit area takes the mouse at a time: the selected one if it is under the cursor, else the smallest under it. A click selects it.
        private static string s_pick;
        internal static string Picked => s_pick;
        // A press may never reach the element's grab area when another canvas (the pause menu's, vanilla HUD canvases without the
        // grab area's sorting) takes the raycast. A press on the picked element whose top raycast hit is NOT one of the dock's own handles / grips starts the move
        // directly from here and follows the mouse until release, logging which object took the pointer instead.
        private static DragState s_direct;
        internal static bool DirectDragActive => s_direct != null;
        private static Vector2 s_directLast;
        private static readonly List<UnityEngine.EventSystems.RaycastResult> s_pickHits = new List<UnityEngine.EventSystems.RaycastResult>();
        private static bool TickDirectDrag()
        {
            if (s_direct == null) return false;
            if (UnityEngine.Input.GetMouseButton(0))
            {
                Vector2 mp = UnityEngine.Input.mousePosition, d = mp - s_directLast; s_directLast = mp;
                if (d.sqrMagnitude > 0f) DragBy(s_direct, new Vector2(d.x, -d.y));
            }
            else { var s = s_direct; s_direct = null; EndDrag(s); }
            return true;
        }
        // With InventoryGui open, presses on the NPC book's canvas are swallowed, so a live panel's header never starts
        // a drag: a press inside a shown LiveEdit panel's header (DragHandle) rect whose top raycast hit is neither the dock's handle
        // nor a button / input (a close X, a tab) starts the move directly, as TryDirectDrag does for the HUD.
        private static void TryLiveHeaderDirect()
        {
            if (PointerOverMenuButton()) return;
            Vector2 mp = UnityEngine.Input.mousePosition;
            Element hit = null; float best = float.MaxValue;
            foreach (var e in s_els.Values)
            {
                if (!e.O.LiveEdit || e.O.DragHandle == null || e.O.Passive || !IsShown(e) || !e.O.DragHandle.gameObject.activeInHierarchy) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(e.O.DragHandle, mp, Cam(e.O.DragHandle))) continue;
                var r = ScreenRect(e.O.DragHandle); float area = r.width * r.height;
                if (area < best) { best = area; hit = e; }
            }
            if (hit == null) return;
            var es = UnityEngine.EventSystems.EventSystem.current;
            string top = "nothing";
            if (es != null)
            {
                s_pickHits.Clear();
                es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = mp }, s_pickHits);
                if (s_pickHits.Count > 0 && s_pickHits[0].gameObject != null)
                {
                    var go = s_pickHits[0].gameObject;
                    if (go.GetComponentInParent<FiresCore.UI.Shell.ShellWindowDrag>() != null) return;   // A grip keeps its own drag; Core's own grab area (DockDragHandle) no longer defers: the direct drag runs from Core's mouse polling   // the normal path has it
                    if (go.GetComponentInParent<Selectable>() != null || go.GetComponentInParent<TMPro.TMP_InputField>() != null) return;   // a real control keeps its click
                    var cv = go.GetComponentInParent<Canvas>();
                    if (cv != null && (cv.rootCanvas.name == "FiresDockLayoutOverlay" || cv.rootCanvas.name == DockToggle.CanvasName || cv.rootCanvas.name == "FiresDockEditPill")) return;
                    if (!go.transform.IsChildOf(hit.O.Rect)) { ForeignTopHit(hit.O.Id, go, cv); return; }   // A foreign top hit keeps the press
                    top = $"{go.name} (canvas {(cv != null ? cv.rootCanvas.name + " order " + cv.sortingOrder : "none")})";
                }
            }
            s_direct = BeginDrag(hit, UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt));
            s_directLast = mp;
            Log($"{hit.O.Id}: move drag start (direct, live header; the pointer's top hit was {top})");
        }

        // One line per foreign top hit name (not per press)
        private static readonly HashSet<string> s_foreignLogged = new HashSet<string>();
        private static void ForeignTopHit(string id, GameObject go, Canvas cv)
        {
            string key = go.name + "|" + (cv != null ? cv.rootCanvas.name : "");
            if (s_foreignLogged.Add(key))
                Log($"{id}: press NOT taken - the pointer's top hit is another UI over it: {go.name} (canvas {(cv != null ? cv.rootCanvas.name + " order " + cv.sortingOrder : "none")}); it keeps the press (logged once per object)");
        }

        // Fire 10-23 (non-unified UI): pressing on one of our panels dragged the vanilla pause menu (and clicks fell through to Logout):
        // the press picked the SMALLEST dock element under the raw mouse, whatever was drawn on top. Now the element that owns the
        // topmost UI raycast hit is the one dragged; a top hit that belongs to no dock element (another window, a menu button) keeps
        // the press. One Debug line per press with the picked vs the hit canvas.
        private static bool TopHitOwner(GameObject go, ref Element pick, string what)
        {
            Element owner = null; float best = float.MaxValue;
            foreach (var e in s_els.Values)
            {
                if (e.O.Passive || e.O.Rect == null || !IsShown(e) || !go.transform.IsChildOf(e.O.Rect)) continue;
                float area = e.O.Rect.rect.width * e.O.Rect.rect.height;
                if (area < best) { best = area; owner = e; }
            }
            var cv = go.GetComponentInParent<Canvas>();
            string hitCanvas = cv != null ? cv.rootCanvas.name + " order " + cv.rootCanvas.sortingOrder : "none";
            if (owner == null)
            {
                Log($"{what}: press over '{go.name}' (canvas {hitCanvas}) belongs to no dock element; picked '{pick?.O.Id}' NOT dragged, the UI on top keeps the press");
                return false;
            }
            if (pick != owner) Log($"{what}: picked '{pick?.O.Id}' under the mouse, but the top hit '{go.name}' (canvas {hitCanvas}) is '{owner.O.Id}'; dragging that");
            pick = owner;
            return true;
        }

        private static void TryDirectDrag(Element pick)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            string top = "nothing";
            if (es != null)
            {
                s_pickHits.Clear();
                es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = UnityEngine.Input.mousePosition }, s_pickHits);
                if (s_pickHits.Count > 0 && s_pickHits[0].gameObject != null)
                {
                    var go = s_pickHits[0].gameObject;
                    if (!FiresCore.UI.Shell.FiresShell.OverrideOn && !TopHitOwner(go, ref pick, "layout drag")) return;
                    // ours: the normal handler path runs (and logs its own start / refusal)
                    if (go.GetComponentInParent<FiresCore.UI.Shell.ShellWindowDrag>() != null) return;   // A grip keeps its own drag; Core's own grab area (DockDragHandle) no longer defers: the direct drag runs from Core's mouse polling
                    var cv = go.GetComponentInParent<Canvas>();
                    // the editor's own chrome (tags, buttons, legend, docking toggle) and the pause menu keep their clicks
                    if (cv != null && (cv.rootCanvas.name == "FiresDockLayoutOverlay" || cv.rootCanvas.name == DockToggle.CanvasName)) return;
                    if (Menu.instance != null && Menu.instance.m_menuDialog != null && go.transform.IsChildOf(Menu.instance.m_menuDialog) && go.GetComponentInParent<Selectable>() != null) return;
                    // Another mod's UI on top that handles the pointer
                    // (its own drag / click / control) keeps the press
                    if (!go.transform.IsChildOf(pick.O.Rect) && OwnPointerControl(go, null)) return;
                    // Presses on the minimap's +/- buttons were taken by FiresDockOverlay: the Esc-menu grab area sits over the element's own
                    // buttons. A press whose first hit UNDER the grab area is a button inside the element clicks that button instead.
                    if (go.name == "FiresDockOverlay" && go.transform.IsChildOf(pick.O.Rect))
                        for (int i = 1; i < s_pickHits.Count; i++)
                        {
                            var under = s_pickHits[i].gameObject;
                            if (under == null || under.name == "FiresDockOverlay") continue;
                            if (!under.transform.IsChildOf(pick.O.Rect)) break;
                            var btn = under.GetComponentInParent<Button>();
                            if (btn == null || !btn.transform.IsChildOf(pick.O.Rect) || !btn.interactable) break;
                            btn.onClick.Invoke();
                            Log($"{pick.O.Id}: press on its own button '{btn.name}' under the grab area: clicked it (no drag)");
                            return;
                        }
                    // The element was picked by GEOMETRY and the top hit only vetoed the press when it was a control; a
                    // foreign raycast blocker (EWPFD_ClickBlocker: an invisible Image, no handlers) let the press fall through to the dock
                    // element underneath. Any top hit that is not part of the picked element now keeps the press: nothing is dragged.
                    if (!go.transform.IsChildOf(pick.O.Rect)) { ForeignTopHit(pick.O.Id, go, cv); return; }
                    top =$"{go.name} (canvas {(cv != null ? cv.rootCanvas.name + " order " + cv.sortingOrder : "none")})";
                }
            }
            if (!CanDrag(pick)) { Log($"{pick.O.Id}: move drag (direct) refused ({(!IsShown(pick) ? "not shown" : "not editable now")})"); return; }
            s_direct = BeginDrag(pick, UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt));
            s_directLast = UnityEngine.Input.mousePosition;
            Selected = pick.O.Id;
            Log($"{pick.O.Id}: move drag start (direct; the pointer's top hit was {top}) ({s_direct.Moving.Count} moving{(pick.Group != null ? ", group " + pick.Group : "")})");
        }

        // ---- Default OFF, saved ----------
        private static bool? s_dockingOn;
        private static string DockingPath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", "docking.txt");
        /// <summary>True = magnets / groups / edge snap while dragging. Default ON, saved per machine. Add-only.</summary>
        public static bool DockingOn
        {
            get
            {
                if (!s_dockingOn.HasValue)
                {
                    // Default ON (the magnet works by drag; Shift = free placement; the pill is the override). A saved "0"
                    // from the old default-OFF builds is flipped to ON ONCE (marker file), after that the player's choice sticks.
                    try
                    {
                        string marker = DockingPath + ".v2";
                        if (!File.Exists(marker))
                        {
                            s_dockingOn = true;
                            if (TestLaunch && !TestAllowSave) { Log("docking: default ON (a -firestest round writes nothing into the layout folder)"); return true; }
                            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DockingPath));
                            File.WriteAllText(DockingPath, "1"); File.WriteAllText(marker, "default ON since 0.2.595");
                            Log("docking: default ON (one-time switch from the old default OFF)");
                        }
                        else s_dockingOn = !File.Exists(DockingPath) || File.ReadAllText(DockingPath).Trim() != "0";
                    }
                    catch { s_dockingOn = true; }
                }
                return s_dockingOn.Value;
            }
            set
            {
                s_dockingOn = value;
                if (TestLaunch && !TestAllowSave) { Log($"docking {(value ? "ON" : "OFF")} for this run only (a -firestest round never writes docking.txt)"); return; }
                try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DockingPath)); File.WriteAllText(DockingPath, value ? "1" : "0"); } catch (Exception ex) { Log("docking setting not saved: " + ex.Message); }
                Log($"docking {(value ? "ON (magnets, groups, edge snap)" : "OFF (free placement)")}");
            }
        }
        // In ANY mode, while the pointer
        // is over one of the pause menu's buttons, every raycaster the dock owns (editor overlay canvas, docking toggle, element grab
        // areas, grips) is switched off, so the press always reaches the menu. Restored the moment the pointer leaves the buttons.
        private static bool s_menuGuard;
        private static readonly List<GraphicRaycaster> s_guardRc = new List<GraphicRaycaster>();
        private static bool s_pickDirty;
        internal static void GuardMenuButtons()
        {
            if (!s_menuGuard && !EscDialogOpen()) return;   // Nothing to guard while the pause menu is closed
            bool block = PointerOverMenuButton();
            // An UNCHANGED state does nothing. Before, while the
            // pointer rested on a menu button, every frame re-ran 3x GameObject.Find + component walks over every element and grip.
            if (block == s_menuGuard) return;
            s_guardRc.Clear();
            foreach (var name in new[] { "FiresDockLayoutOverlay", DockToggle.CanvasName, "FiresDockEditPill" })
            {
                var go = GameObject.Find(name);
                if (go != null) s_guardRc.AddRange(go.GetComponentsInChildren<GraphicRaycaster>(true));
            }
            foreach (var e in s_els.Values)
            {
                if (e.Overlay != null) { var r = e.Overlay.GetComponent<GraphicRaycaster>(); if (r != null) s_guardRc.Add(r); }
                foreach (var g in e.Grips) if (g != null) { var r = g.GetComponent<GraphicRaycaster>(); if (r != null) s_guardRc.Add(r); }            }
            foreach (var r in s_guardRc) if (r != null && r.enabled == block) r.enabled = !block;
            if (block != s_menuGuard) { s_menuGuard = block; if (block) Log($"pause-menu guard: pointer on a menu button, {s_guardRc.Count} dock raycaster(s) off"); }
        }

        // While attached, the column places it (the dock stands down: OwnerPositions). A drag detaches it: the dock owns its spot from then
        // on (saved with the layout) and the column closes the gap. Reset re-attaches it. Persisted per machine.
        private static HashSet<string> s_colDetached;
        private static string ColumnPath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", "column_detached.txt");
        private static HashSet<string> ColDetached
        {
            get
            {
                if (s_colDetached == null)
                {
                    s_colDetached = new HashSet<string>(StringComparer.Ordinal);
                    try { if (File.Exists(ColumnPath)) foreach (var l in File.ReadAllLines(ColumnPath)) if (l.Trim().Length > 0) s_colDetached.Add(l.Trim()); } catch { }
                }
                return s_colDetached;
            }
        }
        private static void SaveColDetached()
        {
            if (TestWritesBlocked) { Log("column detach list kept for this run only (a -firestest round writes nothing into the layout folder)"); return; }
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ColumnPath)); File.WriteAllLines(ColumnPath, new List<string>(ColDetached).ToArray()); }
            catch (Exception ex) { Log("column detach list not saved: " + ex.Message); }
        }
        /// <summary>True when the element on <paramref name="rt"/> was dragged out of the minimap column (the dock places it). Add-only.</summary>
        public static bool DetachedFromColumn(RectTransform rt)
        {
            if (rt == null) return false;
            foreach (var e in s_els.Values) if (e.O.Rect == rt) return ColDetached.Contains(e.O.Id);
            return false;
        }
        internal static void TickColumnMembers()
        {
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null || !FiresCore.UI.Shell.ShellMinimap.IsColumnBox(e.O.Rect)) continue;
                bool column = !ColDetached.Contains(e.O.Id);
                if (column && !e.OwnerPositions) e.OwnerPositions = true;
                else if (!column && e.OwnerPositions && e.FightFrames == 0 && !e.OwnerPlaced) e.OwnerPositions = false;
            }
        }
        /// <summary>A column member the player had already given a saved spot (before it was a column member) keeps that spot:
        /// it is detached from the column at once. No saved spot = it stays in the column (its default place). Add-only.</summary>
        internal static void DetachFromColumnIfSaved(string id)
        {
            if (id == null || KeepsColumnOffset(id) || !s_els.TryGetValue(id, out var e) || !s_saved.ContainsKey(id) || ColDetached.Contains(id)) return;   // + offset members stay in the column
            Log($"{id}: has a saved spot from before it joined the minimap column - keeping it (detached)");
            DetachFromColumn(e);
        }

        /// <summary>A detached Group HUD sat at a fixed spot and the territory box grew onto it, so these column members are never detached by a drag; the drop is kept as an
        /// OFFSET from the member's column slot (ShellMinimap), so they still slide with the boxes above them.</summary>
        internal static bool KeepsColumnOffset(string id) => id == "hud.grouphud";
        /// <summary>True while the player drags the element on <paramref name="rt"/> (the column leaves it to the drag).</summary>
        internal static bool IsDragging(RectTransform rt)
        {
            if (rt == null || s_dragging == null) return false;
            foreach (var m in s_dragging.Moving) if (m.O.Rect == rt) return true;
            return false;
        }
        /// <summary>The one-time migration of a Group HUD the player had detached earlier: back in the column (the column
        /// keeps it centred, a gap under the box above); written once, logged.</summary>
        internal static bool MigrateDetachedToColumnOffset(string id)
        {
            if (!KeepsColumnOffset(id) || !ColDetached.Remove(id)) return false;
            SaveColDetached();
            if (s_els.TryGetValue(id, out var e)) { e.OwnerPositions = true; e.FightFrames = 0; }
            Log($"{id}: was detached from the minimap column (a saved spot from before 0.2.712) - back in the column once (one-time migration); a drag now keeps an offset from its column slot");
            return true;
        }

        private static void DetachFromColumn(Element e)
        {
            if (e != null && KeepsColumnOffset(e.O.Id)) return;   // Kept in the column; the drop becomes an offset (ShellMinimap)
            if (e?.O.Rect == null || !FiresCore.UI.Shell.ShellMinimap.IsColumnBox(e.O.Rect) || !ColDetached.Add(e.O.Id)) return;
            e.OwnerPositions = false; e.FightFrames = 0;
            SaveColDetached();
            Log($"{e.O.Id}: detached from the minimap column (own spot from now on; Reset puts it back)");
        }
        private static void ReattachToColumn(Element e)
        {
            if (e != null && KeepsColumnOffset(e.O.Id)) FiresCore.UI.Shell.ShellMinimap.ResetColumnOffset(e.O.Rect);   // Reset = no offset
            if (e == null || !ColDetached.Remove(e.O.Id)) return;
            SaveColDetached();
            Log($"{e.O.Id}: back in the minimap column (reset)");
        }

        internal static long WillRenderProbeTicks;   // The pre-render re-apply's time, for the cost line
        internal static string CostMode => (EscEditMode ? "Esc menu" : HudEditMode ? "HUD editor" : InventoryEditMode ? "inventory editor" : ScreenEditLayer != null ? "screen editor" : "no editor") + ", " + s_els.Count + " elements";

        /// <summary>The element's owner places it (the dock still drags it and raises Moved on the drop, but never re-places it).</summary>
        private static void OwnerPlacedReset(Element e) { if (e.O.Id == "hud.plates") FiresCore.UI.Shell.ShellMinimap.ResetPlateOffset(); }
        internal static void MarkOwnerPlaced(string id) { var e = Find(id); if (e != null) { e.OwnerPositions = true; e.OwnerPlaced = true; } }

        /// <summary>Any layout moving is possible right now (Esc menu, HUD / inventory / screen editor).</summary>
        internal static bool AnyMoveMode => EscEditMode || HudEditMode || InventoryEditMode || ScreenEditLayer != null || s_revealed != null;   // + a revealed element

        // Raw mouse, mouse-DOWN only (the HUD canvas has no raycaster): two presses on the same RevealOnDoubleClick element within 0.35 s
        // reveal it (grab area + handle) until a press elsewhere, Esc or the cursor locking again; then its spot is saved.
        private static string s_revealed, s_lastDownId; private static float s_lastDownAt;
        private static void TickReveal()
        {
            if (!Cursor.visible) return;
            Vector2 m = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
            Element hit = null; float best = float.MaxValue;
            foreach (var e in s_els.Values)
            {
                if (!e.O.RevealOnDoubleClick || e.O.Rect == null || !IsShown(e)) continue;
                var r = VisibleScreenRect(e); if (!r.Contains(m)) continue;
                float area = r.width * r.height; if (area < best) { best = area; hit = e; }
            }
            if (s_revealed != null)
            {
                var cur = Find(s_revealed);
                bool inside = cur != null && cur.O.Rect != null && GrowBy(VisibleScreenRect(cur), 24f).Contains(m);   // the handle sits at the corner
                if (!inside) EndReveal("click elsewhere");
                return;
            }
            if (hit == null) { s_lastDownId = null; return; }
            if (s_lastDownId == hit.O.Id && Time.unscaledTime - s_lastDownAt <= 0.35f)
            {
                s_revealed = hit.O.Id; Selected = hit.O.Id; s_lastDownId = null;
                Log($"{hit.O.Id}: revealed by double-click (drag to move, corner handle to resize; click elsewhere or Esc to finish)");
            }
            else { s_lastDownId = hit.O.Id; s_lastDownAt = Time.unscaledTime; }
        }
        // Rule (ui-panels-drag-resize-without-editor): with the cursor free, every registered element moves by click-drag ANYWHERE on its
        // DRAWN body, no editor / Alt. A press on an element's drawn bounds ARMS a move; it starts after 6 px of travel (a plain click still
        // clicks), and never when the press landed on a control that handles the pointer itself (button, input field, item slot, scroll).
        // The drag target is the drawn bounds (VisibleScreenRect), so a LiveEdit element whose handle rect is tiny drags from all of it.
        private static Element s_freeArm; private static Vector2 s_freeArmAt;
        private static readonly List<UnityEngine.EventSystems.RaycastResult> s_fHits = new List<UnityEngine.EventSystems.RaycastResult>();
        private static Vector2 s_fLastPos = new Vector2(-1f, -1f); private static bool s_fLast; private static string s_fLastId;
        /// <summary>True when, at the pointer, the first raycast hit that is NOT the dock's own (grab areas, grips, editor canvases) is
        /// another mod's UI that handles the pointer itself and is not part of the element. Re-raycast only when the pointer moves.</summary>
        private static bool ForeignPointerOnTop(Element e)
        {
            Vector2 mp = UnityEngine.Input.mousePosition;
            if (mp == s_fLastPos && s_fLastId == e.O.Id) return s_fLast;
            s_fLastPos = mp; s_fLastId = e.O.Id; s_fLast = false;
            var es = UnityEngine.EventSystems.EventSystem.current; if (es == null) return false;
            s_fHits.Clear(); es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = mp }, s_fHits);
            foreach (var h in s_fHits)
            {
                var go = h.gameObject; if (go == null) continue;
                if (go.GetComponentInParent<DockDragHandle>() != null || go.GetComponentInParent<FiresCore.UI.Shell.ShellWindowDrag>() != null) continue;   // ours
                var cv = go.GetComponentInParent<Canvas>(); string root = cv != null ? cv.rootCanvas.name : "";
                if (root == "FiresDockLayoutOverlay" || root == DockToggle.CanvasName || root == "FiresDockEditPill") continue;
                if (go.transform.IsChildOf(e.O.Rect)) return s_fLast = false;   // the element itself is on top
                return s_fLast = OwnPointerControl(go, null);
            }
            return false;
        }
        /// <summary>One Info line per left press: where, what the EventSystem hit (or nothing), and the dock's gate state.</summary>
        private static void LogPress()
        {
            Vector2 mp = UnityEngine.Input.mousePosition;
            string top = "no hit (no raycaster there)";
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) top = "no EventSystem";
            else
            {
                s_fHits.Clear(); es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = mp }, s_fHits);
                if (s_fHits.Count > 0 && s_fHits[0].gameObject != null) { var g = s_fHits[0].gameObject; var cv = g.GetComponentInParent<Canvas>(); top = g.name + " (canvas " + (cv != null ? cv.rootCanvas.name + " order " + cv.sortingOrder : "none") + ")"; }
            }
            Vector2 m = new Vector2(mp.x, Screen.height - mp.y); string under = "none";
            float best = float.MaxValue;
            foreach (var e in s_els.Values) { if (e.O.Passive || e.O.Rect == null || !IsShown(e)) continue; var r = VisibleScreenRect(e); if (!r.Contains(m)) continue; float a = r.width * r.height; if (a < best) { best = a; under = e.O.Id; } }
            FiresCore.Logging.FiresLogger.LogInfo($"[Dock] press at {mp.x:0},{mp.y:0}: cursor {(Cursor.visible ? "free" : "LOCKED (the camera has the mouse: no UI press possible)")}, top hit {top}, dock element under it {under}, mode {CostMode}, dragging {(s_dragging != null ? s_dragging.E.O.Id : "-")}");
        }
        /// <summary>A drag whose end event never came (its window closed mid-drag) kept s_dragging set and silently blocked every later
        /// press; it is released after half a second with no button held.</summary>
        private static float s_stuckSince = -1f;
        private static void TickStuckDrag()
        {
            if (s_dragging == null || s_direct != null || UnityEngine.Input.GetMouseButton(0)) { s_stuckSince = -1f; return; }
            if (s_stuckSince < 0f) { s_stuckSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - s_stuckSince < 0.5f) return;
            var s = s_dragging; s_stuckSince = -1f;
            FiresCore.Logging.FiresLogger.LogInfo($"[Dock] stuck drag of {s.E.O.Id} released (no end event, no button held)");
            try { EndDrag(s); } catch (Exception ex) { s_dragging = null; Log("stuck drag end threw " + ex.Message); }
        }
        private static readonly HashSet<string> s_dragTargetLogged = new HashSet<string>(StringComparer.Ordinal);
        private static void ArmFreeDrag()
        {
            if (!Cursor.visible || s_dragging != null) return;
            Vector2 mp = UnityEngine.Input.mousePosition, m = new Vector2(mp.x, Screen.height - mp.y);
            Element hit = null; float best = float.MaxValue; Rect hitR = default;
            foreach (var e in s_els.Values)
            {
                if (e.O.Passive || e.O.Rect == null || !IsShown(e)) continue;
                var r = VisibleScreenRect(e); if (!r.Contains(m)) continue;
                float area = r.width * r.height; if (area < best) { best = area; hit = e; hitR = r; }
            }
            if (hit == null) return;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
            {
                s_pickHits.Clear();
                es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = mp }, s_pickHits);
                if (s_pickHits.Count > 0 && s_pickHits[0].gameObject != null)
                {
                    var go = s_pickHits[0].gameObject;
                    if (!FiresCore.UI.Shell.FiresShell.OverrideOn && !TopHitOwner(go, ref hit, "free drag")) return;   // non-unified: drag only what is really under the pointer
                    if (go.GetComponentInParent<FiresCore.UI.Shell.ShellWindowDrag>() != null) return;   // A grip keeps its own drag; Core's own grab area (DockDragHandle) no longer defers: the direct drag runs from Core's mouse polling   // ours: normal path
                    if (OwnPointerControl(go, hit.O.Rect)) return;   // the element's own control keeps the press
                    if (!go.transform.IsChildOf(hit.O.Rect))
                    {
                        // Something else is
                        // on top (another mod's window, the pause menu). If it handles the pointer in ANY way (Selectable, drag / down / click
                        // handler, scroll, input) the press is its own, never a dock move.
                        if (OwnPointerControl(go, null)) return;
                    }
                }
            }
            s_freeArm = hit; s_freeArmAt = mp;
            if (s_dragTargetLogged.Add(hit.O.Id)) FiresCore.Logging.FiresLogger.LogInfo($"[Dock] drag target '{hit.O.Id}' = whole bounds {hitR.width:0}x{hitR.height:0}");
        }
        private static bool OwnPointerControl(GameObject go, Transform elementRoot)
        {
            // only the CHILDREN of the element count (its root may carry the owner's own drag / double-click handlers, which must not block)
            for (var t = go.transform; t != null && t != elementRoot; t = t.parent)
            {
                foreach (var c in t.GetComponents<MonoBehaviour>())
                {
                    if (c == null || !c.enabled) continue;
                    if (c is DockDragHandle || c is FiresCore.UI.Shell.ShellWindowDrag) return false;
                    if (c is Selectable || c is ScrollRect || c is TMPro.TMP_InputField) return true;
                    if (c is UnityEngine.EventSystems.IBeginDragHandler || c is UnityEngine.EventSystems.IPointerDownHandler || c is UnityEngine.EventSystems.IPointerClickHandler) return true;   // item slots (UIInputHandler), owner widgets
                }
                if (t.GetComponent<Canvas>() != null && t.GetComponent<Canvas>().isRootCanvas) break;
            }
            return false;
        }
        private static bool TickFreeArm()
        {
            if (!UnityEngine.Input.GetMouseButton(0)) { s_freeArm = null; return false; }
            Vector2 mp = UnityEngine.Input.mousePosition;
            if ((mp - s_freeArmAt).sqrMagnitude < 36f) return false;   // 6 px: still a click
            var e = s_freeArm; s_freeArm = null;
            if (e == null || e.O.Rect == null) return false;
            // A press
            // inside a LIVE panel's drawn bottom-right corner zone (32 GUI square) RESIZES that panel, whatever is on top there
            if (e.O.LiveEdit && !e.O.ScalePerAxis && InCornerZone(e, s_freeArmAt))
            {
                s_freeScale = e; s_freeScaleFrom = s_freeArmAt; s_freeScaleUser = e.User; s_freeScaleRect = VisibleScreenRect(e, false);
                Selected = e.O.Id;
                Log($"{e.O.Id}: grip FiresDockGrip3 drag start (corner zone press at {s_freeArmAt.x:0},{s_freeArmAt.y:0}; scale {e.User:0.00})");
                return true;
            }
            s_direct = BeginDrag(e, UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt));
            s_directLast = s_freeArmAt;   // the 6 px already travelled count
            Selected = e.O.Id;
            Log($"{e.O.Id}: move drag start (free: anywhere on its body, no editor) ({s_direct.Moving.Count} moving)");
            return true;
        }
        // Corner-zone resize (see TickFreeArm)
        private static Element s_freeScale; private static Vector2 s_freeScaleFrom; private static float s_freeScaleUser; private static Rect s_freeScaleRect;
        private const float CornerZoneGui = 32f;
        private static string s_lastCornerId; private static float s_lastCornerAt;

        /// <summary>Is <paramref name="mp"/> (screen px, y up) inside the element's drawn bottom-right corner zone?</summary>
        internal static bool InCornerZone(Element e, Vector2 mp)
        {
            var r = VisibleScreenRect(e, true);   // y down, the on-screen part
            if (r.width < 1f || r.height < 1f) return false;
            float z = CornerZoneGui * ResScale(), x = mp.x, yDown = Screen.height - mp.y;
            // On a SMALL element (under 3 zones wide or
            // high: hotbars, chips) the zone reaches only 4 GUI inside its edge, so a press on a slot can never start a resize
            if (r.width < z * 3f || r.height < z * 3f) z = 4f * ResScale();
            // 8 px outside the edge counts
            float o = 12f * ResScale();   // ~12 GUI outside the drawn corner (the screen edge clamps it naturally)
            return x <= r.xMax + o && x >= r.xMax - z && yDown <= r.yMax + o && yDown >= r.yMax - z;
        }

        /// <summary>On mouse-down, a press in ANY shown live panel's corner zone starts its resize, whatever the top raycast hit is
        /// (the free-body path only ran when the press landed on the panel itself). Smallest panel wins when zones overlap.</summary>
        private static bool TryCornerZonePress()
        {
            if (!Cursor.visible) return false;
            Vector2 mp = UnityEngine.Input.mousePosition;
            Element best = null; float bestArea = float.MaxValue;
            foreach (var e in s_els.Values)
            {
                if (!e.O.LiveEdit || e.O.ScalePerAxis || e.O.Passive || e.O.Rect == null || !e.O.Rect.gameObject.activeInHierarchy || !IsShown(e)) continue;
                if (e.O.Layer == "hud" && IsHudHidden) continue;
                if (!InCornerZone(e, mp)) continue;
                // a press ON the live handle itself stays with the handle's own drag (no double resize)
                if (e.Grip != null && e.Grip.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(e.Grip, mp, Cam(e.Grip))) return false;
                var r = VisibleScreenRect(e, true); float a = r.width * r.height;
                if (a < bestArea) { bestArea = a; best = e; }
            }
            if (best == null) return false;
            // A DOUBLE press in the corner zone resets that
            // element (spot + size/scale, like a double-click on its grip) instead of starting a resize
            if (s_lastCornerId == best.O.Id && Time.unscaledTime - s_lastCornerAt < 0.35f) { s_lastCornerId = null; Log($"{best.O.Id}: corner double-press: reset"); ResetOne(best); return true; }
            s_lastCornerId = best.O.Id; s_lastCornerAt = Time.unscaledTime;
            s_freeScale = best; s_freeScaleFrom = mp; s_freeScaleUser = best.User; s_freeScaleRect = VisibleScreenRect(best, false);
            Selected = best.O.Id; s_freeArm = null;
            Log($"{best.O.Id}: grip FiresDockGrip3 drag start (corner zone press at {mp.x:0},{mp.y:0}; scale {best.User:0.00})");
            return true;
        }

        /// <summary>Drives a corner-zone resize while the button is held; ends it (EndScale + the proof line) on release.</summary>
        private static bool TickFreeScale()
        {
            var e = s_freeScale; if (e == null) return false;
            if (e.O.Rect == null) { s_freeScale = null; return false; }
            Vector2 mp = UnityEngine.Input.mousePosition;
            if (!UnityEngine.Input.GetMouseButton(0))
            {
                s_freeScale = null;
                CommitAll(e.O.Layer);
                Log($"{e.O.Id}: grip FiresDockGrip3 scale applied {s_freeScaleUser:0.00} -> {e.User:0.00} (localScale {e.O.Rect.localScale.x:0.00}, corner zone)");
                EndScale(e);
                return true;
            }
            float w = Mathf.Max(20f, s_freeScaleRect.width), h = Mathf.Max(20f, s_freeScaleRect.height);
            float fx = (w + (mp.x - s_freeScaleFrom.x)) / w, fy = (h + (s_freeScaleFrom.y - mp.y)) / h;   // right / down = bigger
            float f = Mathf.Abs(fx - 1f) >= Mathf.Abs(fy - 1f) ? fx : fy;
            float want = ClampUser(e, s_freeScaleUser * Mathf.Max(0.05f, f));
            if (Mathf.Abs(want - e.User) > 0.0005f && e.User > 0.0001f) ScaleOne(e, want / e.User, want / e.User);
            return true;
        }

        private static Rect GrowBy(Rect r, float d) => new Rect(r.x - d, r.y - d, r.width + 2f * d, r.height + 2f * d);
        private static void EndReveal(string why)
        {
            var e = Find(s_revealed); s_revealed = null;
            if (e != null) { CommitAll(e.O.Layer); Save(e.O.Layer); }
            Log($"{e?.O.Id}: reveal ended ({why}); layout saved");
        }

        internal static void TickPick()
        {
            if (UnityEngine.Input.GetMouseButtonDown(0)) LogPress();   // EVERY press, whatever stops it later
            TickStuckDrag();
            if (TickFreeScale()) return;   // Corner-zone resize in progress
            if (TickDirectDrag()) return;
            if (s_dragging != null) return;   // keep the one being dragged
            if (UnityEngine.Input.GetMouseButtonDown(0) && !AnyMoveMode && TryCornerZonePress()) return;   // Corner zone first
            if (UnityEngine.Input.GetMouseButtonDown(0)) { TryLiveHeaderDirect(); TickReveal(); if (s_direct == null && !AnyMoveMode) ArmFreeDrag(); }
            if (s_freeArm != null && TickFreeArm()) return;   // Only while a press is armed
            if (s_revealed != null && (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || !Cursor.visible)) EndReveal("Esc / cursor locked");
            // No hover pick at all unless the Esc menu or an editor is OPEN
            // right now; on the way out, the grab areas' raycast targets are cleared once
            if (!AnyMoveMode)
            {
                if (s_pick != null || s_pickDirty)
                {
                    s_pick = null; s_pickDirty = false;
                    foreach (var e in s_els.Values) { if (e.Overlay == null) continue; var oi = e.Overlay.GetComponent<Image>(); if (oi != null && oi.raycastTarget) oi.raycastTarget = false; }
                }
                return;
            }
            s_pickDirty = true;
            Vector2 m = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
            Element pick = null; float best = float.MaxValue;
            foreach (var e in s_els.Values)
            {
                // Elements WITHOUT a dock
                // grab area (LiveEdit pages, owner DragHandles: the globes) were never pickable here, so the Esc direct drag never ran for them
                if (e.O.Passive || !ElementActive(e) || !IsShown(e)) continue;
                var r = VisibleScreenRect(e);   // Pick by what is drawn
                if (!r.Contains(m)) continue;
                if (e.O.Id == Selected) { pick = e; break; }
                float area = r.width * r.height;
                if (area < best) { best = area; pick = e; }
            }
            if (pick != null && EscEditMode && PointerOverMenuButton()) pick = null;   // Pause-menu buttons stay clickable
            // The picked element's grab area (sorting 30900) sat ABOVE another
            // mod's own pointer UI (FDT 30000) and took its presses. Under a foreign pointer control, nothing is picked.
            if (pick != null && ForeignPointerOnTop(pick)) pick = null;
            s_pick = pick?.O.Id;
            foreach (var e in s_els.Values)
            {
                if (e.Overlay == null) continue;
                var img = e.Overlay.GetComponent<Image>();
                if (img != null) img.raycastTarget = e == pick;
            }
            if (pick != null && UnityEngine.Input.GetMouseButtonDown(0))
            {
                Selected = pick.O.Id;
                if (pick.O.Layer == "hud" && (EscEditMode || HudEditMode)) TryDirectDrag(pick);
            }
        }

        private static readonly HashSet<string> s_hudHiders = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_hudHideApplied, s_priorUserHidden;
        private static Hud s_hudFor;
        private static readonly HashSet<GameObject> s_hiddenOutsideRoot = new HashSet<GameObject>();

        /// <summary>True while at least one owner holds the HUD hidden.</summary>
        public static bool HudHeldHidden => s_hudHiders.Count > 0;
        /// <summary>Who holds the HUD hidden right now (for the minimap diagnosis).</summary>
        internal static string HudHolders => s_hudHiders.Count == 0 ? "none" : string.Join(", ", new List<string>(s_hudHiders).ToArray());
        /// <summary>True while this GameObject was switched off by HideHudWhile (it comes back on release).</summary>
        internal static bool HiddenByHudHide(GameObject go) => go != null && s_hiddenOutsideRoot.Contains(go);

        /// <summary>Owner-counted: hide = true adds the owner, false removes it; the HUD stays hidden while any owner holds it.</summary>
        public static void HideHudWhile(string owner, bool hide)
        {
            if (string.IsNullOrEmpty(owner)) owner = "?";
            bool changed = hide ? s_hudHiders.Add(owner) : s_hudHiders.Remove(owner);
            if (changed) Log($"hud hide {(hide ? "held" : "released")} by {owner} ({s_hudHiders.Count} holder(s))");
            TickHudHide();
        }

        // Every frame (vanilla's own hide key can flip m_userHidden back): keeps vanilla's HUD hidden and every registered HUD element that
        // lives OUTSIDE vanilla's Hud root inactive while held; puts both back exactly as found on the last release.
        internal static void TickHudHide()
        {
            var hud = Hud.instance;
            // Holders from the old world can never release (their windows are gone); a NEW Hud starts clean
            if (hud != null && !ReferenceEquals(hud, s_hudFor))
            {
                if (s_hudFor is object && s_hudHiders.Count > 0) { Log($"hud hide: new HUD (scene change / hop); dropped {s_hudHiders.Count} stale holder(s): {string.Join(", ", new List<string>(s_hudHiders).ToArray())}"); s_hudHiders.Clear(); }
                s_hudHideApplied = false; s_hiddenOutsideRoot.Clear(); s_hudFor = hud;
            }
            bool want = s_hudHiders.Count > 0 && hud != null;
            if (want)
            {
                if (!s_hudHideApplied) { s_priorUserHidden = hud.m_userHidden; s_hudHideApplied = true; }
                if (!hud.m_userHidden) hud.m_userHidden = true;
                Transform root = hud.m_rootObject != null ? hud.m_rootObject.transform : hud.transform;
                foreach (var e in s_els.Values)
                {
                    if (e.O.Layer != "hud" || e.O.Rect == null) continue;
                    var go = e.O.Rect.gameObject;
                    if (go.transform.IsChildOf(root) || go.transform.IsChildOf(hud.transform)) continue;   // vanilla's hide covers it
                    if (go.activeSelf) { go.SetActive(false); s_hiddenOutsideRoot.Add(go); }
                }
            }
            else if (s_hudHideApplied)
            {
                if (hud != null) hud.m_userHidden = s_priorUserHidden;
                foreach (var go in s_hiddenOutsideRoot) if (go != null) go.SetActive(true);
                s_hiddenOutsideRoot.Clear();
                s_hudHideApplied = false;
                Log("hud hide released: vanilla hidden = " + s_priorUserHidden + ", outside elements restored");
            }
        }

        /// <summary>Ends any edit mode that lost its world or its screen (loading, hop, death, inventory closed).</summary>
        internal static void TickEditModes()
        {
            bool esc = InWorld && !s_hudToggle && EscDialogOpen();
            if (esc != s_escEdit)
            {
                if (esc) TakeSnapshot("hud");
                s_escEdit = esc;
                if (!esc) Save("hud");
                Log(esc ? "Esc menu open: HUD elements drag / hover-resize directly (no editor chrome)" : "Esc menu closed: HUD layout saved");
                if (!esc) FiresCore.Logging.FiresLogger.LogInfo("[Dock] esc direct edit off");   // Proof line (Info)
            }
            TickNudge();
            // Esc while editing = Cancel (runs before the closed-inventory check below, which would otherwise save)
            if ((s_hudToggle || s_invToggle || s_screenEdit != null) && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                if (s_screenEdit != null) CancelEdit(s_screenEdit);
                if (s_invToggle) CancelEdit("inventory");
                if (s_hudToggle) CancelEdit("hud");
                return;
            }
            if (s_hudToggle && !InWorld) SetHudEditMode(false);            if (s_invToggle && (!InWorld || !InventoryGui.IsVisible())) SetEditMode("inventory", false);
            if (s_screenEdit != null && (!InWorld || !ScreenOpen(s_screenEdit))) SetEditMode(s_screenEdit, false);   // Its window closed = saved
        }

        internal static IEnumerable<Element> All => s_els.Values;

        // Arrow keys nudge the SELECTED element (its group's root) 1 px, Shift + arrow 10 px, while its layer is editable
        private static float s_nudgeSaveAt = -1f; private static string s_nudgeLayer;
        private static void TickNudge()
        {
            if (s_nudgeSaveAt > 0f && Time.unscaledTime >= s_nudgeSaveAt) { s_nudgeSaveAt = -1f; if (s_nudgeLayer != null) Save(s_nudgeLayer); }
            var e = Find(Selected);
            if (e == null || e.O.Rect == null || !CanDrag(e)) return;
            Vector2 d = Vector2.zero;
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) d.x -= 1f;
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) d.x += 1f;
            if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) d.y -= 1f;
            if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) d.y += 1f;
            if (d == Vector2.zero) return;
            if (UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)) d *= 10f;
            var mover = e; if (e.Anchor != null) foreach (var m in Members(e)) if (m.Anchor == null) { mover = m; break; }
            MoveBy(mover.O.Rect, d);   // screen px, y down (MoveBy takes the screen-down delta like DragBy)
            Commit(mover); Reflow(); CommitAll(e.O.Layer);
            s_nudgeLayer = e.O.Layer; s_nudgeSaveAt = Time.unscaledTime + 0.5f;
            Log($"{mover.O.Id}: nudged {d.x:0},{d.y:0} px");
        }

        // A direct scene hop (main -> main without the start scene) destroys every element's RectTransform while this static
        // list still holds them (NullReferenceExceptions from ScaleBy / Save on the next grip drag): elements whose rect is gone (Unity null)
        // are dropped, once a frame and before any walk that touches rects. Their saved spots stay in s_saved for the re-register.
        private static int s_pruneFrame = -1;
        internal static void PruneDead()
        {
            if (s_pruneFrame == Time.frameCount || s_els.Count == 0) return;
            s_pruneFrame = Time.frameCount;
            List<string> dead = null;
            foreach (var kv in s_els) if (kv.Value.O.Rect == null) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var id in dead) s_els.Remove(id);
            s_forced.RemoveWhere(g => g == null); s_hiddenByEditor.RemoveWhere(g => g == null); s_hiddenOutsideRoot.RemoveWhere(g => g == null);
            if (s_dragging != null && (s_dragging.E.O.Rect == null)) s_dragging = null;
            Log($"dropped {dead.Count} element(s) whose UI was destroyed (scene change / hop): {string.Join(", ", dead.ToArray())}");
        }
        /// <summary>Runs every LateUpdate right after Reflow (Core's slot proxies place spawned instances from their slot).</summary>
        internal static Action AfterReflow;
        internal static bool IsHudHidden => Hud.instance != null && Hud.instance.m_rootObject != null && !Hud.instance.IsVisible();
        internal static bool IsShownPublic(Element e) => IsShown(e);
        internal static Element DraggedElement => s_dragging?.E;

        // ShowInLayout elements forced active while their layer is in layout mode (LateUpdate, after vanilla's Update hid them).
        private static readonly HashSet<GameObject> s_forced = new HashSet<GameObject>();
        internal static void TickShowInLayout()
        {
            foreach (var e in s_els.Values)
            {
                if (!e.O.ShowInLayout || e.O.Rect == null) continue;
                var go = e.O.Rect.gameObject;
                // The Esc menu previews NOTHING; hidden elements are shown only in the pill editor
                bool on = ElementActive(e) && !EscEditMode;
                if (on) { if (!go.activeSelf) { go.SetActive(true); s_forced.Add(go); } }
                else if (s_forced.Remove(go) && go != null)
                {
                    // An element forced on for the editor is switched back off only when its owner says it is hidden (Visible() false); with no
                    // Visible rule the owner's own show/hide decides (vanilla parks its hidden HUD pieces itself)
                    // Leaving it "as its owner shows it" left vanilla's piece window on after its owner had hidden it: Core never leaves on what IT switched on. It was inactive before the force,
                    // so it goes back off unless the owner's Visible() rule says shown right now.
                    bool keep = e.O.Visible != null && SafeVisible(e);
                    if (!keep) go.SetActive(false);
                    Log($"{e.O.Id}: layout editor closed: {(keep ? "left on (its owner says shown)" : "back off as it was before the editor")}");
                }
            }
            // The legend's Hide toggle (editing only); whatever the editor hid comes back when editing ends or the toggle clears
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null) continue;
                var go = e.O.Rect.gameObject;
                bool hide = ElementActive(e) && !EscEditMode && EditHidden.Contains(e.O.Id);
                if (hide) { if (go.activeSelf) { go.SetActive(false); s_hiddenByEditor.Add(go); } }
                else if (s_hiddenByEditor.Remove(go) && go != null) go.SetActive(true);
            }
            TickUserHidden();
        }

        // The legend's Hide is now the PLAYER's choice, saved in the layout file (field 11) and kept after editing: outside the editor a
        // hidden element's CanvasRenderers are culled (not SetActive: its owner keeps showing / hiding it as usual, nothing to fight; a
        // culled graphic draws nothing and takes no clicks). Shown again by its Show toggle, "Show all", or Reset layout.
        private static readonly Dictionary<string, List<CanvasRenderer>> s_culled = new Dictionary<string, List<CanvasRenderer>>(StringComparer.Ordinal);
        private static float s_nextCullScan;
        private static readonly List<CanvasRenderer> s_crScratch = new List<CanvasRenderer>();

        private static void TickUserHidden()
        {
            // The editor's Hide is for arranging without other elements in the way; once no editor is open, everything is shown
            if (EditHidden.Count > 0 && !s_hudToggle && !s_invToggle && s_screenEdit == null) { Log($"layout editor closed: {EditHidden.Count} edit-hidden element(s) shown again"); EditHidden.Clear(); }
            bool rescan = Time.unscaledTime >= s_nextCullScan;
            if (rescan) s_nextCullScan = Time.unscaledTime + 0.5f;   // new children (icons, counters) get culled within half a second
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null) continue;
                // Never culled outside the editor; this only un-culls what older builds culled
                bool cull = false;
                s_culled.TryGetValue(e.O.Id, out var list);
                if (cull)
                {
                    if (list == null || rescan)
                    {
                        if (list == null) { list = new List<CanvasRenderer>(); s_culled[e.O.Id] = list; }
                        s_crScratch.Clear(); e.O.Rect.GetComponentsInChildren(true, s_crScratch);
                        foreach (var cr in s_crScratch) if (cr != null && !list.Contains(cr)) list.Add(cr);
                    }
                    foreach (var cr in list) if (cr != null && !cr.cull) cr.cull = true;   // every frame: RectMask2D clipping may reset it
                }
                else if (list != null)
                {
                    foreach (var cr in list) if (cr != null) cr.cull = false;
                    s_culled.Remove(e.O.Id);
                }
            }
        }

        /// <summary>The player's per-element visibility (saved with the layout on the next Save). Add-only.</summary>
        public static void SetUserHidden(string id, bool hidden)
        {
            if (string.IsNullOrEmpty(id)) return;
            bool changed = hidden ? EditHidden.Add(id) : EditHidden.Remove(id);
            if (changed) Log($"{id}: {(hidden ? "hidden" : "shown")} by the player");
        }

        public static bool IsUserHidden(string id) => id != null && EditHidden.Contains(id);

        /// <summary>"Hide all" / "Show all" for one layer's elements (passive references excluded).</summary>
        public static void SetAllHidden(string layer, bool hidden)
        {
            int n = 0;
            foreach (var e in s_els.Values)
            {
                if (e.O.Layer != layer || (e.O.Passive && !e.O.OutlineOnly)) continue;
                if (hidden ? EditHidden.Add(e.O.Id) : EditHidden.Remove(e.O.Id)) n++;
            }
            Log($"{(hidden ? "hide" : "show")} all ({layer}): {n} element(s) changed");
        }

        internal static bool LayerActive(string layer) => layer == "inventory" ? InventoryEditMode : IsScreenLayer(layer) ? ScreenEditLayer == layer : (HudEditMode || EscEditMode) && FiresCore.UI.Shell.FiresShell.OverrideOn;

        /// <summary>An element is editable
        /// while its own layer is, or, for a HUD element that opted in (<see cref="DockElementOptions.AlsoInInventoryLayout"/>), while the
        /// inventory layout session is on. It stays a HUD element: same saved spot (dock_hud), docks only with HUD elements.</summary>
        internal static bool ElementActive(Element e) =>
            e != null && (LayerActive(e.O.Layer) || (e.O.AlsoInInventoryLayout && e.O.Layer == "hud" && InventoryEditMode) || (s_revealed != null && e.O.Id == s_revealed));   // + revealed by double-click

        // ---- register ------------------------------------------------------------------------------------------
        public static void Register(DockElementOptions o)
        {
            if (o == null || string.IsNullOrEmpty(o.Id) || o.Rect == null) { Log("register ignored (no id or rect)"); return; }
            if (FiresCore.UI.HeadlessGuard.IsHeadless) return;
            EnsureTicker();
            LoadIfNeeded();
            if (s_els.ContainsKey(o.Id)) Unregister(o.Id);
            if (o.Id == "hud.grouphud") s_groupDefaultDone = false;   // Each registration (login / relog / override ON) places the default once
            // An element nested inside another
            // registered element (or containing one) cannot be placed on its own: the inner one becomes Passive (rides its parent). A
            // FollowId element is exempt (it follows by design, e.g. the plates inside the minimap column).
            if (!o.Passive && string.IsNullOrEmpty(o.FollowId))
                foreach (var x in s_els.Values)
                {
                    if (x.O.Rect == null || x.O.Id == o.Id || x.O.Layer != o.Layer) continue;
                    if (o.Rect.IsChildOf(x.O.Rect) && x.O.Rect != o.Rect)
                    { FiresCore.Logging.FiresLogger.LogWarning($"[Dock] {o.Id} sits inside {x.O.Id}: registered passive (it moves with {x.O.Id})"); o.Passive = true; break; }
                }
            var e = new Element { O = o };
            e.Base = o.BaseScale > 0f ? o.BaseScale : (o.Rect.localScale.x > 0f ? o.Rect.localScale.x : 1f);
            // The NPC book re-registers its window
            // on each tab, and the rect's localScale was ALREADY Base x User, so Base x User compounded smaller every tab. The base is the
            // element's own scale at its FIRST register this session; a re-register reuses it.
            // The base is cached per id AND Unified mode.
                // An element registered in OFF (vanilla-look bar, its own localScale) handed that base to the ON kit row, so the saved user scale
                // multiplied the wrong base after the switch. Each mode keeps its own first base; the user scale per id is unchanged.
                string baseKey = o.Id + (FiresCore.UI.Shell.FiresShell.OverrideOn ? "@on" : "@off");
                if (s_baseById.TryGetValue(baseKey, out float firstBase)) e.Base = firstBase; else s_baseById[baseKey] = e.Base;
            e.DefaultTopLeft = o.DefaultRect != null ? Safe(o.DefaultRect).position : TopLeft(o.Rect);
            e.DefaultSize = o.Rect.rect.size;
            s_els[o.Id] = e;
            if (!o.Passive) AttachHandles(e);
            // A saved spot that would put the element mostly off screen is
            // ignored (the element starts at its original spot) and dropped on the next save.
            if (s_saved.TryGetValue(o.Id, out var bad))
            {
                var sz = ScreenRect(o.Rect).size;
                var sr = new Rect(bad.X, bad.Y, sz.x, sz.y);
                float visW = Mathf.Max(0f, Mathf.Min(sr.xMax, Screen.width) - Mathf.Max(sr.xMin, 0f)), visH = Mathf.Max(0f, Mathf.Min(sr.yMax, Screen.height) - Mathf.Max(sr.yMin, 0f));
                string why = null;
                // An element registered before it is laid out
                // measures 0x0, so the area test never ran; the saved top-left POINT itself off screen also counts
                if (bad.X < -8f || bad.Y < -8f || bad.X > Screen.width - 8f || bad.Y > Screen.height - 8f) why = "off screen";
                // A spot the
                // PLAYER saved is never thrown away for overlapping another element (that one was an invisible, empty message area) or for
                // reaching past an edge; only a top-left that is entirely off the screen (an old resolution) falls back to the default
                _ = visW; _ = visH;
                if (why != null) { Log($"{o.Id}: saved spot {bad.X:0},{bad.Y:0} is {why}; ignored (original spot)"); s_saved.Remove(o.Id); }
            }
            if (s_saved.TryGetValue(o.Id, out var sv))
            {
                Log($"{o.Id}: restored saved spot {sv.X:0},{sv.Y:0} scale {sv.User:0.00}{(sv.Anchor != null ? " docked to " + sv.Anchor : "")}");   // Proof line
                e.User = ClampUser(e, sv.User); e.Aspect = ClampAspect(e, sv.Aspect);
            // ONE time per
            // element and player, a saved hotbar scale goes back to 1 (logged). After that the player's own resizes are kept as usual.
            if (o.Id.StartsWith("vainv.hotbar2.", StringComparison.Ordinal) && Mathf.Abs(e.User - 1f) > 0.001f)
            {
                string mk = "FiresDock.Migr668." + o.Id;
                bool done = false; try { done = PlayerPrefs.GetInt(mk, 0) == 1; } catch { }
                if (!done) { Log($"{o.Id}: saved scale {e.User:0.00} reset to 1 once (0.2.668: a bug made it)"); e.User = 1f; try { PlayerPrefs.SetInt(mk, 1); PlayerPrefs.Save(); } catch { } s_migratedSave = o.Layer; }
            }
                Place(o.Rect, e.Base * e.User, new Vector2(sv.X, sv.Y), e.Aspect);
                e.Group = sv.Group; e.Anchor = sv.Anchor; e.Edge = sv.Edge; e.AlignEnd = sv.AlignEnd; e.DockOffset = new Vector2(sv.OX, sv.OY); e.PendingOwnerSpot = null;   // The saved offset (0 in older files)
                    if (e.O.NoDock && (e.Anchor != null || e.Group != null)) { Log($"{e.O.Id}: NoDock - saved dock to {e.Anchor ?? "-"} / group {e.Group ?? "-"} dropped, kept at its saved top-left ({sv.X:0.#}, {sv.Y:0.#})"); e.Group = null; e.Anchor = null; e.Edge = DockEdge.None; e.AlignEnd = false; e.DockOffset = Vector2.zero; }
                e.FromEmbedded = sv.Embedded;
            }
            else
            {
                Place(o.Rect, e.Base, e.DefaultTopLeft);
                if (!string.IsNullOrEmpty(o.DefaultDockTo) && o.DefaultEdge != DockEdge.None)
                {
                    e.Anchor = o.DefaultDockTo; e.Edge = o.DefaultEdge; e.AlignEnd = false;
                    if (s_els.TryGetValue(o.DefaultDockTo, out var to) && to.O.Layer == o.Layer) Join(e, to);
                    else e.Group = "g_" + o.DefaultDockTo;   // the target registers later: it joins this group then
                }
            }
            // an element registering late joins whoever already anchors to it by default
            foreach (var x in s_els.Values)
                if (x != e && x.Anchor == o.Id && x.O.Layer == o.Layer && x.Group != null && (e.Group == null || e.Group != x.Group)) Join(x, e);
            Reflow();
            // No clamp on register / load. A saved spot is the player's, partly or fully off-screen included.
            if (o.InventorySpot) { e.Committed = TopLeft(o.Rect); e.HasCommit = true; }   // Its HUD spot even if the inventory is open now
            else Commit(e);
            if (IsScreenLayer(o.Layer) && !o.Passive) { LoadScreenSize(); if (ApplyScreenSize(e)) Log($"{o.Id}: shared screen size applied"); }
            if (o.InventorySpot && s_saved.TryGetValue(InvKey(o.Id), out var inv)) { e.InvCommitted = new Vector2(inv.X, inv.Y); e.HasInvCommit = true; Log($"{o.Id}: inventory-open spot {inv.X:0},{inv.Y:0}"); }
            Log($"registered {o.Id} ({o.Owner ?? "?"}, layer {o.Layer}, base scale {e.Base:0.###}){(s_saved.ContainsKey(o.Id) ? " at its saved spot" : "")}");
        }

        /// <summary>What a real mouse at the element's visible centre would hit first, as the EventSystem
        /// raycasts it: "<top object path> | dock=<true if that object is this element's grab area / handle>". Proves a hand drag reaches
        /// the dock (TestDrag bypasses input).</summary>
        /// <summary>The element's DRAWN screen rect (px, top-left, y down; mask-clipped, NOT screen-clipped),
        /// the one Core's on-screen clamp and fit checks use. Rect.zero for an unknown id.</summary>
        public static Rect TestVisibleRect(string id)
        {
            var e = Find(id);
            return e == null || e.O.Rect == null ? Rect.zero : VisibleScreenRect(e, false);
        }

        public static string TestRaycastTop(string id)
        {
            var e = Find(id);
            if (e == null || e.O.Rect == null) return "no element " + id;
            var es = EventSystem.current;
            if (es == null) return "no EventSystem";
            var r = VisibleScreenRect(e);
            var ped = new PointerEventData(es) { position = new Vector2(r.center.x, Screen.height - r.center.y) };
            var hits = new List<RaycastResult>();
            es.RaycastAll(ped, hits);
            if (hits.Count == 0) return "nothing hit at " + ped.position;
            var top = hits[0].gameObject;
            var dh = top.GetComponentInParent<DockDragHandle>();
            string path = top.name; for (var p = top.transform.parent; p != null && path.Length < 160; p = p.parent) path = p.name + "/" + path;
            string line = $"{path} | dock={(dh != null && dh.Id == id)} ({hits.Count} hits)";
            Log("test raycast " + id + ": " + line);
            return line;
        }

        /// <summary>The owner sets an element's default spot (screen px, top-left, y down), e.g. from its own position config
        /// after placing it. Reset layout puts the element there. Prefer passing DockElementOptions.DefaultRect (read at Register and at Reset).</summary>
        public static void SetDefault(string id, Vector2 screenTopLeft)
        {
            if (id != null && s_els.TryGetValue(id, out var e) && !s_saved.ContainsKey(id) && e.Anchor != null && e.Anchor == e.O.DefaultDockTo)
            {
                // No saved spot and only default-docked: the owner's spot wins exactly, and it STAYS docked (an offset
                // from the dock rule's spot, applied by the next reflow)
                e.PendingOwnerSpot = screenTopLeft;
            }
            if (id != null && s_els.TryGetValue(id, out e)) { e.DefaultTopLeft = screenTopLeft; e.OwnerDefault = true; Log($"{id}: default spot set by its owner to {screenTopLeft.x:0},{screenTopLeft.y:0}"); }
        }

        public static void Unregister(string id)
        {
            if (id == null || !s_els.TryGetValue(id, out var e)) return;
            // A screen layer still being edited is
            // ended and SAVED first, while all of its panels are still registered (an edit is never lost to the owner's close order)
            if (IsScreenLayer(e.O.Layer) && s_screenEdit == e.O.Layer && e.O.Rect != null) SetEditMode(e.O.Layer, false);
            if (e.O.Rect != null && s_forced.Remove(e.O.Rect.gameObject)) e.O.Rect.gameObject.SetActive(false);
            if (e.Overlay != null) UnityEngine.Object.Destroy(e.Overlay);
            foreach (var gg in e.Grips) if (gg != null) UnityEngine.Object.Destroy(gg.gameObject);
            var h = (e.O.DragHandle ?? e.O.Rect) != null ? (e.O.DragHandle ?? e.O.Rect).GetComponent<DockDragHandle>() : null;
            if (h != null) UnityEngine.Object.Destroy(h);
            s_els.Remove(id);
            Log("unregistered " + id);
        }

        public static bool IsDocked(string id) => id != null && s_els.TryGetValue(id, out var e) && e.Group != null;
        public static string GroupOf(string id) => id != null && s_els.TryGetValue(id, out var e) ? e.Group : null;

        /// <summary>Back to defaults for every element on the layer (and drops their saved spots).</summary>
        public static void ResetLayer(string layer) => ResetLayer(layer, true);

        /// <summary>ResetLayer; with <paramref name="save"/> false the saved layout file is left untouched (tests).</summary>
        public static void ResetLayer(string layer, bool save)
        {
            if (layer == "hud") s_chatSpotKey = null;   // A reset recomputes the chat default spot once
            if (layer == "inventory")   // InventorySpot elements go back to the computed gap default
                foreach (var e in s_els.Values)
                    if (e.O.InventorySpot) { e.HasInvCommit = false; if (save) s_saved.Remove(InvKey(e.O.Id)); }
            foreach (var e in s_els.Values)
            {
                if (e.O.Layer != layer) continue;
                e.Group = null; e.User = 1f; e.Aspect = 1f; e.Anchor = null; e.Edge = DockEdge.None; e.AlignEnd = false;
                ResetSize(e);   // Before the default spot is read (the size can move the owner's default)
                ReattachToColumn(e);
                // The default is the OWNER's spot (its DefaultRect, read again
                // now, i.e. its config), never the spot the element happened to have when it registered (vanilla's, before the owner placed it)
                if (e.O.DefaultRect != null) { e.DefaultTopLeft = Safe(e.O.DefaultRect).position; e.OwnerDefault = true; }
                Place(e.O.Rect, e.Base, e.DefaultTopLeft);
                if (save) s_saved.Remove(e.O.Id);
            }
            foreach (var e in s_els.Values)
                if (e.O.Layer == layer && !string.IsNullOrEmpty(e.O.DefaultDockTo) && s_els.TryGetValue(e.O.DefaultDockTo, out var to))
                {
                    Join(e, to);
                    // When both spots are the OWNERS' defaults they stay exactly there
                    // (grouped, so they move together, but not re-anchored by the dock gap); otherwise the dock rule places it as before
                    e.Anchor = to.O.Id; e.Edge = e.O.DefaultEdge; e.DockOffset = Vector2.zero;
                    // Both spots are the OWNERS' defaults: it stays docked (a non-root member, as the dock tests expect) but keeps the
                    // owner's exact spot through a per-member offset from the dock rule's spot (cleared when the player drags / snaps it)
                    if (e.OwnerDefault && to.OwnerDefault) e.PendingOwnerSpot = e.DefaultTopLeft;
                }
            // The shipped default wins over the registered defaults where it has the element
            if (LoadDefaults(layer) > 0)
                foreach (var e in s_els.Values)
                {
                    if (e.O.Layer != layer || !s_saved.TryGetValue(e.O.Id, out var sv)) continue;
                    e.User = ClampUser(e, sv.User); e.Aspect = ClampAspect(e, sv.Aspect);
                    Place(e.O.Rect, e.Base * e.User, new Vector2(sv.X, sv.Y), e.Aspect);
                    e.Group = sv.Group; e.Anchor = sv.Anchor; e.Edge = sv.Edge; e.AlignEnd = sv.AlignEnd; e.DockOffset = new Vector2(sv.OX, sv.OY); e.PendingOwnerSpot = null;   // The saved offset (0 in older files)
                    if (e.O.NoDock && (e.Anchor != null || e.Group != null)) { Log($"{e.O.Id}: NoDock - saved dock to {e.Anchor ?? "-"} / group {e.Group ?? "-"} dropped, kept at its saved top-left ({sv.X:0.#}, {sv.Y:0.#})"); e.Group = null; e.Anchor = null; e.Edge = DockEdge.None; e.AlignEnd = false; e.DockOffset = Vector2.zero; }
                    e.FromEmbedded = sv.Embedded;   // A reset brings back the computed chat spot
                }
            CommitAll(layer);   // The just-placed defaults are the committed spots BEFORE the reflow (it re-places roots at them)
            foreach (var e in new List<Element>(s_els.Values)) if (e.O.Layer == layer && e.OwnerPlaced) OwnerPlacedReset(e);   // Owner-placed elements go back to the owner's default
            Reflow();
            CommitAll(layer);
            if (save) Save(layer);
            Log("reset " + layer);
        }

        // ---- geometry (screen px, origin top-left, y down) -------------------------------------------------------
        internal static Rect ScreenRect(RectTransform rt)
        {
            var c = s_corners4; rt.GetWorldCorners(c);   // One static buffer (was a new array per call, twice a frame per element)
            var cam = Cam(rt);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, c[0]), tr = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return new Rect(bl.x, Screen.height - tr.y, tr.x - bl.x, tr.y - bl.y);
        }

        internal static Vector2 TopLeft(RectTransform rt) => ScreenRect(rt).position;

        /// <summary>Moves a grip so it lies fully inside the
        /// screen (the visible corner). localPosition only; a grip already on screen is untouched.</summary>
        private static void ClampGripOnScreen(RectTransform gg)
        {
            var sr = ScreenRect(gg);
            float lw = gg.rect.width * gg.localScale.x;
            if (sr.width <= 0f || lw <= 0f) return;
            float dx = 0f, dy = 0f;
            if (sr.xMin < 0f) dx = -sr.xMin; else if (sr.xMax > Screen.width) dx = Screen.width - sr.xMax;
            if (sr.yMin < 0f) dy = -sr.yMin; else if (sr.yMax > Screen.height) dy = Screen.height - sr.yMax;
            if (dx == 0f && dy == 0f) return;
            float ppu = sr.width / lw;   // screen px per parent-local unit
            gg.localPosition += new Vector3(dx / ppu, -dy / ppu, 0f);   // screen y is down, local y is up
        }
        private static readonly Vector3[] s_corners4 = new Vector3[4], s_wc4 = new Vector3[4];   // main thread only

        /// <summary>The camera of the ROOT canvas (null for
        /// overlay). The nearest canvas can be a nested override (Core's inventory-in-front / grab-area canvases) which has no camera.</summary>
        internal static Camera Cam(RectTransform rt)
        {
            var cv = rt != null ? rt.GetComponentInParent<Canvas>() : null;
            var root = cv != null ? cv.rootCanvas : null;
            return root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        }

        /// <summary>Scales the rect and moves it so its top-left lands on the given screen point. Position + localScale only.</summary>
        internal static void Place(RectTransform rt, float scale, Vector2 topLeft) => Place(rt, scale, topLeft, 1f);

        /// <summary>+ the height / width scale ratio (<paramref name="aspect"/> 1 = uniform).</summary>
        internal static void Place(RectTransform rt, float scale, Vector2 topLeft, float aspect)
        {
            if (rt == null) return;
            float sy = scale * aspect;
            if (Mathf.Abs(rt.localScale.x - scale) > 0.0001f || Mathf.Abs(rt.localScale.y - sy) > 0.0001f) rt.localScale = new Vector3(scale, sy, 1f);
            MoveBy(rt, topLeft - TopLeft(rt));
        }

        internal static void MoveBy(RectTransform rt, Vector2 screenDelta)
        {
            if (rt == null || screenDelta.sqrMagnitude < 0.01f) return;
            // Through world space with the ROOT canvas's camera: exact whatever the parents' scale and whatever nested canvases
            // (Core's own inventory / grab-area overrides) sit in between (a nested canvas has no camera of its own)
            var cam = Cam(rt);
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, rt.position) + new Vector2(screenDelta.x, -screenDelta.y);
            var plane = rt.parent as RectTransform ?? rt;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle(plane, sp, cam, out var wp)) rt.position = new Vector3(wp.x, wp.y, rt.position.z);
            else rt.position += new Vector3(screenDelta.x, -screenDelta.y, 0f);
        }

        private static float Ui(float px) => px * Screen.height / 1080f;   // 1080p reference distances

        private static void Clamp(Element e)
        {
            if (e.Anchor != null) return;   // a docked element follows its anchor; its group's root is the one clamped
            // Clamp by what is DRAWN; vanilla's m_player rect is taller than
            // the grid it shows, so the registered bounds never fit and every move was clamped straight back
            var r = VisibleBounds(Members(e));
            Vector2 d = Vector2.zero;
            if (r.xMin < 0f) d.x = -r.xMin; else if (r.xMax > Screen.width) d.x = Screen.width - r.xMax;
            if (r.yMin < 0f) d.y = -r.yMin; else if (r.yMax > Screen.height) d.y = Screen.height - r.yMax;
            // A clamp bigger than the screen means the bounds are wrong, never move by it
            if (Mathf.Abs(d.x) > Screen.width || Mathf.Abs(d.y) > Screen.height)
            {
                Log($"{e.O.Id}: clamp of {d.x:0},{d.y:0} refused (bounds {r.xMin:0},{r.yMin:0} {r.width:0}x{r.height:0} look wrong; left where it is)");
                return;
            }
            if (d != Vector2.zero)
            {
                foreach (var m in Members(e)) MoveBy(m.O.Rect, d);
                Log($"{e.O.Id}: clamped by {d.x:0},{d.y:0} to stay on screen (drawn bounds {r.xMin:0},{r.yMin:0} {r.width:0}x{r.height:0}, screen {Screen.width}x{Screen.height})");
            }
        }

        internal static List<Element> Members(Element e)
        {
            var l = new List<Element>();
            if (e.Group == null) { l.Add(e); return l; }
            foreach (var x in s_els.Values) if (x.Group == e.Group && x.O.Layer == e.O.Layer) l.Add(x);
            return l;
        }

        // The union of the element's drawn graphics (active,
        // visible, not Core's own overlay / grips), in the element's local space; its own rect when nothing is drawn. Cached per frame.
        private static readonly Dictionary<string, (int frame, float at, Rect local)> s_visCache = new Dictionary<string, (int, float, Rect)>();   // + at
        private static readonly List<Graphic> s_gfx = new List<Graphic>();
        // The walk is cached per element per
        // frame (one walk a frame however many callers ask), so the cost no longer grows with calls per frame.
        private static string s_migratedSave;   // A layer to save once the migrated element is placed
        private static readonly Dictionary<Element, (int frame, Rect r)> s_localBoundsCache = new Dictionary<Element, (int, Rect)>();
        internal static Rect VisibleLocalBounds(Element e)
        {
            if (e == null) return default;
            int fr = Time.frameCount;
            if (s_localBoundsCache.TryGetValue(e, out var hit) && hit.frame == fr) return hit.r;
            var res = VisibleLocalBoundsUncached(e);
            if (s_localBoundsCache.Count > 256) s_localBoundsCache.Clear();
            s_localBoundsCache[e] = (fr, res);
            return res;
        }

        private static Rect VisibleLocalBoundsUncached(Element e)
        {
            var rt = e.O.Rect;
            if (rt == null) return default;
            // An owner can
            // name the rect that IS its art (HitBounds); hit area, outline and grips then use exactly that, in the element's local space
            if (e.O.HitBounds != null && e.O.HitBounds.gameObject.activeInHierarchy)
            {
                var hc = s_wc4; e.O.HitBounds.GetWorldCorners(hc);
                float hx0 = float.MaxValue, hy0 = float.MaxValue, hx1 = float.MinValue, hy1 = float.MinValue;
                for (int k = 0; k < 4; k++) { var l = rt.InverseTransformPoint(hc[k]); hx0 = Mathf.Min(hx0, l.x); hx1 = Mathf.Max(hx1, l.x); hy0 = Mathf.Min(hy0, l.y); hy1 = Mathf.Max(hy1, l.y); }
                var hr = Rect.MinMaxRect(hx0, hy0, hx1, hy1);
                s_frameCache[e.O.Id] = (Time.frameCount, hr); s_visCache[e.O.Id] = (Time.frameCount, Time.unscaledTime, hr);
                return hr;
            }
            if (s_visCache.TryGetValue(e.O.Id, out var c) && c.frame == Time.frameCount) return c.local;
            // This walks EVERY graphic under the element (minimap pins, chat lines);
            // while the Esc menu / an editor is open it ran per element per frame. Reused for 0.2 s unless the element is being moved.
            if (c.frame != 0 && Time.unscaledTime - c.at < 0.2f && !InDrag(e) && (s_direct == null || !s_direct.Moving.Contains(e))) return c.local;
            s_gfx.Clear(); rt.GetComponentsInChildren(false, s_gfx);
            bool any = false; float x0 = 0, y0 = 0, x1 = 0, y1 = 0; var wc = s_wc4;   // Static buffer
            float bestArea = 0f; Rect frame = default;
            foreach (var g in s_gfx)
            {
                if (g == null || !g.enabled || g.color.a < 0.02f) continue;
                if (g.canvasRenderer != null && g.canvasRenderer.GetAlpha() < 0.02f) continue;
                var t = g.transform; bool ours = false;
                for (var p = t; p != null && p != rt; p = p.parent) if (p.name.StartsWith("FiresDock", StringComparison.Ordinal)) { ours = true; break; }
                if (ours) continue;
                if (g.canvasRenderer != null && g.canvasRenderer.cull) continue;   // culled by a RectMask2D
                g.rectTransform.GetWorldCorners(wc);
                float gx0 = float.MaxValue, gy0 = float.MaxValue, gx1 = float.MinValue, gy1 = float.MinValue;
                for (int k = 0; k < 4; k++) { var l = rt.InverseTransformPoint(wc[k]); gx0 = Mathf.Min(gx0, l.x); gx1 = Mathf.Max(gx1, l.x); gy0 = Mathf.Min(gy0, l.y); gy1 = Mathf.Max(gy1, l.y); }
                // A graphic under a Mask / RectMask2D counts only where the
                // mask shows it (a scroll list's content runs far past its viewport)
                for (var p = g.transform.parent; p != null && p != rt.parent; p = p.parent)
                {
                    if (p.GetComponent<RectMask2D>() == null && p.GetComponent<Mask>() == null) continue;
                    ((RectTransform)p).GetWorldCorners(wc);
                    float mx0 = float.MaxValue, my0 = float.MaxValue, mx1 = float.MinValue, my1 = float.MinValue;
                    for (int k = 0; k < 4; k++) { var l = rt.InverseTransformPoint(wc[k]); mx0 = Mathf.Min(mx0, l.x); mx1 = Mathf.Max(mx1, l.x); my0 = Mathf.Min(my0, l.y); my1 = Mathf.Max(my1, l.y); }
                    gx0 = Mathf.Max(gx0, mx0); gy0 = Mathf.Max(gy0, my0); gx1 = Mathf.Min(gx1, mx1); gy1 = Mathf.Min(gy1, my1);
                }
                if (gx1 - gx0 < 1f || gy1 - gy0 < 1f) continue;   // fully masked out
                float ga = (gx1 - gx0) * (gy1 - gy0);
                if (ga > bestArea) { bestArea = ga; frame = Rect.MinMaxRect(gx0, gy0, gx1, gy1); }   // The panel's frame
                if (!any) { x0 = gx0; x1 = gx1; y0 = gy0; y1 = gy1; any = true; }
                else { x0 = Mathf.Min(x0, gx0); x1 = Mathf.Max(x1, gx1); y0 = Mathf.Min(y0, gy0); y1 = Mathf.Max(y1, gy1); }
            }
            var r = any && x1 - x0 > 2f && y1 - y0 > 2f ? Rect.MinMaxRect(x0, y0, x1, y1) : rt.rect;
            s_visCache[e.O.Id] = (Time.frameCount, Time.unscaledTime, r);
            if (bestArea > 0f) s_frameCache[e.O.Id] = (Time.frameCount, frame);
            else s_frameCache.Remove(e.O.Id);
            return r;
        }

        // Resize handles must sit on the panel's corner: the union of every drawn child can run past the panel (a count label, a drag ghost, a shadow).
        // A grip anchors to the panel's FRAME: the largest single drawn graphic (its background plate), which is what the eye reads
        // as the panel's edge.
        private static readonly HashSet<string> s_gripLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (int frame, Rect local)> s_frameCache = new Dictionary<string, (int, Rect)>(StringComparer.Ordinal);
        internal static Rect FrameLocalBounds(Element e)
        {
            var vb = VisibleLocalBounds(e);
            // The frame plate is valid for as long as the drawn bounds it was measured with (same walk)
            if (s_frameCache.TryGetValue(e.O.Id, out var c) && s_visCache.TryGetValue(e.O.Id, out var vc) && c.frame == vc.frame)
            {
                // a frame that is a sliver of the drawn content (a header strip) is not the panel: use the drawn bounds then
                if (c.local.width * c.local.height >= vb.width * vb.height * 0.5f) return c.local;
            }
            return vb;
        }

        /// <summary>The drawn content's screen rect (px, top-left, y down).</summary>
        internal static Rect VisibleScreenRect(Element e) => VisibleScreenRect(e, true);

        /// <summary>Clip = false keeps the off-screen part (the on-screen rules must see an overflow).</summary>
        internal static Rect VisibleScreenRect(Element e, bool clip)
        {
            var rt = e.O.Rect; if (rt == null) return default;
            var lb = VisibleLocalBounds(e);
            var cam = Cam(rt);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(lb.xMin, lb.yMin, 0f)));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(lb.xMax, lb.yMax, 0f)));
            float xMin = Mathf.Min(a.x, b.x), xMax = Mathf.Max(a.x, b.x), yMin = Mathf.Min(a.y, b.y), yMax = Mathf.Max(a.y, b.y);
            var vr = new Rect(xMin, Screen.height - yMax, xMax - xMin, yMax - yMin);
            // Only the on-screen part counts (outlines, picking and the raycast probe use what the player can actually see)
            var on = Rect.MinMaxRect(Mathf.Max(vr.xMin, 0f), Mathf.Max(vr.yMin, 0f), Mathf.Min(vr.xMax, Screen.width), Mathf.Min(vr.yMax, Screen.height));
            if (!clip) return vr;
            return on.width > 1f && on.height > 1f ? on : vr;
        }

        private static readonly HashSet<string> s_dockClampLogged = new HashSet<string>(StringComparer.Ordinal);

        // The union of the members' DRAWN rects (VisibleScreenRect), for the on-screen rules
        /// <summary>The element's DRAWN screen rect (px, y down) as the dock snaps and docks it - the
        /// registered rect plus real overhangs up to 32 px (e.g. the minimap's wind pill). Rect.zero when the id isn't registered or shown.</summary>
        public static Rect VisibleRect(string id)
        {
            var e = Find(id);
            return e != null && e.O.Rect != null && IsShown(e) ? VisibleBounds(new List<Element> { e }) : Rect.zero;
        }

        private static Rect VisibleBounds(List<Element> els)
        {
            bool first = true; Rect b = default;
            foreach (var x in els)
            {
                if (!IsShown(x)) continue;
                // Bogus drawn bounds (thousands of px wide) must not shove a group off screen: the drawn rect counts only within the registered rect grown by 32 px (real overhangs such as a 14 px header survive)
                var reg = ScreenRect(x.O.Rect); var drawn = VisibleScreenRect(x, false);
                var r = Rect.MinMaxRect(Mathf.Max(drawn.xMin, reg.xMin - 32f), Mathf.Max(drawn.yMin, reg.yMin - 32f), Mathf.Min(drawn.xMax, reg.xMax + 32f), Mathf.Min(drawn.yMax, reg.yMax + 32f));
                if (r.width < 1f || r.height < 1f) r = reg;
                if (first) { b = r; first = false; } else b = Rect.MinMaxRect(Mathf.Min(b.xMin, r.xMin), Mathf.Min(b.yMin, r.yMin), Mathf.Max(b.xMax, r.xMax), Mathf.Max(b.yMax, r.yMax));
            }
            return b;
        }

        private static Rect Shift(Rect r, Vector2 d) => new Rect(r.position + d, r.size);

        /// <summary>The rect docking, snapping and the
        /// dock gap measure: the owner's HitBounds (slots + labels + padding) when set, else the element rect as before. Moves still apply
        /// the delta to the element, so the whole element follows.</summary>
        internal static Rect DockRect(Element e) => e != null && e.O.HitBounds != null && e.O.HitBounds.gameObject.activeInHierarchy ? VisibleScreenRect(e, false) : ScreenRect(e.O.Rect);

        private static Rect Bounds(List<Element> els)
        {
            bool first = true; Rect b = default;
            foreach (var x in els)
            {
                if (!IsShown(x)) continue;
                var r = DockRect(x);
                if (first) { b = r; first = false; } else b = Rect.MinMaxRect(Mathf.Min(b.xMin, r.xMin), Mathf.Min(b.yMin, r.yMin), Mathf.Max(b.xMax, r.xMax), Mathf.Max(b.yMax, r.yMax));
            }
            return b;
        }

        // IsShown runs several times per element per
        // frame (Reflow, the anchor walk, overlays, followers) and each call ran the OWNER's Visible() callback. One evaluation per element
        // per frame now; the callback's time is measured per element for the cost line.
        private static bool IsShown(Element e)
        {
            if (e.ShownFrame == Time.frameCount) return e.ShownVal;
            bool v;
            if (e.O.Rect == null || !e.O.Rect.gameObject.activeInHierarchy) v = false;
            else if (e.O.ShowInLayout && ElementActive(e)) v = true;   // Placed in the editor even while its owner calls it hidden
            else if (e.O.Visible == null) v = true;
            else
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { v = e.O.Visible(); } catch { v = true; }
                Charge(s_costVisible, e.O.Id, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            }
            e.ShownFrame = Time.frameCount; e.ShownVal = v;
            return v;
        }
        internal static readonly Dictionary<string, long> s_costVisible = new Dictionary<string, long>(StringComparer.Ordinal), s_costMoves = new Dictionary<string, long>(StringComparer.Ordinal);
        internal static void Charge(Dictionary<string, long> d, string id, long v) { d.TryGetValue(id, out long c); d[id] = c + v; }
        internal static string Top3(Dictionary<string, long> d, bool ticks)
        {
            var l = new List<KeyValuePair<string, long>>(d); l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder(); double f = 1e6 / System.Diagnostics.Stopwatch.Frequency;
            for (int i = 0; i < l.Count && i < 3; i++) sb.Append(i > 0 ? ", " : "").Append(l[i].Key).Append(' ').Append(ticks ? (l[i].Value * f).ToString("0") + " us" : "x" + l[i].Value);
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        // The spot of r docked on 'edge' of a: side = gap away, top or bottom aligned; stack = gap away, left or right aligned.
        private static Vector2 DockedTopLeft(Rect a, Rect r, DockEdge edge, bool alignEnd)
        {
            float gap = Ui(GapPx);
            switch (edge)
            {
                case DockEdge.Right:  return new Vector2(a.xMax + gap, alignEnd ? a.yMax - r.height : a.yMin);
                case DockEdge.Left:   return new Vector2(a.xMin - gap - r.width, alignEnd ? a.yMax - r.height : a.yMin);
                case DockEdge.Bottom: return new Vector2(alignEnd ? a.xMax - r.width : a.xMin, a.yMax + gap);
                case DockEdge.Top:    return new Vector2(alignEnd ? a.xMax - r.width : a.xMin, a.yMin - gap - r.height);
            }
            return r.position;
        }

        // The anchor an element is placed from now: through hidden anchors (same edge) to the next shown one.
        private static Element LiveAnchor(Element e, out DockEdge edge, out bool alignEnd)
        {
            edge = e.Edge; alignEnd = e.AlignEnd;
            var a = e.Anchor != null && s_els.TryGetValue(e.Anchor, out var x) ? x : null;
            int guard = 0;
            // Through a hidden anchor the
            // element takes the HIDDEN element's own edge to its anchor, i.e. it steps into the hidden element's place.
            while (a != null && !IsShown(a) && guard++ < 16)
            {
                if (a.Anchor != null) { edge = a.Edge; alignEnd = a.AlignEnd; }
                a = a.Anchor != null && s_els.TryGetValue(a.Anchor, out var y) ? y : null;
            }
            return a;
        }

        /// <summary>Re-places every docked element from its anchor's live rect (children after parents). Every LateUpdate.</summary>
        // Roots and floating elements
        // keep their committed top-left + scale, re-applied every LateUpdate (after the Animator) unless being dragged or scaled.
        // A follower takes its target's live top-left + its offset while the target is shown (else it stays committed).
        private static bool FollowTick(Element e)
        {
            if (!s_els.TryGetValue(e.O.FollowId, out var target) || target.O.Rect == null || !IsShown(target)) return false;
            var tt = TopLeft(target.O.Rect);
            if (!e.HasFollow)
            {
                e.FollowOffset = float.IsNaN(e.O.FollowOffset.x) ? TopLeft(e.O.Rect) - tt : e.O.FollowOffset;
                e.HasFollow = true;
                Log($"{e.O.Id} follows {target.O.Id} at offset {e.FollowOffset.x:0},{e.FollowOffset.y:0}");
            }
            var want = tt + e.FollowOffset;
            // A follow offset that puts the follower off screen (a bad saved layout / an old clamp) is dropped: it goes back to the
            // spot it had beside its target by default (logged once)
            // Only a broken offset (NaN / infinite / more than 4 screens away) is reset; a follower the
            // player or its owner placed partly or fully off-screen stays there.
            if (float.IsNaN(want.x) || float.IsNaN(want.y) || float.IsInfinity(want.x) || float.IsInfinity(want.y)
                || Mathf.Abs(want.x) > 4f * Screen.width || Mathf.Abs(want.y) > 4f * Screen.height)
            {
                var def = e.DefaultTopLeft - tt;
                if (float.IsNaN(def.x) || tt.x + def.x < -8f || tt.x + def.x > Screen.width - 8f || tt.y + def.y < -8f || tt.y + def.y > Screen.height - 8f) def = new Vector2(0f, ScreenRect(target.O.Rect).height + Ui(GapPx));
                Log($"{e.O.Id}: follow offset {e.FollowOffset.x:0},{e.FollowOffset.y:0} put it off screen; back to {def.x:0},{def.y:0} beside {target.O.Id}");
                e.FollowOffset = def; want = tt + def;
            }
            var now = TopLeft(e.O.Rect);
            if ((want - now).sqrMagnitude > 0.25f) MoveBy(e.O.Rect, want - now);
            e.Committed = want;
            return true;
        }

        internal static void Commit(Element e)
        {
            if (e == null || e.O.Rect == null || e.Anchor != null || e.O.Passive) return;
            if (UseInvSpot(e)) { if (InventoryEditMode || InDrag(e)) { e.InvCommitted = TopLeft(e.O.Rect); e.HasInvCommit = true; } return; }
            e.Committed = TopLeft(e.O.Rect); e.HasCommit = true;
            if (InDrag(e)) e.FromEmbedded = false;   // The player placed it: no computed default any more
        }

        // The default spot for the chat-channel panel is computed LIVE from
        // rpgclasses.bars' drawn rect (front + back rows): its bottom 20 GUI above the bars' top, its right side 20 GUI in from the screen
        // edge. Only while the spot is still the built-in default (no player placement).
        private const string ChatId = "firesnpcs.chatchannels", BarsId = "rpgclasses.bars";
        private static Vector2 s_chatSpot; private static string s_chatSpotKey;   // Computed once per screen size

        // The group HUD's default is horizontally CENTRED on the territory pill (same drawn centre x),
        // kept live (the group list changes width with the party; the pill follows the plates); its y stays the table's. Only
        // while the spot is the built-in default; a player's placement wins.
        private static Vector2 s_groupSpot; private static bool s_hasGroupSpot;   // Held while the pill is away
        private const string GroupUserKey = "FiresDock.UserPlaced.hud.grouphud";   // The player dragged it: their spot wins
        internal static bool GroupUserPlaced { get { try { return PlayerPrefs.GetInt(GroupUserKey, 0) == 1; } catch { return false; } } }
        /// <summary>Forget the player's group HUD spot; it goes back to its default (centred under the day/time panel).</summary>
        public static void ResetGroupHudToDefault() { try { PlayerPrefs.DeleteKey(GroupUserKey); PlayerPrefs.Save(); } catch { } s_hasGroupSpot = false; s_groupDefaultDone = false; }
        private static bool s_groupDefaultDone;   // The default was placed this session
        private static Vector2 s_plateLast; private static int s_plateStill;
        // the plate (hud.plates) has kept the same drawn spot for 20 frames (it is re-laid out after login)
        private static bool GroupPlateSettled()
        {
            if (!s_els.TryGetValue("hud.plates", out var p) || p.O.Rect == null || !IsShown(p)) { s_plateStill = 0; return false; }
            var now = TopLeft(p.O.Rect);
            if ((now - s_plateLast).sqrMagnitude > 0.25f) { s_plateLast = now; s_plateStill = 0; return false; }
            return ++s_plateStill >= 20;
        }
        private static bool ComputedGroupHudSpot(Element e, out Vector2 spot)
        {
            spot = default;
            // . It was
            // "not always" because (1) it only followed while FromEmbedded (any drag or saved layout dropped it to a fixed spot), (2) its y
            // was the table's fixed y, not the pill's bottom, so a taller/shorter pill left it off, and (3) while RPGmaker unregisters the
            // pill (large-map hover) it fell back to the committed spot. Now ALWAYS: centred on the pill's drawn centre x, its drawn top a
            // dock gap under the pill's drawn bottom, every frame; while the pill is away the last computed spot is held.
            if (e.O.Id != "hud.grouphud") return false;
            // Centre under hud.plates (Core's day/time minimap plate), not rpgmaker.territory (which moves after login), and never
            // re-place every frame over the player's drag: the centred spot under hud.plates is only the DEFAULT; once the player drags it (GroupUserPlaced) the dock's normal path
            // (its follow offset, saved) places it and Core never re-centres it.
            if (GroupUserPlaced) return false;
            if (s_els.TryGetValue("hud.plates", out var t) && t.O.Rect != null && t.O.Rect.gameObject.activeInHierarchy && IsShown(t))
            {
                Rect tr = VisibleScreenRect(t, false), gr = VisibleScreenRect(e, false);
                if (tr.width >= 1f && gr.width >= 1f)
                {
                    var now = TopLeft(e.O.Rect);
                    float dx = tr.center.x - gr.center.x, dy = (tr.yMax + Ui(GapPx)) - gr.yMin;
                    spot = new Vector2(Mathf.Abs(dx) < 0.5f ? now.x : now.x + dx, Mathf.Abs(dy) < 0.5f ? now.y : now.y + dy);
                    s_groupSpot = spot; s_hasGroupSpot = true;
                    return true;
                }
            }
            if (!s_hasGroupSpot) return false;
            spot = s_groupSpot;
            return true;
        }

        /// <summary>The bottom-right corner the
        /// chat panel's default spot aims at, in screen px (y down): <paramref name="gapPx"/> in from the right edge and above the drawn
        /// top of rpgclasses.bars (back row included). False when the bars are not shown. Another mod aligns ITS bottom-right here.</summary>
        public static bool TryGetChatDefaultCorner(out Vector2 bottomRight, out float gapPx)
        {
            bottomRight = default; gapPx = 0f;
            if (!s_els.TryGetValue(BarsId, out var bars) || bars.O.Rect == null || !bars.O.Rect.gameObject.activeInHierarchy) return false;
            Rect b = VisibleScreenRect(bars, false); if (b.width < 1f) return false;
            float u = ResScale();
            try { if (s_els.TryGetValue(ChatId, out var c) && c.O.Rect != null) { var cv = c.O.Rect.GetComponentInParent<Canvas>(); if (cv != null && cv.rootCanvas.scaleFactor > 0.05f) u = cv.rootCanvas.scaleFactor; } } catch { }
            gapPx = 20f * u;
            bottomRight = new Vector2(Screen.width - gapPx, b.yMin - gapPx);
            return true;
        }
        private static bool ComputedChatSpot(Element e, out Vector2 spot)
        {
            spot = default;
            if (e.O.Id != ChatId || !e.FromEmbedded || !s_els.TryGetValue(BarsId, out var bars) || bars.O.Rect == null || !bars.O.Rect.gameObject.activeInHierarchy) return false;
            // Place by
            // the panel's DRAWN right + bottom edges (graphic walk), relative to where the root is now, so the drawn panel ends exactly
            // 20 GUI left of the screen edge and 20 GUI above the bars' drawn top, and growth upward (RPGmaker 0.0.696) never moves it
            // The drawn FRAME: Window/GrowBg when it is active, else the Window rect itself (the root keeps an
            // alpha-0 hit Image 4.6 px off the frame, so a plain graphic walk would be wrong)
            RectTransform frame = null;
            foreach (var t in e.O.Rect.GetComponentsInChildren<RectTransform>(false)) if (t.name == "GrowBg") { frame = t; break; }
            // Computed ONCE per
            // screen size and then held (the frame changes with the chat state, so a per-frame recompute moved the root), and the 20 GUI
            // Are in the CHAT canvas's own units (its root canvas scaleFactor, 1.546 at 3440 on the rig), the units measures in
            string key = Screen.width + "x" + Screen.height;
            if (s_chatSpotKey == key) { spot = s_chatSpot; return true; }
            Rect b = VisibleScreenRect(bars, false), v = frame != null ? ScreenRect(frame) : ScreenRect(e.O.Rect);
            if (b.width < 1f || v.width < 1f) return false;
            float u = ResScale();
            try { var cv = e.O.Rect.GetComponentInParent<Canvas>(); if (cv != null && cv.rootCanvas.scaleFactor > 0.05f) u = cv.rootCanvas.scaleFactor; } catch { }
            float g = 20f * u;
            var now = TopLeft(e.O.Rect);
            spot = new Vector2(now.x + (Screen.width - g - v.xMax), now.y + (b.yMin - g - v.yMax));
            s_chatSpot = spot; s_chatSpotKey = key;
            Log($"chat default spot {spot.x:0},{spot.y:0} (frame {(frame != null ? "GrowBg" : "Window")} {v.width:0}x{v.height:0}, bars top {b.yMin:0}, gap 20 x {u:0.###} px); held for {key}");
            return true;
        }

        // InventorySpot elements (see DockElementOptions.InventorySpot)
        private static bool UseInvSpot(Element e) => e.O.InventorySpot && InventoryGui.IsVisible();
        private static string InvKey(string id) => id + "@inventory";
        /// <summary>Where the element belongs now: its HUD spot, or (inventory open) its inventory spot / the computed gap default.</summary>
        private static Vector2 Spot(Element e)
        {
            if (!UseInvSpot(e)) return ComputedChatSpot(e, out var cs) ? cs : e.Committed;   // The group HUD default is placed once in Reflow
            if (e.HasInvCommit) return e.InvCommitted;
            // The free gap is between EVERY shown inventory-layer panel on the left
            // half (rightmost right edge) and every one on the right half (leftmost left edge): grid, character, companion, crafting ...
            // the WIDEST free horizontal gap between the panels' x spans (merged), so a panel docked right of the character panel
            // (vainv.companion, centred past the middle) still counts as part of the left block
            var spans = new List<Vector2>();
            foreach (var p in s_els.Values)
            {
                if (p.O.Layer != "inventory" || p.O.Passive || p.O.Rect == null || !p.O.Rect.gameObject.activeInHierarchy || !IsShown(p)) continue;
                Rect pr = DockRect(p); if (pr.width < 1f) continue;
                spans.Add(new Vector2(Mathf.Max(0f, pr.xMin), Mathf.Min(Screen.width, pr.xMax)));
            }
            Rect d = VisibleScreenRect(e, false);
            if (spans.Count == 0 || d.width < 1f) return e.Committed;
            spans.Sort((u, v) => u.x.CompareTo(v.x));
            float gapL = 0f, gapR = 0f, best = -1f, cursor = 0f;
            foreach (var sp in spans)
            {
                if (sp.x - cursor > best) { best = sp.x - cursor; gapL = cursor; gapR = sp.x; }
                cursor = Mathf.Max(cursor, sp.y);
            }
            if (Screen.width - cursor > best) { best = Screen.width - cursor; gapL = cursor; gapR = Screen.width; }
            if (best < d.width)
            {
                // No gap wide enough = TOP CENTRE, between the
                // grid panel's right edge and the minimap's left edge, just under the screen top (above the panels, which start lower)
                float l = s_els.TryGetValue("vainv.grid", out var gr) && gr.O.Rect != null && gr.O.Rect.gameObject.activeInHierarchy ? DockRect(gr).xMax : 0f;
                float r = s_els.TryGetValue("hud.minimap", out var mm) && mm.O.Rect != null && mm.O.Rect.gameObject.activeInHierarchy ? DockRect(mm).xMin : Screen.width;
                if (r - l < d.width) { l = 0f; r = Screen.width; }
                var tl = TopLeft(e.O.Rect);
                return new Vector2(tl.x + ((l + r) * 0.5f - d.center.x), tl.y + (6f * ResScale() - d.yMin));
            }
            var now = TopLeft(e.O.Rect);
            return new Vector2(now.x + ((gapL + gapR) * 0.5f - d.center.x), e.Committed.y);
        }
        private static void CommitAll(string layer) { foreach (var e in s_els.Values) if (layer == null || e.O.Layer == layer) Commit(e); }
        private static bool InDrag(Element e) => s_dragging != null && s_dragging.Moving.Contains(e);

        private static bool s_inWillRender;
        // Compare first, write only on a mismatch. A cheap pass over the roots / floaters (committed top-left vs
        // now, no allocation); the full Reflow runs only when one was moved after the ticker. Timing behind [Debug] PeriodicDiagnostics.
        private static readonly System.Diagnostics.Stopwatch s_wrSw = new System.Diagnostics.Stopwatch();
        private static long s_wrTicks; private static int s_wrFrames, s_wrFixes; private static float s_wrNextLog;
        private static readonly Dictionary<string, int> s_wrWho = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Vector2> s_wrLastDelta = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        private static void OnWillRenderCanvases()
        {
            if (s_inWillRender || s_els.Count == 0) return;
            PruneDead();
            s_inWillRender = true;
            s_wrSw.Restart();
            try
            {
                bool hudHidden = IsHudHidden; bool mismatch = false;
                foreach (var e in s_els.Values)
                {
                    if (e.Anchor != null || !e.HasCommit || e.O.Passive || e.OwnerPositions || e.O.Rect == null || InDrag(e) || !string.IsNullOrEmpty(e.O.FollowId)) continue;
                    if (hudHidden && e.O.Layer == "hud") continue;
                    if (!e.O.Rect.gameObject.activeInHierarchy) continue;
                    if (!IsShown(e)) continue;   // Reflow never places a hidden element, so it is never a mismatch to fix
                    if ((Spot(e) - TopLeft(e.O.Rect)).sqrMagnitude <= 0.25f) { e.FightFrames = 0; continue; }   // Spot = Committed, or the inventory spot
                    // An element its owner moves back EVERY frame is
                    // the owner's to place; after 120 frames in a row Core stops fighting it (one warning) instead of rewriting it forever
                    if (++e.FightFrames > 120)
                    {
                        e.OwnerPositions = true;
                        FiresCore.Logging.FiresLogger.LogWarning($"[Dock] {e.O.Id} ({e.O.Owner}) is repositioned by its owner every frame; the dock stops placing it. Owner: skip your own placement while it is registered (DockService / FiresShell.RegisterHudElement).");
                        continue;
                    }
                    {
                        mismatch = true;
                        // Who keeps being moved, and by how much
                        s_wrWho.TryGetValue(e.O.Id, out int c); s_wrWho[e.O.Id] = c + 1;
                        s_wrLastDelta[e.O.Id] = TopLeft(e.O.Rect) - Spot(e);
                        break;
                    }
                }
                if (mismatch) { Reflow(); s_wrFixes++; }
            }
            catch (Exception e) { Log("pre-render reflow threw " + e.Message); }
            finally
            {
                s_inWillRender = false;
                s_wrSw.Stop(); s_wrTicks += s_wrSw.ElapsedTicks; s_wrFrames++; WillRenderProbeTicks += s_wrSw.ElapsedTicks;
                if (FiresCore.Logging.Diag.Periodic && Time.unscaledTime >= s_wrNextLog)
                {
                    if (s_wrFrames > 0 && s_wrNextLog > 0f)
                    {
                        var who = new List<KeyValuePair<string, int>>(s_wrWho); who.Sort((x, y) => y.Value.CompareTo(x.Value));
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < who.Count && i < 3; i++)
                        { var dl = s_wrLastDelta.TryGetValue(who[i].Key, out var dv) ? dv : Vector2.zero; sb.Append(i > 0 ? "; " : "").Append(who[i].Key).Append(" x").Append(who[i].Value).Append(" (last off by ").Append(dl.x.ToString("0.0")).Append(",").Append(dl.y.ToString("0.0")).Append(")"); }
                        FiresCore.Logging.FiresLogger.LogDebug($"[Dock] pre-render check: {s_wrTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / s_wrFrames:0.0} us a frame over {s_wrFrames} frames, {s_wrFixes} re-applied, {s_els.Count} elements{(sb.Length > 0 ? "; moved after the ticker: " + sb : "")}");
                    }
                    s_wrWho.Clear(); s_wrLastDelta.Clear();
                    s_wrTicks = 0; s_wrFrames = 0; s_wrFixes = 0; s_wrNextLog = Time.unscaledTime + 60f;
                }
            }
        }

        internal static void Reflow()
        {
            if (s_els.Count == 0) return;
            if (s_migratedSave != null) { var ml = s_migratedSave; s_migratedSave = null; try { Save(ml); } catch (Exception ex) { Log("0.2.668 migration save failed: " + ex.Message); } }
            // Hiding the HUD moves vanilla's Hud root offscreen; re-placing HUD-layer elements
            // at their committed screen spots would pull them back on screen. While the HUD is hidden the HUD layer is left alone.
            bool hudHidden = Hud.instance != null && Hud.instance.m_rootObject != null && !Hud.instance.IsVisible();
            foreach (var e in s_els.Values)
            {
                if (hudHidden && e.O.Layer == "hud") continue;
                if (e.Anchor != null || !e.HasCommit || e.O.Passive || e.OwnerPositions || InDrag(e) || !IsShown(e)) continue;
                // The group HUD follows the day/time pill (centred, a gap under it), never its FollowId (hud.minimap at a
                // fixed offset, which drifted whenever the pill's width or spot changed)
                // Placed ONCE, when the day/time
                // plate has held still for a moment, then never again by Core: from here the normal follow (its offset = that spot) and
                // the player's drags place it. 650's every-frame centring fought the plate's own bounce (log: hud.grouphud re-placed x459).
                if (e.O.Id == "hud.grouphud" && !s_groupDefaultDone && !GroupUserPlaced && GroupPlateSettled() && ComputedGroupHudSpot(e, out var gsp))
                {
                    var gnow = TopLeft(e.O.Rect);
                    if ((gsp - gnow).sqrMagnitude > 0.25f) { MoveBy(e.O.Rect, gsp - gnow); Charge(s_costMoves, e.O.Id, 1); }
                    e.Committed = gsp;
                    if (!string.IsNullOrEmpty(e.O.FollowId) && s_els.TryGetValue(e.O.FollowId, out var gft) && gft.O.Rect != null && IsShown(gft))
                    { e.FollowOffset = gsp - TopLeft(gft.O.Rect); e.HasFollow = true; }
                    s_groupDefaultDone = true;
                    Log($"hud.grouphud: default spot placed once, centred under the day/time plate at {gsp.x:0},{gsp.y:0}; Core will not move it again");
                    continue;
                }
                if (!string.IsNullOrEmpty(e.O.FollowId) && FollowTick(e)) continue;   // A follower with a shown target is placed by it
                float want = e.Base * e.User;
                // Scale is only held per frame on the inventory layer (vanilla's Animator); HUD elements may animate their own scale (pulses)
                if ((e.O.Layer == "inventory" || IsScreenLayer(e.O.Layer)) && (Mathf.Abs(e.O.Rect.localScale.x - want) > 0.0001f || Mathf.Abs(e.O.Rect.localScale.y - want * e.Aspect) > 0.0001f)) e.O.Rect.localScale = new Vector3(want, want * e.Aspect, 1f);   // + height scale
                var now = TopLeft(e.O.Rect);
                var spot = Spot(e);
                if ((spot - now).sqrMagnitude > 0.25f) { MoveBy(e.O.Rect, spot - now); Charge(s_costMoves, e.O.Id, 1); }   // Counted
            }
            for (int pass = 0; pass < 4; pass++)   // chains settle in a few passes regardless of registration order
            {
                bool moved = false;
                foreach (var e in s_els.Values)
                {
                    if (e.Anchor == null || !IsShown(e) || (hudHidden && e.O.Layer == "hud")) continue;
                    // hud.plates is OWNER-placed (the minimap puts it under the map), but once docked this
                    // chain pass moved it to its dock spot every frame too - two writers. An owner-positioned element is never moved here.
                    if (e.OwnerPositions) continue;
                    if (s_dragging != null && s_dragging.Alt && s_dragging.E == e) continue;   // the one being pulled away
                    var a = LiveAnchor(e, out var edge, out var alignEnd);
                    if (a == null) continue;
                    var r = DockRect(e);    // HitBounds when the owner set one
                    var ar = DockRect(a);
                    var want = DockedTopLeft(ar, r, edge, alignEnd);
                    if (e.PendingOwnerSpot.HasValue) { e.DockOffset = e.PendingOwnerSpot.Value - want; e.PendingOwnerSpot = null; }
                    want += e.DockOffset;
                    // A docked
                    // member that would overflow first tries the other alignment on the same edge, then is clamped inside the screen (logged once)
                    if (!FitsOnScreen(new Rect(want, r.size)))
                    {
                        var alt = DockedTopLeft(ar, r, edge, !alignEnd);
                        string how;
                        if (FitsOnScreen(new Rect(alt, r.size))) { want = alt; how = "aligned to the other end"; }
                        else how = "kept at its docked spot (0.2.553: no automatic clamp; Fire)";
                        if (s_dockClampLogged.Add(e.O.Id + how)) Log($"{e.O.Id} docked {edge.ToString().ToLowerInvariant()} of {a.O.Id} would leave the screen: {how}");
                    }
                    if ((want - r.position).sqrMagnitude > 0.25f) { MoveBy(e.O.Rect, want - r.position); moved = true; Charge(s_costMoves, e.O.Id, 1); }   // Counted
                }
                if (!moved) break;
            }
        }

        // The best snap for a moving set (bounds b) against the other shown elements on the layer.
        internal static bool FindSnap(List<Element> moving, Rect b, string layer, out Element target, out DockEdge edge, out bool alignEnd, out Vector2 delta)
        {
            target = null; edge = DockEdge.None; alignEnd = false; delta = Vector2.zero;
            float best = Ui(SnapPx), gap = Ui(GapPx);
            // A set reaching past a screen edge by more
            // Than the snap distance is being placed off-screen on purpose - no magnet.
            if (b.xMin < -best || b.yMin < -best || b.xMax > Screen.width + best || b.yMax > Screen.height + best) return false;
            foreach (var m in moving) if (m.O.NoDock) return false;   // a NoDock panel is always free
            foreach (var o in s_els.Values)
            {
                if (o.O.Layer != layer || moving.Contains(o) || o.O.Passive || !IsShown(o) || o.O.NoDock) continue;
                // Docking onto its own follower causes a dock <-> follow loop with runaway offsets: a FOLLOWER is never a magnet (followers don't dock - see FollowId)
                if (!string.IsNullOrEmpty(o.O.FollowId)) continue;
                var r = DockRect(o);
                // An element the
                // dragged set deliberately overlaps (deeper than the dock gap on both axes) is not a magnet. Only an approach to its edge docks.
                if (Mathf.Min(b.xMax, r.xMax) - Mathf.Max(b.xMin, r.xMin) > gap && Mathf.Min(b.yMax, r.yMax) - Mathf.Max(b.yMin, r.yMin) > gap) continue;
                bool vOverlap = b.yMax > r.yMin - best && b.yMin < r.yMax + best;
                bool hOverlap = b.xMax > r.xMin - best && b.xMin < r.xMax + best;
                bool endY = Mathf.Abs(b.yMax - r.yMax) < Mathf.Abs(b.yMin - r.yMin);
                bool endX = Mathf.Abs(b.xMax - r.xMax) < Mathf.Abs(b.xMin - r.xMin);
                float alignY = endY ? r.yMax - b.yMax : r.yMin - b.yMin;
                float alignX = endX ? r.xMax - b.xMax : r.xMin - b.xMin;
                // The edges ALIGN (top-to-top, left-to-left...) only when they are already within the snap range; otherwise
                // the set stays where the player aims it along the edge
                float snapR = Ui(SnapPx);
                if (Mathf.Abs(alignY) > snapR) alignY = 0f;
                if (Mathf.Abs(alignX) > snapR) alignX = 0f;
                if (vOverlap)
                {
                    float dR = Mathf.Abs(b.xMin - (r.xMax + gap)); if (dR < best) { best = dR; target = o; edge = DockEdge.Right; alignEnd = endY; delta = new Vector2(r.xMax + gap - b.xMin, alignY); }
                    float dL = Mathf.Abs(b.xMax - (r.xMin - gap)); if (dL < best) { best = dL; target = o; edge = DockEdge.Left; alignEnd = endY; delta = new Vector2(r.xMin - gap - b.xMax, alignY); }
                }
                if (hOverlap)
                {
                    float dB = Mathf.Abs(b.yMin - (r.yMax + gap)); if (dB < best) { best = dB; target = o; edge = DockEdge.Bottom; alignEnd = endX; delta = new Vector2(alignX, r.yMax + gap - b.yMin); }
                    float dT = Mathf.Abs(b.yMax - (r.yMin - gap)); if (dT < best) { best = dT; target = o; edge = DockEdge.Top; alignEnd = endX; delta = new Vector2(alignX, r.yMin - gap - b.yMax); }
                }
            }
            return target != null;
        }

        // Diagnostic, Log Level = Debug, at the DROP only (never per
        // frame): a real drag that ended unsnapped names, for every same-layer element within twice the snap distance, why FindSnap passed
        // it over, with both rects (the dragged set's dock bounds and visible bounds, the candidate's dock rect) and the gap.
        private static void ExplainNoSnap(DragState s)
        {
            try
            {
                if (!FiresCore.Logging.FiresLogger.DebugEnabled) return;
                var b = Bounds(s.Moving); var vb = VisibleBounds(s.Moving);
                float snap = Ui(SnapPx), gap = Ui(GapPx), near = snap * 2f;
                string R(Rect r) => $"({r.xMin:0},{r.yMin:0})-({r.xMax:0},{r.yMax:0})";
                var parts = new List<string>();
                if (b.xMin < -snap || b.yMin < -snap || b.xMax > Screen.width + snap || b.yMax > Screen.height + snap) parts.Add("the set reaches past a screen edge (no magnet at all)");
                foreach (var o in s_els.Values)
                {
                    if (o.O.Layer != s.E.O.Layer || s.Moving.Contains(o)) continue;
                    var r = DockRect(o);
                    float dx = Mathf.Max(0f, Mathf.Max(r.xMin - b.xMax, b.xMin - r.xMax)), dy = Mathf.Max(0f, Mathf.Max(r.yMin - b.yMax, b.yMin - r.yMax));
                    if (dx > near || dy > near) continue;
                    string why;
                    if (o.O.Passive) why = "passive";
                    else if (!IsShown(o)) why = "not shown";
                    else if (!string.IsNullOrEmpty(o.O.FollowId)) why = "a follower";
                    else
                    {
                        float ox = Mathf.Min(b.xMax, r.xMax) - Mathf.Max(b.xMin, r.xMin), oy = Mathf.Min(b.yMax, r.yMax) - Mathf.Max(b.yMin, r.yMin);
                        if (ox > gap && oy > gap) why = $"deliberate overlap {ox:0}x{oy:0} px > gap {gap:0} (0.2.552 skip)";
                        else if (FindSnap(s.Moving, b, s.E.O.Layer, out var t, out _, out _, out var d) && t == o)
                            why = !FitsOnScreen(Shift(vb, d)) ? $"would dock, but the set would not fit on screen after {d.x:0},{d.y:0}" : !FitsOnScreen(VisibleBounds(Members(t))) ? "would dock, but its own group is off screen" : "would dock now (the last drag step never ran the magnet)";
                        else why = $"no edge within {snap:0} px (gap {gap:0})";
                    }
                    parts.Add($"{o.O.Id} {R(r)}: {why}");
                }
                Log($"{s.E.O.Id}: dropped unsnapped at dock bounds {R(b)} (visible {R(vb)}), travel {s.Travel:0} px - {(parts.Count == 0 ? "no element near" : string.Join("; ", parts))}");
            }
            catch (Exception ex) { Log("no-snap explain failed: " + ex.Message); }
        }

        private static void Join(Element a, Element b)
        {
            string g = b.Group ?? a.Group ?? ("g" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string old = a.Group, oldB = b.Group;
            foreach (var x in s_els.Values) if (x.O.Layer == a.O.Layer && ((old != null && x.Group == old) || (oldB != null && x.Group == oldB))) x.Group = g;
            a.Group = g; b.Group = g;
        }

        /// <summary>Take one member out of its cluster (double-click), committed and saved where it is.</summary>
        internal static void DetachMember(Element e)
        {
            if (e == null || e.Group == null) return;
            string g = e.Group;
            Undock(e); Reflow(); CommitAll(e.O.Layer); Save(e.O.Layer);
            Log($"{e.O.Id}: detached from group {g} (double-click); it moves on its own now");
        }

        // Pull one member out: its dependents re-anchor to its own anchor (or become the group's root), it floats.
        private static void Undock(Element e)
        {
            if (e.Group == null) return;
            // Keep the dock it had when this pull began, so dropping it back on that spot re-docks it (EndDrag)
            if (e.DragStartDocked)
            {
                e.HasLastDock = true; e.LastAnchor = e.DragStartAnchor; e.LastGroup = e.DragStartGroup; e.LastEdge = e.DragStartEdge;
                e.LastAlignEnd = e.DragStartAlignEnd; e.LastOffset = e.DragStartOffset; e.LastSpot = e.DragStartSpot;
            }
            foreach (var x in s_els.Values)
                if (x.Anchor == e.O.Id) { x.Anchor = e.Anchor; if (e.Anchor == null) { x.Edge = DockEdge.None; } }
            string g = e.Group; e.Group = null; e.Anchor = null; e.Edge = DockEdge.None; e.DockOffset = Vector2.zero; e.PendingOwnerSpot = null;
            int left = 0; Element last = null;
            foreach (var x in s_els.Values) if (x.Group == g) { left++; last = x; }
            if (left == 1) { last.Group = null; last.Anchor = null; }   // a group of one is floating
            Commit(e); if (left == 1) Commit(last);
            Log("undock " + e.O.Id);
        }

        // ---- drag / scale --------------------------------------------------------------------------------------------
        internal static Element Find(string id) => id != null && s_els.TryGetValue(id, out var e) ? e : null;
        internal static bool CanDrag(Element e) => e != null && (ElementActive(e) || (e.O.LiveEdit && IsShown(e)));   // + live panels, no editor

        // Free placement: the non-unified UI never docks; in the unified UI, Alt held at the drag start or during it skips every magnet.
        private static bool FreePlacement(DragState s) => !FiresCore.UI.Shell.FiresShell.OverrideOn || s.AltKey
            || UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);

        internal sealed class DragState { public Element E; public List<Element> Moving; public bool Alt; public bool AltKey; public Element Snap; public DockEdge Edge; public bool AlignEnd; public Vector2 SnapDelta; public bool Snapped; public Element Root; public float Travel; }

        // A click or a tiny move never docks; the magnet only runs after a real drag, and only for a spot on screen.
        private const float SnapAfterPx = 12f;
        private static bool FitsOnScreen(Rect b) => b.xMin >= -1f && b.yMin >= -1f && b.xMax <= Screen.width + 1f && b.yMax <= Screen.height + 1f;

        /// <summary>Every root / floating element of the layer kept inside the screen (after any drag, dock, scale or reset).</summary>
        internal static void ClampAll(string layer)
        {
            foreach (var e in s_els.Values)
                if (e.O.Layer == layer && e.Anchor == null && !e.O.Passive && IsShown(e)) Clamp(e);
        }

        internal static DragState BeginDrag(Element e, bool alt)
        {
            // Dragging a docked NON-root member pulls just it (it snaps back unless released more
            // than 80 px from its docked spot); dragging the root moves the group; Alt pulls any member and always undocks.
            // Fire 10-23: in the NON-unified UI nothing docks: every drag is a free single-element move (undocked on drop), like Alt.
            bool free = !FiresCore.UI.Shell.FiresShell.OverrideOn;
            if (free) alt = true;
            bool pull = e.Group != null && (alt || e.Anchor != null);
            var s = new DragState { E = e, Alt = pull || free, AltKey = alt };
            if (free || alt) Log($"{e.O.Id}: drag {(free ? "in the non-unified UI" : "with Alt")}: free placement, no dock / magnet / edge snap");
            s.Moving = s.Alt ? new List<Element> { e } : Members(e);
            // the group's root (no anchor) carries the move; docked members follow it through the reflow
            s.Root = e;
            if (!s.Alt) foreach (var m in s.Moving) if (m.Anchor == null) { s.Root = m; break; }
            s_dragging = s;
            // Remember a docked member's dock as it was when the drag began (Undock runs at the drop, after it has moved)
            e.DragStartDocked = e.Anchor != null; if (e.DragStartDocked) { e.DragStartAnchor = e.Anchor; e.DragStartGroup = e.Group; e.DragStartEdge = e.Edge; e.DragStartAlignEnd = e.AlignEnd; e.DragStartOffset = e.DockOffset; e.DragStartSpot = TopLeft(e.O.Rect); }
            // What the pull-start dock holds
            if (e.DragStartDocked) Log($"{e.O.Id}: drag begins docked on the {e.Edge.ToString().ToLowerInvariant()} edge of {e.Anchor}: top-left ({e.DragStartSpot.x:0},{e.DragStartSpot.y:0}), dock offset ({e.DockOffset.x:0},{e.DockOffset.y:0}), owner spot pending {(e.PendingOwnerSpot.HasValue ? $"({e.PendingOwnerSpot.Value.x:0},{e.PendingOwnerSpot.Value.y:0})" : "none")}");
            DetachFromColumn(e);
            return s;
        }

        internal static void DragBy(DragState s, Vector2 screenDelta)
        {
            // every member whose anchor isn't inside the moving set carries the move (normally just the root); the rest follow by reflow
            var movers = new List<Element>();
            foreach (var m in s.Moving) { var an = Find(m.Anchor); if (s.Alt || an == null || !s.Moving.Contains(an)) movers.Add(m); }
            if (s.Snapped) foreach (var m in movers) MoveBy(m.O.Rect, -s.SnapDelta);
            foreach (var m in movers) MoveBy(m.O.Rect, screenDelta);
            Reflow();
            s.Snapped = false;
            s.Travel += screenDelta.magnitude;
            if (s.Travel < Ui(SnapAfterPx)) return;   // No magnet until a real drag
            if (UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)) return;   // Shift = free placement, no dock / edge snap
            if (FreePlacement(s)) return;   // Alt (at the start or held now) or the non-unified UI = free placement
            if (!DockingOn) return;   // Docking OFF = no magnets, no groups formed, no edge snap
            if (FindSnap(s.Moving, Bounds(s.Moving), s.E.O.Layer, out var t, out var edge, out var alignEnd, out var d)
                && FitsOnScreen(Shift(VisibleBounds(s.Moving), d)) && FitsOnScreen(VisibleBounds(Members(t))))
            {
                foreach (var m in movers) MoveBy(m.O.Rect, d);
                Reflow();
                s.Snapped = true; s.SnapDelta = d; s.Snap = t; s.Edge = edge; s.AlignEnd = alignEnd;
                return;
            }
            // The screen-edge snap happens once, on drop (EndDrag): snapping every frame kept pulling a panel dragged along an edge back flush.
        }

        // On drop, a player-dragged set that ends near a screen edge snaps flush to it (no dock, it stays floating). Never on load /
        // reflow / register: code-placed off-screen spots (vanilla parks hidden panels off-screen) are left alone. Shift = free placement.
        private static void EdgeSnapOnDrop(DragState s)
        {
            if (s.Snapped || s.Travel < Ui(SnapAfterPx) || !DockingOn || FreePlacement(s)) return;
            if (UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift)) return;
            var vb = VisibleBounds(s.Moving);
            float snapE = Ui(SnapPx);
            Vector2 ed = Vector2.zero;
            if (Mathf.Abs(vb.xMin) < snapE) ed.x = -vb.xMin; else if (Mathf.Abs(Screen.width - vb.xMax) < snapE) ed.x = Screen.width - vb.xMax;
            if (Mathf.Abs(vb.yMin) < snapE) ed.y = -vb.yMin; else if (Mathf.Abs(Screen.height - vb.yMax) < snapE) ed.y = Screen.height - vb.yMax;
            if (ed.sqrMagnitude <= 0.01f) return;
            foreach (var m in s.Moving) { var an = Find(m.Anchor); if (s.Alt || an == null || !s.Moving.Contains(an)) MoveBy(m.O.Rect, ed); }
            Reflow();
            s.Snapped = true; s.SnapDelta = ed; s.Snap = null; s.Edge = DockEdge.None;
            Log($"{s.E.O.Id}: screen-edge snap on drop {ed.x:0},{ed.y:0} px (within {snapE:0} px of the edge; hold Shift for free placement)");
        }

        private static readonly HashSet<string> s_edgeLogged = new HashSet<string>(StringComparer.Ordinal);

        internal static void EndDrag(DragState s)
        {
            s_dragging = null;
            EdgeSnapOnDrop(s);
            if (s.Travel >= Ui(SnapAfterPx)) s_playerMovedSinceSave = true;   // a hand-over round keeps what the player moved
            if (!s.Snapped && s.Travel >= Ui(SnapAfterPx) && DockingOn) ExplainNoSnap(s);
            // The Reflow below re-places every root
            // at its COMMITTED spot, which was still the pre-drag one, so the move was undone and then re-committed. The dragged roots
            // commit their new spot first. A docked member pulled out (Alt) is committed by Undock.
            var before = new Dictionary<string, Vector2>();
            foreach (var m in s.Moving) if (m.HasCommit) before[m.O.Id] = m.Committed;
            foreach (var m in s.Moving) if (m.Anchor == null) Commit(m);   // The offset survives a short pull (cleared by Undock / snap)
            // The 80 px snap-back is gone; a real drag (12 px+) of a docked member undocks it,
            // a click or a tiny move leaves it docked.
            if (s.Alt && !s.AltKey && !s.Snapped && s.E.Anchor != null && s.Travel < Ui(SnapAfterPx))
            {
                var a = LiveAnchor(s.E, out var edge, out var alignEnd);
                if (a != null)
                {
                    var r = DockRect(s.E);
                    float dist = Vector2.Distance(r.position, DockedTopLeft(DockRect(a), r, edge, alignEnd));
                    if (dist <= Ui(UndockPx))
                    {
                        Reflow();   // a short pull: it snaps back onto its docked spot
                        Log($"{s.E.O.Id} pulled {dist:0} px (< {Ui(UndockPx):0}): stays docked");
                        return;
                    }
                }
            }
            if (s.Alt) Undock(s.E);
            // A docked spot can sit closer than one dock gap to its anchor, which the magnet never aims for. A floating element dropped within the snap distance of the spot it was pulled from re-docks
            // exactly as it was: same anchor, edge, alignment, offset and group. Any other drop is unchanged.
            if (!s.Snapped && s.E.Anchor == null && s.E.HasLastDock && s.Moving.Count == 1 && DockingOn)
            {
                var back = Find(s.E.LastAnchor);
                float d = Vector2.Distance(TopLeft(s.E.O.Rect), s.E.LastSpot);
                if (back != null && IsShown(back) && back.O.Layer == s.E.O.Layer && d <= Ui(SnapPx))
                {
                    var e = s.E;
                    e.Anchor = e.LastAnchor; e.Edge = e.LastEdge; e.AlignEnd = e.LastAlignEnd; e.DockOffset = e.LastOffset; e.PendingOwnerSpot = null;
                    if (back.Group == null && e.LastGroup != null) e.Group = e.LastGroup;   // Join keeps this id: the group comes back as it was
                    Join(e, back);
                    e.HasLastDock = false;
                    Log($"dock anchor: {e.O.Id} put back on its own spot ({d:0} px off) - re-docked on the {e.Edge.ToString().ToLowerInvariant()} edge of {back.O.Id} as before (group {e.Group})");
                }
            }
            if (s.Snapped && s.Snap != null)
            {
                // the dragged set docks by its root (or the pulled member) onto the target
                var docker = s.Alt ? s.E : s.Root;
                docker.Anchor = s.Snap.O.Id; docker.Edge = s.Edge; docker.AlignEnd = s.AlignEnd; docker.DockOffset = Vector2.zero; docker.PendingOwnerSpot = null;
                // Keep the drop spot ALONG the edge (no jump to the aligned corner): the offset from the dock rule's spot on that axis
                {
                    var ar = DockRect(s.Snap); var dr = DockRect(docker);
                    var rule = DockedTopLeft(ar, dr, s.Edge, s.AlignEnd);
                    var off = dr.position - rule;
                    docker.DockOffset = s.Edge == DockEdge.Left || s.Edge == DockEdge.Right ? new Vector2(0f, off.y) : new Vector2(off.x, 0f);
                    if (docker.DockOffset.sqrMagnitude < 0.25f) docker.DockOffset = Vector2.zero;
                }
                Join(docker, s.Snap);
                Log($"dock anchor: {docker.O.Id} docked on the {s.Edge.ToString().ToLowerInvariant()} edge of {s.Snap.O.Id}, aligned to its {(s.AlignEnd ? (s.Edge == DockEdge.Left || s.Edge == DockEdge.Right ? "bottom" : "right") : (s.Edge == DockEdge.Left || s.Edge == DockEdge.Right ? "top" : "left"))} (group {docker.Group})");
            }
            Reflow();
            // A dock whose group no longer fits on screen is refused: the dragged set floats at the drop spot instead
            if (s.Snapped && s.Snap != null && !FitsOnScreen(VisibleBounds(Members(s.Alt ? s.E : s.Root))))
            {
                var docker = s.Alt ? s.E : s.Root;
                Undock(docker);
                Log($"{docker.O.Id}: dock refused (the docked group would leave the screen); floating at the drop spot");
                Reflow();
            }
            // No clamp after a drag - the drop spot is the player's (off-screen allowed); other elements are never touched
            CommitAll(s.E.O.Layer);
            if (s.Moving.Count > 1 && !s.Alt) { var rt0 = TopLeft(s.Root.O.Rect); Log($"group move: {s.Root.O.Id} + {s.Moving.Count - 1} member(s) (group {s.Root.Group}) now at {rt0.x:0},{rt0.y:0}"); }
            // A dragged follower keeps following at its new offset (when its target is shown)
            if (!string.IsNullOrEmpty(s.E.O.FollowId) && s_els.TryGetValue(s.E.O.FollowId, out var ft) && IsShown(ft))
            { s.E.FollowOffset = TopLeft(s.E.O.Rect) - TopLeft(ft.O.Rect); s.E.HasFollow = true; Log($"{s.E.O.Id} new follow offset {s.E.FollowOffset.x:0},{s.E.FollowOffset.y:0}"); }
            // A real drag of the group HUD makes the player's spot win from now on (kept across sessions)
            if (s.E.O.Id == "hud.grouphud" && s.Travel >= Ui(SnapAfterPx) && !GroupUserPlaced)
            { try { PlayerPrefs.SetInt(GroupUserKey, 1); PlayerPrefs.Save(); } catch { } Log("hud.grouphud: placed by the player; the centred default no longer applies"); }
            foreach (var m in s.Alt ? new List<Element> { s.E } : Members(s.E)) RaiseMoved(m);
            foreach (var m in s.Moving)
                if (m.Anchor == null && before.TryGetValue(m.O.Id, out var b0))
                {
                    var moved = m.Committed - b0;
                    if (moved.sqrMagnitude < 0.25f) Log($"{m.O.Id}: drag ended where it started ({(s.Travel < Ui(SnapAfterPx) ? "under the 12 px drag threshold" : "clamped / snapped back: see the lines above")})");
                }
            Save(s.E.O.Layer);
        }

        /// <summary>Grip: a floating element scales itself; a docked one scales its whole group (the reflow keeps them docked).</summary>
        internal static void ScaleBy(Element e, float factor) => ScaleBy(e, factor, factor);

        /// <summary>Width factor <paramref name="fx"/> and height factor <paramref name="fy"/> apart (User = width scale,
        /// Aspect = height / width).</summary>
        internal static void ScaleBy(Element e, float fx, float fy)
        {
            PruneDead();
            if (e == null || e.O.Rect == null) return;
            foreach (var m in Members(e))
            {
                if (m.O.Rect == null) continue;
                float oldW = m.User, oldH = m.User * m.Aspect;
                m.User = ClampUser(m, oldW * fx);
                m.Aspect = ClampAspect(m, ClampUser(m, oldH * fy) / m.User);   // Per element (ScalePerAxis = 0.3..2.5 per axis)
                var tl = TopLeft(m.O.Rect);
                Place(m.O.Rect, m.Base * m.User, tl, m.Aspect);
                Commit(m);
            }
            Reflow();
        }

        /// <summary>Scales ONE element (not its dock group); its neighbours follow by the dock rule on the reflow.</summary>
        internal static void ScaleOne(Element m, float fx, float fy)
        {
            PruneDead();
            if (m == null || m.O.Rect == null) return;
            float oldW = m.User, oldH = m.User * m.Aspect;
            m.User = ClampUser(m, oldW * fx);
            m.Aspect = ClampAspect(m, ClampUser(m, oldH * fy) / m.User);
            var tl = TopLeft(m.O.Rect);
            Place(m.O.Rect, m.Base * m.User, tl, m.Aspect);
            Commit(m);
            Reflow();
        }

        internal static void EndScale(Element e)
        {
            if (e == null) return;
            CommitAll(e.O.Layer);   // No clamp after a scale either
            foreach (var m in Members(e)) RaiseMoved(m);
            Log(e.Group != null ? $"group {e.Group} scale {e.User:0.00}" : $"{e.O.Id} scale {e.User:0.00}");
            Save(e.O.Layer);
            if (IsScreenLayer(e.O.Layer)) ShareScreenSize(e);
        }

        // Single-panel screens (every "screen.*" layer: the NPC book's tabs, Dressing, ...) share ONE size. Resizing any of them
        // sets it for all (scale, or the rect size for owner-sized panels), it is saved (layout\screen_size.txt) and applied at register.
        private static float s_screenUser, s_screenAspect = 1f; private static Vector2 s_screenSize; private static bool s_screenLoaded;
        private static string ScreenSizePath => System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", "screen_size.txt");

        private static void LoadScreenSize()
        {
            if (s_screenLoaded) return; s_screenLoaded = true;
            try
            {
                if (!File.Exists(ScreenSizePath)) return;
                var f = File.ReadAllText(ScreenSizePath).Trim().Split('|');
                float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out s_screenUser);
                if (f.Length > 1) float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s_screenAspect);
                if (f.Length > 3) { float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s_screenSize.x); float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s_screenSize.y); }
                if (s_screenAspect <= 0f) s_screenAspect = 1f;
                Log($"shared screen size loaded: scale {s_screenUser:0.###} aspect {s_screenAspect:0.###} size {s_screenSize.x:0}x{s_screenSize.y:0}");
            }
            catch (Exception ex) { Log("shared screen size unreadable: " + ex.Message); }
        }

        private static void ShareScreenSize(Element src)
        {
            if (src.O.SizeChanged != null) s_screenSize = src.O.Rect.rect.size;
            else { s_screenUser = src.User; s_screenAspect = src.Aspect; }
            int n = 0;
            foreach (var m in new List<Element>(s_els.Values))
            {
                if (m == src || !IsScreenLayer(m.O.Layer) || m.O.Passive || m.O.Rect == null || m.Anchor != null) continue;
                if (ApplyScreenSize(m)) { n++; Save(m.O.Layer); }
            }
            if (!TestWritesBlocked)
                try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScreenSizePath)); File.WriteAllText(ScreenSizePath, string.Format(CultureInfo.InvariantCulture, "{0:0.###}|{1:0.###}|{2:0}|{3:0}", s_screenUser, s_screenAspect, s_screenSize.x, s_screenSize.y)); }
                catch (Exception ex) { Log("shared screen size not saved: " + ex.Message); }
            Log($"shared screen size from {src.O.Id}: scale {s_screenUser:0.###} size {s_screenSize.x:0}x{s_screenSize.y:0}; applied to {n} other screen panel(s)");
        }

        // applies the shared size to one screen element (true when it changed)
        private static bool ApplyScreenSize(Element m)
        {
            if (m.O.Rect == null) return false;
            var tl = TopLeft(m.O.Rect);
            if (m.O.SizeChanged != null)
            {
                if (s_screenSize.x < 1f || s_screenSize.y < 1f) return false;
                var want = Vector2.Max(s_screenSize, m.O.MinSize);
                if ((m.O.Rect.rect.size - want).sqrMagnitude < 0.25f) return false;
                m.O.Rect.sizeDelta += want - m.O.Rect.rect.size;
                Place(m.O.Rect, m.O.Rect.localScale.x, tl);
                try { m.O.SizeChanged(want); } catch (Exception ex) { Log($"{m.O.Id}: SizeChanged threw {ex.Message}"); }
            }
            else
            {
                if (s_screenUser <= 0f) return false;
                float u = ClampUser(m, s_screenUser), a = ClampAspect(m, s_screenAspect);
                if (Mathf.Abs(u - m.User) < 0.0005f && Mathf.Abs(a - m.Aspect) < 0.0005f) return false;
                m.User = u; m.Aspect = a;
                Place(m.O.Rect, m.Base * m.User, tl, m.Aspect);
            }
            Commit(m);
            return true;
        }

        /// <summary>The owner's SizeReset, else the default size through SizeChanged. Scale-only
        /// elements are reset by User / Aspect = 1.</summary>
        private static void ResetSize(Element e)
        {
            if (e == null || e.O.Rect == null) return;
            try
            {
                if (e.O.SizeReset != null) { e.O.SizeReset(); Log($"{e.O.Id}: size reset (owner SizeReset) -> {e.O.Rect.rect.width:0}x{e.O.Rect.rect.height:0}"); return; }
                if (e.O.SizeChanged == null) return;
                Vector2 want = e.DefaultSize;
                if (want.x <= 0f || want.y <= 0f) return;
                want = Vector2.Max(want, e.O.MinSize);
                var tl = TopLeft(e.O.Rect);
                e.O.Rect.sizeDelta += want - e.O.Rect.rect.size;
                Place(e.O.Rect, e.O.Rect.localScale.x, tl);
                e.O.SizeChanged(want);
                Log($"{e.O.Id}: size reset -> {want.x:0}x{want.y:0} (SizeChanged)");
            }
            catch (Exception ex) { Log($"{e.O.Id}: size reset threw {ex.Message}"); }
        }

        internal static void ResetOne(Element e)
        {
            foreach (var m in Members(e))
            {
                m.Group = null; m.User = 1f; m.Aspect = 1f; m.Anchor = null; m.Edge = DockEdge.None;
                ResetSize(m);
                ReattachToColumn(m);
                Place(m.O.Rect, m.Base, m.DefaultTopLeft); s_saved.Remove(m.O.Id);
            }
            CommitAll(e.O.Layer);
            foreach (var m in Members(e)) if (m.OwnerPlaced) OwnerPlacedReset(m);   // Owner-placed elements go back to the owner's default
            Log("reset " + e.O.Id);
            Save(e.O.Layer);
        }

        private static void RaiseMoved(Element e)
        {
            try { Moved?.Invoke(e.O.Id, ScreenRect(e.O.Rect)); } catch (Exception ex) { Log("Moved subscriber threw: " + ex.Message); }
        }

        // ---- save / load ---------------------------------------------------------------------------------------
        private static long PlayerId()
        {
            try { return Game.instance != null && Game.instance.GetPlayerProfile() != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L; } catch { return 0L; }
        }

        // HUD: per character + resolution; inventory: per resolution.
        private static string PathFor(string layer)
        {
            string res = Screen.width + "x" + Screen.height;
            string name = layer == "inventory" ? "dock_inventory_" + res : IsScreenLayer(layer) ? "dock_" + layer + "_" + res : "dock_hud_" + PlayerId().ToString(CultureInfo.InvariantCulture) + "_" + res;
            return System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout", name + ".txt");
        }

        // The SHIPPED default layout.
        // fires_layout_capture <layer> writes the current arrangement next to the Core DLL (layout_defaults\<layer>.txt, shipped with
        // the mod); positions are screen FRACTIONS so it fits any resolution. Used when a player has no saved layout for that layer.
        private static string DefaultsPath(string layer)
            => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(DockService).Assembly.Location) ?? ".", "layout_defaults", layer + ".txt");

        /// <summary>Writes every registered element of the layer (position as screen fractions, user scale, group, anchor) as the
        /// shipped default. Returns the path, or null.</summary>
        public static string CaptureDefaults(string layer)
        {
            string p = DefaultsPath(layer);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                var sb = new StringBuilder("# Fires default layout, captured " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " at " + Screen.width + "x" + Screen.height + "\n# id|xFrac|yFrac|userScale|group|anchor|edge|alignEnd\n");
                int n = 0;
                foreach (var e in s_els.Values)
                {
                    if (e.O.Layer != layer || e.O.Rect == null) continue;
                    var tl = TopLeft(e.O.Rect);
                    sb.Append(e.O.Id).Append('|').Append((tl.x / Screen.width).ToString("0.#####", CultureInfo.InvariantCulture)).Append('|')
                      .Append((tl.y / Screen.height).ToString("0.#####", CultureInfo.InvariantCulture)).Append('|').Append(e.User.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append('|').Append(e.Group ?? "").Append('|').Append(e.Anchor ?? "").Append('|').Append((int)e.Edge).Append('|').Append(e.AlignEnd ? "1" : "0").Append('\n');
                    n++;
                }
                File.WriteAllText(p, sb.ToString());
                Log($"captured the {layer} layout as the shipped default ({n} elements, {Screen.width}x{Screen.height}) -> {p}");
                return p;
            }
            catch (Exception ex) { Log("capture failed: " + ex.Message); return null; }
        }

        // The built-in default layout (captured at 3440x1440; every placed element, while chat, build info, ship, mount and unlock keep
        // vanilla's spot), embedded so it ships with Core (a defaults FILE next to the DLL is not copied by DLL-only installs). Each row is measured
        // from the element's NEAREST screen edge in GUI units (pixels / min(W/1920, H/1080), 1.333 on the capture screen): x L = from the left,
        // R = right edge to the element's left side, C = from the centre; y T = from the top, B = from the bottom. Element sizes scale by
        // the same factor, so the gaps stay proportional on any size or aspect. A layout_defaults\<layer>.txt next to the DLL still wins.
        // id|xEdge|x|yEdge|y|userScale|group|anchor|edge|alignEnd|dockOffsetX|dockOffsetY
        private const float FireDefaultsUiScale = 1.33333f;
        // The 3440x1440 inventory and Dressing arrangements, same edge-relative GUI units.
        private static readonly string[] FireInventoryDefaults =
        {
            // Fire's unified-UI default 10-23 19:43 (his "save this" layout), all free at his exact top-left spots; the chest is NoDock in
            // VAInventory so a bigger chest grows down / right from this corner
            "vainv.grid|L|40|T|40|0.851|||0|0|0|0",
            "vainv.character|L|541.1|T|40|0.826|||0|0|0|0",
            "vainv.companion|L|973.4|T|40|0.98|||0|0|0|0",
            "companions.inventory|L|973.4|T|44.6|0.667|||0|0|0|0",
            "vainv.crafting|R|576.4|T|225.5|0.941|||0|0|0|0",
            "vainv.chest|L|44.6|T|537.5|1.255|||0|0|0|0",
        };
        private static readonly string[] FireDressingDefaults =
        {
            // Default Dressing spot
            "dressing.setup|L|111.8|T|46.5|0.918|||0|0|0|0",
            "dressing.book|L|854.6|T|47.9|1.063|||0|0|0|0",
            "dressing.wardrobe|R|875.5|T|47.3|0.907|||0|0|0|0",
        };
        // Default NPC book spot.
        private static readonly string[] FireNpcBookDefaults =
        {
            "npcbook.window|L|532.9|T|55.4|0.856|||0|0|0|0",
        };
        private static readonly string[] FireHudDefaults =
        {
            // ONLY Fires elements, from Fire's rig layout 10-23 (dock_hud_<Fire>_3440x1440; Fire's layout IS the default, placed once on a
            // fresh profile). Every vanilla hud.* row is gone (vanilla places those). rpgclasses.possession keeps its earlier row (not in his file).
            "vainv.hotbar2.food|L|23.8|B|279|1|||0|0|0|0",
            "vainv.hotbar2.ammo|L|23.3|B|351.5|1|||0|0|0|0",
            "vainv.hotbar2.tool|L|24.5|B|211.1|1|||0|0|0|0",
            "rpgclasses.bars|R|578.2|B|235|1|||0|0|0|0",
            "rpgclasses.summons|L|16|B|530|1|||0|0|0|0",
            "rpgclasses.possession|L|33|T|135.3|1|||0|0|0|0",
            "rpgmaker.territory|R|260|T|248|1|||0|0|0|0",
            "rpgmaker.globes|L|2.3|B|227.6|1.816|||0|0|0|0|0.837",
            "firesnpcs.chatchannels|R|480|B|559|1|||0|0|0|0",
            "firesmounts.flighthints|L|15|T|112.5|1|||0|0|0|0",
            "hud.plates|R|328.6|T|271.7|1|||0|0|0|0",
            "hud.grouphud|R|260.3|T|351.7|1|||0|0|0|0",
            "fap.buildmenu|L|731.8|T|-14.1|1|||0|0|0|0",
        };
        // The previous default table as shipped (vanilla rows included). Read ONLY by the load migration: a saved row equal to its row here
        // was the old default, not a player's move, and is dropped.
        private static readonly string[] OldFireHudDefaults670 =
        {
            "hud.pausemenu|C|-182|T|195|1|g3b3357|hud.boss|4|0|24|0",
            "hud.msg.topleft|L|46.5|T|124|1|||0|0|0|0",
            "hud.minimap|R|255|T|22.7|1|||0|0|0|0",
            "hud.piecehealth|C|35|T|517.5|1|||0|0|0|0",
            "vainv.hotbar2.food|L|23.8|B|279|1|||0|0|0|0",
            "hud.hotbar.vanilla|L|33|T|55.3|1|gfec245||0|0|0|0",
            "hud.keyhints|L|120|B|139.3|0.5|||0|0|0|0",
            "rpgclasses.bars|R|587.2|B|238.7|1|||0|0|0|0",
            "hud.action|C|-100|B|236|1|||0|0|0|0",
            "hud.saveicon|L|11|T|11|1|||0|0|0|0",
            "vainv.hotbar2.ammo|L|23.3|B|351.5|1|||0|0|0|0",
            "rpgmaker.territory|R|260|T|248|1|||0|0|0|0",
            "firesmounts.flighthints|L|0|T|135.3|1|gfec245|hud.hotbar.vanilla|4|0|-44|0",
            "hud.status|R|702.5|T|45|1|g2bddc5||0|0|0|0",
            "hud.plates|R|263.8|T|238.6|1|g2bddc5|hud.status|4|1|36.9|0",
            "hud.boss|C|-200|T|127|1|g3b3357||0|0|0|0",
            "hud.stealth|C|-120.2|T|544.3|1|||0|0|0|0",
            "hud.stagger|C|161.9|B|483.9|1|||0|0|0|0",
            "hud.eitr|C|-15.7|B|162|1|||0|0|0|0",
            "hud.adrenaline|C|-40|B|162|1|||0|0|0|0",
            "firesnpcs.chatchannels|R|480|B|559|1|||0|0|0|0",
            "hud.stamina|C|-44.5|B|162|1|||0|0|0|0",
            "hud.health|L|49.5|B|265.9|1|||0|0|0|0",
            "hud.connection|L|11|T|11|1|||0|0|0|0",
            "hud.event|C|-200|T|90|1|||0|0|0|0",
            "hud.hover|C|19.5|B|532.5|1|||0|0|0|0",
            "hud.power|L|145|B|150|1|||0|0|0|0",
            "hud.msg.center|C|-128.5|T|377|1|||0|0|0|0",
            "hud.grouphud|R|259.3|T|313.4|1|||0|0|0|0",
            "rpgclasses.possession|L|33|T|135.3|1|gfec245|hud.hotbar.vanilla|4|0|0|0",
            "rpgclasses.summons|L|16|B|530|1|||0|0|0|0",
            "vainv.hotbar2.tool|L|24.5|B|211.1|1|||0|0|0|0",
            "rpgmaker.globes|L|2.3|B|227.6|1.816|||0|0|0|0|0.837",
            "fap.buildmenu|L|601|T|75|1|gfec245|hud.hotbar.vanilla|2|0|0|26.3",
            "hud.msg.biome|L|708|B|420.8|1|||0|0|0|0",
        };


        // ---- Migration off the old vanilla defaults --------------------------------------------------------------------
        private static readonly HashSet<string> s_migrationLogged = new HashSet<string>();
        private static readonly string[] OldVanillaGroups = { "g3b3357", "gfec245", "g2bddc5" };
        internal static bool IsVanillaHudId(string id) => id.StartsWith("hud.", StringComparison.Ordinal) && id != "hud.plates" && id != "hud.grouphud";

        private static bool WasOld670Default(string id, SavedEl sv, out string why)
        {
            why = null;
            string row = null;
            foreach (var r in OldFireHudDefaults670) if (r.StartsWith(id + "|", StringComparison.Ordinal)) { row = r; break; }
            if (row == null) return false;
            var f = row.Split('|');
            string grp = f[6].Length > 0 ? f[6] : null, anchor = f[7].Length > 0 ? f[7] : null;
            // only vanilla rows, and OUR rows that were docked to a vanilla element (their default is absolute now)
            if (!IsVanillaHudId(id) && !(anchor != null && IsVanillaHudId(anchor))) return false;
            if (sv.Group != grp || sv.Anchor != anchor) return false;
            float s = ResScale(), k = s / FireDefaultsUiScale;
            if (anchor != null)
            {
                int.TryParse(f[8], out int edge);
                float.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float ox);
                float.TryParse(f[11], NumberStyles.Float, CultureInfo.InvariantCulture, out float oy);
                if ((int)sv.Edge != edge || sv.AlignEnd != (f[9] == "1") || Mathf.Abs(sv.OX - ox * k) > 2f || Mathf.Abs(sv.OY - oy * k) > 2f) return false;
                why = $"docked to {anchor} edge {edge}, offset {sv.OX:0.#},{sv.OY:0.#}";
                return true;
            }
            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx);
            float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy);
            float ex = f[1] == "R" ? Screen.width - vx * s : f[1] == "C" ? Screen.width * 0.5f + vx * s : vx * s;
            float ey = f[3] == "B" ? Screen.height - vy * s : vy * s;
            if (Mathf.Abs(sv.X - ex) > 2f || Mathf.Abs(sv.Y - ey) > 2f) return false;
            why = $"at {sv.X:0.#},{sv.Y:0.#} = {f[1]}{vx} {f[3]}{vy}";
            return true;
        }

        // a group from the old defaults that no longer binds two kept rows is cleared (it only tied vanilla elements together)
        private static void ClearVanillaOnlyGroups()
        {
            foreach (var g in OldVanillaGroups)
            {
                var members = new List<string>();
                foreach (var kv in s_saved) if (kv.Value.Layer == "hud" && kv.Value.Group == g) members.Add(kv.Key);
                if (members.Count >= 2 && members.Exists(m => !IsVanillaHudId(m))) continue;   // still binds one of ours to something: kept
                foreach (var m in members) { s_saved[m].Group = null; }
                if (members.Count > 0 && s_migrationLogged.Add("group " + g)) Log($"group {g}: cleared ({string.Join(", ", members)}); it only bound the old vanilla defaults");
            }
        }

        private static int LoadEmbeddedDefaults(string layer)
        {
            string[] table = layer == "hud" ? FireHudDefaults : layer == "inventory" ? FireInventoryDefaults : layer == "screen.FiresDressing" ? FireDressingDefaults : layer == "screen.FiresNpcBook" ? FireNpcBookDefaults : null;
            if (table == null) return 0;
            // The rows are GUI units, so they are
            // placed with the LIVE canvas scale (resolution x the player's GUI scale setting), the same factor the elements are drawn at;
            // the resolution-only min(W/1920, H/1080) is the fallback before any game canvas exists
            // The Hud root measures 1.0 px/unit because Valheim scales the ELEMENTS, not their parent, so the rows stay in
            // resolution units, min(W/1920, H/1080). The chat spot
            // no longer depends on a scale at all (ComputedChatSpot measures the bars and the panel themselves).
            float s = ResScale(), k = s / FireDefaultsUiScale;
            int n = 0;
            foreach (var row in table)
            {
                var f = row.Split('|');
                if (f.Length < 12) continue;
                float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vx);
                float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float vy);
                var sv = new SavedEl { Layer = layer, Embedded = true };
                sv.X = f[1] == "R" ? Screen.width - vx * s : f[1] == "C" ? Screen.width * 0.5f + vx * s : vx * s;
                sv.Y = f[3] == "B" ? Screen.height - vy * s : vy * s;
                if (!float.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.User)) sv.User = 1f;
                if (f[6].Length > 0) sv.Group = f[6];
                if (f[7].Length > 0) sv.Anchor = f[7];
                if (int.TryParse(f[8], out var ed)) sv.Edge = (DockEdge)ed;
                sv.AlignEnd = f[9] == "1";
                float.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.OX); sv.OX *= k;
                float.TryParse(f[11], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.OY); sv.OY *= k;
                if (f.Length > 12 && float.TryParse(f[12], NumberStyles.Float, CultureInfo.InvariantCulture, out float asp) && asp > 0f) sv.Aspect = asp;   // Height / width scale (the globes)
                s_saved[f[0]] = sv; n++;
                if (f[0] == "firesnpcs.chatchannels" || f[0] == "rpgclasses.bars" || f[0] == "hud.minimap")   // Proof line
                    Log($"default {f[0]}: top-left {sv.X:0.#},{sv.Y:0.#} px ({f[1]}{vx} {f[3]}{vy} GUI x {s:0.###})");
            }
            Log($"no saved {layer} layout: using Fire's built-in default ({n} elements, GUI scale {s:0.###} (resolution scale) at {Screen.width}x{Screen.height})");
            return n;
        }

        /// <summary>The game's GUI canvas scale (Hud, else the inventory canvas); min(W/1920, H/1080) when neither exists yet.</summary>
        internal static float ResScale() => Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);   // What LoadEmbeddedDefaults uses

        internal static float LiveGuiScale()
        {
            try
            {
                // MEASURE screen px per local unit of the Hud root (the HUD elements' parent), the exact factor they draw at
                RectTransform rt = null;
                if (Hud.instance != null && Hud.instance.m_rootObject != null) rt = Hud.instance.m_rootObject.transform as RectTransform;
                if (rt == null && InventoryGui.instance != null) rt = InventoryGui.instance.transform as RectTransform;
                if (rt != null)
                {
                    var cv = rt.GetComponentInParent<Canvas>();
                    var cam = cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay ? cv.worldCamera : null;
                    Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(Vector3.zero)), b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(100f, 0f, 0f)));
                    float px = (b - a).magnitude / 100f;
                    if (px > 0.05f && px < 20f) return px;
                }
            }
            catch { }
            return Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
        }

        private static int LoadDefaults(string layer)
        {
            string p = DefaultsPath(layer);
            if (!File.Exists(p)) return LoadEmbeddedDefaults(layer);
            int n = 0;
            foreach (var line in File.ReadAllLines(p))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('|');
                if (f.Length < 4 || f[0].Length == 0) continue;
                var sv = new SavedEl { Layer = layer };
                float fx, fy;
                float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fx);
                float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out fy);
                sv.X = fx * Screen.width; sv.Y = fy * Screen.height;
                if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.User)) sv.User = 1f;
                if (f.Length > 4 && f[4].Length > 0) sv.Group = f[4];
                if (f.Length > 5 && f[5].Length > 0) sv.Anchor = f[5];
                if (f.Length > 6 && int.TryParse(f[6], out var ed)) sv.Edge = (DockEdge)ed;
                if (f.Length > 7) sv.AlignEnd = f[7] == "1";
                s_saved[f[0]] = sv; n++;
            }
            Log($"no saved {layer} layout: using the shipped default {p} ({n} elements)");
            return n;
        }

        private static void LoadIfNeeded()
        {
            string key = Screen.width + "x" + Screen.height + "/" + PlayerId();
            if (s_loadedFor == key) return;
            s_loadedFor = key; s_saved.Clear();
            var layers = new List<string> { "hud", "inventory", "screen.FiresDressing", "screen.FiresNpcBook" };   // + the screen layer with an embedded default (no file needed to load it)
            try   // Every saved screen layer of this resolution
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "layout"), suffix = "_" + Screen.width + "x" + Screen.height + ".txt";
                if (Directory.Exists(dir))
                    foreach (var p in Directory.GetFiles(dir, "dock_" + ScreenLayerPrefix + "*" + suffix))
                    {
                        string fn = System.IO.Path.GetFileName(p);
                        layers.Add(fn.Substring(5, fn.Length - 5 - suffix.Length));
                    }
            }
            catch { }
            foreach (var layer in layers)
            {
                string p = PathFor(layer);
                try
                {
                    if (!File.Exists(p)) { LoadDefaults(layer); continue; }
                    int n = 0;
                    foreach (var line in File.ReadAllLines(p))
                    {
                        // id|x|y|userScale|group|anchor|edge|alignEnd
                        var f = line.Split('|');
                        if (f.Length < 4 || f[0].Length == 0) continue;
                        var sv = new SavedEl { Layer = layer };
                        float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.X);
                        float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.Y);
                        if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.User)) sv.User = 1f;
                        if (f.Length > 4 && f[4].Length > 0) sv.Group = f[4];
                        if (f.Length > 5 && f[5].Length > 0) sv.Anchor = f[5];
                        if (f.Length > 6 && int.TryParse(f[6], out var ed)) sv.Edge = (DockEdge)ed;
                        if (f.Length > 7) sv.AlignEnd = f[7] == "1";
                        if (f.Length > 9) { float.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.OX); float.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out sv.OY); }   // |dockOffsetX|dockOffsetY
                        // |hidden was read here; now edit-session only, an old file's flag is ignored
                        if (f.Length > 11 && float.TryParse(f[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var asp) && asp > 0f) sv.Aspect = asp;
                        // Placeholder spots written by older saves (exactly 0,0 or exactly the screen centre, not docked) are not
                        // places anyone chose; the element keeps its own default, and the next save drops the line.
                        if (sv.Group == null && sv.Anchor == null
                            && ((sv.X == 0f && sv.Y == 0f) || (Mathf.Abs(sv.X - Screen.width * 0.5f) < 0.6f && Mathf.Abs(sv.Y - Screen.height * 0.5f) < 0.6f)))
                        {
                            Log($"{f[0]}: ignored placeholder spot {sv.X:0},{sv.Y:0} in {System.IO.Path.GetFileName(p)} (an older save of a hidden element); it keeps its default");
                            continue;
                        }
                        // A saved row equal to its old built-in default (same edge spot within 2 px at this resolution, or the same dock + offset, and the
                        // same group) is that default written back by a save, not a player's move: dropped, logged once, the next save leaves it out.
                        if (layer == "hud" && WasOld670Default(f[0], sv, out string oldWhy))
                        {
                            if (s_migrationLogged.Add(f[0])) Log($"{f[0]}: dropped the 0.2.670 built-in default row in {System.IO.Path.GetFileName(p)} ({oldWhy}); {(IsVanillaHudId(f[0]) ? "vanilla spot" : "Fire's default spot")}");
                            continue;
                        }
                        s_saved[f[0]] = sv; n++;
                    }
                    if (layer == "hud") ClearVanillaOnlyGroups();
                    Log($"loaded layout {p} ({n} elements)");
                }
                catch (Exception e) { Log("layout unreadable " + p + ": " + e.Message); }
            }
        }

        // A -firestest launch never writes
        // a layout file. Tests that check a save use their own byte backup + TestReload; the player's files are never touched by a round.
        private static bool? s_testLaunch;
        private static bool TestLaunch => s_testLaunch ?? (s_testLaunch = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-firestest", StringComparison.OrdinalIgnoreCase))).Value;

        /// <summary>Add-only: a test that checks the save itself (it byte-backs-up the files first and writes them back after,
        /// e.g. FDT dock_pointer_test save_reload) sets this true for that step only; every other save in a test round is skipped.</summary>
        public static bool TestAllowSave { get; set; }

        // A hand-over round (-firestest without -firestest-quit) gives Fire the client to play: a layout the PLAYER moved by hand is
        // saved there; code-placed changes and every automated round still never write.
        private static bool? s_handOverLaunch;
        private static bool HandOverLaunch => s_handOverLaunch ?? (s_handOverLaunch = TestLaunch && !Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-firestest-quit", StringComparison.OrdinalIgnoreCase))).Value;
        private static bool s_playerMovedSinceSave;

        /// <summary>True in a -firestest round unless a step opted in with
        /// TestAllowSave. EVERY writer into cache\FiresUnifiedCore\layout checks it (layouts, docking, column list, plate offset).</summary>
        internal static bool TestWritesBlocked => TestLaunch && !TestAllowSave;

        internal static void Save(string layer)
        {
            if (TestLaunch && !TestAllowSave && !(HandOverLaunch && s_playerMovedSinceSave)) { Log($"layout NOT saved ({layer}): a -firestest round never writes the player's layout file"); return; }
            if (TestLaunch && !TestAllowSave) Log($"layout saved ({layer}) in a hand-over round: the player moved it by hand");
            s_playerMovedSinceSave = false;
            PruneDead();
            foreach (var e in s_els.Values)
            {
                if (e.O.Layer != layer || e.O.Passive || e.O.Rect == null) continue;
                // A hidden or zero-size element has no
                // real place; its TopLeft reads 0,0 (chat while closed) or the screen centre (parked bars), and saving that turned it into a
                // "saved spot" the next load restored. Such an element keeps its previous good entry, or none.
                if (!e.O.Rect.gameObject.activeInHierarchy || e.O.Rect.rect.width < 1f || e.O.Rect.rect.height < 1f) continue;
                var tl = UseInvSpot(e) && e.HasCommit ? e.Committed : TopLeft(e.O.Rect);   // While it sits at its inventory spot, its HUD spot is Committed
                s_saved[e.O.Id] = new SavedEl { X = tl.x, Y = tl.y, User = e.User, Aspect = e.Aspect, Group = e.Group, Layer = layer, Anchor = e.Anchor, Edge = e.Edge, AlignEnd = e.AlignEnd, OX = e.Anchor != null ? e.DockOffset.x : 0f, OY = e.Anchor != null ? e.DockOffset.y : 0f, Hidden = EditHidden.Contains(e.O.Id) };
            }
            if (layer == "inventory")   // InventorySpot elements' own inventory spots (only the ones the player set)
                foreach (var e in s_els.Values)
                    if (e.O.InventorySpot && e.HasInvCommit) s_saved[InvKey(e.O.Id)] = new SavedEl { X = e.InvCommitted.x, Y = e.InvCommitted.y, User = e.User, Aspect = e.Aspect, Layer = "inventory" };
            string p = PathFor(layer);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                var sb = new StringBuilder(); int n = 0;
                foreach (var kv in s_saved)
                {
                    if (kv.Value.Layer != layer) continue;   // unknown ids of this layer (a mod not loaded) are kept
                    var v = kv.Value;
                    sb.Append(kv.Key).Append('|').Append(v.X.ToString("0.#", CultureInfo.InvariantCulture)).Append('|').Append(v.Y.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append('|').Append(v.User.ToString("0.###", CultureInfo.InvariantCulture)).Append('|').Append(v.Group ?? "").Append('|').Append(v.Anchor ?? "")
                      .Append('|').Append((int)v.Edge).Append('|').Append(v.AlignEnd ? "1" : "0")
                      .Append('|').Append(v.OX.ToString("0.#", CultureInfo.InvariantCulture)).Append('|').Append(v.OY.ToString("0.#", CultureInfo.InvariantCulture))
                      .Append('|').Append("0")   // Field 11 kept for readers, always 0 (edit-session visibility is never saved)
                      .Append('|').Append(v.Aspect.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');   // Field 12 = height / width scale
                    n++;
                }
                File.WriteAllText(p, sb.ToString());
                Log($"saved layout ({n} elements) for {Screen.width}x{Screen.height} -> {p}");
            }
            catch (Exception e) { Log("layout not saved: " + e.Message); }
        }

        // ---- handles + overlay -------------------------------------------------------------------------------------
        private static void AttachHandles(Element e)
        {
            var handle = e.O.DragHandle ?? e.O.Rect;
            var dh = handle.GetComponent<DockDragHandle>() ?? handle.gameObject.AddComponent<DockDragHandle>();
            dh.Id = e.O.Id;
            // Every inventory section also gets
            // the full-size grab area on top while its editor is open (slots, scroll lists and buttons inside a panel caught the drag
            // before it reached the panel's own handle). Smallest-under-cursor picking (TickPick) applies to both layers.
            if (!e.O.LiveEdit && ((e.O.Layer == "hud" && e.O.DragHandle == null) || e.O.Layer == "inventory" || IsScreenLayer(e.O.Layer)))   // + screen panels; a live panel keeps its contents clickable (header drag only)
            {
                // Layout-mode overlay (placeholder look until 's mock): outline + name + the hit area
                var go = new GameObject("FiresDockOverlay", typeof(RectTransform));
                var rt = (RectTransform)go.transform; rt.SetParent(e.O.Rect, false);
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                // Only the hit area lives here (invisible); the approved look (dashed outline + name tag) is drawn by DockLayoutOverlay.
                var img = go.AddComponent<Image>(); img.color = new Color(1f, 1f, 1f, 0f); img.raycastTarget = true;
                go.AddComponent<DockDragHandle>().Id = e.O.Id;
                // The grab area is its own canvas sorted above every panel child (a child canvas with its own sorting, e.g. one
                // lifted with the inventory, would otherwise draw over it and catch the drag); raycast by its own raycaster.
                var gcv = go.AddComponent<Canvas>(); gcv.overrideSorting = true; gcv.sortingOrder = 30900;
                go.AddComponent<GraphicRaycaster>();
                go.SetActive(false);
                e.Overlay = go;
            }
            // A grip on every corner (hit areas only; the look is
            // DockLayoutOverlay's corner handles), on every inventory panel whatever its owner asked, placed on the element's VISIBLE corners.
            // Every HUD element also gets ONE bottom-right handle for Esc direct resize (shown on hover in the Esc menu only; the
            // editor keeps its own corner squares)
            bool oneGrip = (e.O.LiveEdit || EscGripOnly(e)) && !e.O.ScalePerAxis;   // ScalePerAxis = four corner grips (the old globe)
            if (e.O.ShowGrip || e.O.Layer == "inventory" || e.O.Layer == "hud" || IsScreenLayer(e.O.Layer))
                for (int i = 0; i < 4; i++)
                {
                    if (oneGrip && i != 3) { e.Grips.Add(null); continue; }   // A live panel has the one bottom-right handle
                    var g = new GameObject("FiresDockGrip" + i, typeof(RectTransform));
                    var grt = (RectTransform)g.transform; grt.SetParent(e.O.Rect, false);
                    grt.anchorMin = grt.anchorMax = grt.pivot = new Vector2(0.5f, 0.5f); grt.sizeDelta = new Vector2(20f, 20f);
                    Image liveArt = null;
                    var gi = g.AddComponent<Image>(); gi.color = new Color(1f, 1f, 1f, 0f); gi.raycastTarget = true;
                    if (oneGrip || e.O.ScalePerAxis)   // + per-axis elements: art on all four corners
                    {
                        // REUSED from the hub window's bottom-right grip (ShellHub.BuildDragHandles
                        // + ShellWindowDrag): a transparent hit area, Core's shared grip art (FiresResizeGrip.AddGrip) in kit colours, Edge at
                        // rest and GoldBright on hover
                        // a live handle's invisible hit area is 40 px (the 18 px art is unchanged), so a press anywhere in the corner resizes
                        grt.sizeDelta = oneGrip && e.O.LiveEdit ? new Vector2(40f, 40f) : new Vector2(26f, 26f);
                        var art = new GameObject("Art", typeof(RectTransform)); var art2 = (RectTransform)art.transform; art2.SetParent(grt, false);
                        art2.anchorMin = art2.anchorMax = art2.pivot = new Vector2(0.5f, 0.5f); art2.sizeDelta = new Vector2(18f, 18f);
                        var gart = FiresCore.UI.FiresResizeGrip.AddGrip(art2, FiresCore.UI.Shell.Kit.FiresTokens.Edge);
                        if (gart != null) gart.raycastTarget = false;
                        art.AddComponent<FiresCore.UI.Shell.Kit.KitLeaf>();
                        liveArt = gart;
                    }
                    var gh = DockGrip.Wire(g, e.O.Id, i % 2 == 1 ? 1f : -1f, null, i < 2 ? -1f : 1f);   // Right corners grow to the right
                    if (liveArt != null) { gh.Tint = liveArt; gh.Rest = FiresCore.UI.Shell.Kit.FiresTokens.Edge; gh.Hover = FiresCore.UI.Shell.Kit.FiresTokens.GoldBright; }
                    // A grip had no canvas
                    // of its own, so it raycast at its parent canvas' order, UNDER the element's own grab area (a 30900 override canvas
                    // stretched over the same corners) and under any HUD canvas without a raycaster: the press always went to the move
                    // handle. Each grip is now its own canvas sorted above the grab area, with its own raycaster.
                    var gcan = g.AddComponent<Canvas>(); gcan.overrideSorting = true; gcan.sortingOrder = GripSortingOrder;
                    g.AddComponent<GraphicRaycaster>();
                    g.SetActive(false);
                    e.Grips.Add(grt);
                    if (i == 3) e.Grip = grt;
                }
        }

        internal const int GripSortingOrder = 30950;
        // A HUD element whose only handle is the Esc-menu hover handle (no owner grip, not a live panel)
        private static bool EscGripOnly(Element e) => !e.O.LiveEdit && !e.O.ShowGrip && e.O.Layer == "hud";

        // 0.5 s after a LIVE panel is shown, its header (DragHandle) and
        // its bottom-right handle are raycast through EventSystem.RaycastAll at their centres; PASS = that object (or a child) is the top
        // hit, else FAIL with the top three hits (names the blocker). Once per showing.
        private static readonly Dictionary<string, float> s_liveTestAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_liveTested = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<UnityEngine.EventSystems.RaycastResult> s_liveHits = new List<UnityEngine.EventSystems.RaycastResult>();
        private static void LiveHitTest(Element e)
        {
            if (s_liveTested.Contains(e.O.Id)) return;
            if (!s_liveTestAt.TryGetValue(e.O.Id, out float at)) { s_liveTestAt[e.O.Id] = Time.unscaledTime + 0.5f; return; }
            if (Time.unscaledTime < at) return;
            s_liveTested.Add(e.O.Id);
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;
            void Hit(string what, RectTransform rt)
            {
                if (rt == null || !rt.gameObject.activeInHierarchy) { Log($"live hit-test {e.O.Id} {what}: not shown"); return; }
                var c = new Vector3[4]; rt.GetWorldCorners(c); var cam = Cam(rt);
                Vector2 p = (RectTransformUtility.WorldToScreenPoint(cam, c[0]) + RectTransformUtility.WorldToScreenPoint(cam, c[2])) * 0.5f;
                s_liveHits.Clear(); es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es) { position = p }, s_liveHits);
                bool ok = s_liveHits.Count > 0 && s_liveHits[0].gameObject != null && s_liveHits[0].gameObject.transform.IsChildOf(rt);
                var top = new List<string>();
                for (int i = 0; i < s_liveHits.Count && i < 3; i++) top.Add(s_liveHits[i].gameObject != null ? s_liveHits[i].gameObject.name + "@" + (s_liveHits[i].module != null ? s_liveHits[i].module.name : "?") : "null");
                Log($"live hit-test {(ok ? "PASS" : "FAIL")} {e.O.Id} {what} [{rt.name}] at {p.x:0},{p.y:0}: top {(top.Count > 0 ? string.Join(" > ", top.ToArray()) : "nothing (no raycast target there)")}");
            }
            Hit("header", e.O.DragHandle);
            Hit("resize handle", e.Grip);
        }
        internal static void ResetLiveHitTest(string id) { s_liveTested.Remove(id); s_liveTestAt.Remove(id); }

        // ---- Test hooks for the REAL-pointer FDT test (screen points only; the test drives the EventSystem itself) ----
        /// <summary>Screen point (pixels, y up, as PointerEventData.position) at the centre of the element's header (DragHandle, else its
        /// rect); (-1,-1) when unknown. Add-only.</summary>
        public static Vector2 TestHeaderPoint(string id)
        {
            var e = Find(id); var rt = e != null ? (e.O.DragHandle ?? e.O.Rect) : null;
            if (rt == null) return new Vector2(-1f, -1f);
            var c = new Vector3[4]; rt.GetWorldCorners(c); var cam = Cam(rt);
            return (RectTransformUtility.WorldToScreenPoint(cam, c[0]) + RectTransformUtility.WorldToScreenPoint(cam, c[2])) * 0.5f;
        }

        /// <summary>Screen point (y UP, for SetCursorPos use Screen.height - y) of the element's
        /// drawn on-screen bottom-right corner, <paramref name="insetPx"/> in on both axes; (-1,-1) when not shown. Add-only.</summary>
        public static Vector2 TestCornerPoint(string id, float insetPx)
        {
            var e = Find(id); if (e == null || e.O.Rect == null || !IsShown(e)) return new Vector2(-1f, -1f);
            var r = VisibleScreenRect(e, true);
            if (r.width < 1f) return new Vector2(-1f, -1f);
            return new Vector2(r.xMax - insetPx, Screen.height - (r.yMax - insetPx));
        }

        /// <summary>Screen point (y up) at the centre of corner grip <paramref name="corner"/> (0 top-left, 1 top-right, 2 bottom-left,
        /// 3 bottom-right) as drawn now: the layout editor's corner square while an editor is open, else the live bottom-right handle;
        /// (-1,-1) when that grip isn't shown. Add-only.</summary>
        public static Vector2 TestGripPoint(string id, int corner)
        {
            var p = DockLayoutOverlay.CornerPoint(id, corner);
            if (p.x >= 0f) return p;
            var e = Find(id);
            if (e == null || corner < 0 || corner >= e.Grips.Count || e.Grips[corner] == null || !e.Grips[corner].gameObject.activeInHierarchy) return new Vector2(-1f, -1f);
            var rt = e.Grips[corner]; var c = new Vector3[4]; rt.GetWorldCorners(c); var cam = Cam(rt);
            return (RectTransformUtility.WorldToScreenPoint(cam, c[0]) + RectTransformUtility.WorldToScreenPoint(cam, c[2])) * 0.5f;
        }

        /// <summary>The element's current user scale and top-left (screen px, y down), for before/after checks. Add-only.</summary>
        public static bool TestState(string id, out float scale, out Vector2 topLeft, out string group)
        {
            var e = Find(id); scale = 0f; topLeft = Vector2.zero; group = null;
            if (e == null || e.O.Rect == null) return false;
            scale = e.User; topLeft = TopLeft(e.O.Rect); group = e.Group; return true;
        }

        internal static void TickOverlays()
        {
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null) continue;
                bool on = ElementActive(e) && IsShown(e);
                // The corner grip shows on the picked / selected element only (a grip on every element covered the small ones)
                bool grip = on && (e.Overlay == null || e.O.Id == s_pick || e.O.Id == Selected);
                bool live = e.O.LiveEdit && IsShown(e);   // A live panel shows its resize handle at all times
                if (live) LiveHitTest(e);   // Once per showing, prove the header + handle take the pointer
                else if (e.O.LiveEdit && s_liveTested.Contains(e.O.Id)) ResetLiveHitTest(e.O.Id);
                if (live && !on) { on = true; grip = true; }
                // A live element that opted into double-click reveal keeps its handle hidden until revealed (drag stays available)
                if (live && e.O.RevealOnDoubleClick && e.O.Id != s_revealed && !(EscEditMode || HudEditMode)) grip = false;
                if (e.Overlay != null && e.Overlay.activeSelf != on) { e.Overlay.SetActive(on); if (on) e.Overlay.transform.SetAsLastSibling(); }
                // Children the owner adds while editing (relayouts) must not end up over the grab area
                if (on && e.Overlay != null && e.Overlay.transform.GetSiblingIndex() != e.Overlay.transform.parent.childCount - 1) e.Overlay.transform.SetAsLastSibling();
                var vb = on ? VisibleLocalBounds(e) : default;
                for (int gi = 0; gi < e.Grips.Count; gi++)
                {
                    var gg = e.Grips[gi]; if (gg == null) continue;
                    bool show = grip;
                    // A HUD element's Esc handle shows on hover (or while dragged) in the Esc menu only, never in the editor
                    if (EscGripOnly(e)) show = on && (EscEditMode || e.O.Id == s_revealed) && (e.O.Id == s_pick || e.O.Id == s_revealed || (gg.gameObject.activeSelf && UnityEngine.Input.GetMouseButton(0)));   // held = stays while resizing
                    if (gg.gameObject.activeSelf != show)
                    {
                        gg.gameObject.SetActive(show);
                        if (show)
                        {
                            gg.SetAsLastSibling();
                            var gc = gg.GetComponent<Canvas>();   // a nested canvas drops overrideSorting while inactive: re-assert on show
                            if (gc != null) { gc.overrideSorting = true; gc.sortingOrder = GripSortingOrder; }
                        }
                    }
                    if (show)
                    {
                        // A LIVE panel's handle sits
                        // INSIDE its corner (inset by half its size), so it is on screen whenever the panel is
                        // A live / Esc handle sits INSIDE the panel's FRAME (its largest drawn
                        // plate, FrameLocalBounds), 3 px in from the corner like the hub window's grip; re-placed every frame, so it follows
                        // every move / resize. Editor corner squares stay centred on the drawn corners.
                        bool inside = e.O.LiveEdit || EscGripOnly(e);
                        // Only a PANEL (a header DragHandle) anchors to its plate;
                        // a whole-body element (no header, e.g. the globes) takes the union of everything it draws
                        bool panel = e.O.DragHandle != null && e.O.DragHandle != e.O.Rect;
                        var fb = inside ? (panel ? FrameLocalBounds(e) : vb) : vb;
                        float inset = inside ? gg.sizeDelta.x * 0.5f + 3f : 0f;
                        gg.localPosition = new Vector3(gi % 2 == 1 ? fb.xMax - inset : fb.xMin + inset, gi < 2 ? fb.yMax - inset : fb.yMin + inset, 0f);
                        if (inside && s_gripLogged.Add(e.O.Id))
                            Log($"{e.O.Id}: handle inside its frame corner: frame {fb.width:0}x{fb.height:0} (drawn bounds {vb.width:0}x{vb.height:0}), handle centre {gg.localPosition.x:0},{gg.localPosition.y:0} = {inset:0} px in");
                        ClampGripOnScreen(gg);
                    }
                }
                // The hit area covers what is actually drawn (some owners register a container larger / offset from its content)
                if (on && e.Overlay != null)
                {
                    var ort = (RectTransform)e.Overlay.transform;
                    ort.anchorMin = ort.anchorMax = ort.pivot = new Vector2(0.5f, 0.5f);
                    ort.localPosition = new Vector3(vb.center.x, vb.center.y, 0f); ort.sizeDelta = vb.size;
                }
            }
        }

        private static void EnsureTicker()
        {
            if (s_ticker != null) return;
            var go = new GameObject("FiresDockService"); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave;
            s_ticker = go.AddComponent<DockTicker>();
            // An owner's own LateUpdate (VAInventory PanelsLayout) can run AFTER the ticker and
            // put a panel back on its layout spot. The committed spots are re-applied once more right before the UI renders, after every
            // LateUpdate, so the player's / the dock's spot always wins.
            Canvas.willRenderCanvases += OnWillRenderCanvases;
        }

        // ---- test hooks (FDT; no OS input) ---------------------------------------------------------------------------
        /// <summary>Drags the element (its group, or alone with alt) so its top-left lands on <paramref name="toScreenTopLeft"/>
        /// (screen px, y down), then ends the drag exactly as a mouse would: snap (element or screen edge), dock / undock, reflow, save, Moved (no clamp).</summary>
        public static bool TestDrag(string id, Vector2 toScreenTopLeft, bool alt)
        {
            var e = Find(id); if (e == null) return false;
            var s = BeginDrag(e, alt);
            DragBy(s, toScreenTopLeft - TopLeft(e.O.Rect));
            EndDrag(s);
            Log($"test drag {id} -> {toScreenTopLeft.x:0},{toScreenTopLeft.y:0}{(alt ? " (alt)" : "")}: {(e.Group != null ? "docked in " + e.Group : "floating")}");
            return true;
        }

        /// <summary>Scales like a grip drag end - a docked element scales its whole group, a floating one only
        /// itself - to the user scale <paramref name="s"/> (0.5..1.5), then clamps, saves and raises Moved.</summary>
        public static bool TestScale(string id, float s)
        {
            var e = Find(id); if (e == null) return false;
            float factor = e.User > 0f ? Mathf.Clamp(s, MinScale, MaxScale) / e.User : 1f;
            ScaleBy(e, factor);
            EndScale(e);
            Log($"test scale {id} -> {e.User:0.00}{(e.Group != null ? " (group " + e.Group + ")" : "")}");
            return true;
        }

        /// <summary>Saves both layers, forgets what is loaded, reloads the files and re-places every registered
        /// element from them (the same path as a relog), so a test can assert the layout survives.</summary>
        public static void TestReload() => TestReload(true);

        /// <summary>TestReload without saving first when <paramref name="save"/> is false (restores whatever the files hold, e.g. a backup a test wrote back).</summary>
        public static void TestReload(bool save)
        {
            if (save) { Save("hud"); Save("inventory"); }
            s_loadedFor = null; s_saved.Clear();
            LoadIfNeeded();
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null || !s_saved.TryGetValue(e.O.Id, out var sv)) continue;
                Log($"{e.O.Id}: restored saved spot {sv.X:0},{sv.Y:0} scale {sv.User:0.00}{(sv.Anchor != null ? " docked to " + sv.Anchor : "")} (reload)");   // The same proof line as at Register
                e.User = ClampUser(e, sv.User); e.Aspect = ClampAspect(e, sv.Aspect);
                Place(e.O.Rect, e.Base * e.User, new Vector2(sv.X, sv.Y), e.Aspect);
                e.Group = sv.Group; e.Anchor = sv.Anchor; e.Edge = sv.Edge; e.AlignEnd = sv.AlignEnd; e.DockOffset = new Vector2(sv.OX, sv.OY); e.PendingOwnerSpot = null;   // The saved offset (0 in older files)
                    if (e.O.NoDock && (e.Anchor != null || e.Group != null)) { Log($"{e.O.Id}: NoDock - saved dock to {e.Anchor ?? "-"} / group {e.Group ?? "-"} dropped, kept at its saved top-left ({sv.X:0.#}, {sv.Y:0.#})"); e.Group = null; e.Anchor = null; e.Edge = DockEdge.None; e.AlignEnd = false; e.DockOffset = Vector2.zero; }
            }
            CommitAll(null);   // Commit the reloaded spots BEFORE the reflow (it re-places roots at their committed spots)
            Reflow();
            CommitAll(null);
            Log($"test reload: {s_els.Count} elements re-placed from the saved layouts");
        }

        /// <summary>The layout file this session reads and writes for the layer.</summary>
        public static string LayoutPath(string layer) => PathFor(layer);

        /// <summary>Snapshot plus visible, layer, edge, anchor id and the group's root id (the member with no anchor).</summary>
        public static List<(string id, Rect rect, string group, bool docked, float scale, bool visible, string layer, DockEdge edge, string anchor, string root)> SnapshotEx()
        {
            var l = new List<(string, Rect, string, bool, float, bool, string, DockEdge, string, string)>();
            foreach (var e in s_els.Values)
            {
                if (e.O.Rect == null) continue;
                string root = null;
                if (e.Group != null) foreach (var m in Members(e)) if (m.Anchor == null) { root = m.O.Id; break; }
                l.Add((e.O.Id, ScreenRect(e.O.Rect), e.Group, e.Group != null, e.User, IsShown(e), e.O.Layer, e.Edge, e.Anchor, root));
            }
            return l;
        }

        /// <summary>Every registered element: id, screen rect, group (or null), docked, user scale.</summary>
        public static List<(string id, Rect rect, string group, bool docked, float scale)> Snapshot()
        {
            var l = new List<(string, Rect, string, bool, float)>();
            foreach (var e in s_els.Values) if (e.O.Rect != null) l.Add((e.O.Id, ScreenRect(e.O.Rect), e.Group, e.Group != null, e.User));
            return l;
        }

        private static Rect Safe(Func<Rect> f) { try { return f(); } catch { return default; } }
        internal static void Log(string s) => FiresCore.Logging.FiresLogger.LogInfo("[Dock] dock: " + s);
    }

    // Console "fires_layout_capture <hud|inventory>" and "fires_layout_reset <layer>".
    [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class DockConsoleCommands
    {
        private static bool s_done;
        private static void Postfix()
        {
            if (s_done) return;
            s_done = true;
            new Terminal.ConsoleCommand("fires_layout_capture",
                "[FiresUnifiedCore] fires_layout_capture <hud|inventory> - writes the current arrangement as the SHIPPED default layout (layout_defaults next to the Core DLL)",
                args =>
                {
                    string layer = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (layer != "hud" && layer != "inventory") { args.Context?.AddString("fires_layout_capture: give a layer, hud or inventory"); return; }
                    var p = DockService.CaptureDefaults(layer);
                    args.Context?.AddString(p != null ? "fires_layout_capture: " + layer + " default written to " + p : "fires_layout_capture: failed (see the log)");
                });
            new Terminal.ConsoleCommand("fires_layout_reset",
                "[FiresUnifiedCore] fires_layout_reset <hud|inventory> - every element of the layer back to its default spot (the shipped default when one exists)",
                args =>
                {
                    string layer = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (layer != "hud" && layer != "inventory") { args.Context?.AddString("fires_layout_reset: give a layer, hud or inventory"); return; }
                    DockService.ResetLayer(layer);
                    args.Context?.AddString("fires_layout_reset: " + layer + " reset");
                });
        }
    }

    internal sealed class DockTicker : MonoBehaviour
    {
        // The dock's own per-frame cost, one Info line a minute (whatever the
        // log level), with the mode it ran in, so a user log shows at once whether the dock is the cost
        private static readonly System.Diagnostics.Stopwatch s_cost = new System.Diagnostics.Stopwatch();
        private static long s_costTicks, s_costMax; private static int s_costFrames; private static float s_costNext;
        private static void CostEnd()
        {
            s_cost.Stop(); long t = s_cost.ElapsedTicks; s_costTicks += t; if (t > s_costMax) s_costMax = t; s_cost.Reset();
            if (Time.unscaledTime < s_costNext) return;
            if (s_costFrames > 0)
            {
                double f = 1e6 / System.Diagnostics.Stopwatch.Frequency;
                // Per-phase averages (us a frame), the pre-render re-apply, and the top 3
                // elements by owner Visible() time and by re-placements
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < PhaseNames.Length; i++) { double us = s_phase[i] * f / s_costFrames; if (us >= 1.0) sb.Append(sb.Length > 0 ? ", " : "").Append(PhaseNames[i]).Append(' ').Append(us.ToString("0")); s_phase[i] = 0; }
                double wr = DockService.WillRenderProbeTicks * f / s_costFrames; DockService.WillRenderProbeTicks = 0;
                FiresCore.Logging.FiresLogger.LogInfo($"[Dock] cost: {s_costTicks * f / s_costFrames:0} us a frame avg, {s_costMax * f:0} us max, over {s_costFrames} frames ({DockService.CostMode}); phases (us/frame): {(sb.Length > 0 ? sb.ToString() : "all < 1")}; pre-render {wr:0}; Visible() top: {DockService.Top3(DockService.s_costVisible, true)}; re-placed top: {DockService.Top3(DockService.s_costMoves, false)}");
                DockService.s_costVisible.Clear(); DockService.s_costMoves.Clear();
            }
            s_costTicks = 0; s_costMax = 0; s_costFrames = 0; s_costNext = Time.unscaledTime + 60f;
        }
        private static readonly string[] PhaseNames = { "prune", "column", "overlays", "edit-modes", "hud-hide", "show-in-layout", "pick", "reflow", "after-reflow", "layout-overlay", "toggle", "guard" };
        private static readonly long[] s_phase = new long[12];
        private static long s_t;
        private static void Mark(int i) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); s_phase[i] += n - s_t; s_t = n; }
        private void Update()
        {
            s_cost.Start(); s_costFrames++; s_t = System.Diagnostics.Stopwatch.GetTimestamp();
            DockService.PruneDead(); Mark(0);
            try { DockService.TickColumnMembers(); } catch (Exception e) { DockService.Log("column members threw " + e.Message); } Mark(1);
            DockService.TickOverlays(); Mark(2);
            s_cost.Stop();   // Cost
        }
        private void LateUpdate()
        {
            s_cost.Start(); s_t = System.Diagnostics.Stopwatch.GetTimestamp();
            DockService.PruneDead(); Mark(0);
            try { DockService.TickEditModes(); } catch (Exception e) { DockService.Log("edit modes threw " + e.Message); } Mark(3);
            try { DockService.TickHudHide(); } catch (Exception e) { DockService.Log("hud hide threw " + e.Message); } Mark(4);
            try { DockService.TickShowInLayout(); } catch (Exception e) { DockService.Log("show-in-layout threw " + e.Message); } Mark(5);
            try { DockService.TickPick(); } catch (Exception e) { DockService.Log("pick threw " + e.Message); } Mark(6);
            try { DockService.Reflow(); } catch (Exception e) { DockService.Log("reflow threw " + e.Message); } Mark(7);
            try { DockService.AfterReflow?.Invoke(); } catch (Exception e) { DockService.Log("after-reflow threw " + e.Message); } Mark(8);   // Slot proxies
            try { DockLayoutOverlay.Tick(); } catch (Exception e) { DockService.Log("layout overlay threw " + e.Message); } Mark(9);
            try { DockToggle.Tick(); } catch (Exception e) { DockService.Log("docking toggle threw " + e.Message); } Mark(10);
            try { DockService.GuardMenuButtons(); } catch (Exception e) { DockService.Log("menu guard threw " + e.Message); } Mark(11);   // Last
            CostEnd();
        }   // placement in LateUpdate
    }

    internal sealed class DockDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        internal string Id;
        // Double-click a cluster member = select just it and take it out of the cluster (it then moves alone)
        public void OnPointerClick(PointerEventData ev)
        {
            var e = DockService.Find(Id);
            if (e == null || !DockService.CanDrag(e)) return;
            DockService.Selected = Id;
            if (ev.clickCount >= 2 && e.Group != null) DockService.DetachMember(e);
        }
        private DockService.DragState _s;
        public void OnBeginDrag(PointerEventData ev)
        {
            if (DockService.DirectDragActive) { _s = null; return; }   // The direct drag already moves it (no double drag)
            var e = DockService.Find(Id);
            // Every move drag logs its start or why it was refused
            if (!DockService.CanDrag(e)) { _s = null; DockService.Log($"{Id}: move drag on {name} refused ({(e == null ? "not registered" : !DockService.IsShownPublic(e) ? "not shown" : "not editable now (no editor open, not LiveEdit)")})"); return; }
            _s = DockService.BeginDrag(e, UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt));
            DockService.Log($"{Id}: move drag start on {name} ({_s.Moving.Count} moving{(e.Group != null ? ", group " + e.Group : "")})");
        }
        public void OnDrag(PointerEventData ev) { if (_s != null) DockService.DragBy(_s, new Vector2(ev.delta.x, -ev.delta.y)); }
        public void OnEndDrag(PointerEventData ev) { if (_s != null) DockService.EndDrag(_s); _s = null; }
    }

    /// <summary>
    /// The dock's corner grips are Core's standard window grip,
    /// FiresCore.UI.Shell.ShellWindowDrag (the hub's resize path) in Mode.Scale, wired here to the dock element named by its Key. The
    /// former DockGripHandle (a third grip class) is gone.
    /// </summary>
    internal static class DockGrip
    {
        internal static FiresCore.UI.Shell.ShellWindowDrag Wire(GameObject go, string id, float sign, Graphic tint = null, float signY = 1f)
        {
            var w = go.GetComponent<FiresCore.UI.Shell.ShellWindowDrag>() ?? go.AddComponent<FiresCore.UI.Shell.ShellWindowDrag>();
            w.DragMode = FiresCore.UI.Shell.ShellWindowDrag.Mode.Scale; w.Key = id; w.Sign = sign; w.SignY = signY;
            if (tint != null) { w.Tint = tint; w.Rest = FiresCore.UI.Shell.Kit.FiresTokens.Edge; w.Hover = FiresCore.UI.Shell.Kit.FiresTokens.GoldBright; }
            bool on = false; float startScale = 0f;
            w.Pressed = () => DockService.Log($"{w.Key}: grip {go.name} pressed");
            w.Began = () =>
            {
                var e = DockService.Find(w.Key);
                on = DockService.CanDrag(e); startScale = e != null ? e.User : 0f;
                DockService.Log($"{w.Key}: grip {go.name} drag start (scale {startScale:0.00}{(on ? "" : ", NOT editable now: ignored")})");   // point of use
            };
            w.OnScale = d =>
            {
                if (!on) return 0f;
                var e = DockService.Find(w.Key); if (e == null || e.O.Rect == null) return 0f;
                if (e.O.SizeChanged != null)   // Size resize (the owner re-lays its content)
                {
                    var rt = e.O.Rect; var tl = DockService.TopLeft(rt);
                    var cv = rt.GetComponentInParent<Canvas>(); float k = cv != null && cv.scaleFactor > 0.0001f ? cv.scaleFactor : 1f;
                    float sc = Mathf.Max(0.0001f, rt.lossyScale.x / (cv != null ? cv.transform.lossyScale.x : 1f));
                    Vector2 want = rt.rect.size + new Vector2(d.x, -d.y) / (k * sc);
                    want = Vector2.Max(want, e.O.MinSize);
                    rt.sizeDelta += want - rt.rect.size;
                    DockService.Place(rt, rt.localScale.x, tl, rt.localScale.x > 0.0001f ? rt.localScale.y / rt.localScale.x : 1f);   // the top-left stays where it was
                    try { e.O.SizeChanged(want); } catch (Exception ex) { DockService.Log($"{w.Key}: SizeChanged threw {ex.Message}"); }
                    return 0f;
                }
                // A real resize like the hub window, reusing ShellWindowDrag Mode.Resize's axis math (x = width, -y = height): independent axes on a scale-only element, as a non-uniform localScale (x and y apart). Diagonal = both,
                // straight across = width only, straight down = height only.
                var sr = DockService.ScreenRect(e.O.Rect);
                float fx = 1f + d.x / Mathf.Max(20f, sr.width), fy = 1f - d.y / Mathf.Max(20f, sr.height);   // outward = bigger (signs applied)
                // No visibly stretched text / icons. A scale-only element keeps a UNIFORM scale (the axis dragged further
                // wins, so a straight vertical drag scales too) until its owner registers SizeChanged; ScaleBy(e, fx, fy) stays for later
                if (e.O.ScalePerAxis)
                {
                    // RPGmaker 0.0.582's OnSideDrag rule: mostly sideways = width only, mostly up/down = height only, diagonal = both
                    if (Mathf.Abs(d.x) > 2f * Mathf.Abs(d.y)) fy = 1f; else if (Mathf.Abs(d.y) > 2f * Mathf.Abs(d.x)) fx = 1f;
                    if (fx > 0.5f && fx < 2f && fy > 0.5f && fy < 2f) DockService.ScaleBy(e, fx, fy);
                    return 0f;
                }
                float f = Mathf.Abs(fx - 1f) >= Mathf.Abs(fy - 1f) ? fx : fy;
                // A LIVE panel's handle scales only its own element, docked
                // or not (its dock neighbours follow by the dock rule); editor corner grips keep scaling the whole dock group as before
                if (f > 0.5f && f < 2f) { if (e.O.LiveEdit) DockService.ScaleOne(e, f, f); else DockService.ScaleBy(e, f); }
                return 0f;
            };
            w.Ended = () =>
            {
                var e = DockService.Find(w.Key);
                if (on && e != null && e.O.Rect != null && e.O.SizeChanged != null) { DockService.Log($"{w.Key}: grip {go.name} size applied -> {e.O.Rect.rect.width:0}x{e.O.Rect.rect.height:0}"); DockService.EndScale(e); }
                else if (on && e != null && e.O.Rect != null && e.O.ScalePerAxis) { DockService.Log($"{w.Key} scale X {e.O.Rect.localScale.x:0.00} / Y {e.O.Rect.localScale.y:0.00} (grip {go.name}, per-axis)"); DockService.EndScale(e); }   // Proof line
                else if (on && e != null && e.O.Rect != null) { DockService.Log($"{w.Key}: grip {go.name} scale applied {startScale:0.00} -> {e.User:0.00} (localScale {e.O.Rect.localScale.x:0.00})"); DockService.EndScale(e); }
                on = false;
            };
            w.DoubleClicked = () => { var e = DockService.Find(w.Key); if (DockService.CanDrag(e)) DockService.ResetOne(e); };
            return w;
        }
    }}
