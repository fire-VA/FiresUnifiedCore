using UnityEngine;

namespace FiresCore.UI.ContextMenu
{
    using Input = UnityEngine.Input;   // FiresCore.Input (a namespace) otherwise shadows UnityEngine.Input here

    /// <summary>
    /// Drives the context-menu system. HOLD ALT+SHIFT (chosen over bare Alt to avoid colliding with other
    /// mods' Alt binds; Alt+Shift+MMB stays the companion stay/follow quick-command) frees the cursor, pins the
    /// camera, and blocks character input via <see cref="FiresCore.UI.InputBlock"/> — exactly like opening the
    /// inventory. While held, RIGHT-CLICK raycasts from the cursor and opens a parchment menu from whatever
    /// provider claims the target. The menu stays interactive after Alt+Shift is released; releasing it with no
    /// menu open exits the mode. Cursor/crosshair/camera follow Tools/FIRES_CLICKABLE_UI_RECIPE.md: InputBlock
    /// (camera pin + input block), ModUiRegistry (real OS cursor), and crosshair-only suppression (the HUD stays up).
    /// </summary>
    public sealed class FiresContextMenuDriver : MonoBehaviour
    {
        private static FiresContextMenuDriver _instance;

        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresContextMenuDriver") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FiresContextMenuDriver>();
            // Registering makes GUIManager.IsCustomPanelOpen() true while engaged, so the game frees the OS
            // cursor for us (Tools/FIRES_CLICKABLE_UI_RECIPE.md) — no custom-drawn pointer needed.
            FiresCore.Bridge.ModUiRegistry.Register("FiresContextMenu",
                () => _instance != null && _instance._engaged,
                () => _instance?.Disengage());
        }

        private bool _engaged;        // we currently hold the input gate
        private bool _iEngagedBlock;  // we (not another modal) turned InputBlock on, so we own turning it off
        // Crosshair raycast suppression, per Tools/FIRES_CLICKABLE_UI_RECIPE.md item 5 and NpcBookController.
        // NEVER touch Hud.m_rootObject — deactivating the HUD root fights vanilla Hud.Update (which re-asserts
        // it via localPosition every frame) and strobes the whole HUD (minimap/hotbar/health). We leave the HUD
        // AND the crosshair "+" visible; we only clear the crosshair's raycastTarget so it can't eat center clicks.
        // (SetActive-hiding the "+" fights vanilla Hud.UpdateCrosshair every frame and flickers it — so we don't.)
        private bool _crosshairSuppressed;
        private readonly System.Collections.Generic.List<UnityEngine.UI.Graphic> _crosshairRaycasts = new System.Collections.Generic.List<UnityEngine.UI.Graphic>();

        // DEBUG: dumps the arm-gate + cursor state (only when it CHANGES) while Alt+Shift is held, to catch a
        // per-frame flap in the gate. Turned off once the gate is confirmed stable.
        internal static bool CtxDebug = true;
        private string _lastDbgSig;

        // Hit ANYTHING with a collider — the same broad mask the dungeon mod's own door raycast uses
        // (Physics.DefaultRaycastLayers = everything except IgnoreRaycast). The old narrow named-layer mask
        // silently dropped colliders on other layers (e.g. dungeon-door meshes), so aiming dead-on an object
        // could still hit nothing and open no menu — the "takes many clicks" bug. ResolveRoot + the providers/
        // claims decide what's meaningful; the mask must not pre-filter and discard otherwise-valid targets.
        private static int RayMask => Physics.DefaultRaycastLayers;

        private void Update()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (CtxDebug) LogGateState();
            if (Hud.instance == null || Player.m_localPlayer == null) { Disengage(); return; }
            if (!ContextMenuConfig.Enabled) { Disengage(); return; } // whole feature toggled off in config
            if (FiresContextMenu.SuppressDriver)
            {
                // Hand the gate to the sub-mode (e.g. node grab): drop our engaged state but DON'T release
                // InputBlock — the sub-mode owns it now and releases it on exit. Avoids a camera-pin flicker.
                if (_engaged) { _engaged = false; _iEngagedBlock = false; HideGameUi(false); FiresContextMenu.Close(); }
                return;
            }

