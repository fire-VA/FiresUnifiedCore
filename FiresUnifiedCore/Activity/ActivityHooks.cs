using System.Collections.Generic;
using HarmonyLib;

namespace FiresCore.Activity
{
    /// <summary>
    /// The client-only kinds (the server can't see or verify them). Every hook returns at the first test unless CoreActivityApi.Enabled
    /// says the server listens AND that kind's key is on; then it builds one small JSON object and queues it. Local player only.
    /// </summary>
    internal static class ActivityHooks
    {
        private static readonly Dictionary<Skills.SkillType, (int raises, float factor)> s_skills = new Dictionary<Skills.SkillType, (int, float)>();

        private static bool Local(Character c) => c != null && c == Player.m_localPlayer;
        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";
        private static string Item(ItemDrop.ItemData it) => it == null ? "null"
            : "{\"item\":" + Q(it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared?.m_name) + ",\"q\":" + it.m_quality + ",\"n\":" + it.m_stack + "}";

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Equip
        {
            private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
            {
                if (!__result || !Local(__instance) || !CoreActivityApi.Enabled("equip")) return;
                CoreActivityApi.Report("equip", "{\"on\":true,\"it\":" + Item(item) + "}");
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        private static class Unequip
        {
            private static void Prefix(Humanoid __instance, ItemDrop.ItemData item)
            {
                if (item == null || !item.m_equipped || !Local(__instance) || !CoreActivityApi.Enabled("equip")) return;
                CoreActivityApi.Report("equip", "{\"on\":false,\"it\":" + Item(item) + "}");
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        private static class Consume
        {
            private static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result)
            {
                if (!__result || !Local(__instance) || !CoreActivityApi.Enabled("consume")) return;
                CoreActivityApi.Report("consume", "{\"it\":" + Item(item) + "}");
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "RepairOneItem")]
        private static class Repair
        {
            private static void Prefix(out int __state)
            {
                __state = 0;
                if (!CoreActivityApi.Enabled("repair") || Player.m_localPlayer == null) return;
                foreach (var it in Player.m_localPlayer.GetInventory().GetAllItems()) if (it.m_durability < it.GetMaxDurability()) __state++;
            }

            private static void Postfix(int __state)
            {
                if (__state == 0 || Player.m_localPlayer == null) return;
                int damaged = 0;
                foreach (var it in Player.m_localPlayer.GetInventory().GetAllItems()) if (it.m_durability < it.GetMaxDurability()) damaged++;
                if (damaged >= __state) return;                                                      // nothing was repaired
                var st = Player.m_localPlayer.GetCurrentCraftingStation();
                CoreActivityApi.Report("repair", "{\"station\":" + Q(st != null ? st.m_name : null) + ",\"left\":" + damaged + "}");
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class Craft
        {
            private static readonly AccessTools.FieldRef<InventoryGui, Recipe> s_recipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
            private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> s_upgrade = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");

            private static void Prefix(InventoryGui __instance, Player player)
            {
                if (!Local(player) || !CoreActivityApi.Enabled("craft")) return;
                var r = s_recipe(__instance);
                if (r == null || r.m_item == null) return;
                var up = s_upgrade(__instance);
                var st = player.GetCurrentCraftingStation();
                CoreActivityApi.Report("craft", "{\"item\":" + Q(r.m_item.name) + ",\"n\":" + r.m_amount + ",\"q\":" + (up != null ? up.m_quality + 1 : 1)
                    + ",\"upgrade\":" + (up != null ? "true" : "false") + ",\"station\":" + Q(st != null ? st.m_name : null) + ",\"stationLevel\":" + (st != null ? st.GetLevel() : 0) + "}");
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
        private static class Pickup
        {
            private static void Prefix(Humanoid __instance, UnityEngine.GameObject go, out string __state)
            {
                __state = null;
                if (!Local(__instance) || go == null || !CoreActivityApi.Enabled("pickup")) return;
                var d = go.GetComponent<ItemDrop>();
                if (d != null) __state = "{\"it\":" + Item(d.m_itemData) + ",\"pos\":[" + (int)go.transform.position.x + "," + (int)go.transform.position.y + "," + (int)go.transform.position.z + "]}";
            }

            private static void Postfix(bool __result, string __state)
            {
                if (__result && __state != null) CoreActivityApi.Report("pickup", __state);
            }
        }

        [HarmonyPatch(typeof(Skills), nameof(Skills.RaiseSkill))]
        private static class Skill
        {
            private static void Postfix(Skills __instance, Skills.SkillType skillType, float factor)
            {
                if (!CoreActivityApi.Enabled("skill") || Player.m_localPlayer == null || __instance != Player.m_localPlayer.GetSkills()) return;
                s_skills.TryGetValue(skillType, out var s);
                s_skills[skillType] = (s.raises + 1, s.factor + factor);
            }
        }

        // once a minute (ActivityRpc's summary tick): one row per raised skill, never one per raise
        internal static void FlushSkills()
        {
            if (s_skills.Count == 0) return;
            if (CoreActivityApi.Enabled("skill") && Player.m_localPlayer != null)
            {
                var sk = Player.m_localPlayer.GetSkills();
                foreach (var kv in s_skills)
                    CoreActivityApi.Report("skill", "{\"skill\":" + Q(kv.Key.ToString()) + ",\"raises\":" + kv.Value.raises + ",\"factor\":" + kv.Value.factor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"level\":" + sk.GetSkillLevel(kv.Key).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            s_skills.Clear();
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.TryRunCommand))]
        private static class Command
        {
            private static void Prefix(string text)
            {
                if (string.IsNullOrEmpty(text) || Player.m_localPlayer == null || !CoreActivityApi.Enabled("command")) return;
                CoreActivityApi.Report("command", "{\"text\":" + Q(text.Length > 200 ? text.Substring(0, 200) : text) + "}");
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
        private static class Hotbar
        {
            private static void Prefix(Player __instance, int index)
            {
                if (!Local(__instance) || !CoreActivityApi.Enabled("hotbar")) return;
                var it = __instance.GetInventory().GetItemAt(index - 1, 0);
                CoreActivityApi.Report("hotbar", "{\"slot\":" + index + ",\"it\":" + Item(it) + "}");
            }
        }
    }
}
