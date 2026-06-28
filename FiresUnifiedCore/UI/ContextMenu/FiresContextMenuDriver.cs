using UnityEngine;

namespace FiresCore.UI.ContextMenu
{
    using Input = UnityEngine.Input;   // FiresCore.Input (a namespace) otherwise shadows UnityEngine.Input here

    /// <summary>
    /// Drives the context-menu system. HOLD ALT (no Shift — that chord is the companion quick-command) frees the
    /// cursor, pins the camera, and blocks character input via <see cref="FiresCore.UI.InputBlock"/> — exactly
    /// like opening the inventory. While held, RIGHT-CLICK raycasts from the cursor and opens a parchment menu
    /// from whatever provider claims the target. The menu stays interactive after Alt is released; releasing Alt
    /// with no menu open exits the mode. Client-only — spawned from Setup under !isBatchMode, no-ops headless.
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
        }

        private bool _engaged;        // we currently hold the input gate
        private bool _iEngagedBlock;  // we (not another modal) turned InputBlock on, so we own turning it off

        private static int RayMask
        {
            get
            {
                int m = LayerMask.GetMask("character", "Default", "piece", "terrain", "static_solid", "piece_nonsolid");
                return m == 0 ? ~0 : m;
            }
        }

        private void Update()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (Hud.instance == null || Player.m_localPlayer == null) { Disengage(); return; }
            if (FiresContextMenu.SuppressDriver)
            {
                // Hand the gate to the sub-mode (e.g. node grab): drop our engaged state but DON'T release
                // InputBlock — the sub-mode owns it now and releases it on exit. Avoids a camera-pin flicker.
                if (_engaged) { _engaged = false; _iEngagedBlock = false; FiresContextMenu.Close(); }
                return;
            }

            bool menuOpen = FiresContextMenu.IsOpen;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // Engaged while a menu is up OR while Alt is held alone and no vanilla modal owns the cursor.
            bool wantEngaged = menuOpen || (alt && !shift && !VanillaModalUp());
            if (wantEngaged && !_engaged) Engage();
            else if (!wantEngaged && _engaged) { Disengage(); return; }
            if (!_engaged) return;

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

        private void TryOpen(Vector2 mouse)
        {
            var target = Raycast(mouse);
            if (target == null) { FiresContextMenu.Close(); return; }
            FiresContextMenu.Open(target, mouse);
        }

        private static ContextTarget Raycast(Vector2 mouse)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            Ray ray = cam.ScreenPointToRay(new Vector3(mouse.x, mouse.y, 0f));

            ContextTarget best = null;
            float bestDist = float.MaxValue;

            var hits = Physics.RaycastAll(ray, 80f, RayMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].distance >= bestDist) continue;
                bestDist = hits[i].distance;
                best = new ContextTarget
                {
                    GameObject = ResolveRoot(hits[i].collider.gameObject),
                    Point = hits[i].point,
                    Normal = hits[i].normal,
                    Distance = hits[i].distance,
                    Collider = hits[i].collider,
                };
            }

            // Non-physics markers (patrol node spheres etc.) win when nearer than any physics hit, so a node in
            // front of terrain is selectable even though its collider was stripped.
            foreach (var tester in FiresContextMenu.MarkerSources)
            {
                if (tester(ray, out var mt) && mt != null && mt.Distance < bestDist)
                {
                    bestDist = mt.Distance;
                    best = mt;
                }
            }
            return best;
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
            if (_iEngagedBlock) InputBlock.Block(true);
        }

        private void Disengage()
        {
            if (!_engaged) return;
            _engaged = false;
            FiresContextMenu.Close();
            if (_iEngagedBlock) InputBlock.Block(false);
            _iEngagedBlock = false;
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
    }
}