            bool menuOpen = FiresContextMenu.IsOpen;

            // Arm-gate with a LATCH. Decide "enter context mode" ONLY on the rising edge of the modifier combo —
            // when gameplay is genuinely clean — then hold that state while the keys stay down. Re-running the
            // VanillaModalUp() check every frame WHILE engaged caused a 1-frame feedback oscillation: once we
            // engage, InputBlock's cursor-free/blocked state makes vanilla Menu.IsVisible() (m_hiddenFrames <= 2,
            // a 2-frame-latched flag) read true the NEXT frame; VanillaModalUp() then saw "a vanilla modal is up"
            // and disengaged us; disengaging cleared it, so we re-engaged the frame after — flip-flopping every
            // frame (the cursor strobe + camera drift the log caught). The modifier keys and the whole feature are
            // configurable (ContextMenuConfig); default combo is Alt+Shift.
            bool mods = ContextMenuConfig.ModifiersHeld();
            bool wantEngaged;
            if (menuOpen) wantEngaged = true;             // a menu is open — stay engaged until it closes
            else if (_engaged) wantEngaged = mods;        // LATCH: already armed → hold while the combo is held
            else wantEngaged = mods && !VanillaModalUp(); // ARM: only on a clean frame (no real vanilla modal up)
            if (wantEngaged && !_engaged) Engage();
            else if (!wantEngaged && _engaged) { Disengage(); return; }
            if (!_engaged) return;

            // The cursor is held free by ONE keeper (InputBlock's GameCamera.UpdateMouseCapture prefix) while
            // engaged. The driver must NOT also write the cursor — competing per-frame writers strobed it.
            Vector2 mouse = Input.mousePosition;

            if (menuOpen)
            {
                FiresContextMenu.View?.Tick(mouse);
                if (Input.GetKeyDown(KeyCode.Escape)) { FiresContextMenu.Close(); return; }
                if (Input.GetMouseButtonDown(0)) { FiresContextMenu.View?.HandleLeftClick(mouse); return; }
                if (Input.GetMouseButtonDown(1)) { TryOpen(mouse); return; } // right-click again → menu for the new target
                return;
            }

