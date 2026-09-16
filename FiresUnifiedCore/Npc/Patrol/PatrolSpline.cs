using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Catmull-Rom smoothing over a patrol polyline. The curve passes THROUGH every node (so a must-hit
    /// doorway is still on the path) but rounds the corners between them. A <c>smoothing</c> of 0 returns the
    /// straight chord (identical to the raw polyline); 1 returns the full Catmull-Rom curve; values between
    /// blend the two. Shared by the follower (<see cref="PatrolBehavior"/>'s carrot) and the world visualizer
    /// so what the admin sees drawn is exactly what the NPC walks.
    /// </summary>
    public static class PatrolSpline
    {
        /// <summary>
        /// Point on the segment from node <paramref name="i"/> to node i+1 at fraction <paramref name="f"/>∈[0,1],
        /// blended from the straight chord toward the Catmull-Rom curve by <paramref name="smoothing"/>.
        /// </summary>
        public static Vector3 Point(IList<Vector3> pts, bool loop, int i, float f, float smoothing)
        {
            int count = pts.Count;
            if (count == 0) return Vector3.zero;
            if (count == 1) return pts[0];

            i = Mathf.Clamp(i, 0, count - 1);
            int i1 = loop ? Wrap(i + 1, count) : Mathf.Min(i + 1, count - 1);
            Vector3 p1 = pts[i], p2 = pts[i1];
            Vector3 straight = Vector3.LerpUnclamped(p1, p2, f);
            if (smoothing <= 0.0001f) return straight;

            int i0 = loop ? Wrap(i - 1, count) : Mathf.Max(i - 1, 0);
            int i2 = loop ? Wrap(i + 2, count) : Mathf.Min(i + 2, count - 1);
            Vector3 p0 = pts[i0], p3 = pts[i2];

            Vector3 curve = CatmullRom(p0, p1, p2, p3, f);
            return Vector3.LerpUnclamped(straight, curve, Mathf.Clamp01(smoothing));
        }

        /// <summary>
        /// Dense sampled polyline over the whole route (loop closes back to node 0). Each segment is subdivided
        /// into <paramref name="perSegment"/> steps when smoothing is active, or emitted as a single chord when
        /// it's flat — so a straight route stays cheap. Used to feed the LineRenderer.
        /// </summary>
        public static List<Vector3> Densify(IList<Vector3> pts, bool loop, float smoothing, int perSegment)
        {
            var outPts = new List<Vector3>();
            int count = pts.Count;
            if (count == 0) return outPts;
            if (count == 1) { outPts.Add(pts[0]); return outPts; }

            int steps = smoothing <= 0.0001f ? 1 : Mathf.Max(1, perSegment);
            int segs = loop ? count : count - 1;
            for (int segment = 0; segment < segs; segment++)
            {
                for (int k = 0; k < steps; k++)
                    outPts.Add(Point(pts, loop, segment, k / (float)steps, smoothing));
            }
            outPts.Add(loop ? pts[0] : pts[count - 1]);   // close the final vertex exactly on the node
            return outPts;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float f)
        {
            float f2 = f * f, f3 = f2 * f;
            return 0.5f * ((2f * p1)
                + (-p0 + p2) * f
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f2
                + (-p0 + 3f * p1 - 3f * p2 + p3) * f3);
        }

        private static int Wrap(int i, int n) => ((i % n) + n) % n;
    }
}
