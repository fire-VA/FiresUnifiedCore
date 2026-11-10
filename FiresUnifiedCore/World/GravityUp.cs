using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// The ONE "which way is up here" answer for every mod (halo ring worlds, Fire 10-24). Moved from FDT's halo spike
    /// (FiresDebugginTools Utilities\HaloSpike\GravityUp.cs, rig-tested 1.1.1012..1.1.1017) into Core, as agreed with [seasons] (FTS),
    /// [saddles] (FAT) and [visual]. Default everywhere: world up, so nothing changes outside a registered field.
    ///
    /// Public, ADD-ONLY contract (callers bind by reflection, type "FiresCore.World.GravityUp, FiresUnifiedCore"):
    ///   bool    Active                     a field exists in this world (any provider registered, or FAT's ring active)
    ///   bool    InField(Vector3 pos)       pos is inside a field
    ///   Vector3 At(Vector3 pos)            normalised up at pos (world up outside every field)
    ///   bool    InField(Vector3 pos, out Vector3 up)
    ///   Quaternion Frame(Vector3 pos)      FromToRotation(world up, At(pos))
    ///   Vector3 UpFromAxis(pos, origin, dir)   a ring's up: toward the closest point on its hub line (dir normalised)
    ///   void    Register / Unregister(IGravityUpProvider)
    ///
    /// Built in: FAT's halo ring through its public FatApi (VerdantsAscent.FatApi: HaloActive, HaloTryGetUp, HaloAxis, HaloBand), by
    /// reflection, re-checked at most every 2 s (FAT syncs the ring itself, so it can appear after load). Inside a FULL ring
    /// (half arc >= 180) the whole interior answers, floor to axis (Fire: "fly all the way up to the ring's center before it turns us").
    /// </summary>
    public interface IGravityUpProvider
    {
        /// <summary>True when pos is inside this provider's field; up = unit vector away from "down" there.</summary>
        bool TryGetUp(Vector3 pos, out Vector3 up);
    }

    public static class GravityUp
    {
        private static readonly List<IGravityUpProvider> s_providers = new List<IGravityUpProvider>();

        public static void Register(IGravityUpProvider p) { if (p != null && !s_providers.Contains(p)) s_providers.Add(p); }
        public static void Unregister(IGravityUpProvider p) { if (p != null) s_providers.Remove(p); }

        /// <summary>A field exists in this world.</summary>
        public static bool Active { get { FatRing.Check(); return s_providers.Count > 0 || FatRing.Registered; } }

        public static bool InField(Vector3 pos) => InField(pos, out _);

        public static bool InField(Vector3 pos, out Vector3 up)
        {
            FatRing.Check();
            for (int i = 0; i < s_providers.Count; i++)
            {
                try { if (s_providers[i].TryGetUp(pos, out up) && up.sqrMagnitude > 0.5f) { up.Normalize(); return true; } }
                catch { }
            }
            if (FatRing.Registered && FatRing.TryGetUp(pos, out up) && up.sqrMagnitude > 0.5f) { up.Normalize(); return true; }
            up = Vector3.up;
            return false;
        }

        /// <summary>Normalised up at pos; world up outside every field.</summary>
        public static Vector3 At(Vector3 pos) => InField(pos, out Vector3 up) ? up : Vector3.up;

        /// <summary>The rotation from the world frame (y up) to the local frame at pos.</summary>
        public static Quaternion Frame(Vector3 pos) => Quaternion.FromToRotation(Vector3.up, At(pos));

        /// <summary>Add-only ([ghost], companion ring pass): v in the floor plane at <paramref name="at"/>. Outside every field this is
        /// exactly new Vector3(v.x, 0, v.z); in a field, v minus its component along the local up.</summary>
        public static Vector3 Flat(Vector3 v, Vector3 at) => InField(at, out Vector3 up) ? v - up * Vector3.Dot(v, up) : new Vector3(v.x, 0f, v.z);

        /// <summary>Add-only: the component of v along the local up at <paramref name="at"/>; exactly v.y outside every field.</summary>
        public static float UpComponent(Vector3 v, Vector3 at) => InField(at, out Vector3 up) ? Vector3.Dot(v, up) : v.y;

        // ── 0.2.817 (add-only): bodies whose motion something else owns right now. HaloFrame (FDT) and Core's GravityBody add no
        // field gravity to them: a climber (Core Climbing), a body a possession form carries (BodyCarrier), anything attached (a seated
        // companion, a chair, a saddle), and whatever another mod marks with SetBodyOverride (a tornado lift, a scripted move).
        private static readonly HashSet<int> s_override = new HashSet<int>();

        /// <summary>0.2.817: mark / unmark a body as moved by its owner (no field gravity while marked). Pair every true with a false.</summary>
        public static void SetBodyOverride(GameObject body, bool on)
        {
            if (body == null) return;
            if (on) s_override.Add(body.GetInstanceID()); else s_override.Remove(body.GetInstanceID());
        }

        /// <summary>0.2.817: true while something else owns this body's motion (see above); field gravity must leave it alone.</summary>
        public static bool BodyOverride(GameObject body)
        {
            if (body == null) return false;
            if (s_override.Contains(body.GetInstanceID())) return true;
            var c = body.GetComponent<Character>();
            if (c != null)
            {
                if (c.IsAttached()) return true;
                if (FiresCore.Movement.Climb.IsClimbing(c)) return true;
                // 0.2.819: a possessed creature in flight is steered by the possession flight model
                if (c.IsFlying() && FiresPossession.PossessionEngine.Creature == c) return true;
            }
            return FiresCore.Possession.BodyCarrier.IsCarrying(body);
        }

        /// <summary>A ring's up: toward the closest point on its hub line (axisDir normalised). A single centre point would tilt the band
        /// edges toward the midplane, so the line it is.</summary>
        public static Vector3 UpFromAxis(Vector3 pos, Vector3 axisOrigin, Vector3 axisDir)
        {
            Vector3 closest = axisOrigin + axisDir * Vector3.Dot(pos - axisOrigin, axisDir);
            Vector3 toAxis = closest - pos;
            float d = toAxis.magnitude;
            return d > 1e-4f ? toAxis / d : Vector3.up;
        }

        // ── FAT's halo ring, by reflection (no FAT reference) ──
        private static class FatRing
        {
            private delegate bool TryUp(Vector3 pos, out Vector3 up);
            private delegate bool AxisOf(out Vector3 origin, out Vector3 dir, out float radius);
            private delegate bool BandOf(out float halfWidth, out float halfArcDeg, out float thickness);

            private static bool s_looked, s_bound;
            private static Func<bool> s_active;
            private static TryUp s_tryUp;
            private static AxisOf s_axis;
            private static BandOf s_band;
            private static float s_nextCheck;
            internal static bool Registered;

            internal static void Check()
            {
                if (Time.realtimeSinceStartup < s_nextCheck) return;
                s_nextCheck = Time.realtimeSinceStartup + 2f;
                if (!Bind()) { Registered = false; return; }
                try { Registered = s_active(); } catch { Registered = false; }
            }

            private static bool Bind()
            {
                if (s_looked) return s_bound;
                s_looked = true;
                try
                {
                    Type t = Type.GetType("VerdantsAscent.FatApi, FiresAdminTerrain", false);
                    if (t == null) return false;
                    const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                    var active = t.GetProperty("HaloActive", S)?.GetGetMethod();
                    var tryUp = t.GetMethod("HaloTryGetUp", S, null, new[] { typeof(Vector3), typeof(Vector3).MakeByRefType() }, null);
                    if (active == null || tryUp == null) return false;
                    s_active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active);
                    s_tryUp = (TryUp)Delegate.CreateDelegate(typeof(TryUp), tryUp);
                    var axis = t.GetMethod("HaloAxis", S, null, new[] { typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                    if (axis != null) s_axis = (AxisOf)Delegate.CreateDelegate(typeof(AxisOf), axis);
                    var band = t.GetMethod("HaloBand", S, null, new[] { typeof(float).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                    if (band != null) s_band = (BandOf)Delegate.CreateDelegate(typeof(BandOf), band);
                    s_bound = true;
                }
                catch { s_bound = false; }
                return s_bound;
            }

            internal static bool TryGetUp(Vector3 pos, out Vector3 up)
            {
                up = Vector3.up;
                try
                {
                    if (s_tryUp != null && s_tryUp(pos, out up)) return true;
                    // the whole interior of a full ring (older FATs answered only R-30 m .. R+T+1 m)
                    if (s_axis == null || s_band == null) return false;
                    if (!s_axis(out Vector3 origin, out Vector3 dir, out float radius) || !s_band(out float halfWidth, out float halfArc, out float thickness)) return false;
                    if (halfArc < 180f) return false;
                    dir.Normalize();
                    Vector3 rel = pos - origin;
                    if (Mathf.Abs(Vector3.Dot(rel, dir)) > halfWidth + 2f) return false;
                    float r = Vector3.ProjectOnPlane(rel, dir).magnitude;
                    if (r < 1f || r > radius + thickness + 1f) return false;
                    up = UpFromAxis(pos, origin, dir);
                    return true;
                }
                catch { up = Vector3.up; return false; }
            }
        }
    }
}
