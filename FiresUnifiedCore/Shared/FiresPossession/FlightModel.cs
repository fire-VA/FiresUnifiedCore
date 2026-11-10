using System;
using System.Collections.Generic;
using UnityEngine;

// Shared\FiresPossession: one source, compiled into Core (FIRES_POSSESSION_PUBLIC defined: public, the API other mods call) and
// into Valcast (internal: Valcast stays standalone). The names below are a frozen API - additive changes only; Core and FiresMounts
// depend on them.
//
// Flight for a vanilla flying Character. PRESETS modelled on the games people
// know, each a flight model + a key map. FlightTuning.ApplyPreset picks the model; FlightInput.ReadLocal(preset, reads) maps the keys.
// Models: Heli (FireHeli, below), Arcade (Zeall / Raaka: plane-like, bank-to-turn), Camera (ARK / WoW: fly where you look), Momentum
// (GW2 griffon: no hover, dive for speed, timed climb), Hover (GW2 skyscale: hovers, dashes, clings to walls). Collective (Rust /
// Battlefield heli) flies the Heli step and Jet (Battlefield jet / No Man's Sky) the Arcade step, each with its own tuning
// (FlightPresets.Tune).
//
// FireHeli: W nose down and faster forward, S nose up and slower (backs off once stopped), A / D bank AND
// slide left / right, Q / E flat yaw, Shift speeds up (and takes off from the ground), Space held climbs straight up and a double tap
// of Space locks a hover in place (another double tap or any movement key unlocks it; Q / E still turn), Ctrl (crouch) held descends
// straight down and touches down (a double tap of Ctrl is the host's: FiresMounts steps the rider off). No stall: no input eases into
// a hover that holds its altitude.
//
// Vanilla Character.UpdateFlying does the physics (moveDir x m_flyFastSpeed with SetRun on, no gravity, its own flight anims); a
// creature faces m_lookYaw (Character.AlwaysRotateCamera is true for them), so Step slides sideways while SetLookDir holds the
// heading. No possession needed: a ridden mount uses the same model. Every model honours the same limits: CanClimb, Tired, Ceiling.
namespace FiresPossession
{
    // Stamina states: Climbing .. Boosting appended, so the old values keep their numbers.
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    enum FlightState { Grounded, Hover, Powered, Glide, Climbing, Descending, Diving, Clinging, Boosting }

    /// <summary>The flight model a preset uses (FlightTuning.Model picks the Step branch).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    enum FlightStyle { Heli, Arcade, Collective, Jet, Camera, Momentum, Hover }

    /// <summary>The control presets. Auto = each flyer's natural model (the default).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    enum FlightPreset { Auto, FireHeli, Arcade, ArcadeRoll, Rust, BattlefieldHeli, BattlefieldJet, ARK, WoW, GW2Griffon, GW2Skyscale, NoMansSky }

