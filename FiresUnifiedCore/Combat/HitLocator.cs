using System;
using System.Linq;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Combat
{
    public enum BodyPart
    {
        Unknown,
        Head,
        Neck,
        Chest,
        Torso,
        UpperArm,
        Forearm,
        Hand,
        Thigh,
        Shin,
        Foot,
    }

    /// <summary>Where a hit landed on a character: the body part, the bone it was matched to, and how far from it.</summary>
    public readonly struct HitLocation
    {
        public HitLocation(BodyPart part, string bone, float distance, bool fromSkeleton)
        {
            Part = part;
            Bone = bone ?? string.Empty;
            Distance = distance;
            FromSkeleton = fromSkeleton;
        }

        public BodyPart Part { get; }
        public string Bone { get; }
        public float Distance { get; }

        /// <summary>False when the character has no humanoid skeleton and the part came from its head bone or height bands.</summary>
        public bool FromSkeleton { get; }

        public override string ToString() =>
            FromSkeleton ? $"{Part} ({Bone}, {Distance:0.00} m)" : Bone.Length > 0 ? $"{Part} (near {Bone})" : $"{Part} (height band)";
    }

    /// <summary>
    /// Maps a world point on a character to a body part, as this client draws the character: called on the attacker's client it
    /// is the attacker's view, as vanilla's hit detection is. Humanoid skeletons (players and humanoid creatures) use the
    /// nearest bone segment. Others use their head bone where they have one (quadrupeds: the top of the capsule is the back),
    /// else height bands of the capsule. Pure: no state.
    /// </summary>
    public static class HitLocator
    {
        // The Head bone sits at the base of the skull; the head, a hand or a foot is a short segment past its bone, along the limb.
        private const float HeadLengthMeters = 0.22f;
        private const float HandLengthMeters = 0.12f;
        private const float FootLengthMeters = 0.18f;
        // Non-humanoid characters: a point this close to the head bone is the head.
        private const float HeadRadiusMeters = 0.4f;
        // Height bands (fractions of the capsule from the feet) when a character has no head bone at all.
        private const float HeadBandFraction = 0.15f;
        private const float LegBandFraction = 0.35f;
        private const float MinHeightMeters = 0.01f;
        private const string HeadBoneName = "Head";
        private const string HeadBoneNameLower = "head";

        private static readonly AccessTools.FieldRef<Character, Animator> s_animator = AccessTools.FieldRefAccess<Character, Animator>("m_animator");

        // Neck, Chest and UpperChest are optional in Unity's humanoid rig: each end of a segment takes the first bone present.
        private static readonly HumanBodyBones[] ChestOrSpine = { HumanBodyBones.Chest, HumanBodyBones.Spine };
        private static readonly HumanBodyBones[] AboveChest = { HumanBodyBones.Neck, HumanBodyBones.Head };
        private static readonly HumanBodyBones[] BelowHead = { HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine };

        private readonly struct Segment
        {
            public Segment(BodyPart part, HumanBodyBones[] from, HumanBodyBones[] to)
            {
                Part = part;
                From = from;
                To = to;
            }

            public BodyPart Part { get; }
            public HumanBodyBones[] From { get; }
            public HumanBodyBones[] To { get; }
        }

        private static Segment Between(BodyPart part, HumanBodyBones from, HumanBodyBones to) => new Segment(part, new[] { from }, new[] { to });

        private static readonly Segment[] Segments =
        {
            Between(BodyPart.Neck, HumanBodyBones.Neck, HumanBodyBones.Head),
            new Segment(BodyPart.Chest, ChestOrSpine, AboveChest),
            new Segment(BodyPart.Torso, new[] { HumanBodyBones.Hips }, ChestOrSpine),
            Between(BodyPart.UpperArm, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
            Between(BodyPart.UpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
            Between(BodyPart.Forearm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
            Between(BodyPart.Forearm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            Between(BodyPart.Thigh, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg),
            Between(BodyPart.Thigh, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
            Between(BodyPart.Shin, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
            Between(BodyPart.Shin, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
        };

        private static readonly (BodyPart Part, HumanBodyBones Bone, HumanBodyBones[] Parent, float Length)[] Ends =
        {
            (BodyPart.Head, HumanBodyBones.Head, BelowHead, HeadLengthMeters),
            (BodyPart.Hand, HumanBodyBones.LeftHand, new[] { HumanBodyBones.LeftLowerArm }, HandLengthMeters),
            (BodyPart.Hand, HumanBodyBones.RightHand, new[] { HumanBodyBones.RightLowerArm }, HandLengthMeters),
            (BodyPart.Foot, HumanBodyBones.LeftFoot, new[] { HumanBodyBones.LeftLowerLeg }, FootLengthMeters),
            (BodyPart.Foot, HumanBodyBones.RightFoot, new[] { HumanBodyBones.RightLowerLeg }, FootLengthMeters),
        };

        /// <summary>Where on the victim, as this client draws it, the world point is.</summary>
        public static HitLocation Locate(Character victim, Vector3 point)
        {
            if (victim == null) return default;
            Animator animator = s_animator(victim);
            if (animator != null && animator.isHuman)
            {
                HitLocation best = default;
                float bestDistance = float.MaxValue;
                foreach (Segment segment in Segments)
                {
                    Transform from = FirstBone(animator, segment.From), to = FirstBone(animator, segment.To);
                    if (from == null || to == null || from == to) continue;
                    float distance = DistanceToSegment(point, from.position, to.position);
                    if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(segment.Part, from.name, distance, true); }
                }
                foreach (var end in Ends)
                {
                    Transform bone = Bone(animator, end.Bone), parent = FirstBone(animator, end.Parent);
                    if (bone == null || parent == null || bone == parent) continue;
                    Vector3 along = bone.position - parent.position;
                    Vector3 tip = bone.position + (along.sqrMagnitude > 0f ? along.normalized : Vector3.up) * end.Length;
                    float distance = DistanceToSegment(point, bone.position, tip);
                    if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(end.Part, bone.name, distance, true); }
                }
                if (bestDistance < float.MaxValue) return best;
            }
            float height = Mathf.Max(MinHeightMeters, victim.GetHeight());
            float fraction = (point.y - victim.transform.position.y) / height;
            Transform head = Utils.FindChild(victim.transform, HeadBoneName) ?? Utils.FindChild(victim.transform, HeadBoneNameLower);
            if (head != null)
            {
                float distance = Vector3.Distance(point, head.position);
                BodyPart near = distance <= HeadRadiusMeters ? BodyPart.Head : fraction <= LegBandFraction ? BodyPart.Thigh : BodyPart.Torso;
                return new HitLocation(near, head.name, distance, false);
            }
            BodyPart band = fraction >= 1f - HeadBandFraction ? BodyPart.Head : fraction <= LegBandFraction ? BodyPart.Thigh : BodyPart.Torso;
            return new HitLocation(band, string.Empty, 0f, false);
        }

        // How far the shot is followed past the capsule's far side.
        private const float ShotExtraMeters = 0.2f;
        private const float SegmentEpsilon = 1e-8f;

        /// <summary>
        /// Where on the victim the SHOT went. Vanilla reports a hit on the character's single capsule collider, a capsule radius
        /// from any bone ([fgn], R46: every located hit 0.45-0.55 m from its "nearest" segment, so the centre segments won and a
        /// head shot read Chest). So the shot is followed from that surface point through the body, and the part is the bone
        /// segment it passes closest to. A zero direction falls back to the point-only Locate.
        /// </summary>
        public static HitLocation Locate(Character victim, Vector3 point, Vector3 direction)
        {
            if (victim == null) return default;
            if (direction.sqrMagnitude < SegmentEpsilon) return Locate(victim, point);
            Vector3 shotEnd = point + direction.normalized * (2f * victim.GetRadius() + ShotExtraMeters);
            Animator animator = s_animator(victim);
            if (animator != null && animator.isHuman)
            {
                // First contact: an arrow stops at the first part it meets. Walk the shot from the entry point and take the first
                // part within its own thickness (R71 pvp bow_head: shooting 18-32 deg upward, the whole-shot nearest segment was the
                // head 0.5 m further up the line, 5/6 "Head" where the victim's side saw 1/6).
                Vector3 unit = direction.normalized;
                float length = Vector3.Distance(point, shotEnd);
                for (float s = 0f; s <= length + 0.0001f; s += FirstContactStep)
                {
                    HitLocation near = Nearest(animator, point + unit * s);
                    if (near.Part != BodyPart.Unknown && near.Distance <= Thickness(near.Part))
                    {
                        LastMethod = $"first contact {s:0.00} m in";
                        return near;
                    }
                }
                LastMethod = "nearest to the shot (no first contact)";

                HitLocation best = default;
                float bestDistance = float.MaxValue;
                foreach (Segment segment in Segments)
                {
                    Transform from = FirstBone(animator, segment.From), to = FirstBone(animator, segment.To);
                    if (from == null || to == null || from == to) continue;
                    float distance = SegmentToSegmentDistance(point, shotEnd, from.position, to.position);
                    if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(segment.Part, from.name, distance, true); }
                }
                foreach (var end in Ends)
                {
                    Transform bone = Bone(animator, end.Bone), parent = FirstBone(animator, end.Parent);
                    if (bone == null || parent == null || bone == parent) continue;
                    Vector3 along = bone.position - parent.position;
                    Vector3 tip = bone.position + (along.sqrMagnitude > 0f ? along.normalized : Vector3.up) * end.Length;
                    float distance = SegmentToSegmentDistance(point, shotEnd, bone.position, tip);
                    if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(end.Part, bone.name, distance, true); }
                }
                if (bestDistance < float.MaxValue) return best;
            }
            // No humanoid skeleton: the point of the shot nearest the head bone, else its middle, through the point-only rules.
            LastMethod = "no skeleton";
            Transform head = Utils.FindChild(victim.transform, HeadBoneName) ?? Utils.FindChild(victim.transform, HeadBoneNameLower);
            Vector3 probe = head != null ? ClosestOnSegment(head.position, point, shotEnd) : (point + shotEnd) * 0.5f;
            return Locate(victim, probe);
        }

        /// <summary>How the last <see cref="Locate(Character, Vector3, Vector3)"/> picked its part, for the log: "first contact 0.12 m in",
        /// "nearest to the shot (no first contact)", or "no skeleton".</summary>
        public static string LastMethod { get; private set; } = "";

        // First-contact walk: the step along the shot, and each part's half-thickness round its bone segment (m).
        private const float FirstContactStep = 0.04f;

        private static float Thickness(BodyPart part)
        {
            switch (part)
            {
                case BodyPart.Head: return 0.12f;
                case BodyPart.Neck: return 0.07f;
                case BodyPart.Chest: return 0.17f;
                case BodyPart.Torso: return 0.16f;
                case BodyPart.UpperArm: return 0.07f;
                case BodyPart.Forearm: return 0.06f;
                case BodyPart.Hand: return 0.05f;
                case BodyPart.Thigh: return 0.09f;
                case BodyPart.Shin: return 0.07f;
                case BodyPart.Foot: return 0.06f;
                default: return 0f;
            }
        }

        // The part whose bone segment is nearest the point, with the distance (Unknown when the skeleton has none).
        private static HitLocation Nearest(Animator animator, Vector3 point)
        {
            HitLocation best = default;
            float bestDistance = float.MaxValue;
            foreach (Segment segment in Segments)
            {
                Transform from = FirstBone(animator, segment.From), to = FirstBone(animator, segment.To);
                if (from == null || to == null || from == to) continue;
                float distance = DistanceToSegment(point, from.position, to.position);
                if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(segment.Part, from.name, distance, true); }
            }
            foreach (var end in Ends)
            {
                Transform bone = Bone(animator, end.Bone), parent = FirstBone(animator, end.Parent);
                if (bone == null || parent == null || bone == parent) continue;
                Vector3 along = bone.position - parent.position;
                Vector3 tip = bone.position + (along.sqrMagnitude > 0f ? along.normalized : Vector3.up) * end.Length;
                float distance = DistanceToSegment(point, bone.position, tip);
                if (distance < bestDistance) { bestDistance = distance; best = new HitLocation(end.Part, bone.name, distance, true); }
            }
            return best;
        }

        /// <summary>The shortest distance between the segments p1-q1 and p2-q2 (either may be a single point).</summary>
        public static float SegmentToSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= SegmentEpsilon && e <= SegmentEpsilon) return r.magnitude;
            if (a <= SegmentEpsilon) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= SegmentEpsilon) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2), denominator = a * e - b * b;
                    s = denominator > SegmentEpsilon ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
        }

        private static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            float t = lengthSquared > 0f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSquared) : 0f;
            return a + ab * t;
        }

        /// <summary>The distance from a point to the segment a-b.</summary>
        public static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            float t = lengthSquared > 0f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSquared) : 0f;
            return Vector3.Distance(point, a + ab * t);
        }

        // [visual] (R46 bow_legs 0/8): vanilla Utils.GetBoneTransform finds a child NAMED like the enum, and the player skeleton is
        // Mixamo-named (LeftUpLeg, LeftLeg, LeftArm, LeftForeArm, Spine1...), so every limb and Chest came back null and each hit read
        // Chest/Torso. The humanoid avatar map knows them; the name lookup stays for rigs without one.
        private static Transform Bone(Animator animator, HumanBodyBones bone)
        {
            Transform mapped = animator.isHuman ? animator.GetBoneTransform(bone) : null;
            return mapped != null ? mapped : Utils.GetBoneTransform(animator, bone);
        }

        private static Transform FirstBone(Animator animator, HumanBodyBones[] bones)
        {
            foreach (HumanBodyBones bone in bones)
            {
                Transform found = Bone(animator, bone);
                if (found != null) return found;
            }
            return null;
        }

        private const string CommandName = "fires_hitlocate";
        private const float CommandRayMeters = 100f;

        /// <summary>fires_hitlocate: what the crosshair is on, located the way a hit there would be.</summary>
        internal static void RegisterCommand()
        {
            try
            {
                new Terminal.ConsoleCommand(CommandName,
                    "[FiresUnifiedCore] Where on the character under your crosshair a hit would land: body part, bone and distance.",
                    args =>
                    {
                        Camera camera = GameCamera.instance != null ? GameCamera.instance.GetComponent<Camera>() : null;
                        if (camera == null) { args.Context?.AddString($"{CommandName}: no camera"); return; }
                        var ray = new Ray(camera.transform.position, camera.transform.forward);
                        foreach (RaycastHit hit in Physics.RaycastAll(ray, CommandRayMeters).OrderBy(h => h.distance))
                        {
                            Character character = hit.collider != null ? hit.collider.GetComponentInParent<Character>() : null;
                            if (character == null || character == Player.m_localPlayer) continue;
                            args.Context?.AddString($"{CommandName}: {character.m_name} at {hit.distance:0.0} m: {Locate(character, hit.point, ray.direction)}");
                            return;
                        }
                        args.Context?.AddString($"{CommandName}: no character under the crosshair");
                    });
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[HitLocator] {CommandName} not registered: {ex.Message}");
            }
        }
    }
}
