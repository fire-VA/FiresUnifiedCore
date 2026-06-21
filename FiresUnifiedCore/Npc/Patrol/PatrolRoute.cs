using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// An ordered patrol polyline (world XYZ points). Loop is auto-detected when the first and last
    /// points are within <see cref="LoopCloseThreshold"/> — a loop is walked continuously in one
    /// direction; an open route is walked there-and-back (to the end, wait, reverse, wait, repeat).
    /// </summary>
    public class PatrolRoute
    {
        public string Name;
        public readonly List<Vector3> Points = new List<Vector3>();
        public bool IsLoop;
        public const float LoopCloseThreshold = 4f;

        public void DetectLoop()
        {
            IsLoop = Points.Count >= 3 &&
                     Vector3.Distance(Points[0], Points[Points.Count - 1]) <= LoopCloseThreshold;
        }
    }
}