    /// <summary>The host's reads for one step. Each mod fills it through its own key seam (Core: FiresKeys; Valcast: ValcastKeys).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    struct FlightReads
    {
        public Func<string, bool> Button;          // ZInput button names (Forward, Backward, Left, Right, Run, Jump, Crouch, Block, Joy*)
        public Func<bool> YawLeft, YawRight;       // the host's Q / E
        public Func<bool> Ability1, Ability2, Stop; // the host's V / C / X (null = not pressed)
        public Vector2 Mouse;                      // the RAW mouse delta this step; the model scales it (FlightTuning.MouseSensitivity)
        public Vector3 Look;                       // the camera's forward (world)
    }

#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    struct FlightInput
    {
        public float Pitch;   // +1 nose up (S) .. -1 nose down (W)
        public float Bank;    // -1 left (A) .. +1 right (D): bank and slide that way
        public float Yaw;     // -1 left (Q) .. +1 right (E)
        public bool Gas;      // Shift: faster
        public bool Brake;    // hover in place while true (a caller's own hold; ReadLocal leaves it false)
        public bool Descend;  // Ctrl (crouch) held: straight down, touch down near the ground; double tap = StepOffRequested
        public bool Climb;    // Space held: straight up; double tap = lock / unlock the hover

        // Presets: additive.
        public float Forward;    // +1 W .. -1 S, for the models where W / S are speed, not the nose
        public bool Boost;       // Space in Arcade
        public bool Ability1;    // V: the griffon's dive, the skyscale's dash
        public bool Ability2;    // C: the griffon's climb, the skyscale's descend
        public bool Stop;        // X: ARK's hang still
        public bool StepOffKey;  // Ctrl itself (the step-off double tap), whatever Ctrl does in the preset
        public Vector2 Mouse;    // raw mouse delta (the phase-2 attitude presets)
        public Vector3 Look;     // the camera's forward (the steered presets)

        // The keys are the caller's: 'button' takes ZInput button names (Forward, Backward, Left, Right, Run, Jump, Crouch and the
        // Joy* ones), so each mod reads them through its own seam (Core: FiresKeys with its chat/console guard). When input is
        // closed, pass default(FlightInput): it hovers. FireHeli's keys.
        public static FlightInput ReadLocal(Func<string, bool> button, Func<bool> yawLeft, Func<bool> yawRight)
        {
            var i = new FlightInput();
            if (button == null) return i;
            i.Pitch = (button("Backward") ? 1f : 0f) - (button("Forward") ? 1f : 0f);
            i.Bank = (button("Right") ? 1f : 0f) - (button("Left") ? 1f : 0f);
            i.Yaw = ((yawRight != null && yawRight()) ? 1f : 0f) - ((yawLeft != null && yawLeft()) ? 1f : 0f);
            i.Gas = button("Run") || button("JoyRun");
            i.Climb = button("Jump") || button("JoyJump");
            i.Descend = button("Crouch") || button("JoyCrouch");
            i.StepOffKey = i.Descend;
            return i;
        }

        /// <summary>The preset's key map. Pass the RESOLVED preset (FlightTuning.Preset).</summary>
        public static FlightInput ReadLocal(FlightPreset preset, in FlightReads reads)
        {
            var b = reads.Button;
            if (b == null) return new FlightInput();
            bool fwd = b("Forward"), back = b("Backward"), left = b("Left"), right = b("Right");
            bool run = b("Run") || b("JoyRun"), jump = b("Jump") || b("JoyJump"), crouch = b("Crouch") || b("JoyCrouch");
            bool q = reads.YawLeft != null && reads.YawLeft(), e = reads.YawRight != null && reads.YawRight();
            bool a1 = reads.Ability1 != null && reads.Ability1(), a2 = reads.Ability2 != null && reads.Ability2();
            bool stop = reads.Stop != null && reads.Stop();
            float ws = (fwd ? 1f : 0f) - (back ? 1f : 0f), ad = (right ? 1f : 0f) - (left ? 1f : 0f), qe = (e ? 1f : 0f) - (q ? 1f : 0f);
            var i = new FlightInput
            {
                Mouse = reads.Mouse,
                Look = reads.Look,
                StepOffKey = crouch,
            };
            switch (FlightPresets.Effective(preset))
            {
                case FlightPreset.NoMansSky:   // Starship, the Zeall map (W thrust, S brake, A / D bank-turn, Space boost)
                case FlightPreset.Arcade:   // Zeall: W faster, S slower / land, A / D bank-turn, Shift / Ctrl + move = nose up / down, Space boost
                    bool moving = fwd || back || left || right;
                    i.Forward = ws; i.Bank = ad;
                    i.Pitch = (run && moving ? 1f : 0f) - (crouch && moving ? 1f : 0f);
                    i.Boost = jump;
                    break;
                case FlightPreset.BattlefieldJet:   // Jet, the Raaka map (W / S nose, Q / E roll, A / D rudder, Shift throttle, Space afterburner)
                case FlightPreset.ArcadeRoll:   // Raaka: W / S nose down / up, Q / E roll (bank-turn), A / D yaw, Shift faster, Space boost, Ctrl slow + land
                    i.Pitch = -ws; i.Bank = qe; i.Yaw = ad;
                    i.Forward = run ? 1f : crouch ? -1f : 0f;
                    i.Gas = run; i.Boost = jump; i.Descend = crouch;
                    break;
                case FlightPreset.ARK:   // W / S along the look, A / D strafe, Space up, Ctrl down, Shift faster, X hang still
                case FlightPreset.WoW:   // the same, X down
                    i.Forward = ws; i.Bank = ad; i.Gas = run; i.Climb = jump;
                    if (preset == FlightPreset.WoW) i.Descend = stop || crouch; else { i.Descend = crouch; i.Stop = stop; }
                    break;
                case FlightPreset.GW2Griffon:   // mouse / A / D steer, W glide, S air brake, Space flap, V dive, C climb, Ctrl lands near the ground
                    i.Forward = ws; i.Bank = ad; i.Climb = jump; i.Ability1 = a1; i.Ability2 = a2; i.Descend = crouch;
                    break;
                case FlightPreset.GW2Skyscale:   // W / S along the look, A / D strafe, Space rise, C / Ctrl sink, V dash, Shift faster
                    i.Forward = ws; i.Bank = ad; i.Climb = jump; i.Descend = a2 || crouch; i.Ability1 = a1; i.Gas = run;
                    break;
                default:   // FireHeli, Rust, BattlefieldHeli (their own tuning, FireHeli's keys)
                    i.Pitch = -ws; i.Bank = ad; i.Yaw = qe; i.Gas = run; i.Climb = jump; i.Descend = crouch;
                    break;
            }
            return i;
        }
    }

#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    sealed class FlightTuning
    {
        // Visual tilt (degrees) and how fast it follows / levels: the nose with W / S, the roll with A / D.
        public float PitchMax = 25f, PitchRate = 90f, PitchReverse = 1.5f, PitchLevel = 60f;
        public float BankMax = 25f, BankRate = 90f, BankLevel = 60f, YawRate = 90f;
        // Turning into a bank (degrees a second at full bank); 0 = helicopter (A / D only slide, Q / E turn).
        public float BankTurn = 0f;
        // Speeds as fractions of the creature's m_flyFastSpeed, and how fast they change (per second).
        public float Gas = 0.8f, Coast = 0.4f, Cruise = 0.55f, Brake = 1.5f, HoverBelow = 0.15f;
        public float Reverse = 0.25f, StrafeMax = 0.5f, StrafeRate = 1.5f, ClimbRate = 0.45f, DescendRate = 0.5f, VerticalRate = 2f;
        public float VelFollow = 8f, VisualSmooth = 8f;
        public float Floor = 0.5f, Ceiling = 60f, LandWithin = 1.2f;
        // Shift held below this height (m) keeps climbing at ClimbRate, so a take-off clears the ground.
        public float TakeOffClear = 2.5f;
        public Func<Character, bool> CanLand;            // null = the creature's BaseAI.m_randomFly (walk-and-fly creatures)
        public Func<FlightState, float> StaminaDrain;    // per second by state; null = no stamina
        public Func<bool> CanClimb;                      // null = always
        public Action<string> Log;                       // the host's Debug logger; null = silent

        // ── Presets ───────────────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>The resolved preset and its model (set by ApplyPreset; FireHeli / Heli until then).</summary>
        public FlightPreset Preset = FlightPreset.FireHeli;
        public FlightStyle Model = FlightStyle.Heli;
        /// <summary>"Tired" (null = never): no climb, no Gas / Boost, sinks at TiredSink (m/s), still steerable, lands normally.</summary>
        public Func<bool> Tired;
        public float TiredSink = 2f;
        /// <summary>The host pays for "dash" / "wall_launch" (false refuses); null = free, on the model's own recharge.</summary>
        public Func<string, bool> Spend;
        /// <summary>The skyscale's wall cling; null = any steep surface the probe finds.</summary>
        public Func<Character, bool> CanCling;
        public float MouseSensitivity = 1f;
        public bool InvertMouseY;
        // Steered models (Camera, Momentum, Hover): degrees a second the heading follows the camera, and the look's pitch limit.
        public float SteerRate = 240f, LookPitchMax = 60f;
        // Arcade: the cruise floor in the air (it never stops or flips: it turns by banking), bank / nose limits, turn at full bank.
        public float ArcadeMin = 0.35f, ArcadeBank = 40f, ArcadePitch = 40f, ArcadeTurn = 75f, BoostMul = 1.6f;
        // Momentum (GW2 griffon), speeds as fractions of m_flyFastSpeed: glide speed, the dive's top, sink, bleed, air brake; the dive
        // (accel, nose angle), the climb (window after a dive, lift, the speed it costs), the flap (rise, time, recharge), the launch hop.
        public float GlideSpeed = 0.8f, MomentumMax = 2.6f, GlideSink = 0.12f, SpeedBleed = 0.05f, AirBrake = 0.6f;
        public float DiveAccel = 0.8f, DivePitch = 40f, ClimbWindow = 2.5f, ClimbLift = 0.9f, ClimbCost = 0.8f;
        public float FlapRise = 0.6f, FlapTime = 0.45f, FlapRecharge = 1.6f, LaunchHop = 0.9f, LaunchTime = 0.7f, MomentumTurn = 100f;
        // Hover (GW2 skyscale): the look's pitch limit, the dash, the wall cling probe (beyond the radius), the charge and launch.
        public float HoverPitchMax = 30f, DashMul = 2.2f, DashTime = 0.5f, DashRecharge = 2f;
        public float ClingReach = 0.8f, ChargeTime = 1f, WallLaunch = 1.4f, WallLaunchTime = 0.7f;
        // Aerobatics: above AerobaticHeight (m above the ground;
        // 0 = never) a bank or nose held past AeroHold seconds keeps turning (rolls / loops at these degrees a second); lower, or let
        // go, the attitude comes back level. Heli, Arcade and Momentum (rolls only; its W / S aren't the nose).
        public float AerobaticHeight = 15f, AeroRollRate = 270f, AeroPitchRate = 180f, AeroHold = 0.3f;
        // Jet: below this throttle in the air it sinks (0 = never stalls), harder the slower it goes.
        public float StallSpeed = 0f;
        private FlightTuning _base;   // the host's own values, restored before each preset's tuning

        /// <summary>Picks the preset's model for this flyer (Auto resolves by the flyer). Touches only Preset and Model.</summary>
        public FlightTuning ApplyPreset(FlightPreset preset, Character body)
        {
            var resolved = FlightPresets.Resolve(preset, body);
            var effective = FlightPresets.Effective(resolved);
            if (effective != resolved) Log?.Invoke($"[Flight] {FlightPresets.Name(resolved)} isn't built yet (phase 2): flying {FlightPresets.Name(effective)}");
            Preset = effective;
            Model = FlightPresets.Model(effective);
            // The host's tuning first, then the preset's own (Rust, Battlefield heli / jet, No Man's Sky); the host's
            // MouseSensitivity, InvertMouseY and callbacks are never touched.
            if (_base == null) _base = (FlightTuning)MemberwiseClone();
            foreach (var f in typeof(FlightTuning).GetFields())
                if (f.FieldType == typeof(float) && f.Name != nameof(MouseSensitivity)) f.SetValue(this, f.GetValue(_base));
            FlightPresets.Tune(effective, this);
            return this;
        }
    }

