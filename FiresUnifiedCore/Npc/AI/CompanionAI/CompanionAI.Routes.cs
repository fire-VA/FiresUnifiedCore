using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    // 0.2.919 ([ghost], Fire 10-27): route memory inside RequestPathfindingMovement, so every chore walk learns / reuses routes (RouteMemory).
    public partial class CompanionAI
    {
        private const float RouteSampleStep = 2f, RouteCornerReach = 1.2f, RouteStuckSeconds = 4f, RouteStuckMove = 0.5f;
        private Vector3 _rtGoal = new Vector3(float.NaN, 0f, 0f);
        private readonly List<Vector3> _rtWalked = new List<Vector3>();
        private float _rtTripStart;
        private RouteMemory.Route _rtRoute;
        private Vector3[] _rtPoints;
        private int _rtIdx;
        private Vector3 _rtProgPos;
        private float _rtProgAt;

        /// <summary>True while this companion walks a stored route's corners (no path search).</summary>
        internal bool OnStoredRoute => _rtRoute != null;

        // the movement step of RequestPathfindingMovement: a stored route's corners when there is one, else vanilla MoveTo
        private bool RouteMove(Vector3 destination, float reachDistance, bool run)
        {
            if (!RouteMemory.On) return MoveTo(Time.deltaTime, destination, reachDistance, run);
            RouteMemory.TickSummary();
            Vector3 pos = transform.position;
            if ((destination - _rtGoal).sqrMagnitude > 1f) BeginRouteTrip(destination);
            if (_rtWalked.Count == 0 || Vector3.Distance(_rtWalked[_rtWalked.Count - 1], pos) >= RouteSampleStep) { if (_rtWalked.Count < 256) _rtWalked.Add(pos); }

            if (_rtRoute != null)
            {
                while (_rtIdx < _rtPoints.Length && Utils.DistanceXZ(pos, _rtPoints[_rtIdx]) < RouteCornerReach && Mathf.Abs(pos.y - _rtPoints[_rtIdx].y) < 1.8f) _rtIdx++;
                if (_rtIdx >= _rtPoints.Length || Vector3.Distance(pos, destination) <= reachDistance + 1f)
                {
                    RouteMemory.Succeeded(_rtRoute);
                    _rtRoute = null;   // the last stretch to the goal itself: vanilla MoveTo
                }
                else
                {
                    if ((pos - _rtProgPos).sqrMagnitude > RouteStuckMove * RouteStuckMove) { _rtProgPos = pos; _rtProgAt = Time.time; }
                    else if (Time.time - _rtProgAt > RouteStuckSeconds)
                    {
                        RouteMemory.DropStuckRoute(_rtRoute, m_character?.m_name, pos, _rtIdx);
                        _rtRoute = null;
                        return MoveTo(Time.deltaTime, destination, reachDistance, run);
                    }
                    Vector3 dir = _rtPoints[_rtIdx] - pos; dir.y = 0f;
                    if (dir.sqrMagnitude > 1e-4f) MoveTowards(dir.normalized, run);
                    return false;
                }
            }
            return MoveTo(Time.deltaTime, destination, reachDistance, run);
        }

        private void BeginRouteTrip(Vector3 destination)
        {
            _rtGoal = destination;
            _rtWalked.Clear();
            _rtTripStart = Time.time;
            _rtRoute = null;
            if (RouteMemory.Find(transform, destination, out var r, out var pts, out int join))
            {
                _rtRoute = r; _rtPoints = pts; _rtIdx = join;
                _rtProgPos = transform.position; _rtProgAt = Time.time;
                RouteMemory.Hits++;
                FiresCore.Logging.FiresLogger.LogDebug($"[Route] {m_character?.m_name} -> {RouteMemory.At(destination)}: reused route ({pts.Length} corners, joined at {join}, {Vector3.Distance(transform.position, pts[join]):0.0} m away; learned by {r.Learner})");
            }
            else RouteMemory.Misses++;
        }

        // RequestPathfindingMovement arrived (not short): the walked corners become / refresh the route to this goal
        private void RouteArrived(Vector3 destination)
        {
            if (!RouteMemory.On || (destination - _rtGoal).sqrMagnitude > 1f) return;
            _rtWalked.Add(transform.position);
            RouteMemory.Learn(m_character?.m_name, destination, _rtWalked, Time.time - _rtTripStart);
            _rtGoal = new Vector3(float.NaN, 0f, 0f);
            _rtWalked.Clear();
            _rtRoute = null;
        }
    }
}
