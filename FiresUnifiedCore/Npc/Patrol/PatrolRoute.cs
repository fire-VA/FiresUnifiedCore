using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// An ordered patrol polyline (world XYZ points). Loop is auto-detected when the first and last
    /// points are within <see cref="LoopCloseThreshold"/> — a loop is walked continuously in one
    /// direction; an open route is walked there-and-back (to the end, wait, reverse, wait, repeat).
    /// A route also owns zero or more named <see cref="SpeedPreset"/>s (the DBSM speed model); an NPC
    /// assignment selects a route + a preset (empty = Default = today's flat base-walk behaviour).
    /// </summary>
    public class PatrolRoute
    {
        public string Name;
        public readonly List<Vector3> Points = new List<Vector3>();
        public bool IsLoop;
        public const float LoopCloseThreshold = 4f;

        /// <summary>Named DBSM speed presets saved to this route. Empty on a legacy route = Default behaviour.</summary>
        public readonly List<SpeedPreset> Presets = new List<SpeedPreset>();

        // ── Path-following tuning (route-wide; applies even to the Default no-preset walk) ──
        // These are the knobs the patrol editor exposes so an admin can shape how an NPC tracks the line.

        /// <summary>How close (metres) the NPC must get to a node before advancing to the next. Small = it
        /// hugs each checkpoint (direct, point-to-point); large = it advances early and rounds the corner.</summary>
        public float ArrivalRadius = DefaultArrivalRadius;

        /// <summary>Pure-pursuit "carrot on a string" distance (metres) aimed ahead along the path so the NPC
        /// flows through nodes instead of braking at each. Larger cuts corners more (and can skip nodes).</summary>
        public float LookAhead = DefaultLookAhead;

        /// <summary>0 = straight polyline (segments), 1 = full Catmull-Rom curve through the nodes. Bends both the
        /// rendered line and the followed path so corners round off instead of jerking point-to-point.</summary>
        public float Smoothing = 0f;

        /// <summary>Node indices the NPC must actually reach (doorways / bridges / around obstacles where the drawn
        /// path is the only correct one). The look-ahead never aims past a must-hit node, and it's never skipped.</summary>
        public readonly HashSet<int> MustHit = new HashSet<int>();

        public const float DefaultArrivalRadius = 3.5f;
        public const float DefaultLookAhead = 5f;

        public void DetectLoop()
        {
            IsLoop = Points.Count >= 3 &&
                     Vector3.Distance(Points[0], Points[Points.Count - 1]) <= LoopCloseThreshold;
        }

        /// <summary>A node was inserted at <paramref name="newIndex"/> — shift later must-hit indices up so they still flag the same physical nodes.</summary>
        public void ShiftMustHitForInsert(int newIndex)
        {
            if (MustHit.Count == 0) return;
            var shifted = new List<int>();
            foreach (var i in MustHit) shifted.Add(i >= newIndex ? i + 1 : i);
            MustHit.Clear();
            foreach (var i in shifted) MustHit.Add(i);
        }

        /// <summary>The node at <paramref name="removedIndex"/> was deleted — drop its must-hit flag and shift later indices down.</summary>
        public void ShiftMustHitForDelete(int removedIndex)
        {
            if (MustHit.Count == 0) return;
            var shifted = new List<int>();
            foreach (var i in MustHit)
            {
                if (i == removedIndex) continue;
                shifted.Add(i > removedIndex ? i - 1 : i);
            }
            MustHit.Clear();
            foreach (var i in shifted) MustHit.Add(i);
        }

        /// <summary>Drops must-hit indices outside [0, count-1]. Call after any node-count change.</summary>
        public void ClampMustHit(int count)
        {
            if (MustHit.Count == 0) return;
            MustHit.RemoveWhere(i => i < 0 || i >= count);
        }

        /// <summary>Deep copy (points + presets + follow tuning + must-hit) — used by node edits that must not hold a live reference.</summary>
        public PatrolRoute Clone()
        {
            var copy = new PatrolRoute
            {
                Name = Name, IsLoop = IsLoop,
                ArrivalRadius = ArrivalRadius, LookAhead = LookAhead, Smoothing = Smoothing,
            };
            copy.Points.AddRange(Points);
            foreach (var preset in Presets) copy.Presets.Add(preset.Clone());
            foreach (var i in MustHit) copy.MustHit.Add(i);
            return copy;
        }

        /// <summary>Resolves a preset by name (case-insensitive). Null/empty/"Default" → null = identity (base walk).</summary>
        public SpeedPreset GetPreset(string name)
        {
            if (string.IsNullOrEmpty(name) || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
                return null;
            for (int i = 0; i < Presets.Count; i++)
                if (string.Equals(Presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return Presets[i];
            return null;
        }
    }

    /// <summary>Curve shape of a <see cref="Section"/>'s speed envelope over its span.</summary>
    public enum SpeedShape { Flat, EaseInOut, Arch, Ramp, Custom }

    /// <summary>
    /// A DBSM speed envelope over an admin-marked span of a route (StartIndex→EndIndex, arc-length
    /// normalized to u∈[0,1]). Speed is a multiple of the NPC's base walk speed:
    /// <c>speedMul(u) = lerp(Floor, Peak, C(u))</c> where C is the <see cref="Shape"/> curve, or a
    /// direct piecewise-linear read of <see cref="CustomCurve"/> when Shape=Custom. Outside every
    /// section the multiplier is 1.0 (base walk). Sections are non-overlapping and index-ordered.
    /// </summary>
    public class Section
    {
        public int StartIndex;
        public int EndIndex;
        public float Peak = 1f;   // top multiple of base walk (curve at C(u)=1)
        public float Floor = 1f;  // multiple at the span ends (never 0 => never freezes)
        public SpeedShape Shape = SpeedShape.EaseInOut;
        public float ShapeK = 1f; // steepness / hump width
        public List<Vector2> CustomCurve; // (u, speedMul) control points, sorted by u; only for Shape=Custom

        /// <summary>Inclusive-low index of the span, order-normalized (Start ≤ End).</summary>
        public int Low => Mathf.Min(StartIndex, EndIndex);
        /// <summary>Inclusive-high index of the span, order-normalized (Start ≤ End).</summary>
        public int High => Mathf.Max(StartIndex, EndIndex);

        public bool Contains(int index) => index >= Low && index <= High;

        /// <summary>Speed multiplier at local progress u∈[0,1] along this span.</summary>
        public float Sample(float u) => DbsmEnvelope.SampleSection(this, u);

        public Section Clone() => new Section
        {
            StartIndex = StartIndex, EndIndex = EndIndex, Peak = Peak, Floor = Floor,
            Shape = Shape, ShapeK = ShapeK,
            CustomCurve = CustomCurve == null ? null : new List<Vector2>(CustomCurve),
        };
    }

    /// <summary>An admin-flagged route node the NPC eases to a stop at, holds for <see cref="Duration"/>s, then resumes.</summary>
    public class StopPoint
    {
        public int Index;
        public float Duration; // seconds
    }

    /// <summary>
    /// A named DBSM speed preset saved to a route. Fully defines speed behaviour: a set of
    /// <see cref="Sections"/> (spans with an envelope) + <see cref="StopPoints"/> (paused nodes), plus
    /// the <see cref="RunThreshold"/> above which the NPC switches to the run tier + run animation.
    /// Different NPCs on the same route can select different presets.
    /// </summary>
    public class SpeedPreset
    {
        public string Name;
        public float RunThreshold = 1.15f; // speedMul above this → run tier + run anim
        public readonly List<Section> Sections = new List<Section>();
        public readonly List<StopPoint> StopPoints = new List<StopPoint>();

        /// <summary>The section covering <paramref name="index"/>, or null (base walk). Sections are non-overlapping.</summary>
        public Section SectionAt(int index)
        {
            for (int i = 0; i < Sections.Count; i++)
                if (Sections[i].Contains(index)) return Sections[i];
            return null;
        }

        /// <summary>The stop-point flagged on <paramref name="index"/>, or null.</summary>
        public StopPoint StopAt(int index)
        {
            for (int i = 0; i < StopPoints.Count; i++)
                if (StopPoints[i].Index == index) return StopPoints[i];
            return null;
        }

        /// <summary>A node was inserted at <paramref name="newIndex"/> — shift later section/stop indices up so they still point at the same physical nodes.</summary>
        public void ShiftForInsert(int newIndex)
        {
            foreach (var section in Sections)
            {
                if (section.StartIndex >= newIndex) section.StartIndex++;
                if (section.EndIndex >= newIndex) section.EndIndex++;
            }
            foreach (var stop in StopPoints)
                if (stop.Index >= newIndex) stop.Index++;
        }

        /// <summary>The node at <paramref name="removedIndex"/> was deleted — drop any stop on it and shift later indices down.</summary>
        public void ShiftForDelete(int removedIndex)
        {
            StopPoints.RemoveAll(sp => sp.Index == removedIndex);
            foreach (var section in Sections)
            {
                if (section.StartIndex > removedIndex) section.StartIndex--;
                if (section.EndIndex > removedIndex) section.EndIndex--;
            }
            foreach (var stop in StopPoints)
                if (stop.Index > removedIndex) stop.Index--;
        }

        /// <summary>Clamps section/stop indices into [0, count-1] and de-duplicates stops sharing a node. Call after any node-count change.</summary>
        public void ClampToRoute(int count)
        {
            if (count <= 0) { Sections.Clear(); StopPoints.Clear(); return; }
            int max = count - 1;
            foreach (var section in Sections)
            {
                section.StartIndex = Mathf.Clamp(section.StartIndex, 0, max);
                section.EndIndex = Mathf.Clamp(section.EndIndex, 0, max);
            }
            var seen = new HashSet<int>();
            for (int i = StopPoints.Count - 1; i >= 0; i--)
            {
                StopPoints[i].Index = Mathf.Clamp(StopPoints[i].Index, 0, max);
                if (!seen.Add(StopPoints[i].Index)) StopPoints.RemoveAt(i);
            }
        }

        public SpeedPreset Clone()
        {
            var copy = new SpeedPreset { Name = Name, RunThreshold = RunThreshold };
            foreach (var section in Sections) copy.Sections.Add(section.Clone());
            foreach (var stop in StopPoints) copy.StopPoints.Add(new StopPoint { Index = stop.Index, Duration = stop.Duration });
            return copy;
        }
    }
}