    /// <summary>The presets' names, models, the Auto rule and the shared hint lines (FiresMounts' panel, Core's help, Valcast).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    static class FlightPresets
    {
        /// <summary>Auto -> the flyer's natural model by its prefab (Moder / Dragon: skyscale; Drake / Hatchling / Fader: griffon; else FireHeli). Others pass through.</summary>
        public static FlightPreset Resolve(FlightPreset preset, Character body)
        {
            if (preset != FlightPreset.Auto) return preset;
            string name = body != null ? Utils.GetPrefabName(body.gameObject) : "";
            if (Has(name, "Moder") || Has(name, "Dragon")) return FlightPreset.GW2Skyscale;
            if (Has(name, "Drake") || Has(name, "Hatchling") || Has(name, "Fader")) return FlightPreset.GW2Griffon;
            return FlightPreset.FireHeli;
        }

        /// <summary>The preset that actually flies: a phase-2 preset (its model not built yet) flies FireHeli.</summary>
        public static FlightPreset Effective(FlightPreset preset)
        {
            return preset == FlightPreset.Auto ? FlightPreset.FireHeli : preset;   // every preset flies its own way
        }

        /// <summary>The preset's own numbers over the host's (only Rust, Battlefield heli / jet and No Man's Sky have any).</summary>
        public static void Tune(FlightPreset preset, FlightTuning t)
        {
            switch (preset)
            {
                case FlightPreset.Rust:   // heavy minicopter: slow to tilt, slow to speed up and stop, drifts, a lazy bank-turn, no stunts
                    t.PitchMax = 20f; t.PitchRate = 45f; t.PitchLevel = 35f; t.BankMax = 22f; t.BankRate = 45f; t.BankLevel = 35f;
                    t.YawRate = 55f; t.BankTurn = 25f; t.Gas = 0.4f; t.Coast = 0.2f; t.Cruise = 0.5f; t.Brake = 0.7f;
                    t.StrafeMax = 0.35f; t.StrafeRate = 0.7f; t.ClimbRate = 0.35f; t.DescendRate = 0.45f; t.VerticalRate = 1f;
                    t.VelFollow = 2.5f; t.VisualSmooth = 4f; t.AerobaticHeight = 0f;
                    break;
                case FlightPreset.BattlefieldHeli:   // agile attack heli: steep fast tilt, quick yaw, strong bank-turn, snappy climb
                    t.PitchMax = 35f; t.PitchRate = 160f; t.PitchLevel = 100f; t.BankMax = 40f; t.BankRate = 170f; t.BankLevel = 100f;
                    t.YawRate = 140f; t.BankTurn = 50f; t.Gas = 1.5f; t.Coast = 0.6f; t.Cruise = 0.7f; t.Brake = 2.4f;
                    t.StrafeMax = 0.7f; t.StrafeRate = 2.6f; t.ClimbRate = 0.65f; t.DescendRate = 0.75f; t.VerticalRate = 3.5f;
                    t.VelFollow = 11f; t.VisualSmooth = 12f;
                    break;
                case FlightPreset.BattlefieldJet:   // fast fixed-wing: never slow in the air, stalls and sinks without speed, big afterburner
                    t.ArcadeMin = 0.55f; t.StallSpeed = 0.75f; t.Cruise = 0.9f; t.Gas = 0.6f; t.Coast = 0.15f; t.Brake = 0.5f; t.BoostMul = 2.2f;
                    t.ArcadeBank = 75f; t.ArcadePitch = 60f; t.ArcadeTurn = 95f; t.BankRate = 220f; t.BankLevel = 120f;
                    t.PitchRate = 120f; t.PitchLevel = 80f; t.YawRate = 40f; t.ClimbRate = 0.5f; t.DescendRate = 0.7f;
                    t.VelFollow = 4f; t.VisualSmooth = 10f;
                    break;
                case FlightPreset.NoMansSky:   // floaty starship: soft attitude, drifting momentum, slow cruise floor, very strong boost
                    t.ArcadeMin = 0.3f; t.Cruise = 0.8f; t.Gas = 1f; t.Coast = 0.3f; t.Brake = 1.2f; t.BoostMul = 3f;
                    t.ArcadeBank = 45f; t.ArcadePitch = 45f; t.ArcadeTurn = 70f; t.BankRate = 70f; t.BankLevel = 40f;
                    t.PitchRate = 70f; t.PitchLevel = 40f; t.ClimbRate = 0.5f; t.VelFollow = 2f; t.VisualSmooth = 4f;
                    break;
            }
        }

        public static FlightStyle Model(FlightPreset preset)
        {
            switch (preset)
            {
                case FlightPreset.Arcade: case FlightPreset.ArcadeRoll: return FlightStyle.Arcade;
                case FlightPreset.Rust: case FlightPreset.BattlefieldHeli: return FlightStyle.Collective;
                case FlightPreset.BattlefieldJet: case FlightPreset.NoMansSky: return FlightStyle.Jet;
                case FlightPreset.ARK: case FlightPreset.WoW: return FlightStyle.Camera;
                case FlightPreset.GW2Griffon: return FlightStyle.Momentum;
                case FlightPreset.GW2Skyscale: return FlightStyle.Hover;
                default: return FlightStyle.Heli;
            }
        }

        public static string Name(FlightPreset preset)
        {
            switch (preset)
            {
                case FlightPreset.Auto: return "Auto";
                case FlightPreset.FireHeli: return "Fire heli";
                case FlightPreset.Arcade: return "Arcade (Zeall)";
                case FlightPreset.ArcadeRoll: return "Arcade roll (Raaka)";
                case FlightPreset.Rust: return "Rust minicopter";
                case FlightPreset.BattlefieldHeli: return "Battlefield heli";
                case FlightPreset.BattlefieldJet: return "Battlefield jet";
                case FlightPreset.ARK: return "ARK";
                case FlightPreset.WoW: return "WoW";
                case FlightPreset.GW2Griffon: return "GW2 griffon";
                case FlightPreset.GW2Skyscale: return "GW2 skyscale";
                case FlightPreset.NoMansSky: return "No Man's Sky";
                default: return preset.ToString();
            }
        }

        private static readonly string[] s_fireHeli =
        {
            "W / S: nose down + faster / nose up + slower", "A / D: bank and slide", "Q / E: turn", "Shift: speed (takes off)",
            "Hold Space: climb (double tap: hover lock)", "Hold Ctrl: descend + land (double tap: step off)",
        };

        /// <summary>The preset's key hint lines (resolve Auto first for a known flyer).</summary>
        public static IReadOnlyList<string> Hints(FlightPreset preset)
        {
            switch (preset)
            {
                case FlightPreset.Auto:
                    return new[] { "Auto: each flyer flies its own way ([Flight] Preset picks one for all)" };
                case FlightPreset.Arcade:
                    return new[] { "W: faster   S: slower (lands near the ground)", "A / D: bank and turn", "Shift + move: nose up   Ctrl + move: nose down", "Space: boost" };
                case FlightPreset.ArcadeRoll:
                    return new[] { "W / S: nose down / nose up", "Q / E: roll (bank and turn)", "A / D: yaw", "Shift: faster   Space: boost", "Ctrl: slow down (lands near the ground)" };
                case FlightPreset.ARK:
                    return new[] { "W / S: fly where you look / back", "A / D: strafe", "Space: up   Ctrl: down (lands)", "Shift: faster", "X: hang still (X again or move: go)" };
                case FlightPreset.WoW:
                    return new[] { "W / S: fly where you look / back", "A / D: strafe", "Space: up   X or Ctrl: down (lands)", "Shift: faster" };
                case FlightPreset.GW2Griffon:
                    return new[] { "Mouse: steer   A / D: turn", "No hover: launch from a height", "Space: flap (recharges)", "V: dive (height into speed)", "C: climb, right after a dive (speed into height)", "S: air brake   Ctrl near the ground: land" };
                case FlightPreset.GW2Skyscale:
                    return new[] { "W / S: fly where you look (up to 30 deg) / back", "A / D: strafe", "Hold Space: rise   C or Ctrl: sink (lands)", "V: dash", "Hold W into a wall: cling; hold Space, release: launch" };
                case FlightPreset.BattlefieldJet:
                    return new[] { "W / S: nose down / nose up", "Q / E: roll (bank and turn)", "A / D: rudder", "Shift: throttle   Space: afterburner", "Ctrl: slow down (lands near the ground)", "Too slow and it sinks: keep speed up" };
                case FlightPreset.NoMansSky:
                    return new[] { "W: thrust   S: brake (lands near the ground)", "A / D: bank and turn", "Shift + move: nose up   Ctrl + move: nose down", "Space: boost" };
                case FlightPreset.Rust:
                case FlightPreset.BattlefieldHeli:
                {
                    var lines = new List<string> { Name(preset) + (preset == FlightPreset.Rust ? ": heavy, slow to tilt and stop" : ": agile, fast tilt and turn") };
                    lines.AddRange(s_fireHeli);
                    return lines;
                }
                default:
                    return s_fireHeli;
            }
        }

        private static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    sealed class FlightModel
    {
        // Pitch / Bank: the visual tilt in degrees (+ nose up, + bank right). Throttle: forward speed (-Reverse .. 1).
        // Lateral: sideways speed (-StrafeMax left .. +StrafeMax right). Vertical: climb (+) / descent (-) speed.
        // HoverLocked: the double-tapped Space hover; any movement key (not Q / E) or another double tap clears it.
        public float Pitch, Bank, Throttle, Heading, Lateral, Vertical;
        public bool HoverLocked;
        public FlightState State { get; private set; } = FlightState.Grounded;
        public event Action<FlightState> StateChanged;
        /// <summary>Ctrl double-tapped in the air: the host steps its rider off (FiresMounts); a possession ignores it.</summary>
        public event Action StepOffRequested;

        /// <summary>Two presses within this many seconds are a double tap; a hold longer than it counts as a hold.</summary>
        public const float DoubleTapSeconds = 0.35f;

        private Transform _visual;
        private Quaternion _visualRest;
        // Tap timing on the model's own clock (sum of Step dt), so every caller's held keys give the same taps.
        private float _clock, _climbHeld, _lastClimbPress = float.NegativeInfinity, _lastDescendPress = float.NegativeInfinity;
        private bool _climbWasDown, _descendWasDown;
        // The other models' state (on the same clock).
        private float _speed, _sinceDive = 99f, _flapUntil, _flapReady, _launchUntil, _dashUntil, _dashReady, _charge, _steerPitch, _launchVertical;
        private float _rollHeld, _pitchHeld;
        private bool _diving, _climbing, _clinging, _stopWasDown, _ability1WasDown, _chargeWasDown;
        private Vector3 _wallNormal;

        public void Reset(float heading)
        {
            ResetVisual();
            Pitch = Bank = Throttle = Lateral = Vertical = 0f;
            HoverLocked = false;
            Heading = heading;
            _speed = _charge = _steerPitch = _launchVertical = _rollHeld = _pitchHeld = 0f;
            _sinceDive = 99f;
            _flapUntil = _flapReady = _launchUntil = _dashUntil = _dashReady = 0f;
            _diving = _climbing = _clinging = _stopWasDown = _ability1WasDown = _chargeWasDown = false;
            SetState(FlightState.Grounded);
        }

        // One physics tick (FixedUpdate). Returns the flight direction it set (length = speed as a fraction of m_flyFastSpeed).
        public Vector3 Step(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            if (body == null || t == null) return Vector3.zero;
            SetFrame(body.transform.position, t);
            if (!s_ring) return StepModel(body, in input, t, dt);
            FlightInput local = input;   // halo ring: the camera's forward in the ring frame
            local.Look = ToLocal(input.Look);
            return StepModel(body, in local, t, dt);
        }

        // Halo ring: the whole model runs in the local frame (y = the local up) and converts at the edges (velocity, SetMoveDir,
        // SetLookDir, the returned direction, the wall ray). UpAt is set by Core (GravityUp.At); Valcast's copy of this file leaves it
        // null, so there and off every ring s_ring is false and every path is the old world-y one.
        public static Func<Vector3, Vector3> UpAt;
        private static bool s_ring, s_ringLogged;
        private static Quaternion s_frame = Quaternion.identity, s_inv = Quaternion.identity;
        private static Vector3 s_up = Vector3.up;
        private static void SetFrame(Vector3 pos, FlightTuning t)
        {
            Vector3 up = UpAt != null ? UpAt(pos) : Vector3.up;
            s_ring = up != Vector3.up;
            if (!s_ring) return;
            s_up = up; s_frame = Quaternion.FromToRotation(Vector3.up, up); s_inv = Quaternion.Inverse(s_frame);
            if (!s_ringLogged) { s_ringLogged = true; t.Log?.Invoke($"[Flight] halo ring: flight runs in the ring frame (up {up:F2}); heading, climb and height relative to the floor"); }
        }
        private static Vector3 ToLocal(Vector3 v) => s_ring ? s_inv * v : v;
        private static Vector3 ToWorld(Vector3 v) => s_ring ? s_frame * v : v;

        private Vector3 StepModel(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            switch (t.Model)
            {
                case FlightStyle.Arcade:
                case FlightStyle.Jet: _clock += dt; return StepArcade(body, in input, t, dt);   // Jet = Arcade + its tuning
                case FlightStyle.Camera: _clock += dt; return StepCamera(body, in input, t, dt);
                case FlightStyle.Momentum: _clock += dt; return StepMomentum(body, in input, t, dt);
                case FlightStyle.Hover: _clock += dt; return StepHover(body, in input, t, dt);
                default: return StepHeli(body, in input, t, dt);
            }
        }

        // ── FireHeli (drop 4 v2; drop 6 adds Tired, the Climbing / Descending states and the Ctrl step-off key) ──────────────────────
        private Vector3 StepHeli(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            Vector3 pos = body.transform.position;
            float height = Height(body);   // halo ring: Height() measures along the local up
            bool canLand = CanLand(body, t);
            bool tired = IsTired(t);
            bool gas = input.Gas && !tired;

            if (!body.IsFlying())
            {
                SetState(FlightState.Grounded);
                ApplyVisual(body, dt, level: true, t);
                if (!gas) return Vector3.zero;
                body.TakeOff();
                Throttle = 0f; Lateral = 0f; Vertical = t.ClimbRate; Pitch = 0f; Bank = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} took off (gas)");
            }

            float pitchIn = Mathf.Clamp(input.Pitch, -1f, 1f);
            float bankIn = Mathf.Clamp(input.Bank, -1f, 1f);
            _clock += dt;
            bool spaceDoubleTap = Pressed(input.Climb, ref _climbWasDown, ref _lastClimbPress);
            if (Pressed(input.Descend || input.StepOffKey, ref _descendWasDown, ref _lastDescendPress)) StepOff(body, t);
            // A tap of Space is half of a double tap, so it never unlocks; holding it past the double-tap window climbs out of the lock.
            _climbHeld = input.Climb ? _climbHeld + dt : 0f;
            if (spaceDoubleTap)
            {
                HoverLocked = !HoverLocked;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} hover {(HoverLocked ? "locked" : "unlocked")} (double Space)");
            }
            else if (HoverLocked && (pitchIn != 0f || bankIn != 0f || gas || input.Descend || _climbHeld > DoubleTapSeconds))
            {
                HoverLocked = false;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} hover unlocked (movement)");
            }
            bool hover = input.Brake || HoverLocked;
            bool climb = input.Climb && !hover && CanClimbNow(t, tired);
            bool descend = input.Descend && !hover && !climb;
            if (hover) { pitchIn = 0f; bankIn = 0f; }
            // Space / Ctrl with no movement key go straight up / down: the drift stops quickly.
            bool straight = (climb || descend) && pitchIn == 0f && bankIn == 0f && !gas;

