using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;

namespace FiresCore.Snow
{
    /// <summary>
    /// Creature snow trails on vanilla HEIGHTMAP ground (Fire 10-27: "Creatures should make paths in the snow as well... deer or boar or
    /// humanoid skeletons carve out paths as they walk through the snow in the loaded area"; "the path they carve out obviously
    /// determined by their actual size"; "small animals like hares making a 0.9 m path is fine").
    /// Vanilla only gives the Player prefab an m_deepSnowWalkObj (Character.UpdateWalking, Character.cs:1694-1706), so creatures leave
    /// no trail. This mirrors vanilla's own condition for every other walking Character - Deep North, on the heightmap, cultivation
    /// mask x m_deepSnowSlowMaxHeight &gt; m_deepSnowSlowStartHeight (or Core's painted deep snow) - and spawns a copy of the player's
    /// snow_walk op whose paint radius is the creature's size: width = max(0.9, 2 x capsule radius x lossyScale), capped at 6 m,
    /// snapped to 15 widths. A terrain op travels by PREFAB NAME only (TerrainOp.Settings.Serialize), so each width is its own op
    /// prefab "FAT_CreatureTrail_&lt;cm&gt;", registered on every peer - the SAME names and radii as FAT 0.2.1129's FatCreatureTrail, so
    /// both mods agree (registered only when missing). Zones FAT owns are left to FAT (its voxel snow draws the trail there).
    /// Written by the character's ZDO owner only, one op per 1 m moved, at most 30 ops/s overall. Config "Creature snow trails"
    /// (default OFF). Core's snow path mask skips these ops like vanilla's walk trail (SnowPathMask calls IsCreatureTrail).
    /// </summary>
    public static class CreatureSnowTrail
    {
        public const float MinWidth = 0.9f, MaxWidth = 6f;
        public const float RadiusTag = 0.007f;   // FAT's shovel patch uses 0.003: op radius = width / 2 + tag tells them apart
        private const float Spacing = 1.5f, OpsPerSecond = 6f, AlreadyCutG = 0.02f;   /* 0.2.1166 (Fire: "companions and enemies are still not making snow trails"; log: they do stamp): at 3 m a 1.25 m trench leaves separate shallow dimples */   // 0.2.1136: as FAT 0.2.1182 after the RPC census (was 1 m, 30 a second)
        private const string Tag = "[SnowTrail] ";

        private static readonly AccessTools.FieldRef<Character, ZNetView> NView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Character, Heightmap> LastHeightmap = AccessTools.FieldRefAccess<Character, Heightmap>("m_lastHeightmap");
        private static readonly AccessTools.FieldRef<Character, Heightmap.Biome> LastBiome = AccessTools.FieldRefAccess<Character, Heightmap.Biome>("m_lastBiome");

        // ---- config (bound lazily from Core's file, like SnowConfig; new behaviour ships OFF)

        public static bool On => SnowConfig.P("Creature snow trails", false);   // 1086 ([generator], Fire 11-01: configs live in their own mods): owner FiresAdminTerrain

        // ---- widths: 0.9, 1.0, 1.2 .. 2.0 by 0.2, 2.5 .. 6.0 by 0.5 (FAT's FatCreatureTrail.Widths)
        public static readonly float[] Widths = BuildWidths();
        private static float[] BuildWidths()
        {
            var w = new List<float> { 0.9f, 1.0f };
            for (float x = 1.2f; x <= 2.001f; x += 0.2f) w.Add(Mathf.Round(x * 10f) / 10f);
            for (float x = 2.5f; x <= 6.001f; x += 0.5f) w.Add(x);
            return w.ToArray();
        }
        public static string OpName(float width) => "FAT_CreatureTrail_" + Mathf.RoundToInt(width * 100f).ToString("000");
        public static float RadiusFor(float width) => width * 0.5f + RadiusTag;
        public static float Snap(float width)
        {
            float best = Widths[0];
            foreach (float w in Widths) if (Mathf.Abs(w - width) < Mathf.Abs(best - width)) best = w;
            return best;
        }

        /// <summary>2 x collider radius x lossyScale (capsule; else the collider's XZ extents).</summary>
        public static float ComputedWidth(GameObject go)
        {
            if (go == null) return 0f;
            var cap = go.GetComponent<CapsuleCollider>();
            Vector3 s = go.transform.lossyScale;
            float sc = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            if (cap != null) return 2f * cap.radius * sc;
            var col = go.GetComponent<Collider>();
            return col != null ? 2f * Mathf.Max(col.bounds.extents.x, col.bounds.extents.z) : 0f;
        }
        public static float AppliedWidth(float computed) => Mathf.Clamp(Mathf.Max(MinWidth, computed), MinWidth, MaxWidth);

