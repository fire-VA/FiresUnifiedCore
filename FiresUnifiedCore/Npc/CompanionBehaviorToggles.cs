using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Centralized read/write of per-companion behavior enable flags stored on
    /// the companion's ZDO.  These are toggled from the radial menu and read
    /// from each idle/work behavior's <c>CanStart()</c> to gate execution.
    ///
    /// All flags default to <c>true</c> so legacy companions (with no flags
    /// set) behave exactly as before.  The radial menu only ever writes when
    /// the player explicitly toggles, so a default-true read is the correct
    /// "no preference set" answer.
    /// </summary>
    public static class CompanionBehaviorToggles
    {
        // ZDO key constants - single source of truth for these strings.
        public const string KEY_GATHER   = "companion_gather_enabled";    // wood / resource / farming gathering
        public const string KEY_FIRES    = "companion_fires_enabled";     // fire tending (fuel + light)
        public const string KEY_SMELTER  = "companion_smelter_enabled";   // smelter / kiln / blast furnace operation
        public const string KEY_COOKING  = "companion_cooking_enabled";   // cooking station tending
        public const string KEY_ORGANIZE = "companion_organize_enabled";  // chest deposit + organization
        public const string KEY_CRAFTING = "companion_crafting_enabled";  // crafting station upgrades / repairs
        public const string KEY_LOOT     = "companion_loot_enabled";      // proximity loot pickup
        public const string KEY_HUNTING  = "companion_hunt_enabled";      // attacks neutral wildlife (deer/boar/etc.) on sight
        public const string KEY_FISHING  = "companion_fishing_enabled";   // fishing behavior
        public const string KEY_REPAIR   = "companion_repair_enabled";    // building repair behavior
        public const string KEY_WANDER   = "companion_wander_enabled";    // idle wandering (stationed NPCs use CompanionNpcModule.allowIdleWandering)

        // readers
        public static bool IsGatherEnabled(CompanionController companion)   => Read(companion, KEY_GATHER);
        public static bool IsFiresEnabled(CompanionController companion)    => Read(companion, KEY_FIRES);
        public static bool IsSmelterEnabled(CompanionController companion)  => Read(companion, KEY_SMELTER);
        public static bool IsCookingEnabled(CompanionController companion)  => Read(companion, KEY_COOKING);
        public static bool IsOrganizeEnabled(CompanionController companion) => Read(companion, KEY_ORGANIZE);
        public static bool IsCraftingEnabled(CompanionController companion) => Read(companion, KEY_CRAFTING);
        public static bool IsLootEnabled(CompanionController companion)     => Read(companion, KEY_LOOT);
        public static bool IsFishingEnabled(CompanionController companion)  => Read(companion, KEY_FISHING);
        public static bool IsRepairEnabled(CompanionController companion)   => Read(companion, KEY_REPAIR);
        public static bool IsWanderEnabled(CompanionController companion)   => Read(companion, KEY_WANDER);
        // Hunting defaults to TRUE so wild / freshly-tamed companions still
        // pursue prey on sight. The radial "Hunt" toggle lets the owner
        // disable it on companions that should ignore neutral wildlife.
        public static bool IsHuntingEnabled(CompanionController companion)  => Read(companion, KEY_HUNTING, true);

        // writers
        public static void SetGatherEnabled(CompanionController companion, bool v)   => Write(companion, KEY_GATHER, v);
        public static void SetFiresEnabled(CompanionController companion, bool v)    => Write(companion, KEY_FIRES, v);
        public static void SetSmelterEnabled(CompanionController companion, bool v)  => Write(companion, KEY_SMELTER, v);
        public static void SetCookingEnabled(CompanionController companion, bool v)  => Write(companion, KEY_COOKING, v);
        public static void SetOrganizeEnabled(CompanionController companion, bool v) => Write(companion, KEY_ORGANIZE, v);
        public static void SetCraftingEnabled(CompanionController companion, bool v) => Write(companion, KEY_CRAFTING, v);
        public static void SetLootEnabled(CompanionController companion, bool v)     => Write(companion, KEY_LOOT, v);
        public static void SetFishingEnabled(CompanionController companion, bool v)  => Write(companion, KEY_FISHING, v);
        public static void SetRepairEnabled(CompanionController companion, bool v)   => Write(companion, KEY_REPAIR, v);
        public static void SetHuntingEnabled(CompanionController companion, bool v)  => Write(companion, KEY_HUNTING, v);

        // implementation
        private static bool Read(CompanionController companion, string key)
        {
            return Read(companion, key, true);
        }

        private static bool Read(CompanionController companion, string key, bool defaultValue)
        {
            if (companion == null) return defaultValue;
            var nview = companion.GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo?.GetBool(key, defaultValue) ?? defaultValue;
        }

        private static void Write(CompanionController companion, string key, bool value)
        {
            if (companion == null) return;
            // Skip during local player respawn / loading screen.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            var nview = companion.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            // Take ownership so the write actually persists in MP.
            if (!nview.IsOwner()) nview.ClaimOwnership();

            var zdo = nview.GetZDO();
            if (zdo == null) return;
            zdo.Set(key, value);
        }
    }
}
