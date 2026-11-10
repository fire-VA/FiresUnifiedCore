using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>[Snow] settings, owned by FiresAdminTerrain and read here.</summary>
    internal static class SnowConfig
    {

        // 0.2.1037 (Fire 10-30, plan approved: the [Snow] settings move to FAT and FiresSnowmen): every [Snow] read goes through here -
        // the owner's value (CoreSettings: FiresSnowmen's 7 / FAT's) when one provides it, else Core's own entry (its current value, so
        // nothing changes until an owner takes over). Read lazily at use (Snowmen provides at Start, after every Awake) and kept 1 s, since
        // the snow ground rule asks per sample.
        // 1086 ([generator], Fire 11-01: configs live in their own mods): FiresAdminTerrain provides [Snow] with a getter: re-read on every use, no cache; own = the old default
        // 0.2.1099 (lead, [worldgen]'s stall read: 1086 dropped the cache, and the snow rules ask per sample): each key kept 1 s again, dropped at once on a
        // CoreSettings change. FAT provides a plain getter, which raises no change, hence the 1 s as well. TickCount + lock: worker threads read these too.
        private static readonly System.Collections.Generic.Dictionary<string, (int at, object v)> s_owned = InitOwned();
        private static System.Collections.Generic.Dictionary<string, (int at, object v)> InitOwned()
        {
            var d = new System.Collections.Generic.Dictionary<string, (int at, object v)>();
            FiresCore.Config.CoreSettings.Changed += (section, key) => { if (section == "Snow") lock (d) d.Clear(); };
            return d;
        }
        internal static T P<T>(string key, T own)
        {
            int now = System.Environment.TickCount;
            lock (s_owned)
                if (s_owned.TryGetValue(key, out var c) && now - c.at < 1000 && c.v is T cached) return cached;
            T v = FiresCore.Config.CoreSettings.Get("Snow", key, own);
            lock (s_owned) s_owned[key] = (now, v);
            return v;
        }

        internal static bool On => P("Dynamic snow", false);

        // 0.2.853 ([seasons], Fire 10-27 "Yes, grow in snowfall"): x N on the ground snow build-up rate, for testing (1 = natural)
        internal static float GroundFillRate { get { return Mathf.Clamp(P("Ground snow fill rate",  1f), 1f, 100f); } }

        // 0.2.914 (Fire 10-27: "Mountain biomes should get snow build up as well, just not to the extent that deep north does... a lower Max depth")
        /// <summary>Deepest Mountain snow in metres (Deep North's cap is 1.9 m); 0 = no Mountain buildup.</summary>
        internal static float MountainMaxDepth { get { return Mathf.Clamp(P("Max depth Mountain (m)",  0.8f), 0f, 1.9f); } }

        // 0.2.927 (Fire 10-27 "if the room is closed off no snow"): the shelter mask (SnowShelter), server-decided, ships OFF
        internal static bool ShelterOn { get { return P("No snow in closed rooms", false); } }

        // 0.2.935 (Fire 10-27 "Slider, default 0.3 m"): seasonal (Vedr winter) snow depth outside the snow biomes, at full winter cover
        internal static float WinterMaxDepth { get { return Mathf.Clamp(P("Winter snow depth outside the snow biomes (m)",  0.7f), 0f, 1f); } }

        // 0.2.1060 (lead, Fire's RenderDoc: the terrain shader samples the paint mask as sRGB, so Core's snow drew ~7x too low and its
        // trails were invisible): Core writes its snow G encoded so the drawn height is the real one, and decodes it for the sink / slow /
        // footsteps (SnowGroundAsDeepNorth). On by default; off = the old raw write, for an A/B.
        // 0.2.1061: the 1060 sRGB encode is retired (vanilla's raw G scale, like the Deep North); kept as a constant for the old readers
        internal static bool RealHeight => false;
        /// <summary>0.2.1061: the Deep North way - season snow in the heightmap base mask, vanilla trails, SnowRefill. Default on.</summary>
        internal static bool NaturalSnow { get { return P("Snow in the natural ground", true); } }

        // 0.2.1180: a fix, default on; off = the old first build, for the A/B.
        internal static bool FirstBuildSnow { get { return P("Snow in place on first zone build", true); } }
        // 0.2.1187 (Fire 11-06 perf pass, mud cost): the first-build ground rules patch only the paint texels they changed (vanilla's order: base, then
        // painted texels win), instead of re-running Heightmap.Generate. An optimizer, default on; off = the 1180 second Generate, for the A/B.
        internal static bool FirstBuildPatchPaint { get { return P("Snow in place without a second terrain pass", true); } }
        // 0.2.1189 (Fire 11-06 perf pass, SnowTicker cost): a wind-drift-only re-bake skips chunks with no Deep North / Mountain ground (drift
        // cannot change them). An optimizer, default on; off = every announced chunk recomputed, for the A/B. FAT binds it.
        internal static bool BaseRefreshSkipsUnchanged { get { return P("Snow base refresh skips unchanged chunks", true); } }

        // 0.2.915 (lead 10-27, Fire's player-facing rule: world-behaviour settings are server-decided): every key that shapes the shared
        // snow field is server-synced, so a client with other values still computes the server's field (trails, shovel, fire rings agree).
        // Bound once per process from Core's ConfigSync setup; the lazy Bind stays for the read paths.
        internal static void BindToSync(FiresCore.Sync.ConfigSync configSync)
        {
            // 1086 ([generator], Fire 11-01: configs live in their own mods): the owner syncs the [Snow] keys
        }

        internal static void Bind()
        {
            // 1086 ([generator], Fire 11-01: configs live in their own mods): no Core bind
        }
    }
}