        /// <summary>True when an op's settings are one of the creature trail widths (paint radius = width / 2 + tag).</summary>
        public static bool IsCreatureTrail(TerrainOp.Settings m)
        {
            if (m == null || m.m_paintType != TerrainModifier.PaintType.Cultivate || m.m_level || m.m_raise || m.m_smooth) return false;
            float w = (m.m_paintRadius - RadiusTag) * 2f;
            foreach (float x in Widths) if (Mathf.Abs(x - w) <= 0.002f) return true;
            return false;
        }

        // ---- op prefabs, on every peer (UpdateRegisters clears the table, so they are put back after it and before each received op)
        private static readonly Dictionary<int, TerrainOp> s_tpl = new Dictionary<int, TerrainOp>();
        private static Transform s_root;
        private static bool s_said;

        public static bool EnsureTemplates()
        {
            var db = ObjectDB.instance;
            var scene = ZNetScene.instance;
            if (db == null || scene == null) return false;
            var pl = scene.GetPrefab("Player");
            var walk = pl != null ? pl.GetComponent<Player>()?.m_deepSnowWalkObj : null;
            if (walk == null || walk.GetComponentInChildren<TerrainOp>(true) == null) return false;
            int added = 0, kept = 0;
            foreach (float w in Widths)
            {
                string name = OpName(w);
                int hash = name.GetStableHashCode();
                if (db.m_terrainOpsByHash.TryGetValue(hash, out var have) && have != null) { kept++; continue; }   // FAT (or us) already did
                if (!s_tpl.TryGetValue(hash, out var op) || op == null)
                {
                    if (s_root == null)
                    {
                        var h = new GameObject("Core_CreatureTrailOps");
                        h.SetActive(false);   // the copies never Awake (TerrainOp.Awake applies and destroys itself)
                        Object.DontDestroyOnLoad(h);
                        s_root = h.transform;
                    }
                    var go = Object.Instantiate(walk, s_root);
                    go.name = name;
                    op = go.GetComponentInChildren<TerrainOp>(true);
                    op.m_settings.m_paintRadius = RadiusFor(w);
                    s_tpl[hash] = op;
                }
                db.m_terrainOpsByHash[hash] = op;
                added++;
            }
            if (!s_said)
            {
                s_said = true;
                FiresLogger.LogDebug($"{Tag}creature trail op prefabs on {Peer()}: {added} registered by Core, {kept} already present (FAT) - {Widths.Length} widths {MinWidth:0.0}-{MaxWidth:0.0} m ({OpName(MinWidth)} .. {OpName(MaxWidth)}, paint r = width / 2 + {RadiusTag})");
            }
            return true;
        }

        private static string Peer()
            => ZNet.instance == null ? "no ZNet yet" : ZNet.instance.IsServer() ? (ZNet.instance.IsDedicated() ? "dedicated server" : "host") : "client";

        private static TerrainOp Template(float width)
        {
            var db = ObjectDB.instance;
            int hash = OpName(width).GetStableHashCode();
            if (db != null && db.m_terrainOpsByHash.TryGetValue(hash, out var op) && op != null) return op;
            return EnsureTemplates() && db.m_terrainOpsByHash.TryGetValue(hash, out op) ? op : null;
        }

