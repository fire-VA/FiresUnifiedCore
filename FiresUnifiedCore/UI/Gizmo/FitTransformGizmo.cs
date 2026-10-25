using UnityEngine;

// reused from FDT Inspector\TransformEdit\FiresTransformGizmo.cs (copied; FiresColliderGizmo.LineWidth -> a constant). Used by the
// hair / beard / helmet fit tool (FitAttachTool) on preview bodies; the freeze helpers are kept but not used there.
namespace FiresCore.UI.Gizmo
{
    /// <summary>
    /// A Unity-style 3D transform handle for the whole OBJECT, drawn in screen space with GL immediate mode during
    /// OnGUI Repaint (projected from the live camera each frame, so the axes look perspective-correct — true 90°
    /// in world space, foreshortened on screen). Four tools, each with its own handle set:
    /// Move — 3 axis arrows (drag to translate along X/Y/Z)
    /// Rotate — 3 axis rings (drag to rotate around an axis)
    /// Scale — 3 axis boxes + a centre box (per-axis / uniform)
    /// Transform — rings + arrows + centre uniform box, all live at once
    /// Handles are constant on-screen size (scaled by camera distance). Move/Rotate sync natively via
    /// ZSyncTransform (owner claimed on grab); rotation edits call PinFacing so a live creature holds the new yaw.
    /// The target is frozen while a tool is active (see SetFrozen) so it can't drift under the handles.
    /// </summary>
    // public
    // (add-only API: Mode, LocalSpace, SnapDegrees, OnGui), + Local / World axes, Ctrl = rotation snap, Unity-style plane handles in
    // Move / Transform, and every pixel size scales with the screen height (big and clear at 3440x1440).
    public static class FitTransformGizmo
    {
        public enum Tool { None, Move, Rotate, Scale, Transform }
        public static Tool Mode = Tool.None;
        public static bool Active => Mode != Tool.None;
        /// <summary>true = the target's own axes (Unity "Local"), false = world axes.</summary>
        public static bool LocalSpace = true;
        /// <summary>rotation steps while Ctrl is held.</summary>
        public static float SnapDegrees = 15f;

        private enum Grab { None, Move, Rotate, Scale, Uniform, Plane }
        private static Grab _grabKind = Grab.None;
        private static int _grabAxis = -1;
        private static Vector2 _lastMouse;            // Y-up screen space
        private static float _rotAccum;               // unsnapped degrees not yet applied

        private static Camera _cam;
        private static Material _mat;

        private const float ScreenScale = 0.14f;      // handle world size = camera-distance * this → constant screen size
        // the pixel sizes are for 1080 lines and grow with the screen height (like the HUD)
        private static float K => Mathf.Max(1f, Screen.height / 1080f);
        private static float TipPickPx => 13f * K;
        private static float RingPickPx => 9f * K;
        private static float UniHalfPx => 10f * K;
        private static float BoxHalfPx => 7f * K;
        private static float HeadLenPx => 16f * K;
        private static float HeadWpx => 6.5f * K;
        private static float PlaneHalfPx => 9f * K;
        private const float PlaneAt = 0.3f;           // a plane square's centre: this share of the axis length along both of its axes
        private const int RingSeg = 52;

        private static readonly Color[] AxisCol =
        {
            new Color(1f, 0.35f, 0.35f), new Color(0.45f, 1f, 0.45f), new Color(0.45f, 0.65f, 1f),
        };
        private static readonly Color UniCol = new Color(1f, 0.87f, 0.35f);
        private static readonly Color PivotCol = new Color(0.85f, 0.78f, 0.55f);

        public static void Clear() { Mode = Tool.None; _grabKind = Grab.None; _grabAxis = -1; SetFrozen(null, false); }

        // ---------------------------------------------------------------- freeze (position + facing)
        // While a tool is active the target is pinned so it doesn't drift out from under the handles: rigidbody
        // kinematic (physics), AI disabled + move dir zeroed + m_lookYaw pinned (a live creature won't re-face).
        // We claim ownership so our edits win the ZSyncTransform sync. Restored on release.
        private static GameObject _frozenGo;
        private static Rigidbody _frozenRb;
        private static BaseAI _frozenAi;
        private static Character _frozenChar;
        private static bool _savedKinematic;
        private static bool _savedAiEnabled;

