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
            public bool RequiresCombat => Type == ResourceType.Tree || Type == ResourceType.Log || Type == ResourceType.Rock;
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
            return new ResourceData
            {
                Type = ResourceType.Destructible,
                Name = GetFriendlyName(obj.name, "Object"),
                GameObject = obj,
                Destructible = destructible,
                RequiredTool = ToolType.Any,
                MinToolTier = 0,
                InteractionPosition = obj.transform.position,
                InteractionRadius = 2f
            };
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
        /// Checks if a weapon/tool is appropriate for the given tool requirement.
        /// </summary>
        public static bool IsToolAppropriate(ItemDrop.ItemData item, ToolRequirement requirement, int minTier)
        {
            if (item == null) return requirement == ToolRequirement.None;
            if (requirement == ToolRequirement.None) return true;
            
            int toolTier = item.m_shared?.m_toolTier ?? -1;
            
            // CRITICAL: Check PREFAB name, not localized m_name!
            // m_name is the localization key like "$item_PickaxeBlackMetal" which may not contain "pickaxe"
            // The prefab name is always in English: "PickaxeBlackMetal", "AxeBronze", etc.
            string prefabName = item.m_dropPrefab?.name?.ToLowerInvariant() ?? "";
            string localizedName = item.m_shared?.m_name?.ToLowerInvariant() ?? "";
            var itemType = item.m_shared?.m_itemType ?? ItemDrop.ItemData.ItemType.None;
            var skillType = item.m_shared?.m_skillType ?? Skills.SkillType.None;
            
            // Combined check - use both prefab name and localized name for robustness
            string combinedName = prefabName + " " + localizedName;
            
            // Check tool tier first
            if (toolTier < minTier)
            {
                // Only log tier failures for tools that otherwise match the requirement
                bool wouldMatchType = false;
                if (requirement == ToolRequirement.Pickaxe && 
                    (combinedName.Contains("pickaxe") || skillType == Skills.SkillType.Pickaxes)) 
                    wouldMatchType = true;
                if (requirement == ToolRequirement.Axe && 
                    (combinedName.Contains("axe") && !combinedName.Contains("pickaxe")) || 
                    skillType == Skills.SkillType.WoodCutting) 
                    wouldMatchType = true;
                
                if (wouldMatchType)
                {
                    Debug.LogWarning($"[ResourceDataHelper] Tool tier too low: {prefabName} (tier {toolTier}) needs tier {minTier}");
                }
                return false;
            }
            
            switch (requirement)
            {
                case ToolRequirement.Axe:
                    // Check by skill type first (most reliable)
                    if (skillType == Skills.SkillType.WoodCutting) return true;
                    // Check by name - axes work but not pickaxes
                    if (combinedName.Contains("pickaxe")) return false;
                    if (combinedName.Contains("axe")) return true;
                    // Battleaxes work too
                    if (itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon && combinedName.Contains("axe")) return true;
                    return false;
                    
                case ToolRequirement.Pickaxe:
                    // Check by skill type first (most reliable)
                    if (skillType == Skills.SkillType.Pickaxes) return true;
                    // Check by name
                    return combinedName.Contains("pickaxe");
                    
                case ToolRequirement.Any:
                    // Any weapon or tool works
                    return itemType == ItemDrop.ItemData.ItemType.Tool ||
                           itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                           itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                           itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
                           
                default:
                    return true;
            }
        }
        
        /// <summary>
        /// Overload that accepts ToolType for external API compatibility.
        /// </summary>
        public static bool IsToolAppropriate(ItemDrop.ItemData item, ToolType toolType, int minTier)
        {
            return IsToolAppropriate(item, (ToolRequirement)toolType, minTier);
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
