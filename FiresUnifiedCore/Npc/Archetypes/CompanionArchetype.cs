using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// A companion's combat role, assigned each session from its equipment: shield users (giants especially) tank,
    /// with one tank per group; support-staff users support; everyone else is melee or ranged DPS by weapon.
    /// </summary>
    public enum CompanionArchetypeType
    {
        /// <summary>No archetype assigned yet.</summary>
        None,
        
        /// <summary>
        /// Tank archetype - draws aggro, blocks, protects allies.
        /// Requirements: Shield + melee weapon, or giant with heavy armor
        /// Behaviors: Uses taunt, prioritizes blocking over attacking, intercepts enemies
        /// </summary>
        Tank,
        
        /// <summary>
        /// Support archetype - buffs and heals allies.
        /// Requirements: Support staff (shield staff, healing staff)
        /// Behaviors: Stays at range, re-applies buffs, prioritizes ally protection
        /// </summary>
        Support,
        
        /// <summary>
        /// Melee DPS archetype - aggressive melee damage dealer.
        /// Requirements: Melee weapon without shield, or dual weapons
        /// Behaviors: Combos, flanking, target switching to low health enemies
        /// </summary>
        MeleeDPS,
        
        /// <summary>
        /// Ranged DPS archetype - ranged damage dealer.
        /// Requirements: Bow, crossbow, or offensive staff
        /// Behaviors: Kiting, focus fire on weak targets, retreat when threatened
        /// </summary>
        RangedDPS
    }
    
    /// <summary>
    /// Data class describing a companion's archetype configuration.
    /// </summary>
    [Serializable]
    public class ArchetypeConfig
    {
        public CompanionArchetypeType Type;
        public float AssignmentTime;
        public bool IsLocked; // Once assigned, don't change mid-combat
        
        // Tank-specific settings
        public float TauntCooldown = 15f;
        public float TauntDuration = 10f;
        public float TauntRange = 8f;
        public float BlockPriorityMultiplier = 2.0f; // How much more likely to block vs attack
        public float InterceptionRange = 10f;
        
        // Support-specific settings  
        public float BuffCheckInterval = 5f;
        public float HealPriorityHealthPercent = 0.5f;
        public float PreferredSupportRange = 12f;
        
        // DPS-specific settings
        public float AggressionLevel = 1.0f;
        public float FlankingPreference = 0.5f;
        public float TargetSwitchHealthThreshold = 0.3f;
    }
    
    /// <summary>
    /// Static utility class for archetype-related operations.
    /// </summary>
    public static class ArchetypeUtils
    {
        private const float GiantScaleThreshold = 1.3f;
        private const float TankGiantPriority = 50f;
        private const float TankShieldPriority = 30f;
        private const float TankMeleeWeaponPriority = 20f;
        private const float ArmorPerTankPriorityPoint = 10f;
        private const float MaxTankArmorPriority = 20f;
        private const float SupportStaffPriority = 100f;
        private const float RangedWeaponPriority = 50f;
        private const float MeleeWeaponPriority = 30f;
        private const float MeleeDwarfPriority = 20f;
        private const float DpsNoShieldPriority = 20f;

        /// <summary>
        /// Evaluates what archetype a companion should have based on their equipment.
        /// </summary>
        public static CompanionArchetypeType EvaluateArchetype(CompanionController companion)
        {
            if (companion == null) return CompanionArchetypeType.None;
            
            var inventory = companion.GetInventory();
            var combat = companion.GetCombat();
            
            if (inventory == null || combat == null) return CompanionArchetypeType.None;
            
            // Check for support staff first (highest priority for Support role)
            if (HasSupportStaff(inventory))
            {
                return CompanionArchetypeType.Support;
            }
            
            // Check for tank equipment (shield + melee weapon)
            bool hasShield = HasShieldEquipped(inventory);
            bool hasMeleeWeapon = HasMeleeWeapon(inventory);
            bool isGiant = IsGiant(companion);
            
            // Giants with shield+sword are natural tanks
            if (hasShield && hasMeleeWeapon)
            {
                // Giants strongly favor tank
                if (isGiant)
                {
                    return CompanionArchetypeType.Tank;
                }
                
                // Non-giants can also be tanks but with lower priority
                // This will be resolved by GroupRoleManager
                return CompanionArchetypeType.Tank;
            }
            
            // Check for ranged weapons
            if (HasRangedWeapon(inventory))
            {
                return CompanionArchetypeType.RangedDPS;
            }
            
            // Default to melee DPS
            if (hasMeleeWeapon)
            {
                return CompanionArchetypeType.MeleeDPS;
            }
            
            return CompanionArchetypeType.None;
        }
        
        /// <summary>
        /// Checks if companion has a support staff (shield staff, healing staff, etc.)
        /// </summary>
        public static bool HasSupportStaff(CompanionInventory inventory)
        {
            if (inventory == null) return false;
            
            // Check both hands and back slots
            var slots = new[] {
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.LeftHand,
                CompanionInventory.EquipmentSlot.RightBack,
                CompanionInventory.EquipmentSlot.LeftBack
            };
            
            foreach (var slot in slots)
            {
                var item = inventory.GetEquippedItem(slot);
                if (IsSupportStaff(item))
                {
                    return true;
                }
            }
            
            // Also check storage inventory
            var storage = inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (IsSupportStaff(item))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an item is a support/buff staff.
        /// Support staves include: shield staves, protection staves, healing staves, and restoration staves.
        /// </summary>
        public static bool IsSupportStaff(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            // Must be a staff (elemental or blood magic skill)
            var skill = item.m_shared.m_skillType;
            if (skill != Skills.SkillType.ElementalMagic && 
                skill != Skills.SkillType.BloodMagic)
            {
                // Some support staves might not have magic skill - check prefab name
                string prefabCheck = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
                if (!prefabCheck.Contains("staff"))
                    return false;
            }
            
            string weaponName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            
            // Check for known support staff patterns
            // Valheim support staves:
            // - StaffShield (protection staff)
            // - StaffGreenRoots (root/entangle - could be support)
            // - StaffClusterbomb (bubble shield)
            if (prefabName.Contains("staffshield") ||
                prefabName.Contains("staff_shield") ||
                prefabName.Contains("staffclusterbomb") ||
                prefabName.Contains("clusterbomb") ||
                weaponName.Contains("$item_staffshield") ||
                weaponName.Contains("shield") ||
                weaponName.Contains("protection") ||
                weaponName.Contains("greenroots") ||
                weaponName.Contains("gentle") ||
                weaponName.Contains("restoration") ||
                weaponName.Contains("heal") ||
                weaponName.Contains("bubble"))
            {
                return true;
            }
            
            // Check for shield-type status effect on the staff
            if (item.m_shared.m_equipStatusEffect != null)
            {
                string effectName = item.m_shared.m_equipStatusEffect.name?.ToLowerInvariant() ?? "";
                if (effectName.Contains("shield") || effectName.Contains("protect"))
                    return true;
            }
            
            // Check for attack status effect that applies shields/protection
            if (item.m_shared.m_attackStatusEffect != null)
            {
                string attackEffect = item.m_shared.m_attackStatusEffect.name?.ToLowerInvariant() ?? "";
                if (attackEffect.Contains("shield") || attackEffect.Contains("protect") || attackEffect.Contains("bubble"))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an item is an offensive staff (fire, ice, lightning, etc.)
        /// </summary>
        public static bool IsOffensiveStaff(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            // Must be a staff (elemental or blood magic)
            var skill = item.m_shared.m_skillType;
            if (skill != Skills.SkillType.ElementalMagic && 
                skill != Skills.SkillType.BloodMagic)
                return false;
            
            // If it's a support staff, it's not offensive
            if (IsSupportStaff(item))
                return false;
            
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            string weaponName = item.m_shared.m_name?.ToLowerInvariant() ?? "";
            
            // Check for known offensive staff patterns
            if (prefabName.Contains("staff_fireball") ||
                prefabName.Contains("staff_ice") ||
                prefabName.Contains("staff_lightning") ||
                prefabName.Contains("staff_skeleton") ||
                prefabName.Contains("staff_dead") ||
                weaponName.Contains("fire") ||
                weaponName.Contains("frost") ||
                weaponName.Contains("ice") ||
                weaponName.Contains("lightning") ||
                weaponName.Contains("dead") ||
                weaponName.Contains("skeleton"))
            {
                return true;
            }
            
            // If it has projectile attacks, it's offensive
            if (item.m_shared.m_attack?.m_attackProjectile != null)
                return true;
            
            return true; // Default to offensive for unknown staves
        }
        
        /// <summary>
        /// Checks if companion has a shield equipped or available.
        /// </summary>
        public static bool HasShieldEquipped(CompanionInventory inventory)
        {
            if (inventory == null) return false;
            
            // Check left hand (shields go here)
            var leftHand = inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand);
            if (leftHand?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                return true;
            }
            
            // Check storage for shields
            var storage = inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (item?.m_shared?.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if companion has a melee weapon.
        /// </summary>
        public static bool HasMeleeWeapon(CompanionInventory inventory)
        {
            if (inventory == null) return false;
            
            var slots = new[] {
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.RightBack
            };
            
            foreach (var slot in slots)
            {
                var item = inventory.GetEquippedItem(slot);
                if (IsMeleeWeapon(item))
                {
                    return true;
                }
            }
            
            // Check storage
            var storage = inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (IsMeleeWeapon(item))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an item is a melee weapon (not ranged, not shield, not tool).
        /// </summary>
        public static bool IsMeleeWeapon(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (!item.IsWeapon()) return false;
            
            // Not shields
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                return false;
            
            // Not bows
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return false;
            
            // Not tools
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                return false;
            
            // Not ranged skills
            var skill = item.m_shared.m_skillType;
            if (skill == Skills.SkillType.Bows ||
                skill == Skills.SkillType.Crossbows ||
                skill == Skills.SkillType.ElementalMagic ||
                skill == Skills.SkillType.BloodMagic)
                return false;
            
            // Not gathering tools
            if (skill == Skills.SkillType.Pickaxes ||
                skill == Skills.SkillType.WoodCutting)
                return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if companion has a ranged weapon.
        /// </summary>
        public static bool HasRangedWeapon(CompanionInventory inventory)
        {
            if (inventory == null) return false;
            
            var slots = new[] {
                CompanionInventory.EquipmentSlot.LeftHand,
                CompanionInventory.EquipmentSlot.LeftBack,
                CompanionInventory.EquipmentSlot.RightHand,
                CompanionInventory.EquipmentSlot.RightBack
            };
            
            foreach (var slot in slots)
            {
                var item = inventory.GetEquippedItem(slot);
                if (IsRangedWeapon(item))
                {
                    return true;
                }
            }
            
            // Check storage
            var storage = inventory.GetStorageInventory();
            if (storage != null)
            {
                foreach (var item in storage.GetAllItems())
                {
                    if (IsRangedWeapon(item))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if an item is a ranged weapon.
        /// </summary>
        public static bool IsRangedWeapon(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                return true;
            
            var skill = item.m_shared.m_skillType;
            if (skill == Skills.SkillType.Bows ||
                skill == Skills.SkillType.Crossbows)
                return true;
            
            // Offensive staves (not support staves)
            if ((skill == Skills.SkillType.ElementalMagic ||
                 skill == Skills.SkillType.BloodMagic) &&
                !IsSupportStaff(item))
                return true;
            
            // Check for bow draw or reload
            if (item.m_shared.m_attack?.m_bowDraw == true ||
                item.m_shared.m_attack?.m_requiresReload == true)
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Checks if companion is a giant (large scale).
        /// </summary>
        public static bool IsGiant(CompanionController companion)
        {
            if (companion == null) return false;
            
            var loadout = companion.GetComponent<CompanionRandomLoadout>();
            if (loadout != null)
            {
                return loadout.IsGiant();
            }
            
            // Fallback: check transform scale
            float scale = companion.transform.localScale.x;
            return scale > GiantScaleThreshold;
        }
        
        /// <summary>
        /// Gets the priority score for a companion to take a specific archetype.
        /// Higher score = more suitable for the role.
        /// </summary>
        public static float GetArchetypePriority(CompanionController companion, CompanionArchetypeType archetype)
        {
            if (companion == null) return 0f;
            
            var inventory = companion.GetInventory();
            if (inventory == null) return 0f;
            
            float priority = 0f;
            
            switch (archetype)
            {
                case CompanionArchetypeType.Tank:
                    // Giants are best tanks
                    if (IsGiant(companion)) priority += TankGiantPriority;

                    // Shield is required
                    if (HasShieldEquipped(inventory)) priority += TankShieldPriority;
                    else return 0f; // Can't be tank without shield

                    // Melee weapon needed
                    if (HasMeleeWeapon(inventory)) priority += TankMeleeWeaponPriority;
                    else return 0f; // Can't be tank without melee
                    
                    // Bonus for heavy armor (check total armor)
                    var combat = companion.GetCombat();
                    if (combat != null)
                    {
                        float armor = combat.GetTotalArmor();
                        priority += Mathf.Min(armor / ArmorPerTankPriorityPoint, MaxTankArmorPriority); // Up to 20 bonus for armor
                    }
                    break;
                    
                case CompanionArchetypeType.Support:
                    if (HasSupportStaff(inventory)) priority += SupportStaffPriority;
                    else return 0f; // Must have support staff
                    break;
                    
                case CompanionArchetypeType.RangedDPS:
                    if (HasRangedWeapon(inventory)) priority += RangedWeaponPriority;
                    else return 0f;

                    // Bonus if no shield (pure ranged)
                    if (!HasShieldEquipped(inventory)) priority += DpsNoShieldPriority;
                    break;
                    
                case CompanionArchetypeType.MeleeDPS:
                    if (HasMeleeWeapon(inventory)) priority += MeleeWeaponPriority;
                    else return 0f;

                    // Dwarves are good melee DPS (aggressive)
                    var loadout = companion.GetComponent<CompanionRandomLoadout>();
                    if (loadout != null && loadout.IsDwarf()) priority += MeleeDwarfPriority;

                    // Two-handed weapons = more DPS focused
                    // No shield = pure DPS
                    if (!HasShieldEquipped(inventory)) priority += DpsNoShieldPriority;
                    break;
            }
            
            return priority;
        }
    }
}