        [HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
        private static class CreatureSnowTrail_Registers { private static void Postfix() { try { if (On) EnsureTemplates(); } catch { } } }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class CreatureSnowTrail_SceneAwake { private static void Postfix() { try { if (On) EnsureTemplates(); } catch { } } }

        // A received op is looked up by its name hash: make sure the set is there first (the config may be off on this peer).
        [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
        private static class CreatureSnowTrail_ReceivedOp { [HarmonyPriority(Priority.First)] private static void Prefix() { try { EnsureTemplates(); } catch { } } }

        // Core's snow path mask skips vanilla's walk trail (0.2.830); a creature trail is the same kind of op, so it skips these too.
        // 0.2.904 freeze ([wishbone]): SnowPathMask calls IsCreatureTrail directly next to IsWalkTrail (was a Harmony postfix on that
        // small private static, which the JIT may inline so the postfix never runs).

        // ---- the trigger: vanilla's player condition, for every other walking Character
        private sealed class Last { public Vector3 Pos = new Vector3(float.MaxValue, 0f, 0f); }
        private static readonly ConditionalWeakTable<Character, Last> s_last = new ConditionalWeakTable<Character, Last>();
        private static float s_tokens = OpsPerSecond, s_tokenAt, s_sumAt;
        private static int s_writes, s_capped, s_bossCaps, s_alreadyCut, s_nearPlayer;
        private const float NearPlayer = 2.5f;
        private static readonly HashSet<int> s_creatures = new HashSet<int>();
        private static readonly Dictionary<string, float> s_typeWidth = new Dictionary<string, float>();
        public static int Sent;

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class CreatureSnowTrail_Walking
        {
            private static void Postfix(Character __instance)
            {
                try { if (!(__instance is Player)) Tick(__instance); } catch { }
            }
        }

        private static void Tick(Character c)
        {
            if (!On) return;
            if (SnowField.GlobalCover <= 0f && SnowField.WinterCover <= 0f) return;   // 0.2.950 (perf, round 45): no snow anywhere, nothing to trail
            Summary();
            if (c.IsDead() || c.IsFlying() || c.IsSwimming() || c.InWater() || !c.IsOnGround()) return;
            var nv = NView(c);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            Vector3 p = c.transform.position;
            var last = s_last.GetOrCreateValue(c);
            if ((last.Pos - p).sqrMagnitude < Spacing * Spacing) return;
            // 0.2.1206 (lead 11-08, same as FAT 0.2.1418): a companion / tame walking WITH a player stamps onto the player's own path and
            // makes the trail fat; any step within 2.5 m of a player is skipped (counted at Debug). Creatures away from players keep their trails.
            if (Player.IsPlayerInRange(p, NearPlayer)) { last.Pos = p; s_nearPlayer++; return; }
            if (!DeepSnowHere(c, p)) { last.Pos = p; return; }
            // 0.2.1136: ground already dug needs no op (each one is a networked paint + a chunk regenerate); raw paint, GetCultivationMask decodes
            var dug = LastHeightmap(c);
            if (dug != null && dug.m_paintMask != null) { dug.WorldToVertexMask(p, out int vx, out int vy); if (dug.m_paintMask.GetPixel(vx, vy).g < AlreadyCutG) { last.Pos = p; s_alreadyCut++; return; } }
            float now = Time.unscaledTime;
            s_tokens = Mathf.Min(OpsPerSecond, s_tokens + (now - s_tokenAt) * OpsPerSecond); s_tokenAt = now;
            if (s_tokens < 1f) { s_capped++; return; }   // keep last.Pos: it tries again next step
            float computed = ComputedWidth(c.gameObject);
            if (computed > MaxWidth) s_bossCaps++;
            float w = Snap(AppliedWidth(computed));
            var tpl = Template(w);
            if (tpl == null) return;
            s_tokens -= 1f;
            last.Pos = p;
            Object.Instantiate(tpl.gameObject, p, Quaternion.identity);   // active at the root: TerrainOp.Awake sends it by its name
            Sent++; s_writes++;
            s_creatures.Add(c.GetInstanceID());
            s_typeWidth[Utils.GetPrefabName(c.gameObject)] = w;
        }

        /// <summary>Vanilla's deep-snow walk condition (Character.cs:1694-1703) on heightmap ground, minus the FAT-owned zones.</summary>
        public static bool DeepSnowHere(Character c, Vector3 p)
        {
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(p))) return false;   // FAT's voxel snow draws trails there
            if (!c.IsOnHeightmap()) return false;
            float start = c.m_deepSnowSlowStartHeight > 0f ? c.m_deepSnowSlowStartHeight : 0.3f;
            float maxH = c.m_deepSnowSlowMaxHeight > 0f ? c.m_deepSnowSlowMaxHeight : 2f;
            var hm = LastHeightmap(c);
            bool deepNorth = LastBiome(c) == Heightmap.Biome.DeepNorth;
            if (deepNorth && hm != null && hm.GetCultivationMask(p) * maxH > start) return true;
            // Core's painted deep snow (SnowGroundAsDeepNorth: only the local player is switched to DeepNorth there)
            // 0.2.1136 (Fire: "companions dont leave snow trails", winter Meadows at x 1597 z 358): seasonal winter ground counts as well
            return (SnowField.DeepGroundAt(p) || SnowField.WinterGroundAt(p) || SnowField.MountainWeight(p) > 0f) && SnowField.DepthAt(p) * maxH > start;   // 0.2.914: Mountain snow takes creature trails too
        }

        private static void Summary()
        {
            if (Time.unscaledTime < s_sumAt) return;
            s_sumAt = Time.unscaledTime + 5f;
            if (s_alreadyCut > 0) { FiresLogger.LogDebug($"{Tag}creature trails (Core): {s_alreadyCut} step(s) on ground already dug, no op"); s_alreadyCut = 0; }
            if (s_nearPlayer > 0) { FiresLogger.LogDebug($"{Tag}creature trails (Core): {s_nearPlayer} step(s) within {NearPlayer:0.0} m of a player skipped (the player's own trail)"); s_nearPlayer = 0; }
            if (s_writes > 0 || s_capped > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_typeWidth) sb.Append(sb.Length > 0 ? ", " : "").Append($"{kv.Key} {kv.Value:0.0} m");
                FiresLogger.LogDebug($"{Tag}creature trails (Core): {s_writes} writes from {s_creatures.Count} creatures, capped {s_capped}{(s_bossCaps > 0 ? $", {s_bossCaps} op(s) capped at {MaxWidth:0} m (giants)" : "")} ({sb})");
            }
            s_writes = s_capped = s_bossCaps = 0; s_creatures.Clear(); s_typeWidth.Clear();
        }
    }
}
