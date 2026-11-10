using System;
using System.IO;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// Core's own underwater assets: the embedded "firescoreunderwater" bundle (FatShaderLinear, LINEAR; never firescoreshaders):
    /// Custom/FiresCaustics (from FTS 0.3.362's FatShaderLinear copy, wind-travel scroll), Custom/FiresUnderwaterFog (the underwater zone
    /// of FTS's FiresVolFog) and the bubble textures (BubblePng1, BubblePng3, UnderwaterBubble). Loaded once on first use and kept loaded
    /// (the bubbles scan its textures once). Missing: the parts that need it stand down, one Info WHY line (Underwater.Start logs it).
    /// </summary>
    internal static class UnderwaterAssets
    {
        private const string BundleName = "firescoreunderwater";
        private static bool s_tried;
        private static AssetBundle s_bundle;
        internal static string Why;

        internal static AssetBundle Bundle { get { Load(); return s_bundle; } }

        /// <summary>A shader from the bundle (null if absent or unsupported). The second name is accepted for call-site parity with FTS.</summary>
        internal static Shader LinearShader(string name, string alsoName = null)
        {
            var b = Bundle;
            if (b == null) return null;
            Shader s = null;
            try { s = b.LoadAsset<Shader>(ShortName(name)) ?? (alsoName != null ? b.LoadAsset<Shader>(ShortName(alsoName)) : null); } catch { }
            if (s == null)
                foreach (var x in b.LoadAllAssets<Shader>()) if (x != null && (x.name == name || x.name == alsoName)) { s = x; break; }
            return s != null && s.isSupported ? s : null;
        }

        private static string ShortName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return shaderName;
            int i = shaderName.LastIndexOf('/');
            return i >= 0 ? shaderName.Substring(i + 1) : shaderName;
        }

        private static void Load()
        {
            if (s_tried) return;
            s_tried = true;
            var asm = typeof(UnderwaterAssets).Assembly; string res = null;
            foreach (var n in asm.GetManifestResourceNames()) if (n == BundleName || n.EndsWith("." + BundleName, StringComparison.Ordinal)) res = n;
            if (res == null) { Why = "no embedded firescoreunderwater bundle in this Core"; return; }
            try
            {
                using (var st = asm.GetManifestResourceStream(res)) using (var ms = new MemoryStream()) { st.CopyTo(ms); s_bundle = AssetBundle.LoadFromMemory(ms.ToArray()); }
                if (s_bundle == null) { Why = "firescoreunderwater did not load"; return; }
                FiresLogger.LogDebug($"[Underwater] bundle loaded: {s_bundle.GetAllAssetNames().Length} asset(s)");
            }
            catch (Exception e) { Why = "firescoreunderwater failed: " + e.Message; s_bundle = null; }
        }
    }
}
