using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Helper class for extracting and caching useful information from Piece components.
    /// Similar to how ItemDrop data is handled, this provides a clean interface for
    /// companions to interact with world pieces like workstations, chairs, fires, etc.
    /// </summary>
    public static class PieceDataHelper
    {
        #region Data Structures
        
        /// <summary>
        /// Cached data about a piece for companion interactions.
        /// </summary>
        public class PieceData
        {
            // Basic identification
            public string Name { get; set; }
            public string Description { get; set; }
            public Piece.PieceCategory Category { get; set; }
            
            // Comfort data (for chairs, beds, etc.)
            public int Comfort { get; set; }
            public Piece.ComfortGroup ComfortGroup { get; set; }
            public bool HasComfort => Comfort > 0;
            
            // Attachment data (for sitting, sleeping, etc.)
            public bool HasAttachPoint { get; set; }
            public Transform AttachPoint { get; set; }
            public string AttachAnimation { get; set; }
            
            // Crafting station data
            public bool IsCraftingStation { get; set; }
            public CraftingStation CraftingStation { get; set; }
            public string CraftingStationType { get; set; }
            
            // Fire/cooking data
            public bool IsFireplace { get; set; }
            public Fireplace Fireplace { get; set; }
            public bool IsCookingStation { get; set; }
            public CookingStation CookingStation { get; set; }
            
            // Smelter data
            public bool IsSmelter { get; set; }
            public Smelter Smelter { get; set; }
            public string SmelterType { get; set; } // Display label only: "Smelter", "Charcoal Kiln", "Blast Furnace", etc.
            
            // Container data
            public bool HasContainer { get; set; }
            public Container Container { get; set; }
            
            // Chair-specific data
            public bool IsChair { get; set; }
            public Chair Chair { get; set; }
            
            // Bed-specific data
            public bool IsBed { get; set; }
            public Bed Bed { get; set; }
            
            // Interaction position
            public Vector3 InteractionPosition { get; set; }
            public float InteractionRadius { get; set; }
            
            // The original piece reference
            public Piece Piece { get; set; }
            public GameObject GameObject { get; set; }
        }
        
        #endregion
        
        #region Main API
        
        /// <summary>
        /// Extracts all relevant data from a GameObject that has a Piece component.
        /// Returns null if the GameObject doesn't have a Piece.
        /// </summary>
        public static PieceData GetPieceData(GameObject obj)
        {
            if (obj == null) return null;
            
            var piece = obj.GetComponent<Piece>();
            if (piece == null)
            {
                // Try parent - some pieces have colliders on children
                piece = obj.GetComponentInParent<Piece>();
            }
            
            if (piece == null) return null;
            
            return ExtractPieceData(piece);
        }
        
        /// <summary>
        /// Extracts all relevant data from a Piece component.
        /// </summary>
        public static PieceData ExtractPieceData(Piece piece)
        {
            if (piece == null) return null;
            
            var data = new PieceData
            {
                Piece = piece,
                GameObject = piece.gameObject,
                Name = piece.m_name,
                Description = piece.m_description,
                Category = piece.m_category,
                Comfort = piece.m_comfort,
                ComfortGroup = piece.m_comfortGroup,
                InteractionPosition = piece.transform.position,
                InteractionRadius = 2f // Default
            };
            
            // Extract crafting station data
            var craftingStation = piece.GetComponent<CraftingStation>();
            if (craftingStation != null)
            {
                data.IsCraftingStation = true;
                data.CraftingStation = craftingStation;
                data.CraftingStationType = GetCraftingStationType(craftingStation);
                data.InteractionRadius = craftingStation.m_rangeBuild;
            }
            
            // Extract fireplace data
            var fireplace = piece.GetComponent<Fireplace>();
            if (fireplace != null)
            {
                data.IsFireplace = true;
                data.Fireplace = fireplace;
            }
            
            // Extract cooking station data
            var cookingStation = piece.GetComponent<CookingStation>();
            if (cookingStation != null)
            {
                data.IsCookingStation = true;
                data.CookingStation = cookingStation;
            }
            
            // Extract smelter data
            var smelter = piece.GetComponent<Smelter>();
            if (smelter != null)
            {
                data.IsSmelter = true;
                data.Smelter = smelter;
                data.SmelterType = GetSmelterType(smelter);
            }
            
            // Extract container data
            var container = piece.GetComponent<Container>();
            if (container == null)
                container = piece.GetComponentInChildren<Container>();
            if (container != null)
            {
                data.HasContainer = true;
                data.Container = container;
            }
            
            // Extract chair data
            var chair = piece.GetComponent<Chair>();
            if (chair != null)
            {
                data.IsChair = true;
                data.Chair = chair;
                ExtractChairAttachData(chair, data);
            }
            
            // Extract bed data
            var bed = piece.GetComponent<Bed>();
            if (bed != null)
            {
                data.IsBed = true;
                data.Bed = bed;
                ExtractBedAttachData(bed, data);
            }
            
            // If no specific attach point found, try generic attach point search
            if (!data.HasAttachPoint)
            {
                FindGenericAttachPoint(piece, data);
            }
            
            return data;
        }
        
        #endregion
        
        #region Specialized Extractors
        
        /// <summary>
        /// Extracts attach point data from a Chair component.
        /// </summary>
        private static void ExtractChairAttachData(Chair chair, PieceData data)
        {
            if (chair == null) return;
            
            // Chair has m_attachPoint for where to sit
            var attachPoint = chair.m_attachPoint;
            if (attachPoint != null)
            {
                data.HasAttachPoint = true;
                data.AttachPoint = attachPoint;
                data.AttachAnimation = chair.m_attachAnimation;
                data.InteractionPosition = attachPoint.position;
            }
            else
            {
                // Fallback - use chair position
                data.HasAttachPoint = true;
                data.AttachPoint = chair.transform;
                data.AttachAnimation = "attach_chair";
                data.InteractionPosition = chair.transform.position;
            }
        }
        
        /// <summary>
        /// Extracts attach point data from a Bed component.
        /// </summary>
        private static void ExtractBedAttachData(Bed bed, PieceData data)
        {
            if (bed == null) return;
            
            // Bed has m_spawnPoint for where to lie
            var spawnPoint = bed.m_spawnPoint;
            if (spawnPoint != null)
            {
                data.HasAttachPoint = true;
                data.AttachPoint = spawnPoint;
                data.AttachAnimation = "attach_bed"; // Standard bed animation
                data.InteractionPosition = spawnPoint.position;
            }
        }
        
        /// <summary>
        /// Tries to find a generic attach point on the piece.
        /// Many pieces have child transforms named "attach" or similar.
        /// </summary>
        private static void FindGenericAttachPoint(Piece piece, PieceData data)
        {
            if (piece == null) return;
            
            // Common attach point names
            string[] attachPointNames = { "attach", "Attach", "attachpoint", "AttachPoint", "sit", "Sit", "use", "Use" };
            
            foreach (var name in attachPointNames)
            {
                var attachPoint = piece.transform.Find(name);
                if (attachPoint != null)
                {
                    data.HasAttachPoint = true;
                    data.AttachPoint = attachPoint;
                    data.InteractionPosition = attachPoint.position;
                    
                    // Try to determine animation based on comfort group
                    data.AttachAnimation = GetAnimationForComfortGroup(data.ComfortGroup);
                    return;
                }
            }
            
            // Also check for children with "attach" in the name
            foreach (Transform child in piece.transform)
            {
                if (child.name.ToLower().Contains("attach"))
                {
                    data.HasAttachPoint = true;
                    data.AttachPoint = child;
                    data.InteractionPosition = child.position;
                    data.AttachAnimation = GetAnimationForComfortGroup(data.ComfortGroup);
                    return;
                }
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Gets the crafting station type string for logging/display.
        /// </summary>
        public static string GetCraftingStationType(CraftingStation station)
        {
            if (station == null) return "Unknown";
            
            string prefabName = Utils.GetPrefabName(station.gameObject);
            
            // Map common prefab names to friendly names
            if (prefabName.Contains("workbench")) return "Workbench";
            if (prefabName.Contains("forge")) return "Forge";
            if (prefabName.Contains("cauldron")) return "Cauldron";
            if (prefabName.Contains("stonecutter")) return "Stonecutter";
            if (prefabName.Contains("artisan")) return "Artisan Table";
            if (prefabName.Contains("blackforge")) return "Black Forge";
            if (prefabName.Contains("galdr")) return "Galdr Table";
            
            return station.m_name ?? prefabName;
        }
        
        /// <summary>
        /// Label for logs and status text only. Behaviour reads the Smelter's data through
        /// <see cref="IsOperableStation"/> and <see cref="IsCharcoalKiln"/>, never this string.
        /// </summary>
        public static string GetSmelterType(Smelter smelter)
        {
            if (smelter == null) return "Unknown";
            if (IsCharcoalKiln(smelter)) return "Charcoal Kiln";
            
            string prefabName = Utils.GetPrefabName(smelter.gameObject).ToLower();
            if (prefabName.Contains("blastfurnace") || prefabName.Contains("blast_furnace"))
                return "Blast Furnace";
            if (prefabName.Contains("smelter"))
                return "Smelter";
            if (prefabName.Contains("spinning"))
                return "Spinning Wheel";
            if (prefabName.Contains("windmill"))
                return "Windmill";
            if (prefabName.Contains("eitr") || prefabName.Contains("refinery"))
                return "Eitr Refinery";
            
            if (string.IsNullOrEmpty(smelter.m_name)) return "Processing Station";
            return Localization.instance?.Localize(smelter.m_name) ?? smelter.m_name;
        }
        
        /// <summary>What a charcoal kiln turns its wood into.</summary>
        public const string CoalPrefab = "Coal";
        
        /// <summary>
        /// A Smelter a companion may run on its own: not a siege machine's engine (the battering ram's wood-burning
        /// "kiln engine", SiegeMachine.m_engine) and producing something (the bathtub has no conversions).
        /// </summary>
        public static bool IsOperableStation(Smelter smelter)
        {
            if (smelter == null || smelter.GetComponentInParent<SiegeMachine>() != null) return false;
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_to != null) return true;
            return false;
        }
        
        /// <summary>
        /// Charcoal kiln, from its data: no fuel item, and every conversion turns an input into Coal. The 1.0 Frost Kiln
        /// (fuel Ice, no ore slot, one input-less conversion; fuel-only production, Smelter.cs:346) is not one.
        /// </summary>
        public static bool IsCharcoalKiln(Smelter smelter)
        {
            if (!IsOperableStation(smelter) || smelter.m_fuelItem != null) return false;
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_from == null || conversion.m_to == null || conversion.m_to.gameObject.name != CoalPrefab)
                    return false;
            return true;
        }
        
        /// <summary>The items a Smelter-type station takes: exactly its m_conversion inputs, as vanilla
        /// Smelter.IsItemAllowed checks them (the charcoal kiln is Wood, FineWood and RoundLog in 1.0).</summary>
        public static List<string> GetStationInputs(Smelter smelter)
        {
            var inputs = new List<string>();
            if (smelter == null) return inputs;
            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion.m_from == null) continue;
                string name = conversion.m_from.gameObject.name;
                if (!inputs.Contains(name)) inputs.Add(name);
            }
            return inputs;
        }

        public static bool StationAccepts(Smelter smelter, string prefabName)
        {
            if (smelter == null || string.IsNullOrEmpty(prefabName)) return false;
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_from != null && conversion.m_from.gameObject.name == prefabName)
                    return true;
            return false;
        }
        
        public static bool StationProduces(Smelter smelter, string prefabName)
        {
            if (smelter == null || string.IsNullOrEmpty(prefabName)) return false;
            foreach (var conversion in smelter.m_conversion)
                if (conversion.m_to != null && conversion.m_to.gameObject.name == prefabName)
                    return true;
            return false;
        }

        /// <summary>
        /// Gets the appropriate animation for a comfort group.
        /// </summary>
        public static string GetAnimationForComfortGroup(Piece.ComfortGroup group)
        {
            switch (group)
            {
                case Piece.ComfortGroup.Chair:
                    return "attach_chair";
                case Piece.ComfortGroup.Bed:
                    return "attach_bed";
                case Piece.ComfortGroup.Fire:
                    return "idle"; // No special animation for fire
                case Piece.ComfortGroup.Table:
                    return "idle"; // Standing at table
                case Piece.ComfortGroup.Carpet:
                    return "sit"; // Sit on carpet
                case Piece.ComfortGroup.Banner:
                case Piece.ComfortGroup.None:
                default:
                    return "idle";
            }
        }
        
        /// <summary>
        /// Gets the crafting animation trigger for a crafting station.
        /// </summary>
        public static string GetCraftingAnimation(CraftingStation station)
        {
            if (station == null) return "Working";
            
            string prefabName = Utils.GetPrefabName(station.gameObject).ToLower();
            
            // Different stations use different work animations
            if (prefabName.Contains("forge") || prefabName.Contains("blackforge"))
                return "Working"; // Hammering animation
            if (prefabName.Contains("cauldron"))
                return "Working"; // Stirring animation
            if (prefabName.Contains("workbench"))
                return "Working"; // General crafting
            if (prefabName.Contains("stonecutter"))
                return "Working"; // Chiseling animation
            
            return "Working";
        }
        
        /// <summary>
        /// Calculates a safe interaction position near a piece.
        /// </summary>
        public static Vector3 GetSafeInteractionPosition(PieceData data, float distance = 1.5f)
        {
            if (data == null || data.Piece == null) return Vector3.zero;
            
            Vector3 piecePos = data.Piece.transform.position;
            Vector3 pieceForward = data.Piece.transform.forward;
            
            // Try to find a position in front of the piece
            Vector3 frontPos = piecePos + pieceForward * distance;
            
            // For crafting stations, try to use the "in front" direction
            if (data.IsCraftingStation && data.CraftingStation != null)
            {
                // Most crafting stations face the player when used
                frontPos = piecePos + pieceForward * distance;
            }
            
            // For chairs, position is usually beside/in front
            if (data.IsChair && data.AttachPoint != null)
            {
                frontPos = data.AttachPoint.position;
            }
            
            return frontPos;
        }
        
        /// <summary>
        /// Checks if a piece is currently in use by another character.
        /// </summary>
        public static bool IsPieceInUse(PieceData data, Character excludeCharacter = null)
        {
            if (data == null) return false;
            
            // Check occupancy manager
            if (data.GameObject != null)
            {
                return InteractableOccupancyManager.IsOccupied(data.GameObject, excludeCharacter);
            }
            
            // For chairs, check the Chair component's in-use state
            if (data.IsChair && data.Chair != null)
            {
                // Chair doesn't have a direct "in use" check, so rely on occupancy manager
                return false;
            }
            
            return false;
        }
        
        /// <summary>
        /// Finds all pieces of a specific type within range.
        /// </summary>
        public static List<PieceData> FindPiecesInRange(Vector3 position, float range, System.Predicate<PieceData> filter = null)
        {
            var results = new List<PieceData>();
            var pieces = new List<Piece>();
            
            Piece.GetAllPiecesInRadius(position, range, pieces);
            
            foreach (var piece in pieces)
            {
                var data = ExtractPieceData(piece);
                if (data != null && (filter == null || filter(data)))
                {
                    results.Add(data);
                }
            }
            
            return results;
        }
        
        /// <summary>
        /// Finds the nearest crafting station of a specific type.
        /// </summary>
        public static PieceData FindNearestCraftingStation(Vector3 position, float range, string stationType = null)
        {
            var stations = FindPiecesInRange(position, range, d => d.IsCraftingStation);
            
            if (stations.Count == 0) return null;
            
            PieceData nearest = null;
            float nearestDist = float.MaxValue;
            
            foreach (var station in stations)
            {
                if (stationType != null && station.CraftingStationType != stationType)
                    continue;
                    
                float dist = Vector3.Distance(position, station.InteractionPosition);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = station;
                }
            }
            
            return nearest;
        }
        
        /// <summary>
        /// Finds the nearest available chair.
        /// </summary>
        public static PieceData FindNearestAvailableChair(Vector3 position, float range, Character excludeCharacter = null)
        {
            var chairs = FindPiecesInRange(position, range, d => d.IsChair);
            
            if (chairs.Count == 0) return null;
            
            PieceData nearest = null;
            float nearestDist = float.MaxValue;
            
            foreach (var chair in chairs)
            {
                if (IsPieceInUse(chair, excludeCharacter))
                    continue;
                    
                float dist = Vector3.Distance(position, chair.InteractionPosition);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = chair;
                }
            }
            
            return nearest;
        }
        
        #endregion
        
        #region Logging
        
        /// <summary>
        /// Logs detailed information about a piece for debugging.
        /// </summary>
        public static void LogPieceData(PieceData data, string context = "")
        {
            if (data == null)
            {
                Debug.Log($"[PieceDataHelper] {context} - No piece data");
                return;
            }
            
            Debug.Log($"[PieceDataHelper] {context} - Piece: {data.Name}\n" +
                $"  Category: {data.Category}, Comfort: {data.Comfort} ({data.ComfortGroup})\n" +
                $"  IsCraftingStation: {data.IsCraftingStation} ({data.CraftingStationType})\n" +
                $"  IsChair: {data.IsChair}, IsBed: {data.IsBed}\n" +
                $"  IsFireplace: {data.IsFireplace}, IsCookingStation: {data.IsCookingStation}\n" +
                $"  IsSmelter: {data.IsSmelter}, HasContainer: {data.HasContainer}\n" +
                $"  HasAttachPoint: {data.HasAttachPoint}, AttachAnimation: {data.AttachAnimation}\n" +
                $"  InteractionPos: {data.InteractionPosition}, Radius: {data.InteractionRadius}");
        }
        
        #endregion
    }
}
