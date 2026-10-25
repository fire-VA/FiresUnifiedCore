using FiresCore.Variants;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Variants
{
    /// <summary>
    /// Attack FX follow a variant's colour (the tint of the emission also determines the colour of its attack fx).
    /// Three paths, all with a cheap early-out (VariantFx.Any: no variant on this client = one bool check, no allocation):
    /// - EffectList.Create: an effect created with a tinted character (or anything under it) as its parent is recoloured on the
    ///   spot. Every peer runs these locally, and every peer has the variant applied (it rides the ZDO), so remote players see
    ///   the same colour.
    /// - Projectile.Setup / Aoe.Setup (the owner only): a projectile or AoE fired by a tinted character is recoloured, and the
    ///   colour is written into its ZDO as "fires_fx_tint".
    /// - Projectile.Awake / Aoe.Awake (every peer): an object whose ZDO already carries "fires_fx_tint" is recoloured once on
    ///   load (read once, never per frame). If the ZDO isn't attached yet at Awake, a one-shot reader checks at Start.
    /// </summary>
    internal static class VariantHooks
    {
        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        private static class EffectListCreate
        {
            private static void Postfix(GameObject[] __result, Transform baseParent)
            {
                if (!VariantFx.Any || baseParent == null || __result == null || __result.Length == 0) return;
                if (!VariantFx.TryGetTint(baseParent, out Color tint)) return;
                foreach (GameObject fx in __result) if (fx != null) VariantFx.Recolour(fx, tint);
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class ProjectileSetup
        {
            private static void Postfix(Projectile __instance, Character owner) => OwnerTint(__instance, owner);
        }

        [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup))]
        private static class AoeSetup
        {
            private static void Postfix(Aoe __instance, Character owner) => OwnerTint(__instance, owner);
        }

        [HarmonyPatch(typeof(Projectile), "Awake")]
        private static class ProjectileAwake
        {
            private static void Postfix(Projectile __instance) => ReadTint(__instance);
        }

        [HarmonyPatch(typeof(Aoe), "Awake")]
        private static class AoeAwake
        {
            private static void Postfix(Aoe __instance) => ReadTint(__instance);
        }

        private static void OwnerTint(Component spawned, Character owner)
        {
            if (!VariantFx.Any || owner == null || spawned == null) return;
            if (!VariantFx.TryGetTint(owner.transform, out Color tint)) return;
            VariantFx.Recolour(spawned.gameObject, tint);
            ZNetView nview = spawned.GetComponent<ZNetView>();
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null && nview.IsOwner()) zdo.Set(VariantTable.KeyFxTintHash, new Vector3(tint.r, tint.g, tint.b));
        }

        private static void ReadTint(Component spawned)
        {
            if (spawned == null) return;
            ZNetView nview = spawned.GetComponent<ZNetView>();
            if (nview == null) return;
            ZDO zdo = nview.GetZDO();
            if (zdo == null) { spawned.gameObject.AddComponent<VariantTintReader>(); return; }
            ApplyStored(spawned.gameObject, zdo);
        }

        internal static void ApplyStored(GameObject go, ZDO zdo)
        {
            Vector3 v = zdo.GetVec3(VariantTable.KeyFxTintHash, Vector3.zero);
            if (v == Vector3.zero) return;
            VariantFx.Recolour(go, new Color(v.x, v.y, v.z));
        }
    }

    /// <summary>One check at Start, for an object whose ZDO wasn't attached yet at Awake; then it removes itself.</summary>
    internal sealed class VariantTintReader : MonoBehaviour
    {
        private void Start()
        {
            ZNetView nview = GetComponent<ZNetView>();
            ZDO zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null) VariantHooks.ApplyStored(gameObject, zdo);
            Destroy(this);
        }
    }
}
