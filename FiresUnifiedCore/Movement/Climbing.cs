using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>What a climb costs a body right now (Tools\CLIMBING.md §2b); a modifier may change any field.</summary>
    public struct ClimbTerms
    {
        /// <summary>Stamina per metre moved on a 38° face; steeper faces and Wet cost more (<see cref="Climbing.CostPerMetre"/>). Hanging still costs nothing.</summary>
        public float StaminaPerMetre;
        /// <summary>Metres per second along a 38° face; steeper faces are slower (<see cref="Climbing.SpeedAt"/>).</summary>
        public float Speed;
        /// <summary>The steepest face (degrees from level) this body climbs; 90 = vertical walls.</summary>
        public float MaxAngle;
        /// <summary>The chance per climb step (about 1.27 s) of slipping 0.25 m while Wet.</summary>
        public float WetSlipChance;
        public bool Allowed;
    }

    /// <summary>A mod adjusting climbing for a body (FiresRPGClasses' class passives, a grapple, a hardcore switch): <see cref="Climbing.AddModifier"/>.</summary>
    public interface IClimbModifier
    {
        void Modify(Character body, ref ClimbTerms terms);
    }

    /// <summary>The climb's two ints on the body's own ZDO, written by its owner and read on every peer (the animator, §1).</summary>
    public static class ClimbZdo
    {
        public const string State = "FiresClimb";
        public const string Clip = "FiresClimbClip";
        /// <summary>The network time a wet slip's slide effect runs until (float).</summary>
        public const string Slip = "fires_climb_slip";
        public static readonly int StateHash = State.GetStableHashCode(), ClipHash = Clip.GetStableHashCode(), SlipHash = Slip.GetStableHashCode();
    }

    /// <summary>Which clip a climbing body shows (FiresClimbClip), chosen by its owner.</summary>
    public enum ClimbClip { None = 0, Enter = 1, IdleLoop = 2, UpLoop = 3, DownLoop = 4, LeftLoop = 5, RightLoop = 6, Exit = 7, Ledge = 8, Up1m = 9, Up2m = 10, Vault = 11 }   // Vault (Malbers S_Vault_1M) - add-only (ZDO FiresClimbClip)

    /// <summary>
    /// Climbing (Tools\CLIMBING.md): bots climb steep cliffs instead of getting stuck, up to 90°, including
    /// building walls, guarded so that nothing
    /// starts climbing by accident when stuck. The terms, the faces, the plans; <see cref="Climb"/> is the state.
    /// start climbing on accident anytime they're stuck"). The terms, the faces, the plans; <see cref="Climb"/> is the state.
    /// Guards: a player climbs only on a deliberate input (Jump held while moving into the face, read from the real keyboard /
    /// pad, so a bot's virtual presses never start one); a bot or companion only on a PLANNED route step (<see cref="Climb.Plan"/>,
    /// from PathWalker or the swim exit: the better route to a real goal, the top known, the stamina for all of it with a margin),
    /// never as a stuck reflex. Every start is logged with its reason.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Climbing)]
    public static class Climbing
    {
        /// <summary>Below this a face is walked (and a player slides above it: Character.GetSlideAngle 38).</summary>
        public const float MinAngle = 38f;
        public const float BaseSpeed = 1.2f, BaseMaxAngle = 90f, BaseWetSlipChance = 0.15f;
        /// <summary>
        /// Sized so a starting
        /// character's 50-75 stamina gets it 8-10 m up a vertical face: stamina per metre moved at 38°, x VerticalCostFactor at 90°
        /// (5.6 a metre), x WetCostFactor while Wet. Paid for the metres moved, so hanging still is free.
        /// </summary>
        public const float BaseStaminaPerMetre = 3.7f, WetCostFactor = 1.5f;
        /// <summary>At 90° the speed is this share of the 38° speed and the cost this multiple (steeper = slower and costlier).</summary>
        public const float VerticalSpeedShare = 0.6f, VerticalCostFactor = 1.5f;
        /// <summary>
        /// A body that climbs vertical walls also climbs
        /// an overhang up to OverhangMaxAngle, slower (x OverhangSpeedShare at the limit) and dearer (x OverhangCostFactor).
        /// </summary>
        public const float OverhangMaxAngle = 115f, OverhangSpeedShare = 0.6f, OverhangCostFactor = 1.4f;
        private static float Overhang(float angle) => angle > 90f ? Mathf.InverseLerp(90f, OverhangMaxAngle, angle) : 0f;
        /// <summary>A climb doesn't start under this much stamina (players).</summary>
        public const float StartStamina = 15f;
        /// <summary>
        /// A planned climb no higher than this (m) is a mantle: up a hip-high ledge or out of water over a rim, like a jump (the
        /// jump apex is 1.6-1.8 m), so a swim's spent stamina never stops or drops it. 2.2 m covers a ledge grabbed from the water
        /// (a planned 2.2 m climb out of a pond otherwise ran out of stamina part-way).
        /// </summary>
        public const float MantleMaxHeight = 2.2f;
        /// <summary>A planned climb must leave this share of the body's max stamina in hand at the top.</summary>
        public const float PlanMargin = 0.2f;
        public const float WetSlipMetres = 0.25f, StepSeconds = 1.27f;
        /// <summary>How far ahead (m) a face is felt for.</summary>
        public const float FaceReach = 1.2f;
        /// <summary>A planned climb reaches at most this far (m, flat) and this high (m).</summary>
        public const float PlanRange = 30f, PlanMaxHeight = 30f;

        /// <summary>Knee, waist and chest heights (m, at size 1) of the rays that feel for a face.</summary>
        internal static readonly float[] FaceProbeHeights = { 0.4f, 1.0f, 1.6f };
        /// <summary>Slack (°) on every face-angle limit, so a face right at the limit still counts.</summary>
        internal const float AngleTolerance = 0.5f;
        /// <summary>A MaxAngle at or over this counts as "climbs vertical walls" (and so overhangs too).</summary>
        internal const float VerticalMaxAngle = 89.99f;
        /// <summary>The room-to-stand capsule: from knee to head height (m over the spot), and its radius.</summary>
        internal const float StandKneeHeight = 0.65f, StandHeadHeight = 1.6f, StandCapsuleRadius = 0.3f;
        /// <summary>A stand spot lies this far above the solid top found (m).</summary>
        internal const float SpotLift = 0.05f;
        /// <summary>A stand spot may be this far below the base / feet (m) at most.</summary>
        internal const float MaxSpotDrop = 0.3f;
        /// <summary>Water this far over a spot's top (m) makes it "under water".</summary>
        internal const float UnderWaterMargin = 0.1f;
        private const float MinSizeScale = 0.25f, MaxSizeScale = 4f;
        private const float FallbackJumpForce = 8f, MinGravity = 0.1f;
        private const float DefaultVaultMax = 2.25f, DefaultPullUpMax = 4f;
        private const float MinFaceSine = 0.2f;                 // floor of sin(angle) when turning a height into a face length
        private const float NpcStamina = 100f;                  // what a non-player body counts as its stamina
        // PlanClimb
        private const float PlanSampleStep = 0.5f, PlanMinGoalRise = 0.8f, PlanMaxDrop = 3f, PlanRayAbove = 40f, PlanRayLength = 120f, PlanTopInset = 0.6f;
        // PlanNearestWall
        private const float NearestWallStartRadius = 1.5f, NearestWallRadiusStep = 1f;
        // FindBank / BankReport / ProbeBank
        private const float BankGridStep = 2f, BankMinWaterDepth = 1f, BankProbeStep = 0.25f, BankPastMaxSlack = 2f;
        private const int BankDirections = 16;
        private const float BankIslandScore = 30f, BankTooHighScoreFactor = 10f, BankJumpableScoreFactor = 20f;
        private const float BankProbeDefaultRadius = 16f, BankProbeMinAngle = 45f, BankProbeMaxAngle = 90f;
        private const int BankProbeCells = 4;

        private static ConfigEntry<bool> s_pieces, s_playerInput;
        private static readonly List<IClimbModifier> s_modifiers = new List<IClimbModifier>();
        private static int s_mask, s_terrainMask;

        /// <summary>Whether climbing exists at all: Auto (on only beside FiresAdminTerrain or FiresRPGClasses), On, Off.</summary>
        public enum ClimbingMode { Auto, On, Off }

        private static ConfigEntry<ClimbingMode> s_mode;
        private static int s_resolvedFor = -1;   // the mode the cached answer belongs to (-1: not resolved yet)
        private static bool s_enabled;

        private const string AdminTerrainGuid = "com.Fire.FiresAdminTerrain", RpgClassesGuid = "com.Fire.FiresRPGClasses";

        /// <summary>
        /// True when climbing may start (off by default; Auto turns it on only
        /// with admin terrain or RPG classes installed). Auto reads the loaded plugins on first use, after every plugin has loaded.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                ClimbingMode mode = s_mode != null ? s_mode.Value : ClimbingMode.Auto;
                if (s_resolvedFor == (int)mode) return s_enabled;
                string why;
                if (mode == ClimbingMode.On) { s_enabled = true; why = "On (config)"; }
                else if (mode == ClimbingMode.Off) { s_enabled = false; why = "Off (config)"; }
                else
                {
                    // Auto = a consumer registered at runtime (MovementFeature.Register), OR a
                    // post-load owner (FAT / RPGClasses, read on first use after every plugin has loaded).
                    // The owners are CoreFeatures.Climbing's (FAT, RPGClasses, FCC): their presence is what applies the climb's patches.
                    var by = new List<string>();
                    foreach (string guid in FiresCore.Config.CoreFeatures.OwnersOf(FiresCore.Config.CoreFeatures.Climbing))
                        if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info)) by.Add("owner " + (info.Metadata?.Name ?? guid));
                    bool owned = by.Count > 0;
                    bool reg = MovementFeature.HasConsumer(MovementFeature.Climbing);
                    if (reg) by.Insert(0, "consumer " + MovementFeature.ConsumersOf(MovementFeature.Climbing));
                    // a runtime consumer without an owner can't climb: the patches were never applied (owner gating), so say so
                    s_enabled = owned;
                    if (reg && !owned) FiresCore.Logging.FiresLogger.LogWarning($"[Movement] Climbing: consumer {MovementFeature.ConsumersOf(MovementFeature.Climbing)} registered, but no climbing owner (FiresAdminTerrain / FiresRPGClasses / Fires Character Creator) is installed, so the climb's patches are not applied");
                    why = s_enabled ? "Auto -> active (" + string.Join(", ", by.ToArray()) + ")" : "Auto -> off (no consumer)";
                }
                // The [Movement] master switch overrides every mode.
                if (!MovementFeature.MasterOn) { s_enabled = false; why = "master OFF"; }
                else if (!MasterOn) { s_enabled = false; why = $"[{SplitSection}] master off"; }
                s_resolvedFor = (int)mode;
                FiresCore.Logging.FiresLogger.LogInfo($"[Movement] Climbing: {why}");
                return s_enabled;
            }
        }

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            // Climbing moves to [Movement] (advanced, under the master switch). The old [Climbing] values are
            // carried over once, never reset: TakeOldValues before the binds, ApplyOldValues after.
            MovementFeature.Bind(config);
            const string Old = "Climbing", S = MovementFeature.Section;
            var old = MovementFeature.TakeOldValues(config, Old, "Climbing", "Climb building pieces", "Player climb input", "Jump clear height",
                "Vault max height", "Pull-up max height", "Edge pull-up", "Scale with character size");
            s_mode = config.Bind(S, "Climbing", ClimbingMode.Auto, MovementFeature.Advanced(
                "SERVER. Whether players, bots and companions can climb at all. Auto: on only when a mod that uses climbing is installed (FiresAdminTerrain, FiresRPGClasses, or one that registers for it, e.g. Fires Character Creator). On / Off: always. The master switch above overrides this."));
            configSync?.AddConfigEntry(s_mode);
            // Re-resolved on the next check; an Off mid-climb drops the body into vanilla movement on its next motion step.
            s_mode.SettingChanged += (sender, args) => s_resolvedFor = -1;
            MovementFeature.Changed += _ => s_resolvedFor = -1;   // a consumer registering later re-resolves Auto
            s_pieces = config.Bind(S, "Climb building pieces", true, MovementFeature.Advanced(
                "SERVER. Players, bots and companions may climb player-built walls (38-90 deg) as well as terrain and rock. Off: terrain and rock only (a PvP / raid lever)."));
            s_playerInput = config.Bind(S, "Player climb input", true, MovementFeature.Advanced(
                "Players start a climb only on a deliberate input: holding Jump while moving into a climbable face. Off: players never climb (bots and companions still climb planned routes)."));
            configSync?.AddConfigEntry(s_pieces);
            // Climb polish (starting values; each attempt logs height / band / clip at Debug for tuning).
            s_jumpClear = config.Bind(S, "Jump clear height", 0f, MovementFeature.Advanced(
                "Metres above the feet a plain jump clears. A player never grabs a face whose top is this low: vanilla's jump takes it. 0 = measured from the character's jump (logged once)."));
            // Vault max default 2.25 (a 2.6-3 m wall should be a pull-up, not a vault). Existing cfg files keep their value.
            BindClimbVaultPullup(config, configSync);   // Their own section, master first
            s_edgePullUp = config.Bind(S, "Edge pull-up", false, MovementFeature.Advanced(
                "Grab a ledge lip in mid-air (moving into it, no Jump press needed) and pull up onto it. Does nothing while a Parkour mod is installed (that mod's own pull-up is used)."));
            // The player's vault / pull-up / edge-reach bands and the standing-room
            // check scale with the body's height scale (transform.localScale.y, which FCC's fcc_size sets through Territories).
            s_scaleWithSize = config.Bind(S, "Scale with character size", true, MovementFeature.Advanced(
                "A bigger or smaller player (Fires Character Creator size, or any mod that scales the body) vaults, pulls up and reaches ledges in proportion: 2x size = 2x the Vault max / Pull-up max heights. The jump clear height is not scaled (the jump itself is unchanged). Off: the heights above apply to every size."));
            MovementFeature.ApplyOldValues(config, old, Old, s_mode, s_pieces, s_playerInput, s_jumpClear, s_vaultMax, s_pullUpMax, s_edgePullUp, s_scaleWithSize);
            // No owner gate any more. OwnerGatedConfig drops an absent owner's local keys from the file, which would erase a
            // player's climbing values whenever only a runtime consumer (e.g. FCC) uses climbing. The keys are advanced-hidden instead
            // ([Movement] rule) and Auto decides whether climbing runs.
        }

        private static ConfigEntry<float> s_jumpClear, s_vaultMax, s_pullUpMax;

        // ---- Climbing, vaulting and pull-up are separate so each can be adjusted or
        // toggled off, with a master toggle at the top; vaulting and pull-up get a min/max range and climbing
        // a min trigger height: [Climbing and vaulting] - master first, then each move's own switch and tuning. Defaults keep
        // today's behaviour (0 = automatic: vault from the jump height, pull-up from the vault max, climb from the pull-up max). The two
        // height keys move here from [Movement] once, and a climbing mode of Off starts the master off - one migration, never forced later.
        public const string SplitSection = "Climbing and vaulting";
        private static ConfigEntry<bool> s_master, s_climbOn, s_vaultOn, s_pullOn, s_splitDone;
        private static ConfigEntry<float> s_climbMin, s_climbSpeed, s_climbStamina, s_vaultMin, s_vaultStamina, s_pullMin, s_pullStamina;

        public static bool MasterOn => s_master == null || s_master.Value;
        public static bool ClimbOn => MasterOn && (s_climbOn == null || s_climbOn.Value);
        public static bool VaultOn => MasterOn && (s_vaultOn == null || s_vaultOn.Value);
        public static bool PullUpOn => MasterOn && (s_pullOn == null || s_pullOn.Value);
        public static float VaultStamina => s_vaultStamina != null ? s_vaultStamina.Value : 0f;
        public static float PullUpStamina => s_pullStamina != null ? s_pullStamina.Value : 0f;
        /// <summary>Vault range for this player (m over the feet): min 0 = the jump clear height.</summary>
        public static float VaultMinFor(Player p) => s_vaultMin != null && s_vaultMin.Value > 0f ? s_vaultMin.Value * SizeScale(p) : JumpClearHeight(p);
        public static float PullUpMinFor(Player p) => s_pullMin != null && s_pullMin.Value > 0f ? s_pullMin.Value * SizeScale(p) : VaultMaxFor(p);
        public static float ClimbMinFor(Player p) => s_climbMin != null && s_climbMin.Value > 0f ? s_climbMin.Value * SizeScale(p) : PullUpMaxFor(p);

        private static ConfigDescription Ordered(string text, int order, AcceptableValueBase range = null, bool hidden = false) =>
            new ConfigDescription(text, range, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true, Order = order, Browsable = hidden ? false : (bool?)null });

        private static void BindClimbVaultPullup(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            // the old [Movement] height values, read before the keys are bound here (the orphan table holds unbound keys)
            string oldVault = null, oldPull = null;
            var orphans = MovementFeature.Orphans(config);
            if (orphans != null)
            {
                var dv = new ConfigDefinition(MovementFeature.Section, "Vault max height"); var dp = new ConfigDefinition(MovementFeature.Section, "Pull-up max height");
                if (orphans.TryGetValue(dv, out oldVault)) orphans.Remove(dv);
                if (orphans.TryGetValue(dp, out oldPull)) orphans.Remove(dp);
            }
            string X = SplitSection;
            s_master = config.Bind(X, "Enable climbing vaulting and pull-ups", true, Ordered("SERVER. Master switch: off turns climbing, vaulting and pull-ups all off.", 100));
            s_climbOn = config.Bind(X, "Climbing enabled", true, Ordered("SERVER. Climbing up tall faces (holding Jump into a wall too tall to vault or pull up).", 90));
            s_climbMin = config.Bind(X, "Climbing min trigger height", 0f, Ordered("SERVER. Metres over the feet a face must be for a jump at it to lock into climbing. 0 = just above the pull-up max height.", 89, new AcceptableValueRange<float>(0f, 30f)));
            s_climbSpeed = config.Bind(X, "Climbing speed multiplier", 1f, Ordered("SERVER. Climbing speed x this.", 88, new AcceptableValueRange<float>(0.25f, 3f)));
            s_climbStamina = config.Bind(X, "Climbing stamina multiplier", 1f, Ordered("SERVER. Stamina used per metre climbed x this (0 = free).", 87, new AcceptableValueRange<float>(0f, 3f)));
            s_vaultOn = config.Bind(X, "Vaulting enabled", true, Ordered("SERVER. Hand-plant vaults onto low walls and ledges.", 80));
            s_vaultMin = config.Bind(X, "Vault min height", 0f, Ordered("SERVER. Metres over the feet: the lowest top that is vaulted. 0 = the height a plain jump clears.", 79, new AcceptableValueRange<float>(0f, 10f)));
            s_vaultMax = config.Bind(X, "Vault max height", DefaultVaultMax, Ordered("SERVER. Metres over the feet: the highest top that is vaulted.", 78, new AcceptableValueRange<float>(0.5f, 10f)));
            s_vaultStamina = config.Bind(X, "Vault stamina cost", 0f, Ordered("SERVER. Stamina one vault uses (0 = free, as before).", 77, new AcceptableValueRange<float>(0f, 100f)));
            s_pullOn = config.Bind(X, "Pull-up enabled", true, Ordered("SERVER. Jump-up and pull-up onto ledges too high to vault.", 70));
            s_pullMin = config.Bind(X, "Pull-up min height", 0f, Ordered("SERVER. Metres over the feet: the lowest top that is pulled up onto. 0 = just above the vault max height.", 69, new AcceptableValueRange<float>(0f, 15f)));
            s_pullUpMax = config.Bind(X, "Pull-up max height", DefaultPullUpMax, Ordered("SERVER. Metres over the feet: the highest top that is pulled up onto. Higher faces are climbed.", 68, new AcceptableValueRange<float>(0.5f, 15f)));
            s_pullStamina = config.Bind(X, "Pull-up stamina cost", 0f, Ordered("SERVER. Stamina one pull-up uses (0 = free, as before).", 67, new AcceptableValueRange<float>(0f, 100f)));
            s_splitDone = config.Bind(X, "Split migration done", false, Ordered("Set by Core once the old [Movement] climbing values were carried over. Leave it alone.", 0, null, hidden: true));
            if (configSync != null)
            {
                configSync.AddConfigEntry(s_master); configSync.AddConfigEntry(s_climbOn); configSync.AddConfigEntry(s_climbMin); configSync.AddConfigEntry(s_climbSpeed);
                configSync.AddConfigEntry(s_climbStamina); configSync.AddConfigEntry(s_vaultOn); configSync.AddConfigEntry(s_vaultMin); configSync.AddConfigEntry(s_vaultMax);
                configSync.AddConfigEntry(s_vaultStamina); configSync.AddConfigEntry(s_pullOn); configSync.AddConfigEntry(s_pullMin); configSync.AddConfigEntry(s_pullUpMax);
                configSync.AddConfigEntry(s_pullStamina);
            }
            if (!s_splitDone.Value)
            {
                var moved = new List<string>();
                if (oldVault != null) { try { s_vaultMax.SetSerializedValue(oldVault); moved.Add("Vault max height=" + oldVault); } catch { } }
                if (oldPull != null) { try { s_pullUpMax.SetSerializedValue(oldPull); moved.Add("Pull-up max height=" + oldPull); } catch { } }
                if (s_mode != null && s_mode.Value == ClimbingMode.Off) { s_master.Value = false; moved.Add("master off (climbing was Off)"); }
                s_splitDone.Value = true;
                try { config.Save(); } catch { }
                FiresCore.Logging.FiresLogger.LogInfo($"[Climb] config split into [{X}] (once): {(moved.Count > 0 ? string.Join(", ", moved.ToArray()) : "defaults")}");
            }
            foreach (var e in new ConfigEntryBase[] { s_master, s_climbOn, s_vaultOn, s_pullOn }) ((ConfigEntry<bool>)e).SettingChanged += (a, b) => s_resolvedFor = -1;
        }

        private static bool s_rangesLogged;
        /// <summary>One load line with every switch and range, plus a warning when ranges overlap or leave a gap (vault > pull-up > climb).</summary>
        internal static void LogRangesOnce(Player p)
        {
            if (s_rangesLogged || p == null) return;
            s_rangesLogged = true;
            float jump = JumpClearHeight(p), vMin = VaultMinFor(p), vMax = VaultMaxFor(p), pMin = PullUpMinFor(p), pMax = PullUpMaxFor(p), cMin = ClimbMinFor(p);
            FiresCore.Logging.FiresLogger.LogInfo($"[Climb] config: master {(MasterOn ? "on" : "OFF")}; climb {(ClimbOn ? "on" : "off")} (from {cMin:0.00} m), vault {(VaultOn ? "on" : "off")} ({vMin:0.00}-{vMax:0.00} m), pull-up {(PullUpOn ? "on" : "off")} ({pMin:0.00}-{pMax:0.00} m); jump clears {jump:0.00} m; precedence vault > pull-up > climb");
            var warn = new List<string>();
            if (vMin > vMax) warn.Add($"vault min {vMin:0.00} > vault max {vMax:0.00}: no vaults");
            if (pMin > pMax) warn.Add($"pull-up min {pMin:0.00} > pull-up max {pMax:0.00}: no pull-ups");
            if (vMax > pMin + 0.01f && pMin <= pMax) warn.Add($"vault max {vMax:0.00} > pull-up min {pMin:0.00}: vault wins in {pMin:0.00}-{vMax:0.00} m");
            if (pMin > vMax + 0.01f) warn.Add($"gap {vMax:0.00}-{pMin:0.00} m between vault max and pull-up min: nothing happens there");
            if (cMin > pMax + 0.01f) warn.Add($"gap {pMax:0.00}-{cMin:0.00} m between pull-up max and climbing min trigger: nothing happens there");
            if (cMin < pMax - 0.01f) warn.Add($"climbing min trigger {cMin:0.00} < pull-up max {pMax:0.00}: pull-up wins up to {pMax:0.00} m");
            if (warn.Count > 0) FiresCore.Logging.FiresLogger.LogWarning("[Climb] config ranges: " + string.Join("; ", warn.ToArray()));
        }
        private static ConfigEntry<bool> s_edgePullUp, s_scaleWithSize;
        private static float s_loggedScale = -1f;
        private static float s_measuredJump = -1f;

        /// <summary>Metres a plain jump clears for <paramref name="player"/>: the config, else apex = v^2 / 2g from its jump force (logged once).</summary>
        public static float JumpClearHeight(Player player) => JumpClearUnscaled(player) * SizeScale(player);   // FCC 1.0.1 scales the jump with size

        private static float JumpClearUnscaled(Player player)
        {
            if (s_jumpClear != null && s_jumpClear.Value > 0.01f) return s_jumpClear.Value;
            if (s_measuredJump > 0f) return s_measuredJump;
            float v = player != null ? player.m_jumpForce : FallbackJumpForce, g = Mathf.Max(MinGravity, -Physics.gravity.y);
            if (player != null && UpFrame.At(player.transform.position).Ring) g = Mathf.Max(MinGravity, Physics.gravity.magnitude);   // halo ring: the field's pull, whatever way it points
            s_measuredJump = v * v / (2f * g);
            FiresCore.Logging.FiresLogger.LogDebug($"[Climb] jump clear height measured: {s_measuredJump:0.00} m (jump force {v:0.0}, gravity {g:0.0}); configure [Climbing] Jump clear height to override");
            return s_measuredJump;
        }

        public static float VaultMaxHeight => s_vaultMax != null ? s_vaultMax.Value : DefaultVaultMax;
        public static float PullUpMaxHeight => s_pullUpMax != null ? s_pullUpMax.Value : DefaultPullUpMax;

        /// <summary>The player's height scale for the climb bands (1 when [Climbing] Scale with character size is off), clamped
        /// 0.25-4; logged at Debug whenever it changes.</summary>
        public static float SizeScale(Player player)
        {
            if (player == null || s_scaleWithSize == null || !s_scaleWithSize.Value) return 1f;
            float k = Mathf.Clamp(player.transform.localScale.y, MinSizeScale, MaxSizeScale);
            if (Mathf.Abs(k - s_loggedScale) > 0.01f)
            {
                s_loggedScale = k;
                FiresCore.Logging.FiresLogger.LogDebug($"[Climb] size scale {k:0.00} (transform y {player.transform.localScale.y:0.00}): vault max {VaultMaxHeight * k:0.00} m, pull-up max {PullUpMaxHeight * k:0.00} m");
            }
            return k;
        }

        /// <summary>The lowest size factor climb speed, top-out time and climb clip speed follow (a tiny player must not climb like a grasshopper; below this they stay at x0.35).</summary>
        public const float MinSpeedScale = 0.35f;

        public static float VaultMaxFor(Player player) => VaultMaxHeight * SizeScale(player);
        public static float PullUpMaxFor(Player player) => PullUpMaxHeight * SizeScale(player);

        private static int s_parkourResolved = -1;
        private static string s_parkourName;

        /// <summary>Edge pull-up is on and no Parkour mod is loaded (any plugin whose GUID or name says "parkour"; logged once - disclaim, never block).</summary>
        public static bool EdgePullUp
        {
            get
            {
                if (s_edgePullUp == null || !s_edgePullUp.Value) return false;
                if (s_parkourResolved < 0)
                {
                    foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    {
                        string guid = kv.Key ?? "", name = kv.Value?.Metadata?.Name ?? "";
                        if (guid.IndexOf("parkour", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("parkour", StringComparison.OrdinalIgnoreCase) >= 0) { s_parkourName = $"{name} ({guid})"; break; }
                    }
                    s_parkourResolved = 1;
                    if (s_parkourName != null) FiresCore.Logging.FiresLogger.LogDebug($"[Climb] Edge pull-up is on but {s_parkourName} is installed: Core's edge pull-up stands down, that mod's is used");
                }
                return s_parkourName == null;
            }
        }

        public static bool PiecesClimbable => s_pieces == null || s_pieces.Value;
        public static bool PlayerInputClimbs => s_playerInput == null || s_playerInput.Value;

        public static void AddModifier(IClimbModifier modifier) { if (modifier != null && !s_modifiers.Contains(modifier)) s_modifiers.Add(modifier); }
        public static void RemoveModifier(IClimbModifier modifier) => s_modifiers.Remove(modifier);

        /// <summary>Core's base terms plus every registered modifier (a throwing modifier is skipped).</summary>
        public static ClimbTerms TermsFor(Character body)
        {
            // The [Climbing and vaulting] speed / stamina multipliers on the base terms
            var terms = new ClimbTerms { StaminaPerMetre = BaseStaminaPerMetre * (s_climbStamina != null ? s_climbStamina.Value : 1f), Speed = BaseSpeed * (s_climbSpeed != null ? s_climbSpeed.Value : 1f), MaxAngle = BaseMaxAngle, WetSlipChance = BaseWetSlipChance, Allowed = true };
            foreach (IClimbModifier m in s_modifiers)
            {
                try { m.Modify(body, ref terms); }
                catch (Exception ex) { Debug.LogWarning($"[Climb] a climb modifier threw: {ex.Message}"); }
            }
            terms.MaxAngle = Mathf.Clamp(terms.MaxAngle, MinAngle, 90f);
            // Climb speed x its size factor
            float k = Mathf.Max(MinSpeedScale, SizeScale(body as Player));   // Both ways (up and down), floor MinSpeedScale
            if (Mathf.Abs(k - 1f) > 0.01f) terms.Speed *= k;
            return terms;
        }

        private static float Steepness(float angle) => Mathf.InverseLerp(MinAngle, 90f, angle);
        public static float SpeedAt(ClimbTerms terms, float angle) => terms.Speed * Mathf.Lerp(1f, VerticalSpeedShare, Steepness(angle)) * Mathf.Lerp(1f, OverhangSpeedShare, Overhang(angle));
        /// <summary>Stamina per metre moved on a face of <paramref name="angle"/>° (x WetCostFactor when <paramref name="wet"/>).</summary>
        public static float CostPerMetre(ClimbTerms terms, float angle, bool wet) =>
            terms.StaminaPerMetre * Mathf.Lerp(1f, VerticalCostFactor, Steepness(angle)) * Mathf.Lerp(1f, OverhangCostFactor, Overhang(angle)) * (wet ? WetCostFactor : 1f);

        /// <summary>Wet (vanilla's status effect): a climb costs WetCostFactor more and may slip.</summary>
        public static bool IsWet(Character body) => body != null && body.GetSEMan() != null && body.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectWet);

        /// <summary>The stamina a climb of <paramref name="height"/> m up a face of <paramref name="angle"/>° costs this body: surface length × the cost a metre (Wet as it is now).</summary>
        public static float StaminaFor(Character body, float height, float angle)
        {
            ClimbTerms terms = TermsFor(body);
            float surface = height / Mathf.Max(MinFaceSine, Mathf.Sin(Mathf.Clamp(angle, MinAngle, 90f) * Mathf.Deg2Rad));
            return surface * CostPerMetre(terms, angle, IsWet(body));
        }

        public static float StaminaOf(Character body) => body is Player p ? p.GetStamina() : NpcStamina;
        public static float MaxStaminaOf(Character body) => body is Player p ? Mathf.Max(1f, p.GetMaxStamina()) : NpcStamina;

        /// <summary>Whether a collider is a face one may climb: terrain, rock, other static ground, and (per server config) building pieces; never a tree, a creature or an item.</summary>
        public static bool ClimbableCollider(Collider c, out string surface)
        {
            surface = "nothing";
            if (c == null || c.isTrigger) return false;
            if (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<Character>() != null) { surface = "a creature"; return false; }
            if (c.GetComponentInParent<TreeBase>() != null || c.GetComponentInParent<TreeLog>() != null) { surface = "a tree"; return false; }
            if (c.GetComponentInParent<ItemDrop>() != null) { surface = "an item"; return false; }
            Piece piece = c.GetComponentInParent<Piece>();
            if (piece != null)
            {
                surface = Utils.GetPrefabName(piece.gameObject);
                if (!PiecesClimbable) { surface += " (building pieces are off on this server)"; return false; }
                return true;
            }
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            surface = (s_terrainMask & (1 << c.gameObject.layer)) != 0 ? "terrain" : Utils.GetPrefabName(c.transform.root.gameObject);
            return true;
        }

        /// <summary>
        /// The face in front of <paramref name="body"/> along <paramref name="dir"/> (flat): rays from knee, waist and chest height out to
        /// <see cref="FaceReach"/>; the first climbable hit steeper than <see cref="MinAngle"/> and no steeper than the body's MaxAngle.
        /// </summary>
        public static bool FaceAhead(Character body, Vector3 dir, out RaycastHit face, out float angle, out string surface)
        {
            face = default;
            angle = 0f;
            surface = "nothing";
            if (body == null) return false;
            Vector3 at = body.transform.position;
            UpFrame f = UpFrame.At(at);   // halo ring: probes along the local up (world up elsewhere, unchanged)
            dir = f.Flat(dir);
            if (dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            if (s_mask == 0) s_mask = FiresCore.World.Surface.Mask;
            float max = TermsFor(body).MaxAngle;
            // A scaled player's probe heights
            // and reach scale with its size, as the size bands do; NPCs and size 1 are unchanged
            float sizeScale = SizeScale(body as Player);
            foreach (float h in FaceProbeHeights)
            {
                if (!Physics.Raycast(at + f.Up * (h * sizeScale), dir, out RaycastHit hit, FaceReach * sizeScale, s_mask, QueryTriggerInteraction.Ignore)) continue;
                float a = f.Angle(hit.normal);
                if (!ClimbableCollider(hit.collider, out surface)) return false;
                if (a < MinAngle || a > (max >= VerticalMaxAngle ? OverhangMaxAngle : max) + AngleTolerance) { angle = a; continue; }
                face = hit;
                angle = a;
                return true;
            }
            return false;
        }

        /// <summary>FindBank's reach from deep water to the face (across a shelf or a shore) and its top's height over the water.</summary>
        public const float BankReach = 12f, BankMinAbove = 2.2f, BankMaxAbove = 10f;
        /// <summary>A bank's face, foot to top, is at least this high, so a body has to climb it.</summary>
        public const float BankMinFace = 2.2f;

        /// <summary>
        /// A face rising from water (<see cref="ProbeBank"/>): <see cref="Foot"/> where it starts (the waterline, or the shelf / shore
        /// where the ground starts to rise), <see cref="Top"/> where it levels off, its angle foot to top, its top's height over the water,
        /// its top's flat distance from the water point the probe started at, and what the top is (terrain, or the collider's name).
        /// </summary>
        public sealed class BankFace
        {
            public Vector3 From, Foot, Top;
            public float Angle, Height, Above, Distance;
            public string Surface = "terrain", Reject;
            public override string ToString() =>
                $"{Angle:0}° {Height:0.0} m face ({Surface}) from ({Foot.x:0}, {Foot.z:0}) to ({Top.x:0}, {Top.z:0}), {Above:0.0} m above the water, {Distance:0.0} m from deep water at ({From.x:0}, {From.z:0})";
        }

        private static readonly RaycastHit[] s_topHits = new RaycastHit[16];
        private static int s_terrainLayer = -1;

        /// <summary>
        /// The topmost solid surface at (x, z): terrain, rocks, pieces (what a body climbs; GetGroundHeight sees the terrain
        /// only, under a rock cliff), not water or triggers, not loose bodies (characters, items, ships), and nothing
        /// <see cref="ClimbableCollider"/> refuses (trees, logs, items, creatures) or that is vegetation (a beech without a
        /// TreeBase read as an 85° face). GetGroundHeight where nothing solid is loaded.
        /// </summary>
        public static bool TopSolid(float x, float z, out float height, out Collider hitCollider) => TopSolid(x, z, true, out height, out hitCollider);

        private static readonly float[] StandInset = { 0f, 0.3f, 0.6f };

        /// <summary>
        /// A spot a body can stand on at <paramref name="p"/> or a
        /// little further along <paramref name="dir"/> (0.3, 0.6 m): its top no more than <paramref name="maxRise"/> above
        /// <paramref name="baseY"/> nor 0.3 m below it, out of the water, with room for a body (a capsule from knee to head height).
        /// </summary>
        public static bool StandSpot(Vector3 p, Vector3 dir, float baseY, float maxRise, out Vector3 spot, out string why)
        {
            spot = p;
            why = "no ground";
            if (s_mask == 0) s_mask = FiresCore.World.Surface.Mask;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
            foreach (float d in StandInset)
            {
                Vector3 s = p + dir * d;
                if (!TopSolid(s.x, s.z, out float h, out _)) { why = "no ground"; continue; }
                if (h - baseY > maxRise) { why = $"the top is {h - baseY:0.0} m up"; continue; }
                if (h - baseY < -MaxSpotDrop) { why = "below the water line"; continue; }
                if (FiresCore.World.Water.SurfaceOr(s.x, s.z, Floating.GetLiquidLevel(new Vector3(s.x, h + UnderWaterMargin, s.z))) > h + UnderWaterMargin) { why = "under water"; continue; }
                Vector3 at = new Vector3(s.x, h + SpotLift, s.z);
                if (Physics.CheckCapsule(at + Vector3.up * StandKneeHeight, at + Vector3.up * StandHeadHeight, StandCapsuleRadius, s_mask, QueryTriggerInteraction.Ignore)) { why = "no room to stand"; continue; }
                spot = at;
                return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="TopSolid(float, float, out float, out Collider)"/>, with building pieces counted only when <paramref name="pieces"/>
        /// (FindBank's natural bank is terrain and static rock, never a placed piece).
        /// </summary>
        public static bool TopSolid(float x, float z, bool pieces, out float height, out Collider hitCollider)
        {
            height = 0f;
            hitCollider = null;
            if (s_terrainLayer < 0) s_terrainLayer = LayerMask.NameToLayer("terrain");
            int n = Physics.RaycastNonAlloc(new Vector3(x, 1000f, z), Vector3.down, s_topHits, 2000f, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore);
            float best = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                RaycastHit hit = s_topHits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody != null || hit.point.y <= best) continue;
                if (hit.collider.gameObject.layer != s_terrainLayer && (!ClimbableCollider(hit.collider, out _) || IsVegetation(hit.collider)
                    || (!pieces && hit.collider.GetComponentInParent<Piece>() != null))) continue;
                best = hit.point.y;
                hitCollider = hit.collider;
            }
            if (hitCollider != null) { height = best; return true; }
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out height);
        }

        /// <summary>Halo ring: how far above a point the ring top probe starts (m) and how long it is.</summary>
        private const float RingTopAbove = 60f, RingTopLength = 160f;

        /// <summary>
        /// <see cref="TopSolid(float, float, bool, out float, out Collider)"/> in a frame: off a field exactly that (at p.x, p.z);
        /// inside one (the halo ring) a ray down the LOCAL up through p, the same filters, height = f.H(the hit), no world ground-height fallback.
        /// </summary>
        public static bool TopSolidAt(Vector3 p, UpFrame f, bool pieces, out float height, out Collider hitCollider)
        {
            if (!f.Ring) return TopSolid(p.x, p.z, pieces, out height, out hitCollider);
            height = 0f;
            hitCollider = null;
            if (s_terrainLayer < 0) s_terrainLayer = LayerMask.NameToLayer("terrain");
            int n = Physics.RaycastNonAlloc(p + f.U * RingTopAbove, -f.U, s_topHits, RingTopLength, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore);
            float best = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                RaycastHit hit = s_topHits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody != null || f.H(hit.point) <= best) continue;
                if (hit.collider.gameObject.layer != s_terrainLayer && (!ClimbableCollider(hit.collider, out _) || IsVegetation(hit.collider)
                    || (!pieces && hit.collider.GetComponentInParent<Piece>() != null))) continue;
                best = f.H(hit.point);
                hitCollider = hit.collider;
            }
            if (hitCollider == null) return false;
            height = best;
            return true;
        }

        // Bushes, saplings and small trees (Beech_small, shrubs: a Destructible of the Tree type) and pickable plants: never a bank or a face.
        private static bool IsVegetation(Collider c)
        {
            Destructible d = c.GetComponentInParent<Destructible>();
            if (d != null && (d.m_destructibleType & DestructibleType.Tree) != 0) return true;
            return c.GetComponentInParent<Pickable>() != null;
        }

        /// <summary>A face's top needs ground to stand on behind it: the next <see cref="LedgeDepth"/> m stay within <see cref="LedgeDrop"/> m of the top (not a post or a trunk).</summary>
        public const float LedgeDepth = 0.5f, LedgeDrop = 0.5f;

        /// <summary>
        /// Along <paramref name="dir"/> from <paramref name="from"/> (in water whose surface is <paramref name="surface"/>), out to
        /// <paramref name="reach"/> m in 0.25 m steps on <see cref="TopSolid"/>: across the water (deep, or a shallow shelf) to the
        /// waterline, then across the shore, to the first face whose top stands <paramref name="minAbove"/>+ m over the water. The face runs
        /// from its foot (the last gentle step, under <see cref="MinAngle"/>, before it: the waterline itself for a wall straight out of the
        /// water) to where it levels off. Ground that rises gently that high is a face too, measured from the waterline (a low angle the
        /// caller turns down). False when nothing stands that high within reach (or the ground isn't loaded).
        /// </summary>
        public static bool ProbeBank(Vector3 from, Vector3 dir, float surface, float reach, float minAbove, out BankFace face) =>
            ProbeBank(from, dir, surface, reach, minAbove, true, out face);

        /// <summary><see cref="ProbeBank(Vector3, Vector3, float, float, float, out BankFace)"/>, building pieces counted only when <paramref name="pieces"/>.</summary>
        public static bool ProbeBank(Vector3 from, Vector3 dir, float surface, float reach, float minAbove, bool pieces, out BankFace face)
        {
            face = null;
            const float Step = BankProbeStep;
            float line = -1f, footT = 0f, footG = surface, prevT = 0f, prevG = float.NaN;
            bool steep = false;
            Collider prevCol = null;
            for (float t = Step; t <= reach + 0.001f; t += Step)
            {
                if (!TopSolid(from.x + dir.x * t, from.z + dir.z * t, pieces, out float g, out Collider col)) return false;
                if (line < 0f)
                {
                    if (g < surface) { prevT = t; prevG = g; prevCol = col; continue; }   // still water, deep or a shelf
                    line = t;
                    footT = Mathf.Max(0f, t - Step * 0.5f);   // the waterline, between the last wet step and this one
                    footG = surface;
                }
                float slope = float.IsNaN(prevG) ? 90f : Mathf.Atan2(g - prevG, Step) * Mathf.Rad2Deg;
                if (slope >= MinAngle) steep = true;
                else if (steep)
                {
                    // The face levelled off at the last step: a face if its top stands high enough and has a ledge to stand on behind it
                    // (a post or a trunk top drops away again), else a small step or an obstacle and the shore goes on.
                    if (prevG - surface >= minAbove && Ledge(from, dir, t, g, prevG, pieces))
                    { face = Face(from, dir, surface, footT, footG, prevT, prevG, prevCol); return true; }
                    steep = false;
                    footT = t; footG = g;
                }
                else
                {
                    if (g - surface >= minAbove) { face = Face(from, dir, surface, line - Step * 0.5f, surface, t, g, col); return true; }   // a gentle rise
                    footT = t; footG = g;
                }
                // Far past any top worth climbing: the face so far, for the caller to turn down as too high.
                if (g - surface > BankMaxAbove + BankPastMaxSlack) { face = Face(from, dir, surface, footT, footG, t, g, col); return true; }
                prevT = t; prevG = g; prevCol = col;
            }
            // Out of reach while still on the face: no ledge seen, so no face.
            return false;
        }

        // A ledge behind a face's top (prevG): the ground at t (already sampled: g) and LedgeDepth on from it stays within LedgeDrop of the top.
        private static bool Ledge(Vector3 from, Vector3 dir, float t, float g, float top, bool pieces)
        {
            if (g < top - LedgeDrop) return false;
            for (float s = t + BankProbeStep; s <= t + LedgeDepth + 0.001f; s += BankProbeStep)
            {
                if (!TopSolid(from.x + dir.x * s, from.z + dir.z * s, pieces, out float h, out _)) return false;
                if (h < top - LedgeDrop) return false;
            }
            return true;
        }

        private static BankFace Face(Vector3 from, Vector3 dir, float surface, float footT, float footG, float topT, float topG, Collider top)
        {
            float rise = topG - footG, run = Mathf.Max(0.05f, topT - footT);
            return new BankFace
            {
                From = new Vector3(from.x, surface, from.z),
                Foot = new Vector3(from.x + dir.x * footT, footG, from.z + dir.z * footT),
                Top = new Vector3(from.x + dir.x * topT, topG, from.z + dir.z * topT),
                Angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg,
                Height = rise,
                Above = topG - surface,
                Distance = topT,
                Surface = top == null || top.gameObject.layer == s_terrainLayer ? "terrain" : top.name,
            };
        }

        /// <summary>
        /// A bank to climb out of water near <paramref name="near"/> (the climb drill): <paramref name="foot"/> in water at
        /// least 1 m deep, <paramref name="top"/> the top of a face <see cref="BankMinAbove"/>-<see cref="BankMaxAbove"/> m above the water
        /// within <see cref="BankReach"/> m of it (a pond's face can stand past a shallow shelf, more than 4 m out,
        /// and vertical climbs are allowed now), the face (<see cref="ProbeBank"/>, measured from its own foot) <paramref name="minAngle"/>-
        /// <paramref name="maxAngle"/>°. Searched on a 2 m grid within <paramref name="radius"/>, nearest first, 16 ways from each cell. On
        /// failure <paramref name="why"/> says how many deep-water cells and faces were tried and the nearest miss: where, how high, how
        /// steep, how far, and why it was turned down.
        /// </summary>
        public static bool FindBank(Vector3 near, float radius, float minAngle, float maxAngle, out Vector3 foot, out Vector3 top, out string why)
        {
            foot = top = near;
            why = "";
            if (ZoneSystem.instance == null) { why = "no world"; return false; }
            var best = (d: float.MaxValue, foot: near, face: (BankFace)null);
            BankFace miss = null;
            float missScore = float.MaxValue;
            int n = Mathf.CeilToInt(radius / BankGridStep), cells = 0, faces = 0;
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    Vector3 w = near + new Vector3(i * BankGridStep, 0f, j * BankGridStep);
                    float d = (w - near).sqrMagnitude;
                    if (d > radius * radius || d >= best.d) continue;
                    FiresCore.Npc.AI.Swimming.GoalInDeepWater(w, out float depth);
                    if (depth < BankMinWaterDepth || !ZoneSystem.instance.GetGroundHeight(w, out float bed)) continue;
                    float surface = bed + depth;
                    cells++;
                    for (int k = 0; k < BankDirections; k++)
                    {
                        float a = k * Mathf.PI * 2f / BankDirections;
                        Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                        if (!ProbeBank(w, dir, surface, BankReach, BankMinAbove, false, out BankFace face)) continue;   // Terrain and static rock only
                        faces++;
                        float score;
                        if (face.Above > BankMaxAbove) { face.Reject = $"its top is {face.Above:0.0} m above the water (over {BankMaxAbove:0})"; score = BankTooHighScoreFactor * (face.Above - BankMaxAbove); }
                        //
                        // a bank's top stands on dry terrain, not a rock island.
                        else if (!ZoneSystem.instance.GetGroundHeight(face.Top, out float under) || under < surface) { face.Reject = "a rock standing in the water (an island), not a bank"; score = BankIslandScore; }
                        else if (face.Angle < minAngle) { face.Reject = face.Angle < MinAngle ? "a slope, not a face" : $"under {minAngle:0}°"; score = minAngle - face.Angle; }
                        else if (face.Angle > maxAngle + AngleTolerance) { face.Reject = $"past {maxAngle:0}°"; score = face.Angle - maxAngle; }
                        // A bank is a face a body has to climb.
                        else if (face.Height < BankMinFace) { face.Reject = $"jumpable (a {face.Height:0.0} m face; a jump clears ~1.6-1.8 m, a bank is {BankMinFace:0.0}+ m)"; score = BankJumpableScoreFactor * (BankMinFace - face.Height); }
                        else
                        {
                            best = (d, new Vector3(w.x, surface, w.z), face);
                            break;
                        }
                        if (score < missScore) { missScore = score; miss = face; }
                    }
                }
            if (best.face == null)
            {
                why = $"no {minAngle:0}-{maxAngle:0}° bank {BankMinAbove:0.0}-{BankMaxAbove:0} m above water at least 1 m deep within {BankReach:0} m of it, within {radius:0} m: " +
                      $"{cells} deep-water cell(s), {faces} face(s) tried; " +
                      (cells == 0 ? "no water that deep"
                       : miss == null ? $"no ground {BankMinAbove:0.0}+ m above the water within {BankReach:0} m of it"
                       : $"nearest miss: {miss} - {miss.Reject}");
                return false;
            }
            foot = best.foot;
            top = best.face.Top;
            Debug.Log($"[Climbing] FindBank: {best.face}");
            return true;
        }

        /// <summary>
        /// What FindBank sees near (x, z): the <paramref name="cells"/> deep-water cells (1 m+) nearest it within
        /// <paramref name="radius"/>, and per cell each of the 16 ways the face ProbeBank finds (terrain and static rock) or "nothing
        /// 1.5+ m over the water within 12 m". Console: fires_bankprobe x z [radius] (the drill's angles 45-90°).
        /// </summary>
        public static List<string> BankReport(Vector3 at, float radius, int cells, float minAngle, float maxAngle)
        {
            var lines = new List<string>();
            if (ZoneSystem.instance == null) { lines.Add("no world"); return lines; }
            var wet = new List<(float d, Vector3 w, float surface)>();
            int n = Mathf.CeilToInt(radius / BankGridStep);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    Vector3 w = at + new Vector3(i * BankGridStep, 0f, j * BankGridStep);
                    float d = new Vector2(w.x - at.x, w.z - at.z).magnitude;
                    if (d > radius) continue;
                    FiresCore.Npc.AI.Swimming.GoalInDeepWater(w, out float depth);
                    if (depth < BankMinWaterDepth || !ZoneSystem.instance.GetGroundHeight(w, out float bed)) continue;
                    wet.Add((d, w, bed + depth));
                }
            wet.Sort((a, b) => a.d.CompareTo(b.d));
            lines.Add($"{wet.Count} deep-water cell(s) within {radius:0} m of ({at.x:0}, {at.z:0}); the {Mathf.Min(cells, wet.Count)} nearest:");
            foreach (var (d, w, surface) in wet.GetRange(0, Mathf.Min(cells, wet.Count)))
            {
                lines.Add($"cell ({w.x:0}, {w.z:0}), water at {surface:0.0}, {d:0.0} m off:");
                for (int k = 0; k < BankDirections; k++)
                {
                    float a = k * Mathf.PI * 2f / BankDirections;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    string way = $"  {k * 360f / BankDirections:0}°: ";
                    if (!ProbeBank(w, dir, surface, BankReach, BankMinAbove, false, out BankFace face)) { lines.Add(way + $"nothing {BankMinAbove:0.0}+ m over the water within {BankReach:0} m (with a ledge)"); continue; }
                    bool island = !ZoneSystem.instance.GetGroundHeight(face.Top, out float under) || under < surface;
                    string verdict = face.Above > BankMaxAbove ? "top too high" : island ? "an island" : face.Angle < minAngle ? $"under {minAngle:0}°" : face.Angle > maxAngle + AngleTolerance ? $"past {maxAngle:0}°" : face.Height < BankMinFace ? $"jumpable ({face.Height:0.0} m face)" : "OK";
                    lines.Add(way + $"{face} - {verdict}");
                }
            }
            return lines;
        }

        private static bool s_bankProbeRegistered;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class BankProbe_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_bankProbeRegistered) return;
                s_bankProbeRegistered = true;
                new Terminal.ConsoleCommand("fires_bankprobe",
                    "[FiresUnifiedCore] fires_bankprobe x z [radius] - what the climb drill's FindBank sees near (x, z): each nearby deep-water cell's faces 16 ways, and why each is or isn't a bank (also in the log).",
                    args =>
                    {
                        if (args.Length < 3 || !float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                            || !float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                        { args.Context?.AddString("fires_bankprobe x z [radius]"); return; }
                        float r = args.Length > 3 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr) ? rr : BankProbeDefaultRadius;
                        float y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float gy) ? gy : 0f;
                        foreach (string line in BankReport(new Vector3(x, y, z), r, BankProbeCells, BankProbeMinAngle, BankProbeMaxAngle))
                        {
                            args.Context?.AddString("fires_bankprobe: " + line);
                            Debug.Log("[Climbing] bankprobe: " + line);
                        }
                    });
