using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Heals the Humanoid.SetupVisEquipment NRE from an equipped item whose m_dropPrefab was never wired
    /// (ItemDrop.Awake's ObjectDB lookup missed), which otherwise takes the whole appearance rebuild down. The
    /// prefix recovers the prefab from the item's SharedData and assigns it for good, or, when the item is truly
    /// unregistered, hides the slot for that rebuild and the postfix restores it. Each culprit is logged once.
    /// Effectively client-only, since a dedicated server has no VisEquipment to rebuild.
    /// </summary>
    internal static class VisEquipmentDropPrefabHeal
    {
        private static readonly HashSet<string> _logged = new HashSet<string>();

        // Explicit attachment (called from FiresUnifiedCore.Setup) with read-back verification —
        // attribute discovery silently missed the Diagnostics guards in the field; see
        // CharacterListLeakGuard's header for the evidence.
        internal static void Register(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(Humanoid), "SetupVisEquipment");
                if (method == null)
                {
                    Debug.LogError("[VisEquip null-drop] Humanoid.SetupVisEquipment NOT FOUND — heal not attached.");
                    return;
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VisEquipmentDropPrefabHeal), nameof(Prefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VisEquipmentDropPrefabHeal), nameof(Postfix))));
                Debug.Log("[VisEquip null-drop] heal attached to Humanoid.SetupVisEquipment (verified by read-back).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VisEquip null-drop] FAILED to attach: {ex.Message}");
            }
        }

        private static readonly (string slot, FieldInfo field)[] _slots = ResolveSlots(
            "m_leftItem", "m_rightItem", "m_hiddenLeftItem", "m_hiddenRightItem",
            "m_chestItem", "m_legItem", "m_helmetItem", "m_shoulderItem",
            "m_utilityItem", "m_trinketItem");

        private static (string, FieldInfo)[] ResolveSlots(params string[] names)
        {
            var list = new List<(string, FieldInfo)>(names.Length);
            foreach (var fieldName in names)
            {
                var field = AccessTools.Field(typeof(Humanoid), fieldName);
                if (field != null) list.Add((fieldName, field));
            }
            return list.ToArray();
        }

        private static void Prefix(Humanoid __instance, out List<KeyValuePair<FieldInfo, ItemDrop.ItemData>> __state)
        {
            __state = null;
            if (__instance == null) return;
            var odb = ObjectDB.instance;

            foreach (var (slot, field) in _slots)
            {
                var item = field.GetValue(__instance) as ItemDrop.ItemData;
                if (item == null || item.m_dropPrefab != null) continue;

                GameObject recovered = (odb != null && item.m_shared != null)
                    ? odb.GetItemPrefab(item.m_shared)
                    : null;
                string sharedName = item.m_shared != null ? item.m_shared.m_name : "<null-shared>";
                bool firstLog = _logged.Add(sharedName + "|" + slot);

                if (recovered != null)
                {
                    item.m_dropPrefab = recovered;
                    if (firstLog)
                        Debug.LogWarning(
                            $"[VisEquip null-drop] {__instance.name} slot={slot} item='{sharedName}' had a null m_dropPrefab — " +
                            $"recovered '{recovered.name}' from SharedData. ROOT: this item's ItemDrop.Awake did not resolve it in " +
                            $"ObjectDB (check its registration name / ItemDrop-on-root / load order).");
                }
                else
                {
                    if (firstLog)
                        Debug.LogError(
                            $"[VisEquip null-drop] {__instance.name} slot={slot} item='{sharedName}' had a null m_dropPrefab and is NOT " +
                            $"resolvable in ObjectDB by SharedData — visual skipped this rebuild (item stays equipped).");
                    (__state ??= new List<KeyValuePair<FieldInfo, ItemDrop.ItemData>>()).Add(
                        new KeyValuePair<FieldInfo, ItemDrop.ItemData>(field, item));
                    field.SetValue(__instance, null);
                }
            }
        }

        private static void Postfix(Humanoid __instance, List<KeyValuePair<FieldInfo, ItemDrop.ItemData>> __state)
        {
            if (__state == null) return;
            foreach (var kv in __state)
                kv.Key.SetValue(__instance, kv.Value);
        }
    }
}
