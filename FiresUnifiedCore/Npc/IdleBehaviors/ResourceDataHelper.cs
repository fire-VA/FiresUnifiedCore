using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Helper class for extracting and caching useful information from gatherable resources.
    /// Provides a clean interface for companions to interact with trees, rocks, bushes, etc.
    /// </summary>
    public static class ResourceDataHelper
    {
        #region Data Structures
        
        /// <summary>
        /// Types of gatherable resources.
        /// </summary>
        public enum ResourceType
        {
            None,
            Tree,           // TreeBase - standing trees
            Log,            // TreeLog - fallen logs
            Rock,           // MineRock/MineRock5 - rocks and ore deposits
            Pickable,       // Pickable - berries, mushrooms, flowers, etc.
            PickableItem,   // PickableItem - random loot items
            Destructible    // Generic destructible objects
        }
        
        /// <summary>
        /// What tool type is required for this resource.
        /// </summary>
        public enum ToolRequirement
        {
            None,           // No tool needed (pickables)
            Axe,            // Trees, logs
            Pickaxe,        // Rocks, ore
            Any             // Any damaging weapon works
        }
        
        // Alias for compatibility with ResourceGatheringBehavior
        public enum ToolType
        {
            None = ToolRequirement.None,
            Axe = ToolRequirement.Axe,
            Pickaxe = ToolRequirement.Pickaxe,
            Any = ToolRequirement.Any
        }
        
        /// <summary>
        /// Cached data about a gatherable resource.
        /// </summary>
        public class ResourceData
        {
            // Basic identification
            public ResourceType Type { get; set; }
            public string Name { get; set; }
            public GameObject GameObject { get; set; }
            
            // Tool requirements - use ToolType for external API compatibility
            public ToolType RequiredTool { get; set; }
            public int MinToolTier { get; set; }
            
            // Internal helper to get as ToolRequirement
            internal ToolRequirement RequiredToolRequirement => (ToolRequirement)RequiredTool;
            
            // Component references (only one will be set based on Type)
            public TreeBase TreeBase { get; set; }
            public TreeLog TreeLog { get; set; }
            public MineRock MineRock { get; set; }
            public MineRock5 MineRock5 { get; set; }
            public Pickable Pickable { get; set; }
            public PickableItem PickableItem { get; set; }
            public IDestructible Destructible { get; set; }
            
            // Interaction data
            public Vector3 InteractionPosition { get; set; }
            public float InteractionRadius { get; set; }
            
            // State
            public bool IsPickable => Type == ResourceType.Pickable || Type == ResourceType.PickableItem;
            public bool RequiresCombat => RequiredTool == ToolType.Axe || RequiredTool == ToolType.Pickaxe;
            public bool IsValid => GameObject != null && Type != ResourceType.None;
            
            /// <summary>
            /// Checks if this resource can be gathered with the given tool tier.
            /// </summary>
            public bool CanGatherWithTier(int toolTier)
            {
                if (IsPickable) return true;
                return toolTier >= MinToolTier;
            }
            
            /// <summary>
            /// Gets the skill type that should be raised when gathering this resource.
            /// </summary>
            public Skills.SkillType GetGatheringSkill()
            {
                switch (Type)
                {
                    case ResourceType.Tree:
                    case ResourceType.Log:
                        return Skills.SkillType.WoodCutting;
                    case ResourceType.Rock:
                        return Skills.SkillType.Pickaxes;
                    case ResourceType.Pickable:
                        // Pickables may have their own skill (like Farming for crops)
                        if (Pickable != null && Pickable.m_pickRaiseSkill != Skills.SkillType.None)
                            return Pickable.m_pickRaiseSkill;
                        return Skills.SkillType.None;
                    default:
                        return Skills.SkillType.None;
                }
            }
        }
        
        #endregion
        
        #region Main API
        
        /// <summary>
        /// Extracts resource data from a GameObject.
        /// Returns null if the object is not a gatherable resource.
        /// </summary>
        public static ResourceData GetResourceData(GameObject obj)
        {
            if (obj == null) return null;
            
            // Check for Pickable first (no combat needed)
            var pickable = obj.GetComponent<Pickable>() ?? obj.GetComponentInParent<Pickable>();
            if (pickable != null && pickable.CanBePicked())
            {
                return CreatePickableData(pickable);
            }
            
            // Check for PickableItem
            var pickableItem = obj.GetComponent<PickableItem>() ?? obj.GetComponentInParent<PickableItem>();
            if (pickableItem != null)
            {
                return CreatePickableItemData(pickableItem);
            }
            
            // Check for TreeBase
            var treeBase = obj.GetComponent<TreeBase>() ?? obj.GetComponentInParent<TreeBase>();
            if (treeBase != null)
            {
                return CreateTreeBaseData(treeBase);
            }
            
            // Check for TreeLog
            var treeLog = obj.GetComponent<TreeLog>() ?? obj.GetComponentInParent<TreeLog>();
            if (treeLog != null)
            {
                return CreateTreeLogData(treeLog);
            }
            
            // Check for MineRock5 (more common, check first)
            var mineRock5 = obj.GetComponent<MineRock5>() ?? obj.GetComponentInParent<MineRock5>();
            if (mineRock5 != null)
            {
                return CreateMineRock5Data(mineRock5);
            }
            
            // Check for MineRock
            var mineRock = obj.GetComponent<MineRock>() ?? obj.GetComponentInParent<MineRock>();
            if (mineRock != null)
            {
                return CreateMineRockData(mineRock);
            }
            
            // Check for generic IDestructible
            // CRITICAL: Skip objects that have a Pickable component - these are berry bushes, etc.
            // Berry bushes have BOTH Pickable and Destructible, but should ONLY be interacted with,
            // never attacked. If the bush has no berries, just skip it entirely.
            var destructible = obj.GetComponent<IDestructible>();
            if (destructible == null)
            {
                destructible = obj.GetComponentInParent<IDestructible>();
            }
            if (destructible != null)
            {
                // CRITICAL: Check if this has a Pickable component anywhere
                // If it does, it's a berry bush or similar - DO NOT treat as destructible resource
                var hasPickable = obj.GetComponent<Pickable>() != null || obj.GetComponentInParent<Pickable>() != null;
                if (hasPickable)
                {
                    // This is a bush with berries/items - don't return destructible data
                    // The bush itself is not a resource to be gathered
                    return null;
                }
                
                return CreateDestructibleData(obj, destructible);
            }
            
            return null;
        }
        
        /// <summary>
        /// Checks if a GameObject is a gatherable resource.
        /// CRITICAL: Returns false for berry bushes that have no berries - those should not be gathered/attacked.
        /// </summary>
        public static bool IsGatherableResource(GameObject obj)
        {
            if (obj == null) return false;
            
            // Check for Pickable first - but only if it CAN be picked
            var pickable = obj.GetComponent<Pickable>() ?? obj.GetComponentInParent<Pickable>();
            if (pickable != null)
            {
                // Only gatherable if it has something to pick
                return pickable.CanBePicked();
            }
            
            if (obj.GetComponent<PickableItem>() != null) return true;
            if (obj.GetComponent<TreeBase>() != null) return true;
            if (obj.GetComponent<TreeLog>() != null) return true;
            if (obj.GetComponent<MineRock>() != null) return true;
            if (obj.GetComponent<MineRock5>() != null) return true;
            
            // Check parent as well
            if (obj.GetComponentInParent<TreeBase>() != null) return true;
            if (obj.GetComponentInParent<TreeLog>() != null) return true;
            if (obj.GetComponentInParent<MineRock>() != null) return true;
            if (obj.GetComponentInParent<MineRock5>() != null) return true;
            
            return false;
        }
        
        /// <summary>
        /// Finds all gatherable resources within range.
        /// </summary>
        public static List<ResourceData> FindResourcesInRange(Vector3 position, float range, System.Predicate<ResourceData> filter = null)
        {
            var results = new List<ResourceData>();
            var colliders = Physics.OverlapSphere(position, range);
            var processed = new HashSet<GameObject>();
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var obj = collider.gameObject;
                
                // Avoid processing same object multiple times (may have multiple colliders)
                var root = GetResourceRoot(obj);
                if (root == null || processed.Contains(root)) continue;
                processed.Add(root);
                
                var data = GetResourceData(root);
                if (data != null && data.IsValid)
                {
                    if (filter == null || filter(data))
                    {
                        results.Add(data);
                    }
                }
            }
            
            return results;
        }
        
        /// <summary>
        /// Finds the nearest gatherable resource within range.
        /// </summary>
        public static ResourceData FindNearestResource(Vector3 position, float range, System.Predicate<ResourceData> filter = null)
        {
            var resources = FindResourcesInRange(position, range, filter);
            if (resources.Count == 0) return null;
            
            ResourceData nearest = null;
            float nearestDist = float.MaxValue;
            
            foreach (var resource in resources)
            {
                float dist = Vector3.Distance(position, resource.InteractionPosition);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = resource;
                }
            }
            
            return nearest;
        }
        
        #endregion
        
        #region Data Creation Methods
        
        private static ResourceData CreatePickableData(Pickable pickable)
        {
            return new ResourceData
            {
                Type = ResourceType.Pickable,
                Name = pickable.GetHoverName(),
                GameObject = pickable.gameObject,
                Pickable = pickable,
                RequiredTool = ToolType.None,
                MinToolTier = 0,
                InteractionPosition = pickable.transform.position,
                InteractionRadius = 2f
            };
        }
        
        private static ResourceData CreatePickableItemData(PickableItem pickableItem)
        {
            return new ResourceData
            {
                Type = ResourceType.PickableItem,
                Name = pickableItem.GetHoverName(),
                GameObject = pickableItem.gameObject,
                PickableItem = pickableItem,
                RequiredTool = ToolType.None,
                MinToolTier = 0,
                InteractionPosition = pickableItem.transform.position,
                InteractionRadius = 2f
            };
        }
        
        private static ResourceData CreateTreeBaseData(TreeBase tree)
        {
            // Sanity check: m_minToolTier should be 0-10 max. Values like int.MaxValue indicate corruption.
            int minTier = tree.m_minToolTier;
            if (minTier < 0 || minTier > 100)
            {
                Debug.LogWarning($"[ResourceDataHelper] TreeBase {tree.name} has invalid m_minToolTier={minTier}, defaulting to 0");
                minTier = 0;
            }
            
            return new ResourceData
            {
                Type = ResourceType.Tree,
                Name = GetFriendlyName(tree.gameObject.name, "Tree"),
                GameObject = tree.gameObject,
                TreeBase = tree,
                Destructible = tree,
                RequiredTool = ToolType.Axe,
                MinToolTier = minTier,
                InteractionPosition = tree.transform.position,
                InteractionRadius = 2.5f
            };
        }
        
        private static ResourceData CreateTreeLogData(TreeLog log)
        {
            // Sanity check: m_minToolTier should be 0-10 max. Values like int.MaxValue indicate corruption.
            int minTier = log.m_minToolTier;
            if (minTier < 0 || minTier > 100)
            {
                Debug.LogWarning($"[ResourceDataHelper] TreeLog {log.name} has invalid m_minToolTier={minTier}, defaulting to 0");
                minTier = 0;
            }
            
            return new ResourceData
            {
                Type = ResourceType.Log,
                Name = GetFriendlyName(log.gameObject.name, "Log"),
                GameObject = log.gameObject,
                TreeLog = log,
                Destructible = log,
                RequiredTool = ToolType.Axe,
                MinToolTier = minTier,
                InteractionPosition = log.transform.position,
                InteractionRadius = 3f
            };
        }
        
        private static ResourceData CreateMineRockData(MineRock rock)
        {
            // Sanity check: m_minToolTier should be 0-10 max. Values like int.MaxValue indicate corruption.
            int minTier = rock.m_minToolTier;
            if (minTier < 0 || minTier > 100)
            {
                Debug.LogWarning($"[ResourceDataHelper] MineRock {rock.name} has invalid m_minToolTier={minTier}, defaulting to 0");
                minTier = 0;
            }
            
            return new ResourceData
            {
                Type = ResourceType.Rock,
                Name = rock.GetHoverName(),
                GameObject = rock.gameObject,
                MineRock = rock,
                Destructible = rock,
                RequiredTool = ToolType.Pickaxe,
                MinToolTier = minTier,
                InteractionPosition = rock.transform.position,
                InteractionRadius = 2.5f
            };
        }
        
        private static ResourceData CreateMineRock5Data(MineRock5 rock)
        {
            // Sanity check: m_minToolTier should be 0-10 max. Values like int.MaxValue indicate corruption.
            int minTier = rock.m_minToolTier;
            if (minTier < 0 || minTier > 100)
            {
                Debug.LogWarning($"[ResourceDataHelper] MineRock5 {rock.name} has invalid m_minToolTier={minTier}, defaulting to 0");
                minTier = 0;
            }
            
            return new ResourceData
            {
                Type = ResourceType.Rock,
                Name = rock.GetHoverName(),
                GameObject = rock.gameObject,
                MineRock5 = rock,
                Destructible = rock,
                RequiredTool = ToolType.Pickaxe,
                MinToolTier = minTier,
                InteractionPosition = rock.transform.position,
                InteractionRadius = 3f
            };
        }
        
        private static ResourceData CreateDestructibleData(GameObject obj, IDestructible destructible)
        {
            var worldDestructible = destructible as Destructible;
            return new ResourceData
            {
                Type = ResourceType.Destructible,
                Name = GetFriendlyName(obj.name, "Object"),
                GameObject = obj,
                Destructible = destructible,
                RequiredTool = worldDestructible != null ? GetToolForDamageModifiers(worldDestructible.m_damages) : ToolType.Any,
                MinToolTier = worldDestructible != null ? worldDestructible.m_minToolTier : 0,
                InteractionPosition = obj.transform.position,
                InteractionRadius = 2f
            };
        }
        
        /// <summary>
        /// The tool a destructible takes damage from (Destructible.cs:77 applies m_damages): anything that accepts weapon
        /// damage needs no particular tool, one immune to all but chop (stumps) needs an axe, one immune to all but
        /// pickaxe damage (ore deposits) needs a pickaxe.
        /// </summary>
        private static ToolType GetToolForDamageModifiers(HitData.DamageModifiers modifiers)
        {
            if (TakesDamage(modifiers.m_blunt) || TakesDamage(modifiers.m_slash) || TakesDamage(modifiers.m_pierce)) return ToolType.Any;
            if (TakesDamage(modifiers.m_chop)) return ToolType.Axe;
            if (TakesDamage(modifiers.m_pickaxe)) return ToolType.Pickaxe;
            return ToolType.Any;
        }

        private static bool TakesDamage(HitData.DamageModifier modifier)
        {
            return modifier != HitData.DamageModifier.Immune && modifier != HitData.DamageModifier.Ignore;
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Gets the root GameObject of a resource (handles child colliders).
        /// </summary>
        private static GameObject GetResourceRoot(GameObject obj)
        {
            if (obj == null) return null;
            
            // Check for resource components on object or parents
            if (obj.GetComponent<Pickable>() != null) return obj;
            if (obj.GetComponent<PickableItem>() != null) return obj;
            if (obj.GetComponent<TreeBase>() != null) return obj;
            if (obj.GetComponent<TreeLog>() != null) return obj;
            if (obj.GetComponent<MineRock>() != null) return obj;
            if (obj.GetComponent<MineRock5>() != null) return obj;
            
            // Check parents
            var parent = obj.GetComponentInParent<TreeBase>();
            if (parent != null) return parent.gameObject;
            
            var logParent = obj.GetComponentInParent<TreeLog>();
            if (logParent != null) return logParent.gameObject;
            
            var rockParent = obj.GetComponentInParent<MineRock>();
            if (rockParent != null) return rockParent.gameObject;
            
            var rock5Parent = obj.GetComponentInParent<MineRock5>();
            if (rock5Parent != null) return rock5Parent.gameObject;
            
            var pickableParent = obj.GetComponentInParent<Pickable>();
            if (pickableParent != null) return pickableParent.gameObject;
            
            return null;
        }
        
        /// <summary>
        /// Creates a friendly display name from a prefab name.
        /// </summary>
        private static string GetFriendlyName(string prefabName, string fallback)
        {
            if (string.IsNullOrEmpty(prefabName)) return fallback;
            
            string name = prefabName.ToLowerInvariant();
            
            // Trees
            if (name.Contains("beech")) return "Beech Tree";
            if (name.Contains("birch")) return "Birch Tree";
            if (name.Contains("oak")) return "Oak Tree";
            if (name.Contains("pine")) return "Pine Tree";
            if (name.Contains("fir")) return "Fir Tree";
            if (name.Contains("ygg")) return "Ancient Tree";
            if (name.Contains("tree")) return "Tree";
            
            // Logs
            if (name.Contains("log")) return "Log";
            if (name.Contains("stump")) return "Tree Stump";
            
            // Rocks and Ore
            if (name.Contains("copper")) return "Copper Deposit";
            if (name.Contains("tin")) return "Tin Deposit";
            if (name.Contains("iron") || name.Contains("mudpile")) return "Iron Deposit";
            if (name.Contains("silver")) return "Silver Deposit";
            if (name.Contains("obsidian")) return "Obsidian";
            if (name.Contains("marble")) return "Marble";
            if (name.Contains("rock")) return "Rock";
            if (name.Contains("stone")) return "Stone";
            
            return fallback;
        }
        
        /// <summary>
        /// A tool counts by what it does, not its name: a wielded weapon or tool (1.0 AxeHead1/2 and the Uncooked gold
        /// axes are Materials) of at least <paramref name="minTier"/> that deals chop damage for axe work or pickaxe
        /// damage for mining (Scythe and Shovel deal neither).
        /// </summary>
        public static bool IsToolAppropriate(ItemDrop.ItemData item, ToolRequirement requirement, int minTier)
        {
            if (requirement == ToolRequirement.None) return true;
            if (item?.m_shared == null || !IsWieldedWeaponOrTool(item)) return false;
            if (item.m_shared.m_toolTier < minTier) return false;
            return requirement == ToolRequirement.Any || GetToolDamage(item, requirement) > 0f;
        }
            
        /// <summary>
        /// Overload that accepts ToolType for external API compatibility.
        /// </summary>
        public static bool IsToolAppropriate(ItemDrop.ItemData item, ToolType toolType, int minTier)
        {
            return IsToolAppropriate(item, (ToolRequirement)toolType, minTier);
        }
                
        public static bool IsWieldedWeaponOrTool(ItemDrop.ItemData item)
        {
            var itemType = item.m_shared.m_itemType;
            return itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                   itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                   itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                   itemType == ItemDrop.ItemData.ItemType.Tool;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is the better tool for the job: higher m_toolTier first, then more of
        /// the damage that job needs (chop for axes, pickaxe for mining).
        /// </summary>
        public static bool IsBetterTool(ItemDrop.ItemData candidate, ItemDrop.ItemData current, ToolType tool)
        {
            if (current == null) return true;
            if (candidate.m_shared.m_toolTier != current.m_shared.m_toolTier)
                return candidate.m_shared.m_toolTier > current.m_shared.m_toolTier;
            var requirement = (ToolRequirement)tool;
            return GetToolDamage(candidate, requirement) > GetToolDamage(current, requirement);
        }
            
        private static float GetToolDamage(ItemDrop.ItemData item, ToolRequirement requirement)
        {
            var damage = item.GetDamage();
            switch (requirement)
            {
                case ToolRequirement.Axe:
                    return damage.m_chop;
                case ToolRequirement.Pickaxe:
                    return damage.m_pickaxe;
                default:
                    return 0f;
            }
        }
                    
        /// <summary>
        /// Takes the item out of <paramref name="slot"/>: onto the first free slot of <paramref name="backSlots"/>, else
        /// into storage when it has room. Returns false and leaves the item equipped when nothing can take it.
        /// </summary>
        public static bool TryStowEquipped(CompanionInventory inventory, CompanionInventory.EquipmentSlot slot, params CompanionInventory.EquipmentSlot[] backSlots)
        {
            var item = inventory.GetEquippedItem(slot);
            if (item == null) return true;
                           
            foreach (var backSlot in backSlots)
            {
                if (inventory.GetEquippedItem(backSlot) != null) continue;
                inventory.UnequipSlotSilent(slot);
                inventory.EquipItemSilent(backSlot, item);
                return true;
            }

            var storage = inventory.GetStorageInventory();
            if (storage == null || !storage.CanAddItem(item)) return false;
            inventory.UnequipSlotSilent(slot);
            storage.AddItem(item);
            return true;
        }
        
        /// <summary>
        /// Moves <paramref name="tool"/> into the right hand from <paramref name="toolSlot"/> (null: from storage). What the
        /// hand held goes to a free RightBack (weapons, when <paramref name="holsterWeaponOnBack"/>) or storage; when
        /// neither can take it the tool goes back where it was and false is returned.
        /// </summary>
        public static bool TryEquipInRightHand(CompanionInventory inventory, ItemDrop.ItemData tool, CompanionInventory.EquipmentSlot? toolSlot, bool holsterWeaponOnBack)
        {
            var storage = inventory.GetStorageInventory();
            if (toolSlot.HasValue)
                inventory.UnequipSlotSilent(toolSlot.Value);
            else
                storage.RemoveItem(tool);

            var held = inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand);
            bool handFree = held == null || (holsterWeaponOnBack && held.IsWeapon()
                ? TryStowEquipped(inventory, CompanionInventory.EquipmentSlot.RightHand, CompanionInventory.EquipmentSlot.RightBack)
                : TryStowEquipped(inventory, CompanionInventory.EquipmentSlot.RightHand));
            if (!handFree)
            {
                if (toolSlot.HasValue)
                    inventory.EquipItemSilent(toolSlot.Value, tool);
                else
                    storage.AddItem(tool);
                return false;
            }
        
            inventory.EquipItemSilent(CompanionInventory.EquipmentSlot.RightHand, tool);
            return true;
        }
        
        /// <summary>
        /// Gets the skill type for the given tool requirement.
        /// </summary>
        public static Skills.SkillType GetSkillForTool(ToolRequirement requirement)
        {
            switch (requirement)
            {
                case ToolRequirement.Axe:
                    return Skills.SkillType.WoodCutting;
                case ToolRequirement.Pickaxe:
                    return Skills.SkillType.Pickaxes;
                default:
                    return Skills.SkillType.None;
            }
        }
        
        // Damage multiplier for companion resource gathering
        // This prevents high-tier axes from one-shotting trees
        // Makes gathering feel more natural and balanced
        private const float TreeDamageMultiplier = 0.35f;      // Trees take ~3 hits
        private const float LogDamageMultiplier = 0.5f;        // Logs take ~2 hits
        private const float RockDamageMultiplier = 0.4f;       // Rocks take multiple hits
        
        /// <summary>
        /// Creates a HitData for attacking a resource.
        /// Damage is scaled down to prevent one-shotting resources with high-tier tools.
        /// </summary>
        public static HitData CreateResourceHitData(
            ResourceData resource,
            Character attacker,
            ItemDrop.ItemData weapon,
            Vector3 hitPoint,
            Vector3 hitDir,
            Collider hitCollider = null)
        {
            var hitData = new HitData();
            
            // Set tool tier from weapon
            hitData.m_toolTier = weapon != null ? (short)weapon.m_shared.m_toolTier : (short)0;
            
            // Set damage from weapon
            if (weapon != null)
            {
                hitData.m_damage = weapon.GetDamage();
                hitData.m_skill = weapon.m_shared.m_skillType;
                hitData.m_pushForce = weapon.m_shared.m_attackForce;
                hitData.m_backstabBonus = weapon.m_shared.m_backstabBonus;
                hitData.m_blockable = weapon.m_shared.m_blockable;
                hitData.m_dodgeable = weapon.m_shared.m_dodgeable;
                // HitData.CheckToolTier compares m_itemWorldLevel with the world level (HitData.cs:303; Attack.cs:1069-1070).
                hitData.m_itemLevel = (short)weapon.m_quality;
                hitData.m_itemWorldLevel = (byte)weapon.m_worldLevel;
                
                // Apply damage scaling based on resource type
                // This prevents high-tier tools from one-shotting resources
                float damageMultiplier = 1f;
                switch (resource.Type)
                {
                    case ResourceType.Tree:
                        damageMultiplier = TreeDamageMultiplier;
                        break;
                    case ResourceType.Log:
                        damageMultiplier = LogDamageMultiplier;
                        break;
                    case ResourceType.Rock:
                        damageMultiplier = RockDamageMultiplier;
                        break;
                }
                
                // Scale all damage types (but not tool tier - that affects what CAN be gathered)
                if (damageMultiplier < 1f)
                {
                    hitData.m_damage.m_chop *= damageMultiplier;
                    hitData.m_damage.m_pickaxe *= damageMultiplier;
                    hitData.m_damage.m_slash *= damageMultiplier;
                    hitData.m_damage.m_blunt *= damageMultiplier;
                    hitData.m_damage.m_pierce *= damageMultiplier;
                    // Keep fire/frost/lightning/poison/spirit at full - usually not relevant for gathering
                }
            }
            else
            {
                // Unarmed fallback - minimal damage
                hitData.m_damage.m_blunt = 5f;
                hitData.m_skill = Skills.SkillType.Unarmed;
                hitData.m_itemWorldLevel = (byte)Game.m_worldLevel;
            }
            
            // Set hit location
            hitData.m_point = hitPoint;
            hitData.m_dir = hitDir;
            hitData.m_hitCollider = hitCollider;
            
            // Set attacker
            if (attacker != null)
            {
                hitData.SetAttacker(attacker);
                hitData.m_hitType = attacker is Player ? HitData.HitType.PlayerHit : HitData.HitType.EnemyHit;
                
                if (weapon != null)
                {
                    hitData.m_skillLevel = attacker.GetSkillLevel(weapon.m_shared.m_skillType);
                }
            }
            
            // For MineRock5 and MineRock which use radius detection when no specific collider
            // This allows fallback AoE damage when companion can't line up exactly
            if (hitCollider == null && resource.Type == ResourceType.Rock)
            {
                hitData.m_radius = 1.5f; // Increased radius for better hit detection on floating rocks
            }
            
            // For trees and logs, also allow radius-based damage if no collider found
            if (hitCollider == null && (resource.Type == ResourceType.Tree || resource.Type == ResourceType.Log))
            {
                hitData.m_radius = 1.0f;
            }
            
            return hitData;
        }
        
        #endregion
        
        #region Work Swing

        private const string UnarmedAttackTrigger = "unarmed_attack0";

        /// <summary>
        /// Plays a weapon's primary-attack trigger for a work swing, built the way vanilla Attack.Start builds it
        /// (Attack.cs:239-253): the chain only continues inside vanilla's 0.2 s window after the previous swing, and a
        /// hit on a destructible type in m_resetChainIfHit (axes: trees) restarts it (Attack.cs:343-345, 1104-1105).
        /// </summary>
        public sealed class AttackChain
        {
            private const float ChainWindow = 0.2f;

            private string _animation;
            private int _nextLevel;

            public string Swing(ZSyncAnimation zanim, Animator animator, ItemDrop.ItemData weapon, float timeSinceLastAttack, DestructibleType target)
            {
                string trigger = NextTrigger(weapon, timeSinceLastAttack, target);
                if (zanim != null)
                    zanim.SetTrigger(trigger);
                else if (animator != null)
                    animator.SetTrigger(HasTrigger(animator, trigger) ? trigger : UnarmedAttackTrigger);
                return trigger;
            }

            private string NextTrigger(ItemDrop.ItemData weapon, float timeSinceLastAttack, DestructibleType target)
            {
                var attack = weapon?.m_shared?.m_attack;
                if (attack == null || string.IsNullOrEmpty(attack.m_attackAnimation))
                {
                    _animation = null;
                    return UnarmedAttackTrigger;
                }

                string animation = attack.m_attackAnimation;
                bool continuesChain = animation == _animation && timeSinceLastAttack <= ChainWindow;
                _animation = animation;

                if (attack.m_attackChainLevels > 1)
                {
                    int level = continuesChain && _nextLevel < attack.m_attackChainLevels ? _nextLevel : 0;
                    _nextLevel = (target & attack.m_resetChainIfHit) != DestructibleType.None ? 0 : (level + 1) % attack.m_attackChainLevels;
                    return animation + level;
                }
                if (attack.m_attackRandomAnimations >= 2)
                    return animation + Random.Range(0, attack.m_attackRandomAnimations);
                return animation;
            }

            private static bool HasTrigger(Animator animator, string trigger)
            {
                foreach (var parameter in animator.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Trigger && parameter.name == trigger) return true;
                return false;
            }
        }

        #endregion

        #region Tool Crafting

        private const int CraftQuality = 1;
        private static readonly List<CraftingStation> StationsInRange = new List<CraftingStation>();

        /// <summary>
        /// The first of <paramref name="toolPrefabs"/> that suits the job and whose vanilla recipe (ObjectDB.GetRecipe) the
        /// inventory can pay for. <paramref name="station"/> is the nearest station within <paramref name="stationRadius"/>
        /// that meets the recipe, or null when the recipe needs none (Player.RequiredCraftingStation).
        /// </summary>
        public static Recipe FindCraftableTool(IEnumerable<string> toolPrefabs, ToolType tool, int minTier, Inventory inventory,
            Vector3 position, float stationRadius, out CraftingStation station)
        {
            station = null;
            if (ObjectDB.instance == null || inventory == null) return null;

            foreach (string prefabName in toolPrefabs)
            {
                var itemPrefab = ObjectDB.instance.GetItemPrefab(prefabName);
                var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (itemDrop == null || !IsToolAppropriate(itemDrop.m_itemData, tool, minTier)) continue;

                var recipe = ObjectDB.instance.GetRecipe(itemDrop.m_itemData);
                if (recipe == null || !recipe.m_enabled || !HasCraftingResources(recipe, inventory)) continue;

                if (recipe.GetRequiredStation(CraftQuality) == null) return recipe;
                station = FindStationForRecipe(recipe, position, stationRadius);
                if (station != null) return recipe;
            }
            return null;
        }

        public const int NoToolTier = -1;

        /// <summary>The highest m_toolTier among <paramref name="items"/> that suits the job, or <see cref="NoToolTier"/>.</summary>
        public static int BestToolTier(IEnumerable<ItemDrop.ItemData> items, ToolType tool)
        {
            int best = NoToolTier;
            foreach (var item in items)
                if (IsToolAppropriate(item, tool, 0) && item.m_shared.m_toolTier > best)
                    best = item.m_shared.m_toolTier;
            return best;
        }

        /// <summary>The highest tier among <paramref name="toolPrefabs"/> the inventory can craft right now, or <see cref="NoToolTier"/>.</summary>
        public static int BestCraftableToolTier(IEnumerable<string> toolPrefabs, ToolType tool, Inventory inventory, Vector3 position, float stationRadius)
        {
            int best = NoToolTier;
            if (ObjectDB.instance == null) return best;
            foreach (string prefabName in toolPrefabs)
            {
                var itemDrop = ObjectDB.instance.GetItemPrefab(prefabName)?.GetComponent<ItemDrop>();
                if (itemDrop == null || itemDrop.m_itemData.m_shared.m_toolTier <= best) continue;
                if (FindCraftableTool(new[] { prefabName }, tool, itemDrop.m_itemData.m_shared.m_toolTier, inventory, position, stationRadius, out _) != null)
                    best = itemDrop.m_itemData.m_shared.m_toolTier;
            }
            return best;
        }

        /// <summary>A standing tree or log an axe of <paramref name="axeTier"/> can cut (TreeBase/TreeLog.m_minToolTier).</summary>
        public static bool CanChop(GameObject tree, int axeTier)
        {
            if (tree == null) return false;
            var treeBase = tree.GetComponent<TreeBase>();
            if (treeBase != null) return treeBase.m_minToolTier <= axeTier;
            var treeLog = tree.GetComponent<TreeLog>();
            return treeLog == null || treeLog.m_minToolTier <= axeTier;
        }

        /// <summary>
        /// Crafts as InventoryGui.DoCrafting does: station, resources and room for the result are checked before anything
        /// is spent, and the item is added from its prefab so it keeps m_dropPrefab and the world level (Inventory.cs:88).
        /// </summary>
        public static bool CraftTool(Recipe recipe, CraftingStation station, Inventory inventory)
        {
            if (!StationMeetsRecipe(recipe, station) || !HasCraftingResources(recipe, inventory)) return false;

            GameObject itemPrefab = recipe.m_item.gameObject;
            if (!inventory.CanAddItem(itemPrefab, recipe.m_amount)) return false;

            foreach (var requirement in GetCraftCosts(recipe))
                inventory.RemoveItem(requirement.m_resItem.m_itemData.m_shared.m_name, requirement.GetAmount(CraftQuality));
            return inventory.AddItem(itemPrefab, recipe.m_amount);
        }

        private static CraftingStation FindStationForRecipe(Recipe recipe, Vector3 position, float radius)
        {
            StationsInRange.Clear();
            CraftingStation.FindStationsInRange(recipe.GetRequiredStation(CraftQuality).m_name, position, radius, StationsInRange);

            CraftingStation nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (var candidate in StationsInRange)
            {
                if (!StationMeetsRecipe(recipe, candidate)) continue;
                float distance = Vector3.Distance(position, candidate.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }
            }
            return nearest;
        }

        private static bool StationMeetsRecipe(Recipe recipe, CraftingStation station)
        {
            var requiredStation = recipe.GetRequiredStation(CraftQuality);
            if (requiredStation == null) return true;
            return station != null && station.m_name == requiredStation.m_name
                && station.GetLevel() >= recipe.GetRequiredStationLevel(CraftQuality);
        }

        private static bool HasCraftingResources(Recipe recipe, Inventory inventory)
        {
            foreach (var requirement in GetCraftCosts(recipe))
                if (inventory.CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) < requirement.GetAmount(CraftQuality))
                    return false;
            return true;
        }

        /// <summary>
        /// What a craft at a normal station costs: 1.0 upgrade tokens (m_upgraderResource) ride on recipes but are only
        /// taken at an upgrader station (Player.cs:1967, 2086).
        /// </summary>
        private static IEnumerable<Piece.Requirement> GetCraftCosts(Recipe recipe)
        {
            foreach (var requirement in recipe.m_resources)
                if (requirement.m_resItem != null && !requirement.m_upgraderResource)
                    yield return requirement;
        }

        #endregion

        #region World Prefab Data

        private static ZNetScene _indexedScene;
        private static readonly HashSet<string> TreeStumpPrefabs = new HashSet<string>();
        private static readonly Dictionary<string, string> SaplingByStumpPrefab = new Dictionary<string, string>();
        private static readonly HashSet<string> SmeltableItemPrefabs = new HashSet<string>();

        /// <summary>Every item some smelting station converts (the m_conversion inputs of each Smelter prefab).</summary>
        public static ICollection<string> SmeltableItems
        {
            get
            {
                EnsurePrefabIndex();
                return SmeltableItemPrefabs;
            }
        }

        /// <summary>A stump a felled tree leaves behind: some TreeBase's m_stubPrefab (a StumpHut or stubbe is not one).</summary>
        public static bool IsTreeStump(GameObject obj)
        {
            if (obj == null) return false;
            EnsurePrefabIndex();
            return TreeStumpPrefabs.Contains(Utils.GetPrefabName(obj));
        }

        /// <summary>The sapling that grows the tree this stump came from (Plant.m_grownPrefabs), or null when none does.</summary>
        public static string GetSaplingForStump(string stumpPrefabName)
        {
            EnsurePrefabIndex();
            return SaplingByStumpPrefab.TryGetValue(stumpPrefabName, out string sapling) ? sapling : null;
        }

        public static GameObject FindNearestTreeStump(Vector3 position, float radius)
        {
            GameObject closest = null;
            float closestDistance = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(position, radius))
            {
                var destructible = collider.GetComponentInParent<Destructible>();
                if (destructible == null || !IsTreeStump(destructible.gameObject)) continue;
                float distance = Vector3.Distance(position, destructible.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = destructible.gameObject;
                }
            }
            return closest;
        }

        /// <summary>
        /// True when mining the object out yields one of <paramref name="itemPrefabs"/>: its own drop table, or that of the
        /// fractured object a Destructible deposit turns into (Destructible.cs:154-163, rock4_copper -> rock4_copper_frac).
        /// </summary>
        public static bool YieldsAnyOf(GameObject obj, ICollection<string> itemPrefabs)
        {
            if (DropTableContainsAny(GetMinedDropTable(obj), itemPrefabs)) return true;
            var destructible = obj.GetComponent<Destructible>();
            return destructible != null && destructible.m_spawnWhenDestroyed != null
                && DropTableContainsAny(GetMinedDropTable(destructible.m_spawnWhenDestroyed), itemPrefabs);
        }

        private static DropTable GetMinedDropTable(GameObject obj)
        {
            var mineRock5 = obj.GetComponent<MineRock5>();
            if (mineRock5 != null) return mineRock5.m_dropItems;
            var mineRock = obj.GetComponent<MineRock>();
            if (mineRock != null) return mineRock.m_dropItems;
            var dropOnDestroyed = obj.GetComponent<DropOnDestroyed>();
            return dropOnDestroyed != null ? dropOnDestroyed.m_dropWhenDestroyed : null;
        }

        private static bool DropTableContainsAny(DropTable table, ICollection<string> itemPrefabs)
        {
            if (table == null) return false;
            foreach (var drop in table.m_drops)
                if (drop.m_item != null && itemPrefabs.Contains(drop.m_item.name)) return true;
            return false;
        }

        private static void EnsurePrefabIndex()
        {
            var scene = ZNetScene.instance;
            if (scene == null || scene == _indexedScene) return;
            _indexedScene = scene;
            TreeStumpPrefabs.Clear();
            SaplingByStumpPrefab.Clear();
            SmeltableItemPrefabs.Clear();

            var saplingByTree = new Dictionary<string, string>();
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                var plant = prefab.GetComponent<Plant>();
                if (plant != null)
                {
                    foreach (var grown in plant.m_grownPrefabs)
                        if (grown != null && !saplingByTree.ContainsKey(grown.name))
                            saplingByTree[grown.name] = prefab.name;
                }
                var smelter = prefab.GetComponent<Smelter>();
                if (smelter != null)
                    SmeltableItemPrefabs.UnionWith(PieceDataHelper.GetStationInputs(smelter));
            }

            foreach (var prefab in scene.m_prefabs)
            {
                var tree = prefab != null ? prefab.GetComponent<TreeBase>() : null;
                if (tree == null || tree.m_stubPrefab == null) continue;
                string stump = tree.m_stubPrefab.name;
                TreeStumpPrefabs.Add(stump);
                if (!SaplingByStumpPrefab.ContainsKey(stump) && saplingByTree.TryGetValue(prefab.name, out string sapling))
                    SaplingByStumpPrefab[stump] = sapling;
            }
        }

        #endregion

        #region Logging
        
        /// <summary>
        /// Logs detailed information about a resource for debugging.
        /// </summary>
        public static void LogResourceData(ResourceData data, string context = "")
        {
            if (data == null)
            {
                Debug.Log($"[ResourceDataHelper] {context} - No resource data");
                return;
            }
            
            Debug.Log($"[ResourceDataHelper] {context} - Resource: {data.Name}\n" +
                $"  Type: {data.Type}, RequiredTool: {data.RequiredTool}, MinTier: {data.MinToolTier}\n" +
                $"  IsPickable: {data.IsPickable}, RequiresCombat: {data.RequiresCombat}\n" +
                $"  InteractionPos: {data.InteractionPosition}, Radius: {data.InteractionRadius}");
        }
        
        #endregion
    }
}
