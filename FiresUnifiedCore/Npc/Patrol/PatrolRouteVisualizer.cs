using System.Collections.Generic;
using UnityEngine;
using FiresCore.UI.ContextMenu;

namespace FiresCore.Npc.Patrol
{
    /// <summary>Identifies a route node under the cursor; carried on a ContextTarget so a provider can edit it.</summary>
    public sealed class PatrolNodeMarker
    {
        public string RouteName;
        public int Index;
    }

    /// <summary>
    /// Draws a saved patrol route in the world so an admin can see where it runs (and judge if it needs
    /// remaking): a golden polyline through the markers plus a sphere at each node — green start, red end on
    /// an open route, gold mid/loop. Toggled from the patrol popup; the drawing persists (DontDestroyOnLoad)
    /// so you can walk the route to inspect it, and auto-clears on logout. Reuses the recorder's LineRenderer
    /// recipe (PatrolRecorder.cs). Client-only — no-ops headless.
    /// </summary>
    public static class PatrolRouteVisualizer
    {
        private static GameObject _go;
        private static string _routeName;
        private static VizDriver _driver;
        private static string _previewPresetName;   // when set, nodes are colored by that preset's resulting speed

        public static bool IsShowing(string routeName) => _go != null && _routeName == routeName;

        public static string ActiveRouteName => _routeName;
        public static int NodeCount => _driver != null ? _driver.Count : 0;

        /// <summary>
        /// Color the visualized nodes by the speed they'd run at under <paramref name="presetName"/> (green→red)
        /// and highlight stop points. Pass null to restore the plain start/end/mid coloring. Persists across
        /// redraws so tuning a preset live keeps the preview; re-apply after an edit via <see cref="RefreshSpeedPreview"/>.
        /// </summary>
        public static void SetSpeedPreview(string presetName)
        {
            _previewPresetName = presetName;
            RefreshSpeedPreview();
        }

        /// <summary>Re-applies the active speed preview (call after a preset edit). No-op if no preview or nothing showing.</summary>
        public static void RefreshSpeedPreview()
        {
            if (_driver == null) return;
            if (string.IsNullOrEmpty(_previewPresetName)) { _driver.ClearSpeedColors(); return; }
            var route = PatrolRouteManager.GetRoute(_routeName);
            _driver.ApplySpeedColors(route?.GetPreset(_previewPresetName));
        }

        public static bool IsPreviewingSpeed => !string.IsNullOrEmpty(_previewPresetName);

        /// <summary>Live-color the nodes from a preset object WITHOUT saving — for slider-drag preview in the editor.</summary>
        public static void PreviewSpeedPreset(SpeedPreset preset)
        {
            if (_driver == null) return;
            if (preset == null) _driver.ClearSpeedColors();
            else _driver.ApplySpeedColors(preset);
        }

        /// <summary>Current (possibly preview-moved) world position of a node sphere.</summary>
        public static bool TryGetNodeWorld(int index, out Vector3 world)
        {
            world = Vector3.zero;
            if (_driver == null || index < 0 || index >= _driver.Count) return false;
            world = _driver.GetNodeWorld(index);
            return true;
        }

        /// <summary>Moves a node's sphere + line segment live, WITHOUT saving — for the grab/edit preview.</summary>
        public static void PreviewNode(int index, Vector3 world)
        {
            if (_driver != null) _driver.MoveNode(index, world);
        }

        /// <summary>Live-set the curve smoothing (0..1) and redraw — for the smoothing slider's drag preview.</summary>
        public static void RefreshCurve(float smoothing)
        {
            if (_driver != null) _driver.SetSmoothing(smoothing);
        }

        public static void Toggle(string routeName)
        {
            if (IsShowing(routeName)) Hide();
            else Show(routeName);
        }

        public static void Show(string routeName)
        {
            Hide();
            if (string.IsNullOrEmpty(routeName)) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // headless
            var route = PatrolRouteManager.GetRoute(routeName);
            if (route == null || route.Points.Count < 2) return;

            _routeName = routeName;
            _go = new GameObject("FiresPatrolRouteViz");
            Object.DontDestroyOnLoad(_go);
            _driver = _go.AddComponent<VizDriver>();
            _driver.Build(route, routeName);
            RefreshSpeedPreview();   // re-apply the color-by-speed overlay if one was active
        }

        public static void Hide()
        {
            if (_go != null) Object.Destroy(_go);
            _go = null;
            _driver = null;
            _routeName = null;
        }