        public static void SetFrozen(GameObject go, bool freeze)
        {
            if (!freeze || go == null) { Unfreeze(); return; }

            if (!ReferenceEquals(go, _frozenGo))
            {
                Unfreeze();
                _frozenGo = go;
                _frozenRb = go.GetComponent<Rigidbody>();
                _frozenChar = go.GetComponent<Character>();
                _frozenAi = go.GetComponent<BaseAI>();
                if (_frozenRb != null) _savedKinematic = _frozenRb.isKinematic;
                ClaimOwner(go);
                if (_frozenAi != null) { _savedAiEnabled = _frozenAi.enabled; _frozenAi.enabled = false; }
                if (_frozenChar != null)
                {
                    _frozenChar.SetMoveDir(Vector3.zero);
                    _frozenChar.SetLookDir(_frozenChar.transform.forward, 0f);
                }
            }
            if (_frozenRb != null && !_frozenRb.isKinematic)
            {
                _frozenRb.velocity = Vector3.zero;
                _frozenRb.angularVelocity = Vector3.zero;
                _frozenRb.isKinematic = true;
            }
            if (_frozenChar != null) _frozenChar.SetMoveDir(Vector3.zero);
        }

        private static void Unfreeze()
        {
            if (_frozenAi != null) { try { _frozenAi.enabled = _savedAiEnabled; } catch { } }
            if (_frozenRb != null) { try { _frozenRb.isKinematic = _savedKinematic; } catch { } }
            _frozenRb = null; _frozenAi = null; _frozenChar = null; _frozenGo = null;
        }

        // Pin a creature's facing to its transform.forward so vanilla UpdateRotation holds an edited yaw. Call
        // after any rotation edit. No-op on non-creatures.
        public static void PinFacing(GameObject go)
        {
            if (go == null) return;
            var ch = go.GetComponent<Character>();
            if (ch != null) ch.SetLookDir(go.transform.forward, 0f);
        }