#if FIRES_TESTS
                RegisterBankProbeTest();   // test-only console command, compiled when the test sources are present
#endif
            }
        }

        // ── `bankprobe_test <x> <z> [radius]`, the bank probe as
        // an on-demand F5 test. Without x z its one step SKIPs (a full fires_test_all is harmless). PASS when the probe printed (the
        // area loaded and its deep-water cells listed, none being a result too); FAIL when the area never loaded. Lines go to the log,
        // the console and BepInEx\FiresTests\bankprobe_test.txt (FDT's runner reads it), in the runner's BEGIN / n/N / END format.
        private const string BankTestCommand = "bankprobe_test", BankTestTag = "[BankProbeTest]";
        private const float BankTestLoadWait = 20f;
        private static bool s_bankTestRunning;

        private static void RegisterBankProbeTest()
        {
            new Terminal.ConsoleCommand(BankTestCommand,
                "[F5 test] what the climb drill's FindBank sees near (x, z): each nearby deep-water cell's faces 16 ways and why each is or isn't a bank (fires_bankprobe as a test). Args: <x> <z> [radius] (on demand; without them the step SKIPs).",
                args =>
                {
                    if (s_bankTestRunning) { BankTestSay($"{BankTestTag} a run is already going."); return; }
                    if (ZNet.instance == null || ZoneSystem.instance == null) { BankTestSay($"{BankTestTag} join a world first."); return; }
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var num = System.Globalization.NumberStyles.Float;
                    bool haveX = float.TryParse(args.Length > 1 ? args[1] : "", num, inv, out float x);
                    bool haveZ = float.TryParse(args.Length > 2 ? args[2] : "", num, inv, out float z);
                    bool have = haveX && haveZ;
                    float r = args.Length > 3 && float.TryParse(args[3], num, inv, out float rr) ? Mathf.Clamp(rr, 2f, 80f) : 16f;
                    ZNet.instance.StartCoroutine(RunBankProbeTest(have, x, z, r));
                });
        }

        private static System.Collections.IEnumerator RunBankProbeTest(bool have, float x, float z, float radius)
        {
            s_bankTestRunning = true;
            float started = Time.realtimeSinceStartup;
            string result = "SKIP";
            try
            {
                BankTestSay($"{BankTestTag} BEGIN {FiresCore.FiresUnifiedCore.PluginName} {FiresCore.FiresUnifiedCore.PluginVersion} world '{(ZNet.World != null ? ZNet.World.m_name : "?")}': 1 step");
                if (!have)
                {
                    BankTestSay($"{BankTestTag} 1/1 bankprobe SKIP - on demand: run {BankTestCommand} <x> <z> [radius]");
                    yield break;
                }
                Vector3 at = new Vector3(x, 0f, z);
                float until = Time.realtimeSinceStartup + BankTestLoadWait;
                while (!ZoneSystem.instance.IsZoneLoaded(at) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
                if (!ZoneSystem.instance.IsZoneLoaded(at) || !ZoneSystem.instance.GetGroundHeight(at, out float y))
                {
                    result = "FAIL";
                    BankTestSay($"{BankTestTag} 1/1 bankprobe FAIL - the area at ({x:0}, {z:0}) isn't loaded after {BankTestLoadWait:0} s (the tester must be within ~64 m)");
                    yield break;
                }
                List<string> lines = BankReport(new Vector3(x, y, z), radius, 4, 45f, 90f);
                foreach (string line in lines) BankTestSay($"{BankTestTag} {line}");
                int ok = 0;
                foreach (string line in lines) if (line.EndsWith(" - OK")) ok++;
                result = "PASS";
                BankTestSay($"{BankTestTag} 1/1 bankprobe PASS - probed near ({x:0}, {z:0}) within {radius:0} m: {lines[0]}; {ok} face(s) FindBank would take (45-90°)");
            }
            finally
            {
                BankTestSay($"{BankTestTag} END {(result == "FAIL" ? "FAIL" : "PASS")}: {(result == "PASS" ? 1 : 0)} pass, {(result == "FAIL" ? 1 : 0)} fail, {(result == "SKIP" ? 1 : 0)} skip in {Time.realtimeSinceStartup - started:0} s");
                s_bankTestRunning = false;
            }
        }

        // The log line, the console, and BepInEx\FiresTests\bankprobe_test.txt (FDT's runner reads it).
        private static void BankTestSay(string line)
        {
            Debug.Log(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, BankTestCommand + ".txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) { }
        }

        /// <summary>A planned climb (<see cref="PlanClimb"/>): where it starts, where it tops out, how high and steep, its cost.</summary>
        public sealed class ClimbPlan
        {
            public Vector3 Foot, Top, Goal;
            public float Height, Angle, Stamina;
            public string Surface = "terrain";
            public override string ToString() => $"up {Surface} {Angle:0}° {Height:0.0} m from ({Foot.x:0}, {Foot.z:0}) to ({Top.x:0}, {Top.z:0}), est {Stamina:0} stamina";
        }

        /// <summary>
        /// A climb on the straight way from <paramref name="from"/> to <paramref name="goal"/> (higher, within <see cref="PlanRange"/>
        /// m flat): the ground sampled every 0.5 m (the topmost solid surface), its steep steps (38°+) the climb, each no steeper than
        /// the body's MaxAngle, no drop over 3 m on the way, every face climbable (pieces per the server config), and the stamina for
        /// the whole climb with <see cref="PlanMargin"/> of the max left at the top. "" with the plan, or why not.
        /// </summary>
        public static bool PlanClimb(Character body, Vector3 from, Vector3 goal, out ClimbPlan plan, out string why)
        {
            if (!ClimbOn) { plan = default; why = "climbing is off ([Climbing and vaulting])"; return false; }
            plan = null;
            why = "";
            if (!Enabled) { why = "climbing is off (config)"; return false; }
            if (body == null) { why = "no body"; return false; }
            ClimbTerms terms = TermsFor(body);
            if (!terms.Allowed) { why = "climbing is not allowed for this body"; return false; }
            Vector3 flat = goal - from;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist > PlanRange) { why = $"the goal is {dist:0} m off (a climb plans {PlanRange:0} m at most)"; return false; }
            // From 0.8 m up (was 1.5), so a body wading at
            // the foot of a hip-high ledge, or in a pit, plans its way up when the walk makes no headway.
            if (goal.y - from.y < PlanMinGoalRise) { why = "the goal is not above"; return false; }
            if (s_mask == 0) s_mask = FiresCore.World.Surface.Mask;
            Vector3 dir = dist > 0.01f ? flat / dist : Vector3.forward;
            float prevH = from.y, height = 0f, steepest = 0f, cost = 0f;
            Vector3 foot = Vector3.zero, top = Vector3.zero;
            bool inClimb = false, any = false;
            string surface = "terrain";
            for (float t = PlanSampleStep; t <= dist + 0.01f; t += PlanSampleStep)
            {
                Vector3 p = from + dir * t;
                if (!Physics.Raycast(new Vector3(p.x, Mathf.Max(prevH, goal.y) + PlanRayAbove, p.z), Vector3.down, out RaycastHit hit, PlanRayLength, s_mask, QueryTriggerInteraction.Ignore)) continue;
                float h = hit.point.y, rise = h - prevH;
                float a = Mathf.Atan2(Mathf.Abs(rise), PlanSampleStep) * Mathf.Rad2Deg;
                if (rise < -PlanMaxDrop) { why = $"a drop of {-rise:0.0} m at ({p.x:0}, {p.z:0})"; return false; }
                if (rise > 0f && a >= MinAngle)
                {
                    if (a > terms.MaxAngle + AngleTolerance) { why = $"a {a:0}° face at ({p.x:0}, {p.z:0}) is past this body's {terms.MaxAngle:0}°"; return false; }
                    if (!ClimbableCollider(hit.collider, out string s)) { why = $"the face at ({p.x:0}, {p.z:0}) is {s}"; return false; }
                    if (!inClimb) { if (!any) foot = from + dir * Mathf.Max(0f, t - PlanSampleStep); inClimb = true; any = true; surface = s; }
                    height += rise;
                    steepest = Mathf.Max(steepest, a);
                    cost += StaminaFor(body, rise, a);
                    top = hit.point;
                }
                else inClimb = false;
                prevH = h;
            }
            if (!any) { why = "no face to climb on the straight way (it walks)"; return false; }
            if (height > PlanMaxHeight) { why = $"a {height:0} m climb (more than {PlanMaxHeight:0} m)"; return false; }
            float have = StaminaOf(body), max = MaxStaminaOf(body);
            if (cost > have - PlanMargin * max)
            {
                why = cost > max * (1f - PlanMargin)
                    ? $"the climb needs {cost:0} stamina, more than this body ever has to spare ({max * (1f - PlanMargin):0})"
                    : $"the climb needs {cost:0} stamina, {have:0} now (rest first)";
                return false;
            }
            plan = new ClimbPlan { Foot = foot, Top = top + dir * PlanTopInset, Goal = goal, Height = height, Angle = steepest, Stamina = cost, Surface = surface };
            return true;
        }

        /// <summary>How far round a boxed-in body <see cref="PlanNearestWall"/> looks for a wall (m), in how many directions.</summary>
        public const float NearestWallReach = 4.5f;
        private const int NearestWallDirections = 16;

        /// <summary>
        /// The nearest
        /// climbable wall round <paramref name="from"/> that tops out at least 0.8 m above it, each way planned by <see cref="PlanClimb"/>
        /// (so the face, the height and the stamina rules are the same), the nearest foot wins. The plan's Goal is
        /// <paramref name="goal"/>: the climb serves that walk.
        /// </summary>
        public static bool PlanNearestWall(Character body, Vector3 from, Vector3 goal, out ClimbPlan plan, out string why)
        {
            plan = null;
            why = $"no wall that tops out 0.8 m+ above within {NearestWallReach:0.0} m";
            if (!Enabled) { why = "climbing is off (config)"; return false; }
            if (body == null) { why = "no body"; return false; }
            float best = float.MaxValue;
            string last = null;
            for (int i = 0; i < NearestWallDirections; i++)
            {
                float a = i * Mathf.PI * 2f / NearestWallDirections;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (float r = NearestWallStartRadius; r <= NearestWallReach + 0.01f; r += NearestWallRadiusStep)
                {
                    Vector3 p = from + dir * r;
                    if (!TopSolid(p.x, p.z, out float h, out _) || h - from.y < PlanMinGoalRise) continue;
                    if (PlanClimb(body, from, new Vector3(p.x, h, p.z), out ClimbPlan c, out string w))
                    {
                        float d = new Vector2(c.Foot.x - from.x, c.Foot.z - from.z).magnitude;
                        if (d < best) { best = d; plan = c; }
                    }
                    else last = w;
                    break;
                }
            }
            if (plan == null) { if (last != null) why = $"{why}; the last wall: {last}"; return false; }
            plan.Goal = goal;
            why = "";
            return true;
        }
    }

    /// <summary>
    /// The climb state for any body (Tools\CLIMBING.md §3-§4), run on its owner in a postfix on Character.UpdateMotion (after vanilla's
    /// motion, which sets gravity and the slide every tick): Idle, Engage (0.4 s on the face), Climb (along the face's steepest way up at
    /// <see cref="Climbing.SpeedAt"/>, gravity off, the slide held at 0, stamina by the metre moved at <see cref="Climbing.CostPerMetre"/>), TopOut (over the edge
    /// onto the ledge, 0.8 s), Fall (stamina out, a hit, the face lost or past the body's angle: vanilla physics again). The state and the
    /// clip go into the body's ZDO for every peer.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Climbing)]
    public static class Climb
    {
        public enum State { Idle = 0, Engage = 1, Climb = 2, TopOut = 3, Fall = 4 }

        // How long a player's vault / pull-up carries its run-in velocity into the top-out (matches the animator crossfade).
        private const float BlendSeconds = 0.15f;
        private const float EngageSeconds = 0.4f, TopOutSeconds = 0.8f, FallShowSeconds = 0.6f, LostFaceSeconds = 0.3f, HugSpeed = 0.5f;
        /// <summary>A move key counts as held past this share of full input (W/S up-down, A/D along the face).</summary>
        private const float InputDeadzone = 0.3f;
        /// <summary>How far past the pull-up / mantle max (m) a top may still be read before it is "too high".</summary>
        private const float TopReadSlack = 0.4f;
        private const float FacingLogInterval = 2f, WhyNotLogInterval = 1f, EdgeLogInterval = 1f;
        private const float RecentHitSeconds = 0.35f, WetSlipShowSeconds = 0.4f, HangStepDelay = 0.2f;
        private const float NoTopFaceCostMargin = 1.2f;      // a mantle with no top climbs on only with this x the face's cost in hand
        private const float LedgeClipMinRise = 1.2f;         // a top-out higher than this plays the Ledge clip, else Up1m
        private const float MantlePastRimSlack = 0.5f;       // past the planned rim by this much a mantle becomes a paid climb
        private const float CornerMinTurn = 30f, CornerMaxTurn = 150f, CornerFaceMaxOffAngle = 35f;
        private const float BodyFaceGap = 0.05f;             // a body stands this far off a face (beyond its radius)
        private const float ChestHeight = 1.0f, AheadHitReach = 3f;
        // Ledge probe
        private const float LedgeProbeAbove = 2.4f, LedgeProbeLength = 3.2f, LedgeMaxRise = 2.2f, LedgeMinRise = -0.2f, LedgeStepIn = 0.1f;
        private const float OverApexAboveTop = 0.45f;        // going over a thin wall rises this far above its top
        // TopOutTimeFor: base + per metre up + per metre in, clamped
        private const float TopOutBaseSeconds = 0.5f, TopOutSecondsPerRise = 0.25f, TopOutSecondsPerInward = 0.2f, TopOutMinSeconds = 0.6f, TopOutMaxSeconds = 1.6f;

        private sealed class Run
        {
            public State State;
            public float Since;
            public Vector3 Normal, WallDir;
            public float Angle;
            public float StartY, StaminaAtStart, StartedAt;
            // What the climb itself charged (the topped-out / fell lines; start minus end counted regen and vanilla's drains).
            public float Charged;
            public float LostFaceSince = -1f, NextStep, SlipUntil = -1f;
            public Vector3 TopFrom, TopTo;
            public bool Planned;
            public string Surface, Reason;
            public ClimbClip Clip;
            // A planned rim up to Climbing.MantleMaxHeight (out of water, up a hip-high ledge): stamina never drops it.
            public bool Mantle;
            // The planned height (0 for a player's own climb), so the last MantleMaxHeight of a planned climb is a pull-up.
            public float PlanHeight;
            // Where the plan tops out (NaN x: none) and where the face was met, for a mantle's vault; the highest the body got
            // and when, for the no-headway watchdog.
            public Vector3 PlanTop = new Vector3(float.NaN, 0f, 0f), FacePoint;
            public float BestY, BestYAt;
            // Where the body was last step, so the climb is paid by the metre moved.
            public Vector3 LastPos;
            // What a mantle's height is measured from: the water line for a swimmer (its feet ride Character.m_swimDepth, 2 m,
            // under it, and a rim exit's height is over the water), else the feet.
            public float MantleBaseY;
            // Space was down last step (a player's grab starts with it down), so only a fresh press releases.
            public bool JumpWasHeld;
            // How long this top-out takes (TopOutTimeFor).
            public float TopOutTime;
            // Where the grab was (the hang / move line gives the metres along the face from it).
            public Vector3 StartPos;
            // A turn round a corner under way (until > 0): from / to position and facing; no new one before CornerNext.
            public float CornerUntil, CornerNext;
            // An over-the-wall top-out's apex (NaN: a plain top-out); the top-edge hang was said.
            public float TopApexY = float.NaN;
            // The body's velocity when the run began (a player's vault / pull-up carries it into the top-out over BlendSeconds).
            public Vector3 EntryVel;
            public bool TopEdgeLogged;
            public Vector3 CornerFromPos, CornerToPos, CornerFromDir, CornerToDir;
            // A player's vault / pull-up: how high its top may be and the top-out clip it plays (None: the old Ledge / Up1m pick).
            public float MantleMax = Climbing.MantleMaxHeight;
            public ClimbClip MantleClip = ClimbClip.None;
            // Halo ring: the frame the climb started in (world up off a field); every height of this run is measured in it.
            public UpFrame F;
        }

        /// <summary>A player's release press counts this long (s) after the grab at the earliest; A / D move along the face at this share of the climb speed.</summary>
        private const float ReleaseAfterSeconds = 0.3f, SideSpeedShare = 0.7f;
        // After a release, Space has to come up before the next grab (the local player only: there is one).
        private static bool s_jumpMustRise;
        private static bool JumpHeld() => ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump");

        // The level a mantle's height counts from for this body now (see Run.MantleBaseY).
        private static float MantleBase(Character body) =>
            body.IsSwimming() ? Mathf.Max(body.transform.position.y, body.GetLiquidLevel()) : body.transform.position.y;

        // Halo ring: the same in a frame (no water on a ring: the feet).
        private static float MantleBase(Character body, UpFrame f) => f.Ring ? f.H(body.transform.position) : MantleBase(body);

        private sealed class Ticket
        {
            public Climbing.ClimbPlan Plan;
            public float Until;
            public string Reason;
        }

        private static readonly Dictionary<Character, Run> s_runs = new Dictionary<Character, Run>();
        private static readonly Dictionary<Character, Ticket> s_tickets = new Dictionary<Character, Ticket>();

        /// <summary>
        /// A PLANNED climb for a bot or companion (PathWalker, the swim exit): the body starts climbing when it reaches the plan's face
        /// within <paramref name="seconds"/>. The only way a body without a player's deliberate input starts a climb.
        /// </summary>
        public static void Plan(Character body, Climbing.ClimbPlan plan, string reason, float seconds = 30f)
        {
            if (body == null || plan == null) return;
            s_tickets[body] = new Ticket { Plan = plan, Until = Time.time + seconds, Reason = reason };
        }

        public static bool TicketFor(Character body, out Climbing.ClimbPlan plan)
        {
            plan = null;
            if (body == null || !s_tickets.TryGetValue(body, out Ticket t)) return false;
            if (Time.time > t.Until) { s_tickets.Remove(body); return false; }
            plan = t.Plan;
            return true;
        }

        public static void CancelPlan(Character body) { if (body != null) s_tickets.Remove(body); }

        /// <summary>The body's climb state: its owner's run, or the ZDO's FiresClimb on any other peer.</summary>
        public static State StateOf(Character body)
        {
            if (body == null) return State.Idle;
            if (s_runs.TryGetValue(body, out Run run)) return run.State;
            ZDO zdo = body.m_nview != null && body.m_nview.IsValid() ? body.m_nview.GetZDO() : null;
            return zdo != null ? (State)zdo.GetInt(ClimbZdo.StateHash, 0) : State.Idle;
        }

        /// <summary>On the face (Engage, Climb or TopOut).</summary>
        public static bool IsClimbing(Character body)
        {
            State s = StateOf(body);
            return s == State.Engage || s == State.Climb || s == State.TopOut;
        }

        /// <summary>This peer runs <paramref name="body"/>'s climb (its owner, on the face or falling off it): no ZDO read.</summary>
        public static bool HasRun(Character body) => body != null && s_runs.Count > 0 && s_runs.ContainsKey(body);

        /// <summary>Toward the face (flat) while climbing, for a driver's move / look; zero when not climbing.</summary>
        public static Vector3 FaceDirection(Character body) => body != null && s_runs.TryGetValue(body, out Run run) ? run.WallDir : Vector3.zero;

        /// <summary>Ends <paramref name="body"/>'s climb now (its driver gave it up): it falls back to vanilla physics.</summary>
        public static void Stop(Character body, string why)
        {
            if (body != null && s_runs.TryGetValue(body, out Run run) && run.State != State.Fall && run.State != State.Idle) ToFall(body, run, why);
        }

        private static bool TryBegin(Character body, RaycastHit face, float angle, string surface, bool planned, string reason, Vector3 goal, float height, Vector3 planTop, ClimbClip playerMantle = ClimbClip.None)
        {
            if (!Climbing.Enabled) return false;
            if (body.IsDead() || body.InAttack() || body.InDodge() || body.IsAttached()) return false;
            if (NoClimbMode(body) != null) return false;   // Debug fly / ghost / noclip
            ClimbTerms terms = Climbing.TermsFor(body);
            if (!terms.Allowed) return false;
            // A planned rim up to MantleMaxHeight is a mantle
            // (a vault up a ledge, like a jump), started and finished whatever stamina a swim left; a real climb still needs StartStamina.
            bool mantle = (planned && height > 0f && height <= Climbing.MantleMaxHeight) || playerMantle != ClimbClip.None;
            if (body is Player && !mantle && Climbing.StaminaOf(body) < Climbing.StartStamina) return false;
            // Fire 10-23: a vault / pull-up went up through ceilings and roofs. The whole path (straight up to the landing height, then
            // across onto it) must be clear for the player's capsule, or the move is refused.
            UpFrame f = UpFrame.At(body.transform.position);   // halo ring: this climb's frame (world up elsewhere)
            if (body is Player && playerMantle != ClimbClip.None && !float.IsNaN(goal.x) && !HeadroomClear(body, goal, out string ceiling))
            {
                Debug.Log($"[Climb] vault refused: {ceiling} (landing at y {f.H(goal):0.0}, feet at y {f.H(body.transform.position):0.0})");
                return false;
            }
            Vector3 wall = f.Flat(-face.normal);
            if (wall.sqrMagnitude < 0.0001f) return false;
            // A planned climb starts only with the body facing into the face; the
            // walker squares it up at the face first. A player's start keeps its own 60° rule (TryPlayerInput).
            float facingYaw = f.Yaw(body.transform.forward), wallYaw = f.Yaw(wall), apart = Mathf.Abs(Mathf.DeltaAngle(facingYaw, wallYaw));
            if (planned && apart > StartFacingDegrees)
            {
                if (Time.time >= s_facingLogAt)
                {
                    s_facingLogAt = Time.time + FacingLogInterval;
                    Debug.Log($"[Climb] climb: {Name(body)} not started up {surface}: facing {facingYaw:0}° vs the face {wallYaw:0}° (normal {Yaw(face.normal):0}°), " +
                              $"{apart:0}° apart (over {StartFacingDegrees:0}°)");
                }
                return false;
            }
            var run = new Run
            {
                State = State.Engage, Since = Time.time, StartedAt = Time.time, Normal = face.normal, WallDir = wall.normalized, Angle = angle,
                F = f, StartY = f.H(body.transform.position), StaminaAtStart = Climbing.StaminaOf(body), Planned = planned, Surface = surface, Reason = reason,
                NextStep = Time.time + Climbing.StepSeconds, Mantle = mantle, PlanHeight = planned || playerMantle != ClimbClip.None ? height : 0f,
                MantleMax = playerMantle != ClimbClip.None ? Mathf.Max(Climbing.MantleMaxHeight, Climbing.PullUpMaxFor(body as Player)) : Climbing.MantleMaxHeight, MantleClip = playerMantle,
                PlanTop = planTop, FacePoint = face.point, BestY = f.H(body.transform.position), BestYAt = Time.time, MantleBaseY = MantleBase(body, f),
                JumpWasHeld = !planned, StartPos = body.transform.position,
                EntryVel = body.m_body != null ? body.m_body.linearVelocity : Vector3.zero,
            };
            s_runs[body] = run;
            if (f.Ring) Debug.Log($"[Climb] climb: {Name(body)} on a gravity field (halo ring): this climb measures along the local up {f.U}");
            // A player's vault / pull-up goes straight to its clip (no Enter clip first: one crossfade, not two).
            if (playerMantle == ClimbClip.Vault)
                FiresCore.Logging.FiresLogger.LogDebug($"[Climb] vault start: crossfade {ClimbAnimator.CrossFadeTime:0.00} s from {ClimbAnimator.CurrentStateName(body)}, speed {run.EntryVel.magnitude:0.00} m/s");
            SetZdo(body, run, playerMantle != ClimbClip.None ? playerMantle : ClimbClip.Enter);
            Debug.Log($"[Climb] climb: {Name(body)} up {surface} {angle:0}° {(height > 0f ? $"{height:0.0} m " : "")}to reach ({goal.x:0}, {goal.y:0}, {goal.z:0}) " +
                      $"({(planned ? "planned" : "player input")}: {reason}); stamina {Climbing.StaminaOf(body):0}; " +
                      $"facing {facingYaw:0}° vs the face {wallYaw:0}° (normal {Yaw(face.normal):0}°), {apart:0}° apart; " +
                      (mantle ? "a mantle (no stamina)" : $"{Climbing.CostPerMetre(terms, angle, Climbing.IsWet(body)):0.0} stamina a metre{(Climbing.IsWet(body) ? " (wet)" : "")}" + (!planned && body.m_lastGroundTouch < 0.3f ? $" (the grab from the ground also took vanilla's {body.m_jumpStaminaUsage:0} for the jump)" : "")));
            return true;
        }

        /// <summary>A planned climb starts only with the body's forward within this (°) of into the face.</summary>
        public const float StartFacingDegrees = 30f;
        /// <summary>A planned climb lets go when it hasn't risen this much (m) in this long (s).</summary>
        public const float ClimbHeadwayMetres = 0.3f, ClimbHeadwaySeconds = 3f;
        // How far in from where the face was met a mantle looks for the floor to vault onto (m), nearest first after 0.5.
        private static readonly float[] MantleInset = { 0.5f, 0.35f, 0.7f, 1.0f };
        /// <summary>A vault lands no further than this (m, flat) from where the body is.</summary>
        public const float MantleReach = 2.0f;

        // ---- Corners: go around corners instead of falling off the wall ----

        /// <summary>How long the turn round a corner takes (s).</summary>
        public const float CornerSeconds = 0.3f;
        private const float InsideReach = 0.35f, EdgeProbe = 0.25f, CornerProbeBack = 0.9f, CornerProbeReach = 1.4f;
        // Outside-corner turns tried, most likely first (degrees the facing turns away from the way the body moves).
        private static readonly float[] CornerTurns = { 90f, 75f, 105f, 60f, 120f, 45f };

        /// <summary>
        /// Moving along the face toward <paramref name="sideDir"/>: an inside corner (a climbable face across the way, within InsideReach
        /// of the capsule) or an outside edge (the face gone EdgeProbe past the body's side, or already gone right ahead when
        /// <paramref name="lost"/>) with a climbable face round it, which the body then turns onto over CornerSeconds. False (nothing
        /// started) at an open edge: the climb goes on as before (and lets go when the face is lost).
        /// </summary>
        private static bool TryCorner(Character body, Run run, Vector3 sideDir, bool lost)
        {
            UpFrame f = run.F;   // halo ring: the run's frame
            sideDir = f.Flat(sideDir);
            if (sideDir.sqrMagnitude < 0.0001f || run.WallDir.sqrMagnitude < 0.0001f) return false;
            sideDir.Normalize();
            int mask = FiresCore.World.Surface.Mask;
            Vector3 at = body.transform.position;
            float radius = body.GetRadius();
            Vector3 chest = at + f.Up * ChestHeight;
            ClimbTerms terms = Climbing.TermsFor(body);

            // Inside corner: a face across the way it moves.
            if (Physics.Raycast(chest, sideDir, out RaycastHit inside, radius + InsideReach, mask, QueryTriggerInteraction.Ignore)
                && CornerFace(inside, terms, f, out string insideSurface))
            {
                Vector3 into = f.Flat(-inside.normal);
                // (flattened above)
                float turn = into.sqrMagnitude > 0.0001f ? Vector3.Angle(run.WallDir, into.normalized) : 0f;
                if (turn >= CornerMinTurn && turn <= CornerMaxTurn)
                {
                    Vector3 to = inside.point - into.normalized * (radius + BodyFaceGap);
                    to = f.WithH(to, f.H(at));
                    StartCorner(body, run, to, into.normalized, inside.normal, "an inside", turn, insideSurface);
                    return true;
                }
            }

            // Outside edge: the face ends EdgeProbe past the body's side (or is already gone right ahead).
            if (!lost && Physics.Raycast(chest + sideDir * (radius + EdgeProbe), run.WallDir, out _, Climbing.FaceReach, mask, QueryTriggerInteraction.Ignore)) return false;
            // Round the corner: the facing turns away from the move (moving right round a pillar's corner it ends facing the side face).
            float sign = Vector3.Dot(sideDir, Vector3.Cross(f.Up, run.WallDir)) > 0f ? -1f : 1f;
            Vector3 corner = chest + sideDir * (radius + EdgeProbe) + run.WallDir * (radius + 0.35f);
            foreach (float a in CornerTurns)
            {
                Vector3 d = Quaternion.AngleAxis(sign * a, f.Up) * run.WallDir;
                d = f.Flat(d);
                d.Normalize();
                if (!Physics.Raycast(corner - d * CornerProbeBack, d, out RaycastHit hit, CornerProbeReach, mask, QueryTriggerInteraction.Ignore)) continue;
                if (!CornerFace(hit, terms, f, out string surface)) continue;
                Vector3 into = f.Flat(-hit.normal);
                // (flattened above)
                if (into.sqrMagnitude < 0.0001f || Vector3.Angle(into.normalized, d) > CornerFaceMaxOffAngle) continue;   // a face this way round, not a side of it
                Vector3 to = hit.point - into.normalized * (radius + BodyFaceGap);
                to = f.WithH(to, f.H(at));
                // Room for the body there.
                if (Physics.CheckCapsule(to + f.Up * (radius + 0.1f), to + f.Up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                StartCorner(body, run, to, into.normalized, hit.normal, "an outside", Vector3.Angle(run.WallDir, into.normalized), surface);
                return true;
            }
            return false;
        }

        // A face the body can climb on after a turn: steep enough (MinAngle) and not past the overhang limit, and climbable.
        private static bool CornerFace(RaycastHit hit, ClimbTerms terms, UpFrame f, out string surface)
        {
            surface = "nothing";
            float a = f.Angle(hit.normal);
            float max = terms.MaxAngle >= Climbing.VerticalMaxAngle ? Climbing.OverhangMaxAngle : terms.MaxAngle;
            if (a < Climbing.MinAngle || a > max + Climbing.AngleTolerance) return false;
            return Climbing.ClimbableCollider(hit.collider, out surface);
        }

        private static void StartCorner(Character body, Run run, Vector3 to, Vector3 newWallDir, Vector3 newNormal, string kind, float turn, string surface)
        {
            run.CornerFromPos = body.transform.position;
            run.CornerToPos = to;
            run.CornerFromDir = run.WallDir;
            run.CornerToDir = newWallDir;
            run.CornerUntil = Time.time + CornerSeconds;
            run.Normal = newNormal;
            run.Surface = surface;
            run.LostFaceSince = -1f;
            Debug.Log($"[Climb] climb: {Name(body)} round {kind} corner {turn:0}° onto {surface} at {run.F.H(body.transform.position) - run.StartY:0.0} m up");
        }

        // A top-out's time grows with how far it goes,
        // 0.5 s + 0.25 s a metre up + 0.2 s a metre in, 0.6-1.6 s.
        // A scaled-up player's distances count in its own body lengths (k = size factor; both ways, floor MinSpeedScale), so a 2x vault takes as long as a 1x one.
        private static float TopOutTimeFor(Vector3 from, Vector3 to, float k = 1f) => TopOutTimeFor(from, to, default, k);

        private static float TopOutTimeFor(Vector3 from, Vector3 to, UpFrame f, float k)
        {
            k = Mathf.Max(Climbing.MinSpeedScale, k);   // Both ways
            float rise = Mathf.Max(0f, f.H(to) - f.H(from)) / k;
            float inward = f.FlatDist(from, to) / k;
            return Mathf.Clamp(TopOutBaseSeconds + TopOutSecondsPerRise * rise + TopOutSecondsPerInward * inward, TopOutMinSeconds, TopOutMaxSeconds);
        }

        // Where a mantle vaults to: the plan's top, else the first floor MantleInset in from the face, at most MantleMaxHeight + 0.4 m
        // above the feet and not below them, out of the water, with room to stand (a capsule from knee to head height).
        private static bool MantleTarget(Character body, Run run, out Vector3 onto, out string tried, out float topRise)
        {
            onto = Vector3.zero;
            tried = "no spot";
            topRise = 0f;
            UpFrame f = run.F;   // halo ring: the run's frame
            float feet = MantleBase(body, f);
            int mask = FiresCore.World.Surface.Mask;
            Vector3 at = body.transform.position;
            for (int i = -1; i < MantleInset.Length; i++)
            {
                Vector3 s;
                if (i < 0) { if (float.IsNaN(run.PlanTop.x)) continue; s = run.PlanTop; }
                else s = run.FacePoint + run.WallDir * MantleInset[i];
                // A vault lands at the
                // face it grabbed, within MantleReach.
                if (f.FlatDist(at, s) > MantleReach) { tried = "the planned top is out of reach"; continue; }
                if (!Climbing.TopSolidAt(s, f, true, out float h, out _)) { tried = "no ground there"; continue; }
                float rise = h - feet;
                topRise = Mathf.Max(topRise, rise);
                if (rise > run.MantleMax + TopReadSlack) { tried = $"the top is {rise:0.0} m up"; continue; }
                if (rise < -Climbing.MaxSpotDrop) { tried = $"the spot is {-rise:0.0} m down"; continue; }
                if (!f.Ring && FiresCore.World.Water.SurfaceOr(s.x, s.z, Floating.GetLiquidLevel(new Vector3(s.x, h + Climbing.UnderWaterMargin, s.z))) > h + Climbing.UnderWaterMargin) { tried = "the spot is under water"; continue; }
                Vector3 spot = f.WithH(s, h + Climbing.SpotLift);
                if (Physics.CheckCapsule(spot + f.Up * Climbing.StandKneeHeight, spot + f.Up * Climbing.StandHeadHeight, Climbing.StandCapsuleRadius, mask, QueryTriggerInteraction.Ignore)) { tried = "no room to stand"; continue; }
                onto = spot;
                return true;
            }
            return false;
        }
        /// <summary>How fast Core turns a body into the face before a planned start (vanilla turns a player 300°/s, a swimmer 100°/s).</summary>
        private const float SquareUpDegreesPerSecond = 720f;
        private static float s_facingLogAt;

        // While the local player holds Jump,
        // the gate that kept the climb from starting, at Debug, at most once a second.
        private static float s_whyNotAt;

        private static void WhyNot(Player player, string why)
        {
            if (Time.time < s_whyNotAt) return;
            s_whyNotAt = Time.time + WhyNotLogInterval;
            FiresCore.Logging.FiresLogger.LogDebug($"[Climb] climb: {Name(player)} Jump held, not started: {why}");
        }

        // What the knee / waist / chest rays hit straight ahead, for the why-line (any layer of the climb mask, any distance up to 3 m).
        private static string AheadHit(Player player, Vector3 forward)
        {
            UpFrame f = UpFrame.At(player.transform.position);   // halo ring
            forward = f.Flat(forward);
            if (forward.sqrMagnitude < 0.0001f) return "no forward";
            forward.Normalize();
            foreach (float h in Climbing.FaceProbeHeights)
            {
                if (!Physics.Raycast(player.transform.position + f.Up * h, forward, out RaycastHit hit, AheadHitReach, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore)) continue;
                Climbing.ClimbableCollider(hit.collider, out string what);
                return $"at {h:0.0} m up: {what} at {hit.distance:0.00} m, {f.Angle(hit.normal):0}° steep";
            }
            return "nothing within 3 m at knee, waist or chest height";
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
        }

        // Compass yaw of a flat direction (0 = +z, 90 = +x), as the bot's Think lines give "hd".
        private static float Yaw(Vector3 dir)
        {
            dir.y = 0f;
            return dir.sqrMagnitude < 0.0001f ? 0f : Quaternion.LookRotation(dir).eulerAngles.y;
        }

        // How far the body's forward is off into the face (°): the topped-out / fell lines carry it, so the facing hold shows in the log.
        private static float OffFace(Character body, Run run) =>
            run.WallDir.sqrMagnitude > 0.0001f ? Mathf.Abs(Mathf.DeltaAngle(Yaw(body.transform.forward), Yaw(run.WallDir))) : 0f;

        // Climbing out of water, the body still read as swimming through the first
        // 2 m: vanilla's swim drain (Player.OnSwimming, ~5 a second while moving) and its drowning tick ran on
        // under the climb. The climb owns a body on the face: no swim drain, no drowning there.
        // Vanilla's CheckRun needs only the run
        // key and a move (no ground), so a player holding Shift paid the sprint drain (m_runStaminaDrain, 10 a second) on the face as
        // well. On the face nobody runs.
        [HarmonyPatch(typeof(Player), "CheckRun")]
        private static class Player_CheckRun_Climb
        {
            private static bool Prefix(Player __instance, ref bool __result)
            {
                if (s_runs.Count == 0 || !s_runs.TryGetValue(__instance, out Run run)) return true;
                if (run.State != State.Engage && run.State != State.Climb && run.State != State.TopOut) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), "OnSwimming")]
        private static class Player_OnSwimming_Climb
        {
            private static bool Prefix(Player __instance)
            {
                if (s_runs.Count == 0 || !s_runs.TryGetValue(__instance, out Run run)) return true;
                return run.State != State.Engage && run.State != State.Climb && run.State != State.TopOut;
            }
        }

        [HarmonyPatch(typeof(Character), "UpdateMotion")]
        private static class Character_UpdateMotion_Climb
        {
            private static void Postfix(Character __instance, float dt)
            {
                try
                {
                    if (s_runs.Count == 0 && s_tickets.Count == 0 && __instance != Player.m_localPlayer) return;
                    if (!Climbing.Enabled)
                    {
                        // Climbing switched off: a body still on a face ends its climb now and moves as vanilla.
                        if (s_runs.TryGetValue(__instance, out Run off)) End(__instance, off, "climbing is off (config)");
                        return;
                    }
                    // No climb in debug
                    // fly, ghost mode or noclip (collider off); one already running ends the moment any of those turns on.
                    string noClimb = NoClimbMode(__instance);
                    if (noClimb != null)
                    {
                        if (s_runs.TryGetValue(__instance, out Run flying)) End(__instance, flying, noClimb);
                        return;
                    }
                    if (s_runs.TryGetValue(__instance, out Run run)) { Step(__instance, run, dt); return; }
                    if (s_tickets.Count > 0 && TicketFor(__instance, out Climbing.ClimbPlan plan)) { TryPlanned(__instance, plan, dt); return; }
                    if (__instance == Player.m_localPlayer) TryPlayerInput((Player)__instance);
                }
                catch (Exception ex) { Debug.LogWarning($"[Climb] {Name(__instance)}: {ex.Message}"); }
            }
        }

        // A planned climb starts once the body is at the plan's face.
        private static void TryPlanned(Character body, Climbing.ClimbPlan plan, float dt)
        {
            Vector3 toTop = plan.Top - body.transform.position;
            toTop.y = 0f;
            if (!Climbing.FaceAhead(body, toTop, out RaycastHit face, out float angle, out string surface)) return;
            // At the face, Core turns the body into it before the start (TryBegin wants it within StartFacingDegrees), whoever
            // drives it: the walker squares up on land, but a swimmer is driven straight at its exit point (FDT's SwimToShore), which
            // may meet the rim at a slant. Faster than vanilla's own turn toward the driver's move (it ran this step, before this).
            Vector3 into = -face.normal;
            into.y = 0f;
            if (into.sqrMagnitude > 0.0001f && Vector3.Angle(Flat(body.transform.forward), into) > StartFacingDegrees)
            {
                Quaternion turned = Quaternion.RotateTowards(Quaternion.LookRotation(Flat(body.transform.forward)), Quaternion.LookRotation(into.normalized), SquareUpDegreesPerSecond * dt);
                body.transform.rotation = turned;
                if (body.m_body != null) body.m_body.rotation = turned;
                if (Time.time >= s_facingLogAt)
                {
                    s_facingLogAt = Time.time + FacingLogInterval;
                    Debug.Log($"[Climb] climb: {Name(body)} squaring up to {surface}: facing {Yaw(body.transform.forward):0}° vs the face {Yaw(into):0}°");
                }
                if (Vector3.Angle(Flat(body.transform.forward), into) > StartFacingDegrees) return;
            }
            s_tickets.TryGetValue(body, out Ticket ticket);
            if (TryBegin(body, face, angle, surface, true, ticket?.Reason ?? "planned", plan.Goal, plan.Height, plan.Top)) s_tickets.Remove(body);
        }

        // A player's deliberate start: Jump held on the real keyboard / pad while moving into a climbable face (never by pushing into
        // a wall or sliding alone; a bot's virtual presses don't reach ZInput).
        private static void TryPlayerInput(Player player)
        {
            bool jumpHeld = JumpHeld();
            // After a release (Space while attached) a new grab needs Space let go and pressed again, so the release press itself
            // never re-grabs the face it just left.
            if (s_jumpMustRise) { if (!jumpHeld) s_jumpMustRise = false; else WhyNot(player, "Jump must be let go and pressed again after a release"); return; }
            if (!Climbing.PlayerInputClimbs) { if (jumpHeld) WhyNot(player, "[Climbing] Player climb input is off"); return; }
            if (player.m_moveDir.sqrMagnitude < 0.01f) { if (jumpHeld) WhyNot(player, "no move direction (Forward not held)"); return; }
            // A jumpable top was left to vanilla's jump: no grab until the feet are back down.
            if (s_jumpOverPending) { if (player.IsOnGround() && Time.time - s_jumpOverAt > 0.3f) s_jumpOverPending = false; else return; }
            if (!jumpHeld)
            {
                if (Climbing.EdgePullUp && !player.IsOnGround() && !player.IsSwimming()) TryEdgePullUp(player);
                return;
            }
            Vector3 forward = player.transform.forward;
            UpFrame f = UpFrame.At(player.transform.position);   // halo ring: the start's frame (world up elsewhere)
            if (!Climbing.FaceAhead(player, forward, out RaycastHit face, out float angle, out string surface))
            {
                WhyNot(player, $"no climbable face within {Climbing.FaceReach * Climbing.SizeScale(player):0.0} m ahead ({AheadHit(player, forward)})");
                return;
            }
            Vector3 into = f.Flat(-face.normal);
            // (flattened above)
            if (into.sqrMagnitude < 0.0001f || Vector3.Dot(forward, into.normalized) < 0.5f)
            {
                WhyNot(player, $"not facing {surface} squarely (facing {f.Yaw(forward):0}°, the face {f.Yaw(into):0}°, {angle:0}° steep)");
                return;
            }
            // Climb polish: the top's height picks the move - jumpable: vanilla's jump, no grab; up to Vault max: a
            // vault; up to Pull-up max: a pull-up; higher (or no top found): the climb as before. One Debug line per attempt.
            if (!player.IsSwimming() && TopAhead(player, face, into.normalized, out Vector3 top, out float rise))
            {
                float jump = Climbing.JumpClearHeight(player);
                Climbing.LogRangesOnce(player);
                if (rise <= jump)
                {
                    s_jumpOverPending = true; s_jumpOverAt = Time.time;
                    Attempt(player, rise, "jump", "vanilla", $"at or under the jump clear height {jump:0.00} m");
                    return;
                }
                float vaultMax = Climbing.VaultMaxFor(player), pullUpMax = Climbing.PullUpMaxFor(player);
                // Each move only inside its own range and when its switch is on; precedence vault > pull-up > climb
                bool inVault = Climbing.VaultOn && rise >= Climbing.VaultMinFor(player) && rise <= vaultMax;
                bool inPull = !inVault && Climbing.PullUpOn && rise >= Climbing.PullUpMinFor(player) && rise <= pullUpMax;
                if (inVault || inPull)
                {
                    float cost = inVault ? Climbing.VaultStamina : Climbing.PullUpStamina;
                    if (cost > 0f && !player.HaveStamina(cost)) { Attempt(player, rise, "none", "-", $"{(inVault ? "vault" : "pull-up")} needs {cost:0} stamina"); return; }
                    bool vault = inVault;
                    // The Malbers S_Vault_1M hand-plant vault, ClimbUp_1m if the bundle lacks it.
                    ClimbClip clip = vault ? (ClimbAnimator.HasClip("S_Vault_1M") ? ClimbClip.Vault : ClimbClip.Up1m) : ClimbClip.Up2m;
                    if (TryBegin(player, face, angle, surface, false, vault ? "vault (forward + jump)" : "pull-up (forward + jump)", top, rise, top, clip))
                    {
                        if (cost > 0f) player.UseStamina(cost);
                        Attempt(player, rise, vault ? "vault" : "pull-up", clip.ToString(), vault ? $"in {Climbing.VaultMinFor(player):0.00}-{vaultMax:0.00} m" : $"in {Climbing.PullUpMinFor(player):0.00}-{pullUpMax:0.0} m");
                    }
                    return;
                }
                if (!Climbing.ClimbOn) { Attempt(player, rise, "none", "-", $"no vault / pull-up for {rise:0.00} m and climbing is off"); return; }
                if (rise < Climbing.ClimbMinFor(player)) { Attempt(player, rise, "none", "-", $"no lock: face {rise:0.00} m < min {Climbing.ClimbMinFor(player):0.00} m; {rise:0.00} m is under the climbing min trigger {Climbing.ClimbMinFor(player):0.00} m (and outside the vault / pull-up ranges)"); return; }
                if (PieceTooLowToClimb(player, face, out string lowWhy)) { Attempt(player, rise, "none", "-", lowWhy); return; }
                if (!AtJumpApex(player, face, rise)) return;
                Attempt(player, rise, "climb", "loops", $"over the pull-up max {pullUpMax:0.0} m");
            }
            else
            {
                if (PieceTooLowToClimb(player, face, out string lowWhy)) { Attempt(player, -1f, "none", "-", lowWhy); return; }
                if (!Climbing.ClimbOn) { Attempt(player, -1f, "none", "-", "no top found and climbing is off"); return; }
                if (!player.IsSwimming() && !AtJumpApex(player, face, -1f)) return;
                Attempt(player, -1f, "climb", "loops", player.IsSwimming() ? "swimming" : "no top found ahead");
            }
            if (TryBegin(player, face, angle, surface, false, "forward + jump against the face", face.point, 0f, new Vector3(float.NaN, 0f, 0f)))
                Debug.Log($"[Climb] lock: Space held at the jump apex on {surface} (let go of Jump to drop off)");
        }

        // A
        // build piece whose solid colliders top out under 2.5 m over the feet is never climbed (it is a jump or a pull-up, or nothing); the
        // top probe can read a piece's smoke / effect volume as its height
        private const float PieceClimbMin = 2.5f;

        // A climb never locks on 1 m off the ground under a high min trigger height:
        // a climb only locks on in the air at the top of a jump
        // (vertical speed at or under 0.5 m/s). Holding Jump on the ground used to grab the face at once - from the ground, at any height.
        // The face-height check (rise >= Climbing min trigger height) runs before this. Refusals log once per face + reason.
        private const float ApexSpeed = 0.5f;
        private static string s_noLockKey;
        private static bool AtJumpApex(Player player, RaycastHit face, float rise)
        {
            float vy = UpFrame.At(player.transform.position).V(player.GetVelocity());   // halo ring: speed along the local up
            if (!player.IsOnGround() && vy <= ApexSpeed) { s_noLockKey = null; return true; }
            string key = (face.collider != null ? face.collider.GetInstanceID() : 0) + (player.IsOnGround() ? "g" : "u");
            if (key != s_noLockKey)
            {
                s_noLockKey = key;
                FiresCore.Logging.FiresLogger.LogDebug($"[Climb] no lock: not at the jump apex ({(player.IsOnGround() ? "on the ground" : "still rising")}, vy {vy:0.00}; face {(rise >= 0f ? rise.ToString("0.00") + " m" : "no top found")}, min {Climbing.ClimbMinFor(player):0.00} m)");
            }
            return false;
        }
        private static bool PieceTooLowToClimb(Player player, RaycastHit face, out string why)
        {
            why = null;
            Piece piece = face.collider != null ? face.collider.GetComponentInParent<Piece>() : null;
            if (piece == null) return false;
            UpFrame f = UpFrame.At(player.transform.position);   // halo ring
            float feet = f.H(player.transform.position), top = float.NegativeInfinity;
            foreach (Collider c in piece.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger) top = Mathf.Max(top, f.TopOf(c.bounds));
            if (float.IsNegativeInfinity(top) || top - feet >= PieceClimbMin) return false;
            why = $"build piece {Utils.GetPrefabName(piece.gameObject)} is {top - feet:0.00} m over the feet (< {PieceClimbMin:0.0} m): no climb";
            return true;
        }

        private static bool s_jumpOverPending;
        private static float s_jumpOverAt;
        // How far in from the face the top is looked for (m), nearest the usual vault first.
        private static readonly float[] TopInset = { 0.5f, 0.35f, 0.7f, 1.0f };

        // The first solid top in from the face, out of the water, with room to stand; rise = its height over the feet.
        private static bool TopAhead(Player player, RaycastHit face, Vector3 into, out Vector3 top, out float rise)
        {
            top = Vector3.zero; rise = 0f;
            UpFrame f = UpFrame.At(player.transform.position);   // halo ring: the start's frame
            float feet = f.H(player.transform.position);
            int mask = FiresCore.World.Surface.Mask;
            float pullUpMax = Climbing.PullUpMaxFor(player), k = Climbing.SizeScale(player);
            // Thin walls (a ~0.2 m wood wall just over 2 m found no top): the insets below start 0.35 m past the
            // face, beyond a thin piece. Probe close behind the hit face first; the thick-wall result below still wins when it agrees.
            bool thin = ThinTop(player, face, into, feet, mask, f, out Vector3 thinTop, out float thinRise);
            foreach (float inset in TopInset)
            {
                Vector3 s = face.point + into * inset;
                if (!Climbing.TopSolidAt(s, f, true, out float h, out _)) continue;
                float r = h - feet;
                if (thin && r < thinRise - 0.15f) break;   // Landed past a thin wall (the far-side ground): use the thin top
                if (r < -Climbing.MaxSpotDrop || r > pullUpMax + TopReadSlack) { rise = r; top = f.WithH(s, h); return r > pullUpMax; }
                Vector3 spot = f.WithH(s, h + Climbing.SpotLift);
                if (Physics.CheckCapsule(spot + f.Up * Climbing.StandKneeHeight * k, spot + f.Up * Climbing.StandHeadHeight * k, Climbing.StandCapsuleRadius * k, mask, QueryTriggerInteraction.Ignore)) continue;
                top = spot; rise = r;
                return true;
            }
            if (thin) { top = thinTop; rise = thinRise; return true; }
            return false;
        }

        // Depths behind the hit face for the thin-wall top probe (m).
        private static readonly float[] ThinDepth = { 0.05f, 0.1f, 0.2f, 0.35f };

        // The top of a thin face: a downward ray at each depth just behind the hit point, first roughly flat hit (normal.y > 0.6)
        // inside the band; else the hit collider's bounds.max.y when that collider is thin (<= 0.5 m through along the face normal).
        private static bool ThinTop(Player player, RaycastHit face, Vector3 into, float feet, int mask, UpFrame f, out Vector3 top, out float rise)
        {
            top = Vector3.zero; rise = 0f;
            float pullUpMax = Climbing.PullUpMaxFor(player);
            float ceiling = feet + pullUpMax + TopReadSlack;
            Vector3 back = f.Flat(-face.normal);
            back = back.sqrMagnitude > 0.0001f ? back.normalized : into;
            string name = face.collider != null ? face.collider.name : "?";
            foreach (float depth in ThinDepth)
            {
                Vector3 p = face.point + back * depth;
                if (!Physics.Raycast(f.WithH(p, ceiling + 0.5f), f.Down, out RaycastHit hit, ceiling + 0.5f - f.H(face.point) + 0.05f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (f.V(hit.normal) <= 0.6f || hit.collider == null || hit.collider.attachedRigidbody != null) continue;
                float r = f.H(hit.point) - feet;
                if (r < 0.3f || r > pullUpMax + TopReadSlack) continue;
                top = f.WithH(p, f.H(hit.point) + 0.05f); rise = r;
                FiresCore.Logging.FiresLogger.LogDebug($"[Climb] top probe: {r:0.00} m via depth {depth:0.00} ({hit.collider.name})");
                return true;
            }
            Collider c = face.collider;
            if (c != null && c.GetComponentInParent<Heightmap>() == null &&!(c is MeshCollider mc && !mc.convex && c.bounds.size.magnitude > 20f))
            {
                Bounds b = c.bounds;
                float through = Mathf.Abs(Vector3.Dot(b.size, new Vector3(Mathf.Abs(back.x), f.Ring ? Mathf.Abs(back.y) : 0f, Mathf.Abs(back.z))));
                float r = f.TopOf(b) - feet;
                if (through <= 0.5f && f.TopOf(b) > f.H(face.point) && r >= 0.3f && r <= pullUpMax + TopReadSlack)
                {
                    Vector3 p = face.point + back * Mathf.Min(0.1f, through * 0.5f);
                    top = f.WithH(p, f.TopOf(b) + 0.05f); rise = r;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Climb] top probe: {r:0.00} m via bounds ({name})");
                    return true;
                }
            }
            return false;
        }

        // In mid-air, moving into a face whose lip is within hand reach: pull up onto it, no Jump press.
        private const float EdgeReachMin = 0.6f, EdgeReachMax = 2.2f;
        private static float s_edgeLogAt;

        private static void TryEdgePullUp(Player player)
        {
            Vector3 forward = player.transform.forward;
            if (!Climbing.FaceAhead(player, forward, out RaycastHit face, out float angle, out string surface)) return;
            Vector3 into = UpFrame.At(player.transform.position).Flat(-face.normal);   // halo ring
            if (into.sqrMagnitude < 0.0001f || Vector3.Dot(forward, into.normalized) < 0.5f) return;
            if (!TopAhead(player, face, into.normalized, out Vector3 top, out float rise)) return;
            if (!Climbing.PullUpOn) return;   // The mid-air ledge grab is a pull-up
            float k = Climbing.SizeScale(player), reachMin = EdgeReachMin * k, reachMax = EdgeReachMax * k;
            if (rise < reachMin || rise > reachMax)
            {
                if (Time.time >= s_edgeLogAt) { s_edgeLogAt = Time.time + EdgeLogInterval; FiresCore.Logging.FiresLogger.LogDebug($"[Climb] edge pull-up: {Name(player)} lip {rise:0.00} m over the feet, outside {reachMin:0.0}-{reachMax:0.0} m"); }
                return;
            }
            if (TryBegin(player, face, angle, surface, false, "edge pull-up (mid-air)", top, rise, top, ClimbClip.Up2m))
                Attempt(player, rise, "edge pull-up", ClimbClip.Up2m.ToString(), $"mid-air, lip within {reachMin:0.0}-{reachMax:0.0} m");
        }

        // One line per attempt for tuning the bands.
        private static void Attempt(Player player, float rise, string band, string clip, string why) =>
            FiresCore.Logging.FiresLogger.LogDebug($"[Climb] attempt: {Name(player)} top {(rise >= 0f ? $"{rise:0.00} m" : "?")}, band {band}, clip {clip} ({why})");

        private static void Step(Character body, Run run, float dt)
        {
            if (body.IsDead()) { End(body, run, "dead"); return; }
            Rigidbody rb = body.m_body;
            UpFrame rf = run.F;   // halo ring: the frame this climb started in (world up elsewhere)
            switch (run.State)
            {
                case State.Engage:
                    // A player's vault / pull-up skips the 0.4 s engage hold
                    // (velocity zeroed at the face) and tops out on its first step; the climb and planned mantles keep the hold.
                    bool quick = run.MantleClip != ClimbClip.None && run.Mantle;
                    if (!quick) Hold(body, rb, run, Vector3.zero);
                    if (quick || Time.time - run.Since >= EngageSeconds)
                    {
                        // A bank exit could otherwise climb the whole cliff above it from the water,
                        // and the bot drowned at the face: a mantle is a vault onto the rim it planned, not a climb up whatever face is there.
                        if (run.Mantle)
                        {
                            if (MantleTarget(body, run, out Vector3 onto, out string tried, out float topRise))
                            {
                                Vector3 from = body.transform.position;
                                float inward = rf.FlatDist(from, onto);
                                run.State = State.TopOut;
                                run.Since = Time.time;
                                run.TopFrom = from;
                                run.TopTo = onto;
                                run.TopOutTime = TopOutTimeFor(from, onto, run.F, Climbing.SizeScale(body as Player));
                                SetZdo(body, run, run.MantleClip != ClimbClip.None ? run.MantleClip : rf.H(onto) - rf.H(from) > LedgeClipMinRise ? ClimbClip.Ledge : ClimbClip.Up1m);
                                Debug.Log($"[Climb] climb: {Name(body)} mantle{(run.MantleClip != ClimbClip.None ? $" ({run.MantleClip})" : "")}: vaulting {rf.H(onto) - rf.H(from):0.0} m onto ({onto.x:0.0}, {onto.y:0.0}, {onto.z:0.0}), {inward:0.0} m in, over {run.TopOutTime:0.0} s");
                                return;
                            }
                            // A "rim" whose top is far higher than planned, climbed from the water on no
                            // stamina: no clear top in reach = not a rim. From the water it is barred and the swim exit picks again, unless the
                            // stamina pays for the whole real face (x1.2), which it then climbs as a paid climb (the last MantleMaxHeight a pull-up).
                            float faceCost = topRise > 0f ? Climbing.StaminaFor(body, topRise, Mathf.Max(run.Angle, Climbing.MinAngle)) * NoTopFaceCostMargin : float.MaxValue;
                            if (body.IsSwimming() && Climbing.StaminaOf(body) < faceCost)
                            {
                                Debug.Log($"[Climb] climb: {Name(body)} mantle: not a rim ({tried}; the face needs {(faceCost < float.MaxValue ? $"{faceCost:0}" : "?")} stamina, " +
                                          $"{Climbing.StaminaOf(body):0} in hand); barred, the swim exit picks again");
                                FiresCore.Npc.AI.Swimming.MarkUnclimbable(body, run.FacePoint);
                                ToFall(body, run, "not a rim from the water");
                                return;
                            }
                            Debug.Log($"[Climb] climb: {Name(body)} mantle: no clear top to vault onto ({tried}); climbing the face instead" +
                                      (topRise > 0f ? $" ({topRise:0.0} m, paid)" : ""));
                            run.Mantle = false;
                            if (topRise > 0f) run.PlanHeight = topRise;
                        }
                        run.State = State.Climb;
                        run.Since = Time.time;
                        run.BestY = rf.H(body.transform.position);
                        run.BestYAt = Time.time;
                        run.LastPos = body.transform.position;
                        SetZdo(body, run, ClimbClip.UpLoop);
                    }
                    return;
                case State.TopOut:
                {
                    float k = Mathf.Clamp01((Time.time - run.Since) / (run.TopOutTime > 0f ? run.TopOutTime : TopOutSeconds));
                    Vector3 target;
                    if (!float.IsNaN(run.TopApexY))
                    {
                        // Over a thin wall: up to the apex above its top, across, then down onto the far side.
                        float apex = run.TopApexY;
                        target = k < 0.4f ? rf.WithH(run.TopFrom, Mathf.Lerp(rf.H(run.TopFrom), apex, k / 0.4f))
                            : k < 0.75f ? Vector3.Lerp(rf.WithH(run.TopFrom, apex), rf.WithH(run.TopTo, apex), (k - 0.4f) / 0.35f)
                            : rf.WithH(run.TopTo, Mathf.Lerp(apex, rf.H(run.TopTo), (k - 0.75f) / 0.25f));
                    }
                    else target = k < 0.5f
                        ? rf.WithH(run.TopFrom, Mathf.Lerp(rf.H(run.TopFrom), rf.H(run.TopTo), k * 2f))
                        : Vector3.Lerp(rf.WithH(run.TopFrom, rf.H(run.TopTo)), run.TopTo, (k - 0.5f) * 2f);
                    // A player's vault / pull-up blends its run-in velocity into the scripted path over the crossfade (no stop-start).
                    float since = Time.time - run.Since;
                    if (run.MantleClip != ClimbClip.None && since < BlendSeconds && run.EntryVel.sqrMagnitude > 0.01f)
                    {
                        Vector3 carry = rf.Ring ? run.EntryVel + rf.U * (Mathf.Max(0f, rf.V(run.EntryVel)) - rf.V(run.EntryVel)) : new Vector3(run.EntryVel.x, Mathf.Max(0f, run.EntryVel.y), run.EntryVel.z);
                        target = Vector3.Lerp(run.TopFrom + carry * since, target, since / BlendSeconds);
                    }
                    Hold(body, rb, run, Vector3.zero);
                    rb.MovePosition(target);
                    if (k >= 1f)
                    {
                        float climbed = rf.H(run.TopTo) - run.StartY;
                        Debug.Log($"[Climb] climb: {Name(body)} topped out after {climbed:0.0} m up {run.Surface} in {Time.time - run.StartedAt:0.0} s, " +
                                  $"{run.Charged:0} stamina for the climb ({Climbing.StaminaOf(body):0} left), facing {OffFace(body, run):0}° off the face ({run.Reason})");
                        End(body, run, null);
                    }
                    return;
                }
                case State.Fall:
                    if (Time.time - run.Since >= FallShowSeconds && (body.IsOnGround() || body.IsSwimming())) End(body, run, null);
                    return;   // vanilla physics: gravity, the slide, fall damage
            }

            // Climb.
            ClimbTerms terms = Climbing.TermsFor(body);
            if (!terms.Allowed) { ToFall(body, run, "climbing was disallowed"); return; }
            // The face can be steeper than the
            // ground samples priced it: with no more than MantleMaxHeight left to the planned top, the last of it is a pull-up.
            bool pullUp = run.PlanHeight > 0f && run.PlanHeight - (rf.H(body.transform.position) - run.StartY) <= Climbing.MantleMaxHeight;
            // A mantle that found no top to vault onto and climbs on is a mantle only up to the rim it planned; past it,
            // a climb under the stamina rules.
            if (run.Mantle && rf.H(body.transform.position) - run.MantleBaseY > run.PlanHeight + MantlePastRimSlack) run.Mantle = false;
            // A planned climb that gains nothing in ClimbHeadwaySeconds is going nowhere (e.g. 40 s at 0.2 m, a wet slip each step,
            // the swim exit never given up because the climb held the body): it lets go, and a face it met from the water is barred.
            if (run.Planned)
            {
                if (rf.H(body.transform.position) > run.BestY + ClimbHeadwayMetres) { run.BestY = rf.H(body.transform.position); run.BestYAt = Time.time; }
                else if (Time.time - run.BestYAt > ClimbHeadwaySeconds)
                {
                    bool wet = body.IsSwimming();
                    ToFall(body, run, $"no headway up the face in {ClimbHeadwaySeconds:0} s{(wet ? " (in the water)" : "")}");
                    if (wet) FiresCore.Npc.AI.Swimming.MarkUnclimbable(body, run.FacePoint);
                    return;
                }
            }
            // Never at the face in the water with no stamina (vanilla drowns a swimmer at 0 whatever it does): off the face, and the
            // swim exit picks again (at 0 stamina only a mantle rim qualifies).
            if (body is Player sp && sp.GetStamina() <= 0f && body.IsSwimming() && !run.Mantle)
            {
                ToFall(body, run, "out of stamina in the water");
                FiresCore.Npc.AI.Swimming.MarkUnclimbable(body, run.FacePoint);
                return;
            }
            // Once locked on the wall the player can just hang there without holding W or space; only actually
            // climbing uses stamina, and a second Space releases from the
            // wall"): a player's climb follows the move keys on the wall (W up, S down, A / D along it) and hangs with none held.
            Vector3 input = run.Planned ? Vector3.zero : body.m_moveDir;
            input = rf.Flat(input);
            Vector3 wallRight = Vector3.Cross(rf.Up, run.WallDir).normalized;
            float upIn = run.Planned ? 1f : input.sqrMagnitude > 0.01f ? Mathf.Clamp(Vector3.Dot(input, run.WallDir), -1f, 1f) : 0f;
            float sideIn = run.Planned ? 0f : input.sqrMagnitude > 0.01f ? Mathf.Clamp(Vector3.Dot(input, wallRight), -1f, 1f) : 0f;
            bool hanging = Mathf.Abs(upIn) < InputDeadzone && Mathf.Abs(sideIn) < InputDeadzone;
            // Out of stamina: a planned climb, or a player trying to move, falls; a player hanging still stays (hanging is free).
            if (body is Player p && p.GetStamina() <= 0f && !run.Mantle && !pullUp && (run.Planned || !hanging)) { ToFall(body, run, "stamina ran out"); return; }
            bool hitNow = FiresCore.Npc.Combat.FireScare.HitLately(body, RecentHitSeconds, out Character hitter, out _);
            if (body.IsStaggering() || hitNow)
            {
                ToFall(body, run, $"hit{(hitter != null ? $" by {hitter.m_name}" : "")}");
                return;
            }
            // Fire 10-23: the full climb holds only while Jump (Space / the pad's jump, rebind-safe) is HELD; letting go drops off the wall
            // (normal fall, no re-grab until Jump is pressed again). Vaults, pull-ups and a top-out already under way keep their own rules;
            // a planned climb holds on until the top or its driver stops it.
            if (!run.Planned && body == Player.m_localPlayer)
            {
                bool jumpNow = JumpHeld();
                bool fullClimb = (run.State == State.Engage || run.State == State.Climb) && run.Clip != ClimbClip.Up1m && run.Clip != ClimbClip.Up2m && run.Clip != ClimbClip.Vault;
                if (fullClimb && !jumpNow)
                {
                    Debug.Log($"[Climb] release: Space let go after {Time.time - run.StartedAt:0.0} s on {run.Surface}");
                    ToFall(body, run, "released (Space let go)");
                    return;
                }
                run.JumpWasHeld = jumpNow;
                // Down at the foot of the face: off it, standing.
                if (upIn < -InputDeadzone && body.IsOnGround()) { End(body, run, "stepped down off the face"); return; }
            }
            // A turn under way carries
            // the body round the corner (position and facing over CornerSeconds); A/D at an outside edge or into an inside corner starts one.
            if (run.CornerUntil > 0f)
            {
                float ck = Mathf.Clamp01(1f - (run.CornerUntil - Time.time) / CornerSeconds);
                run.WallDir = Vector3.Slerp(run.CornerFromDir, run.CornerToDir, ck).normalized;
                Hold(body, rb, run, Vector3.zero);
                rb.MovePosition(Vector3.Lerp(run.CornerFromPos, run.CornerToPos, ck));
                if (ck >= 1f)
                {
                    run.CornerUntil = 0f;
                    run.CornerNext = Time.time + CornerSeconds;
                    run.WallDir = run.CornerToDir;
                    run.LostFaceSince = -1f;
                    run.LastPos = run.CornerToPos;
                }
                return;
            }
            if (Mathf.Abs(sideIn) >= InputDeadzone && Time.time >= run.CornerNext && TryCorner(body, run, wallRight * Mathf.Sign(sideIn), false)) return;
            if (Climbing.FaceAhead(body, run.WallDir, out RaycastHit face, out float angle, out string surface))
            {
                run.LostFaceSince = -1f;
                run.Normal = face.normal;
                Vector3 wall = rf.Flat(-face.normal);
                // (flattened above)
                if (wall.sqrMagnitude > 0.0001f) run.WallDir = wall.normalized;
                run.Angle = angle;
                // Mantle to the top once the arms/shoulders get past the top, rather than
                // climbing on air: going up, the face gone at hand height while still there lower down = the top edge is between the
                // chest and the hands: the top-out (onto it, over it, or the top-edge hang) starts now.
                if ((run.Planned || upIn > InputDeadzone) && !HandsOnFace(body, run) && !FaceAboveHands(body, run))
                {
                    if (Ledge(body, run, false, out Vector3 handLedge, out bool handOver, out float handApex, out bool handEdge))
                    {
                        Debug.Log($"[Climb] climb: {Name(body)} hands over the top of {run.Surface} at {rf.H(body.transform.position) - run.StartY:0.0} m up: mantling");
                        BeginTopOut(body, run, handLedge, handOver, handApex);
                        return;
                    }
                    if (handEdge && !run.Planned)
                    {
                        if (!run.TopEdgeLogged)
                        {
                            run.TopEdgeLogged = true;
                            Debug.Log($"[Climb] climb: {Name(body)} at the top edge of {run.Surface}, {rf.H(body.transform.position) - run.StartY:0.0} m up: the drop beyond is more than {OverMaxDrop:0} m; hanging (S down, Space lets go)");
                        }
                        Hold(body, rb, run, Vector3.zero);
                        if (run.Clip != ClimbClip.IdleLoop && Time.time >= run.SlipUntil) SetZdo(body, run, ClimbClip.IdleLoop);
                        run.LastPos = body.transform.position;
                        return;
                    }
                }
            }
            else
            {
                // Moving along and the face gone from right ahead: round the corner if a face goes on, before anything else.
                if (Mathf.Abs(sideIn) >= InputDeadzone && TryCorner(body, run, wallRight * Mathf.Sign(sideIn), true)) return;
                // Going up (or hanging) past the top: onto it, or over a thin wall.
                bool topEdge = false;
                if (run.Planned || upIn > -InputDeadzone)
                {
                    if (Ledge(body, run, true, out Vector3 ledge, out bool over, out float apexY, out topEdge))
                    {
                        BeginTopOut(body, run, ledge, over, apexY);
                        return;
                    }
                }
                // The face eased below the slide angle ahead (a slope's top): walking again.
                if (angle > 0f && angle < Climbing.MinAngle) { End(body, run, null); return; }
                if (topEdge && !run.Planned)
                {
                    // At a top it can neither stand on nor go over (the drop beyond is too long), a player hangs on the
                    // edge: no higher; S climbs back down, Space lets go.
                    run.LostFaceSince = -1f;
                    if (!run.TopEdgeLogged)
                    {
                        run.TopEdgeLogged = true;
                        Debug.Log($"[Climb] climb: {Name(body)} at the top edge of {run.Surface}, {rf.H(body.transform.position) - run.StartY:0.0} m up: the drop beyond is more than {OverMaxDrop:0} m; hanging (S down, Space lets go)");
                    }
                    if (upIn > -InputDeadzone)
                    {
                        Hold(body, rb, run, Vector3.zero);
                        if (run.Clip != ClimbClip.IdleLoop && Time.time >= run.SlipUntil) SetZdo(body, run, ClimbClip.IdleLoop);
                        run.LastPos = body.transform.position;
                        return;
                    }
                }
                else
                {
                    if (run.LostFaceSince < 0f) run.LostFaceSince = Time.time;
                    if (Time.time - run.LostFaceSince > LostFaceSeconds) { ToFall(body, run, $"lost the face{(angle > 0f ? $" ({angle:0}°)" : "")}"); return; }
                }
            }
            // Up the face's steepest way, hugging it.
            Vector3 up = Vector3.ProjectOnPlane(rf.Up, run.Normal);
            if (up.sqrMagnitude < 0.0001f) up = rf.Up;
            up.Normalize();
            float speed = Climbing.SpeedAt(terms, run.Angle);
            if (Time.time < run.SlipUntil) speed = 0f;
            // Along the face for a player's A / D (SideSpeedShare of the climb speed); a planned climb goes straight up.
            Vector3 side = Vector3.ProjectOnPlane(wallRight, run.Normal);
            side = side.sqrMagnitude > 0.0001f ? side.normalized : wallRight;
            Vector3 move = hanging ? Vector3.zero : up * speed * upIn + side * speed * SideSpeedShare * sideIn;
            Hold(body, rb, run, move - run.Normal * HugSpeed);
            // A mantle costs nothing.
            // Paid by the metre moved (a wet slip back down is not charged, and holding still is free).
            Vector3 nowPos = body.transform.position;
            // Only movement ALONG the face counts, never the bob into and out of it or a
            // swim's heave; a planned climb pays for its rise only.
            Vector3 step = nowPos - run.LastPos;
            float moved = Time.time < run.SlipUntil ? 0f
                : run.Planned ? Mathf.Max(0f, Vector3.Dot(step, up))
                : Mathf.Abs(Vector3.Dot(step, up)) + Mathf.Abs(Vector3.Dot(step, side));
            run.LastPos = nowPos;
            if (!run.Mantle && moved > 0.001f) { float cost = Climbing.CostPerMetre(terms, run.Angle, Climbing.IsWet(body)) * moved; body.UseStamina(cost); run.Charged += cost; }
            // Wet: each step may slip 0.25 m (a periodic chance to slip down); hanging still takes no steps.
            if (hanging) run.NextStep = Mathf.Max(run.NextStep, Time.time + HangStepDelay);
            if (Time.time >= run.NextStep)
            {
                run.NextStep = Time.time + Climbing.StepSeconds;
                if (body.GetSEMan() != null && body.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectWet) && UnityEngine.Random.value < terms.WetSlipChance)
                {
                    rb.MovePosition(body.transform.position - up * Climbing.WetSlipMetres);
                    run.SlipUntil = Time.time + WetSlipShowSeconds;
                    SetZdo(body, run, ClimbClip.DownLoop);
                    if (ZNet.instance != null) body.m_nview.GetZDO().Set(ClimbZdo.SlipHash, (float)ZNet.instance.GetTimeSeconds() + WetSlipShowSeconds);
                    Debug.Log($"[Climb] climb: {Name(body)} slipped {Climbing.WetSlipMetres:0.00} m (wet) at {rf.H(body.transform.position) - run.StartY:0.0} m");
                }
            }
            // The clip every peer shows: the hang (Climb_Idle_Loop) or the way it moves; a slip's DownLoop runs out first.
            if (Time.time >= run.SlipUntil)
            {
                ClimbClip clip = hanging ? ClimbClip.IdleLoop
                    : Mathf.Abs(sideIn) > Mathf.Abs(upIn) ? (sideIn > 0f ? ClimbClip.RightLoop : ClimbClip.LeftLoop)
                    : upIn > 0f ? ClimbClip.UpLoop : ClimbClip.DownLoop;
                if (clip != run.Clip)
                {
                    if (!run.Planned && (clip == ClimbClip.IdleLoop || run.Clip == ClimbClip.IdleLoop))
                        { float along = Vector3.Dot(body.transform.position - run.StartPos, wallRight); Debug.Log($"[Climb] climb: {Name(body)} {(clip == ClimbClip.IdleLoop ? "hanging" : "moving")} at {rf.H(body.transform.position) - run.StartY:0.0} m up, {Mathf.Abs(along):0.0} m {(along >= 0f ? "right" : "left")} of the grab on {run.Surface}, stamina {Climbing.StaminaOf(body):0}"); }
                    SetZdo(body, run, clip);
                }
            }
        }

        // The ledge over the edge: chest height has no face ahead, and 0.8 m on, a floor (under the slide angle) within 2.2 m above the feet.
        // The ledge was looked for only 0.8 m
        // ahead of the body, beyond a thin wall, on the ground behind it. Now the wall top first, nearest in (measured from the face),
        // where a body fits (floor under its width); a top too narrow to stand on is gone OVER to the far side when that drop is safe.
        private static readonly float[] LedgeInset = { 0.1f, 0.25f, 0.45f, 0.7f, 1.0f };
        private static readonly float[] OverReach = { 0.5f, 0.8f, 1.2f, 1.6f };
        /// <summary>Going over a thin wall lands no more than this (m) below its top.</summary>
        public const float OverMaxDrop = 3f;

        private static bool Ledge(Character body, Run run, bool estimateTop, out Vector3 ledge, out bool over, out float apexY, out bool topEdge)
        {
            ledge = Vector3.zero;
            over = false;
            apexY = float.NaN;
            topEdge = false;
            Vector3 at = body.transform.position;
            UpFrame f = run.F;   // halo ring: the run's frame
            int mask = FiresCore.World.Surface.Mask;
            float radius = body.GetRadius();
            float face = radius + BodyFaceGap;   // the face is about this far ahead of the body's middle
            float topY = float.NaN, topD = 0f;
            foreach (float inset in LedgeInset)
            {
                float d = face + inset;
                Vector3 probe = at + run.WallDir * d + f.Up * LedgeProbeAbove;
                if (!Physics.Raycast(probe, f.Down, out RaycastHit hit, LedgeProbeLength, mask, QueryTriggerInteraction.Ignore)) continue;
                if (f.Angle(hit.normal) >= Climbing.MinAngle) continue;
                float rise = f.H(hit.point) - f.H(at);
                if (rise > LedgeMaxRise || rise < LedgeMinRise) continue;   // too high, or the ground beyond a thin wall
                if (float.IsNaN(topY)) { topY = f.H(hit.point); topD = d; }
                if (!WideEnough(hit.point, run.WallDir, radius, mask, f)) continue;
                Vector3 spot = hit.point + f.Up * 0.05f;
                if (Physics.CheckCapsule(spot + f.Up * (radius + 0.1f), spot + f.Up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                ledge = spot + run.WallDir * LedgeStepIn;
                return true;
            }
            // A top too thin for any probe: under the lowest face ray, which missed (only when the face is gone at every ray; the hands'
            // check needs a measured top, or a notch in a tall wall would read as its top).
            if (float.IsNaN(topY)) { if (!estimateTop) return false; topY = f.H(at) + 0.4f; }
            // Over it: the floor on the far side, within OverMaxDrop below the top, with room to stand.
            foreach (float reach in OverReach)
            {
                Vector3 from = at + run.WallDir * (Mathf.Max(topD, face) + reach) + f.Up * (topY - f.H(at) + 1.0f);
                if (!Physics.Raycast(from, f.Down, out RaycastHit far, 1.0f + OverMaxDrop + 0.5f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (f.Angle(far.normal) >= Climbing.MinAngle) continue;
                float drop = topY - f.H(far.point);
                if (drop < -0.2f || drop > OverMaxDrop) continue;
                Vector3 spot = far.point + f.Up * 0.05f;
                if (Physics.CheckCapsule(spot + f.Up * (radius + 0.1f), spot + f.Up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                ledge = spot;
                over = true;
                apexY = topY + OverApexAboveTop;
                return true;
            }
            // No way onto or over the top (a long drop beyond): the body is at the top edge; it hangs there.
            topEdge = true;
            return false;
        }

        // Onto the top (Up1m / Ledge clip) or, over a thin wall, up / across / down to the far side (Ledge clip).
        private static void BeginTopOut(Character body, Run run, Vector3 ledge, bool over, float apexY)
        {
            if (body is Player && !HeadroomClear(body, over && !float.IsNaN(apexY) ? run.F.WithH(ledge, Mathf.Max(run.F.H(ledge), apexY)) : ledge, out string ceiling))
            {
                Debug.Log($"[Climb] top-out refused: {ceiling} (ledge at y {run.F.H(ledge):0.0}); letting go");
                ToFall(body, run, "top-out blocked by a ceiling");
                return;
            }
            Vector3 from = body.transform.position;
            run.State = State.TopOut;
            run.Since = Time.time;
            run.TopFrom = from;
            run.TopTo = ledge;
            run.TopApexY = over ? apexY : float.NaN;
            run.TopOutTime = over
                ? Mathf.Clamp(0.6f + 0.25f * Mathf.Max(0f, apexY - run.F.H(from)) + 0.2f * run.F.FlatDist(from, ledge) + 0.1f * Mathf.Max(0f, apexY - run.F.H(ledge)), 0.8f, 2.0f)
                : TopOutTimeFor(from, ledge, run.F, Climbing.SizeScale(body as Player));
            SetZdo(body, run, over || run.F.H(ledge) - run.F.H(from) > LedgeClipMinRise ? ClimbClip.Ledge : ClimbClip.Up1m);
            if (over) Debug.Log($"[Climb] climb: {Name(body)} over the top of {run.Surface} onto the far side, {apexY - OverApexAboveTop - run.F.H(ledge):0.0} m down, over {run.TopOutTime:0.0} s");
        }

        /// <summary>The hands' height over the feet while climbing (m); the face gone there = the hands are over the top.</summary>
        public const float HandHeight = 1.7f;

        // The face still in front of the hands (a climbable steep surface within reach at HandHeight).
        private static bool HandsOnFace(Character body, Run run) => FaceAt(body, run, HandHeight);

        // A window sill or a recess at hand height must not
        // read as the top: the wall going on above the hands (0.5 or 1.0 m higher) is no top.
        private static bool FaceAboveHands(Character body, Run run) => FaceAt(body, run, HandHeight + 0.5f) || FaceAt(body, run, HandHeight + 1.0f);

        // A climbable steep surface within reach in front of the body at this height over its feet.
        private static bool FaceAt(Character body, Run run, float height)
        {
            Vector3 from = body.transform.position + run.F.Up * height;
            if (!Physics.Raycast(from, run.WallDir, out RaycastHit hit, Climbing.FaceReach + 0.2f, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore)) return false;
            return run.F.Angle(hit.normal) >= Climbing.MinAngle;
        }

        // Floor under a body's width round the top point (a wall top a body can stand on, not a beam): the top found half a body
        // either side along the wall, at about the same height.
        private static bool WideEnough(Vector3 top, Vector3 wallDir, float radius, int mask, UpFrame f)
        {
            Vector3 side = Vector3.Cross(f.Up, wallDir);
            foreach (Vector3 offset in new[] { side * radius, -side * radius, wallDir * radius * 0.8f })
            {
                Vector3 p = top + offset + f.Up * 0.6f;
                if (!Physics.Raycast(p, f.Down, out RaycastHit h, 1.0f, mask, QueryTriggerInteraction.Ignore)) return false;
                if (Mathf.Abs(f.H(h.point) - f.H(top)) > 0.3f) return false;
            }
            return true;
        }

        // On the face: gravity off, the slide held at 0, facing the wall, this velocity; no fall damage builds up from the climb itself.
        // The facing goes on the transform as well as the rigidbody, after vanilla's UpdateRotation turned it toward the
        // driver's move / look this same step (otherwise a bot walked on to its next point mid-climb), so ZSyncTransform sends the
        // wall-facing rotation to every peer.
        private static void Hold(Character body, Rigidbody rb, Run run, Vector3 velocity)
        {
            rb.useGravity = false;
            rb.linearVelocity = velocity;
            body.m_slippage = 0f;
            body.m_sliding = false;
            body.m_maxAirAltitude = body.transform.position.y;
            if (run.WallDir.sqrMagnitude > 0.0001f)
            {
                Quaternion facing = run.F.Look(run.WallDir);   // halo ring: upright to the local up
                body.transform.rotation = facing;
                rb.rotation = facing;
            }
            // Vanilla's UpdateBodyFriction gives the capsule
            // 0.4 (moving) / 0.8 (standing) friction whenever a face reads as ground, and the hug into the face dragged against it. On the
            // face the capsule slides: no friction this step.
            if (body.m_collider != null && body.m_collider.material != null)
            {
                body.m_collider.material.staticFriction = 0f;
                body.m_collider.material.dynamicFriction = 0f;
                body.m_collider.material.frictionCombine = PhysicsMaterialCombine.Minimum;
            }
            // Off the ground and out of the
            // water vanilla sets the animator's "falling" after 0.1 s, synced to every peer, and its transition pulled the body out of the
            // climb clip. Held false while on the face (the owner writes it; ZSyncAnimation carries it).
            body.m_fallTimer = 0f;
            if (body.m_zanim != null) body.m_zanim.SetBool(s_fallingHash, false);
        }

        private static readonly int s_fallingHash = ZSyncAnimation.GetHash("falling");

        private static void ToFall(Character body, Run run, string why)
        {
            run.State = State.Fall;
            run.Since = Time.time;
            body.m_maxAirAltitude = body.transform.position.y;   // the fall counts from here
            SetZdo(body, run, ClimbClip.Exit);
            Debug.Log($"[Climb] climb: {Name(body)} fell: {why} at {run.F.H(body.transform.position) - run.StartY:0.0} m up {run.Surface}, {run.Charged:0} stamina for the climb, {Climbing.StaminaOf(body):0} left, " +
                      $"facing {OffFace(body, run):0}° off the face");
        }

        // Why this player may not climb right now (debug fly, ghost mode, noclip = its collider off), or null
        private static string NoClimbMode(Character body)
        {
            if (!(body is Player p)) return null;
            if (p.IsDebugFlying()) return "debug fly is on";
            if (p.InGhostMode()) return "ghost mode is on";
            var col = p.GetCollider();
            if (col != null && !col.enabled) return "noclip (collider off)";
            return null;
        }

        // Headroom along a vault / top-out path for the body's capsule: straight up from where it stands to just over the landing height,
        // then across onto the landing spot. Terrain, pieces (roofs, floors, walls), static solids and vehicles block; the body's own
        // colliders and triggers never do. One Debug line per check (clear or the hit).
        private static int s_headroomMask = -1;
        private static readonly RaycastHit[] s_headHits = new RaycastHit[16];
        private static bool HeadroomClear(Character body, Vector3 landing, out string hit)
        {
            hit = null;
            var cap = body.GetComponent<CapsuleCollider>();
            if (cap == null) return true;
            if (s_headroomMask == -1) s_headroomMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            float r = cap.radius * Mathf.Max(body.transform.lossyScale.x, body.transform.lossyScale.z) * 0.9f;
            float h = Mathf.Max(cap.height * body.transform.lossyScale.y, r * 2f);
            Vector3 c = body.transform.TransformPoint(cap.center);
            UpFrame f = UpFrame.At(body.transform.position);   // halo ring: the capsule stands along the local up
            Vector3 bottom = c - f.Up * (h * 0.5f - r), top = c + f.Up * (h * 0.5f - r);
            float rise = Mathf.Max(0f, f.H(landing) + 0.1f - f.H(body.transform.position));
            if (rise > 0.01f && Cast(body, bottom, top, r, f.Up, rise, out hit)) { Debug.Log($"[Climb] headroom: {hit} on the way up"); return false; }
            Vector3 up = f.Up * rise;
            Vector3 across = f.Ring ? f.Flat(landing - c) : new Vector3(landing.x - c.x, 0f, landing.z - c.z);
            if (across.magnitude > 0.05f && Cast(body, bottom + up, top + up, r, across.normalized, across.magnitude, out hit)) { Debug.Log($"[Climb] headroom: {hit} on the way across"); return false; }
            Debug.Log($"[Climb] headroom clear: up {rise:0.0} m, across {across.magnitude:0.0} m");
            return true;
        }

        private static bool Cast(Character body, Vector3 p1, Vector3 p2, float r, Vector3 dir, float dist, out string hit)
        {
            hit = null;
            int n = Physics.CapsuleCastNonAlloc(p1, p2, r, dir, s_headHits, dist, s_headroomMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var col = s_headHits[i].collider;
                if (col == null || col.transform.IsChildOf(body.transform)) continue;
                if (s_headHits[i].distance <= 0f) continue;   // already touching at the start (the wall it stands at): not a ceiling
                if (s_headHits[i].distance < best) { best = s_headHits[i].distance; hit = $"ceiling '{col.transform.root.name}' ({LayerMask.LayerToName(col.gameObject.layer)}) at y {s_headHits[i].point.y:0.0}, {s_headHits[i].distance:0.0} m along"; }
            }
            return hit != null;
        }

        private static void End(Character body, Run run, string why)
        {
            s_runs.Remove(body);
            run.State = State.Idle;
            SetZdo(body, run, ClimbClip.None);
            if (why != null) Debug.Log($"[Climb] climb: {Name(body)} ended: {why}");
        }

        private static void SetZdo(Character body, Run run, ClimbClip clip)
        {
            run.Clip = clip;
            ZDO zdo = body.m_nview != null && body.m_nview.IsValid() && body.m_nview.IsOwner() ? body.m_nview.GetZDO() : null;
            if (zdo == null) return;
            if (zdo.GetInt(ClimbZdo.StateHash, 0) != (int)run.State) zdo.Set(ClimbZdo.StateHash, (int)run.State);
            if (zdo.GetInt(ClimbZdo.ClipHash, 0) != (int)clip) zdo.Set(ClimbZdo.ClipHash, (int)clip);
        }

        private static string Name(Character c) => c is Player p ? p.GetPlayerName() : c != null ? c.m_name : "?";
    }

    /// <summary>
    /// Halo ring ([classes-ui] for [generator], lead 10-24): the up a climb measures heights along. Outside every gravity field
    /// (GravityUp.InField false, or no field in the world) Ring is false and every member answers exactly what the world-up code did
    /// (p.y, (x, 0, z), new Vector3(x, h, z), Vector3.up / down, LookRotation with the default up). Inside a field (FAT's ring) heights
    /// are Dot(p, U) with ONE U for the whole operation - a climb keeps the frame it started in (Run.F), so its stored heights stay
    /// consistent (a tangent plane: over a 10 m climb on a 4.8 km ring the floor bends 1 cm).
    /// </summary>
    public struct UpFrame
    {
        public readonly bool Ring;
        public readonly Vector3 U;

        private UpFrame(bool ring, Vector3 up) { Ring = ring; U = up; }

        private static bool s_logged;

        /// <summary>The frame at a point: the field's up there, else world up (Ring false).</summary>
        public static UpFrame At(Vector3 p)
        {
            if (!FiresCore.World.GravityUp.Active || !FiresCore.World.GravityUp.InField(p, out Vector3 up) || up.sqrMagnitude < 0.5f) return default;
            if (!s_logged) { s_logged = true; FiresCore.Logging.FiresLogger.LogDebug($"[Climb] gravity field: climb / vault / pull-up measure along the local up {up.normalized} at ({p.x:0}, {p.y:0}, {p.z:0})"); }
            return new UpFrame(true, up.normalized);
        }

        public Vector3 Up => Ring ? U : Vector3.up;
        public Vector3 Down => Ring ? -U : Vector3.down;
        /// <summary>Height of a point (world y off a field).</summary>
        public float H(Vector3 p) => Ring ? Vector3.Dot(p, U) : p.y;
        /// <summary>The up component of a direction / velocity (its y off a field).</summary>
        public float V(Vector3 v) => Ring ? Vector3.Dot(v, U) : v.y;
        /// <summary>The direction with its up part removed (y = 0 off a field).</summary>
        public Vector3 Flat(Vector3 v) { if (Ring) return Vector3.ProjectOnPlane(v, U); v.y = 0f; return v; }
        /// <summary>The point moved along up to height h (new Vector3(x, h, z) off a field).</summary>
        public Vector3 WithH(Vector3 p, float h) => Ring ? p + U * (h - Vector3.Dot(p, U)) : new Vector3(p.x, h, p.z);
        /// <summary>The flat distance between two points (x / z off a field).</summary>
        public float FlatDist(Vector3 a, Vector3 b) => Ring ? Vector3.ProjectOnPlane(b - a, U).magnitude : new Vector2(b.x - a.x, b.z - a.z).magnitude;
        /// <summary>A surface normal's angle from up.</summary>
        public float Angle(Vector3 normal) => Vector3.Angle(normal, Up);
        public Quaternion Look(Vector3 dir) => Ring ? Quaternion.LookRotation(dir, U) : Quaternion.LookRotation(dir);
        /// <summary>Compass yaw of a direction in this frame (0 = +z, 90 = +x off a field).</summary>
        public float Yaw(Vector3 dir)
        {
            dir = Flat(dir);
            if (dir.sqrMagnitude < 0.0001f) return 0f;
            return Ring ? (Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, U)) * Quaternion.LookRotation(dir, U)).eulerAngles.y : Quaternion.LookRotation(dir).eulerAngles.y;
        }
        /// <summary>The highest point of a (world-aligned) bounds along up (bounds.max.y off a field).</summary>
        public float TopOf(Bounds b)
        {
            if (!Ring) return b.max.y;
            Vector3 c = b.center, e = b.extents;
            return Vector3.Dot(c, U) + Mathf.Abs(e.x * U.x) + Mathf.Abs(e.y * U.y) + Mathf.Abs(e.z * U.z);
        }
    }
}