        private class VizDriver : MonoBehaviour
        {
            private static readonly Color LineColor = new Color(1f, 0.85f, 0.2f, 0.9f);
            private static readonly Color StartColor = new Color(0.2f, 1f, 0.2f, 1f);
            private static readonly Color EndColor = new Color(1f, 0.25f, 0.2f, 1f);
            private static readonly Color NodeColor = new Color(1f, 0.75f, 0.1f, 1f);
            private const float NodePickRadius = 0.5f;   // generous vs the 0.25 visual sphere — easy cursor pick

            private static readonly Color StopMarkColor = new Color(0.9f, 0.95f, 1f, 1f); // stop points stand out (white-blue)
            private static readonly Color MustHitColor = new Color(0.3f, 0.8f, 1f, 1f);  // must-hit nodes: locked cyan
            private const float NodeScale = 0.5f;
            private const float StopScale = 0.8f;
            private const float MustHitScale = 0.75f;
            private const int CurveSubdiv = 12;   // samples per segment when smoothing is active

            private string _route;
            private readonly List<Vector3> _nodeWorld = new List<Vector3>();
            private readonly List<Transform> _spheres = new List<Transform>();
            private readonly List<Color> _baseColors = new List<Color>(); // start/end/mid, to restore after a speed preview
            private LineRenderer _line;
            private bool _loop;
            private float _smoothing;
            private FiresContextMenu.MarkerHitTester _tester;

            public void SetSmoothing(float s) { _smoothing = Mathf.Clamp01(s); RebuildLine(); }

            // Re-sample the LineRenderer from the current node positions + smoothing. Called on build, on a
            // smoothing change, and on every node drag — so dragging a checkpoint re-curves the segments in
            // front of and behind it (the Catmull-Rom tangents depend on the neighbours).
            private void RebuildLine()
            {
                if (_line == null || _nodeWorld.Count < 2) return;
                var pts = PatrolSpline.Densify(_nodeWorld, _loop, _smoothing, CurveSubdiv);
                _line.positionCount = pts.Count;
                for (int i = 0; i < pts.Count; i++) _line.SetPosition(i, pts[i]);
            }

            public int Count => _nodeWorld.Count;
            public Vector3 GetNodeWorld(int i) => (i >= 0 && i < _nodeWorld.Count) ? _nodeWorld[i] : Vector3.zero;

            // Recolor each node by the speed it would run at under this preset (green slow → red fast), and
            // enlarge + whiten flagged stop points. Arc-length is computed from the authoritative route Points
            // (same math as PatrolBehavior.ComputeSpeed) so the preview matches what the NPC will actually do.
            public void ApplySpeedColors(SpeedPreset preset)
            {
                var route = PatrolRouteManager.GetRoute(_route);
                var pts = route?.Points;
                if (pts == null || pts.Count == 0) return;

                var cum = new float[pts.Count];
                for (int i = 1; i < pts.Count; i++) cum[i] = cum[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);

                int n = Mathf.Min(pts.Count, _spheres.Count);
                for (int i = 0; i < n; i++)
                {
                    float mul = 1f;
                    var sec = preset?.SectionAt(i);
                    if (sec != null)
                    {
                        int lo = Mathf.Clamp(sec.Low, 0, pts.Count - 1);
                        int hi = Mathf.Clamp(sec.High, 0, pts.Count - 1);
                        float len = cum[hi] - cum[lo];
                        float u = len > 1e-3f ? (cum[Mathf.Clamp(i, lo, hi)] - cum[lo]) / len : 0f;
                        mul = sec.Sample(u);
                    }
                    bool isStop = preset?.StopAt(i) != null;
                    bool mustHit = route.MustHit.Contains(i);
                    Color c = isStop ? StopMarkColor : (mustHit ? MustHitColor : SpeedColor(mul));
                    float sc = isStop ? StopScale : (mustHit ? MustHitScale : NodeScale);
                    SetNode(i, c, sc);
                }
            }

            public void ClearSpeedColors()
            {
                int n = Mathf.Min(_baseColors.Count, _spheres.Count);
                for (int i = 0; i < n; i++) SetNode(i, _baseColors[i], NodeScale);
            }

            private void SetNode(int i, Color color, float scale)
            {
                if (i < 0 || i >= _spheres.Count || _spheres[i] == null) return;
                var mr = _spheres[i].GetComponent<MeshRenderer>();
                if (mr != null && mr.sharedMaterial != null) mr.sharedMaterial.color = color;
                _spheres[i].localScale = Vector3.one * scale;
            }