            // Q / E turn flat; a bank adds a turn only when BankTurn is set (0 = helicopter).
            Heading = Mathf.Repeat(Heading + (Mathf.Clamp(input.Yaw, -1f, 1f) * t.YawRate + t.BankTurn * (Bank / Mathf.Max(1f, t.BankMax))) * dt, 360f);

            // Forward: W (nose down) speeds up to cruise, Shift to full; S (nose up) slows, then backs off; nothing eases into a hover.
            float cap = gas ? 1f : t.Cruise;
            float forward = hover ? 0f
                : pitchIn < 0f ? cap * -pitchIn
                : gas ? 1f
                : pitchIn > 0f ? -t.Reverse * pitchIn
                : 0f;
            float forwardRate = hover || straight ? t.Brake : forward > Throttle ? t.Gas : pitchIn > 0f ? t.Brake : t.Coast;
            Throttle = Mathf.MoveTowards(Throttle, forward, forwardRate * dt);

            // Sideways: A / D slide that way; a hover or a straight climb / descent stops the slide.
            Lateral = Mathf.MoveTowards(Lateral, bankIn * t.StrafeMax, (bankIn != 0f ? t.StrafeRate : hover || straight ? t.StrafeRate * 2f : t.StrafeRate * 1.5f) * dt);

