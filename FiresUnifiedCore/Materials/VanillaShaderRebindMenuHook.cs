using HarmonyLib;

namespace FiresCore.Materials
{
    // The main menu builds its ObjectDB here, so the character preview's equipped items are loaded alongside the
    // game's shaders. The in-world sweep runs from TerrainMaterialCache's ZNetScene.Awake hook.
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
    internal static class ObjectDB_CopyOtherDB_RebindVanillaShaders
    {
        private static void Postfix() => VanillaShaderRebind.RebindAllLoadedMaterials(FiresUnifiedCore.PluginName);
    }
}
