using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// A MEASURED auto-fit plus a diagnostic, instead of hand-tuning. Measures a body's skull from its own
    /// skin in its current pose (BakeMesh; vertices whose main bone is the Head or under it), as a frame: centre, rotation (up snapped
    /// to neck to head, forward to the body front, so a snout can't flip it) and size along those axes. A rigid helmet is then carried
    /// from the PLAYER's skull frame (where vanilla seats it on a player) into the monster's: centre to centre, axes to axes, uniform
    /// scale by the skull-size ratio (clamped). <see cref="Report"/> gives the numbers for any placement (fitted or vanilla). Public,
    /// add-only. The fit is behind [MonsterArmorFit] "Measured helmet fit" (default OFF); the report and debug boxes always work.
    /// </summary>
    public static class FitMeasure
    {
        public struct Frame
        {
            public bool Ok;
            public string Why;
            public Vector3 Centre;      // world
            public Quaternion Rot;      // world; up = skull up, forward = face front
            public Vector3 Size;        // along Rot's right, up, forward (m)
            public int Vertices;
            public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(Rot) * (world - Centre);
            public Vector3 ToWorld(Vector3 local) => Centre + Rot * local;
        }

        private static ConfigEntry<bool> s_measured;
        public static bool MeasuredHelmetFit => s_measured != null && s_measured.Value;

        // thresholds the report marks FAIL at (centre 2 cm, tilt 8 deg, scale 15 %)
        public const float CentreFailM = 0.02f, TiltFailDeg = 8f, ScaleFailFrac = 0.15f;
        public const float ScaleMin = 0.6f, ScaleMax = 2f;

        internal static void Bind(ConfigFile config)
        {
            s_measured = config.Bind("MonsterArmorFit", "Measured helmet fit", false,
                "CLIENT. Rigid helmets on registered monsters are placed by MEASURING the monster's skull (from its own skin, in its pose) and " +
                "carrying the helmet over from where it sits on the player's measured skull: centre to centre, tilt to tilt, scaled by the skull-size " +
                "ratio. Off = vanilla's own helmet attach (plus the optional '<Model>|MonsterHelmet' nudge row). The console command " +
                "'fires_fit_debug on' draws the measured skull and helmet boxes.");
        }

        private static Transform Neck(Animator a) =>
            a.GetBoneTransform(HumanBodyBones.Neck) ?? a.GetBoneTransform(HumanBodyBones.UpperChest) ?? a.GetBoneTransform(HumanBodyBones.Chest);

        /// <summary>The skull frame of <paramref name="a"/>'s body skin in its current pose. Ok = false (with Why) when it can't be read.</summary>
        public static Frame Skull(Animator a, IList<SkinnedMeshRenderer> skin)
        {
            var f = new Frame();
            var head = a != null ? a.GetBoneTransform(HumanBodyBones.Head) : null;
            var neck = a != null ? Neck(a) : null;
            if (head == null || neck == null) { f.Why = "no head/neck bone"; return f; }
            Vector3 up = head.position - neck.position; if (up.sqrMagnitude < 1e-8f) up = Vector3.up; up.Normalize();
            Vector3 fwd = ArmorRebind.BodyFront(a); fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude < 1e-6f) fwd = a.transform.forward;
            f.Rot = Quaternion.LookRotation(fwd.normalized, up);
            Quaternion inv = Quaternion.Inverse(f.Rot);

            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            int n = 0;
            var baked = new Mesh();
            try
            {
                foreach (var smr in skin)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    var src = MeshReadback.Readable(smr.sharedMesh, out _);
                    if (src == null) continue;
                    var w = src.boneWeights; var bones = smr.bones;
                    // Baked unscaled and taken to world space by the renderer's own
                    // localToWorldMatrix, so the rig's scale (0.01 on most monsters) is applied exactly once.
                    smr.BakeMesh(baked, false);
                    var v = baked.vertices;
                    if (w.Length != v.Length) continue;
                    var toWorld = smr.transform.localToWorldMatrix;
                    for (int i = 0; i < v.Length; i++)
                    {
                        var bw = w[i];
                        if (bw.weight0 < 0.5f || bw.boneIndex0 < 0 || bw.boneIndex0 >= bones.Length) continue;
                        var b = bones[bw.boneIndex0];
                        if (b == null || !b.IsChildOf(head)) continue;
                        Vector3 p = inv * (toWorld.MultiplyPoint3x4(v[i]) - head.position);
                        min = Vector3.Min(min, p); max = Vector3.Max(max, p); n++;
                    }
                }
            }
            catch (Exception ex) { f.Why = "skin read failed: " + ex.Message; return f; }
            finally { UnityEngine.Object.Destroy(baked); }
            if (n < 8) { f.Why = $"only {n} head vertices"; return f; }
            Vector3 c = (min + max) * 0.5f;
            f.Size = max - min;
            // A skull outside 5-80 cm is a units error, never a verdict.
            float big = Mathf.Max(f.Size.x, Mathf.Max(f.Size.y, f.Size.z));
            if (big < 0.05f || big > 0.8f) { f.Why = $"skull size {f.Size.x:0.000},{f.Size.y:0.000},{f.Size.z:0.000} m is not a skull (units?)"; return f; }
            f.Centre = head.position + f.Rot * c;
            f.Vertices = n;
            f.Ok = true;
            return f;
        }

        /// <summary>The item's renderers' mesh bounds as an oriented box in <paramref name="frame"/>'s space: centre and size (m).</summary>
        public static bool ItemBox(GameObject item, Frame frame, out Vector3 centre, out Vector3 size, out Vector3 upInFrame)
        {
            centre = size = Vector3.zero; upInFrame = Vector3.up;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            bool any = false;
            foreach (var mf in item.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null || mf.GetComponent<Renderer>() == null) continue;
                var b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = frame.ToLocal(mf.transform.TransformPoint(corner));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
                }
            }
            if (!any) return false;
            centre = (min + max) * 0.5f; size = max - min;
            upInFrame = Quaternion.Inverse(frame.Rot) * item.transform.up;
            return true;
        }

        /// <summary>
        /// The diagnostic: the item as placed on the monster vs the player relationship (the same item on the player's skull, measured the
        /// same way). Errors are in the MONSTER frame after scaling the player's relation by <paramref name="scale"/>. Returns a line with
        /// PASS / FAIL against the thresholds.
        /// </summary>
        public static string Report(string what, Frame monster, Frame player, GameObject itemOnMonster, Vector3 playerCentre, Vector3 playerSize, Vector3 playerUp, float scale)
        {
            if (!monster.Ok) return $"{what}: monster skull not measured ({monster.Why})";
            if (!player.Ok) return $"{what}: player skull not measured ({player.Why})";
            if (!ItemBox(itemOnMonster, monster, out var c, out var s, out var up)) return $"{what}: item has no mesh bounds";
            Vector3 wantC = playerCentre * scale;
            float centreErr = (c - wantC).magnitude;
            float tilt = Vector3.Angle(up, playerUp);
            float wantW = playerSize.x * scale;
            float scaleErr = wantW > 1e-4f ? s.x / wantW - 1f : 0f;
            Vector3 top = new Vector3(0f, monster.Size.y * 0.5f, 0f);
            bool covers = top.x >= c.x - s.x * 0.5f && top.x <= c.x + s.x * 0.5f && top.y >= c.y - s.y * 0.5f && top.y <= c.y + s.y * 0.5f + 0.02f
                          && top.z >= c.z - s.z * 0.5f && top.z <= c.z + s.z * 0.5f;
            bool fail = centreErr > CentreFailM || tilt > TiltFailDeg || Mathf.Abs(scaleErr) > ScaleFailFrac;
            return $"{what}: {(fail ? "FAIL" : "PASS")} centre err {centreErr * 100f:0.0} cm (item {Fmt(c)} want {Fmt(wantC)}), tilt {tilt:0.0} deg, " +
                   $"width {s.x * 100f:0.0} cm vs want {wantW * 100f:0.0} ({scaleErr * 100f:+0;-0} %), covers skull top {covers}; " +
                   $"skull {Fmt(monster.Size)} m ({monster.Vertices} verts), player skull {Fmt(player.Size)} m, scale x{scale:0.00}";
        }

        private static string Fmt(Vector3 v) => $"({v.x:0.000},{v.y:0.000},{v.z:0.000})";

        public static float SkullScale(Frame monster, Frame player)
        {
            if (!monster.Ok || !player.Ok || player.Size.x < 1e-4f || player.Size.y < 1e-4f) return 1f;
            return Mathf.Clamp((monster.Size.x / player.Size.x + monster.Size.y / player.Size.y) * 0.5f, ScaleMin, ScaleMax);
        }

        // ── debug boxes (console: fires_fit_debug on|off) ─────────────────────────────────────────────────────────────────────────
        internal static bool DebugBoxes;
        private static Material s_lineMat;

        /// <summary>Draws a wire box (frame space centre/size) parented to <paramref name="parent"/>; debug only.</summary>
        internal static void Box(Transform parent, Frame frame, Vector3 centre, Vector3 size, Color colour, string name)
        {
            if (!DebugBoxes || parent == null) return;
            try
            {
                var old = parent.Find(name);
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                var go = new GameObject(name);
                go.transform.SetParent(parent, true);
                var lr = go.AddComponent<LineRenderer>();
                if (s_lineMat == null) s_lineMat = new Material(Shader.Find("Sprites/Default"));
                lr.material = s_lineMat; lr.startColor = lr.endColor = colour; lr.startWidth = lr.endWidth = 0.005f;
                lr.useWorldSpace = false; lr.positionCount = 16;
                Vector3 h = size * 0.5f;
                Vector3[] k =
                {
                    new Vector3(-h.x,-h.y,-h.z), new Vector3( h.x,-h.y,-h.z), new Vector3( h.x,-h.y, h.z), new Vector3(-h.x,-h.y, h.z), new Vector3(-h.x,-h.y,-h.z),
                    new Vector3(-h.x, h.y,-h.z), new Vector3( h.x, h.y,-h.z), new Vector3( h.x,-h.y,-h.z), new Vector3( h.x, h.y,-h.z), new Vector3( h.x, h.y, h.z),
                    new Vector3( h.x,-h.y, h.z), new Vector3( h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z), new Vector3(-h.x,-h.y, h.z), new Vector3(-h.x, h.y, h.z),
                    new Vector3(-h.x, h.y,-h.z),
                };
                for (int i = 0; i < k.Length; i++) lr.SetPosition(i, go.transform.InverseTransformPoint(frame.ToWorld(centre + k[i])));
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[FitMeasure] debug box failed: {ex.Message}"); }
        }

        private static bool s_cmd;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class FitDebug_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_cmd) return;
                s_cmd = true;
                new Terminal.ConsoleCommand("fires_fit_debug",
                    "[FiresUnifiedCore] fires_fit_debug on|off - draw the measured skull (green) and helmet (yellow) boxes on monsters fitted from now on.",
                    args =>
                    {
                        DebugBoxes = args.Length > 1 ? args[1] == "on" || args[1] == "1" : !DebugBoxes;
                        args.Context?.AddString("fires_fit_debug: boxes " + (DebugBoxes ? "ON" : "OFF") + " (applies to helmets fitted from now on)");
                    });
            }
        }
    }
}