        private static void ClaimOwner(GameObject go)
        {
            try
            {
                var nv = go.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid())
                {
                    var z = nv.GetZDO();
                    if (!z.IsOwner()) z.SetOwner(ZDOMan.GetSessionID());
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- main
        public static void OnGui(Camera cam, GameObject target, bool interact)
        {
            if (!interact || cam == null || target == null || Mode == Tool.None) { _grabKind = Grab.None; _grabAxis = -1; return; }
            _cam = cam;
            var t = target.transform;
            var e = Event.current;
            Vector2 mouse = new Vector2(e.mousePosition.x, Screen.height - e.mousePosition.y);   // → Y-up

            Vector3 center = t.position;
            Vector2 c2 = Project(center, out bool cok);
            if (!cok) { _grabKind = Grab.None; return; }
            float worldLen = Vector3.Distance(cam.transform.position, center) * ScreenScale;
            if (worldLen < 0.01f) worldLen = 0.5f;

            Vector3[] wdir = new Vector3[3];
            Vector2[] tip2 = new Vector2[3];
            bool[] tvis = new bool[3];
            for (int a = 0; a < 3; a++)
            {
                wdir[a] = LocalSpace ? t.TransformDirection(Axis(a)).normalized : Axis(a);   // Local / World
                tip2[a] = Project(center + wdir[a] * worldLen, out tvis[a]);
            }
            s_lastCam = cam; s_lastCenter = center; s_lastLen = worldLen; s_lastWdir = wdir; s_lastFrame = Time.frameCount;   // for RingScreenPoint

            bool hasMove = Mode == Tool.Move || Mode == Tool.Transform;
            bool hasRot = Mode == Tool.Rotate || Mode == Tool.Transform;
            bool hasScale = Mode == Tool.Scale;
            bool hasUni = Mode == Tool.Scale || Mode == Tool.Transform;

            // ---- input (grab / drag / release)
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                _grabKind = Grab.None; _grabAxis = -1;
                if (hasUni && InBox(c2, mouse, UniHalfPx + 3f)) _grabKind = Grab.Uniform;
                if (_grabKind == Grab.None && hasMove)   // plane squares (the axis index = the plane's normal)
                    for (int a = 0; a < 3; a++)
                        if (PlaneSpot(center, wdir, worldLen, a, out Vector2 ps) && InBox(ps, mouse, PlaneHalfPx))
                        { _grabKind = Grab.Plane; _grabAxis = a; break; }
                if (_grabKind == Grab.None && (hasMove || hasScale))
                    for (int a = 0; a < 3; a++)
                        if (tvis[a] && DistPointSeg(mouse, c2, tip2[a]) < TipPickPx)
                        { _grabKind = hasMove ? Grab.Move : Grab.Scale; _grabAxis = a; break; }
                if (_grabKind == Grab.None && hasRot)
                {
                    int ra = PickRing(center, wdir, worldLen, mouse);
                    if (ra >= 0) { _grabKind = Grab.Rotate; _grabAxis = ra; }
                }
                if (_grabKind != Grab.None)
                {
                    _lastMouse = mouse; _rotAccum = 0f; _grabTotal = 0f; ClaimOwner(target); e.Use();
                    Debug.Log($"[FitTransformGizmo] {_grabKind} {AxisName(_grabAxis)} grabbed on {target.name} ({(LocalSpace ? "Local" : "World")})");
                }
            }
            else if (e.type == EventType.MouseDrag && _grabKind != Grab.None)
            {
                ApplyDrag(target, c2, tip2, wdir, worldLen, mouse - _lastMouse, mouse);
                _lastMouse = mouse; e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _grabKind != Grab.None)
            {
                var tt = target.transform;
                Debug.Log($"[FitTransformGizmo] {_grabKind} {AxisName(_grabAxis)} released on {target.name}: {(_grabKind == Grab.Rotate ? $"rotated {_grabTotal:0.0} deg, " : "")}local pos {tt.localPosition.x:0.###},{tt.localPosition.y:0.###},{tt.localPosition.z:0.###} euler {tt.localEulerAngles.x:0.#},{tt.localEulerAngles.y:0.#},{tt.localEulerAngles.z:0.#} scale {tt.localScale.x:0.###}");
                _grabKind = Grab.None; _grabAxis = -1; e.Use();
            }

            // ---- draw (Repaint only)
            if (e.type != EventType.Repaint) return;
            Mat().SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            if (hasRot)
                for (int a = 0; a < 3; a++)
                    DrawRing(center, wdir[a], a, worldLen, _grabKind == Grab.Rotate && _grabAxis == a, Mode == Tool.Transform ? 0.55f : 1f);
            if (hasMove)
                for (int a = 0; a < 3; a++)
                {
                    if (PlaneSpot(center, wdir, worldLen, a, out Vector2 ps))   // plane squares, in the colour of their normal
                    {
                        bool hotP = _grabKind == Grab.Plane && _grabAxis == a;
                        Color pc = AxisCol[a]; pc.a = hotP ? 0.85f : 0.45f;
                        FillBox(ps, pc, hotP ? PlaneHalfPx + 2f : PlaneHalfPx);
                    }
                    if (tvis[a]) DrawArrow(c2, tip2[a], AxisCol[a], _grabKind == Grab.Move && _grabAxis == a);
                }
            if (hasScale)
                for (int a = 0; a < 3; a++)
                    if (tvis[a]) DrawScaleAxis(c2, tip2[a], AxisCol[a], _grabKind == Grab.Scale && _grabAxis == a);
            if (hasUni) FillBox(c2, UniCol, _grabKind == Grab.Uniform ? UniHalfPx + 2f : UniHalfPx);
            else FillBox(c2, PivotCol, 3.5f);   // small pivot dot for move / rotate
            GL.PopMatrix();
        }

        // ---------------------------------------------------------------- drag application
        private static void ApplyDrag(GameObject go, Vector2 c2, Vector2[] tip2, Vector3[] wdir, float worldLen, Vector2 delta, Vector2 mouse)
        {
            // (FDT's FiresEditRecorder hook dropped: the fit tool saves through AttachFits)
            var t = go.transform;
            if (_grabKind == Grab.Uniform)
            {
                float f = 1f + (delta.x + delta.y) * 0.005f;                 // right / up grows
                Vector3 ns = t.localScale * f;
                t.localScale = new Vector3(Mathf.Max(0.02f, ns.x), Mathf.Max(0.02f, ns.y), Mathf.Max(0.02f, ns.z));
                return;
            }

            int a = _grabAxis;
            if (a < 0) return;

            if (_grabKind == Grab.Rotate)
            {
                Vector2 radial = (mouse - c2);
                Vector2 tangent = new Vector2(-radial.y, radial.x);
                if (tangent.sqrMagnitude > 1e-4f) tangent.Normalize();
                float amt = Vector2.Dot(delta, tangent);
                float faceSign = Vector3.Dot(wdir[a], _cam.transform.position - t.position) > 0f ? 1f : -1f;
                _rotAccum += amt * 0.5f * faceSign / K;   // the same degrees per screen share at any resolution
                float apply = _rotAccum;
                bool snap = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
                if (snap && SnapDegrees > 0f) apply = Mathf.Floor(Mathf.Abs(_rotAccum) / SnapDegrees) * SnapDegrees * Mathf.Sign(_rotAccum);
                if (Mathf.Abs(apply) > 1e-4f) { t.Rotate(wdir[a], apply, Space.World); _rotAccum -= apply; _grabTotal += apply; }
                PinFacing(go);
                return;
            }

            if (_grabKind == Grab.Plane)   // the mouse delta split onto the plane's two projected axes
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                Vector2 pb = tip2[b] - c2, pcv = tip2[c] - c2;
                float det = pb.x * pcv.y - pb.y * pcv.x;
                if (Mathf.Abs(det) < 1f) return;   // the plane is edge-on
                float x = (delta.x * pcv.y - delta.y * pcv.x) / det, y = (pb.x * delta.y - pb.y * delta.x) / det;
                t.position += (wdir[b] * x + wdir[c] * y) * worldLen;
                return;
            }

            Vector2 axisGui = tip2[a] - c2;
            float pixLen = axisGui.magnitude;
            if (pixLen < 1f) return;
            Vector2 axisDir = axisGui / pixLen;
            float alongPix = Vector2.Dot(delta, axisDir);

            if (_grabKind == Grab.Move)
            {
                float worldPerPix = worldLen / pixLen;
                t.position += wdir[a] * (alongPix * worldPerPix);
            }
            else if (_grabKind == Grab.Scale)
            {
                Vector3 s = t.localScale;
                s[a] = Mathf.Max(0.02f, s[a] + alongPix * 0.01f);
                t.localScale = s;
            }
        }