            // Alt held, cursor free, no menu yet — right-click opens a menu for whatever the cursor points at.
            if (Input.GetMouseButtonDown(1)) TryOpen(mouse);
        }

        // Forgiving pick. The old version cast ONE ray from the cursor and opened the closest hit; a near-miss
        // grazed the ground, which most providers don't claim, so nothing popped — you had to be pixel-perfect
        // (the "4-10 clicks" problem). Now we sample a small screen-space cluster around the cursor, gather every
        // distinct hit (interactable objects — monsters/chests/wayshrines/pieces — first and closest-first, then
        // bare ground), and open the menu for the FIRST candidate a provider actually claims.
        private void TryOpen(Vector2 mouse)
        {
            // GameCamera's own camera is the reliable gameplay camera; Camera.main can transiently resolve to a
            // different tagged camera. Fall back to Camera.main only if GameCamera isn't up yet.
            var cam = GameCamera.instance != null ? GameCamera.instance.GetComponentInChildren<Camera>() : null;
            if (cam == null) cam = Camera.main;
            if (cam == null) { FiresContextMenu.Close(); return; }

            var candidates = GatherCandidates(cam, mouse);

            for (int i = 0; i < candidates.Count; i++)
                if (FiresContextMenu.TryOpen(candidates[i], mouse)) return;

            FiresContextMenu.Close();
        }

        private const float PickPixelRadius = 14f;   // screen-space tolerance ring so near-misses still pick the object
        private const float PickMaxDistance = 80f;

        private static readonly Vector2[] PickOffsets =
        {
            Vector2.zero,
            new Vector2(0f, PickPixelRadius),          new Vector2(0f, -PickPixelRadius),
            new Vector2(PickPixelRadius, 0f),          new Vector2(-PickPixelRadius, 0f),
            new Vector2(PickPixelRadius, PickPixelRadius),   new Vector2(-PickPixelRadius, PickPixelRadius),
            new Vector2(PickPixelRadius, -PickPixelRadius),  new Vector2(-PickPixelRadius, -PickPixelRadius),
        };

        private static System.Collections.Generic.List<ContextTarget> GatherCandidates(Camera cam, Vector2 mouse)
        {
            var interactables = new System.Collections.Generic.List<ContextTarget>();
            var others = new System.Collections.Generic.List<ContextTarget>();
            var seen = new System.Collections.Generic.HashSet<GameObject>();
            int mask = RayMask;

            for (int o = 0; o < PickOffsets.Length; o++)
            {
                Vector2 off = PickOffsets[o];
                Ray ray = cam.ScreenPointToRay(new Vector3(mouse.x + off.x, mouse.y + off.y, 0f));

                var hits = Physics.RaycastAll(ray, PickMaxDistance, mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; i++)
                {
                    var root = ResolveRoot(hits[i].collider.gameObject);
                    if (root == null || !seen.Add(root)) continue;
                    var t = new ContextTarget
                    {
                        GameObject = root,
                        Point = hits[i].point,
                        Normal = hits[i].normal,
                        Distance = hits[i].distance,
                        Collider = hits[i].collider,
                    };
                    if (root.GetComponent<Character>() != null || root.GetComponent<ZNetView>() != null)
                        interactables.Add(t);
                    else
                        others.Add(t);
                }

                // Collider-less markers (patrol node spheres etc.) — only test the exact-cursor ray.
                if (off == Vector2.zero)
                    foreach (var tester in FiresContextMenu.MarkerSources)
                        if (tester(ray, out var mt) && mt != null) interactables.Add(mt);
            }

            // Nothing within pick range (e.g. flying 400m up inside a giant environment box: its walls are trigger
            // colliders the ray ignores and the terrain is far below) → claims never ran, so volume-based claims
            // (env box "configure the box containing the clicked point") could never fire. Guarantee ONE background
            // target: a long ray for a real hit point, else a point along the view. Claims run against it; if none
            // takes it and no provider adds rows, TryOpen just returns false — same as before.
            if (interactables.Count == 0 && others.Count == 0)
            {
                Ray ray = cam.ScreenPointToRay(new Vector3(mouse.x, mouse.y, 0f));
                if (Physics.Raycast(ray, out var far, 4000f, mask, QueryTriggerInteraction.Ignore))
                    others.Add(new ContextTarget
                    {
                        GameObject = ResolveRoot(far.collider.gameObject),
                        Point = far.point, Normal = far.normal, Distance = far.distance, Collider = far.collider,
                    });
                else
                    others.Add(new ContextTarget { GameObject = null, Point = ray.GetPoint(150f), Normal = Vector3.up, Distance = 150f });
            }

            interactables.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            others.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            interactables.AddRange(others);   // objects first, ground last
            return interactables;
        }

        private static GameObject ResolveRoot(GameObject go)
        {
            if (go == null) return null;
            var ch = go.GetComponentInParent<Character>();
            if (ch != null) return ch.gameObject;
            var nv = go.GetComponentInParent<ZNetView>();
            if (nv != null) return nv.gameObject;
            return go;
        }

        private void Engage()
        {
            _engaged = true;
            _iEngagedBlock = !InputBlock.IsBlocked;
            if (_iEngagedBlock) InputBlock.Block(true);   // frees the cursor ONCE on this edge; InputBlock's
                                                          // UpdateMouseCapture prefix keeps it free every frame after.
            HideGameUi(true);                             // crosshair raycast suppression only — does NOT touch the cursor
            Debug.Log($"[FiresContextMenu] engaged (blocked={InputBlock.IsBlocked}, weOwnBlock={_iEngagedBlock}).");
        }

        private void Disengage()
        {
            if (!_engaged) return;
            _engaged = false;
            FiresContextMenu.Close();
            if (_iEngagedBlock) InputBlock.Block(false);
            _iEngagedBlock = false;
            HideGameUi(false);
            Debug.Log("[FiresContextMenu] disengaged.");
        }

        // DEBUG: on-change dump of the arm gate + cursor state while (trying to be) engaged. Reveals whether the
        // gate flaps per-frame — and WHICH condition toggles (mods / a VanillaModalUp sub-flag / suppress) — or
        // whether the gate is stable and the cursor is being written by an external strobe.
        private void LogGateState()
        {
            // Dev-only input-gate dump; off unless [General] VerboseLogging is on.
            if (FiresCore.Config.ConfigManager.Instance?.configVerboseLogging?.Value != true) return;
            bool alt   = Input.GetKey(KeyCode.LeftAlt)   || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool mods  = ContextMenuConfig.ModifiersHeld();
            if (!(mods || _engaged || InputBlock.IsBlocked)) return;   // only around engage — no combat spam

            bool inv  = InventoryGui.IsVisible();
            bool menu = Menu.IsVisible();
            bool chat = Chat.instance != null && Chat.instance.HasFocus();
            bool map  = Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large;
            string sig = $"alt={alt} shift={shift} mods={mods} | inv={inv} menu={menu} chat={chat} map={map} " +
                         $"| suppress={FiresContextMenu.SuppressDriver} menuOpen={FiresContextMenu.IsOpen} " +
                         $"engaged={_engaged} blocked={InputBlock.IsBlocked} | curVis={Cursor.visible} curLock={Cursor.lockState}";
            if (sig == _lastDbgSig) return;
            _lastDbgSig = sig;
            Debug.LogWarning($"[CtxMenu][dbg] f{Time.frameCount} {sig}");
        }

        // Don't hijack Alt while a vanilla UI already owns the cursor.
        private static bool VanillaModalUp()
        {
            if (InventoryGui.IsVisible()) return true;
            if (Menu.IsVisible()) return true;
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) return true;
            return false;
        }

        // ──────────────────────────────────────────────────────────────────────────────────────────────
        //  Cursor — single-authority pattern, copied from FiresLeaderboardInputPatches and the FIXED
        //  CompanionRadialMenu. Both document that the OLD multi-writer approach (an InputBlock postfix cursor
        //  re-assert + per-frame OnGUI/coroutine/Update forces) caused the on/off STROBE, and that collapsing to
        //  ONE keeper fixed it. The one keeper is InputBlock's GameCamera.UpdateMouseCapture prefix, which forces
        //  the cursor free every frame while InputBlock.IsBlocked; InputBlock.Block(true) sets it free once on the
        //  engage edge and PlayerController.TakeInput=>false freezes the camera look. The driver NEVER writes the
        //  cursor. HideGameUi only clears the crosshair's raycastTarget. Menu hit-tests off Input.mousePosition.
        // ──────────────────────────────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            // Do NOT write the cursor here — InputBlock's UpdateMouseCapture prefix is the single keeper. We only
            // re-assert the crosshair's suppressed raycast (the Hud re-enables it each frame).
            if (!_engaged) return;
            try { FiresCore.UI.CrosshairRaycast.Reassert(_crosshairRaycasts); } catch { }
        }

        // Recipe item 5 / NpcBookController: leave the HUD (and the crosshair "+") up; only clear the crosshair's
        // raycastTarget so it can't eat center clicks. One-shot Suppress/Restore here; the per-frame Reassert
        // lives in OnGUI (the Hud re-enables the raycast each frame). We deliberately do NOT SetActive-hide the
        // "+" — that fights vanilla Hud.UpdateCrosshair and flickers.
        private void HideGameUi(bool hide)
        {
            try
            {
                if (hide == _crosshairSuppressed) return;
                _crosshairSuppressed = hide;
                if (hide) FiresCore.UI.CrosshairRaycast.Suppress(_crosshairRaycasts);
                else      FiresCore.UI.CrosshairRaycast.Restore(_crosshairRaycasts);
            }
            catch { }
        }
    }
}
