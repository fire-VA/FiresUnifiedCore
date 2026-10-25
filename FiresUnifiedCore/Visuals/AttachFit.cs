using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// One helmet fit for FMO monsters, companions and VCS.
    /// A helmet keeps its offset and rotation in a HEAD FRAME (up = neck to head, front = the body front), rebuilt in the target rig's
    /// frame and scaled by the skull-cap size ratio, then a per-model table (hand-placed in the rip): Off (x up, y forward,
    /// z right; m, head frame), Euler (deg), Scale (x the skull ratio). Table rows: built-in defaults, overridden by
    /// BepInEx\config\FiresAttachFit.txt when present (same format as the rip's Tools\vcs_attach_fit.txt). Public, add-only contract.
    /// </summary>
    public static class AttachFit
    {
        public struct Tune { public Vector3 Off, Euler; public float Scale; }

        public const string HelmetAttach = "Helmet_attach";

        private static readonly string[] s_defaults =
        {
            "Dverger|Helmet_attach|-0.111,0.049,-0.023|24.9,0.1,1.3|1.094",
            "DvergerMage|Helmet_attach|-0.111,0.049,-0.023|24.9,0.1,1.3|1.094",
            "Goblin|Helmet_attach|-0.119,0.06,-0.02|9.4,-0.5,0.3|0.632",
            "Draugr|Helmet_attach|0.043,0.026,-0.026|26,0.8,0.3|1",
            "Skeleton|Helmet_attach|-0.002,-0.088,-0.004|0,0,0|0.75",
            "Greyling|Helmet_attach|-0.054,0.038,-0.013|0,0,0|1",
            "Fenring|Helmet_attach|-0.112,0.064,-0.011|0.1,0.7,1.8|1",
            "Fenring_Cultist|Helmet_attach|-0.106,0.074,-0.008|0,0,0|1",
            // Skinned armour on a monster, attach = the item prefab; only Off is used (baked).
            "Draugr|ArmorWolfChest|-0.04,0,0|0,0,0|1",
        };

        private static Dictionary<string, Tune> s_fit;

        /// <summary>Where the current rows were read from (file, its write time, the read time). Add-only.</summary>
        public static string Source { get; private set; } = "not read yet";

        private static Dictionary<string, Tune> Table()
        {
            if (s_fit != null) return s_fit;
            var d = new Dictionary<string, Tune>(StringComparer.Ordinal);
            foreach (var line in s_defaults) Parse(line, d);
            int overridden = 0;
            try
            {
                string path = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAttachFit.txt");
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path)) if (Parse(line, d)) overridden++;
                // Which file the rows came from and when it was read, quoted by every fit line (proof the value reached the code)
                Source = File.Exists(path) ? $"FiresAttachFit.txt (written {File.GetLastWriteTime(path):HH:mm:ss}, read {DateTime.Now:HH:mm:ss}, {overridden} row(s))" : "built-in defaults only";
                FiresLogger.LogDebug($"[AttachFit] table: {d.Count} row(s), {overridden} from {(File.Exists(path) ? path : "no override file")}; keys: {string.Join(", ", new List<string>(d.Keys).ToArray())}");
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[AttachFit] override file not read: {ex.Message}"); }
            return s_fit = d;
        }

        // Add-only.
        /// <summary>Drops the cached table and re-reads it now (built-in rows + BepInEx\config\FiresAttachFit.txt), so the
        /// "[AttachFit] table: ..." Debug line prints the new rows. Returns the row count. Already-placed items keep their old fit
        /// until they are attached again.</summary>
        public static int Reload()
        {
            s_fit = null;
            return Table().Count;
        }

        /// <summary>Writes (or replaces) the row "model|attach" in
        /// BepInEx\config\FiresAttachFit.txt and reloads the table, so every later placement reads it. Off / Euler / Scale as in the table.</summary>
        public static bool Set(string model, string attach, Tune t)
        {
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(attach)) return false;
            string I(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            string row = $"{model}|{attach}|{I(t.Off.x)},{I(t.Off.y)},{I(t.Off.z)}|{I(t.Euler.x)},{I(t.Euler.y)},{I(t.Euler.z)}|{I(t.Scale)}";
            return Rewrite(model, attach, row);
        }

        /// <summary>Removes the file row "model|attach" (the built-in default, if any, applies again).</summary>
        public static bool Remove(string model, string attach) => Rewrite(model, attach, null);

        private static bool Rewrite(string model, string attach, string row)
        {
            try
            {
                string path = Path.Combine(BepInEx.Paths.ConfigPath, "FiresAttachFit.txt");
                var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
                string key = model + "|" + attach + "|";
                lines.RemoveAll(l => l.TrimStart().StartsWith(key, StringComparison.Ordinal));
                if (row != null) lines.Add(row);
                File.WriteAllLines(path, lines);
                Reload();
                FiresLogger.LogInfo($"[AttachFit] {(row != null ? "saved " + row : "removed " + model + "|" + attach)}");
                return true;
            }
            catch (Exception ex) { FiresLogger.LogWarning("[AttachFit] row not written: " + ex.Message); return false; }
        }

        private static bool Parse(string line, Dictionary<string, Tune> into)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) return false;
            var f = line.Split('|');
            if (f.Length < 5) return false;
            if (!V3(f[2], out var off) || !V3(f[3], out var eul) || !float.TryParse(f[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return false;
            into[f[0].Trim() + "|" + f[1].Trim()] = new Tune { Off = off, Euler = eul, Scale = s };
            return true;
        }

        private static bool V3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            var p = s.Split(',');
            if (p.Length != 3) return false;
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                v[i] = x;
            }
            return true;
        }

        /// <summary>The tuning for a model's attach: the exact model name, then the name trimmed at its last '_' (Draugr_Elite -> Draugr),
        /// repeatedly; none = no tuning (Scale 1).</summary>
        public static Tune For(string model, string attach) => For(model, attach, out _);

        /// <summary><see cref="For(string, string)"/>, plus the table key that matched (null = none), so a family fallback is never
        /// silent. The exact key always wins. The attach name is free-form (rows only for new slots).</summary>
        public static Tune For(string model, string attach, out string matched)
        {
            var t = Table();
            for (string m = model; !string.IsNullOrEmpty(m); )
            {
                if (t.TryGetValue(m + "|" + attach, out var hit)) { matched = m + "|" + attach; return hit; }
                int cut = m.LastIndexOf('_');
                m = cut > 0 ? m.Substring(0, cut) : null;
            }
            matched = null;
            return new Tune { Scale = 1f };
        }

        public static Vector3 BodyFront(Animator a) => ArmorRebind.BodyFront(a);

        /// <summary>The head frame: up = neck to head, forward = the body front made perpendicular to it.</summary>
        public static Quaternion HeadFrame(Animator a, Transform neck, Transform head)
        {
            Vector3 up = head.position - neck.position; if (up.sqrMagnitude < 1e-8f) up = Vector3.up; up.Normalize();
            Vector3 fwd = ArmorRebind.BodyFront(a); fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude < 1e-6f) fwd = a.transform.forward;
            return Quaternion.LookRotation(fwd.normalized, up);
        }

        /// <summary>The skull cap: skinned vertices whose main bone is the head or under it, above the head bone; x = widest off the up
        /// axis within the top 40 % (ears/snouts/jaws excluded), y = the top. Zero when the skin can't be read.</summary>
        public static Vector2 SkullSize(ArmorRebind.BodyProfile prof, Transform head, Vector3 up)
        {
            if (prof == null || head == null || prof.Skin == null) return Vector2.zero;
            float top = 0f; var pts = new List<Vector2>();
            foreach (var smr in prof.Skin)
            {
                if (smr == null) continue;
                var mesh = MeshReadback.Readable(smr.sharedMesh, out _);
                if (mesh == null) continue;
                var v = mesh.vertices; var w = mesh.boneWeights; var bp = mesh.bindposes; var bones = smr.bones;
                if (w.Length != v.Length) continue;
                for (int i = 0; i < v.Length; i++)
                {
                    int bi = w[i].boneIndex0;
                    if (bi < 0 || bi >= bones.Length || bi >= bp.Length || bones[bi] == null || !bones[bi].IsChildOf(head)) continue;
                    Vector3 p = (bones[bi].localToWorldMatrix * bp[bi]).MultiplyPoint3x4(v[i]) - head.position;
                    float along = Vector3.Dot(p, up);
                    if (along < 0f) continue;
                    pts.Add(new Vector2((p - along * up).magnitude, along)); top = Mathf.Max(top, along);
                }
            }
            float r = 0f;
            foreach (var q in pts) if (q.y >= top * 0.6f) r = Mathf.Max(r, q.x);
            return new Vector2(r, top);
        }

        // ── any attach point, not only the head ─────────────────────

        private static Transform ParentHuman(Animator a, HumanBodyBones id)
        {
            var b = a.GetBoneTransform(id);
            if (b == null) return null;
            var ids = new HashSet<Transform>();
            foreach (HumanBodyBones h in Enum.GetValues(typeof(HumanBodyBones)))
                if (h != HumanBodyBones.LastBone) { var t = a.GetBoneTransform(h); if (t != null) ids.Add(t); }
            for (var t = b.parent; t != null; t = t.parent) if (ids.Contains(t)) return t;
            return null;
        }

        /// <summary>The bone frame (the rip's VcsAttachFitter.Frame): up = from the parent humanoid bone to this one (lower arm to
        /// hand, neck to head), forward = the body front made perpendicular to it. Identity rotation when the bone is missing.</summary>
        public static Quaternion BoneFrame(Animator a, HumanBodyBones bone)
        {
            var b = a != null ? a.GetBoneTransform(bone) : null;
            if (b == null) return Quaternion.identity;
            var parent = ParentHuman(a, bone);
            Vector3 up = parent != null ? b.position - parent.position : Vector3.up;
            if (up.sqrMagnitude < 1e-8f) up = Vector3.up; up.Normalize();
            Vector3 fwd = ArmorRebind.BodyFront(a); fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.Cross(up, a.transform.right);
            return Quaternion.LookRotation(fwd.normalized, up);
        }

        /// <summary>
        /// <see cref="PlaceOnHead"/>'s rule for any humanoid bone (hands: RightHand_Attach / LeftHand_Attach). <paramref name="att"/>'s
        /// CURRENT world pose is where it sits on <paramref name="from"/>'s <paramref name="bone"/>; it is re-parented to
        /// <paramref name="to"/>'s same bone: offLocal = inverse(Fp) * (att.position - fromBone.position), rotLocal = inverse(Fp) *
        /// att.rotation, s = autoScale * Tune.Scale, position = toBone.position + Fm * (offLocal * s + (Off.z, Off.x, Off.y)),
        /// rotation = Fm * Euler(Tune.Euler) * rotLocal, localScale *= s, with Fp/Fm = <see cref="BoneFrame"/> and the row
        /// For(modelKey, attachKey). Same precondition as PlaceOnHead (a rigid item; the frame front reads the body). Returns an
        /// evidence line, or null when there is nothing to place on. Add-only.
        /// </summary>
        public static string PlaceOnBone(Transform att, Animator from, Animator to, HumanBodyBones bone, string modelKey, string attachKey, float autoScale = 1f)
        {
            if (att == null || from == null || to == null) return null;
            var pb = from.GetBoneTransform(bone); var mb = to.GetBoneTransform(bone);
            if (mb == null) return null;
            if (pb == null) { att.SetParent(mb, true); return $"plain re-parent onto '{mb.name}' (source has no {bone})"; }
            Quaternion fp = BoneFrame(from, bone), fm = BoneFrame(to, bone);
            var fit = For(modelKey, attachKey, out var matched);
            float s = Mathf.Max(0.01f, autoScale) * fit.Scale;
            Vector3 offLocal = Quaternion.Inverse(fp) * (att.position - pb.position);
            Quaternion rotLocal = Quaternion.Inverse(fp) * att.rotation;
            att.SetParent(mb, true);
            att.position = mb.position + fm * (offLocal * s + new Vector3(fit.Off.z, fit.Off.x, fit.Off.y));
            att.rotation = fm * Quaternion.Euler(fit.Euler) * rotLocal;
            att.localScale = att.localScale * s;
            return $"{attachKey} on '{mb.name}' ({bone}); table '{modelKey}' -> {matched ?? "no row"} off {fit.Off} rot {fit.Euler} scale {fit.Scale:0.###}; frames differ {Quaternion.Angle(fp, fm):0} deg";
        }

        private static Transform Neck(Animator a) =>
            a.GetBoneTransform(HumanBodyBones.Neck) ?? a.GetBoneTransform(HumanBodyBones.UpperChest) ?? a.GetBoneTransform(HumanBodyBones.Chest);

        /// <summary>
        /// Seats <paramref name="att"/> (whose CURRENT world pose is where it sits on <paramref name="from"/>'s head) on
        /// <paramref name="to"/>'s head. Contract (the order matters): pF/mF = <see cref="HeadFrame"/> of each rig (neck fallback on both:
        /// Neck ?? UpperChest ?? Chest); offLocal = inverse(pF) * (att.position - fromHead.position); rotLocal = inverse(pF) * att.rotation;
        /// s = auto * Tune.Scale, where auto = clamp((ms.x/ps.x + ms.y/ps.y) / 2, 0.5, 3) from <see cref="SkullSize"/> when all four are
        /// over 0.01, else 1 (Scale MULTIPLIES the auto ratio; it is not absolute); then
        /// position = toHead.position + mF * (offLocal * s + (Off.z, Off.x, Off.y)); rotation = mF * Euler(Tune.Euler) * rotLocal;
        /// localScale *= s. PRECONDITION: both rigs hold the SAME pose when this is called (VCS: after the pose follower's copy; a T-posed
        /// reference against an animated monster gives a wrong frame). Missing head/neck bones = a plain SetParent(toHead, true), said so
        /// in the returned evidence line (null only when there is nothing to place on).
        /// </summary>
        public static string PlaceOnHead(Transform att, Animator from, Animator to, ArmorRebind.BodyProfile fromProfile, ArmorRebind.BodyProfile toProfile, string modelKey)
        {
            if (att == null || from == null || to == null) return null;
            var pHead = from.GetBoneTransform(HumanBodyBones.Head); var pNeck = Neck(from);
            var mHead = to.GetBoneTransform(HumanBodyBones.Head); var mNeck = Neck(to);
            if (mHead == null) return null;
            if (pHead == null || pNeck == null || mNeck == null)
            {
                att.SetParent(mHead, true);
                return $"plain re-parent onto '{mHead.name}' (missing: {(pHead == null ? "source head " : "")}{(pNeck == null ? "source neck " : "")}{(mNeck == null ? "target neck" : "")})";
            }
            Quaternion pF = HeadFrame(from, pNeck, pHead), mF = HeadFrame(to, mNeck, mHead);
            Vector2 ps = SkullSize(fromProfile, pHead, pF * Vector3.up), ms = SkullSize(toProfile, mHead, mF * Vector3.up);
            float auto = ps.x > 0.01f && ms.x > 0.01f && ps.y > 0.01f && ms.y > 0.01f ? Mathf.Clamp((ms.x / ps.x + ms.y / ps.y) * 0.5f, 0.5f, 3f) : 1f;
            var fit = For(modelKey, HelmetAttach, out var matched);
            float s = auto * fit.Scale;
            Vector3 offLocal = Quaternion.Inverse(pF) * (att.position - pHead.position);
            Quaternion rotLocal = Quaternion.Inverse(pF) * att.rotation;
            att.SetParent(mHead, true);
            att.position = mHead.position + mF * (offLocal * s + new Vector3(fit.Off.z, fit.Off.x, fit.Off.y));   // x up, y forward, z right
            att.rotation = mF * Quaternion.Euler(fit.Euler) * rotLocal;
            att.localScale = att.localScale * s;
            return $"skull radius {ps.x * 100f:0}/{ms.x * 100f:0} cm, height {ps.y * 100f:0}/{ms.y * 100f:0} cm -> auto x{auto:0.00}; table '{modelKey}' -> {matched ?? "no row"} off {fit.Off} rot {fit.Euler} scale {fit.Scale:0.###}; head frames differ {Quaternion.Angle(pF, mF):0} deg";
        }
    }
}
