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
        /// <summary>Stamina per second on a 38° face; steeper faces cost more (<see cref="Climbing.CostAt"/>).</summary>
        public float StaminaPerSecond;
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
    public static class Climbing
    {
        /// <summary>Below this a face is walked (and a player slides above it: Character.GetSlideAngle 38).</summary>
        public const float MinAngle = 38f;
        public const float BaseSpeed = 1.2f, BaseStaminaPerSecond = 12f, BaseMaxAngle = 90f, BaseWetSlipChance = 0.15f;
        /// <summary>At 90° the speed is this share of the 38° speed and the cost this multiple (Fire: steeper = slower and costlier).</summary>
        public const float VerticalSpeedShare = 0.6f, VerticalCostFactor = 1.5f;
        /// <summary>A climb doesn't start under this much stamina (players).</summary>
        public const float StartStamina = 15f;
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

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_pieces = config.Bind("Climbing", "Climb building pieces", true,
                "SERVER. Players, bots and companions may climb player-built walls (38-90 deg) as well as terrain and rock. Off: terrain and rock only (a PvP / raid lever).");
            s_playerInput = config.Bind("Climbing", "Player climb input", true,
                "Players start a climb only on a deliberate input: holding Jump while moving into a climbable face. Off: players never climb (bots and companions still climb planned routes).");
            configSync?.AddConfigEntry(s_pieces);
        }

        public static bool PiecesClimbable => s_pieces == null || s_pieces.Value;
        public static bool PlayerInputClimbs => s_playerInput == null || s_playerInput.Value;

        public static void AddModifier(IClimbModifier modifier) { if (modifier != null && !s_modifiers.Contains(modifier)) s_modifiers.Add(modifier); }
        public static void RemoveModifier(IClimbModifier modifier) => s_modifiers.Remove(modifier);

        /// <summary>Core's base terms plus every registered modifier (a throwing modifier is skipped).</summary>
        public static ClimbTerms TermsFor(Character body)
        {
            var terms = new ClimbTerms { StaminaPerSecond = BaseStaminaPerSecond, Speed = BaseSpeed, MaxAngle = BaseMaxAngle, WetSlipChance = BaseWetSlipChance, Allowed = true };
            foreach (IClimbModifier m in s_modifiers)
            {
                try { m.Modify(body, ref terms); }
                catch (Exception ex) { Debug.LogWarning($"[Climb] a climb modifier threw: {ex.Message}"); }
            }
            terms.MaxAngle = Mathf.Clamp(terms.MaxAngle, MinAngle, 90f);
            return terms;
        }

        private static float Steepness(float angle) => Mathf.InverseLerp(MinAngle, 90f, angle);
        public static float SpeedAt(ClimbTerms terms, float angle) => terms.Speed * Mathf.Lerp(1f, VerticalSpeedShare, Steepness(angle));
        public static float CostAt(ClimbTerms terms, float angle) => terms.StaminaPerSecond * Mathf.Lerp(1f, VerticalCostFactor, Steepness(angle));

        /// <summary>The stamina a climb of <paramref name="height"/> m up a face of <paramref name="angle"/>° costs this body: surface length / speed × cost.</summary>
        public static float StaminaFor(Character body, float height, float angle)
        {
            ClimbTerms terms = TermsFor(body);
            float surface = height / Mathf.Max(0.2f, Mathf.Sin(Mathf.Clamp(angle, MinAngle, 90f) * Mathf.Deg2Rad));
            return surface / Mathf.Max(0.1f, SpeedAt(terms, angle)) * CostAt(terms, angle);
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
                if (a < MinAngle || a > max + 0.5f) { angle = a; continue; }
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
            if (body == null) { why = "no body"; return false; }
            ClimbTerms terms = TermsFor(body);
            if (!terms.Allowed) { why = "climbing is not allowed for this body"; return false; }
            Vector3 flat = goal - from;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist > PlanRange) { why = $"the goal is {dist:0} m off (a climb plans {PlanRange:0} m at most)"; return false; }
            if (goal.y - from.y < 1.5f) { why = "the goal is not above"; return false; }
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
    }

    /// <summary>
    /// The climb state for any body (Tools\CLIMBING.md §3-§4), run on its owner in a postfix on Character.UpdateMotion (after vanilla's
    /// motion, which sets gravity and the slide every tick): Idle, Engage (0.4 s on the face), Climb (along the face's steepest way up at
    /// <see cref="Climbing.SpeedAt"/>, gravity off, the slide held at 0, stamina at <see cref="Climbing.CostAt"/>), TopOut (over the edge
    /// onto the ledge, 0.8 s), Fall (stamina out, a hit, the face lost or past the body's angle: vanilla physics again). The state and the
    /// clip go into the body's ZDO for every peer.
    /// </summary>
    public static class Climb
    {
        public enum State { Idle = 0, Engage = 1, Climb = 2, TopOut = 3, Fall = 4 }

        private const float EngageSeconds = 0.4f, TopOutSeconds = 0.8f, FallShowSeconds = 0.6f, LostFaceSeconds = 0.3f, LetGoSeconds = 0.3f, HugSpeed = 0.5f;

        private sealed class Run
        {
            public State State;
            public float Since;
            public Vector3 Normal, WallDir;
            public float Angle;
            public float StartY, StaminaAtStart, StartedAt;
            public float LostFaceSince = -1f, LetGoSince = -1f, NextStep, SlipUntil = -1f;
            public Vector3 TopFrom, TopTo;
            public bool Planned;
            public string Surface, Reason;
            public ClimbClip Clip;
        }

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

        /// <summary>Toward the face (flat) while climbing, for a driver's move / look; zero when not climbing.</summary>
        public static Vector3 FaceDirection(Character body) => body != null && s_runs.TryGetValue(body, out Run run) ? run.WallDir : Vector3.zero;

        /// <summary>Ends <paramref name="body"/>'s climb now (its driver gave it up): it falls back to vanilla physics.</summary>
        public static void Stop(Character body, string why)
        {
            if (body != null && s_runs.TryGetValue(body, out Run run) && run.State != State.Fall && run.State != State.Idle) ToFall(body, run, why);
        }

        private static bool TryBegin(Character body, RaycastHit face, float angle, string surface, bool planned, string reason, Vector3 goal, float height)
        {
            if (body.IsDead() || body.InAttack() || body.InDodge() || body.IsAttached()) return false;
            ClimbTerms terms = Climbing.TermsFor(body);
            if (!terms.Allowed) return false;
            if (body is Player && Climbing.StaminaOf(body) < Climbing.StartStamina) return false;
            Vector3 wall = -face.normal;
            wall.y = 0f;
            if (wall.sqrMagnitude < 0.0001f) return false;
            var run = new Run
            {
                State = State.Engage, Since = Time.time, StartedAt = Time.time, Normal = face.normal, WallDir = wall.normalized, Angle = angle,
                StartY = body.transform.position.y, StaminaAtStart = Climbing.StaminaOf(body), Planned = planned, Surface = surface, Reason = reason,
                NextStep = Time.time + Climbing.StepSeconds,
            };
            s_runs[body] = run;
            SetZdo(body, run, ClimbClip.Enter);
            Debug.Log($"[Climb] climb: {Name(body)} up {surface} {angle:0}° {(height > 0f ? $"{height:0.0} m " : "")}to reach ({goal.x:0}, {goal.y:0}, {goal.z:0}) " +
                      $"({(planned ? "planned" : "player input")}: {reason}); stamina {Climbing.StaminaOf(body):0}");
            return true;
        }

        [HarmonyPatch(typeof(Character), "UpdateMotion")]
        private static class Character_UpdateMotion_Climb
        {
            private static void Postfix(Character __instance, float dt)
            {
                try
                {
                    if (s_runs.Count == 0 && s_tickets.Count == 0 && __instance != Player.m_localPlayer) return;
                    if (s_runs.TryGetValue(__instance, out Run run)) { Step(__instance, run, dt); return; }
                    if (s_tickets.Count > 0 && TicketFor(__instance, out Climbing.ClimbPlan plan)) { TryPlanned(__instance, plan); return; }
                    if (__instance == Player.m_localPlayer) TryPlayerInput((Player)__instance);
                }
                catch (Exception ex) { Debug.LogWarning($"[Climb] {Name(__instance)}: {ex.Message}"); }
            }
        }

        // A planned climb starts once the body is at the plan's face.
        private static void TryPlanned(Character body, Climbing.ClimbPlan plan)
        {
            Vector3 toTop = plan.Top - body.transform.position;
            toTop.y = 0f;
            if (!Climbing.FaceAhead(body, toTop, out RaycastHit face, out float angle, out string surface)) return;
            s_tickets.TryGetValue(body, out Ticket ticket);
            if (TryBegin(body, face, angle, surface, true, ticket?.Reason ?? "planned", plan.Goal, plan.Height)) s_tickets.Remove(body);
        }

        // A player's deliberate start: Jump held on the real keyboard / pad while moving into a climbable face (never by pushing into
        // a wall or sliding alone; a bot's virtual presses don't reach ZInput).
        private static void TryPlayerInput(Player player)
        {
            if (!Climbing.PlayerInputClimbs || player.m_moveDir.sqrMagnitude < 0.01f) return;
            if (!ZInput.GetButton("Jump") && !ZInput.GetButton("JoyJump")) return;
            Vector3 forward = player.transform.forward;
            if (!Climbing.FaceAhead(player, forward, out RaycastHit face, out float angle, out string surface)) return;
            Vector3 into = -face.normal;
            into.y = 0f;
            if (into.sqrMagnitude < 0.0001f || Vector3.Dot(forward, into.normalized) < 0.5f) return;
            TryBegin(player, face, angle, surface, false, "forward + jump against the face", face.point, 0f);
        }

        private static void Step(Character body, Run run, float dt)
        {
            if (body.IsDead()) { End(body, run, "dead"); return; }
            Rigidbody rb = body.m_body;
            switch (run.State)
            {
                case State.Engage:
                    Hold(body, rb, run, Vector3.zero);
                    if (Time.time - run.Since >= EngageSeconds) { run.State = State.Climb; run.Since = Time.time; SetZdo(body, run, ClimbClip.UpLoop); }
                    return;
                case State.TopOut:
                {
                    float k = Mathf.Clamp01((Time.time - run.Since) / TopOutSeconds);
                    Vector3 target = k < 0.5f
                        ? new Vector3(run.TopFrom.x, Mathf.Lerp(run.TopFrom.y, run.TopTo.y, k * 2f), run.TopFrom.z)
                        : Vector3.Lerp(new Vector3(run.TopFrom.x, run.TopTo.y, run.TopFrom.z), run.TopTo, (k - 0.5f) * 2f);
                    Hold(body, rb, run, Vector3.zero);
                    rb.MovePosition(target);
                    if (k >= 1f)
                    {
                        float climbed = run.TopTo.y - run.StartY;
                        Debug.Log($"[Climb] climb: {Name(body)} topped out after {climbed:0.0} m up {run.Surface} in {Time.time - run.StartedAt:0.0} s, " +
                                  $"{run.StaminaAtStart - Climbing.StaminaOf(body):0} stamina ({run.Reason})");
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
            if (body is Player p && p.GetStamina() <= 0f) { ToFall(body, run, "stamina ran out"); return; }
            bool hitNow = FiresCore.Npc.Combat.FireScare.HitLately(body, 0.35f, out Character hitter, out _);
            if (body.IsStaggering() || hitNow)
            {
                ToFall(body, run, $"hit{(hitter != null ? $" by {hitter.m_name}" : "")}");
                return;
            }
            // A player lets go by letting go of forward (> 0.3 s); a planned climb holds on until the top or its driver stops it.
            if (!run.Planned)
            {
                if (body.m_moveDir.sqrMagnitude < 0.01f) { if (run.LetGoSince < 0f) run.LetGoSince = Time.time; }
                else run.LetGoSince = -1f;
                if (run.LetGoSince >= 0f && Time.time - run.LetGoSince > LetGoSeconds) { ToFall(body, run, "let go"); return; }
            }
            if (Climbing.FaceAhead(body, run.WallDir, out RaycastHit face, out float angle, out string surface))
            {
                run.LostFaceSince = -1f;
                run.Normal = face.normal;
                Vector3 wall = -face.normal;
                wall.y = 0f;
                if (wall.sqrMagnitude > 0.0001f) run.WallDir = wall.normalized;
                run.Angle = angle;
            }
            else
            {
                if (Ledge(body, run, out Vector3 ledge))
                {
                    run.State = State.TopOut;
                    run.Since = Time.time;
                    run.TopFrom = body.transform.position;
                    run.TopTo = ledge;
                    SetZdo(body, run, ledge.y - body.transform.position.y > 1.2f ? ClimbClip.Ledge : ClimbClip.Up1m);
                    return;
                }
                // The face eased below the slide angle ahead (a slope's top): walking again.
                if (angle > 0f && angle < Climbing.MinAngle) { End(body, run, null); return; }
                if (run.LostFaceSince < 0f) run.LostFaceSince = Time.time;
                if (Time.time - run.LostFaceSince > LostFaceSeconds) { ToFall(body, run, $"lost the face{(angle > 0f ? $" ({angle:0}°)" : "")}"); return; }
            }
            // Up the face's steepest way, hugging it.
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, run.Normal);
            if (up.sqrMagnitude < 0.0001f) up = Vector3.up;
            up.Normalize();
            float speed = Climbing.SpeedAt(terms, run.Angle);
            if (Time.time < run.SlipUntil) speed = 0f;
            Hold(body, rb, run, up * speed - run.Normal * HugSpeed);
            body.UseStamina(Climbing.CostAt(terms, run.Angle) * dt);
            // Wet: each step may slip 0.25 m (Fire: "a chance to slip down half a meter periodically").
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
            if (run.Clip == ClimbClip.DownLoop && Time.time >= run.SlipUntil) SetZdo(body, run, ClimbClip.UpLoop);
        }

        // The ledge over the edge: chest height has no face ahead, and 0.8 m on, a floor (under the slide angle) within 2.2 m above the feet.
        private static bool Ledge(Character body, Run run, out Vector3 ledge)
        {
            ledge = Vector3.zero;
            Vector3 at = body.transform.position;
            Vector3 probe = at + run.WallDir * 0.8f + Vector3.up * 2.4f;
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3.2f, FiresCore.World.Surface.Mask, QueryTriggerInteraction.Ignore)) return false;
            if (Vector3.Angle(hit.normal, Vector3.up) >= Climbing.MinAngle) return false;
            float rise = hit.point.y - at.y;
            if (rise < -0.2f || rise > 2.2f) return false;
            ledge = hit.point + run.WallDir * 0.3f + Vector3.up * 0.05f;
            return true;
        }

        // On the face: gravity off, the slide held at 0, facing the wall, this velocity; no fall damage builds up from the climb itself.
        private static void Hold(Character body, Rigidbody rb, Run run, Vector3 velocity)
        {
            rb.useGravity = false;
            rb.linearVelocity = velocity;
            body.m_slippage = 0f;
            body.m_sliding = false;
            body.m_maxAirAltitude = body.transform.position.y;
            if (run.WallDir.sqrMagnitude > 0.0001f) rb.rotation = Quaternion.LookRotation(run.WallDir);
        }

        private static void ToFall(Character body, Run run, string why)
        {
            run.State = State.Fall;
            run.Since = Time.time;
            body.m_maxAirAltitude = body.transform.position.y;   // the fall counts from here
            SetZdo(body, run, ClimbClip.Exit);
            Debug.Log($"[Climb] climb: {Name(body)} fell: {why} at {body.transform.position.y - run.StartY:0.0} m up {run.Surface}, stamina {Climbing.StaminaOf(body):0}");
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
