using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Fire 10-27 (a player's sky build): a placed piece floating above the ground never changes the terrain under it.
    /// Vanilla applies every TerrainModifier by XZ only (Heightmap.ApplyModifiers -> TerrainVSModifier, Heightmap.cs:346-401): a rock
    /// placed 2000 m up still levels (to its own Y, clamped to base +-8 m for m_playerModifiction, Heightmap.cs:829-838), smooths and
    /// paints the ground straight below it. The guard skips the modifier (and a TerrainOp child, which would be SAVED into TerrainComp)
    /// when its owner is a Piece whose lowest point is more than <see cref="AboveGroundMargin"/> above the ground at its XZ.
    /// Ground = the world generator height plus the saved TerrainComp level/smooth delta there (never the modifier's own effect).
    /// World-gen locations and vegetation (no Piece) are untouched; a piece standing on the ground keeps vanilla behaviour.
    /// The decision is made once per instance and cached; no FAT type is referenced (works on plain vanilla heightmaps).
    /// Public API for other mods (e.g. a voxel terrain honouring the same rule): <see cref="IsSkipped"/>,
    /// <see cref="IsFloatingPiece"/>, <see cref="GroundHeight"/>, <see cref="Decided"/>.
    /// </summary>
    public static class TerrainGuard
    {
        private const string Tag = "[TerrainGuard]";

        /// <summary>A piece whose lowest point is more than this above the ground counts as floating.</summary>
        public const float AboveGroundMargin = 2f;

        /// <summary>Raised once per modifier / op instance when its decision is made: (owner, skipped, metres above ground).</summary>
        public static event Action<Component, bool, float> Decided;

        private static readonly Dictionary<int, bool> s_skipped = new Dictionary<int, bool>();

        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> s_hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> s_modifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> s_levelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> s_smoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");

        /// <summary>True when this modifier belongs to a floating placed piece and is not applied. Cached per instance.</summary>
        public static bool IsSkipped(TerrainModifier modifier)
        {
            if (modifier == null) return false;
            int id = modifier.GetInstanceID();
            if (s_skipped.TryGetValue(id, out bool skip)) return skip;
            skip = IsFloatingPiece(modifier, out float above);
            s_skipped[id] = skip;
            Report(modifier, skip, above, "terrain modifier");
            return skip;
        }

        /// <summary>
        /// True when <paramref name="part"/> belongs to a Piece whose lowest point is more than <see cref="AboveGroundMargin"/> above the
        /// ground at its XZ. False (above = 0) for anything without a Piece: world-gen locations, vegetation, tool-spawned ops.
        /// </summary>
        public static bool IsFloatingPiece(Component part, out float aboveGround)
        {
            aboveGround = 0f;
            if (part == null) return false;
            Piece piece = part.GetComponentInParent<Piece>();
            if (piece == null) return false;
            float lowest = part.transform.position.y;
            foreach (Collider c in piece.GetComponentsInChildren<Collider>())
                if (c != null && c.enabled && !c.isTrigger) lowest = Mathf.Min(lowest, c.bounds.min.y);
            if (!GroundHeight(part.transform.position, out float ground)) return false;
            aboveGround = lowest - ground;
            return aboveGround > AboveGroundMargin;
        }

        /// <summary>The ground height at <paramref name="pos"/>'s XZ without any TerrainModifier: generator height + saved TerrainComp delta.</summary>
        public static bool GroundHeight(Vector3 pos, out float height)
        {
            height = 0f;
            if (WorldGenerator.instance == null) return false;
            height = WorldGenerator.instance.GetHeight(pos.x, pos.z);
            try
            {
                TerrainComp tc = TerrainComp.FindTerrainCompiler(pos);
                Heightmap hm = tc != null ? s_hmap(tc) : null;
                if (hm == null) return true;
                hm.WorldToVertex(pos, out int x, out int y);
                int pitch = hm.m_width + 1;
                if (x < 0 || y < 0 || x >= pitch || y >= pitch) return true;
                int i = y * pitch + x;
                bool[] modified = s_modifiedHeight(tc);
                if (modified != null && i < modified.Length && modified[i]) height += s_levelDelta(tc)[i] + s_smoothDelta(tc)[i];
            }
            catch (Exception e) { FiresLogger.LogDebug($"{Tag} TerrainComp delta not read at ({pos.x:0},{pos.z:0}): {e.Message} (generator height used)"); }
            return true;
        }

        private static void Report(Component part, bool skipped, float above, string what)
        {
            Piece piece = part.GetComponentInParent<Piece>();
            if (piece != null)
            {
                Vector3 p = part.transform.position;
                string name = Utils.GetPrefabName(piece.gameObject);
                if (skipped) FiresLogger.LogDebug($"{Tag} {name} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}): {above:0.0} m above ground, {what} skipped");
                else FiresLogger.LogDebug($"{Tag} {name} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}): {above:0.0} m, at ground, {what} applied");
            }
            try { Decided?.Invoke(part, skipped, above); }
            catch (Exception e) { FiresLogger.LogDebug($"{Tag} a Decided listener threw: {e.Message}"); }
        }

        // 0.2.939 (Fire 10-27 "Yes, on islands too"; round 35: "[TerrainGuard] snow_walk at (672.5,145.7,529.9): 95.7 m above ground, terrain
        // op skipped" - vanilla's snow_walk prefab carries a Piece, so the floating-piece rule caught it on a sky island): an island top is
        // ground for the walk-trail / creature-trail / shovel ops. They pass when FAT (1155+, FatApi.TryIslandSurface, by reflection) says
        // their XZ is inside a sky-island footprint and their Y is within IslandMargin of the island top. Every other op / modifier, and
        // every player-built piece, keeps the sky-build guard unchanged; without FAT nothing changes.
        private const float IslandMargin = 3f;
        private delegate bool IslandSurfaceFn(float x, float z, out float y);
        private static IslandSurfaceFn s_island;
        private static bool s_islandTried;
        private static int s_islandKept;
        private static float s_islandLogAt;

        private static bool IsTrailOp(Component op)
        {
            string n = Utils.GetPrefabName(op.gameObject);
            return n.StartsWith("snow_walk", StringComparison.OrdinalIgnoreCase) || n.StartsWith("FAT_CreatureTrail_", StringComparison.Ordinal)
                || n.StartsWith("FAT_ShovelPatch_", StringComparison.Ordinal);
        }

        private static bool OnIslandTop(Component op, out float islandY)
        {
            islandY = 0f;
            if (op == null || !IsTrailOp(op)) return false;
            if (!s_islandTried)
            {
                s_islandTried = true;
                try
                {
                    var m = FiresCore.Compat.OptionalTypes.Find("VerdantsAscent.FatApi")?.GetMethod("TryIslandSurface", new[] { typeof(float), typeof(float), typeof(float).MakeByRefType() });
                    if (m != null) s_island = (IslandSurfaceFn)Delegate.CreateDelegate(typeof(IslandSurfaceFn), m);
                    FiresLogger.LogDebug($"{Tag} sky-island trail rule: FatApi.TryIslandSurface {(s_island != null ? "bound (snow / creature trails and shovel ops on an island top count as ground)" : "not found (FAT older than 1155 or absent): the floating rule stays for every op")}");
                }
                catch (Exception e) { FiresLogger.LogWarning($"{Tag} FatApi.TryIslandSurface bind failed: {e.Message}"); }
            }
            if (s_island == null) return false;
            Vector3 p = op.transform.position;
            bool inside;
            try { inside = s_island(p.x, p.z, out islandY); } catch { return false; }
            if (!inside || Mathf.Abs(p.y - islandY) > IslandMargin) return false;
            s_islandKept++;
            if (Time.unscaledTime >= s_islandLogAt)
            {
                s_islandLogAt = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"{Tag} {Utils.GetPrefabName(op.gameObject)} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}): on a sky island top (y {islandY:0.0}), applied as ground ({s_islandKept} this session)");
            }
            return true;
        }

        // Heightmap.ApplyModifier is where one modifier's level / smooth / paint happen (Heightmap.cs:376-385).
        [HarmonyPatch(typeof(Heightmap), "ApplyModifier")]
        private static class Heightmap_ApplyModifier_TerrainGuard
        {
            private static bool Prefix(TerrainModifier modifier) => !IsSkipped(modifier);
        }

        [HarmonyPatch(typeof(TerrainModifier), "OnDestroy")]
        private static class TerrainModifier_OnDestroy_TerrainGuard
        {
            private static void Postfix(TerrainModifier __instance) => s_skipped.Remove(__instance.GetInstanceID());
        }

        // A TerrainOp writes into the SAVED TerrainComp on Awake (TerrainOp.cs Awake). One under a floating piece is dropped, not applied;
        // the piece itself stays (vanilla would destroy the op's GameObject).
        [HarmonyPatch(typeof(TerrainOp), "Awake")]
        private static class TerrainOp_Awake_TerrainGuard
        {
            private static bool Prefix(TerrainOp __instance)
            {
                if (TerrainOp.m_forceDisableTerrainOps || !IsFloatingPiece(__instance, out float above)) return true;
                if (OnIslandTop(__instance, out float island)) return true;   // 0.2.939: a snow trail / shovel op on a FAT sky island top
                Report(__instance, true, above, "terrain op");
                UnityEngine.Object.Destroy(__instance);
                return false;
            }
        }
    }
}
