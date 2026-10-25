using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.818 (Fire 10-24, halo ring; [visual]'s plan A4 + A9). Off the ring (no GravityUp field) every path below is the vanilla one.
    /// A4 - projectiles and cinders: vanilla adds Vector3.down x gravity in Projectile / Cinder.FixedUpdate (Tail has no FixedUpdate: its cosmetic tail stays as it is). Each
    ///      Vector3.down there is replaced by DownFor(this): -GravityUp.At(position) in a field, Vector3.down otherwise.
    /// A9 - fall damage: vanilla's fall = m_maxAirAltitude - position.y (world y). In a field Core tracks each character's height
    ///      ALONG up (the sum of its moves projected on the local up), keeps the highest point while airborne, and hands vanilla a
    ///      m_maxAirAltitude that gives the same fall: position.y + (highest - now). A mod that resets the fall reference the vanilla
    ///      way (m_maxAirAltitude = position.y, e.g. the end of Bat Form) still resets it; GravityUp.ResetFall(c) does it explicitly.
    ///      The under-world rescue (UnderWorldCheck) and the edge-of-world kill are skipped in a field: both measure the flat world.
    /// </summary>
    [HarmonyPatch]
    public static class RingGravityPatches
    {
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static void Once(string key, string line) { if (s_logged.Count < 60 && s_logged.Add(key)) Debug.Log(line); }

        // ---- A4 ------------------------------------------------------------------------------------------------------------
        /// <summary>The "down" a projectile / cinder falls along at its position.</summary>
        public static Vector3 DownFor(Component c)
        {
            if (c == null || !GravityUp.Active) return Vector3.down;
            if (!GravityUp.InField(c.transform.position, out Vector3 up)) return Vector3.down;
            Once("proj:" + c.GetType().Name, $"[GravityUp] {c.GetType().Name} {Utils.GetPrefabName(c.gameObject)}: gravity along -up ({up.x:0.00}, {up.y:0.00}, {up.z:0.00}) (ring frame)");
            return -up;
        }

        [HarmonyPatch]
        private static class DownTranspile
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Projectile), "FixedUpdate");
                yield return AccessTools.Method(typeof(Cinder), "FixedUpdate");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
            {
                var getDown = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.down));
                var downFor = AccessTools.Method(typeof(RingGravityPatches), nameof(DownFor));
                int n = 0;
                foreach (var ins in code)
                {
                    if (ins.Calls(getDown))
                    {
                        // keep the labels on the first new instruction
                        var load = new CodeInstruction(OpCodes.Ldarg_0) { labels = ins.labels, blocks = ins.blocks };
                        yield return load;
                        yield return new CodeInstruction(OpCodes.Call, downFor);
                        n++;
                        continue;
                    }
                    yield return ins;
                }
                Debug.Log($"[GravityUp] {original.DeclaringType?.Name}.{original.Name}: {n} Vector3.down site(s) follow the local up{(n == 0 ? " - NONE FOUND (its gravity stays world -y)" : "")}");
            }
        }

        // ---- 0.2.819 creature #10 ([wishbone]): attack arcs, spread and attack heights around the LOCAL up ---------------------
        // FireProjectileBurst (launch angle axis, spread / circular launch), DoAreaAttack (origin + up x m_attackHeight) and
        // DoMeleeAttack (LookRotation up, origin + up x height, the character sphere casts + up x m_attackHeightChar1/2) read
        // Vector3.up: each read becomes AttackUp(this) = GravityUp.At(attacker) in a field, Vector3.up otherwise.
        private static readonly AccessTools.FieldRef<Attack, Humanoid> s_attacker = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        public static Vector3 AttackUp(Attack a)
        {
            if (a == null || !GravityUp.Active) return Vector3.up;
            Humanoid h = s_attacker(a);
            if (h == null || !GravityUp.InField(h.transform.position, out Vector3 up)) return Vector3.up;
            Once("atk:" + h.m_name, $"[GravityUp] attack arcs / heights along the local up: {h.m_name} (ring frame)");
            return up;
        }

        [HarmonyPatch]
        private static class AttackUpTranspile
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Attack), "FireProjectileBurst");
                yield return AccessTools.Method(typeof(Attack), "DoAreaAttack");
                yield return AccessTools.Method(typeof(Attack), "DoMeleeAttack");
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, MethodBase original)
            {
                var getUp = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.up));
                var attackUp = AccessTools.Method(typeof(RingGravityPatches), nameof(AttackUp));
                int n = 0;
                foreach (var ins in code)
                {
                    if (ins.Calls(getUp))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = ins.labels, blocks = ins.blocks };
                        yield return new CodeInstruction(OpCodes.Call, attackUp);
                        n++;
                        continue;
                    }
                    yield return ins;
                }
                Debug.Log($"[GravityUp] Attack.{original.Name}: {n} Vector3.up site(s) follow the attacker's local up{(n == 0 ? " - NONE FOUND" : "")}");
            }
        }

        // GetMeleeAttackDir keeps the aim's height but takes x/z from the body's forward: that split is in the ring frame here
        private static readonly MethodInfo s_attackOrigin = AccessTools.Method(typeof(Attack), "GetAttackOrigin");

        [HarmonyPatch(typeof(Attack), "GetMeleeAttackDir")]
        [HarmonyPrefix]
        private static bool MeleeDir(Attack __instance, ref Transform originJoint, ref Vector3 attackDir)
        {
            if (!GravityUp.Active) return true;
            Humanoid h = s_attacker(__instance);
            if (h == null || s_attackOrigin == null || !GravityUp.InField(h.transform.position, out Vector3 up)) return true;
            originJoint = s_attackOrigin.Invoke(__instance, null) as Transform;
            if (originJoint == null) return true;
            Quaternion frame = Quaternion.FromToRotation(Vector3.up, up), inv = Quaternion.Inverse(frame);
            Vector3 fwd = h.transform.forward;
            Vector3 aimL = inv * h.GetAimDir(originJoint.position), fwdL = inv * fwd;
            aimL.x = fwdL.x; aimL.z = fwdL.z;
            Vector3 aim = (frame * aimL).normalized;
            attackDir = Vector3.RotateTowards(fwd, aim, Mathf.PI / 180f * __instance.m_maxYAngle, 10f);
            return false;
        }

        // ---- A9 ------------------------------------------------------------------------------------------------------------
        private sealed class FallState { public Vector3 Prev; public float Along, Highest, Written; }
        private static readonly Dictionary<int, FallState> s_fall = new Dictionary<int, FallState>();
        private static readonly AccessTools.FieldRef<Character, float> s_maxAir = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");

        /// <summary>0.2.818 (add-only): reset c's fall reference to where it is now (no fall damage from here), on and off a ring.</summary>
        public static void ResetFall(Character c)
        {
            if (c == null) return;
            s_maxAir(c) = c.transform.position.y;
            if (s_fall.TryGetValue(c.GetInstanceID(), out var st)) { st.Highest = st.Along; st.Written = s_maxAir(c); }
        }

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        [HarmonyPrefix]
        private static void FallPrefix(Character __instance)
        {
            if (__instance == null || !GravityUp.Active) return;
            Vector3 pos = __instance.transform.position;
            int id = __instance.GetInstanceID();
            if (!GravityUp.InField(pos, out Vector3 up) || GravityUp.BodyOverride(__instance.gameObject)) { s_fall.Remove(id); return; }
            if (!s_fall.TryGetValue(id, out var st))
            {
                st = new FallState { Prev = pos, Along = 0f, Highest = 0f };
                s_fall[id] = st;
                Once("fall:" + __instance.m_name, $"[GravityUp] fall height along up: {__instance.m_name} (ring frame)");
            }
            else
            {
                float cur = s_maxAir(__instance);
                // someone reset the fall reference the vanilla way since our last write (cur moved down to about position.y)
                if (Mathf.Abs(cur - st.Written) > 0.01f && cur <= pos.y + 0.05f) st.Highest = st.Along;
                st.Along += Vector3.Dot(pos - st.Prev, up);
                st.Prev = pos;
            }
            if (st.Along > st.Highest) st.Highest = st.Along;
            float fall = st.Highest - st.Along;
            st.Written = pos.y + fall;
            s_maxAir(__instance) = st.Written;
        }

        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        [HarmonyPostfix]
        private static void FallPostfix(Character __instance)
        {
            if (__instance == null || !s_fall.TryGetValue(__instance.GetInstanceID(), out var st)) return;
            // vanilla puts m_maxAirAltitude back to position.y when grounded (and after dealing the damage): follow it
            float cur = s_maxAir(__instance);
            if (Mathf.Abs(cur - st.Written) > 0.01f)
            {
                if (st.Highest - st.Along > 4f) Once("landed:" + __instance.m_name, $"[GravityUp] fall along up: {__instance.m_name} landed after {st.Highest - st.Along:0.0} m (ring frame)");
                st.Highest = st.Along;
                st.Written = cur;
            }
        }

        [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
        [HarmonyPrefix]
        private static bool UnderWorld(Character __instance)
        {
            if (__instance == null || !GravityUp.Active || !GravityUp.InField(__instance.transform.position)) return true;
            Once("underworld", "[GravityUp] UnderWorldCheck skipped in the field (it measures the flat world's heightmap)");
            return false;
        }

        [HarmonyPatch(typeof(Player), "EdgeOfWorldKill")]
        [HarmonyPrefix]
        private static bool EdgeKill(Player __instance)
        {
            if (__instance == null || !GravityUp.Active || !GravityUp.InField(__instance.transform.position)) return true;
            Once("edgekill", "[GravityUp] EdgeOfWorldKill skipped in the field");
            return false;
        }
    }
}
