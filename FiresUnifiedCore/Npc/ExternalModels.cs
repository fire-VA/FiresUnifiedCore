using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Compatibility with creature mods such as Creatures of Origins, so their models get the proper animations.
    /// The one list of body models other mods add,
    /// for every picker (FCC's player models, RPGmaker's NPC model override, the Companions barber). Each entry is a ZNetScene prefab
    /// name that <see cref="BodyModelSwap.Apply"/> takes as is: without "@humanoid" the swap keeps the prefab's OWN animator
    /// controller, so the creature mod's animations come with it. Empty when the mod is not installed. Add-only API.
    /// </summary>
    public static class ExternalModels
    {
        public sealed class ModelOption
        {
            public string Prefab;   // the ZNetScene prefab name, the value pickers store (npc_model_override / FCC model)
            public string Label;    // shown in pickers
            public string Source;   // the mod it comes from
        }

        // Creatures of Origins (AzathothR-Creaturesoforigins, plugin GUID Azathoth.Creaturesoforigins): its humanoid races, from its README
        private const string OriginsGuid = "Azathoth.Creaturesoforigins", OriginsName = "Creatures of Origins";
        private static readonly string[,] s_origins =
        {
            { "RZ_NOMUERTOM", "Undead (male)" }, { "RZ_NOMUERTOF", "Undead (female)" },
            { "RZ_ORCONEGROM", "Black Orc (male)" }, { "RZ_ORCONEGROF", "Black Orc (female)" },
            { "RZ_ORCOVERDEM", "Green Orc (male)" }, { "RZ_ORCOVERDEF", "Green Orc (female)" },
            { "RZ_ORCOVIL", "Vile Orc" },
            { "RZ_ELFOM", "High Elf (male)" }, { "RZ_ELFOF", "High Elf (female)" },
            { "RZ_ELFODELANOCHEM", "Night Elf (male)" }, { "RZ_ELFODELANOCHEF", "Night Elf (female)" },
            { "RZ_DRAENEIM", "Draenei (male)" }, { "RZ_DRAENEIF", "Draenei (female)" },
            { "RZ_NiflbornM", "Niflborn (male)" }, { "RZ_NiflbornF", "Niflborn (female)" },
            { "RZ_JotunM", "Jotun (male)" }, { "RZ_JotunF", "Jotun (female)" },
            { "RZ_MuspelheimM", "Muspelheim (male)" }, { "RZ_MuspelheimF", "Muspelheim (female)" },
            { "RZ_TielfingM", "Tiefling (male)" }, { "RZ_TielfingF", "Tiefling (female)" },
            { "RZ_Mirmidon", "Myrmidon (male)" }, { "RZ_MirmidonF", "Myrmidon (female)" },
            { "RZ_TrollM", "Troll (male)" }, { "RZ_TrollF", "Troll (female)" },
            { "RZ_ENANO", "Dwarf" }, { "RZ_GHOST", "Eternal" },
        };

        private static List<ModelOption> s_cache;
        private static bool s_logged;

        /// <summary>The installed creature mods' body models whose prefab is in ZNetScene now (empty before ZNetScene exists).</summary>
        public static IReadOnlyList<ModelOption> Available()
        {
            if (s_cache != null) return s_cache;
            var list = new List<ModelOption>();
            if (ZNetScene.instance == null) return list;   // not cached: asked again once the scene exists
            bool origins = false;
            try { origins = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(OriginsGuid); } catch { }
            if (origins)
                for (int i = 0; i < s_origins.GetLength(0); i++)
                {
                    var prefab = ZNetScene.instance.GetPrefab(s_origins[i, 0]);
                    if (prefab == null || prefab.GetComponent<Humanoid>() == null) continue;
                    list.Add(new ModelOption { Prefab = s_origins[i, 0], Label = s_origins[i, 1], Source = OriginsName });
                }
            s_cache = list;
            if (!s_logged)
            {
                s_logged = true;
                FiresCore.Logging.FiresLogger.LogInfo(origins
                    ? $"[ExternalModels] {OriginsName} found: {list.Count} body model(s) offered to the character creator, NPC and companion pickers"
                    : "[ExternalModels] no creature model mod installed (Creatures of Origins not loaded)");
            }
            return s_cache;
        }

        /// <summary>True when the prefab is one of the external models (pickers may label or group it).</summary>
        public static bool IsExternal(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return false;
            foreach (var o in Available()) if (string.Equals(o.Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
