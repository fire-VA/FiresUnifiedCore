using System;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1072 (lead / Fire 10-31: a SQUARE bare patch south of the DN shack x266-270 z795-800; "only what the numbers show"):
    /// core_snow_probe [x z] [half] [step] - for every point of a grid (default: the player's spot, half 3 m, step 1 m) one Info line with
    /// every factor Core puts into the snow depth there: DepthAt (m), the deep / Mountain / winter terms, base weight, path clear (Core's
    /// 4 m cells: smooth + raw), shelter, ground factor (paved, piece), melt, FAT zone ownership, and the TerrainComp paint at that texel
    /// (modified, r g b, the base mask G under it). Reads only; admin.
    /// </summary>
    internal static class SnowProbe
    {
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class SnowProbe_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("core_snow_probe",
                    "[FiresUnifiedCore] core_snow_probe [x z] [half] [step] - every factor of Core's snow depth on a grid (default: here, 3 m, 1 m)",
                    args =>
                    {
                        Vector3 c = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                        float half = 3f, step = 1f;
                        try
                        {
                            if (args.Length >= 3) { c.x = float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture); c.z = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture); }
                            if (args.Length >= 4) half = Mathf.Clamp(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), 0f, 16f);
                            if (args.Length >= 5) step = Mathf.Clamp(float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), 0.25f, 8f);
                        }
                        catch { args.Context?.AddString("usage: core_snow_probe [x z] [half] [step]"); return; }
                        int n = 0;
                        for (float dz = -half; dz <= half + 0.001f; dz += step)
                            for (float dx = -half; dx <= half + 0.001f; dx += step)
                            {
                                if (++n > 400) { Say(args.Context, "[SnowProbe] stopped at 400 points"); return; }
                                Say(args.Context, Line(new Vector3(c.x + dx, 0f, c.z + dz)));
                            }
                    });   // 0.2.1140 (Fire ran it twice, 0 lines in the log): as a cheat command it needs devcommands + vanilla's server check and is refused before it runs. It only reads, so anyone may run it.
            }
        }

        private static void Say(Terminal ctx, string s) { ctx?.AddString(s); FiresCore.Logging.FiresLogger.LogInfo(s); }

        internal static string Line(Vector3 p)
        {
            try
            {
                return SnowField.Unbudgeted(() =>
                {
                    float depth = SnowField.DepthAt(p) * SnowField.MaxDepthMetres;
                    float dn = SnowField.DeepGroundWeight(p), mw = SnowField.MountainWeight(p), wg = SnowField.WinterG(p) * SnowField.MaxDepthMetres;
                    float clearS = SnowPathMask.ClearAtSmooth(p), clear = SnowPathMask.ClearAt(p);
                    float shelter = SnowField.ShelterAt(p), ground = SnowGround.FactorAt(p), melt = SnowField.MeltAt(p);
                    var zone = ZoneSystem.GetZone(p);
                    bool fat = SnowFat.OwnsZone(zone);
                    string paint = "no TerrainComp";
                    string drawn = "no heightmap";   // 0.2.1125: what the chunk actually draws: its own paint texture and build data, with or without a TerrainComp
                    var hmDrawn = Heightmap.FindHeightmap(p);
                    if (hmDrawn != null && hmDrawn.m_paintMask != null)
                    {
                        hmDrawn.WorldToVertexMask(p, out int dx, out int dy);
                        drawn = $"drawn G {hmDrawn.m_paintMask.GetPixel(dx, dy).g:0.00}, build base G {SnowBaseMask.BaseG(hmDrawn, dy * (hmDrawn.m_width + 1) + dx):0.00}, chunk ({hmDrawn.transform.position.x:0},{hmDrawn.transform.position.z:0})";
                    }
                    var hm = Heightmap.FindHeightmap(p);
                    var tc = hm != null ? TerrainComp.FindTerrainCompiler(hm.transform.position) : null;
                    if (hm != null && tc != null && tc.m_paintMask != null && tc.m_modifiedPaint != null)
                    {
                        hm.WorldToVertexMask(p, out int x, out int y);
                        int i = y * (hm.m_width + 1) + x;
                        if (i >= 0 && i < tc.m_paintMask.Length)
                        {
                            var col = tc.m_paintMask[i];
                            paint = $"{(tc.m_modifiedPaint[i] ? (SnowRefill.TrailMarked(tc, i) ? "EDITED trail" : "EDITED unmarked") : "not edited")} r {col.r:0.00} g {col.g:0.00} b {col.b:0.00}, base G {SnowBaseMask.BaseG(hm, i):0.00}";
                        }
                    }
                    return $"[SnowProbe] ({p.x:0.0},{p.z:0.0}) zone ({zone.x},{zone.y}){(fat ? " FAT" : "")}: depth {depth:0.00} m | DN weight {dn:0.00}, Mountain {mw:0.00}, winter {wg:0.00} m, base weight {SnowField.BaseWeight(p):0.00} | path clear {clearS:0.00} (raw {clear:0.00}) | shelter {shelter:0.00} | ground {ground:0.00} | melt {melt:0.00} | paint {paint} | {drawn}";
                });
            }
            catch (Exception e) { return $"[SnowProbe] ({p.x:0.0},{p.z:0.0}): failed {e.Message}"; }
        }
    }
}
