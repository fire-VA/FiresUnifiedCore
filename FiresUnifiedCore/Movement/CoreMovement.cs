using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// Core's shared movement extras.
    /// - Double jump: Jump again in the air -> a second jump (the registered "second jump" style if one applies, else a plain one).
    ///   Gate: vanilla Jump skill >= [Movement] "Double jump at Jump skill" (30), or any mod's gate. Cost: stamina.
    /// - Run slide: tap Crouch while sprinting -> a ~1 s low slide with momentum, then stand. Gate: vanilla Run skill >= "Run slide at Run
    ///   skill" (25), or any mod's gate. Cost: stamina.
    /// - Movement styles: the registry (MovementRegistry.cs) for class / race second-jump and dodge styles.
    /// All under [Movement], tri-state Auto/On/Off (MovementFeature; Auto = only once a consumer registered) behind the master switch.
    /// "[Movement] Unlock all" (synced, admin, default false) opens every gate for tests.
    /// STAND-DOWN (disclaim, never block): when a plugin in "[Movement] Stand down for plugins" is loaded, the double jump and the run slide
    /// do nothing here and log it once. Default "Pix.Movement" = PIXPIX "Movement" 0.7.3, which has its own Slide / Double Jump / Blink
    /// (GUID read from its DLL's [BepInPlugin("Pix.Movement", "Movement", "0.7.3")], source https://thunderstore.io/c/valheim/p/PIXPIX/Movement ,
    /// download https://thunderstore.io/package/download/PIXPIX/Movement/0.7.3/ ). Add the user's other "parkour" mod's GUID there.
    /// Refuses while climbing, crawling, swimming, attached, flying (debugfly), dead or teleporting. Physics is owner-side (vanilla syncs
    /// position); visuals go through ClipSlots when present (by reflection until that Core lands). Jump height / slide speed x FCC size.
    /// </summary>
    public static partial class CoreMovement   // 0.2.92x: partial (CoreMovement.Characters.cs adds the companion cores)
    {
        public static readonly MovementFeature DoubleJumpFeature = new MovementFeature("Double jump");
        public static readonly MovementFeature RunSlideFeature = new MovementFeature("Run slide");
        public static readonly MovementFeature Styles = new MovementFeature("Movement styles");

        // 1086 ([generator], Fire 11-01: configs live in their own mods): [Movement] is bound by Fires Advanced Player Movement; Core reads it (old defaults as fallback)
        private static readonly FiresCore.Config.OwnedSetting<bool> s_unlockAll = new FiresCore.Config.OwnedSetting<bool>(MovementFeature.Section, "Unlock all", false);
        private static readonly FiresCore.Config.OwnedSetting<int> s_jumpSkill = new FiresCore.Config.OwnedSetting<int>(MovementFeature.Section, "Double jump at Jump skill", 30),
            s_runSkill = new FiresCore.Config.OwnedSetting<int>(MovementFeature.Section, "Run slide at Run skill", 25);
        private static readonly FiresCore.Config.OwnedSetting<float> s_jumpStamina = new FiresCore.Config.OwnedSetting<float>(MovementFeature.Section, "Double jump stamina", 10f),
            s_slideStamina = new FiresCore.Config.OwnedSetting<float>(MovementFeature.Section, "Run slide stamina", 8f),
            s_slideSeconds = new FiresCore.Config.OwnedSetting<float>(MovementFeature.Section, "Run slide seconds", 1.0f),
            s_slideSpeed = new FiresCore.Config.OwnedSetting<float>(MovementFeature.Section, "Run slide speed", 1.25f);
        private static readonly FiresCore.Config.OwnedSetting<string> s_standDown = new FiresCore.Config.OwnedSetting<string>(MovementFeature.Section, "Stand down for plugins", "Pix.Movement");

        private const float DefaultPuffScale = 0.12f;
        private const float MinSizeScale = 0.25f, MaxSizeScale = 4f;
        private const float RefusalLogIntervalSeconds = 2f;
        private const float DoubleJumpForceShare = 0.9f;       // the second jump is a little weaker than the ground jump
        private const float PuffLifetimeSeconds = 3f, SfxLifetimeSeconds = 5f;
        private const float MinSlideSeconds = 0.1f;

        public static bool UnlockAll => s_unlockAll != null && s_unlockAll.Value;

        internal static MovementFeature FeatureFor(string move) =>
            move == MovementGates.DoubleJump ? DoubleJumpFeature : move == MovementGates.RunSlide ? RunSlideFeature : Styles;

        private static ConfigDescription Adv(string text) => new ConfigDescription(text, null, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true });

        public static void Bind(ConfigFile cfg, FiresCore.Sync.ConfigSync configSync)
        {
            MovementFeature.BindMaster(cfg);
            DoubleJumpFeature.Bind(cfg, "Jump again in the air for a second jump (stamina), once unlocked by the Jump skill or a class / race.");
            RunSlideFeature.Bind(cfg, "Tap Crouch while sprinting to slide low for about a second (stamina), once unlocked by the Run skill or a class.");
            Styles.Bind(cfg, "Class and race styles for the second jump and the dodge (flips, blinks, mist-steps), registered by those mods.");
            // 1086 ([generator], Fire 11-01: configs live in their own mods): no Core binds or sync registration (the owner syncs); the features only hook their owner value

            // Core's own gates: vanilla skills only. Owner "Core" does not count as a consumer, so Auto stays off without one.
            MovementGates.Register(MovementGates.DoubleJump, "core.jump_skill", p => p.GetSkillLevel(Skills.SkillType.Jump) >= s_jumpSkill.Value, "Double jump (Jump skill, [Movement] Double jump at Jump skill)", "Core");
            MovementGates.Register(MovementGates.RunSlide, "core.run_skill", p => p.GetSkillLevel(Skills.SkillType.Run) >= s_runSkill.Value, "Run slide (Run skill, [Movement] Run slide at Run skill)", "Core");
            // Run slide: with only a double jump consumer registered,
            // "Run slide = Auto" stayed off and TrySlide returned before any log. The slide is a Core feature with
            // its own vanilla Run-skill gate and RPGClasses ships its clips, so Core registers itself as its consumer - Auto = active
            // (Off still turns it off; a stand-down plugin still wins).
            // 0.2.1036 (plan step 1, Fire: Core alone changes nothing): Core no longer registers itself; FiresRPGClasses (it ships the
            // slide's clips) registers as the run slide's consumer, so Auto turns it on only with RPGClasses installed
        }

        // ── stand-down ────────────────────────────────────────────────────────────────────────────────────────────────────
        private static int s_standResolved = -1;
        internal static void ResetStandDown() { s_standResolved = -1; s_standBy = null; }   // 0.2.1069
        private static string s_standBy;

        public static bool StoodDown
        {
            get
            {
                if (s_standResolved < 0)
                {
                    s_standResolved = 1;
                    var guids = (s_standDown.Value ?? "").Split(',').Select(g => g.Trim()).Where(g => g.Length > 0);
                    foreach (string g in guids)
                        if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(g, out var info)) { s_standBy = $"{info?.Metadata?.Name} ({g})"; break; }
                    if (s_standBy != null) FiresCore.Logging.FiresLogger.LogDebug($"[Movement] double jump + run slide: skipped, {s_standBy} installed (its own moves are used)");
                }
                return s_standBy != null;
            }
        }

        // ── refusals (one place) ──────────────────────────────────────────────────────────────────────────────────────────
        private static string RefusalReason(Player p)
        {
            if (p == null || p.IsDead()) return "dead";
            if (p.IsTeleporting()) return "teleporting";
            if (Climb.IsClimbing(p)) return "climbing";
            if (Crawl.Active) return "crawling";
            if (p.IsSwimming()) return "swimming";
            if (p.IsAttached()) return "attached";
            if (p.IsDebugFlying()) return "flying";
            if (p.InPlaceMode()) return "building";
            return null;
        }

        private static float SizeScale(Player p) => Mathf.Clamp(p.transform.localScale.y, MinSizeScale, MaxSizeScale);   // FCC size, as the climb bands

        // 1074 ([generator] event-driven/cache): m_body via VanillaRefs typed delegate (FieldInfo fallback inside).
        private static Rigidbody GetBody(Player p) => FiresCore.Utilities.VanillaRefs.Body(p);

        // ── ClipSlots by reflection until that Core build is live (Tools\CORE_CLIP_SLOT_API.md); false = keep the vanilla anim ──
        internal static bool PlayClip(Character body, string id) => FiresCore.Animation.ClipSlots.IsRegistered(id) && FiresCore.Animation.ClipSlots.Play(body, id);   // Same Core

        // ── per-frame input (the local player only; owner-side physics) ───────────────────────────────────────────────────
        private static bool s_airJumpUsed;
        private static float s_slideUntil;
        private static Vector3 s_slideDir;
        private static float s_slideStartSpeed;
        private static float s_nextRefusalLog;

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !__instance.TakeInput()) return;
                Player p = __instance;
                if (p.IsOnGround()) s_airJumpUsed = false;
                TickSlide(p);
                if (ZInput.GetButtonDown("Jump") || ZInput.GetButtonDown("JoyJump")) TryDoubleJump(p);
                if ((ZInput.GetButtonDown("Crouch") || ZInput.GetButtonDown("JoyCrouch")) && p.IsRunning() && p.IsOnGround()) TrySlide(p);
            }
        }

        private static void LogRefusal(string move, Player p, string why)
        {
            if (Time.time < s_nextRefusalLog) return;
            s_nextRefusalLog = Time.time + RefusalLogIntervalSeconds;
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] {move} by {p.GetPlayerName()}: refused ({why})");
        }

        private static void TryDoubleJump(Player p)
        {
            if (p.IsOnGround() || s_airJumpUsed || !DoubleJumpFeature.IsActive || StoodDown) return;
            string why = RefusalReason(p);
            if (why != null) { LogRefusal("double jump", p, why); return; }
            if (!MovementGates.Unlocked(p, MovementGates.DoubleJump, out string gate)) return;
            float cost = s_jumpStamina.Value;
            if (!p.HaveStamina(cost)) { LogRefusal("double jump", p, $"stamina {p.GetStamina():0} < {cost:0}"); return; }
            s_airJumpUsed = true;
            p.UseStamina(cost);
            if (MovementStyles.TryPerform(p, MovementGates.SecondJumpStyle, out string style))
            {
                DoubleJumpFx(p);
                FiresCore.Logging.FiresLogger.LogDebug($"[Movement] double jump by {p.GetPlayerName()}: gate {gate}, style {style}, {cost:0} stamina");
                return;
            }
            var rb = GetBody(p);
            if (rb == null) return;
            Vector3 v = rb.velocity;
            v.y = Mathf.Max(v.y, 0f) + p.m_jumpForce * SizeScale(p) * DoubleJumpForceShare;
            rb.velocity = v;
            bool clip = PlayClip(p, "core.double_jump");
            if (!clip) p.m_zanim?.SetTrigger("jump");
            DoubleJumpFx(p);
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] double jump by {p.GetPlayerName()}: gate {gate} ({(gate == "core.jump_skill" ? $"Jump skill {p.GetSkillLevel(Skills.SkillType.Jump):0}" : gate)}), style generic, up {v.y:0.0}, {cost:0} stamina, anim {(clip ? "clip slot" : "vanilla jump")}");
        }

        // Vanilla's own jump effect list (the
        // dust burst + jump sound at the feet), created by the jumping player's client like vanilla's ground jump; the effect prefabs carry
        // their own ZNetView, so other players see and hear it. Once per double jump; the ground jump is untouched.
        private static bool s_jumpFxLogged;
        private static void DoubleJumpFx(Player p)
        {
            try
            {
                EffectList fx = p.m_jumpEffects;
                if (!s_jumpFxLogged)
                {
                    s_jumpFxLogged = true;
                    var names = new System.Collections.Generic.List<string>();
                    if (fx?.m_effectPrefabs != null) foreach (var e in fx.m_effectPrefabs) if (e?.m_prefab != null && e.m_enabled) names.Add(e.m_prefab.name);
                    if (names.Count == 0) FiresCore.Logging.FiresLogger.LogWarning("[Movement] double jump fx: the player has no jump effects; the double jump plays without sfx/vfx");
                    else FiresCore.Logging.FiresLogger.LogInfo($"[Movement] double jump fx: vanilla jump effects at the feet ({string.Join(", ", names.ToArray())})");
                }
                fx?.Create(p.transform.position, p.transform.rotation, p.transform);
                // The player's jump effects are sound only
                // (sfx_jump + sfx_jump_vibration_only), so a dust puff plays at the feet on EVERY peer (a routed RPC; a vfx prefab has
                // no ZNetView, so a local spawn would show for the jumper only)
                EnsureFxRpc();
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, FxRpc, p.transform.position);
            }
            catch (System.Exception e) { FiresCore.Logging.FiresLogger.LogWarning("[Movement] double jump fx failed: " + e.Message); }
        }

        private const string FxRpc = "Core_DoubleJumpFx";
        private static readonly FiresCore.Config.OwnedSetting<float> s_puffScale = new FiresCore.Config.OwnedSetting<float>(MovementFeature.Section, "Double jump puff scale", DefaultPuffScale);   // 1086 ([generator], Fire 11-01: configs live in their own mods): owner FAPM
        private static readonly FiresCore.Config.OwnedSetting<string> s_jumpSound = new FiresCore.Config.OwnedSetting<string>(MovementFeature.Section, "Double jump sound", "sfx_dodge");
        private static GameObject s_sfx; private static string s_sfxFor;
        // fallbacks when the configured name is missing: airy whooshes first
        private static readonly string[] SfxNames = { "sfx_dodge", "sfx_bow_draw", "sfx_jump" };
        private static ZRoutedRpc s_fxRpcOn;   // registered per ZRoutedRpc instance (a new world brings a new one)
        private static GameObject s_puff;
        private static bool s_puffResolved;
        // vanilla ground puffs, first found wins (logged once with found / MISSING)
        private static readonly string[] PuffNames = { "vfx_dodge", "vfx_sledge_hit", "vfx_stone_hit", "vfx_HitSparks" };

        private static void EnsureFxRpc()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(s_fxRpcOn, ZRoutedRpc.instance)) return;
            s_fxRpcOn = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<Vector3>(FxRpc, (sender, at) => SpawnPuff(at));
        }

        private static void SpawnPuff(Vector3 at)
        {
            if (!s_puffResolved)
            {
                s_puffResolved = true;
                Player me = Player.m_localPlayer;
                if (me != null && me.m_dodgeEffects?.m_effectPrefabs != null)
                    foreach (var e in me.m_dodgeEffects.m_effectPrefabs)
                        if (e?.m_prefab != null && e.m_enabled && e.m_prefab.name.StartsWith("vfx", System.StringComparison.OrdinalIgnoreCase)) { s_puff = e.m_prefab; break; }
                if (s_puff == null && ZNetScene.instance != null)
                    foreach (string n in PuffNames) { s_puff = ZNetScene.instance.GetPrefab(n); if (s_puff != null) break; }
                FiresCore.Logging.FiresLogger.LogInfo($"[Movement] double jump fx: vfx '{(s_puff != null ? s_puff.name : PuffNames[0])}' ({(s_puff != null ? "found" : "MISSING - sound only")}) + vanilla jump sfx");
            }
            PlaySfx(at);
            if (s_puff == null) return;
            var go = UnityEngine.Object.Instantiate(s_puff, at, Quaternion.identity);
            // Footstep size - the transform and every particle system scale with the hierarchy
            float puffScale = s_puffScale != null ? s_puffScale.Value : DefaultPuffScale;
            go.transform.localScale = go.transform.localScale * puffScale;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
            if (go.GetComponent<TimedDestruction>() == null && go.GetComponent<ZNetView>() == null) UnityEngine.Object.Destroy(go, PuffLifetimeSeconds);
        }

        // The sound goes out with the puff RPC, so every peer plays it locally at the jumper's feet. Prefabs with a ZNetView are
        // skipped (a local Instantiate of one would make a networked object per peer).
        private static void PlaySfx(Vector3 at)
        {
            string want = s_jumpSound != null ? (s_jumpSound.Value ?? "").Trim() : "sfx_dodge";
            if (want.Length == 0 || ZNetScene.instance == null) return;
            if (s_sfxFor != want)
            {
                s_sfxFor = want; s_sfx = null;
                var tried = new System.Collections.Generic.List<string> { want };
                foreach (string f in SfxNames) if (!tried.Contains(f)) tried.Add(f);
                foreach (string name in tried)
                {
                    GameObject g = ZNetScene.instance.GetPrefab(name);
                    if (g != null && g.GetComponent<ZNetView>() == null) { s_sfx = g; break; }
                }
                FiresCore.Logging.FiresLogger.LogInfo($"[Movement] double jump sfx '{want}' {(s_sfx != null && s_sfx.name == want ? "found" : "MISSING")}{(s_sfx != null && s_sfx.name != want ? ", playing '" + s_sfx.name + "' instead" : "")}");
                var candidateNames = new System.Collections.Generic.List<string>();
                foreach (GameObject g in ZNetScene.instance.m_prefabs)
                {
                    if (g == null) continue; string lower = g.name.ToLowerInvariant();
                    if (lower.StartsWith("sfx_") && (lower.Contains("dodge") || lower.Contains("jump") || lower.Contains("wing") || lower.Contains("flap") || lower.Contains("whoosh") || lower.Contains("swish") || lower.Contains("glide") || lower.Contains("wind") || lower.Contains("feather"))) candidateNames.Add(g.name);
                }
                FiresCore.Logging.FiresLogger.LogInfo($"[Movement] double jump sfx candidates ({candidateNames.Count}): {string.Join(", ", candidateNames.ToArray())}");
            }
            if (s_sfx == null) return;
            var s = UnityEngine.Object.Instantiate(s_sfx, at, Quaternion.identity);
            if (s.GetComponent<TimedDestruction>() == null) UnityEngine.Object.Destroy(s, SfxLifetimeSeconds);
        }

        private static void TrySlide(Player p)
        {
            if (Time.time < s_slideUntil || !RunSlideFeature.IsActive || StoodDown) return;
            string why = RefusalReason(p);
            if (why != null) { LogRefusal("run slide", p, why); return; }
            if (!MovementGates.Unlocked(p, MovementGates.RunSlide, out string gate)) return;
            float cost = s_slideStamina.Value;
            if (!p.HaveStamina(cost)) { LogRefusal("run slide", p, $"stamina {p.GetStamina():0} < {cost:0}"); return; }
            p.UseStamina(cost);
            if (FiresCore.World.GravityUp.InField(p.transform.position, out Vector3 ringUp))
            {
                // halo ring: slide along the wall (the tangent plane), not along world XZ
                s_slideDir = Vector3.ProjectOnPlane(p.transform.forward, ringUp).normalized;
                if (!s_slideRingLogged) { s_slideRingLogged = true; FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide on the halo ring: direction in the tangent plane, up {ringUp:F2}"); }
            }
            else { s_slideDir = p.transform.forward; s_slideDir.y = 0f; s_slideDir.Normalize(); }
            s_slideStartSpeed = p.m_runSpeed * s_slideSpeed.Value * SizeScale(p);
            s_slideUntil = Time.time + s_slideSeconds.Value;
            bool clip = PlayClip(p, "core.run_slide");
            if (!clip) p.SetCrouch(true);
            if (clip) (p.GetComponent<SlideFootFix>() ?? p.gameObject.AddComponent<SlideFootFix>()).Begin(s_slideUntil);
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide by {p.GetPlayerName()}: gate {gate}, {s_slideSeconds.Value:0.0} s at {s_slideStartSpeed:0.0} m/s, {cost:0} stamina, anim {(clip ? "clip slot" : "vanilla crouch")}");
        }

        private static void TickSlide(Player p)
        {
            if (s_slideUntil <= 0f) return;
            if (Time.time >= s_slideUntil || RefusalReason(p) != null)
            {
                s_slideUntil = 0f;
                p.SetCrouch(false);
                FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide by {p.GetPlayerName()}: ended");
                return;
            }
            var rb = GetBody(p);
            if (rb == null) return;
            float left = Mathf.Clamp01((s_slideUntil - Time.time) / Mathf.Max(MinSlideSeconds, s_slideSeconds.Value));
            Vector3 v = s_slideDir * Mathf.Lerp(p.m_walkSpeed, s_slideStartSpeed, left);
            if (FiresCore.World.GravityUp.InField(p.transform.position, out Vector3 ringUp))
                v += ringUp * Vector3.Dot(rb.velocity, ringUp);   // halo ring: keep the fall along the local up
            else
                v.y = rb.velocity.y;
            rb.velocity = v;
        }
        private static bool s_slideRingLogged;

        /// <summary>
        /// Fire 10-24: "our slide animation holds the player up off the ground a bit too high, its offset is about 1m high". The slide
        /// leaves the collider alone (velocity only) and plays FiresRPGClasses' Slide clip with root motion off, so the float is the
        /// clip's own hips height. After the animator each frame, the lowest foot is measured against the ground under the body and
        /// only the VISUAL is lowered by the gap (the capsule, and so the slide-under clearance, is untouched); restored when it ends.
        /// One Debug line per slide with the numbers.
        /// </summary>
        private sealed class SlideFootFix : MonoBehaviour
        {
            private float _until, _applied, _maxGap;
            private Transform _visual, _lFoot, _rFoot;
            private Vector3 _visualLocal;
            private bool _logged, _active;
            private static readonly int s_groundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "vehicle");

            public void Begin(float until)
            {
                _until = until; _logged = false; _maxGap = 0f;
                if (_visual == null)
                {
                    var p = GetComponent<Player>();
                    _visual = p != null && p.m_visual != null ? p.m_visual.transform : null;
                    if (_visual != null) foreach (var t in _visual.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == "LeftFoot") _lFoot = t; else if (t.name == "RightFoot") _rFoot = t;
                    }
                }
                if (_visual == null || _lFoot == null || _rFoot == null) { FiresCore.Logging.FiresLogger.LogDebug("[Movement] run slide foot fix: no visual / foot bones on this body; nothing done"); return; }
                if (!_active) { _visualLocal = _visual.localPosition; _applied = 0f; }
                _active = true;
            }

            private void LateUpdate()
            {
                if (!_active || _visual == null) return;
                if (Time.time >= _until) { End(); return; }
                // undo last frame's shift before measuring this frame's pose
                _visual.localPosition = _visualLocal;
                Vector3 at = transform.position;
                // halo ring: heights are measured along the local up (world up off the ring, the old maths exactly)
                Vector3 up = FiresCore.World.GravityUp.At(at);
                float foot = Mathf.Min(Vector3.Dot(_lFoot.position, up), Vector3.Dot(_rFoot.position, up));
                float ground = Physics.Raycast(at + up * 1f, -up, out RaycastHit hit, 4f, s_groundMask, QueryTriggerInteraction.Ignore) ? Vector3.Dot(hit.point, up) : Vector3.Dot(at, up);
                float gap = foot - ground - 0.08f;   // a foot bone sits ~8 cm above its sole
                _maxGap = Mathf.Max(_maxGap, gap);
                _applied = Mathf.Clamp(gap, 0f, 1.5f);
                if (_applied > 0.02f) _visual.position -= up * _applied;
                if (!_logged && Time.time > _until - 0.6f)
                {
                    _logged = true;
                    var cap = GetComponent<CapsuleCollider>();
                    FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide pose: lowest foot {foot - ground:0.00} m above ground (max {_maxGap:0.00}) -> visual lowered {_applied:0.00} m; body y {at.y:0.00}, ground {ground:0.00}, capsule height {(cap != null ? cap.height : 0f):0.00} centre {(cap != null ? cap.center.y : 0f):0.00} (unchanged)");
                }
            }

            private void End()
            {
                if (_visual != null) _visual.localPosition = _visualLocal;
                _active = false;
                FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide foot fix: visual back in place (largest gap this slide {_maxGap:0.00} m)");
            }

            private void OnDisable() { if (_active && _visual != null) _visual.localPosition = _visualLocal; _active = false; }
        }

        /// <summary>World load (ZNet.Start / Player.OnSpawned): the per-feature state lines.</summary>
        public static void LogStates()
        {
            DoubleJumpFeature.LogState(); RunSlideFeature.LogState(); Styles.LogState();
            if (StoodDown) { /* logged once inside StoodDown */ }
        }
    }
}