            // Up / down: Space held climbs, Ctrl held descends, otherwise the altitude holds (S only slows and backs off).
            float vertical = descend ? -t.DescendRate : climb ? t.ClimbRate : 0f;
            // A take-off (Shift) keeps rising until it clears TakeOffClear; otherwise the climb set at take-off eased to 0 in ~0.2 s.
            if (gas && !hover && !descend && height < t.TakeOffClear && CanClimbNow(t, tired)) vertical = Mathf.Max(vertical, t.ClimbRate);
            if (tired) vertical = Mathf.Min(vertical, -TiredY(body, t));
            Vertical = Mathf.MoveTowards(Vertical, vertical, t.VerticalRate * dt);

            if ((descend || tired) && height <= t.LandWithin && canLand)
                return LandNow(body, t, descend ? "descend" : "tired");

            // The visual tilt: the nose follows W / S, the roll follows A / D (left side down for A). With room (AerobaticHeight), held
            // past AeroHold, they keep going: a loop on W / S, a barrel roll on A / D.
            bool room = Room(t, height) && !hover;
            if (!Stunt(ref Pitch, pitchIn, t.PitchMax, t.AeroPitchRate, ref _pitchHeld, room, t.AeroHold, dt))
            {
                float pitchWant = pitchIn * t.PitchMax;
                float pitchRate = t.PitchRate * (pitchIn != 0f && Mathf.Sign(pitchIn) != Mathf.Sign(Pitch) && Mathf.Abs(Pitch) > 1f ? t.PitchReverse : 1f);
                Pitch = Mathf.MoveTowards(Pitch, pitchWant, (pitchIn != 0f ? pitchRate : t.PitchLevel) * dt);
            }
            if (!Stunt(ref Bank, bankIn, t.BankMax, t.AeroRollRate, ref _rollHeld, room, t.AeroHold, dt))
                Bank = Mathf.MoveTowards(Bank, bankIn * t.BankMax, (bankIn != 0f ? t.BankRate : t.BankLevel) * dt);

