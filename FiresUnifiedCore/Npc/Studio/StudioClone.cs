using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Studio
{
    /// <summary>
    /// The Studio god clone (Fire 10-30: walk mode with no game client). A Core companion (CompanionNpc) spawned, owned and simulated
    /// by the SERVER, wearing and fighting with copies of the server God character's gear (<see cref="GodProfile"/>), driven by FGC from
    /// the Studio's input. Reuse:
    ///  - body: CompanionPrefabManager.SpawnCompanion / CompanionNetworkHelper.Destroy; Core's AlwaysAnimate culling (CompanionAI) runs
    ///    because the server owns it ([fgn] proved server-owned companions animate on the -nographics dedi);
    ///  - look: CompanionAppearanceService.ApplyOnServer (the barber's own WriteKeys + broadcast);
    ///  - gear: the companion's own CompanionInventory (GetSlotForItem / EquipItem), real item copies, never added to any real inventory;
    ///  - skills: CompanionSkills.SetSkillLevel;
    ///  - brain off: ZDO "valcast_possessed" (CompanionPossession.IsPossessed - the companion brain stands down);
    ///  - ownership: "fgn_server_keeps" (FGN 1.6.24 + CompanionOwnershipKeeper.ServerKeeps) - the server keeps it;
    ///  - zones: TamedCompanionZoneLoader.ServerHeldPositions - the same keep-alive, also with no player connected.
    /// "studio_clone" = never vaulted / kenneled / saved as a companion. Server only. JSON out for FGC reflection. Add-only.
    /// </summary>
    public static class StudioClone
    {
        public const string CloneKey = "studio_clone", GhostKey = "studio_ghost";
        private const string PossessedKey = "valcast_possessed";
        private const float TtlSeconds = 30f;
        internal static CompanionController s_clone;
        internal static float s_lastFrame;

        public static bool IsClone(ZDO zdo) => zdo != null && zdo.GetBool(CloneKey);

        /// <summary>at = "x,y,z" (empty = the god character's logout point is unknown on the server: spawn at the world spawn).</summary>
        public static string Spawn(string at, string godName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Refuse("server only");
            if (s_clone != null) return Pose();
            if (!GodProfile.Loaded && !GodProfile.Load(godName, out string why)) return Refuse(why);
            Vector3 pos = Parse(at, out bool okPos) ;
            if (!okPos) pos = ZoneSystem.instance != null && ZoneSystem.instance.GetLocationIcon("StartTemple", out Vector3 start) ? start + Vector3.up * 2f : Vector3.up * 40f;
            var go = CompanionPrefabManager.SpawnCompanion("CompanionNpc", pos, Quaternion.identity);
            var c = go != null ? go.GetComponent<CompanionController>() : null;
            var nv = go != null ? go.GetComponent<ZNetView>() : null;
            if (c == null || nv == null || !nv.IsValid()) { if (go != null) CompanionNetworkHelper.Destroy(go); return Refuse("spawn failed"); }
            var zdo = nv.GetZDO();
            long god = GodProfile.Shadow.GetPlayerID();
            zdo.Set(CloneKey, true);
            zdo.Set(CompanionOwnershipKeeper.ServerKeepsKey, true);
            zdo.Set(PossessedKey, god);
            zdo.Set("fires_guard", true);   // Core Guard ([wishbone], FiresCore.World.Guard.ZdoKey): the clone never dies on the owning server
            zdo.Set("companion_tamed", true); zdo.Set("companion_owner", god);
            zdo.Set("companion_name", GodProfile.Name);
            c.isTamed = true; c.ownerPlayerId = god; c.companionName = GodProfile.Name;
            go.AddComponent<StudioCloneDriver>();
            SuspendCompanionBrain(c);
            CopyPlayerMovement(c);
            s_clone = c; s_lastFrame = Time.time;
            Resync();
            FiresLogger.LogDebug($"[StudioClone] spawned {zdo.m_uid} at {pos} for god '{c.companionName}'");
            return Pose();
        }

        // Fire 10-30 ("it does NOT move around like a player"). Why it moved badly: the clone's companion brain kept running - CompanionAI
        // (a vanilla BaseAI) rewrites the body's move / look direction in its UpdateAI every physics tick, and the idle sub-behaviours
        // (stay wander, trips, chores, emotes) issue their own moves - so the Studio's input only won between their writes; and it moved at
        // the companion prefab's speeds, not a player's. Now: the brain + idle behaviours are switched off on the clone (it is only ever
        // driven), the driver re-applies the frame's controls right before vanilla's movement physics (Character.CustomFixedUpdate), and
        // the clone takes vanilla Player's movement values (walk / run / crouch / swim speed, acceleration, turn, jump) from the Player prefab.
        private static void SuspendCompanionBrain(CompanionController c)
        {
            var ai = c.GetComponent<FiresCore.Npc.AI.CompanionAI>(); if (ai != null) { ai.StopMoving(); ai.enabled = false; }
            var idle = c.GetComponent<CompanionIdleBehavior>(); if (idle != null) idle.enabled = false;
            FiresLogger.LogDebug($"[StudioClone] companion brain suspended (AI {(ai != null ? "off" : "none")}, idle behaviours {(idle != null ? "off" : "none")}): only the Studio drives it");
        }

        private static void CopyPlayerMovement(CompanionController c)
        {
            var src = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player")?.GetComponent<Player>() : null;
            var dst = c.GetComponent<Character>();
            if (src == null || dst == null) return;
            dst.m_walkSpeed = src.m_walkSpeed; dst.m_speed = src.m_speed; dst.m_runSpeed = src.m_runSpeed; dst.m_crouchSpeed = src.m_crouchSpeed;
            dst.m_swimSpeed = src.m_swimSpeed; dst.m_acceleration = src.m_acceleration; dst.m_turnSpeed = src.m_turnSpeed;
            dst.m_runTurnSpeed = src.m_runTurnSpeed; dst.m_jumpForce = src.m_jumpForce; dst.m_jumpForceForward = src.m_jumpForceForward;
            FiresLogger.LogDebug($"[StudioClone] player movement: walk {src.m_walkSpeed}, jog {src.m_speed}, run {src.m_runSpeed}, crouch {src.m_crouchSpeed}, jump {src.m_jumpForce}");
        }

        // the frame's controls get the last word, right before vanilla's movement physics reads them
        [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
        private static class Character_CustomFixedUpdate_CloneControls
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(Character __instance)
            {
                if (s_clone == null || __instance == null || __instance.gameObject != s_clone.gameObject) return;
                s_clone.GetComponent<StudioCloneDriver>()?.ApplyControls();
            }
        }

        public static string Despawn() => Despawn("walk.end");

        /// <summary>Removes the clone; <paramref name="reason"/> goes into one Info line ("walk.end", "studio session ended", "idle N min").</summary>
        public static string Despawn(string reason)
        {
            var c = s_clone; s_clone = null;
            TamedCompanionZoneLoader.ServerHeldPositions.Clear();
            if (c == null) return Refuse("no clone");
            RegisterClassBody(c, false);
            FiresLogger.LogInfo($"[StudioClone] despawned: {reason ?? "?"}");
            CompanionNetworkHelper.Destroy(c.gameObject);
            return "{\"ok\":true}";
        }

        /// <summary>Re-dresses and re-arms the clone from the god character (look, item copies, equipped, skills).</summary>
        public static string Resync()
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            if (!GodProfile.Loaded) return Refuse("god character not loaded");
            var nv = c.GetComponent<ZNetView>();
            CompanionAppearanceService.ApplyOnServer(nv.GetZDO().m_uid, GodProfile.Look());
            var inv = c.GetComponent<CompanionInventory>();
            int items = 0, worn = 0;
            if (inv != null)
            {
                var store = inv.GetInventory();
                store.RemoveAll();
                var all = GodProfile.ItemCopies(out var equipped);
                foreach (var it in all) if (store.AddItem(it)) items++;
                foreach (var it in equipped)
                {
                    var slot = CompanionInventory.GetSlotForItem(it);
                    if (slot.HasValue && inv.EquipItem(slot.Value, it)) worn++;
                }
            }
            var sk = c.GetComponent<CompanionSkills>();
            if (sk != null) foreach (var kv in GodProfile.SkillLevels()) sk.SetSkillLevel(kv.Key, kv.Value);
            RegisterClassBody(c, true);
            c.GetComponent<CompanionStats>()?.RecalculateMaxStats();
            FiresLogger.LogDebug($"[StudioClone] resynced from the god character: {items} item copies, {worn} equipped");
            return Pose();
        }

        /// <summary>One input frame: {"buttons":["Forward","Run","Attack",...],"mouseX":px,"mouseY":px} - browser PIXELS, turned with the
        /// game's mouse sensitivity. No frame for 250 ms = idle; none for 30 s = despawn.</summary>
        public static string Drive(string frameJson)
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            var d = c.GetComponent<StudioCloneDriver>();
            if (d == null) return Refuse("no driver");
            d.Take(frameJson ?? "");
            s_lastFrame = Time.time;
            return "{\"ok\":true}";
        }

        // ---- admin flight (Fire 10-30: "our own admin flight that works exactly like the admin player flight from vanilla ... Shift up,
        // Ctrl down" + "put the god client into noclip so that they can fly through things"). Vanilla's debug fly is Character code
        // (Character.UpdateDebugFly: no gravity, no fall / no damage, m_debugFlySpeed, x2.5 with run); only its switch, IsDebugFlying(),
        // is Player-only. So the clone uses vanilla's own: IsDebugFlying() answers true for it while flying, and the drive frame's
        // "Up" / "Down" (the web maps Shift / Ctrl) set the vertical at vanilla's speed - the keys vanilla reads are the local player's.
        // Noclip: the clone's solid colliders are off while flying and restored exactly after, then it lands on the ground under it.
        internal static bool s_flying;
        private static readonly List<Collider> s_noclip = new List<Collider>();

        public static string SetFly(bool on)
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            if (on == s_flying) return Pose();
            s_flying = on;
            int n = 0; string landed = "";
            if (on)
            {
                s_noclip.Clear();
                foreach (var col in c.GetComponentsInChildren<Collider>(true))
                    if (col != null && col.enabled && !col.isTrigger) { col.enabled = false; s_noclip.Add(col); n++; }
            }
            else
            {
                foreach (var col in s_noclip) if (col != null) { col.enabled = true; n++; }
                s_noclip.Clear();
                var body = c.GetComponent<Rigidbody>();
                if (body != null) { body.useGravity = true; body.linearVelocity = Vector3.zero; }
                // land safely: never left inside terrain or a piece
                var p = c.transform.position;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(p, out float ground) && p.y < ground + 0.2f)
                {
                    c.transform.position = new Vector3(p.x, ground + 0.2f, p.z);
                    landed = $", snapped up {ground + 0.2f - p.y:0.0} m to the ground";
                }
            }
            c.GetComponent<ZNetView>()?.GetZDO()?.Set(FlyKey, on);
            FiresLogger.LogInfo($"[StudioClone] admin flight {(on ? "ON" : "OFF")}, noclip {(on ? "ON" : "OFF")} ({n} collider(s) {(on ? "off" : "restored")}){landed}");
            return Pose();
        }

        public const string FlyKey = "studio_fly";

        [HarmonyPatch(typeof(Character), nameof(Character.IsDebugFlying))]
        private static class Character_IsDebugFlying_CloneFly
        {
            private static void Postfix(Character __instance, ref bool __result)
            {
                if (!__result && s_flying && s_clone != null && __instance != null && __instance.gameObject == s_clone.gameObject) __result = true;
            }
        }

        private static readonly AccessTools.FieldRef<Character, Vector3> s_currentVel = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> s_body = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<Character, bool> s_run = AccessTools.FieldRefAccess<Character, bool>("m_run");

        // after vanilla's UpdateDebugFly: the vertical from the drive frame (Up / Down) at vanilla's own speed and smoothing
        [HarmonyPatch(typeof(Character), "UpdateDebugFly")]
        private static class Character_UpdateDebugFly_CloneUpDown
        {
            private static void Postfix(Character __instance)
            {
                if (!s_flying || s_clone == null || __instance == null || __instance.gameObject != s_clone.gameObject) return;
                var d = s_clone.GetComponent<StudioCloneDriver>();
                int dir = d != null ? d.FlyVertical : 0;
                float speed = s_run(__instance) ? Character.m_debugFlySpeed * 2.5f : Character.m_debugFlySpeed;
                Vector3 v = s_currentVel(__instance);
                v.y = Mathf.Lerp(v.y, dir * speed, 0.5f);
                s_currentVel(__instance) = v;
                var body = s_body(__instance);
                if (body != null) body.linearVelocity = v;
            }
        }

        public static string SetGhost(bool on)
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            c.GetComponent<ZNetView>().GetZDO().Set(GhostKey, on);
            FiresLogger.LogDebug($"[StudioClone] ghost {(on ? "ON: hidden from enemies and players" : "OFF")}");
            return Pose();
        }

        /// <summary>Keeps the clone (the idle safety net counts from the last Drive / Pose / Touch); FGC may call it while its session lives.</summary>
        public static string Touch()
        {
            if (s_clone == null) return Refuse("no clone");
            s_lastFrame = Time.time;
            return "{\"ok\":true}";
        }

        public static string Pose()
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            s_lastFrame = Time.time;   // the Studio reading the pose = its session is alive
            var ch = c.GetComponent<Character>();
            var d = c.GetComponent<StudioCloneDriver>();
            var nv = c.GetComponent<ZNetView>();
            var p = c.transform.position; var v = ch != null ? ch.GetVelocity() : Vector3.zero;
            var sb = new StringBuilder(400);
            sb.Append("{\"ok\":true,\"zdoid\":").Append(Q(nv != null && nv.IsValid() ? nv.GetZDO().m_uid.UserID.ToString(Inv) + ":" + nv.GetZDO().m_uid.ID.ToString(Inv) : ""))
              .Append(",\"pos\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(']')
              .Append(",\"yaw\":").Append(F(d != null ? d.Yaw : c.transform.eulerAngles.y)).Append(",\"pitch\":").Append(F(d != null ? d.Pitch : 0f))
              .Append(",\"vel\":[").Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(']');
            if (ch != null)
                sb.Append(",\"onGround\":").Append(ch.IsOnGround() ? "true" : "false").Append(",\"swimming\":").Append(ch.IsSwimming() ? "true" : "false")
                  .Append(",\"hp\":").Append(F(ch.GetHealth())).Append(",\"maxHp\":").Append(F(ch.GetMaxHealth())).Append(",\"dead\":").Append(ch.IsDead() ? "true" : "false");
            var st = c.GetComponent<CompanionStats>();
            if (st != null) sb.Append(",\"stamina\":").Append(F(st.CurrentStamina)).Append(",\"maxStamina\":").Append(F(st.MaxStamina));
            sb.Append(",\"ghost\":").Append(nv != null && nv.IsValid() && nv.GetZDO().GetBool(GhostKey) ? "true" : "false");
            sb.Append(",\"fly\":").Append(s_flying ? "true" : "false");
            return sb.Append('}').ToString();
        }

        // ---- class combat (Fire 10-30: the god clone fights as its class) through RPGClasses' own code, by reflection (Core has no
        // reference to it): ClassCaster.CastFor (its caster-generic cast path), ClassBodies.Register / Unregister / PoolBonus. ----
        private static bool s_classResolved;
        private static System.Reflection.MethodInfo s_castFor, s_register, s_unregister, s_poolBonus;
        private static void ResolveClass()
        {
            if (s_classResolved) return;
            s_classResolved = true;
            s_castFor = FiresCore.Compat.OptionalTypes.Method("FiresRPGClasses.Combat.ClassCaster:CastFor");
            s_register = FiresCore.Compat.OptionalTypes.Method("FiresRPGClasses.Combat.ClassBodies:Register");
            s_unregister = FiresCore.Compat.OptionalTypes.Method("FiresRPGClasses.Combat.ClassBodies:Unregister");
            s_poolBonus = FiresCore.Compat.OptionalTypes.Method("FiresRPGClasses.Combat.ClassBodies:PoolBonus");
            FiresLogger.LogDebug($"[StudioClone] RPGClasses hooks: CastFor {(s_castFor != null ? "yes" : "no")}, ClassBodies {(s_register != null ? "yes" : "no")}, PoolBonus {(s_poolBonus != null ? "yes" : "no")}");
        }

        /// <summary>Casts a class skill from the clone: what = a bar slot "2".."8" or a node id; target = "" / "x,y,z" / "zdo:u:i".</summary>
        public static string Cast(string what, string target)
        {
            var c = s_clone;
            if (c == null) return Refuse("no clone");
            if (!GodProfile.Loaded) return Refuse("god character not loaded");
            ResolveClass();
            if (s_castFor == null) return Refuse("RPGClasses ClassCaster.CastFor not loaded on the server");
            try { return (string)s_castFor.Invoke(null, new object[] { c.GetComponent<Character>(), GodProfile.Shadow.m_customData, what ?? "", target ?? "" }); }
            catch (Exception ex) { return Refuse("CastFor threw: " + (ex.InnerException ?? ex).Message); }
        }

        private static void RegisterClassBody(CompanionController c, bool on)
        {
            ResolveClass();
            var body = c != null ? c.GetComponent<Character>() : null;
            if (body == null) return;
            try
            {
                if (on && s_register != null && GodProfile.Loaded) s_register.Invoke(null, new object[] { body, GodProfile.Shadow.m_customData });
                else if (!on && s_unregister != null) s_unregister.Invoke(null, new object[] { body });
            }
            catch (Exception ex) { FiresLogger.LogDebug("[StudioClone] ClassBodies call threw: " + (ex.InnerException ?? ex).Message); }
        }

        /// <summary>CompanionStats.RecalculateMaxStats: adds the clone's class pool bonuses (RPGClasses ClassBodies.PoolBonus).</summary>
        internal static void AddClassPools(GameObject go, ref float health, ref float stamina, ref float eitr)
        {
            if (go == null || s_clone == null || s_clone.gameObject != go) return;
            ResolveClass();
            if (s_poolBonus == null) return;
            var args = new object[] { go.GetComponent<Character>(), 0f, 0f, 0f, 0f, 0f };
            try { s_poolBonus.Invoke(null, args); } catch { return; }
            health += (float)args[1]; stamina += (float)args[2]; eitr += (float)args[3];
        }

        internal static bool IsGhost(Character ch)
        {
            if (ch == null) return false;
            var nv = ch.GetComponent<ZNetView>();
            return nv != null && nv.IsValid() && nv.GetZDO().GetBool(GhostKey);
        }

        // Proof per hit ([lead] 10-30: attack + never-die log-proven): vanilla's Character.ApplyDamage, where a hit lands (the target's
        // owner), one Debug line when the clone is the attacker or the target: attacker, target, damage, hp after.
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        private static class Character_ApplyDamage_CloneHits
        {
            private static void Prefix(Character __instance, out float __state) { __state = __instance != null ? __instance.GetHealth() : 0f; }
            private static void Postfix(Character __instance, HitData hit, float __state)
            {
                var c = s_clone;
                if (c == null || __instance == null || hit == null) return;
                var clone = c.GetComponent<Character>();
                var attacker = hit.GetAttacker();
                bool cloneHits = attacker != null && attacker == clone, cloneHit = __instance == clone;
                if (!cloneHits && !cloneHit) return;
                FiresLogger.LogDebug($"[StudioClone] hit: {(attacker != null ? attacker.GetHoverName() : "?")} -> {__instance.GetHoverName()}"
                    + $" {hit.GetTotalDamage():0.#} dmg, hp {__state:0.#} -> {__instance.GetHealth():0.#}/{__instance.GetMaxHealth():0.#}{(__instance.IsDead() ? " DEAD" : "")}");
            }
        }

        // Ghost ON: no AI picks the clone as an enemy (vanilla's own enemy test, every AI goes through it).
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) })]
        private static class BaseAI_IsEnemy_Ghost
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                if (__result && (IsGhost(a) || IsGhost(b))) __result = false;
            }
        }

        private static Vector3 Parse(string at, out bool ok)
        {
            ok = false;
            var parts = (at ?? "").Split(',');
            if (parts.Length != 3) return Vector3.zero;
            if (float.TryParse(parts[0], NumberStyles.Float, Inv, out float x) && float.TryParse(parts[1], NumberStyles.Float, Inv, out float y)
                && float.TryParse(parts[2], NumberStyles.Float, Inv, out float z)) { ok = true; return new Vector3(x, y, z); }
            return Vector3.zero;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static string Refuse(string why)
        {
            FiresLogger.LogDebug("[StudioClone] refused: " + why);
            return "{\"ok\":false,\"refused\":" + Q(why) + "}";
        }
        private static string F(float v) => v.ToString("0.###", Inv);
        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Drives the server-held clone with vanilla Humanoid calls (SetMoveDir / SetLookDir / SetRun / SetCrouch / Jump / StartAttack /
    /// blocking), from the latest FGC frame; keeps its zones loaded (ServerHeldPositions) and hides it on every peer while ghosted.
    /// </summary>
    internal sealed class StudioCloneDriver : MonoBehaviour
    {
        private Humanoid _h; private ZNetView _nv;
        private readonly HashSet<string> _held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float _mx, _my, _frameAt;
        private bool _hidden, _wasAttack, _wasSecondary, _wasJump;
        private readonly bool[] _skillWas = new bool[9];
        public float Yaw, Pitch;
        /// <summary>Admin flight vertical from the last frame: +1 up ("Jump"), -1 down ("Crouch"), 0 none.</summary>
        public int FlyVertical;
        private Vector3 _look = Vector3.forward, _move;
        private bool _run;

        /// <summary>The frame's controls onto the body (also called right before vanilla's movement physics, so no other writer wins).</summary>
        internal void ApplyControls()
        {
            if (_h == null) return;
            _h.SetLookDir(_look);
            _h.SetMoveDir(_move);
            _h.SetRun(_run);
        }
        private static readonly AccessTools.FieldRef<Character, bool> s_blocking = AccessTools.FieldRefAccess<Character, bool>("m_blocking");

        private void Awake() { _h = GetComponent<Humanoid>(); _nv = GetComponent<ZNetView>(); Yaw = transform.eulerAngles.y; }

        internal void Take(string json)
        {
            _held.Clear();
            int b = json.IndexOf("\"buttons\"", StringComparison.Ordinal);
            if (b >= 0)
            {
                int s = json.IndexOf('[', b), e = s >= 0 ? json.IndexOf(']', s) : -1;
                if (s >= 0 && e > s)
                    foreach (var raw in json.Substring(s + 1, e - s - 1).Split(','))
                    { var n = raw.Trim().Trim('"'); if (n.Length > 0) _held.Add(n); }
            }
            _mx += Num(json, "mouseX"); _my += Num(json, "mouseY");
            _frameAt = Time.time;
        }

        private void Update()
        {
            if (_nv == null || !_nv.IsValid()) return;
            bool ghost = _nv.GetZDO().GetBool(StudioClone.GhostKey);
            if (ghost != _hidden) { _hidden = ghost; foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = !ghost; }
            if (!_nv.IsOwner() || _h == null) return;
            if (StudioClone.s_clone == null || StudioClone.s_clone.gameObject != gameObject) return;
            var list = TamedCompanionZoneLoader.ServerHeldPositions;
            list.Clear(); list.Add(transform.position);
            // Fire 10-30: "We don't need to be removed every time we go AFK." The clone stays while its Studio session lives (FGC calls
            // Despawn on walk.end / session end); this is only the safety net, [Studio] "Clone idle despawn minutes" (FGC owns the key,
            // Core reads it via CoreSettings; 30 min without one Drive / Pose / Touch call).
            float idleMin = FiresCore.Config.CoreSettings.Get("Studio", "Clone idle despawn minutes", 30f);
            if (idleMin > 0f && Time.time - StudioClone.s_lastFrame > idleMin * 60f) { StudioClone.Despawn($"idle {idleMin:0.#} min (no Studio call)"); return; }

            float sens = PlayerController.m_mouseSens;
            Yaw += _mx * sens; Pitch = Mathf.Clamp(Pitch - _my * sens, -89f, 89f); _mx = _my = 0f;
            bool live = Time.time - _frameAt <= 0.25f;
            Quaternion rot = Quaternion.Euler(0f, Yaw, 0f);
            Vector3 move = Vector3.zero;
            if (live)
            {
                if (_held.Contains("Forward")) move += Vector3.forward;
                if (_held.Contains("Backward")) move += Vector3.back;
                if (_held.Contains("Left")) move += Vector3.left;
                if (_held.Contains("Right")) move += Vector3.right;
            }
            _look = Quaternion.Euler(Pitch, Yaw, 0f) * Vector3.forward;
            _move = move.sqrMagnitude > 0f ? rot * move.normalized : Vector3.zero;
            _run = live && _held.Contains("Run");
            ApplyControls();
            bool flying = StudioClone.s_flying;
            // admin flight: "Jump" = up, "Crouch" = down (vanilla debug fly's own up / down inputs), read by UpdateDebugFly's postfix
            FlyVertical = !flying || !live ? 0 : _held.Contains("Jump") ? 1 : _held.Contains("Crouch") ? -1 : 0;
            _h.SetCrouch(!flying && live && _held.Contains("Crouch"));
            s_blocking(_h) = live && _held.Contains("Block");
            bool jump = !flying && live && _held.Contains("Jump"), atk = live && _held.Contains("Attack"), sec = live && _held.Contains("SecondaryAttack");
            if (jump && !_wasJump) _h.Jump();
            if (atk && !_wasAttack) LogAttack(false, _h.StartAttack(null, false));
            if (sec && !_wasSecondary) LogAttack(true, _h.StartAttack(null, true));
            _wasJump = jump; _wasAttack = atk; _wasSecondary = sec;
            // class skills: "Skill2".."Skill8" = the active bar's slots, cast once per press at the nearest enemy / self (RPGClasses picks)
            for (int s = 2; s <= 8; s++)
            {
                bool down = live && _held.Contains("Skill" + s);
                if (down && !_skillWas[s]) StudioClone.Cast(s.ToString(), "");
                _skillWas[s] = down;
            }
        }

        private void LogAttack(bool secondary, bool started)
        {
            var w = _h.GetCurrentWeapon();
            FiresLogger.LogDebug($"[StudioClone] attack {(secondary ? "secondary" : "primary")} with {(w != null && w.m_dropPrefab != null ? w.m_dropPrefab.name : "fists")}: {(started ? "started" : "refused (vanilla StartAttack said no)")}");
        }

        private static float Num(string json, string key)
        {
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return 0f;
            int c = json.IndexOf(':', k); if (c < 0) return 0f;
            int e = c + 1; while (e < json.Length && "0123456789+-.eE ".IndexOf(json[e]) >= 0) e++;
            return float.TryParse(json.Substring(c + 1, e - c - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        }
    }
}
