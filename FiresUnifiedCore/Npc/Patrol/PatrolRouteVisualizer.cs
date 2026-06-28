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

        public static bool IsShowing(string routeName) => _go != null && _routeName == routeName;

        public static string ActiveRouteName => _routeName;
        public static int NodeCount => _driver != null ? _driver.Count : 0;

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

            private string _route;
            private readonly List<Vector3> _nodeWorld = new List<Vector3>();
            private readonly List<Transform> _spheres = new List<Transform>();
            private LineRenderer _line;
            private bool _loop;
            private FiresContextMenu.MarkerHitTester _tester;

            public int Count => _nodeWorld.Count;
            public Vector3 GetNodeWorld(int i) => (i >= 0 && i < _nodeWorld.Count) ? _nodeWorld[i] : Vector3.zero;

            /// <summary>Live-move a node's sphere + the line segments touching it (no save).</summary>
            public void MoveNode(int i, Vector3 world)
            {
                if (i < 0 || i >= _nodeWorld.Count) return;
                _nodeWorld[i] = world;
                if (i < _spheres.Count && _spheres[i] != null) _spheres[i].position = world;
                if (_line != null)
                {
                    _line.SetPosition(i, world);
                    if (_loop && i == 0 && _line.positionCount > _nodeWorld.Count) _line.SetPosition(_nodeWorld.Count, world);
                }
            }

            public void Build(PatrolRoute route, string routeName)
            {
                _route = routeName;
                var pts = route.Points;
                bool loop = route.IsLoop;
                _loop = loop;

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
                lr.positionCount = pts.Count + (loop ? 1 : 0);
                for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, pts[i] + Vector3.up * 0.3f);
                if (loop) lr.SetPosition(pts.Count, pts[0] + Vector3.up * 0.3f);

                var nodeShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 world = pts[i] + Vector3.up * 0.3f;
                    _nodeWorld.Add(world);

                    var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    s.name = "Node" + i;
                    s.transform.SetParent(transform, false);
                    s.transform.position = world;
                    s.transform.localScale = Vector3.one * 0.5f;
                    _spheres.Add(s.transform);
                    var col = s.GetComponent<Collider>(); if (col != null) Destroy(col);
                    var m = new Material(nodeShader);
                    m.color = (!loop && i == 0) ? StartColor
                            : (!loop && i == pts.Count - 1) ? EndColor
                            : NodeColor;
                    s.GetComponent<MeshRenderer>().sharedMaterial = m;
                }

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
