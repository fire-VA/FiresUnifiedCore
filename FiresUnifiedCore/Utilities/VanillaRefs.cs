using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Utilities
{
    // 1074 ([generator] event-driven/cache): one shared set of typed field-ref
    // delegates for vanilla private fields read on per-tick paths, in place of
    // FieldInfo.GetValue (boxing + reflection per call). Each accessor keeps the
    // old FieldInfo as a fallback: if the delegate cannot be built (field missing
    // or its type changed in a game update) the read goes through the FieldInfo
    // exactly as before, and a missing field still reads as null/absent.
    // Fields with private nested element types (InventoryGui.m_availableRecipes,
    // EnemyHud.m_huds) stay on FieldInfo at their call sites.
    internal static class VanillaRefs
    {
        private static AccessTools.FieldRef<T, F> Make<T, F>(FieldInfo fi)
        {
            if (fi == null) return null;
            try { return AccessTools.FieldRefAccess<T, F>(fi); } catch { return null; }
        }

        // Player.m_foods
        internal static readonly FieldInfo F_PlayerFoods = typeof(Player).GetField("m_foods",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        private static readonly AccessTools.FieldRef<Player, List<Player.Food>> R_PlayerFoods =
            Make<Player, List<Player.Food>>(F_PlayerFoods);
        internal static System.Collections.IList PlayerFoods(Player p)
            => R_PlayerFoods != null ? R_PlayerFoods(p) : F_PlayerFoods?.GetValue(p) as System.Collections.IList;

        // Player.m_guardianPowerCooldown
        internal static readonly FieldInfo F_GuardianCooldown = typeof(Player).GetField("m_guardianPowerCooldown",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        private static readonly AccessTools.FieldRef<Player, float> R_GuardianCooldown =
            Make<Player, float>(F_GuardianCooldown);
        internal static float GuardianCooldown(Player p)
            => R_GuardianCooldown != null ? R_GuardianCooldown(p) : (float)F_GuardianCooldown.GetValue(p);

        // Inventory.m_onChanged
        internal static readonly FieldInfo F_InvOnChanged = AccessTools.Field(typeof(Inventory), "m_onChanged");
        private static readonly AccessTools.FieldRef<Inventory, Action> R_InvOnChanged =
            Make<Inventory, Action>(F_InvOnChanged);
        internal static Action InvOnChanged(Inventory inv)
        {
            if (R_InvOnChanged == null) return F_InvOnChanged?.GetValue(inv) as Action;
            if (inv == null) throw new TargetException();   // same as FieldInfo.GetValue(null)
            return R_InvOnChanged(inv);
        }

        // InventoryGui.m_currentContainer
        internal static readonly FieldInfo F_CurrentContainer = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        private static readonly AccessTools.FieldRef<InventoryGui, Container> R_CurrentContainer =
            Make<InventoryGui, Container>(F_CurrentContainer);
        internal static Container CurrentContainer(InventoryGui g)
            => R_CurrentContainer != null ? R_CurrentContainer(g) : F_CurrentContainer?.GetValue(g) as Container;

        // MessageHud.m_biomeMsgInstance
        internal static readonly FieldInfo F_BiomeMsg = AccessTools.Field(typeof(MessageHud), "m_biomeMsgInstance");
        private static readonly AccessTools.FieldRef<MessageHud, GameObject> R_BiomeMsg =
            Make<MessageHud, GameObject>(F_BiomeMsg);
        internal static GameObject BiomeMsg(MessageHud h)
            => R_BiomeMsg != null ? R_BiomeMsg(h) : F_BiomeMsg?.GetValue(h) as GameObject;

        // Character.m_body
        internal static readonly FieldInfo F_Body = AccessTools.Field(typeof(Character), "m_body");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> R_Body =
            Make<Character, Rigidbody>(F_Body);
        internal static Rigidbody Body(Character c)
            => R_Body != null ? R_Body(c) : F_Body?.GetValue(c) as Rigidbody;
    }
}