        // ---------------------------------------------------------------- projection + hit-testing
        private static Vector2 Project(Vector3 world, out bool ok)
        {
            Vector3 s = _cam.WorldToScreenPoint(world);
            ok = s.z > 0.05f;
            return new Vector2(s.x, s.y);
        }

        private static int PickRing(Vector3 center, Vector3[] wdir, float worldLen, Vector2 mouse)
        {
            int best = -1; float bestD = RingPickPx;
            for (int a = 0; a < 3; a++)
            {
                float d = RingDist(center, wdir[a], worldLen, mouse);
                if (d < bestD) { bestD = d; best = a; }
            }
            return best;
        }

        private static float RingDist(Vector3 center, Vector3 n, float worldLen, Vector2 mouse)
        {
            Basis(n, out Vector3 u, out Vector3 v);
            float md = float.MaxValue;
            Vector2 prev = default; bool havePrev = false;
            for (int i = 0; i <= RingSeg; i++)
            {
                float th = i / (float)RingSeg * Mathf.PI * 2f;
                Vector2 sp = Project(center + (u * Mathf.Cos(th) + v * Mathf.Sin(th)) * worldLen, out bool ok);
                if (havePrev && ok) md = Mathf.Min(md, DistPointSeg(mouse, prev, sp));
                prev = sp; havePrev = ok;
            }
            return md;
        }

        // ---- where the last drawn frame put the handles. Add-only.
        private static Camera s_lastCam; private static Vector3 s_lastCenter; private static float s_lastLen; private static Vector3[] s_lastWdir; private static int s_lastFrame = -1;
        private static float _grabTotal;   // degrees applied during the current rotate grab
        private static string AxisName(int a) => a == 0 ? "X" : a == 1 ? "Y" : a == 2 ? "Z" : "-";

        /// <summary>a point on axis <paramref name="axis"/>'s rotation ring (0 X, 1 Y, 2 Z) at <paramref name="angleDeg"/> around it,
        /// in GUI coordinates (px, y DOWN, the space of Event.mousePosition), from the last frame the gizmo drew (null before it drew or if
        /// that point is behind the camera). Press within ~9 px x (screen height / 1080) of it to grab the ring; a drag along the ring turns
        /// it (0.5 deg per px of tangential motion at 1080 lines, scaled so a screen share is the same angle at any height; Ctrl = 15 deg steps).</summary>
        public static Vector2? RingScreenPoint(int axis, float angleDeg)
        {
            if (s_lastCam == null || s_lastWdir == null || axis < 0 || axis > 2) return null;
            Basis(s_lastWdir[axis], out Vector3 u, out Vector3 v);
            float th = angleDeg * Mathf.Deg2Rad;
            Vector3 s = s_lastCam.WorldToScreenPoint(s_lastCenter + (u * Mathf.Cos(th) + v * Mathf.Sin(th)) * s_lastLen);
            if (s.z <= 0.05f) return null;
            return new Vector2(s.x, Screen.height - s.y);
        }

        /// <summary>the gizmo centre (the target's pivot) in GUI coordinates (y down), from the last drawn frame; null before it drew.</summary>
        public static Vector2? CenterScreenPoint()
        {
            if (s_lastCam == null) return null;
            Vector3 s = s_lastCam.WorldToScreenPoint(s_lastCenter);
            return s.z <= 0.05f ? (Vector2?)null : new Vector2(s.x, Screen.height - s.y);
        }

