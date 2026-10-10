using System.Collections.Generic;
using UnityEngine;
using FiresCore.Logging;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.356: the companion pouches (Tool, Food, Ammo) as Core reads them. 0.2.361: a pouch is a row of fixed cells in the
    /// companion bag (CompanionGridLayout: Tool 4, Ammo 3, Food 3); the AI uses the first non-empty matching cell.
    /// Core cannot reference FiresCompanions, so the rule lives here too.
    /// KEEP IN SYNC with FiresCompanions Modules\Companions\CompanionPouches.cs: the same enum names in the same order, and the
    /// same Matches logic. A change on one side must be made on the other.
    /// </summary>
    public static class CompanionPouchRules
    {
        /// <summary>The three single-stack pouch slots on the companion panel (same names and order as FiresCompanions' PouchKind).
        /// Nested so it never collides with FiresCompanions' own PouchKind in files that import both namespaces.</summary>
        public enum PouchKind { Tool, Food, Ammo }

        /// <summary>True when the item belongs in the given pouch.</summary>
        public static bool Matches(PouchKind kind, ItemDrop.ItemData item)
        {
            var shared = item?.m_shared;
            if (shared == null) return false;
            switch (kind)
            {
                case PouchKind.Food:
                    return shared.m_food > 0 || shared.m_foodStamina > 0;
                case PouchKind.Ammo:
                    return shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo;
                default:
                    // Hammer, hoe and cultivator are ItemType Tool; axes, pickaxes and the fishing rod are
                    // weapons in vanilla, so they are recognised by their skill.
                    return shared.m_itemType == ItemDrop.ItemData.ItemType.Tool
                        || shared.m_skillType == Skills.SkillType.Axes
                        || shared.m_skillType == Skills.SkillType.Pickaxes
                        || shared.m_skillType == Skills.SkillType.Fishing;
            }
        }

        /// <summary>The stack in the companion's pouch: 0.2.361, the first non-empty POUCH cell of that kind that matches
        /// (CompanionGridLayout rows 4-6); false (item null) when the pouch row is empty.</summary>
        public static bool TryGetPouchItem(CompanionInventory inventory, PouchKind kind, out ItemDrop.ItemData item)
        {
            item = null;
            if (inventory == null) return false;
            foreach (var candidate in inventory.GetPouchItems(kind))
            {
                if (candidate != null && Matches(kind, candidate)) { item = candidate; return true; }
            }
            return false;
        }

        // ---- Callers' helpers: each returns the pouch item when it fits, otherwise the caller's own pick unchanged ----

        /// <summary>The Tool pouch item when it can do <paramref name="tool"/> work at <paramref name="minTier"/>
        /// (ResourceDataHelper.IsToolAppropriate), otherwise <paramref name="currentPick"/>.</summary>
        public static ItemDrop.ItemData PreferPouchTool(CompanionInventory inventory, ResourceDataHelper.ToolType tool, int minTier,
            ItemDrop.ItemData currentPick, string who)
        {
            if (!TryGetPouchItem(inventory, PouchKind.Tool, out var pouch)) return currentPick;
            if (pouch == currentPick) { LogDecision(who, PouchKind.Tool, $"tool from pouch: {pouch.m_shared.m_name}"); return currentPick; }
            if (!ResourceDataHelper.IsToolAppropriate(pouch, tool, minTier))
            {
                LogDecision(who, PouchKind.Tool, $"pouch tool skipped: {pouch.m_shared.m_name} is not a {tool} of tier {minTier}+ (tier {pouch.m_shared.m_toolTier})");
                return currentPick;
            }
            LogDecision(who, PouchKind.Tool, $"tool from pouch: {pouch.m_shared.m_name} (over {currentPick?.m_shared?.m_name ?? "none"})");
            return pouch;
        }

        /// <summary>The Ammo pouch stack when it fits <paramref name="weapon"/>'s m_ammoType and carries a projectile,
        /// otherwise <paramref name="currentPick"/>.</summary>
        public static ItemDrop.ItemData PreferPouchAmmo(CompanionInventory inventory, ItemDrop.ItemData weapon, ItemDrop.ItemData currentPick, string who)
        {
            string ammoType = weapon?.m_shared?.m_ammoType;
            if (string.IsNullOrEmpty(ammoType) || !TryGetPouchItem(inventory, PouchKind.Ammo, out var pouch)) return currentPick;
            if (pouch.m_shared.m_ammoType != ammoType || pouch.m_shared.m_attack?.m_attackProjectile == null)
            {
                LogDecision(who, PouchKind.Ammo, $"pouch ammo skipped: {pouch.m_shared.m_name} is not {ammoType} ammo");
                return currentPick;
            }
            LogDecision(who, PouchKind.Ammo, $"ammo from pouch: {pouch.m_shared.m_name}");
            return pouch;
        }

        /// <summary>Logs one pouch decision at Debug: again only when the line changes or every
        /// <see cref="RepeatSeconds"/>, so per-shot and per-tick callers do not spam.</summary>
        public static void LogDecision(string who, PouchKind kind, string what)
        {
            string key = (who ?? "?") + "|" + kind;
            string line = $"[Pouch] {who ?? "?"} {what}";
            float now = Time.time;
            if (s_lastLine.TryGetValue(key, out var last) && last.Line == line && now - last.Time < RepeatSeconds) return;
            s_lastLine[key] = (line, now);
            FiresLogger.LogDebug(line);
        }

        private const float RepeatSeconds = 30f;
        private static readonly Dictionary<string, (string Line, float Time)> s_lastLine = new Dictionary<string, (string Line, float Time)>();
    }
}
