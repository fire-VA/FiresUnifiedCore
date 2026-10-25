using System;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// Vanilla terrain: after Heightmap.ApplyModifiers has built the chunk's paint mask (world gen + TerrainComp player paint), raise
    /// the Deep North snow-depth channel (G) in this peer's LOCAL runtime texture by the dynamic snow, minus fires and the path mask,
    /// then Apply. Nothing is written to TerrainComp / TerrainModifier (saved, synced data), so there is no traffic or save churn and
    /// players' painted ground is never overwritten. Vanilla reads G as snow depth only in the Deep North (GetHeightOffset 0.1 to 2.1 m,
    /// the SnowDeep / SnowVeryDeep ground), so only Deep North cells are raised; elsewhere G means cultivated. Paved cells (B) stay
    /// clear. FAT voxel zones are skipped (FAT pulls SnowField.FillZoneDepth into its own texture).
    /// </summary>
    internal static class SnowHeightmapInject
    {
        // Vanilla: snow height = G x Character.m_deepSnowSlowMaxHeight (2 m); world gen gives Deep North G 0.3..0.6, so its deepest
        // is G 0.6 = 1.2 m, waist deep (Fire 10-24: full winter = waist deep = vanilla Deep North max). Full cover + full build-up = MaxG.
        private const float MaxG = 0.6f;

        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_paintMask = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");
        private static int s_logged;

        [HarmonyPatch(typeof(Heightmap), "ApplyModifiers")]
        private static class Heightmap_ApplyModifiers_Snow
        {
            private static void Postfix(Heightmap __instance)
            {
                try { Inject(__instance); }
                catch (Exception e) { if (s_logged++ < 3) FiresLogger.LogDebug($"[Snow] terrain inject skipped: {e.Message}"); }
            }
        }

        private static void Inject(Heightmap hm)
        {
            if (!SnowField.Enabled || (SnowField.GlobalCover <= 0f && SnowField.WinterCover <= 0f) || hm == null) return;
            Vector3 c = hm.transform.position;
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(c))) return;
            if (!AnyDeepNorth(hm)) return;
            var tex = s_paintMask(hm);
            if (tex == null) return;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels();
            int raised = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    var col = px[i];
                    if (col.b > 0.5f) continue;   // paved: a built road stays clear
                    Vector3 p = hm.VertexMaskToWorld(x, y);
                    if (WorldGenerator.instance.GetBiome(p) != Heightmap.Biome.DeepNorth) continue;
                    float dyn = SnowField.CoverageAt(p) * MaxG;   // already net of fires and paths
                    float keep = (1f - SnowField.MeltAt(p)) * (1f - SnowPathMask.ClearAt(p));
                    float g = Mathf.Clamp01(Mathf.Max(col.g * keep, dyn));
                    if (Mathf.Abs(g - col.g) < 0.004f) continue;
                    col.g = g;
                    px[i] = col;
                    raised++;
                }
            if (raised == 0) return;
            tex.SetPixels(px);
            tex.Apply();
        }

        private static bool AnyDeepNorth(Heightmap hm)
        {
            var wg = WorldGenerator.instance;
            float r = hm.m_width * hm.m_scale * 0.5f;
            Vector3 c = hm.transform.position;
            return wg.GetBiome(c) == Heightmap.Biome.DeepNorth
                || wg.GetBiome(c + new Vector3(-r, 0, -r)) == Heightmap.Biome.DeepNorth || wg.GetBiome(c + new Vector3(r, 0, -r)) == Heightmap.Biome.DeepNorth
                || wg.GetBiome(c + new Vector3(-r, 0, r)) == Heightmap.Biome.DeepNorth || wg.GetBiome(c + new Vector3(r, 0, r)) == Heightmap.Biome.DeepNorth;
        }

        /// <summary>Repaints (paint-only) the loaded vanilla chunks in the given zone so the inject reruns.</summary>
        internal static int Repaint(Vector2s zone)
        {
            int n = 0;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            var hm = Heightmap.FindHeightmap(c);
            if (hm != null && !hm.IsDistantLod) { hm.Poke(1, paintOnly: true); n++; }
            return n;
        }
    }
}