        /// <summary>Time.frameCount of the last frame the gizmo drew (a test waits for it to be current).</summary>
        public static int LastDrawnFrame => s_lastFrame;

        // the screen spot of the plane square whose normal is axis a (between its two other axes)
        private static bool PlaneSpot(Vector3 center, Vector3[] wdir, float worldLen, int a, out Vector2 spot)
        {
            int b = (a + 1) % 3, c = (a + 2) % 3;
            spot = Project(center + (wdir[b] + wdir[c]) * (worldLen * PlaneAt), out bool ok);
            return ok;
        }

        private static bool InBox(Vector2 c, Vector2 p, float half)
            => Mathf.Abs(p.x - c.x) <= half && Mathf.Abs(p.y - c.y) <= half;

        private static float DistPointSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Vector2.Dot(p - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude);
            t = Mathf.Clamp01(t);
            return Vector2.Distance(p, a + ab * t);
        }

        private static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
        {
            Vector3 refv = Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right;
            u = Vector3.Cross(n, refv).normalized;
            v = Vector3.Cross(n, u).normalized;
        }

        private static Vector3 Axis(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;

        // ---------------------------------------------------------------- GL drawing (screen space, Y-up)
        private static float LineW() => 2f * K;   // scales with the screen height

        private static void DrawArrow(Vector2 c, Vector2 tip, Color col, bool hot)
        {
            float w = LineW() * (hot ? 1.8f : 1f);
            Vector2 d = tip - c; float len = d.magnitude; if (len < 1f) return; d /= len;
            QuadLine(c, tip - d * (HeadLenPx * 0.7f), col, w);
            Vector2 p = new Vector2(-d.y, d.x);
            float hw = HeadWpx * (hot ? 1.4f : 1f), hl = HeadLenPx * (hot ? 1.25f : 1f);
            Vector2 b = tip - d * hl;
            Tri(tip, b + p * hw, b - p * hw, col);
        }

        private static void DrawScaleAxis(Vector2 c, Vector2 tip, Color col, bool hot)
        {
            float w = LineW() * (hot ? 1.8f : 1f);
            QuadLine(c, tip, col, w);
            FillBox(tip, col, hot ? BoxHalfPx + 2f : BoxHalfPx);
        }

        private static void DrawRing(Vector3 center, Vector3 n, int axis, float worldLen, bool hot, float alpha)
        {
            Basis(n, out Vector3 u, out Vector3 v);
            Color col = AxisCol[axis]; col.a *= alpha;
            float w = LineW() * (hot ? 1.9f : 1f);
            Vector2 prev = default; bool havePrev = false;
            for (int i = 0; i <= RingSeg; i++)
            {
                float th = i / (float)RingSeg * Mathf.PI * 2f;
                Vector2 sp = Project(center + (u * Mathf.Cos(th) + v * Mathf.Sin(th)) * worldLen, out bool ok);
                if (havePrev && ok) QuadLine(prev, sp, col, w);
                prev = sp; havePrev = ok;
            }
        }

        private static void QuadLine(Vector2 a, Vector2 b, Color col, float width)
        {
            Vector2 d = b - a; float len = d.magnitude; if (len < 0.5f) return; d /= len;
            Vector2 p = new Vector2(-d.y, d.x) * (width * 0.5f);
            GL.Begin(GL.QUADS);
            GL.Color(col);
            GL.Vertex3(a.x + p.x, a.y + p.y, 0f);
            GL.Vertex3(b.x + p.x, b.y + p.y, 0f);
            GL.Vertex3(b.x - p.x, b.y - p.y, 0f);
            GL.Vertex3(a.x - p.x, a.y - p.y, 0f);
            GL.End();
        }

        private static void Tri(Vector2 a, Vector2 b, Vector2 c, Color col)
        {
            GL.Begin(GL.TRIANGLES);
            GL.Color(col);
            GL.Vertex3(a.x, a.y, 0f); GL.Vertex3(b.x, b.y, 0f); GL.Vertex3(c.x, c.y, 0f);
            GL.End();
        }

        private static void FillBox(Vector2 c, Color col, float half)
        {
            GL.Begin(GL.QUADS);
            GL.Color(col);
            GL.Vertex3(c.x - half, c.y - half, 0f);
            GL.Vertex3(c.x - half, c.y + half, 0f);
            GL.Vertex3(c.x + half, c.y + half, 0f);
            GL.Vertex3(c.x + half, c.y - half, 0f);
            GL.End();
        }

        private static Material Mat()
        {
            if (_mat == null)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);   // draw over the world
            }
            return _mat;
        }
    }
}
