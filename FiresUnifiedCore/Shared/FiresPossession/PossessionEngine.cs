using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresPossession
{
    /// <summary>
    /// Creature possession, shared by Core (public, FIRES_POSSESSION_PUBLIC) and Valcast (internal): the possessor drives a creature
    /// with its own keys while the creature's AI stands down, the creature's own attacks on LMB / MMB, everything restored at the end.
    /// One possession per client and per engine copy; when both copies are loaded, only the copy holding the AppDomain token
    /// "FiresPossession.Active" acts (its patches check it), and each copy patches under its own Harmony id.
    ///
    /// Network: only the ZDO owner simulates a character, so the possessor owns the creature's ZDO (claimed again every second) and
    /// the engine writes "valcast_possessed" = the possessor's playerID on it (FGN keeps that owner; FiresMonsters reads it; never
    /// rename it), clearing it at the end only if it still holds that id.
    ///
    /// The body: Frozen (stays where it stood, no controls; the creature's own body takes the keys) or Hidden (carried inside the
    /// creature, the whole player stack drives it: PossessionProxy). The caller decides who may possess what (admins, classes,
    /// servers); the engine refuses only what it can't drive (players, the dead, no AI) and the caller's Filter.
    /// </summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    static class PossessionEngine
    {
        public const string PossessedKey = "valcast_possessed";   // ZDO long on the creature: the possessor's playerID
        public const string TokenKey = "FiresPossession.Active";  // AppDomain data: the engine copy that is possessing
        private const float ClaimEverySeconds = 1f, PressWindow = 0.4f;
        private const float FlyFloor = 0.5f, FlyCeiling = 60f, FlyLand = 1.2f;

        private static readonly object s_token = new object();
        private static bool s_installed;
        private static Harmony s_harmony;

        private static Player s_player;
        private static Character s_creature;
        private static ZNetView s_view;
        private static ZDOID s_creatureId;
        private static PossessOptions s_opt;
        private static IPossessionHost s_host;
        private static IPossessionInput s_input;
        private static BaseAI s_ai;
        private static bool s_aiWasEnabled, s_driving, s_failed;
        private static long s_originalOwner;
        private static float s_nextClaim, s_startTime, s_lookYaw;
        private static Vector3 s_creatureAnchorPos, s_bodyAnchorPos;
        private static Quaternion s_creatureAnchorRot;

        // A tap of Jump / Attack lasts a few rendered frames and can fall between two FixedUpdates (Drive runs there), so a press is
        // caught per frame and kept until the next FixedUpdate uses it; an attack that can't start yet is retried for PressWindow.
        private static bool s_jumpHeldLast, s_attackHeldLast, s_secondaryHeldLast;
        private static bool s_jumpLatched, s_attackLatched, s_secondaryLatched;
        private static float s_pendingUntil;
        private static bool s_pendingSpecial;
        // RMB (Block) = the creature's third attack in a Frozen-body possession (a Hidden body keeps its block).
        private static bool s_thirdHeldLast, s_thirdLatched, s_pendingThird, s_noThirdSaid;
        private const float ArcadeYawRate = 90f;

        // The channel's levitation (ChannelLock + ChannelHover), and its descent after the end.
        private static float s_hoverOffset;
        private static Player s_descentPlayer;
        private static Vector3 s_descentAnchor;
        private static float s_descentFrom, s_descentStart, s_descentTime;

        private static readonly FlightModel s_flight = new FlightModel();
        private static readonly FlightTuning s_flightTuning = new FlightTuning { Log = s => s_host?.Log(s) };

        private static readonly System.Reflection.FieldInfo s_targetCreature = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        private static readonly System.Reflection.FieldInfo s_targetStatic = AccessTools.Field(typeof(MonsterAI), "m_targetStatic");
        private static readonly System.Reflection.MethodInfo s_wakeup = AccessTools.Method(typeof(MonsterAI), "Wakeup");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> s_body = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");

        public static event Action<Character> Began;
        /// <summary>Fires exactly once per possession: the creature (null if it was already destroyed) and a PossessReasons value.</summary>
        public static event Action<Character, string> Ended;

        public static bool IsPossessing => s_creature != null;
        /// <summary>
        /// Possessed flyers do not turn with the mouse; the mouse orbits the camera freely and only the keys steer:
        /// true while this copy's possessed creature is airborne on a Frozen body. The mouse doesn't turn it then, so the host's camera
        /// orbits with the mouse instead of trailing the creature's facing.
        /// </summary>
        public static bool CameraFree => Mine && s_creature != null && !PossessionProxy.Active && s_creature.IsFlying();
        public static Character Creature => s_creature;
        public static Player Possessor => s_player;
        public static PossessOptions Options => s_opt;
        /// <summary>The body is Hidden inside the creature (the proxy is on).</summary>
        public static bool BodyHidden => PossessionProxy.Active;
        /// <summary>The creature is driven by the possessor's keys (false = AUTO: its own AI drives).</summary>
        public static bool Driven => s_driving;
        public static string LastEndReason { get; private set; }
        public static string LastEndDetail { get; private set; }
        public static int EndCount { get; private set; }
        /// <summary>The possessed creature's heading (degrees), turned by the mouse; Frozen bodies only.</summary>
        public static float LookYaw { get => s_lookYaw; set => s_lookYaw = value; }
        /// <summary>A test's attack target in place of the creature under the crosshair (null = the crosshair).</summary>
        public static Character AimOverride;
        /// <summary>Add-only (small flyers are hard to aim): when nothing is under the crosshair, the host may
        /// pick the attack target (null = none). Core sets it for possessed flyers ("Flyer attack auto-lock").</summary>
        public static System.Func<Character, Character> AutoTarget;

        /// <summary>This engine copy holds the possession token (its patches act).</summary>
        /// Performance: a plain flag, set when this copy takes the token and cleared when it gives it back (another
        /// copy can't take a held token: its Begin refuses), so the per-character / per-AI-check patches never read the AppDomain.
        internal static bool Mine => s_holding;
        private static bool s_holding;

        /// <summary>The possessed creature's attacks: [0] Main (LMB), the Special (MMB), the rest Other (the cycle picks the Special).</summary>
        public static IReadOnlyList<PossessAttack> Attacks
        {
            get
            {
                if (s_creature is Humanoid h) PossessionAttacks.List(h);
                return PossessionAttacks.Public;
            }
        }

        /// <summary>The index in Attacks the special (MMB) fires; -1 when there is none.</summary>
        public static int CurrentAttack => IsPossessing && Attacks.Count > 0 ? PossessionAttacks.SpecialIndex : -1;

        /// <summary>A flyer: flying now, or a creature whose AI takes off and lands (bats, drakes).</summary>
        public static bool IsFlyer(Character c) => c != null && (c.m_flying || (c.GetComponent<BaseAI>()?.m_randomFly ?? false));

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //  Install / begin / end
        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Applies the engine's patches under the host's Harmony id (e.g. its GUID + ".possession"). Begin installs with a default id if the host didn't.</summary>
        public static bool Install(string harmonyId)
        {
            if (s_installed) return true;
            try
            {
                s_harmony = new Harmony(string.IsNullOrEmpty(harmonyId) ? DefaultHarmonyId : harmonyId);
                var t = typeof(PossessionEngine);
                Patch(s_harmony, typeof(PlayerController), "FixedUpdate", t, nameof(PlayerController_FixedUpdate_Prefix), postfix: false);
                Patch(s_harmony, typeof(Player), "TakeInput", t, nameof(Player_TakeInput_Postfix), postfix: true);
                Patch(s_harmony, typeof(GameCamera), "LateUpdate", t, nameof(GameCamera_LateUpdate_Postfix), postfix: true);
                Patch(s_harmony, typeof(Player), "OnDeath", t, nameof(Player_OnDeath_Postfix), postfix: true);
                Patch(s_harmony, typeof(ZNet), "OnDestroy", t, nameof(ZNet_OnDestroy_Prefix), postfix: false);
                Patch(s_harmony, typeof(Character), "RPC_Damage", t, nameof(Character_RPC_Damage_Prefix), postfix: false);
                Patch(s_harmony, typeof(Character), nameof(Character.Damage), t, nameof(Character_Damage_Prefix), postfix: false);
                Patch(s_harmony, typeof(Character), nameof(Character.CustomFixedUpdate), t, nameof(Character_CustomFixedUpdate_Postfix), postfix: true);
                s_harmony.Patch(AccessTools.Method(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) }),
                    postfix: new HarmonyMethod(AccessTools.Method(t, nameof(BaseAI_IsEnemy_Postfix))));
                PossessionProxy.Install(s_harmony);
                s_installed = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FiresPossession] install failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static string DefaultHarmonyId => "FiresPossession." + typeof(PossessionEngine).Assembly.GetName().Name + ".possession";

        internal static void Patch(Harmony h, Type type, string method, Type patchType, string patchMethod, bool postfix, bool last = false)
        {
            var original = AccessTools.Method(type, method);
            if (original == null) { Debug.LogWarning($"[FiresPossession] no {type.Name}.{method} to patch"); return; }
            var hm = new HarmonyMethod(AccessTools.Method(patchType, patchMethod));
            if (last) hm.priority = Priority.Last;
            if (postfix) h.Patch(original, postfix: hm); else h.Patch(original, prefix: hm);
        }

        /// <summary>
        /// Starts possessing 'creature'. Assumes the caller may (its own rules) and that the creature's ZDO is ours or can be claimed;
        /// accepts "valcast_possessed" when it is 0 or already this player's id. False + why when refused.
        /// </summary>
        public static bool Begin(Player player, Character creature, PossessOptions options, out string why)
        {
            why = null;
            options = options ?? new PossessOptions();
            if (!s_installed && !Install(null)) { why = "the possession engine could not install its patches"; return false; }
            if (player == null || player.IsDead()) { why = "no living player"; return false; }
            if (IsPossessing) { why = "already possessing"; return false; }
            object holder = AppDomain.CurrentDomain.GetData(TokenKey);
            if (holder != null && !ReferenceEquals(holder, s_token)) { why = "another mod is possessing a creature"; return false; }
            if (!Drivable(creature, out why)) return false;
            string refused = options.Filter?.Invoke(creature);
            if (refused != null) { why = refused; return false; }
            var view = creature.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) { why = "it isn't loaded on the network"; return false; }
            long holderId = view.GetZDO().GetLong(PossessedKey, 0L);
            if (holderId != 0L && holderId != player.GetPlayerID()) { why = "someone else is possessing it"; return false; }

            AppDomain.CurrentDomain.SetData(TokenKey, s_token);
            s_holding = true;
            s_player = player;
            s_creature = creature;
            s_view = view;
            s_creatureId = view.GetZDO().m_uid;
            s_opt = options;
            s_host = options.Host;
            s_input = options.Input ?? VanillaPossessionInput.Instance;
            s_ai = creature.GetComponent<BaseAI>();
            s_driving = false;
            s_failed = false;
            s_startTime = Time.time;
            s_lookYaw = LocalYaw(creature.transform);   // it keeps facing where it faced (halo ring: the yaw in the ring frame)
            s_creatureAnchorPos = creature.transform.position;
            s_creatureAnchorRot = creature.transform.rotation;
            s_bodyAnchorPos = player.transform.position;
            s_hoverOffset = 0f;
            s_descentPlayer = null;
            PossessionAttacks.Reset();
            ClearLatches();

            try
            {
                string noProxy = null;
                bool hidden = options.Body == BodyStyle.Hidden && options.Driven && PossessionProxy.CanProxy(creature, out noProxy);
                if (options.Body == BodyStyle.Hidden && !hidden) Log($"hidden body not possible ({noProxy ?? "AUTO drives itself"}): frozen");
                if (hidden) s_host?.CameraOff(); else s_host?.CameraOn(creature);
                if (options.Driven) TakeTheWheel(player);
                if (hidden) PossessionProxy.Begin(player, creature, s_host);
                // The body: no controls from here (the FixedUpdate prefix and the TakeInput postfix below); nothing else changes.
                else player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
                if (!hidden && options.BodyUntouchable) BodyFlagsOn(player);
                if (options.ChannelLock && !hidden) SafeCall(options.ChannelFxStart, player, "ChannelFxStart");
            }
            catch (Exception e)
            {
                Log($"WARN: begin failed: {e.GetType().Name}: {e.Message}");
                Finish(PossessReasons.Error, "begin failed: " + e.Message, fireEnded: false);   // it never began: no Ended
                why = "the possession failed to start: " + e.Message;
                return false;
            }
            Log($"possessing {creature.gameObject.name} ({(PossessionProxy.Active ? "hidden" : "frozen")} body, {(options.Driven ? "driven" : "auto")}, owner was {s_originalOwner}{(string.IsNullOrEmpty(options.SourceTag) ? "" : ", " + options.SourceTag)})");
            try { Began?.Invoke(creature); } catch (Exception e) { Log("WARN: a Began handler threw: " + e.Message); }
            return true;
        }

        // What the engine can't drive, whoever asks.
        private static bool Drivable(Character c, out string why)
        {
            why = null;
            if (c == null) { why = "no creature"; return false; }
            if (c.IsPlayer()) { why = "players can't be possessed"; return false; }
            if (c.IsDead()) { why = "it's dead"; return false; }
            var ai = c.GetComponent<BaseAI>();
            if (ai == null) { why = "it has no AI to stand down"; return false; }
            // Swimmers (MonsterAI.m_avoidLand: serpents) are driven too; Swim keeps them in deep water.
            return true;
        }

        // A swimmer's AI (MonsterAI.m_avoidLand -> MoveToWater) is what keeps it off land, and it stands down while driven,
        // so the drive does it instead: a move whose point ahead is shallower than SwimMinDepth is dropped (it stops at the shore).
        private const float SwimMinDepth = 1.5f;
        private static bool IsSwimmer(Character c) => c != null && c.GetComponent<BaseAI>() is MonsterAI m && m.m_avoidLand;

        private static Vector3 Swim(Character c, Vector3 world)
        {
            if (world.sqrMagnitude < 0.0001f) return world;
            Vector3 ahead = c.transform.position + world.normalized * (c.GetRadius() + 2f);
            float water = Floating.GetLiquidLevel(ahead);
            float ground = ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(ahead, out float g) ? g : water - 10f;
            return water - ground < SwimMinDepth ? Vector3.zero : world;
        }

        /// <summary>Ends the possession (safe at any time, any number of times); Ended fires once. reason: a PossessReasons value.</summary>
        public static void End(string reason, string detail = null) => Finish(reason, detail, fireEnded: true);

        private static void Finish(string reason, string detail, bool fireEnded)
        {
            if (s_creature == null && s_view == null && s_player == null) return;
            var player = s_player;
            var creature = s_creature;
            var view = s_view;
            var opt = s_opt ?? new PossessOptions();
            var host = s_host;
            bool driving = s_driving;
            s_creature = null;
            s_view = null;
            s_player = null;
            s_driving = false;

            try { if (PossessionProxy.Active) PossessionProxy.End(reason, atCreature: opt.ConsumeTarget); }
            catch (Exception e) { Log("WARN: ending the hidden body failed: " + e.Message); }

            try
            {
                if (creature != null) creature.SetMoveDir(Vector3.zero);
                if (driving && s_ai != null) s_ai.enabled = s_aiWasEnabled;
                if (view != null && view.IsValid())
                {
                    var zdo = view.GetZDO();
                    if (player != null && zdo.GetLong(PossessedKey, 0L) == player.GetPlayerID()) zdo.Set(PossessedKey, 0L);
                    if (opt.ConsumeTarget && view.IsOwner() && creature != null) { ZNetScene.instance?.Destroy(creature.gameObject); creature = null; }
                    else
                    {
                        if (opt.ReturnToAnchor && creature != null && !creature.IsDead()) PutBack(creature, s_creatureAnchorPos, s_creatureAnchorRot);
                        // Ownership goes back where it was; a peer that left is reassigned by the normal rules.
                        if (driving && opt.ReturnOwnership && s_originalOwner != 0L && s_originalOwner != ZDOMan.GetSessionID() && view.IsOwner()) zdo.SetOwner(s_originalOwner);
                    }
                }
            }
            catch (Exception e) { Log("WARN: restoring the creature failed: " + e.Message); }

            BodyFlagsOff(player);

            if (opt.ChannelLock && player != null)
            {
                SafeCall(opt.ChannelFxStop, player, "ChannelFxStop");
                if (s_hoverOffset > 0.01f && opt.HoverDescent > 0f)
                {
                    s_descentPlayer = player; s_descentAnchor = s_bodyAnchorPos; s_descentFrom = s_hoverOffset;
                    s_descentStart = Time.time; s_descentTime = opt.HoverDescent;
                }
            }
            s_hoverOffset = 0f;
            s_flight.Reset(s_lookYaw);
            s_ai = null;
            ClearLatches();
            PossessionAttacks.Reset();
            try { host?.CameraOff(); } catch (Exception e) { Log("WARN: CameraOff threw: " + e.Message); }
            if (ReferenceEquals(AppDomain.CurrentDomain.GetData(TokenKey), s_token)) AppDomain.CurrentDomain.SetData(TokenKey, null);
            s_holding = false;
            LastEndReason = reason;
            LastEndDetail = detail;
            EndCount++;
            host?.Log($"ended {(creature != null ? creature.gameObject.name : "(gone)")}: {reason}{(string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")}");
            s_opt = null;
            s_host = null;
            if (fireEnded) { try { Ended?.Invoke(creature, reason); } catch (Exception e) { host?.Log("WARN: an Ended handler threw: " + e.Message); } }
        }

        /// <summary>Switch between driven (the possessor's keys; the AI down, the ZDO ours) and AUTO (its own AI drives, the camera rides along).</summary>
        public static void SetDriven(bool driven)
        {
            if (!IsPossessing || PossessionProxy.Active || s_opt == null) return;
            s_opt.Driven = driven;
            if (driven) TakeTheWheel(s_player); else GiveBackTheWheel();
            ClearLatches();
        }

        private static void TakeTheWheel(Player player)
        {
            if (s_driving || s_view == null || !s_view.IsValid() || s_creature == null) return;
            s_originalOwner = s_view.GetZDO().GetOwner();
            if (!s_view.IsOwner()) s_view.ClaimOwnership();
            s_view.GetZDO().Set(PossessedKey, player.GetPlayerID());
            s_nextClaim = Time.time + ClaimEverySeconds;
            s_lookYaw = LocalYaw(s_creature.transform);
            s_flight.Reset(s_lookYaw);
            if (s_ai != null)
            {
                s_aiWasEnabled = s_ai.enabled;
                s_ai.enabled = false;   // BaseAI.OnDisable takes it off the game's AI tick list
            }
            if (s_ai is MonsterAI monster)
            {
                s_targetCreature?.SetValue(monster, null);
                s_targetStatic?.SetValue(monster, null);
                // A sleeping / buried creature sits in an animator state tagged "freeze" until its AI wakes it, and the AI is off while
                // possessed: vanilla's own Wakeup (the anim bool, the ZDO flag, the wake effect, RPC_Wakeup to everyone).
                if (monster.IsSleeping() && s_wakeup != null)
                {
                    s_wakeup.Invoke(monster, null);
                    Log($"{Utils.GetPrefabName(monster.gameObject)} was asleep: woken (vanilla Wakeup)");
                }
            }
            s_driving = true;
        }

        private static void GiveBackTheWheel()
        {
            if (!s_driving) return;
            s_driving = false;
            if (s_creature != null) s_creature.SetMoveDir(Vector3.zero);
            if (s_ai != null) s_ai.enabled = s_aiWasEnabled;
            var view = s_view;
            if (view != null && view.IsValid() && s_player != null)
            {
                var zdo = view.GetZDO();
                if (zdo.GetLong(PossessedKey, 0L) == s_player.GetPlayerID()) zdo.Set(PossessedKey, 0L);
                if (s_opt != null && s_opt.ReturnOwnership && s_originalOwner != 0L && s_originalOwner != ZDOMan.GetSessionID() && view.IsOwner()) zdo.SetOwner(s_originalOwner);
            }
        }

        // Ghost, God and fly: vanilla's own devcommands flags on the Frozen body, through the methods the 'ghost', 'god'
        // and 'fly' commands call (Player.SetGhostMode / SetGodMode / ToggleDebugFly), and exactly the states it had put back at the end.
        // Debug fly is vanilla's no-clip: no collision, no gravity, and RPC_Damage returns before damage, stagger, push or status; with the
        // body's input closed (TakeInput false) it hovers where it stands.
        private static bool s_bodyFlagsSet, s_ghostWas, s_godWas, s_flyWas;

        private static void BodyFlagsOn(Player player)
        {
            if (player == null || s_bodyFlagsSet) return;
            s_ghostWas = player.InGhostMode(); s_godWas = player.InGodMode(); s_flyWas = player.InDebugFlyMode();
            player.SetGhostMode(true);
            player.SetGodMode(true);
            if (!player.InDebugFlyMode()) player.ToggleDebugFly();
            s_bodyFlagsSet = true;
            Log($"body: ghost+god+fly on (was ghost {s_ghostWas}, god {s_godWas}, fly {s_flyWas})");
        }

        private static void BodyFlagsOff(Player player)
        {
            if (!s_bodyFlagsSet) return;
            s_bodyFlagsSet = false;
            if (player == null) { Log("WARN: body: ghost+god+fly off, but the player is gone (nothing to restore)"); return; }
            try
            {
                player.SetGhostMode(s_ghostWas);
                player.SetGodMode(s_godWas);
                if (player.InDebugFlyMode() != s_flyWas) player.ToggleDebugFly();
                Log($"body: ghost+god+fly off (restored ghost {player.InGhostMode()}, god {player.InGodMode()}, fly {player.InDebugFlyMode()})");
            }
            catch (Exception e) { Log("WARN: body: restoring ghost/god/fly failed: " + e.Message); }
        }

        private static void PutBack(Character c, Vector3 pos, Quaternion rot)
        {
            c.transform.SetPositionAndRotation(pos, rot);
            var body = s_body(c);
            if (body != null) { body.position = pos; body.linearVelocity = Vector3.zero; }
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //  Attacks
        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Fires attack 'which' (an index in Attacks; -1 = Main, -2 = the Special): a Frozen body's creature swings it; a Hidden body's player does.</summary>
        public static bool Fire(int which)
        {
            if (!IsPossessing || !(s_creature is Humanoid h)) return false;
            if (PossessionProxy.Active)
            {
                PossessionProxy.ForcedNext = PossessionAttacks.Pick(h, which);
                bool started = s_player.StartAttack(null, false);
                PossessionProxy.ForcedNext = null;
                return started;
            }
            return PossessionAttacks.Fire(h, which, which == PossessionAttacks.Main ? "main" : which == PossessionAttacks.Special ? "special" : "attack " + which, s_host);
        }

        /// <summary>Picks the next (+1) or previous (-1) attack for the special (MMB).</summary>
        public static void CycleAttack(int dir)
        {
            if (s_creature is Humanoid h) PossessionAttacks.Cycle(h, dir, s_host);
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //  Driving a Frozen body's creature: the possessor's buttons, the creature's own body
        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static bool PlayerController_FixedUpdate_Prefix(PlayerController __instance)
        {
            if (!Mine || s_creature == null || PossessionProxy.Active) return true;   // a Hidden body's player moves itself
            var player = __instance.GetComponent<Player>();
            if (player == null || player != s_player) return true;
            try { Drive(player); }
            catch (Exception ex)
            {
                if (!s_failed) { s_failed = true; Log($"WARN: let go after an error: {ex.GetType().Name}: {ex.Message}"); }
                End(PossessReasons.Error, ex.Message);
            }
            return false;   // the body itself gets no controls while possessing
        }

        // The frozen body's own keys too (hotbar, Use, build placement): vanilla's Player.Update reads them behind TakeInput.
        private static void Player_TakeInput_Postfix(Player __instance, ref bool __result)
        {
            if (__result && Mine && s_creature != null && !PossessionProxy.Active && __instance == s_player) __result = false;
        }

        private static void Drive(Player player)
        {
            player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
            var creature = s_creature;
            if (creature == null || !s_driving) return;   // AUTO: its own AI drives
            if (!s_input.InputOpen()) { creature.SetMoveDir(Vector3.zero); return; }

            Vector3 move = Vector3.zero;
            if (s_input.GetButton("Forward")) move.z += 1f;
            if (s_input.GetButton("Backward")) move.z -= 1f;
            if (s_input.GetButton("Left")) move.x -= 1f;
            if (s_input.GetButton("Right")) move.x += 1f;
            Vector2 stick = ZInput.GetJoyLeftStick();
            move.x += stick.x;
            move.z += stick.y;
            if (move.magnitude > 1f) move.Normalize();

            bool flyer = IsFlyer(creature);
            bool flight = flyer && !Cfg("FlightArcade", false);
            Vector3 flightDir = flight ? Flight(creature, Time.fixedDeltaTime) : Vector3.zero;   // turns the heading too

            // Airborne, the mouse doesn't turn the creature (the camera orbits instead, CameraFree); in Arcade
            // flight Q / E turn it (Flight controls turn it in the FlightModel).
            if (flyer && !flight && creature.IsFlying())
            {
                float yaw = (s_input.YawRight() ? 1f : 0f) - (s_input.YawLeft() ? 1f : 0f);
                s_lookYaw = Mathf.Repeat(s_lookYaw + yaw * ArcadeYawRate * Time.fixedDeltaTime, 360f);
            }

            // Movement and look relative to the creature's own heading (on the ground the mouse turns it, in Tick), like the player's body.
            Quaternion heading = Heading(creature.transform.position, s_lookYaw);   // halo ring: the yaw turns about the local up
            Vector3 lookDir = heading * Vector3.forward;
            Vector3 world = flight ? flightDir : heading * move;
            if (flyer && !flight) world = Fly(creature, world);
            else if (!flyer && IsSwimmer(creature)) world = Swim(creature, world);   // stays in deep water

            creature.SetMoveDir(world);
            creature.SetRun(flight || s_input.GetButton("Run") || s_input.GetButton("JoyRun"));
            creature.SetLookDir(lookDir);

            LatchPresses();   // also here: a press that began and is still held before the first Tick
            if (s_jumpLatched) { s_jumpLatched = false; if (!flyer) creature.Jump(); }   // a flyer's Jump is "up" (Fly)

            bool attack = s_attackLatched || s_input.GetButton("Attack") || s_input.GetButton("JoyAttack");
            bool secondary = s_secondaryLatched || s_input.GetButton("SecondaryAttack") || s_input.GetButton("JoySecondaryAttack");
            bool third = s_thirdLatched || s_input.GetButton("Block") || s_input.GetButton("JoyBlock");
            s_attackLatched = s_secondaryLatched = s_thirdLatched = false;
            if (attack || secondary || third)
            {
                s_pendingSpecial = !attack && secondary;
                s_pendingThird = !attack && !secondary;
                s_pendingUntil = Time.time + PressWindow;
            }
            if (Time.time < s_pendingUntil && creature is Humanoid humanoid && !humanoid.InAttack())
            {
                creature.SetLookDir(lookDir);
                if (s_pendingThird)
                {
                    // RMB: the attack after the Special (it moves when the Special is cycled); a creature with fewer than three has none.
                    int idx = PossessionAttacks.ThirdIndex(humanoid);
                    if (idx == PossessionAttacks.None)
                    {
                        s_pendingUntil = 0f;
                        if (!s_noThirdSaid) { s_noThirdSaid = true; Log($"{Utils.GetPrefabName(humanoid.gameObject)} has no third attack (RMB)"); }
                    }
                    else if (PossessionAttacks.Fire(humanoid, idx, "third", s_host)) s_pendingUntil = 0f;
                }
                else
                {
                    bool special = s_pendingSpecial;
                    if (PossessionAttacks.Fire(humanoid, special ? PossessionAttacks.Special : PossessionAttacks.Main, special ? "special" : "main", s_host)) s_pendingUntil = 0f;
                }
            }
        }

        // Arcade flight: Jump = up, Crouch = down; walk-and-fly creatures take off on Jump and land when Crouch brings them to the
        // ground; pure flyers stay up. Idle = altitude hold (Character.UpdateFlying lerps toward m_moveDir with no gravity).
        private static Vector3 Fly(Character creature, Vector3 world)
        {
            bool up = s_input.GetButton("Jump") || s_input.GetButton("JoyJump");
            bool down = s_input.GetButton("Crouch") || s_input.GetButton("JoyCrouch");
            var ai = creature.GetComponent<BaseAI>();
            bool canLand = ai != null && ai.m_randomFly;
            Vector3 pos = creature.transform.position;
            Vector3 ringUp = RingUp(pos);
            bool ring = ringUp != Vector3.up;
            float height;
            if (ring)
            {
                // halo ring: height above the wall floor along the local up (no flat heightmap)
                int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
                height = Physics.Raycast(pos, -ringUp, out RaycastHit floor, 1000f, mask, QueryTriggerInteraction.Ignore) ? floor.distance : 2f;
            }
            else
            {
                float ground = ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(pos, out float g) ? g : pos.y - 2f;
                height = pos.y - ground;
            }
            if (!creature.IsFlying())
            {
                if (up) { creature.TakeOff(); Log($"{Utils.GetPrefabName(creature.gameObject)} took off"); }
                return world;
            }
            float y = (up ? 1f : 0f) - (down ? 1f : 0f);
            if (y < 0f && height <= FlyLand && canLand) { creature.Land(); Log($"{Utils.GetPrefabName(creature.gameObject)} landed"); return world; }
            if (y < 0f && height <= FlyFloor) y = 0f;
            if (y > 0f && height >= FlyCeiling) y = 0f;
            if (height < FlyFloor * 0.5f) y = 0.5f;   // pushed into a slope: lift clear
            if (ring) world = Vector3.ProjectOnPlane(world, ringUp) + ringUp * y;   // halo ring: climb / dive along the local up
            else world.y = y;
            return world;
        }

        // Halo ring: FlightModel.UpAt is Core's GravityUp.At (null in Valcast's copy of this file = world up, the old maths). The
        // possession yaw is kept in the local frame: off every ring LocalYaw = eulerAngles.y and Heading = Euler(0, yaw, 0) exactly.
        private static bool s_ringLogged;
        private static Vector3 RingUp(Vector3 pos) => FlightModel.UpAt != null ? FlightModel.UpAt(pos) : Vector3.up;

        private static float LocalYaw(Transform t)
        {
            Vector3 up = RingUp(t.position);
            if (up == Vector3.up) return t.eulerAngles.y;
            return (Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, up)) * t.rotation).eulerAngles.y;
        }

        private static Quaternion Heading(Vector3 pos, float yaw)
        {
            Vector3 up = RingUp(pos);
            if (up == Vector3.up) return Quaternion.Euler(0f, yaw, 0f);
            if (!s_ringLogged) { s_ringLogged = true; Log($"halo ring: driving in the ring frame (up {up:F2}); turn, move, hover relative to the floor"); }
            return Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0f, yaw, 0f);
        }

        // Flight controls: the shared FlightModel. The host's preset (Config "FlightPreset", a FlightPreset as a
        // number; 0 = Auto: the creature's natural model) picks the model and the key map; V / C / X
        // are the abilities. FireHeli: W nose down + faster, S nose up + slower, A/D bank and slide, Q/E yaw, Shift faster (takes
        // off), Space held climbs (double tap: hover lock), Ctrl held descends (lands; its double tap is a mount's step-off, which a
        // possession ignores).
        private static FlightPreset s_flightPreset = FlightPreset.Auto;
        private static Character s_flightPresetFor;
        private static readonly Func<string, bool> s_flightButton = name => s_input != null && s_input.GetButton(name);
        private static readonly Func<bool> s_flightYawLeft = () => s_input != null && s_input.YawLeft();
        private static readonly Func<bool> s_flightYawRight = () => s_input != null && s_input.YawRight();
        private static readonly Func<bool> s_flightAbility1 = () => s_input != null && s_input.GetKey(KeyCode.V);
        private static readonly Func<bool> s_flightAbility2 = () => s_input != null && s_input.GetKey(KeyCode.C);
        private static readonly Func<bool> s_flightStop = () => s_input != null && s_input.GetKey(KeyCode.X);

        private static Vector3 Flight(Character creature, float dt)
        {
            var preset = (FlightPreset)Mathf.Clamp(Mathf.RoundToInt(Cfg("FlightPreset", 0f)), 0, (int)FlightPreset.NoMansSky);
            if (preset != s_flightPreset || !ReferenceEquals(creature, s_flightPresetFor))
            {
                s_flightPreset = preset;
                s_flightPresetFor = creature;
                s_flightTuning.ApplyPreset(preset, creature);
                Log($"flight: {FlightPresets.Name(s_flightTuning.Preset)}{(preset == FlightPreset.Auto ? " (auto)" : "")} for {Utils.GetPrefabName(creature.gameObject)}");
            }
            var cam = Camera.main;
            var reads = new FlightReads
            {
                Button = s_flightButton,
                YawLeft = s_flightYawLeft,
                YawRight = s_flightYawRight,
                Ability1 = s_flightAbility1,
                Ability2 = s_flightAbility2,
                Stop = s_flightStop,
                Mouse = s_input != null ? new Vector2(s_input.GetAxis("Mouse X"), s_input.GetAxis("Mouse Y")) : Vector2.zero,
                Look = cam != null ? cam.transform.forward : Vector3.zero,
            };
            var input = FlightInput.ReadLocal(s_flightTuning.Preset, in reads);
            s_flight.Heading = s_lookYaw;
            Vector3 dir = s_flight.Step(creature, in input, s_flightTuning, dt);
            s_lookYaw = s_flight.Heading;
            return dir;
        }

        private static void ClearLatches()
        {
            s_jumpLatched = s_attackLatched = s_secondaryLatched = s_thirdLatched = false;
            s_jumpHeldLast = s_attackHeldLast = s_secondaryHeldLast = s_thirdHeldLast = false;
            s_pendingUntil = 0f;
            s_pendingThird = false;
            s_noThirdSaid = false;
        }

        // Per rendered frame (from Tick): a new press of Jump / Attack / SecondaryAttack / Block (RMB, the third attack) is kept for
        // the next FixedUpdate.
        private static void LatchPresses()
        {
            if (s_input == null) return;
            bool jump = s_input.GetButton("Jump") || s_input.GetButton("JoyJump");
            bool attack = s_input.GetButton("Attack") || s_input.GetButton("JoyAttack");
            bool secondary = s_input.GetButton("SecondaryAttack") || s_input.GetButton("JoySecondaryAttack");
            bool third = s_input.GetButton("Block") || s_input.GetButton("JoyBlock");
            if (jump && !s_jumpHeldLast) s_jumpLatched = true;
            if (attack && !s_attackHeldLast) s_attackLatched = true;
            if (secondary && !s_secondaryHeldLast) s_secondaryLatched = true;
            if (third && !s_thirdHeldLast) s_thirdLatched = true;
            s_jumpHeldLast = jump;
            s_attackHeldLast = attack;
            s_secondaryHeldLast = secondary;
            s_thirdHeldLast = third;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //  Per frame: the reasons to end, the mouse, the cycle, ownership, the channel
        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static void GameCamera_LateUpdate_Postfix()
        {
            try
            {
                if (s_descentPlayer != null) Descend();
                if (!Mine) return;
                Tick();
            }
            catch (Exception ex)
            {
                if (!s_failed) { s_failed = true; Log($"WARN: let go after an error: {ex.GetType().Name}: {ex.Message}"); }
                End(PossessReasons.Error, ex.Message);
            }
        }

        private static void Tick()
        {
            if (s_creature == null) { if (s_view != null || s_player != null) End(PossessReasons.Gone, "the creature is gone"); return; }
            var player = s_player;
            if (player == null || player.IsDead()) { End(PossessReasons.Death, "the player died"); return; }
            if (s_creature.IsDead()) { End(PossessReasons.Death, "the creature died"); return; }
            if (s_view == null || !s_view.IsValid()) { End(PossessReasons.Gone, "the creature is gone"); return; }
            var opt = s_opt;
            if (opt.MaxDuration > 0f && Time.time - s_startTime > opt.MaxDuration) { End(PossessReasons.Duration, $"{opt.MaxDuration:0} s are up"); return; }
            if (opt.MaxRange > 0f)
            {
                float dist = Vector3.Distance(player.transform.position, s_creature.transform.position);
                if (dist > opt.MaxRange) { End(PossessReasons.Range, $"out of range ({dist:0} m > {opt.MaxRange:0} m)"); return; }
            }
            if (opt.ContinueWhile != null && !opt.ContinueWhile()) { End(PossessReasons.Released, "the caller's condition ended"); return; }

            if (s_input.InputOpen())
            {
                LatchPresses();
                // Per rendered frame, as the mouse moves (FixedUpdate would count a frame's movement twice or not at all). A Hidden
                // body's player turns itself. Airborne (CameraFree), the mouse orbits the host's camera and the keys steer.
                if (!PossessionProxy.Active && !CameraFree) s_lookYaw = Mathf.Repeat(s_lookYaw + s_input.GetAxis("Mouse X") * Cfg("MouseSensitivity", 2f), 360f);
                float wheel = s_input.GetAxis("Mouse ScrollWheel");
                var modifier = (KeyCode)(int)Cfg("CycleModifier", (float)(int)KeyCode.LeftAlt);
                if (wheel != 0f && s_input.GetKey(modifier)) CycleAttack(wheel > 0f ? -1 : 1);
            }

            if (s_driving && Time.time >= s_nextClaim)
            {
                s_nextClaim = Time.time + ClaimEverySeconds;
                if (!s_view.IsOwner()) s_view.ClaimOwnership();
            }

            if (opt.ChannelLock && !PossessionProxy.Active)
            {
                float t = Time.time - s_startTime;
                s_hoverOffset = HoverOffset(opt, t);
                Pin(player, s_bodyAnchorPos + RingUp(s_bodyAnchorPos) * s_hoverOffset);
                SafeCall(opt.ChannelPose, player, "ChannelPose");
            }
        }

        private static float HoverOffset(PossessOptions opt, float t)
        {
            if (opt.ChannelHover <= 0f) return 0f;
            float rise = opt.HoverRise > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / opt.HoverRise)) : 1f;
            float bob = opt.HoverBob > 0f && opt.HoverPeriod > 0f ? opt.HoverBob * Mathf.Sin(t * 2f * Mathf.PI / opt.HoverPeriod) * rise : 0f;
            return Mathf.Max(0f, opt.ChannelHover * rise + bob);
        }

        private static void Descend()
        {
            var p = s_descentPlayer;
            float k = s_descentTime > 0f ? Mathf.Clamp01((Time.time - s_descentStart) / s_descentTime) : 1f;
            if (p == null || p.IsDead() || k >= 1f) { s_descentPlayer = null; return; }
            Pin(p, s_descentAnchor + RingUp(s_descentAnchor) * Mathf.Lerp(s_descentFrom, 0f, Mathf.SmoothStep(0f, 1f, k)));
        }

        // The channelling body held where it stands (plus its hover): nothing moves it, no fall.
        private static void Pin(Player player, Vector3 at)
        {
            var body = s_body(player);
            player.transform.position = at;
            if (body != null) { body.position = at; body.linearVelocity = Vector3.zero; }
        }

        private static void Character_CustomFixedUpdate_Postfix(Character __instance)
        {
            if (s_descentPlayer == null && (s_opt == null || !s_opt.ChannelLock)) return;   // the common case: nothing pinned (every character, every tick)
            if (__instance == null) return;
            if (s_descentPlayer != null && __instance == s_descentPlayer) return;   // LateUpdate's Descend holds it
            if (!Mine || s_opt == null || !s_opt.ChannelLock || PossessionProxy.Active || __instance != s_player) return;
            Pin(s_player, s_bodyAnchorPos + RingUp(s_bodyAnchorPos) * s_hoverOffset);
        }

        private static void Player_OnDeath_Postfix(Player __instance)
        {
            if (Mine && __instance == s_player && IsPossessing) End(PossessReasons.Death, "the player died");
        }

        private static void ZNet_OnDestroy_Prefix()
        {
            if (Mine && IsPossessing) End(PossessReasons.Logout, "leaving the world");
            s_descentPlayer = null;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //  The body untouchable (PlayerInvulnerable) and the creature's hits as the possessor's (HitsAsPossessor)
        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        // No damage, stagger, knockback or push on an invulnerable Frozen body (a Hidden body's hits go to the creature: the proxy).
        private static bool Character_RPC_Damage_Prefix(Character __instance, HitData hit)
        {
            if (!Mine || s_opt == null || !s_opt.PlayerInvulnerable || __instance != s_player || PossessionProxy.Active) return true;
            return false;
        }

        private static void Character_Damage_Prefix(Character __instance, HitData hit)
        {
            if (!Mine || s_opt == null || !s_opt.HitsAsPossessor || hit == null || s_player == null || s_creature == null) return;
            if (__instance == s_creature || hit.m_attacker != s_creatureId) return;
            hit.m_attacker = s_player.GetZDOID();
        }

        private static void BaseAI_IsEnemy_Postfix(Character a, Character b, ref bool __result)
        {
            // A possessed / wildshaped body keeps its creature faction, and
            // vanilla Attack lands a non-player attacker's hit only on BaseAI.IsEnemy, so a Boar form could not hurt a Deer. The local
            // driver's body counts every untamed non-player as an enemy; players keep the PvP rules, tames and thralls stay safe.
            if (!__result && Mine && s_creature != null && a == s_creature && b != null && b != s_creature && b != s_player && !b.IsPlayer() && !b.IsTamed())
            { __result = true; return; }
            if (!__result || !Mine || s_opt == null || !s_opt.PlayerInvulnerable || s_player == null) return;
            if (a == s_player || b == s_player) __result = false;
        }

        // ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static float Cfg(string key, float def) => s_host != null ? s_host.Config(key, def) : def;
        private static bool Cfg(string key, bool def) => s_host != null ? s_host.Config(key, def) : def;

        private static void Log(string line) => s_host?.Log(line);

        private static void SafeCall(Action<Player> a, Player p, string what)
        {
            if (a == null) return;
            try { a(p); } catch (Exception e) { Log($"WARN: {what} threw: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