            // Slow (≈0.4×) green → base (≈1×) yellow → fast (≈1.6×+) red.
            private static Color SpeedColor(float mul)
            {
                float t = Mathf.InverseLerp(0.4f, 1.6f, mul);
                return t < 0.5f
                    ? Color.Lerp(new Color(0.25f, 0.9f, 0.3f), new Color(1f, 0.9f, 0.2f), t * 2f)
                    : Color.Lerp(new Color(1f, 0.9f, 0.2f), new Color(1f, 0.25f, 0.2f), (t - 0.5f) * 2f);
            }

            /// <summary>Live-move a node's sphere + re-curve the line (no save). A drag re-smooths the neighbouring
            /// segments too, since the Catmull-Rom tangents depend on the moved node's neighbours.</summary>
            public void MoveNode(int i, Vector3 world)
            {
                if (i < 0 || i >= _nodeWorld.Count) return;
                _nodeWorld[i] = world;
                if (i < _spheres.Count && _spheres[i] != null) _spheres[i].position = world;
                RebuildLine();
            }

            public void Build(PatrolRoute route, string routeName)
            {
                _route = routeName;
                var pts = route.Points;
                bool loop = route.IsLoop;
                _loop = loop;
                _smoothing = Mathf.Clamp01(route.Smoothing);

                var lineGo = new GameObject("Line");
                lineGo.transform.SetParent(transform, false);
                var lr = lineGo.AddComponent<LineRenderer>();
                _line = lr;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.widthMultiplier = 0.25f;
                lr.numCornerVertices = 2;
                lr.numCapVertices = 2;
                lr.useWorldSpace = true;
                lr.startColor = lr.endColor = LineColor;

                var nodeShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 world = pts[i] + Vector3.up * 0.3f;
                    _nodeWorld.Add(world);

                    var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    s.name = "Node" + i;
                    s.transform.SetParent(transform, false);
                    s.transform.position = world;
                    bool mustHit = route.MustHit.Contains(i);
                    s.transform.localScale = Vector3.one * (mustHit ? MustHitScale : NodeScale);
                    _spheres.Add(s.transform);
                    var col = s.GetComponent<Collider>(); if (col != null) Destroy(col);
                    var m = new Material(nodeShader);
                    Color baseColor = mustHit ? MustHitColor
                                    : (!loop && i == 0) ? StartColor
                                    : (!loop && i == pts.Count - 1) ? EndColor
                                    : NodeColor;
                    m.color = baseColor;
                    _baseColors.Add(baseColor);
                    s.GetComponent<MeshRenderer>().sharedMaterial = m;
                }

                RebuildLine();   // draw the (optionally smoothed) polyline through the nodes

                // Make the collider-less node spheres selectable by the context-menu cursor ray (ray-vs-sphere,
                // since Physics can't hit them). The provider in FiresNPCs turns a hit into a node-edit menu.
                _tester = HitTest;
                FiresContextMenu.RegisterMarkerSource(_tester);
            }

            // Auto-clear when the world unloads (logout / back to menu) — the markers are world-space and
            // meaningless without the loaded zone.
            private void Update()
            {
                if (ZNetScene.instance == null) PatrolRouteVisualizer.Hide();
            }

            private void OnDestroy()
            {
                if (_tester != null) FiresContextMenu.UnregisterMarkerSource(_tester);
                _tester = null;
            }

            private bool HitTest(Ray ray, out ContextTarget target)
            {
                target = null;
                float best = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < _nodeWorld.Count; i++)
                    if (RaySphere(ray, _nodeWorld[i], NodePickRadius, out float d) && d < best) { best = d; bestIdx = i; }
                if (bestIdx < 0) return false;
                target = new ContextTarget
                {
                    Point = _nodeWorld[bestIdx],
                    Distance = best,
                    Marker = new PatrolNodeMarker { RouteName = _route, Index = bestIdx },
                    MarkerKind = "PatrolNode",
                };
                return true;
            }

            // Standard ray-vs-sphere; ray.direction is unit (ScreenPointToRay) so the returned t is world distance.
            private static bool RaySphere(Ray ray, Vector3 center, float radius, out float dist)
            {
                dist = 0f;
                Vector3 oc = ray.origin - center;
                float b = Vector3.Dot(oc, ray.direction);
                float c = Vector3.Dot(oc, oc) - radius * radius;
                float disc = b * b - c;
                if (disc < 0f) return false;
                float sq = Mathf.Sqrt(disc);
                float t = -b - sq;
                if (t < 0f) t = -b + sq;   // origin inside the sphere / nearest hit behind origin
                if (t < 0f) return false;
                dist = t;
                return true;
            }
        }
    }
}