            Quaternion yaw = Quaternion.Euler(0f, Heading, 0f);
            // A full diagonal (slide + forward) is capped at full speed, not 1.12x it.
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(Lateral, 0f, Throttle), 1f);
            Vector3 dir = yaw * flat;
            dir.y = Vertical;
            // A loop: past 60 degrees the flight follows the nose (at cruise at least), so it really loops.
            if (Mathf.Abs(Pitch) > 60f)
            {
                Vector3 nose = yaw * (Quaternion.AngleAxis(-Pitch, Vector3.right) * Vector3.forward);
                dir = nose * Mathf.Max(Mathf.Abs(Throttle), t.Cruise) + yaw * new Vector3(Lateral, 0f, 0f);
                Vertical = dir.y;
            }
            var state = climb && Vertical > 0.01f ? FlightState.Climbing
                : descend ? FlightState.Descending
                : dir.magnitude < t.HoverBelow ? FlightState.Hover
                : gas ? FlightState.Powered : FlightState.Glide;
            return Finish(body, dir, t, dt, height, (descend || tired) && canLand, hover, tired, state);
        }

        // ── Arcade (Zeall / Raaka): plane-like; it never stops or flips in the air, it turns by banking ───────────────────────────────
        private Vector3 StepArcade(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            float height = Height(body);
            bool canLand = CanLand(body, t), tired = IsTired(t);
            if (!body.IsFlying())
            {
                SetState(FlightState.Grounded);
                ApplyVisual(body, dt, level: true, t);
                if (tired || !(input.Forward > 0f || input.Gas || input.Boost)) return Vector3.zero;
                body.TakeOff();
                Throttle = t.ArcadeMin; Lateral = 0f; Vertical = t.ClimbRate; Pitch = 15f; Bank = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} took off ({FlightPresets.Name(t.Preset)})");
            }
            TrackStepOff(in input, body, t);
            bool boost = input.Boost && !tired, gas = input.Gas && !tired;
            bool slowing = input.Forward < 0f || input.Descend;
            float top = boost ? t.BoostMul : gas ? 1f : t.Cruise;
            bool low = height <= t.LandWithin * 2f;
            float floorSpeed = low ? 0f : t.ArcadeMin;
            if (input.Forward > 0f || boost) Throttle = Mathf.MoveTowards(Throttle, top, t.Gas * (boost ? 2f : 1f) * dt);
            else if (slowing) Throttle = Mathf.MoveTowards(Throttle, floorSpeed, t.Brake * dt);
            else if (Throttle > top) Throttle = Mathf.MoveTowards(Throttle, top, t.Coast * dt);
            if (Throttle < floorSpeed) Throttle = Mathf.MoveTowards(Throttle, floorSpeed, t.Gas * dt);

            float pitchIn = Mathf.Clamp(input.Pitch, -1f, 1f);
            if (pitchIn > 0f && !CanClimbNow(t, tired)) pitchIn = 0f;
            float bankIn = Mathf.Clamp(input.Bank, -1f, 1f);
            // With room, held past AeroHold: loops and barrel rolls (the flight already follows the nose here).
            bool room = Room(t, height);
            if (!Stunt(ref Pitch, pitchIn, t.ArcadePitch, t.AeroPitchRate, ref _pitchHeld, room, t.AeroHold, dt))
                Pitch = Mathf.MoveTowards(Pitch, pitchIn * t.ArcadePitch, (pitchIn != 0f ? t.PitchRate : t.PitchLevel) * dt);
            if (!Stunt(ref Bank, bankIn, t.ArcadeBank, t.AeroRollRate, ref _rollHeld, room, t.AeroHold, dt))
                Bank = Mathf.MoveTowards(Bank, bankIn * t.ArcadeBank, (bankIn != 0f ? t.BankRate : t.BankLevel) * dt);
            // The turn follows the bank's lift (sin), so a barrel roll wiggles the heading rather than spinning it.
            float turn = Mathf.Clamp(Mathf.Sin(Bank * Mathf.Deg2Rad) / Mathf.Max(0.05f, Mathf.Sin(t.ArcadeBank * Mathf.Deg2Rad)), -1f, 1f);
            Heading = Mathf.Repeat(Heading + (t.ArcadeTurn * turn + Mathf.Clamp(input.Yaw, -1f, 1f) * t.YawRate * 0.5f) * dt, 360f);

            if ((slowing || tired) && canLand && height <= t.LandWithin && Throttle < 0.25f) return LandNow(body, t, tired ? "tired" : "slowed");
            Vector3 dir = Quaternion.Euler(-Pitch, Heading, 0f) * Vector3.forward * Throttle;
            if (low && Throttle < 0.25f && slowing) dir.y = Mathf.Min(dir.y, -t.DescendRate * 0.5f);   // settles to land
            // Jet: too slow to stay up, it sinks (more the slower), so it needs forward speed.
            if (t.StallSpeed > 0f && !low && Throttle < t.StallSpeed) dir.y = Mathf.Min(dir.y, -t.DescendRate * (1f - Throttle / t.StallSpeed) * 2f);
            if (tired) dir.y = Mathf.Min(dir.y, -TiredY(body, t));
            Vertical = dir.y;
            Lateral = 0f;
            var state = boost ? FlightState.Boosting : dir.y > 0.05f ? FlightState.Climbing : dir.y < -0.05f ? FlightState.Descending
                : gas ? FlightState.Powered : FlightState.Glide;
            return Finish(body, dir, t, dt, height, (slowing || tired) && canLand, false, tired, state);
        }

        // ── Camera (ARK / WoW): powered, fly where you look ──────────────────────────────────────────────────────────────────────────
        private Vector3 StepCamera(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            float height = Height(body);
            bool canLand = CanLand(body, t), tired = IsTired(t);
            if (!body.IsFlying())
            {
                SetState(FlightState.Grounded);
                ApplyVisual(body, dt, level: true, t);
                if (tired || !(input.Climb || input.Gas)) return Vector3.zero;
                body.TakeOff();
                Throttle = 0f; Lateral = 0f; Vertical = t.ClimbRate; Pitch = 0f; Bank = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} took off ({FlightPresets.Name(t.Preset)})");
            }
            TrackStepOff(in input, body, t);
            // ARK's X: hang still; X again or any move key lets go.
            bool stopPress = input.Stop && !_stopWasDown;
            _stopWasDown = input.Stop;
            bool moving = input.Forward != 0f || input.Bank != 0f || input.Climb || input.Descend;
            if (stopPress)
            {
                HoverLocked = !HoverLocked;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} {(HoverLocked ? "hangs still" : "goes on")} (X)");
            }
            else if (HoverLocked && moving)
            {
                HoverLocked = false;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} goes on (movement)");
            }
            bool hang = HoverLocked;
            if (!hang)
            {
                Heading = Mathf.MoveTowardsAngle(Heading, LookYaw(in input, Heading), t.SteerRate * dt);
                _steerPitch = Mathf.Clamp(LookPitch(in input), -t.LookPitchMax, t.LookPitchMax);
            }
            bool gas = input.Gas && !tired;
            float cap = gas ? 1f : t.Cruise;
            float fwd = hang ? 0f : input.Forward > 0f ? cap * input.Forward : input.Forward < 0f ? t.Reverse * 2f * input.Forward : 0f;
            Throttle = Mathf.MoveTowards(Throttle, fwd, (Mathf.Abs(fwd) > Mathf.Abs(Throttle) ? t.Gas : t.Brake) * dt);
            Lateral = Mathf.MoveTowards(Lateral, hang ? 0f : Mathf.Clamp(input.Bank, -1f, 1f) * t.StrafeMax, t.StrafeRate * dt);
            bool climb = !hang && input.Climb && CanClimbNow(t, tired);
            bool descend = !hang && input.Descend && !climb;
            float vertical = climb ? t.ClimbRate : descend ? -t.DescendRate : 0f;
            if (Throttle > 0f) vertical += Mathf.Sin(_steerPitch * Mathf.Deg2Rad) * Throttle;   // fly where you look
            if (tired) vertical = Mathf.Min(vertical, -TiredY(body, t));
            Vertical = Mathf.MoveTowards(Vertical, vertical, t.VerticalRate * dt);
            if ((descend || tired) && canLand && height <= t.LandWithin) return LandNow(body, t, descend ? "down" : "tired");

            Pitch = Mathf.MoveTowards(Pitch, Throttle > 0.05f ? _steerPitch * 0.6f : 0f, t.PitchRate * dt);
            Bank = Mathf.MoveTowards(Bank, Lateral / Mathf.Max(0.01f, t.StrafeMax) * t.BankMax, t.BankRate * dt);
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(Lateral, 0f, Throttle), 1f);
            Vector3 dir = Quaternion.Euler(0f, Heading, 0f) * flat;
            dir.y = Vertical;
            var state = hang ? FlightState.Hover : Vertical > 0.05f ? FlightState.Climbing : Vertical < -0.05f ? FlightState.Descending
                : flat.magnitude < t.HoverBelow ? FlightState.Hover : gas ? FlightState.Powered : FlightState.Glide;
            return Finish(body, dir, t, dt, height, (descend || tired) && canLand, hang || flat.magnitude < 0.01f, tired, state);
        }

        // ── Momentum (GW2 griffon): no hover, no climb from the ground; a dive turns height into speed, the speed carries
        //    and bleeds slowly, a climb right after a dive turns it back into height, a flap (on a recharge) wins a little height back,
        //    and a crash or a landing loses it all. "So fast it was hard to control": the faster, the wider it turns. ─────────────────
        private Vector3 StepMomentum(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            float height = Height(body);
            bool canLand = CanLand(body, t), tired = IsTired(t);
            if (!body.IsFlying())
            {
                _speed = 0f; _diving = _climbing = false;
                SetState(FlightState.Grounded);
                ApplyVisual(body, dt, level: true, t);
                if (tired || !input.Climb) return Vector3.zero;
                // GW2: from the ground Space only gives a launch hop; real height comes from a ledge.
                body.TakeOff();
                _speed = t.GlideSpeed * 0.6f; _launchUntil = _clock + t.LaunchTime; _flapReady = _clock + t.FlapRecharge; _sinceDive = 99f;
                Pitch = 10f; Bank = 0f; Lateral = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} launched (hop)");
            }
            TrackStepOff(in input, body, t);
            bool launching = _clock < _launchUntil;
            // A crash keeps no speed: when the body moves at a fraction of the model's speed (a wall, the ground), the speed is lost.
            var rb = body.GetComponent<Rigidbody>();
            float fast = Mathf.Max(0.1f, body.m_flyFastSpeed);
            if (rb != null && !rb.isKinematic && !launching && _speed > 0.5f)
            {
                Vector3 rv = ToLocal(rb.linearVelocity);   // halo ring: the body's velocity in the ring frame
                float real = new Vector3(rv.x, 0f, rv.z).magnitude / fast;
                if (real < _speed * 0.3f) { t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} crashed: speed {_speed:0.0} -> {real:0.0}"); _speed = real; _diving = false; }
            }
            float before = Heading;
            float turnScale = 1f / (1f + Mathf.Max(0f, _speed - t.GlideSpeed));
            Heading = Mathf.MoveTowardsAngle(Heading, LookYaw(in input, Heading), t.SteerRate * turnScale * dt);
            Heading = Mathf.Repeat(Heading + Mathf.Clamp(input.Bank, -1f, 1f) * t.MomentumTurn * turnScale * dt, 360f);

            bool dive = input.Ability1 && !tired && height > t.Floor + 1f;
            if (dive)
            {
                if (!_diving) t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} dives (speed {_speed:0.0})");
                _diving = true; _sinceDive = 0f;
                _speed = Mathf.Min(t.MomentumMax, _speed + t.DiveAccel * dt);
            }
            else { _diving = false; _sinceDive += dt; }
            bool climb = input.Ability2 && !tired && (dive || _sinceDive <= t.ClimbWindow) && _speed > t.GlideSpeed * 0.5f && (t.CanClimb == null || t.CanClimb());
            if (climb && !_climbing) t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} climbs (speed {_speed:0.0}, {_sinceDive:0.0} s after the dive)");
            _climbing = climb;
            if (climb) _speed = Mathf.Max(0f, _speed - t.ClimbCost * dt);
            if (input.Climb && !tired && !launching && _clock >= _flapReady)
            {
                _flapReady = _clock + t.FlapRecharge; _flapUntil = _clock + t.FlapTime;
                if (dive) _speed = t.MomentumMax;   // GW2: one flap mid-dive is top speed at once
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} flaps{(dive ? " (top speed)" : "")}");
            }
            bool flapping = _clock < _flapUntil;
            if (!dive && !climb)
            {
                float to = input.Forward < 0f ? t.GlideSpeed * 0.4f : t.GlideSpeed;
                if (_speed > to) _speed = Mathf.MoveTowards(_speed, to, (input.Forward < 0f ? t.AirBrake : t.SpeedBleed) * dt);
                else if (input.Forward > 0f || flapping) _speed = Mathf.MoveTowards(_speed, to, t.SpeedBleed * 4f * dt);
            }
            float vertical = launching ? t.LaunchHop
                : dive ? -Mathf.Sin(t.DivePitch * Mathf.Deg2Rad) * Mathf.Max(_speed, t.GlideSpeed)
                : climb ? t.ClimbLift * Mathf.Clamp01(_speed / Mathf.Max(0.1f, t.GlideSpeed))
                : flapping ? t.FlapRise
                : -t.GlideSink * (_speed < t.GlideSpeed * 0.5f ? 3f : 1f);   // too slow: it sinks faster (no hover)
            if (tired) vertical = Mathf.Min(vertical, -TiredY(body, t));
            Vertical = Mathf.MoveTowards(Vertical, vertical, t.VerticalRate * 2f * dt);
            if (canLand && height <= t.LandWithin && !launching && (input.Descend || tired || (_speed < t.GlideSpeed * 1.2f && Vertical < 0f)))
                return LandNow(body, t, input.Descend ? "Ctrl" : tired ? "tired" : "touched down");

            Pitch = Mathf.MoveTowards(Pitch, dive ? -t.DivePitch : climb ? 30f : flapping ? 10f : -5f, t.PitchRate * 2f * dt);
            float turnRate = dt > 0f ? Mathf.DeltaAngle(before, Heading) / dt : 0f;
            // Barrel rolls on a held A / D with room (the griffon's W / S aren't the nose, so no loops).
            if (!Stunt(ref Bank, Mathf.Clamp(input.Bank, -1f, 1f), t.BankMax * 1.6f, t.AeroRollRate, ref _rollHeld, Room(t, height), t.AeroHold, dt))
                Bank = Mathf.MoveTowards(Bank, Mathf.Clamp(turnRate / 90f, -1f, 1f) * t.BankMax * 1.6f, t.BankRate * dt);
            Throttle = _speed;
            Lateral = 0f;
            Vector3 dir = Quaternion.Euler(0f, Heading, 0f) * Vector3.forward * _speed;
            dir.y = Vertical;
            var state = dive ? FlightState.Diving : climb || flapping || launching ? FlightState.Climbing : FlightState.Glide;
            return Finish(body, dir, t, dt, height, (input.Descend || tired) && canLand, false, tired, state);
        }

        // ── Hover (GW2 skyscale): hovers, rises / sinks in place, flies where you look (±30°), dashes, clings to walls ─────────────
        private Vector3 StepHover(Character body, in FlightInput input, FlightTuning t, float dt)
        {
            float height = Height(body);
            bool canLand = CanLand(body, t), tired = IsTired(t);
            if (!body.IsFlying())
            {
                _clinging = false;
                SetState(FlightState.Grounded);
                ApplyVisual(body, dt, level: true, t);
                if (tired || !input.Climb) return Vector3.zero;   // hold Space takes off
                body.TakeOff();
                Throttle = 0f; Lateral = 0f; Vertical = t.ClimbRate; Pitch = 0f; Bank = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} took off (Space)");
            }
            TrackStepOff(in input, body, t);
            // V: the dash (a rolling burst forward), on its recharge; the host may charge for it.
            bool dashPress = input.Ability1 && !_ability1WasDown;
            _ability1WasDown = input.Ability1;
            if (dashPress && !tired && !_clinging && _clock >= _dashReady && Pay(t, "dash"))
            {
                _dashUntil = _clock + t.DashTime; _dashReady = _clock + t.DashRecharge;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} dashes (V)");
            }
            bool dashing = _clock < _dashUntil;

            // The wall cling: W held into a steep surface just ahead; let go of W (not charging) and it bounces off.
            if (_clinging && input.Forward <= 0f && !input.Climb)
            {
                _clinging = false; Throttle = -0.4f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} lets go of the wall");
            }
            else if (!_clinging && input.Forward > 0f && !dashing && !tired && (t.CanCling == null || t.CanCling(body)) && WallAhead(body, t, out _wallNormal))
            {
                _clinging = true; _charge = 0f; _chargeWasDown = input.Climb; Throttle = Lateral = Vertical = 0f;
                t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} clings to the wall");
            }
            if (_clinging)
            {
                var away = new Vector3(-_wallNormal.x, 0f, -_wallNormal.z);
                if (away.sqrMagnitude > 1e-4f) Heading = Quaternion.LookRotation(away).eulerAngles.y;
                if (input.Climb) _charge = Mathf.Min(t.ChargeTime, _charge + dt);
                else if (_chargeWasDown && _charge > 0.15f && Pay(t, "wall_launch"))
                {
                    float k = 0.4f + 0.6f * _charge / Mathf.Max(0.05f, t.ChargeTime);
                    _launchVertical = t.WallLaunch * k; _launchUntil = _clock + t.WallLaunchTime;
                    _clinging = false; Throttle = -0.3f;
                    t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} launches off the wall (charge {_charge:0.0} s)");
                }
                _chargeWasDown = input.Climb;
                if (_clinging)
                {
                    Pitch = Mathf.MoveTowards(Pitch, 20f, t.PitchRate * dt);
                    Bank = Mathf.MoveTowards(Bank, 0f, t.BankLevel * dt);
                    return Finish(body, Vector3.zero, t, dt, height, false, true, tired, FlightState.Clinging);
                }
            }
            _chargeWasDown = false;
            bool launching = _clock < _launchUntil;
            Heading = Mathf.MoveTowardsAngle(Heading, LookYaw(in input, Heading), t.SteerRate * dt);
            _steerPitch = Mathf.Clamp(LookPitch(in input), -t.HoverPitchMax, t.HoverPitchMax);
            bool gas = input.Gas && !tired;
            float cap = gas ? 1f : t.Cruise;
            float fwd = dashing ? t.DashMul : input.Forward > 0f ? cap * input.Forward : input.Forward < 0f ? t.Reverse * 2f * input.Forward : 0f;
            Throttle = Mathf.MoveTowards(Throttle, fwd, (dashing ? 8f : Mathf.Abs(fwd) > Mathf.Abs(Throttle) ? t.Gas : t.Brake) * dt);
            Lateral = Mathf.MoveTowards(Lateral, Mathf.Clamp(input.Bank, -1f, 1f) * t.StrafeMax, t.StrafeRate * dt);
            bool climb = input.Climb && CanClimbNow(t, tired);
            bool descend = input.Descend && !climb;
            float vertical = launching ? _launchVertical : climb ? t.ClimbRate : descend ? -t.DescendRate : 0f;
            if (Throttle > 0f && !launching) vertical += Mathf.Sin(_steerPitch * Mathf.Deg2Rad) * Throttle;
            // GW2: hovering low settles it down and lands.
            bool settle = canLand && !climb && !launching && height <= t.LandWithin * 1.5f && input.Forward == 0f && input.Bank == 0f;
            if (settle) vertical = Mathf.Min(vertical, -t.DescendRate * 0.3f);
            if (tired) vertical = Mathf.Min(vertical, -TiredY(body, t));
            Vertical = Mathf.MoveTowards(Vertical, vertical, t.VerticalRate * (launching ? 3f : 1f) * dt);
            if (canLand && height <= t.LandWithin && (descend || tired || settle) && Vertical <= 0f)
                return LandNow(body, t, descend ? "down" : tired ? "tired" : "settled");

            Pitch = Mathf.MoveTowards(Pitch, Throttle > 0.05f ? _steerPitch * 0.6f : 0f, t.PitchRate * dt);
            if (dashing) Bank = 360f * (1f - (_dashUntil - _clock) / Mathf.Max(0.05f, t.DashTime));   // the dash rolls once
            else
            {
                if (Bank > 180f) Bank -= 360f;
                Bank = Mathf.MoveTowards(Bank, Lateral / Mathf.Max(0.01f, t.StrafeMax) * t.BankMax, t.BankRate * dt);
            }
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(Lateral, 0f, Throttle), Mathf.Max(1f, t.DashMul));
            Vector3 dir = Quaternion.Euler(0f, Heading, 0f) * flat;
            dir.y = Vertical;
            bool still = !dashing && input.Forward == 0f && input.Bank == 0f;
            var state = dashing ? FlightState.Boosting : Vertical > 0.05f ? FlightState.Climbing : Vertical < -0.05f ? FlightState.Descending
                : flat.magnitude < t.HoverBelow ? FlightState.Hover : gas ? FlightState.Powered : FlightState.Glide;
            return Finish(body, dir, t, dt, height, (descend || tired || settle) && canLand, still, tired, state);
        }

        // ── Shared by the models ─────────────────────────────────────────────────────────────────────────────────────────────────────

        // The limits every model honours (CanClimb / Tired, the floor, the ceiling), the vertical speed following the controls within
        // ~0.2 s (a hover holds still, a climb doesn't carry on), then vanilla's own flying: SetMoveDir / SetRun / SetLookDir.
        private Vector3 Finish(Character body, Vector3 dir, FlightTuning t, float dt, float height, bool landing, bool holdXZ, bool tired, FlightState state)
        {
            if (dir.y > 0f && !CanClimbNow(t, tired) && _clock >= _launchUntil) dir.y = 0f;
            if (dir.y < 0f && height <= t.Floor && !landing) dir.y = 0f;
            if (dir.y > 0f && height >= t.Ceiling) dir.y = 0f;
            if (height < t.Floor * 0.5f && !landing) dir.y = Mathf.Max(dir.y, 0.3f);
            var rb = body.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                Vector3 v = ToLocal(rb.linearVelocity);
                Vector3 want = dir * body.m_flyFastSpeed;
                float k = 1f - Mathf.Exp(-t.VelFollow * dt);
                v.y = Mathf.Lerp(v.y, want.y, k);
                if (holdXZ) { v.x = Mathf.Lerp(v.x, 0f, k); v.z = Mathf.Lerp(v.z, 0f, k); }
                rb.linearVelocity = ToWorld(v);
            }
            body.SetMoveDir(ToWorld(dir));
            body.SetRun(true);
            body.SetLookDir(ToWorld(Quaternion.Euler(0f, Heading, 0f) * Vector3.forward));
            SetState(state);
            ApplyVisual(body, dt, level: false, t);
            return ToWorld(dir);
        }

        // Aerobatics: true when this step's angle is a stunt (held past AeroHold with room: it keeps turning and wraps -180..180) or a
        // recovery from one (past its limit: back at most of the stunt rate, the short way); false = the model's normal attitude code.
        private static bool Stunt(ref float angle, float input, float limit, float rate, ref float held, bool room, float hold, float dt)
        {
            held = input != 0f ? held + dt : 0f;
            if (room && input != 0f && held >= hold) { angle = Mathf.DeltaAngle(0f, angle + input * rate * dt); return true; }
            float a = Mathf.DeltaAngle(0f, angle);
            if (Mathf.Abs(a) <= limit + 0.5f) { angle = a; return false; }
            angle = Mathf.MoveTowards(a, Mathf.Clamp(input * limit, -limit, limit), rate * 0.75f * dt);
            return true;
        }

        private static bool Room(FlightTuning t, float height) => t.AerobaticHeight > 0f && height >= t.AerobaticHeight;

        private Vector3 LandNow(Character body, FlightTuning t, string why)
        {
            body.Land();
            Throttle = 0f; Lateral = 0f; Vertical = 0f; Pitch = 0f; Bank = 0f; HoverLocked = false; _rollHeld = _pitchHeld = 0f;
            _speed = 0f; _diving = _climbing = _clinging = false;
            SetState(FlightState.Grounded);
            t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} landed ({why})");
            body.SetMoveDir(Vector3.zero);
            return Vector3.zero;
        }

        // Ctrl double-tapped in the air (any preset): the host's step-off.
        private void TrackStepOff(in FlightInput input, Character body, FlightTuning t)
        {
            if (Pressed(input.StepOffKey, ref _descendWasDown, ref _lastDescendPress)) StepOff(body, t);
        }

        private void StepOff(Character body, FlightTuning t)
        {
            t.Log?.Invoke($"[Flight] {Utils.GetPrefabName(body.gameObject)} step-off requested (double Ctrl)");
            try { StepOffRequested?.Invoke(); } catch (Exception e) { t.Log?.Invoke("[Flight] a StepOffRequested handler threw: " + e.Message); }
        }

        private static float Height(Character body)
        {
            Vector3 pos = body.transform.position;
            if (s_ring)
            {
                // halo ring: height above the wall floor along the local up (no flat heightmap); nothing below = 2 m, as off the ring
                int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
                return Physics.Raycast(pos, -s_up, out RaycastHit floor, 1000f, mask, QueryTriggerInteraction.Ignore) ? floor.distance : 2f;
            }
            float ground = ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(pos, out float g) ? g : pos.y - 2f;
            return pos.y - ground;
        }

        private static bool CanLand(Character body, FlightTuning t) =>
            t.CanLand != null ? t.CanLand(body) : (body.GetComponent<BaseAI>()?.m_randomFly ?? false);

        private static bool IsTired(FlightTuning t)
        {
            if (t.Tired == null) return false;
            try { return t.Tired(); } catch { return false; }
        }

        private static bool CanClimbNow(FlightTuning t, bool tired) => !tired && (t.CanClimb == null || t.CanClimb());

        private static float TiredY(Character body, FlightTuning t) => t.TiredSink / Mathf.Max(0.1f, body.m_flyFastSpeed);

        private static bool Pay(FlightTuning t, string what)
        {
            if (t.Spend == null) return true;
            try { return t.Spend(what); } catch { return true; }
        }

        private static float LookYaw(in FlightInput input, float fallback)
        {
            var f = input.Look;
            f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f).eulerAngles.y : fallback;
        }

        private static float LookPitch(in FlightInput input)
        {
            var l = input.Look;
            return l.sqrMagnitude > 1e-4f ? Mathf.Asin(Mathf.Clamp(l.normalized.y, -1f, 1f)) * Mathf.Rad2Deg : 0f;
        }

        private static int s_wallMask = -2;

        // A steep surface (a cliff, a wall, a tree trunk) just ahead of the body's centre, along its heading.
        private static bool WallAhead(Character body, FlightTuning t, out Vector3 normal)
        {
            normal = Vector3.zero;
            if (s_wallMask == -2) s_wallMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            Vector3 fwd = ToLocal(body.transform.forward);   // halo ring: heading and wall test in the ring frame
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return false;
            fwd.Normalize();
            if (!Physics.Raycast(body.GetCenterPoint(), ToWorld(fwd), out RaycastHit hit, body.GetRadius() + t.ClingReach, s_wallMask, QueryTriggerInteraction.Ignore)) return false;
            Vector3 n = ToLocal(hit.normal);
            if (Mathf.Abs(n.y) > 0.5f) return false;   // a slope or a roof, not a wall
            normal = n;
            return true;
        }

        // The model (the Character's "Visual" child) rolls with the bank and pitches with the nose; the body stays upright. The tilt
        // turns about the BODY's axes (pre-multiplied), so a Visual with its own rest rotation tilts the same way as one without.
        public void ApplyVisual(Character body, float dt) => ApplyVisual(body, dt, level: false, null);

        private void ApplyVisual(Character body, float dt, bool level, FlightTuning t)
        {
            if (_visual == null)
            {
                _visual = body != null ? body.transform.Find("Visual") : null;
                if (_visual == null) return;
                _visualRest = _visual.localRotation;
            }
            Quaternion want = level ? _visualRest : Tilt(Pitch, Bank) * _visualRest;
            _visual.localRotation = Quaternion.Slerp(_visual.localRotation, want, 1f - Mathf.Exp(-(t?.VisualSmooth ?? 8f) * dt));
        }

        // True on the second press of a double tap (two presses within DoubleTapSeconds); tracks the press edge itself.
        private bool Pressed(bool down, ref bool wasDown, ref float lastPress)
        {
            bool doubleTap = false;
            if (down && !wasDown)
            {
                if (_clock - lastPress <= DoubleTapSeconds) { doubleTap = true; lastPress = float.NegativeInfinity; }
                else lastPress = _clock;
            }
            wasDown = down;
            return doubleTap;
        }

        /// <summary>The tilt in the body's frame: + pitch = nose up, + bank = right side down (a right bank).</summary>
        public static Quaternion Tilt(float pitch, float bank) =>
            Quaternion.AngleAxis(-bank, Vector3.forward) * Quaternion.AngleAxis(-pitch, Vector3.right);

        public void ResetVisual()
        {
            if (_visual != null) _visual.localRotation = _visualRest;
            _visual = null;
        }

        private void SetState(FlightState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }
    }
}
