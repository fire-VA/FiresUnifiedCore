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
        /// <summary>Stamina per metre moved on a 38° face (0.2.283; was per second); steeper faces and Wet cost more (<see cref="Climbing.CostPerMetre"/>). Hanging still costs nothing.</summary>
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

    /// <summary>The climb's two ints on the body's own ZDO, written by its owner and read on every peer ([ghost]'s animator, §1).</summary>
    public static class ClimbZdo
    {
        public const string State = "FiresClimb";
        public const string Clip = "FiresClimbClip";
        /// <summary>The network time a wet slip's slide effect runs until (float).</summary>
        public const string Slip = "fires_climb_slip";
        public static readonly int StateHash = State.GetStableHashCode(), ClipHash = Clip.GetStableHashCode(), SlipHash = Slip.GetStableHashCode();
    }

    /// <summary>Which clip a climbing body shows (FiresClimbClip), chosen by its owner.</summary>
    public enum ClimbClip { None = 0, Enter = 1, IdleLoop = 2, UpLoop = 3, DownLoop = 4, LeftLoop = 5, RightLoop = 6, Exit = 7, Ledge = 8, Up1m = 9, Up2m = 10 }

    /// <summary>
    /// Climbing (Core 0.2.252, Tools\CLIMBING.md; Fire: "If we had taught our bot how to climb up steep cliffs like we had planned then
    /// it would have never gotten stuck in the pond", "They should be climbing up vertical surfaces if they need to up to 90°", and
    /// "they should be able to climb up building walls as well but we will need to make sure that we guard this so that they do not
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
        /// 0.2.283 (HR5cp: 12-18 stamina a SECOND, about 35-40 a metre: "stamina 50 -> 0 in 2.2 s", 0.3 of a 9.9 m face; the lead: a starting
        /// character's 50-75 stamina should get it 8-10 m up a vertical face): stamina per metre moved at 38°, x VerticalCostFactor at 90°
        /// (5.6 a metre), x WetCostFactor while Wet. Paid for the metres moved, so hanging still is free.
        /// </summary>
        public const float BaseStaminaPerMetre = 3.7f, WetCostFactor = 1.5f;
        /// <summary>At 90° the speed is this share of the 38° speed and the cost this multiple (Fire: steeper = slower and costlier).</summary>
        public const float VerticalSpeedShare = 0.6f, VerticalCostFactor = 1.5f;
        /// <summary>
        /// 0.2.285 (HR5dp: "fell: lost the face (103°) at 2.5 m": Valheim boulders bulge past vertical): a body that climbs vertical walls also climbs
        /// an overhang up to OverhangMaxAngle, slower (x OverhangSpeedShare at the limit) and dearer (x OverhangCostFactor).
        /// </summary>
        public const float OverhangMaxAngle = 115f, OverhangSpeedShare = 0.6f, OverhangCostFactor = 1.4f;
        private static float Overhang(float angle) => angle > 90f ? Mathf.InverseLerp(90f, OverhangMaxAngle, angle) : 0f;
        /// <summary>A climb doesn't start under this much stamina (players).</summary>
        public const float StartStamina = 15f;
        /// <summary>
        /// 0.2.280: a planned climb no higher than this (m) is a mantle: up a hip-high ledge or out of water over a rim, like a jump (the
        /// jump apex is 1.6-1.8 m), so a swim's spent stamina never stops or drops it. 0.2.281: 2.2 m, a ledge grabbed from the water (HR5b:
        /// a planned 2.2 m climb out of the pond "fell: stamina ran out at 0.7 m up terrain, stamina 0").
        /// </summary>
        public const float MantleMaxHeight = 2.2f;
        /// <summary>A planned climb must leave this share of the body's max stamina in hand at the top.</summary>
        public const float PlanMargin = 0.2f;
        public const float WetSlipMetres = 0.25f, StepSeconds = 1.27f;
        /// <summary>How far ahead (m) a face is felt for.</summary>
        public const float FaceReach = 1.2f;
        /// <summary>A planned climb reaches at most this far (m, flat) and this high (m).</summary>
        public const float PlanRange = 30f, PlanMaxHeight = 30f;

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
        /// True when climbing may start (Core 0.2.294, Fire 10-05: "climbing should be off by default and only toggle on automatically if we
        /// have admin terrain or RPG classes installed"). Auto reads the loaded plugins on first use, after every plugin has loaded.
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
                    bool fat = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(AdminTerrainGuid);
                    bool rpg = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(RpgClassesGuid);
                    s_enabled = fat || rpg;
                    why = s_enabled
                        ? $"Auto: {(fat && rpg ? "FiresAdminTerrain and FiresRPGClasses" : fat ? "FiresAdminTerrain" : "FiresRPGClasses")} present"
                        : "Auto: neither FiresAdminTerrain nor FiresRPGClasses loaded";
                }
                s_resolvedFor = (int)mode;
                FiresCore.Logging.FiresLogger.LogDebug($"[Climb] climbing {(s_enabled ? "ON" : "OFF")} ({why})");
                return s_enabled;
            }
        }

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_mode = config.Bind("Climbing", "Climbing", ClimbingMode.Auto,
                "SERVER. Whether players, bots and companions can climb at all. Auto: on only when FiresAdminTerrain or FiresRPGClasses is installed. On / Off: always.");
            configSync?.AddConfigEntry(s_mode);
            // Re-resolved on the next check; an Off mid-climb drops the body into vanilla movement on its next motion step.
            s_mode.SettingChanged += (sender, args) => s_resolvedFor = -1;
            s_pieces = config.Bind("Climbing", "Climb building pieces", true,
                "SERVER. Players, bots and companions may climb player-built walls (38-90 deg) as well as terrain and rock. Off: terrain and rock only (a PvP / raid lever).");
            s_playerInput = config.Bind("Climbing", "Player climb input", true,
                "Players start a climb only on a deliberate input: holding Jump while moving into a climbable face. Off: players never climb (bots and companions still climb planned routes).");
            configSync?.AddConfigEntry(s_pieces);
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Climbing, true, s_mode, s_pieces);   // 0.2.387: FAT or RPGClasses
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Climbing, false, s_playerInput);
        }

        public static bool PiecesClimbable => s_pieces == null || s_pieces.Value;
        public static bool PlayerInputClimbs => s_playerInput == null || s_playerInput.Value;

        public static void AddModifier(IClimbModifier modifier) { if (modifier != null && !s_modifiers.Contains(modifier)) s_modifiers.Add(modifier); }
        public static void RemoveModifier(IClimbModifier modifier) => s_modifiers.Remove(modifier);

        /// <summary>Core's base terms plus every registered modifier (a throwing modifier is skipped).</summary>
        public static ClimbTerms TermsFor(Character body)
        {
            var terms = new ClimbTerms { StaminaPerMetre = BaseStaminaPerMetre, Speed = BaseSpeed, MaxAngle = BaseMaxAngle, WetSlipChance = BaseWetSlipChance, Allowed = true };
            foreach (IClimbModifier m in s_modifiers)
            {
                try { m.Modify(body, ref terms); }
                catch (Exception ex) { Debug.LogWarning($"[Climb] a climb modifier threw: {ex.Message}"); }
            }
            terms.MaxAngle = Mathf.Clamp(terms.MaxAngle, MinAngle, 90f);
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
            float surface = height / Mathf.Max(0.2f, Mathf.Sin(Mathf.Clamp(angle, MinAngle, 90f) * Mathf.Deg2Rad));
            return surface * CostPerMetre(terms, angle, IsWet(body));
        }

        public static float StaminaOf(Character body) => body is Player p ? p.GetStamina() : 100f;
        public static float MaxStaminaOf(Character body) => body is Player p ? Mathf.Max(1f, p.GetMaxStamina()) : 100f;

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
            dir.y = 0f;
            if (body == null || dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            if (s_mask == 0) s_mask = FiresCore.World.Surface.Mask;
            float max = TermsFor(body).MaxAngle;
            Vector3 at = body.transform.position;
            foreach (float h in new[] { 0.4f, 1.0f, 1.6f })
            {
                if (!Physics.Raycast(at + Vector3.up * h, dir, out RaycastHit hit, FaceReach, s_mask, QueryTriggerInteraction.Ignore)) continue;
                float a = Vector3.Angle(hit.normal, Vector3.up);
                if (!ClimbableCollider(hit.collider, out surface)) return false;
                if (a < MinAngle || a > (max >= 89.99f ? OverhangMaxAngle : max) + 0.5f) { angle = a; continue; }
                face = hit;
                angle = a;
                return true;
            }
            return false;
        }

        /// <summary>FindBank's reach from deep water to the face (across a shelf or a shore) and its top's height over the water (0.2.258).</summary>
        public const float BankReach = 12f, BankMinAbove = 2.2f, BankMaxAbove = 10f;
        /// <summary>0.2.263 (R44: the bot jumped a 1.3 m "bank"; vanilla's jump clears 1.6 m at Jump 0, ~1.8 m at 14): a bank's face, foot to top, is at least this high, so a body has to climb it.</summary>
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
        /// The topmost solid surface at (x, z): terrain, rocks, pieces (what a body climbs; 0.2.258: GetGroundHeight sees the terrain
        /// only, under a rock cliff), not water or triggers, not loose bodies (characters, items, ships), and nothing
        /// <see cref="ClimbableCollider"/> refuses (trees, logs, items, creatures) or that is vegetation (0.2.259, R41: a beech without a
        /// TreeBase read as an 85° face). GetGroundHeight where nothing solid is loaded.
        /// </summary>
        public static bool TopSolid(float x, float z, out float height, out Collider hitCollider) => TopSolid(x, z, true, out height, out hitCollider);

        private static readonly float[] StandInset = { 0f, 0.3f, 0.6f };

        /// <summary>
        /// 0.2.285 (HR5de: a "0.6 m rim" was a shelf under a cliff, its top 4.7 m up): a spot a body can stand on at <paramref name="p"/> or a
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
                if (h - baseY < -0.3f) { why = "below the water line"; continue; }
                if (FiresCore.World.Water.SurfaceOr(s.x, s.z, Floating.GetLiquidLevel(new Vector3(s.x, h + 0.1f, s.z))) > h + 0.1f) { why = "under water"; continue; }
                Vector3 at = new Vector3(s.x, h + 0.05f, s.z);
                if (Physics.CheckCapsule(at + Vector3.up * 0.65f, at + Vector3.up * 1.6f, 0.3f, s_mask, QueryTriggerInteraction.Ignore)) { why = "no room to stand"; continue; }
                spot = at;
                return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="TopSolid(float, float, out float, out Collider)"/>, with building pieces counted only when <paramref name="pieces"/>
        /// (0.2.260: FindBank's natural bank is terrain and static rock; R41/R42 picked a placed "beech" and a "base_model" piece).
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

        // Bushes, saplings and small trees (Beech_small, shrubs: a Destructible of the Tree type) and pickable plants: never a bank or a face.
        private static bool IsVegetation(Collider c)
        {
            Destructible d = c.GetComponentInParent<Destructible>();
            if (d != null && (d.m_destructibleType & DestructibleType.Tree) != 0) return true;
            return c.GetComponentInParent<Pickable>() != null;
        }

        /// <summary>A face's top needs ground to stand on behind it: the next <see cref="LedgeDepth"/> m stay within <see cref="LedgeDrop"/> m of the top (0.2.259: not a post or a trunk).</summary>
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

        /// <summary><see cref="ProbeBank(Vector3, Vector3, float, float, float, out BankFace)"/>, building pieces counted only when <paramref name="pieces"/> (0.2.260).</summary>
        public static bool ProbeBank(Vector3 from, Vector3 dir, float surface, float reach, float minAbove, bool pieces, out BankFace face)
        {
            face = null;
            const float Step = 0.25f;
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
                    // (0.2.259: a post or a trunk top drops away again), else a small step or an obstacle and the shore goes on.
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
                if (g - surface > BankMaxAbove + 2f) { face = Face(from, dir, surface, footT, footG, t, g, col); return true; }
                prevT = t; prevG = g; prevCol = col;
            }
            // Out of reach while still on the face: no ledge seen, so no face (0.2.259).
            return false;
        }

        // A ledge behind a face's top (prevG): the ground at t (already sampled: g) and LedgeDepth on from it stays within LedgeDrop of the top.
        private static bool Ledge(Vector3 from, Vector3 dir, float t, float g, float top, bool pieces)
        {
            if (g < top - LedgeDrop) return false;
            for (float s = t + 0.25f; s <= t + LedgeDepth + 0.001f; s += 0.25f)
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
        /// A bank to climb out of water near <paramref name="near"/> (the climb drill, [visual]): <paramref name="foot"/> in water at
        /// least 1 m deep, <paramref name="top"/> the top of a face <see cref="BankMinAbove"/>-<see cref="BankMaxAbove"/> m above the water
        /// within <see cref="BankReach"/> m of it (0.2.258, R37: the CoopStream pond's face stands past a shallow shelf, more than 4 m out,
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
            int n = Mathf.CeilToInt(radius / 2f), cells = 0, faces = 0;
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    Vector3 w = near + new Vector3(i * 2f, 0f, j * 2f);
                    float d = (w - near).sqrMagnitude;
                    if (d > radius * radius || d >= best.d) continue;
                    FiresCore.Npc.AI.Swimming.GoalInDeepWater(w, out float depth);
                    if (depth < 1f || !ZoneSystem.instance.GetGroundHeight(w, out float bed)) continue;
                    float surface = bed + depth;
                    cells++;
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * Mathf.PI / 8f;
                        Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                        if (!ProbeBank(w, dir, surface, BankReach, BankMinAbove, false, out BankFace face)) continue;   // 0.2.260: terrain and static rock only
                        faces++;
                        float score;
                        if (face.Above > BankMaxAbove) { face.Reject = $"its top is {face.Above:0.0} m above the water (over {BankMaxAbove:0})"; score = 10f * (face.Above - BankMaxAbove); }
                        // 0.2.261 (R43: a 2.6 m rock standing in the pond; its top's column is water, so the walk to it read "the goal is in water"):
                        // a bank's top stands on dry terrain, not a rock island.
                        else if (!ZoneSystem.instance.GetGroundHeight(face.Top, out float under) || under < surface) { face.Reject = "a rock standing in the water (an island), not a bank"; score = 30f; }
                        else if (face.Angle < minAngle) { face.Reject = face.Angle < MinAngle ? "a slope, not a face" : $"under {minAngle:0}°"; score = minAngle - face.Angle; }
                        else if (face.Angle > maxAngle + 0.5f) { face.Reject = $"past {maxAngle:0}°"; score = face.Angle - maxAngle; }
                        // 0.2.263 (R44: the bot jumped onto a 53° 1.3 m face): a bank is a face a body has to climb.
                        else if (face.Height < BankMinFace) { face.Reject = $"jumpable (a {face.Height:0.0} m face; a jump clears ~1.6-1.8 m, a bank is {BankMinFace:0.0}+ m)"; score = 20f * (BankMinFace - face.Height); }
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
        /// 0.2.261: what FindBank sees near (x, z): the <paramref name="cells"/> deep-water cells (1 m+) nearest it within
        /// <paramref name="radius"/>, and per cell each of the 16 ways the face ProbeBank finds (terrain and static rock) or "nothing
        /// 1.5+ m over the water within 12 m". Console: fires_bankprobe x z [radius] (the drill's angles 45-90°).
        /// </summary>
        public static List<string> BankReport(Vector3 at, float radius, int cells, float minAngle, float maxAngle)
        {
            var lines = new List<string>();
            if (ZoneSystem.instance == null) { lines.Add("no world"); return lines; }
            var wet = new List<(float d, Vector3 w, float surface)>();
            int n = Mathf.CeilToInt(radius / 2f);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    Vector3 w = at + new Vector3(i * 2f, 0f, j * 2f);
                    float d = new Vector2(w.x - at.x, w.z - at.z).magnitude;
                    if (d > radius) continue;
                    FiresCore.Npc.AI.Swimming.GoalInDeepWater(w, out float depth);
                    if (depth < 1f || !ZoneSystem.instance.GetGroundHeight(w, out float bed)) continue;
                    wet.Add((d, w, bed + depth));
                }
            wet.Sort((a, b) => a.d.CompareTo(b.d));
            lines.Add($"{wet.Count} deep-water cell(s) within {radius:0} m of ({at.x:0}, {at.z:0}); the {Mathf.Min(cells, wet.Count)} nearest:");
            foreach (var (d, w, surface) in wet.GetRange(0, Mathf.Min(cells, wet.Count)))
            {
                lines.Add($"cell ({w.x:0}, {w.z:0}), water at {surface:0.0}, {d:0.0} m off:");
                for (int k = 0; k < 16; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    string way = $"  {k * 22.5f:0}°: ";
                    if (!ProbeBank(w, dir, surface, BankReach, BankMinAbove, false, out BankFace face)) { lines.Add(way + $"nothing {BankMinAbove:0.0}+ m over the water within {BankReach:0} m (with a ledge)"); continue; }
                    bool island = !ZoneSystem.instance.GetGroundHeight(face.Top, out float under) || under < surface;
                    string verdict = face.Above > BankMaxAbove ? "top too high" : island ? "an island" : face.Angle < minAngle ? $"under {minAngle:0}°" : face.Angle > maxAngle + 0.5f ? $"past {maxAngle:0}°" : face.Height < BankMinFace ? $"jumpable ({face.Height:0.0} m face)" : "OK";
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
                        float r = args.Length > 3 && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr) ? rr : 16f;
                        float y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float gy) ? gy : 0f;
                        foreach (string line in BankReport(new Vector3(x, y, z), r, 4, 45f, 90f))
                        {
                            args.Context?.AddString("fires_bankprobe: " + line);
                            Debug.Log("[Climbing] bankprobe: " + line);
                        }
                    });
                RegisterBankProbeTest();
            }
        }

        // ── 0.2.262 ([lead]: the unattended chain only runs "[F5 test]" commands): `bankprobe_test <x> <z> [radius]`, the bank probe as
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
            // 0.2.280 (Fire: "It should be more than capable of climbing or jumping out"): from 0.8 m up (was 1.5), so a body wading at
            // the foot of a hip-high ledge, or in a pit, plans its way up when the walk makes no headway.
            if (goal.y - from.y < 0.8f) { why = "the goal is not above"; return false; }
            if (s_mask == 0) s_mask = FiresCore.World.Surface.Mask;
            Vector3 dir = dist > 0.01f ? flat / dist : Vector3.forward;
            float prevH = from.y, height = 0f, steepest = 0f, cost = 0f;
            Vector3 foot = Vector3.zero, top = Vector3.zero;
            bool inClimb = false, any = false;
            string surface = "terrain";
            for (float t = 0.5f; t <= dist + 0.01f; t += 0.5f)
            {
                Vector3 p = from + dir * t;
                if (!Physics.Raycast(new Vector3(p.x, Mathf.Max(prevH, goal.y) + 40f, p.z), Vector3.down, out RaycastHit hit, 120f, s_mask, QueryTriggerInteraction.Ignore)) continue;
                float h = hit.point.y, rise = h - prevH;
                float a = Mathf.Atan2(Mathf.Abs(rise), 0.5f) * Mathf.Rad2Deg;
                if (rise < -3f) { why = $"a drop of {-rise:0.0} m at ({p.x:0}, {p.z:0})"; return false; }
                if (rise > 0f && a >= MinAngle)
                {
                    if (a > terms.MaxAngle + 0.5f) { why = $"a {a:0}° face at ({p.x:0}, {p.z:0}) is past this body's {terms.MaxAngle:0}°"; return false; }
                    if (!ClimbableCollider(hit.collider, out string s)) { why = $"the face at ({p.x:0}, {p.z:0}) is {s}"; return false; }
                    if (!inClimb) { if (!any) foot = from + dir * Mathf.Max(0f, t - 0.5f); inClimb = true; any = true; surface = s; }
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
            plan = new ClimbPlan { Foot = foot, Top = top + dir * 0.6f, Goal = goal, Height = height, Angle = steepest, Stamina = cost, Surface = surface };
            return true;
        }

        /// <summary>How far round a boxed-in body <see cref="PlanNearestWall"/> looks for a wall (m), in how many directions.</summary>
        public const float NearestWallReach = 4.5f;
        private const int NearestWallDirections = 16;

        /// <summary>
        /// 0.2.283 (HR5c pit: 2.5 m deep, the walker's straight way had no climb and the bot walked the bottom for 30 s): the nearest
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
                for (float r = 1.5f; r <= NearestWallReach + 0.01f; r += 1f)
                {
                    Vector3 p = from + dir * r;
                    if (!TopSolid(p.x, p.z, out float h, out _) || h - from.y < 0.8f) continue;
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

        private const float EngageSeconds = 0.4f, TopOutSeconds = 0.8f, FallShowSeconds = 0.6f, LostFaceSeconds = 0.3f, HugSpeed = 0.5f;

        private sealed class Run
        {
            public State State;
            public float Since;
            public Vector3 Normal, WallDir;
            public float Angle;
            public float StartY, StaminaAtStart, StartedAt;
            // 0.2.291: what the climb itself charged (the topped-out / fell lines; start minus end counted regen and vanilla's drains).
            public float Charged;
            public float LostFaceSince = -1f, NextStep, SlipUntil = -1f;
            public Vector3 TopFrom, TopTo;
            public bool Planned;
            public string Surface, Reason;
            public ClimbClip Clip;
            // 0.2.280: a planned rim up to Climbing.MantleMaxHeight (out of water, up a hip-high ledge): stamina never drops it.
            public bool Mantle;
            // 0.2.281: the planned height (0 for a player's own climb), so the last MantleMaxHeight of a planned climb is a pull-up.
            public float PlanHeight;
            // 0.2.283: where the plan tops out (NaN x: none) and where the face was met, for a mantle's vault; the highest the body got
            // and when, for the no-headway watchdog.
            public Vector3 PlanTop = new Vector3(float.NaN, 0f, 0f), FacePoint;
            public float BestY, BestYAt;
            // 0.2.283: where the body was last step, so the climb is paid by the metre moved.
            public Vector3 LastPos;
            // 0.2.283: what a mantle's height is measured from: the water line for a swimmer (its feet ride Character.m_swimDepth, 2 m,
            // under it, and a rim exit's height is over the water), else the feet.
            public float MantleBaseY;
            // 0.2.284: Space was down last step (a player's grab starts with it down), so only a fresh press releases.
            public bool JumpWasHeld;
            // 0.2.287: how long this top-out takes (TopOutTimeFor).
            public float TopOutTime;
            // 0.2.289: where the grab was (the hang / move line gives the metres along the face from it).
            public Vector3 StartPos;
            // 0.2.289: a turn round a corner under way (until > 0): from / to position and facing; no new one before CornerNext.
            public float CornerUntil, CornerNext;
            // 0.2.290: an over-the-wall top-out's apex (NaN: a plain top-out); the top-edge hang was said.
            public float TopApexY = float.NaN;
            public bool TopEdgeLogged;
            public Vector3 CornerFromPos, CornerToPos, CornerFromDir, CornerToDir;
        }

        /// <summary>0.2.284: a player's release press counts this long (s) after the grab at the earliest; A / D move along the face at this share of the climb speed.</summary>
        private const float ReleaseAfterSeconds = 0.3f, SideSpeedShare = 0.7f;
        // After a release, Space has to come up before the next grab (the local player only: there is one).
        private static bool s_jumpMustRise;
        private static bool JumpHeld() => ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump");

        // The level a mantle's height counts from for this body now (see Run.MantleBaseY).
        private static float MantleBase(Character body) =>
            body.IsSwimming() ? Mathf.Max(body.transform.position.y, body.GetLiquidLevel()) : body.transform.position.y;

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

        private static bool TryBegin(Character body, RaycastHit face, float angle, string surface, bool planned, string reason, Vector3 goal, float height, Vector3 planTop)
        {
            if (!Climbing.Enabled) return false;
            if (body.IsDead() || body.InAttack() || body.InDodge() || body.IsAttached()) return false;
            ClimbTerms terms = Climbing.TermsFor(body);
            if (!terms.Allowed) return false;
            // 0.2.280 (Fire, HR5: "the bot is stuck in the pond at a hip level ledge"): a planned rim up to MantleMaxHeight is a mantle
            // (a vault up a ledge, like a jump), started and finished whatever stamina a swim left; a real climb still needs StartStamina.
            bool mantle = planned && height > 0f && height <= Climbing.MantleMaxHeight;
            if (body is Player && !mantle && Climbing.StaminaOf(body) < Climbing.StartStamina) return false;
            Vector3 wall = -face.normal;
            wall.y = 0f;
            if (wall.sqrMagnitude < 0.0001f) return false;
            // 0.2.282 (HR5be pit, Fire: "he's facing the wrong way"): a planned climb starts only with the body facing into the face; the
            // walker squares it up at the face first. A player's start keeps its own 60° rule (TryPlayerInput).
            float facingYaw = Yaw(body.transform.forward), wallYaw = Yaw(wall), apart = Mathf.Abs(Mathf.DeltaAngle(facingYaw, wallYaw));
            if (planned && apart > StartFacingDegrees)
            {
                if (Time.time >= s_facingLogAt)
                {
                    s_facingLogAt = Time.time + 2f;
                    Debug.Log($"[Climb] climb: {Name(body)} not started up {surface}: facing {facingYaw:0}° vs the face {wallYaw:0}° (normal {Yaw(face.normal):0}°), " +
                              $"{apart:0}° apart (over {StartFacingDegrees:0}°)");
                }
                return false;
            }
            var run = new Run
            {
                State = State.Engage, Since = Time.time, StartedAt = Time.time, Normal = face.normal, WallDir = wall.normalized, Angle = angle,
                StartY = body.transform.position.y, StaminaAtStart = Climbing.StaminaOf(body), Planned = planned, Surface = surface, Reason = reason,
                NextStep = Time.time + Climbing.StepSeconds, Mantle = mantle, PlanHeight = planned ? height : 0f,
                PlanTop = planTop, FacePoint = face.point, BestY = body.transform.position.y, BestYAt = Time.time, MantleBaseY = MantleBase(body),
                JumpWasHeld = !planned, StartPos = body.transform.position,
            };
            s_runs[body] = run;
            SetZdo(body, run, ClimbClip.Enter);
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
        /// <summary>0.2.285: a vault lands no further than this (m, flat) from where the body is.</summary>
        public const float MantleReach = 2.0f;

        // ---- 0.2.289: corners (Fire: "we want to be able to go around corners ideally instead of falling off the wall") ----

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
            sideDir.y = 0f;
            if (sideDir.sqrMagnitude < 0.0001f || run.WallDir.sqrMagnitude < 0.0001f) return false;
            sideDir.Normalize();
            int mask = FiresCore.World.Surface.Mask;
            Vector3 at = body.transform.position;
            float radius = body.GetRadius();
            Vector3 chest = at + Vector3.up * 1.0f;
            ClimbTerms terms = Climbing.TermsFor(body);

            // Inside corner: a face across the way it moves.
            if (Physics.Raycast(chest, sideDir, out RaycastHit inside, radius + InsideReach, mask, QueryTriggerInteraction.Ignore)
                && CornerFace(inside, terms, out string insideSurface))
            {
                Vector3 into = -inside.normal;
                into.y = 0f;
                float turn = into.sqrMagnitude > 0.0001f ? Vector3.Angle(run.WallDir, into.normalized) : 0f;
                if (turn >= 30f && turn <= 150f)
                {
                    Vector3 to = inside.point - into.normalized * (radius + 0.05f);
                    to.y = at.y;
                    StartCorner(body, run, to, into.normalized, inside.normal, "an inside", turn, insideSurface);
                    return true;
                }
            }

            // Outside edge: the face ends EdgeProbe past the body's side (or is already gone right ahead).
            if (!lost && Physics.Raycast(chest + sideDir * (radius + EdgeProbe), run.WallDir, out _, Climbing.FaceReach, mask, QueryTriggerInteraction.Ignore)) return false;
            // Round the corner: the facing turns away from the move (moving right round a pillar's corner it ends facing the side face).
            float sign = Vector3.Dot(sideDir, Vector3.Cross(Vector3.up, run.WallDir)) > 0f ? -1f : 1f;
            Vector3 corner = chest + sideDir * (radius + EdgeProbe) + run.WallDir * (radius + 0.35f);
            foreach (float a in CornerTurns)
            {
                Vector3 d = Quaternion.AngleAxis(sign * a, Vector3.up) * run.WallDir;
                d.y = 0f;
                d.Normalize();
                if (!Physics.Raycast(corner - d * CornerProbeBack, d, out RaycastHit hit, CornerProbeReach, mask, QueryTriggerInteraction.Ignore)) continue;
                if (!CornerFace(hit, terms, out string surface)) continue;
                Vector3 into = -hit.normal;
                into.y = 0f;
                if (into.sqrMagnitude < 0.0001f || Vector3.Angle(into.normalized, d) > 35f) continue;   // a face this way round, not a side of it
                Vector3 to = hit.point - into.normalized * (radius + 0.05f);
                to.y = at.y;
                // Room for the body there.
                if (Physics.CheckCapsule(to + Vector3.up * (radius + 0.1f), to + Vector3.up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                StartCorner(body, run, to, into.normalized, hit.normal, "an outside", Vector3.Angle(run.WallDir, into.normalized), surface);
                return true;
            }
            return false;
        }

        // A face the body can climb on after a turn: steep enough (MinAngle) and not past the overhang limit, and climbable.
        private static bool CornerFace(RaycastHit hit, ClimbTerms terms, out string surface)
        {
            surface = "nothing";
            float a = Vector3.Angle(hit.normal, Vector3.up);
            float max = terms.MaxAngle >= 89.99f ? Climbing.OverhangMaxAngle : terms.MaxAngle;
            if (a < Climbing.MinAngle || a > max + 0.5f) return false;
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
            Debug.Log($"[Climb] climb: {Name(body)} round {kind} corner {turn:0}° onto {surface} at {body.transform.position.y - run.StartY:0.0} m up");
        }

        // 0.2.287 (the lead: from the water a 0.8 m rim is a ~2.8 m rise in 0.4 s): a top-out's time grows with how far it goes,
        // 0.5 s + 0.25 s a metre up + 0.2 s a metre in, 0.6-1.6 s.
        private static float TopOutTimeFor(Vector3 from, Vector3 to)
        {
            float rise = Mathf.Max(0f, to.y - from.y);
            float inward = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            return Mathf.Clamp(0.5f + 0.25f * rise + 0.2f * inward, 0.6f, 1.6f);
        }

        // Where a mantle vaults to: the plan's top, else the first floor MantleInset in from the face, at most MantleMaxHeight + 0.4 m
        // above the feet and not below them, out of the water, with room to stand (a capsule from knee to head height).
        private static bool MantleTarget(Character body, Run run, out Vector3 onto, out string tried, out float topRise)
        {
            onto = Vector3.zero;
            tried = "no spot";
            topRise = 0f;
            float feet = MantleBase(body);
            int mask = FiresCore.World.Surface.Mask;
            Vector3 at = body.transform.position;
            for (int i = -1; i < MantleInset.Length; i++)
            {
                Vector3 s;
                if (i < 0) { if (float.IsNaN(run.PlanTop.x)) continue; s = run.PlanTop; }
                else s = run.FacePoint + run.WallDir * MantleInset[i];
                // 0.2.285 (HR5de: "vaulting 1.9 m onto (-95.8, 37.9, -125.5), 7.3 m in", the plan's top a shore 7 m off): a vault lands at the
                // face it grabbed, within MantleReach.
                if (new Vector2(s.x - at.x, s.z - at.z).magnitude > MantleReach) { tried = "the planned top is out of reach"; continue; }
                if (!Climbing.TopSolid(s.x, s.z, out float h, out _)) { tried = "no ground there"; continue; }
                float rise = h - feet;
                topRise = Mathf.Max(topRise, rise);
                if (rise > Climbing.MantleMaxHeight + 0.4f) { tried = $"the top is {rise:0.0} m up"; continue; }
                if (rise < -0.3f) { tried = $"the spot is {-rise:0.0} m down"; continue; }
                if (FiresCore.World.Water.SurfaceOr(s.x, s.z, Floating.GetLiquidLevel(new Vector3(s.x, h + 0.1f, s.z))) > h + 0.1f) { tried = "the spot is under water"; continue; }
                Vector3 spot = new Vector3(s.x, h + 0.05f, s.z);
                if (Physics.CheckCapsule(spot + Vector3.up * 0.65f, spot + Vector3.up * 1.6f, 0.3f, mask, QueryTriggerInteraction.Ignore)) { tried = "no room to stand"; continue; }
                onto = spot;
                return true;
            }
            return false;
        }
        /// <summary>How fast Core turns a body into the face before a planned start (vanilla turns a player 300°/s, a swimmer 100°/s).</summary>
        private const float SquareUpDegreesPerSecond = 720f;
        private static float s_facingLogAt;

        // 0.2.306 (the lead 10-06: core_today's climb "states Idle" with no [Climb] line at all): while the local player holds Jump,
        // the gate that kept the climb from starting, at Debug, at most once a second.
        private static float s_whyNotAt;

        private static void WhyNot(Player player, string why)
        {
            if (Time.time < s_whyNotAt) return;
            s_whyNotAt = Time.time + 1f;
            FiresCore.Logging.FiresLogger.LogDebug($"[Climb] climb: {Name(player)} Jump held, not started: {why}");
        }

        // What the knee / waist / chest rays hit straight ahead, for the why-line (any layer of the climb mask, any distance up to 3 m).
        private static string AheadHit(Player player, Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return "no forward";
            forward.Normalize();
            foreach (float h in new[] { 0.4f, 1.0f, 1.6f })
            {
                if (!Physics.Raycast(player.transform.position + Vector3.up * h, forward, out RaycastHit hit, 3f, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore)) continue;
                Climbing.ClimbableCollider(hit.collider, out string what);
                return $"at {h:0.0} m up: {what} at {hit.distance:0.00} m, {Vector3.Angle(hit.normal, Vector3.up):0}° steep";
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

        // 0.2.285 (HR5de: "stamina 35 -> 32 in Engage", 19 a metre against the stated 7.3, "SWIMMING in 1.9 m of water" through the first
        // 2 m of a climb out of the pond): vanilla's swim drain (Player.OnSwimming, ~5 a second while moving) and its drowning tick ran on
        // under the climb. The climb owns a body on the face: no swim drain, no drowning there.
        // 0.2.291 ([release], Fire's log: 48 -> 15 over the first 2.0 m of a climb priced 5.6 a metre): vanilla's CheckRun needs only the run
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
                        // Climbing switched off: a body still on a face ends its climb now and moves as vanilla (0.2.294).
                        if (s_runs.TryGetValue(__instance, out Run off)) End(__instance, off, "climbing is off (config)");
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
            // 0.2.282: at the face, Core turns the body into it before the start (TryBegin wants it within StartFacingDegrees), whoever
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
                    s_facingLogAt = Time.time + 2f;
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
            // 0.2.284: after a release (Space while attached) a new grab needs Space let go and pressed again, so the release press itself
            // never re-grabs the face it just left.
            if (s_jumpMustRise) { if (!jumpHeld) s_jumpMustRise = false; else WhyNot(player, "Jump must be let go and pressed again after a release"); return; }
            if (!Climbing.PlayerInputClimbs) { if (jumpHeld) WhyNot(player, "[Climbing] Player climb input is off"); return; }
            if (player.m_moveDir.sqrMagnitude < 0.01f) { if (jumpHeld) WhyNot(player, "no move direction (Forward not held)"); return; }
            if (!jumpHeld) return;
            Vector3 forward = player.transform.forward;
            if (!Climbing.FaceAhead(player, forward, out RaycastHit face, out float angle, out string surface))
            {
                WhyNot(player, $"no climbable face within {Climbing.FaceReach} m ahead ({AheadHit(player, forward)})");
                return;
            }
            Vector3 into = -face.normal;
            into.y = 0f;
            if (into.sqrMagnitude < 0.0001f || Vector3.Dot(forward, into.normalized) < 0.5f)
            {
                WhyNot(player, $"not facing {surface} squarely (facing {Yaw(forward):0}°, the face {Yaw(into):0}°, {angle:0}° steep)");
                return;
            }
            TryBegin(player, face, angle, surface, false, "forward + jump against the face", face.point, 0f, new Vector3(float.NaN, 0f, 0f));
        }

        private static void Step(Character body, Run run, float dt)
        {
            if (body.IsDead()) { End(body, run, "dead"); return; }
            Rigidbody rb = body.m_body;
            switch (run.State)
            {
                case State.Engage:
                    Hold(body, rb, run, Vector3.zero);
                    if (Time.time - run.Since >= EngageSeconds)
                    {
                        // 0.2.283 (HR5cc: a "0.8 m" bank exit climbed the 12 m cliff above it from 1.9 m of water, 0.2 m up, stamina 44 -> 0,
                        // and the bot drowned at the face): a mantle is a vault onto the rim it planned, not a climb up whatever face is there.
                        if (run.Mantle)
                        {
                            if (MantleTarget(body, run, out Vector3 onto, out string tried, out float topRise))
                            {
                                Vector3 from = body.transform.position;
                                float inward = Vector3.Distance(new Vector3(onto.x, 0f, onto.z), new Vector3(from.x, 0f, from.z));
                                run.State = State.TopOut;
                                run.Since = Time.time;
                                run.TopFrom = from;
                                run.TopTo = onto;
                                run.TopOutTime = TopOutTimeFor(from, onto);
                                SetZdo(body, run, onto.y - from.y > 1.2f ? ClimbClip.Ledge : ClimbClip.Up1m);
                                Debug.Log($"[Climb] climb: {Name(body)} mantle: vaulting {onto.y - from.y:0.0} m onto ({onto.x:0.0}, {onto.y:0.0}, {onto.z:0.0}), {inward:0.0} m in, over {run.TopOutTime:0.0} s");
                                return;
                            }
                            // 0.2.285 (the lead, HR5de: a "0.6 m rim" whose top was 4.7 m up, "climbing the face instead" from the water on 0
                            // stamina): no clear top in reach = not a rim. From the water it is barred and the swim exit picks again, unless the
                            // stamina pays for the whole real face (x1.2), which it then climbs as a paid climb (the last MantleMaxHeight a pull-up).
                            float faceCost = topRise > 0f ? Climbing.StaminaFor(body, topRise, Mathf.Max(run.Angle, Climbing.MinAngle)) * 1.2f : float.MaxValue;
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
                        run.BestY = body.transform.position.y;
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
                        // 0.2.290: over a thin wall: up to the apex above its top, across, then down onto the far side.
                        float apex = run.TopApexY;
                        target = k < 0.4f ? new Vector3(run.TopFrom.x, Mathf.Lerp(run.TopFrom.y, apex, k / 0.4f), run.TopFrom.z)
                            : k < 0.75f ? Vector3.Lerp(new Vector3(run.TopFrom.x, apex, run.TopFrom.z), new Vector3(run.TopTo.x, apex, run.TopTo.z), (k - 0.4f) / 0.35f)
                            : new Vector3(run.TopTo.x, Mathf.Lerp(apex, run.TopTo.y, (k - 0.75f) / 0.25f), run.TopTo.z);
                    }
                    else target = k < 0.5f
                        ? new Vector3(run.TopFrom.x, Mathf.Lerp(run.TopFrom.y, run.TopTo.y, k * 2f), run.TopFrom.z)
                        : Vector3.Lerp(new Vector3(run.TopFrom.x, run.TopTo.y, run.TopFrom.z), run.TopTo, (k - 0.5f) * 2f);
                    Hold(body, rb, run, Vector3.zero);
                    rb.MovePosition(target);
                    if (k >= 1f)
                    {
                        float climbed = run.TopTo.y - run.StartY;
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
            // 0.2.281 (HR5be pit: a planned 2.2 m climb "fell: stamina ran out at 0.7 m up terrain, stamina 0", the face steeper than the
            // ground samples priced it): with no more than MantleMaxHeight left to the planned top, the last of it is a pull-up.
            bool pullUp = run.PlanHeight > 0f && run.PlanHeight - (body.transform.position.y - run.StartY) <= Climbing.MantleMaxHeight;
            // 0.2.283 (HR5cc): a mantle that found no top to vault onto and climbs on is a mantle only up to the rim it planned; past it,
            // a climb under the stamina rules.
            if (run.Mantle && body.transform.position.y - run.MantleBaseY > run.PlanHeight + 0.5f) run.Mantle = false;
            // A planned climb that gains nothing in ClimbHeadwaySeconds is going nowhere (HR5cc: 40 s at 0.2 m, a wet slip each step,
            // the swim exit never given up because the climb held the body): it lets go, and a face it met from the water is barred.
            if (run.Planned)
            {
                if (body.transform.position.y > run.BestY + ClimbHeadwayMetres) { run.BestY = body.transform.position.y; run.BestYAt = Time.time; }
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
            // 0.2.284 (Fire: "we should not have to hold W the whole time or space; once we're locked on the wall we should be able to just
            // sit there if we want; only actually climbing would use stamina, and hitting space a second time would release from the
            // wall"): a player's climb follows the move keys on the wall (W up, S down, A / D along it) and hangs with none held.
            Vector3 input = run.Planned ? Vector3.zero : body.m_moveDir;
            input.y = 0f;
            Vector3 wallRight = Vector3.Cross(Vector3.up, run.WallDir).normalized;
            float upIn = run.Planned ? 1f : input.sqrMagnitude > 0.01f ? Mathf.Clamp(Vector3.Dot(input, run.WallDir), -1f, 1f) : 0f;
            float sideIn = run.Planned ? 0f : input.sqrMagnitude > 0.01f ? Mathf.Clamp(Vector3.Dot(input, wallRight), -1f, 1f) : 0f;
            bool hanging = Mathf.Abs(upIn) < 0.3f && Mathf.Abs(sideIn) < 0.3f;
            // Out of stamina: a planned climb, or a player trying to move, falls; a player hanging still stays (hanging is free).
            if (body is Player p && p.GetStamina() <= 0f && !run.Mantle && !pullUp && (run.Planned || !hanging)) { ToFall(body, run, "stamina ran out"); return; }
            bool hitNow = FiresCore.Npc.Combat.FireScare.HitLately(body, 0.35f, out Character hitter, out _);
            if (body.IsStaggering() || hitNow)
            {
                ToFall(body, run, $"hit{(hitter != null ? $" by {hitter.m_name}" : "")}");
                return;
            }
            // A player lets go with a fresh Space press (the grab's own press has to come up first); a planned climb holds on until the
            // top or its driver stops it.
            if (!run.Planned && body == Player.m_localPlayer)
            {
                bool jumpNow = JumpHeld();
                if (jumpNow && !run.JumpWasHeld && Time.time - run.StartedAt > ReleaseAfterSeconds)
                {
                    s_jumpMustRise = true;
                    ToFall(body, run, "released (Space)");
                    return;
                }
                run.JumpWasHeld = jumpNow;
                // Down at the foot of the face: off it, standing.
                if (upIn < -0.3f && body.IsOnGround()) { End(body, run, "stepped down off the face"); return; }
            }
            // 0.2.289 (Fire: "we want to be able to go around corners ideally instead of falling off the wall"): a turn under way carries
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
            if (Mathf.Abs(sideIn) >= 0.3f && Time.time >= run.CornerNext && TryCorner(body, run, wallRight * Mathf.Sign(sideIn), false)) return;
            if (Climbing.FaceAhead(body, run.WallDir, out RaycastHit face, out float angle, out string surface))
            {
                run.LostFaceSince = -1f;
                run.Normal = face.normal;
                Vector3 wall = -face.normal;
                wall.y = 0f;
                if (wall.sqrMagnitude > 0.0001f) run.WallDir = wall.normalized;
                run.Angle = angle;
                // 0.2.290 (Fire: "we should be mantling to the top of the wall etc once our arms/shoulders get past the top so we're not
                // climbing on air"): going up, the face gone at hand height while still there lower down = the top edge is between the
                // chest and the hands: the top-out (onto it, over it, or the top-edge hang) starts now.
                if ((run.Planned || upIn > 0.3f) && !HandsOnFace(body, run) && !FaceAboveHands(body, run))
                {
                    if (Ledge(body, run, false, out Vector3 handLedge, out bool handOver, out float handApex, out bool handEdge))
                    {
                        Debug.Log($"[Climb] climb: {Name(body)} hands over the top of {run.Surface} at {body.transform.position.y - run.StartY:0.0} m up: mantling");
                        BeginTopOut(body, run, handLedge, handOver, handApex);
                        return;
                    }
                    if (handEdge && !run.Planned)
                    {
                        if (!run.TopEdgeLogged)
                        {
                            run.TopEdgeLogged = true;
                            Debug.Log($"[Climb] climb: {Name(body)} at the top edge of {run.Surface}, {body.transform.position.y - run.StartY:0.0} m up: the drop beyond is more than {OverMaxDrop:0} m; hanging (S down, Space lets go)");
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
                // 0.2.289: moving along and the face gone from right ahead: round the corner if a face goes on, before anything else.
                if (Mathf.Abs(sideIn) >= 0.3f && TryCorner(body, run, wallRight * Mathf.Sign(sideIn), true)) return;
                // Going up (or hanging) past the top: onto it, or (0.2.290) over a thin wall.
                bool topEdge = false;
                if (run.Planned || upIn > -0.3f)
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
                    // 0.2.290 (the lead): at a top it can neither stand on nor go over (the drop beyond is too long), a player hangs on the
                    // edge: no higher; S climbs back down, Space lets go.
                    run.LostFaceSince = -1f;
                    if (!run.TopEdgeLogged)
                    {
                        run.TopEdgeLogged = true;
                        Debug.Log($"[Climb] climb: {Name(body)} at the top edge of {run.Surface}, {body.transform.position.y - run.StartY:0.0} m up: the drop beyond is more than {OverMaxDrop:0} m; hanging (S down, Space lets go)");
                    }
                    if (upIn > -0.3f)
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
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, run.Normal);
            if (up.sqrMagnitude < 0.0001f) up = Vector3.up;
            up.Normalize();
            float speed = Climbing.SpeedAt(terms, run.Angle);
            if (Time.time < run.SlipUntil) speed = 0f;
            // Along the face for a player's A / D (SideSpeedShare of the climb speed); a planned climb goes straight up.
            Vector3 side = Vector3.ProjectOnPlane(wallRight, run.Normal);
            side = side.sqrMagnitude > 0.0001f ? side.normalized : wallRight;
            Vector3 move = hanging ? Vector3.zero : up * speed * upIn + side * speed * SideSpeedShare * sideIn;
            Hold(body, rb, run, move - run.Normal * HugSpeed);
            // 0.2.283: a mantle costs nothing (HR5cc: 34 stamina at 0.2 m up).
            // 0.2.283: paid by the metre moved (a wet slip back down is not charged, and holding still is free).
            Vector3 nowPos = body.transform.position;
            // 0.2.285 (HR5de 19 a metre, HR5dp 14 against 8.3): only movement ALONG the face counts, never the bob into and out of it or a
            // swim's heave; a planned climb pays for its rise only.
            Vector3 step = nowPos - run.LastPos;
            float moved = Time.time < run.SlipUntil ? 0f
                : run.Planned ? Mathf.Max(0f, Vector3.Dot(step, up))
                : Mathf.Abs(Vector3.Dot(step, up)) + Mathf.Abs(Vector3.Dot(step, side));
            run.LastPos = nowPos;
            if (!run.Mantle && moved > 0.001f) { float cost = Climbing.CostPerMetre(terms, run.Angle, Climbing.IsWet(body)) * moved; body.UseStamina(cost); run.Charged += cost; }
            // Wet: each step may slip 0.25 m (Fire: "a chance to slip down half a meter periodically"); hanging still takes no steps.
            if (hanging) run.NextStep = Mathf.Max(run.NextStep, Time.time + 0.2f);
            if (Time.time >= run.NextStep)
            {
                run.NextStep = Time.time + Climbing.StepSeconds;
                if (body.GetSEMan() != null && body.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectWet) && UnityEngine.Random.value < terms.WetSlipChance)
                {
                    rb.MovePosition(body.transform.position - up * Climbing.WetSlipMetres);
                    run.SlipUntil = Time.time + 0.4f;
                    SetZdo(body, run, ClimbClip.DownLoop);
                    if (ZNet.instance != null) body.m_nview.GetZDO().Set(ClimbZdo.SlipHash, (float)ZNet.instance.GetTimeSeconds() + 0.4f);
                    Debug.Log($"[Climb] climb: {Name(body)} slipped {Climbing.WetSlipMetres:0.00} m (wet) at {body.transform.position.y - run.StartY:0.0} m");
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
                        { float along = Vector3.Dot(body.transform.position - run.StartPos, wallRight); Debug.Log($"[Climb] climb: {Name(body)} {(clip == ClimbClip.IdleLoop ? "hanging" : "moving")} at {body.transform.position.y - run.StartY:0.0} m up, {Mathf.Abs(along):0.0} m {(along >= 0f ? "right" : "left")} of the grab on {run.Surface}, stamina {Climbing.StaminaOf(body):0}"); }
                    SetZdo(body, run, clip);
                }
            }
        }

        // The ledge over the edge: chest height has no face ahead, and 0.8 m on, a floor (under the slide angle) within 2.2 m above the feet.
        // 0.2.290 (Fire on 0.2.289, an EBM stand-in wall: "fell: lost the face at 1.0 m up" twice): the ledge was looked for only 0.8 m
        // ahead of the body, beyond a thin wall, on the ground behind it. Now the wall top first, nearest in (measured from the face),
        // where a body fits (floor under its width); a top too narrow to stand on is gone OVER to the far side when that drop is safe.
        private static readonly float[] LedgeInset = { 0.1f, 0.25f, 0.45f, 0.7f, 1.0f };
        private static readonly float[] OverReach = { 0.5f, 0.8f, 1.2f, 1.6f };
        /// <summary>0.2.290: going over a thin wall lands no more than this (m) below its top.</summary>
        public const float OverMaxDrop = 3f;

        private static bool Ledge(Character body, Run run, bool estimateTop, out Vector3 ledge, out bool over, out float apexY, out bool topEdge)
        {
            ledge = Vector3.zero;
            over = false;
            apexY = float.NaN;
            topEdge = false;
            Vector3 at = body.transform.position;
            int mask = FiresCore.World.Surface.Mask;
            float radius = body.GetRadius();
            float face = radius + 0.05f;   // the face is about this far ahead of the body's middle
            float topY = float.NaN, topD = 0f;
            foreach (float inset in LedgeInset)
            {
                float d = face + inset;
                Vector3 probe = at + run.WallDir * d + Vector3.up * 2.4f;
                if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3.2f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Vector3.Angle(hit.normal, Vector3.up) >= Climbing.MinAngle) continue;
                float rise = hit.point.y - at.y;
                if (rise > 2.2f || rise < -0.2f) continue;   // too high, or the ground beyond a thin wall
                if (float.IsNaN(topY)) { topY = hit.point.y; topD = d; }
                if (!WideEnough(hit.point, run.WallDir, radius, mask)) continue;
                Vector3 spot = hit.point + Vector3.up * 0.05f;
                if (Physics.CheckCapsule(spot + Vector3.up * (radius + 0.1f), spot + Vector3.up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                ledge = spot + run.WallDir * 0.1f;
                return true;
            }
            // A top too thin for any probe: under the lowest face ray, which missed (only when the face is gone at every ray; the hands'
            // check needs a measured top, or a notch in a tall wall would read as its top).
            if (float.IsNaN(topY)) { if (!estimateTop) return false; topY = at.y + 0.4f; }
            // Over it: the floor on the far side, within OverMaxDrop below the top, with room to stand.
            foreach (float reach in OverReach)
            {
                Vector3 from = at + run.WallDir * (Mathf.Max(topD, face) + reach) + Vector3.up * (topY - at.y + 1.0f);
                if (!Physics.Raycast(from, Vector3.down, out RaycastHit far, 1.0f + OverMaxDrop + 0.5f, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Vector3.Angle(far.normal, Vector3.up) >= Climbing.MinAngle) continue;
                float drop = topY - far.point.y;
                if (drop < -0.2f || drop > OverMaxDrop) continue;
                Vector3 spot = far.point + Vector3.up * 0.05f;
                if (Physics.CheckCapsule(spot + Vector3.up * (radius + 0.1f), spot + Vector3.up * 1.6f, radius * 0.85f, mask, QueryTriggerInteraction.Ignore)) continue;
                ledge = spot;
                over = true;
                apexY = topY + 0.45f;
                return true;
            }
            // 0.2.290 (the lead): no way onto or over the top (a long drop beyond): the body is at the top edge; it hangs there.
            topEdge = true;
            return false;
        }

        // 0.2.290: onto the top (Up1m / Ledge clip) or, over a thin wall, up / across / down to the far side (Ledge clip).
        private static void BeginTopOut(Character body, Run run, Vector3 ledge, bool over, float apexY)
        {
            Vector3 from = body.transform.position;
            run.State = State.TopOut;
            run.Since = Time.time;
            run.TopFrom = from;
            run.TopTo = ledge;
            run.TopApexY = over ? apexY : float.NaN;
            run.TopOutTime = over
                ? Mathf.Clamp(0.6f + 0.25f * Mathf.Max(0f, apexY - from.y) + 0.2f * new Vector2(ledge.x - from.x, ledge.z - from.z).magnitude + 0.1f * Mathf.Max(0f, apexY - ledge.y), 0.8f, 2.0f)
                : TopOutTimeFor(from, ledge);
            SetZdo(body, run, over || ledge.y - from.y > 1.2f ? ClimbClip.Ledge : ClimbClip.Up1m);
            if (over) Debug.Log($"[Climb] climb: {Name(body)} over the top of {run.Surface} onto the far side, {apexY - 0.45f - ledge.y:0.0} m down, over {run.TopOutTime:0.0} s");
        }

        /// <summary>0.2.290: the hands' height over the feet while climbing (m); the face gone there = the hands are over the top.</summary>
        public const float HandHeight = 1.7f;

        // The face still in front of the hands (a climbable steep surface within reach at HandHeight).
        private static bool HandsOnFace(Character body, Run run) => FaceAt(body, run, HandHeight);

        // 0.2.291 (Fire on 0.2.290: "at the top edge of [EasyBake/Zone_18_13/standins], 2.0 m up", then on up the same wall to 3.1 m: a
        // window sill or a recess at hand height read as the top): the wall going on above the hands (0.5 or 1.0 m higher) is no top.
        private static bool FaceAboveHands(Character body, Run run) => FaceAt(body, run, HandHeight + 0.5f) || FaceAt(body, run, HandHeight + 1.0f);

        // A climbable steep surface within reach in front of the body at this height over its feet.
        private static bool FaceAt(Character body, Run run, float height)
        {
            Vector3 from = body.transform.position + Vector3.up * height;
            if (!Physics.Raycast(from, run.WallDir, out RaycastHit hit, Climbing.FaceReach + 0.2f, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore)) return false;
            return Vector3.Angle(hit.normal, Vector3.up) >= Climbing.MinAngle;
        }

        // Floor under a body's width round the top point (a wall top a body can stand on, not a beam): the top found half a body
        // either side along the wall, at about the same height.
        private static bool WideEnough(Vector3 top, Vector3 wallDir, float radius, int mask)
        {
            Vector3 side = Vector3.Cross(Vector3.up, wallDir);
            foreach (Vector3 offset in new[] { side * radius, -side * radius, wallDir * radius * 0.8f })
            {
                Vector3 p = top + offset + Vector3.up * 0.6f;
                if (!Physics.Raycast(p, Vector3.down, out RaycastHit h, 1.0f, mask, QueryTriggerInteraction.Ignore)) return false;
                if (Mathf.Abs(h.point.y - top.y) > 0.3f) return false;
            }
            return true;
        }

        // On the face: gravity off, the slide held at 0, facing the wall, this velocity; no fall damage builds up from the climb itself.
        // 0.2.282: the facing goes on the transform as well as the rigidbody, after vanilla's UpdateRotation turned it toward the
        // driver's move / look this same step (HR5be: the drill walked on to its next point mid-climb), so ZSyncTransform sends the
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
                Quaternion facing = Quaternion.LookRotation(run.WallDir);
                body.transform.rotation = facing;
                rb.rotation = facing;
            }
            // 0.2.285 (HR5dp: a player's 90° climb made 2.5 m in 20 s, 0.12 m/s against 0.72): vanilla's UpdateBodyFriction gives the capsule
            // 0.4 (moving) / 0.8 (standing) friction whenever a face reads as ground, and the hug into the face dragged against it. On the
            // face the capsule slides: no friction this step.
            if (body.m_collider != null && body.m_collider.material != null)
            {
                body.m_collider.material.staticFriction = 0f;
                body.m_collider.material.dynamicFriction = 0f;
                body.m_collider.material.frictionCombine = PhysicsMaterialCombine.Minimum;
            }
            // 0.2.285 (HR5dc, Fire: "the bot floats up the wall" in the fall pose on the viewers' screens): off the ground and out of the
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
            Debug.Log($"[Climb] climb: {Name(body)} fell: {why} at {body.transform.position.y - run.StartY:0.0} m up {run.Surface}, {run.Charged:0} stamina for the climb, {Climbing.StaminaOf(body):0} left, " +
                      $"facing {OffFace(body, run):0}° off the face");
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
}
