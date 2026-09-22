using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using static FiresCore.Npc.CompanionInventory;

// NpcVisEquipment provides player-like model switching and color support

using FiresCore.Logging;
namespace FiresCore.Npc
{
    /// <summary>
    /// Random gear for wild companions, including hammer-placed ones: armor pieces, at least one weapon (possibly back
    /// slots too) and food, from vanilla or modded items, scaled to level and biome. A tamed companion keeps its gear
    /// until the player changes it.
    /// </summary>
    public class CompanionRandomLoadout : MonoBehaviour
    {
        #region Configuration
        
        [Header("Loadout Chances")]
        [Tooltip("Chance to spawn with a helmet (0-1)")]
        [Range(0f, 1f)]
        public float helmetChance = 0.4f;
        
        [Tooltip("Chance to spawn with chest armor (0-1)")]
        [Range(0f, 1f)]
        public float chestChance = 0.7f;
        
        [Tooltip("Chance to spawn with leg armor (0-1)")]
        [Range(0f, 1f)]
        public float legsChance = 0.6f;
        
        [Tooltip("Chance to spawn with a cape (0-1)")]
        [Range(0f, 1f)]
        public float capeChance = 0.3f;
        
        [Tooltip("Chance to spawn with shoulder armor (0-1)")]
        [Range(0f, 1f)]
        public float shoulderChance = 0.25f;
        
        [Tooltip("Chance to spawn with a shield (0-1)")]
        [Range(0f, 1f)]
        public float shieldChance = 0.4f;
        
        [Tooltip("Chance to have a secondary weapon in back slot (0-1)")]
        [Range(0f, 1f)]
        public float secondaryWeaponChance = 0.5f;
        
        [Tooltip("Chance to have a tertiary weapon in back slot 2 (0-1)")]
        [Range(0f, 1f)]
        public float tertiaryWeaponChance = 0.25f;
        
        [Header("Food Settings")]
        [Tooltip("Minimum food items to spawn with")]
        public int minFoodItems = 0;
        
        [Tooltip("Maximum food items to spawn with")]
        public int maxFoodItems = 3;
        
        [Header("Quality Settings")]
        [Tooltip("Minimum item quality level")]
        public int minQuality = 1;
        
        [Tooltip("Maximum item quality level")]
        public int maxQuality = 3;
        
        #endregion
        
        #region State
        
        /// <summary>Enable for debug logging of appearance/loadout operations</summary>
        public static bool VerboseLogging = false;
        
        private CompanionController _companion;
        private CompanionInventory _inventory;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionEquipmentData _equipmentData;
        private NpcVisEquipment _npcVisEquipment;
        private bool _hasGeneratedLoadout;
        private Vector3 _spawnPosition;
        private bool _isFemale;
        
        // Appearance data
        private Color _hairColor;
        private Color _skinColor;
        private Color _eyeColor;
        private string _hairStyle;
        private string _beardStyle;
        
        // Scale data
        private float _companionScale = 1.0f;
        private bool _isGiant; // Scale >= 1.15
        private bool _isDwarf; // Scale <= 0.65
        
        /// <summary>Minimum scale for companions (50%)</summary>
        public const float MIN_SCALE = 0.5f;
        /// <summary>Maximum scale for companions (130%)</summary>
        public const float MAX_SCALE = 1.3f;
        /// <summary>Scale threshold above which companion is considered a giant</summary>
        public const float GIANT_THRESHOLD = 1.15f;
        /// <summary>Scale threshold below which companion is considered a dwarf</summary>
        public const float DWARF_THRESHOLD = 0.65f;
        
        [Header("Wild Behavior")]
        [Tooltip("Wander radius for wild companions around their spawn point")]
        public float wildWanderRadius = 15f;
        
        // Cached item lists (populated once on first use)
        public static List<string> _allHelmets;
        public static List<string> _allChestArmor;
        public static List<string> _allLegArmor;
        public static List<string> _allCapes;
        public static List<string> _allShoulders;
        public static List<string> _allShields;
        public static List<string> _allWeapons;
        public static List<string> _allBows;
        public static List<string> _allArrows;
        public static List<string> _allFood;
        public static bool _itemListsCached;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _inventory = GetComponent<CompanionInventory>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _equipmentData = GetComponent<CompanionEquipmentData>();
            _npcVisEquipment = GetComponent<NpcVisEquipment>();
            
            // Store spawn position for wild behavior
            _spawnPosition = transform.position;
            
            // Ground the companion on spawn to prevent floating
            GroundCompanionOnSpawn();
        }
        
        private void Start()
        {
            // Check if we should generate a random loadout
            if (ShouldGenerateRandomLoadout())
            {
                // Wild companions spawn off-screen, so we can afford to wait well past
                // NpcVisEquipment.DelayedInitialize (T+2s) before running the loadout.
                // This guarantees ConfigureVisEquipment() has set up player models before
                // ApplyRandomHairAndBeard fires, eliminating the "always bald" race.
                // Hammer-placed companions are seen immediately and use a shorter delay.
                bool isWild = GetComponent<WildSpawn.WildCompanionDresser>() != null;
                float loadoutDelay = isWild ? 3.5f : 0.5f;

                Invoke(nameof(GenerateRandomLoadout), loadoutDelay);

                // Set up wild behavior (stay at spawn location)
                SetupWildBehavior();

                // Re-ground after a short delay to ensure physics is settled
                Invoke(nameof(GroundCompanionOnSpawn), 0.1f);
            }
            else
            {
                // This is an existing companion being loaded - restore model state from ZDO
                Invoke(nameof(RestoreModelStateFromZDO), 0.5f);

                // Also restore equipment if wild companion has appearance but lost equipment
                if (_companion != null && !_companion.isTamed)
                {
                    Invoke(nameof(RestoreEquipmentIfNeeded), 0.6f);

                    // Defensive name repair: if a wild companion was loaded with
                    // a placeholder name (e.g. its previous viking-name ZDO write
                    // was lost across save — known to happen when the rename
                    // races with the world save or when ZDO ownership of a
                    // SpawnSystem-owned creature gets reassigned), re-roll a
                    // viking name. Without this, the load path's RestoreModel
                    // branch never fixes a stuck name and the companion lives
                    // forever as "CompanionNpc_Wild" / "Companion NPC".
                    Invoke(nameof(RepairPlaceholderNameIfStuck), 0.7f);
                }
            }
            
            // For non-owner clients, also try to restore appearance from ZDO
            // This ensures multiplayer visual sync
            var nview = GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner())
            {
                // Additional delay for non-owners to ensure ZDO is synced
                Invoke(nameof(RestoreModelStateFromZDO), 1.5f);
            }
        }
        
        /// <summary>
        /// Restores equipment for wild companions that have appearance data but lost equipment.
        /// This can happen when companions despawn and respawn.
        /// </summary>
        private void RestoreEquipmentIfNeeded()
        {
            if (_inventory == null || _companion == null) return;
            if (_companion.isTamed) return; // Tamed companions use vault
            if (IsAuthoredNpcBody()) return; // authored statics/migrated NPCs never regen gear
            
            // Check if we have equipment - if not, we need to regenerate it
            if (!_inventory.HasAnyEquipment())
            {
                Debug.Log($"[CompanionRandomLoadout] Wild companion has appearance but no equipment, regenerating equipment only");
                
                // Cache the appearance data so we don't regenerate it
                _hasGeneratedLoadout = false; // Allow equipment generation
                
                // Ensure item lists are cached
                CacheItemLists();
                
                // Scale loadout based on current biome
                var biome = Heightmap.FindBiome(transform.position);
                _currentBiome = biome;
                
                // Regenerate equipment only (not appearance)
                GenerateRandomArmor();
                GenerateRandomWeapons();
                GenerateRandomFood();
                
                _hasGeneratedLoadout = true;
                
                // Save the loadout
                _inventory.SaveToZDO();
                
                // Refresh equipment data
                if (_equipmentData != null)
                {
                    _equipmentData.ForceRefresh();
                }
                
                Debug.Log($"[CompanionRandomLoadout] Regenerated equipment for {_companion.companionName}");
            }
        }
        
        /// <summary>
        /// Restores the female/male model state and appearance from ZDO for existing companions.
        /// Called when a companion is loaded from persistence, not when freshly generated.
        /// </summary>
        private void RestoreModelStateFromZDO()
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            // Authored NPC bodies store their look in npc_* keys and are restored by
            // StaticNpcInitializer — this companion-side restore reads companion_* keys, finds
            // nothing, and its bald-fallback then generated a FRESH RANDOM look (hair + skin +
            // gender) over migrated/template NPCs. Never run it for authored bodies.
            if (IsAuthoredNpcBody()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;
            
            // Ensure NpcVisEquipment is available
            if (_npcVisEquipment == null)
            {
                _npcVisEquipment = GetComponent<NpcVisEquipment>();
            }
            
            // Check if this companion was saved as female
            bool wasFemale = zdo.GetBool("companion_isfemale", false);
            int modelIndex = zdo.GetInt(ZDOVars.s_modelIndex, 0);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] RestoreModelStateFromZDO: wasFemale={wasFemale}, modelIndex={modelIndex}");
            
            _isFemale = wasFemale || modelIndex == 1;
            
            // Restore hair and beard
            string savedHair = zdo.GetString("companion_hair", "");
            string savedBeard = zdo.GetString("companion_beard", "");
            
            // Restore colors
            Vector3 hairColorVec = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
            Vector3 skinColorVec = zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one);
            Vector3 eyeColorVec = zdo.GetVec3("companion_eyeColor", new Vector3(0.4f, 0.3f, 0.2f)); // Default brown
            
            if (_npcVisEquipment != null)
            {
                // Apply colors
                _hairColor = new Color(hairColorVec.x, hairColorVec.y, hairColorVec.z);
                _skinColor = new Color(skinColorVec.x, skinColorVec.y, skinColorVec.z);
                _eyeColor = new Color(eyeColorVec.x, eyeColorVec.y, eyeColorVec.z);
                
                _npcVisEquipment.SetHairColor(hairColorVec);
                _npcVisEquipment.SetSkinColor(skinColorVec);
                _npcVisEquipment.SetEyeColor(_eyeColor);
                
                // Switch model via NpcVisEquipment
                _npcVisEquipment.SetModel(_isFemale ? 1 : 0);

                // Restore hair and beard using NpcFashionManager's direct bone-binding path
                string hairColorStr = FiresCore.Bridge.NpcFashionBridge.ColorToString(_hairColor);
                FiresCore.Bridge.NpcFashionBridge.ApplyHair(gameObject, savedHair, hairColorStr);
                string savedBeardForGender = _isFemale ? null : savedBeard;
                FiresCore.Bridge.NpcFashionBridge.ApplyBeard(gameObject, savedBeardForGender, hairColorStr);
                
                _hairStyle = savedHair;
                _beardStyle = savedBeard;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Restored appearance - Model: {(_isFemale ? 1 : 0)}, " +
                        $"Hair: {savedHair}, Beard: {savedBeard}, " +
                        $"HairColor: {_hairColor}, SkinColor: {_skinColor}, EyeColor: {_eyeColor}");
            }
            
            // Restore scale from ZDO
            RestoreScaleFromZDO();

            // If no hair was saved this is a companion that has never had appearance generated
            // (e.g. a freshly hammer-placed companion or a companion created before the hair
            // system existed). Generate appearance now so they aren't permanently bald.
            if (string.IsNullOrEmpty(savedHair) && _npcVisEquipment != null)
            {
                Debug.Log($"[CompanionRandomLoadout] No hair data in ZDO for {_companion?.companionName} - generating fresh appearance");

                // Derive gender from name if not already known
                if (!_isFemale && _companion != null && !string.IsNullOrEmpty(_companion.companionName))
                {
                    _isFemale = IsFemaleVikingName(_companion.companionName);
                    if (_isFemale)
                    {
                        _npcVisEquipment.SetModel(1);
                        var nviewFresh = GetComponent<ZNetView>();
                        nviewFresh?.GetZDO()?.Set(ZDOVars.s_modelIndex, 1);
                        nviewFresh?.GetZDO()?.Set("companion_isfemale", true);
                    }
                }

                // Schedule hair generation - NpcVisEquipment is already initialised
                // (we're in RestoreModelStateFromZDO which runs at T+0.5s, well after the
                // component's 2 s DelayedInitialize for static/tamed companions).
                Invoke(nameof(ApplyRandomHairAndBeard), 0.3f);
            }
        }
        
        /// <summary>
        /// Grounds the companion to the surface UNDERFOOT to prevent floating on spawn.
        /// Runs in Awake — i.e. inside Player.PlacePiece's Instantiate for hammer-placed
        /// NPCs — so it must respect whatever surface the piece was placed on. A physics
        /// probe (piece + terrain + static_solid layers) finds the real walkable surface:
        /// build-piece floors and custom terrain included. ZoneSystem.GetGroundHeight is
        /// HEIGHTMAP-ONLY — the old code snapped floor-placed NPCs through their floor to
        /// the dirt, and on custom terrain (heightmap disagrees with the actual surface by
        /// meters) it LIFTED fresh bodies 2-5m, which then poisoned every position stamped
        /// downstream (wander home, static feet-pin, anchor mint). It survives only as an
        /// underground rescue when the probe finds nothing at all.
        /// </summary>
        private void GroundCompanionOnSpawn()
        {
            Vector3 pos = transform.position;
            int surfaceMask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain", "Default_small");
            if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out var surfaceHit, 8f, surfaceMask, QueryTriggerInteraction.Ignore))
            {
                float feetError = pos.y - surfaceHit.point.y;
                if (feetError > 0.25f || feetError < -0.25f)
                {
                    Vector3 grounded = new Vector3(pos.x, surfaceHit.point.y + 0.05f, pos.z);
                    Debug.LogWarning($"[CompanionGround] '{name}' snapped to surface underfoot: {pos} -> {grounded} " +
                        $"(hit '{surfaceHit.collider.name}' layer={LayerMask.LayerToName(surfaceHit.collider.gameObject.layer)})");
                    transform.position = grounded;
                    _spawnPosition = grounded;
                }
            }
            else if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(pos, out float groundHeight)
                && pos.y < groundHeight - 2f)
            {
                // Probe found no surface within 8m below AND the body is buried under the
                // heightmap — true underground rescue (the one case the old code was for).
                Vector3 rescued = new Vector3(pos.x, groundHeight + 0.1f, pos.z);
                Debug.LogWarning($"[CompanionGround] '{name}' underground rescue: {pos} -> {rescued}");
                transform.position = rescued;
                _spawnPosition = rescued;
            }

            // Zero out any velocity on the rigidbody - only if NOT kinematic
            // Unity 6 doesn't allow setting velocity on kinematic rigidbodies
            var body = GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Manually triggers random loadout generation.
        /// </summary>
        public void GenerateRandomLoadout()
        {
            if (_hasGeneratedLoadout) return;
            if (_companion == null || _inventory == null) return;
            
            _hasGeneratedLoadout = true;
            
            // Generate random scale FIRST (before naming, so name can match size)
            GenerateRandomScale();
            
            // Assign a unique Viking name to wild companions (considers scale for giant/dwarf names)
            AssignRandomVikingName();

            // If AssignRandomVikingName() returned early because the name was pre-assigned
            // (e.g. WildCompanionDresser stamped a name before Start() ran), _isFemale was
            // never evaluated from the actual name. Derive it now so model and hair are correct.
            if (!_isFemale && _companion != null && !string.IsNullOrEmpty(_companion.companionName))
            {
                bool derivedFemale = IsFemaleVikingName(_companion.companionName, _isGiant, _isDwarf);
                if (derivedFemale)
                {
                    _isFemale = true;
                    bool isWildComp = GetComponent<WildSpawn.WildCompanionDresser>() != null;
                    Invoke(nameof(ApplyFemaleModel), isWildComp ? 0.5f : 0.1f);
                }
            }

            // Always schedule hair/beard generation here - this is the single authoritative
            // trigger regardless of how the name was assigned. The delay must be long enough
            // for NpcVisEquipment.DelayedInitialize (T+2s) to have completed.
            {
                bool isWildHair = GetComponent<WildSpawn.WildCompanionDresser>() != null;
                Invoke(nameof(ApplyRandomHairAndBeard), isWildHair ? 0.5f : 0.15f);
            }

            // Ensure item lists are cached
            CacheItemLists();
            
            // Scale loadout based on current biome
            var biome = Heightmap.FindBiome(transform.position);
            ScaleForBiome(biome);
            
            // Only log loadout generation when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Generating random loadout for {_companion.companionName} in biome {biome}");
            
            // Clear existing equipment first
            _inventory.ClearAllEquipment();
            
            // Generate armor
            GenerateRandomArmor();
            
            // Generate weapons (always at least one)
            GenerateRandomWeapons();
            
            // Generate food in storage
            GenerateRandomFood();
            
            // Save the loadout
            _inventory.SaveToZDO();
            
            // Only log loadout completion when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Completed random loadout for {_companion.companionName}");
            
            // CRITICAL: Notify equipment data system to refresh from inventory
            // This ensures wild companions can properly attack with their spawned weapons
            if (_equipmentData == null)
            {
                _equipmentData = GetComponent<CompanionEquipmentData>();
            }
            
            if (_equipmentData != null)
            {
                // Delay to ensure inventory is fully set up
                Invoke(nameof(RefreshEquipmentData), 0.2f);
            }
        }
        
        /// <summary>
        /// Refreshes the equipment data system after loadout generation.
        /// This is critical for wild companions to be able to attack properly.
        /// </summary>
        private void RefreshEquipmentData()
        {
            if (_equipmentData != null)
            {
                _equipmentData.ForceRefresh();
                // Only log equipment refresh when verbose logging is enabled to reduce spam
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Refreshed equipment data for {_companion?.companionName}");
            }
        }
        
        /// <summary>
        /// Generates a random scale for the companion (50% to 130%).
        /// Larger companions are giants, smaller ones are dwarves.
        /// </summary>
        private void GenerateRandomScale()
        {
            // Generate random scale between MIN_SCALE (0.5) and MAX_SCALE (1.3)
            // Use weighted distribution: more normal-sized companions, fewer extremes
            float randomValue = UnityEngine.Random.value;
            
            // Weighted distribution: bell curve centered around 0.9 (90% size)
            // This makes extreme sizes (giants and dwarves) rarer but still possible
            if (randomValue < 0.15f)
            {
                // 15% chance for dwarf (50% - 65%)
                _companionScale = UnityEngine.Random.Range(MIN_SCALE, DWARF_THRESHOLD);
                _isDwarf = true;
                _isGiant = false;
            }
            else if (randomValue > 0.85f)
            {
                // 15% chance for giant (115% - 130%)
                _companionScale = UnityEngine.Random.Range(GIANT_THRESHOLD, MAX_SCALE);
                _isGiant = true;
                _isDwarf = false;
            }
            else
            {
                // 70% chance for normal range (65% - 115%)
                _companionScale = UnityEngine.Random.Range(DWARF_THRESHOLD, GIANT_THRESHOLD);
                _isGiant = false;
                _isDwarf = false;
            }
            
            // Apply scale to the transform
            ApplyScale(_companionScale);
            
            // Save scale to ZDO for persistence
            SaveScaleToZDO();
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Generated scale {_companionScale:F2} for {_companion?.companionName} (Giant: {_isGiant}, Dwarf: {_isDwarf})");
        }
        
        /// <summary>
        /// Applies the specified scale to the companion transform.
        /// Also adjusts physics collider and health based on scale.
        /// This method is public so it can be called by CompanionRespawnManager during respawn.
        /// </summary>
        /// <param name="scale">The scale to apply (0.5 to 1.3)</param>
        /// <param name="skipHealthAdjustment">If true, skips health adjustment (use when health is already set from vault)</param>
        public void ApplyScale(float scale, bool skipHealthAdjustment = false)
        {
            if (_companion == null)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] ApplyScale called but _companion is null!");
                return;
            }
            
            // ALWAYS log scale application for debugging
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] ApplyScale START: scale={scale:F2}, skipHealth={skipHealthAdjustment}, companion={_companion.companionName}, currentLocalScale={transform.localScale}");
            
            // Update internal state fields so GetScale(), IsGiant(), IsDwarf() return correct values
            float oldScale = _companionScale;
            bool oldIsGiant = _isGiant;
            bool oldIsDwarf = _isDwarf;
            
            _companionScale = scale;
            _isGiant = scale >= GIANT_THRESHOLD;
            _isDwarf = scale <= DWARF_THRESHOLD;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] ApplyScale: Updated internal state - oldScale={oldScale:F2}->{_companionScale:F2}, isGiant={oldIsGiant}->{_isGiant}, isDwarf={oldIsDwarf}->{_isDwarf}");
            
            // Apply uniform scale to the root transform
            // All child objects (including bones and attached equipment) will inherit this scale
            Vector3 oldLocalScale = transform.localScale;
            transform.localScale = new Vector3(scale, scale, scale);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] ApplyScale: Transform scale changed from {oldLocalScale} to {transform.localScale}, lossyScale={transform.lossyScale}");
            
            // Adjust capsule collider height and radius proportionally
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                // Base values (from CompanionPrefabManager)
                float baseHeight = 1.8f;
                float baseRadius = 0.3f;
                float baseCenter = 0.9f;
                
                capsule.height = baseHeight * scale;
                capsule.radius = baseRadius * scale;
                capsule.center = new Vector3(0, baseCenter * scale, 0);
            }
            
            // NOTE: Health scaling based on scale is now handled by CompanionStats.
            // CompanionStats reads scale from this component via GetScale() and calculates
            // the health multiplier using GetHealthMultiplierFromRandomLoadout().
            // This prevents the "super high health" bug where health was scaled multiple times.
            // We only save scale to ZDO here; CompanionStats.RecalculateMaxStats() applies it.
            //
            // The skipHealthAdjustment parameter is now unused but kept for API compatibility.
            
            // Adjust rigidbody mass based on scale (affects physics interactions)
            var rigidbody = GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                float baseMass = 50f;
                // Mass scales with volume (scale^3) but we cap it for gameplay
                float massMultiplier = Mathf.Pow(scale, 2); // Use square instead of cube for balance
                rigidbody.mass = baseMass * massMultiplier;
            }
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] ApplyScale COMPLETE: {_companion.companionName} now at scale={_companionScale:F2}, localScale={transform.localScale}, isGiant={_isGiant}, isDwarf={_isDwarf}");
        }
        
        /// <summary>
        /// Saves the companion scale to ZDO for persistence.
        /// </summary>
        private void SaveScaleToZDO()
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                Debug.LogWarning($"[CompanionRandomLoadout] SaveScaleToZDO: nview invalid for {_companion?.companionName}");
                return;
            }
            
            var zdo = nview.GetZDO();
            if (zdo == null)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] SaveScaleToZDO: ZDO null for {_companion?.companionName}");
                return;
            }
            
            zdo.Set("companion_scale", _companionScale);
            zdo.Set("companion_isgiant", _isGiant);
            zdo.Set("companion_isdwarf", _isDwarf);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] SaveScaleToZDO: Saved scale={_companionScale:F2}, isGiant={_isGiant}, isDwarf={_isDwarf} for {_companion?.companionName}");
        }
        
        /// <summary>
        /// Restores the companion scale from ZDO.
        /// Called when loading existing companions.
        /// </summary>
        public void RestoreScaleFromZDO()
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: nview invalid for {_companion?.companionName}");
                return;
            }
            
            var zdo = nview.GetZDO();
            if (zdo == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: ZDO null for {_companion?.companionName}");
                return;
            }
            
            float savedScale = zdo.GetFloat("companion_scale", 1.0f);
            bool savedIsGiant = zdo.GetBool("companion_isgiant", false);
            bool savedIsDwarf = zdo.GetBool("companion_isdwarf", false);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: Read from ZDO - scale={savedScale:F2}, isGiant={savedIsGiant}, isDwarf={savedIsDwarf} for {_companion?.companionName}");
            
            // Only apply if scale was actually saved (non-default)
            if (Mathf.Abs(savedScale - 1.0f) > 0.01f || savedIsGiant || savedIsDwarf)
            {
                // Skip if already at the correct scale
                if (Mathf.Abs(_companionScale - savedScale) < 0.001f && 
                    _isGiant == savedIsGiant && _isDwarf == savedIsDwarf &&
                    Mathf.Abs(transform.localScale.x - savedScale) < 0.001f)
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: Already at correct scale {savedScale:F2}, skipping for {_companion?.companionName}");
                    return;
                }
                
                _companionScale = savedScale;
                _isGiant = savedIsGiant;
                _isDwarf = savedIsDwarf;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: Calling ApplyScale({_companionScale:F2}) for {_companion?.companionName}");
                ApplyScale(_companionScale, skipHealthAdjustment: true); // Skip health since we're restoring
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: COMPLETE - scale={_companionScale:F2}, localScale={transform.localScale} for {_companion?.companionName}");
            }
            else
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] RestoreScaleFromZDO: No non-default scale found, skipping for {_companion?.companionName}");
            }
        }
        
        /// <summary>
        /// Gets the current scale of this companion.
        /// </summary>
        public float GetScale() => _companionScale;
        
        /// <summary>
        /// Returns true if this companion is a giant (scale >= 1.15).
        /// </summary>
        public bool IsGiant() => _isGiant;
        
        /// <summary>
        /// Returns true if this companion is a dwarf (scale <= 0.65).
        /// </summary>
        public bool IsDwarf() => _isDwarf;
        
        /// <summary>
        /// Defensive load-path repair: if a wild companion came back from save
        /// with a placeholder name (the rename was either never applied or its
        /// ZDO write was lost), re-roll a viking name now and persist it.
        ///
        /// Runs only on the ZDO owner so we don't fight the network — non-owner
        /// peers will pick up the repaired name through the next ZDO sync.
        /// </summary>
        private void RepairPlaceholderNameIfStuck()
        {
            if (_companion == null) return;
            if (_companion.isTamed) return;

            // Only the ZDO owner is allowed to write authoritatively. If we're
            // not the owner, skip — the owner will run this same path locally.
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            string current = _companion.companionName ?? string.Empty;
            bool isPlaceholder =
                string.IsNullOrEmpty(current) ||
                current == "CompanionNpc" ||
                current == "CompanionNpc_Wild" ||
                current == "Companion NPC" ||
                current == "Companion";

            if (!isPlaceholder) return;

            // AssignRandomVikingName already gates on the same placeholder list,
            // so calling it directly does the right thing — picks a faction-
            // aware name when the dresser has stamped a faction, falls back to
            // the generic viking pool otherwise, and persists to ZDO.
            AssignRandomVikingName();

            if (FiresLogger.VerboseEnabled)
                Debug.Log($"[CompanionRandomLoadout] Repaired placeholder name " +
                          $"on load: '{current}' → '{_companion.companionName}'");
        }

        /// <summary>
        /// Assigns a random Viking-sounding name to wild companions.
        /// Uses giant/dwarf-appropriate names if the companion is scaled.
        /// </summary>
        private void AssignRandomVikingName()
        {
            if (_companion == null) return;
            if (_companion.isTamed) return; // Only for wild companions

            // Don't rename if already has a custom name. NOTE: "CompanionNpc_Wild"
            // is the prefab-baked name written by CompanionPrefabManager.SetupCompanionPrefab
            // for the wild-spawn variant; treat it as a default-prefab placeholder
            // (NOT a custom name) so this path will rename it. Without this entry
            // the AND chain skips the rename and the floating EnemyHud / hover label
            // ends up showing "CompanionNpc_Wild".
            if (!string.IsNullOrEmpty(_companion.companionName) && 
                _companion.companionName != "CompanionNpc" &&
                _companion.companionName != "CompanionNpc_Wild" &&
                _companion.companionName != "Companion NPC" &&
                _companion.companionName != "Companion")
            {
                return;
            }

            // Prefer the faction-aware name pool when the WildCompanionDresser
            // has already stamped a faction onto the ZDO. This gives Norse-pastoral
            // names to Neutral wanderers, guttural names to Bandits, occult names
            // to Cultists. Falls back to the generic Viking pool when no faction
            // info is present (e.g. dresser hasn't run yet, or this is a non-wild
            // CompanionNpc / Companion that hit one of the legacy default names).
            string vikingName = TryGetFactionAwareName() ?? GetRandomVikingName(_isGiant, _isDwarf);
            _companion.companionName = vikingName;
            _companion.UpdateCharacterName(vikingName);
            
            // Check if this is a female name and swap model if needed
            _isFemale = IsFemaleVikingName(vikingName, _isGiant, _isDwarf);
            if (_isFemale)
            {
                // Delay model swap to ensure all components are initialized.
                // Wild companions: longer delay so NpcVisEquipment is fully ready.
                bool isWild = GetComponent<WildSpawn.WildCompanionDresser>() != null;
                Invoke(nameof(ApplyFemaleModel), isWild ? 0.5f : 0.1f);
            }

            // NOTE: Hair/beard is now always scheduled by GenerateRandomLoadout() directly,
            // so we do NOT invoke ApplyRandomHairAndBeard here. This prevents the bug where
            // WildCompanionDresser pre-assigns the name, causing this method to return early
            // (name guard above) and hair to never fire.

            // Update ZDO
            var nview = _companion.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    zdo.Set("companion_name", vikingName);
                    zdo.Set(ZDOVars.s_tamedName, vikingName);
                    zdo.Set("companion_isfemale", _isFemale);
                }
            }
            
            // Only log name assignment when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Assigned Viking name: {vikingName} (Female: {_isFemale}, Giant: {_isGiant}, Dwarf: {_isDwarf})");
        }

        /// <summary>
        /// Reads the faction stamped on the ZDO by <see cref="WildSpawn.WildCompanionDresser"/>
        /// (key <c>companion_wild_faction</c>) and rolls a name from the matching
        /// <see cref="WildSpawn.CompanionNamePool"/>. Returns null when no faction
        /// has been recorded yet - caller falls back to the generic Viking pool.
        /// RNG is seeded from the ZDO UID so a given wild companion keeps the same
        /// name across zone reloads, matching the dresser's deterministic contract.
        /// </summary>
        private string TryGetFactionAwareName()
        {
            try
            {
                var nview = _companion?.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return null;
                var zdo = nview.GetZDO();
                if (zdo == null) return null;

                int factionInt = zdo.GetInt(WildSpawn.WildCompanionDresser.ZDO_FACTION, -1);
                if (factionInt < 0) return null;

                var faction = (WildSpawn.CompanionFaction)factionInt;
                int seed = unchecked(zdo.m_uid.GetHashCode() ^ 0x4E5F_27A1);
                var rng = new System.Random(seed);
                return WildSpawn.CompanionNamePool.Roll(faction, rng);
            }
            catch (Exception ex)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[CompanionRandomLoadout] TryGetFactionAwareName failed: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Gets a random Viking-sounding name.
        /// If isGiant is true, uses giant-appropriate names/epithets.
        /// If isDwarf is true, uses dwarf-appropriate names/epithets.
        /// </summary>
        private string GetRandomVikingName(bool isGiant, bool isDwarf)
        {
            // Traditional Norse/Viking male names
            string[] maleNames = {
                "Bjorn", "Erik", "Ragnar", "Leif", "Harald", "Olaf", "Gunnar", "Ivar",
                "Sigurd", "Thorsten", "Ulf", "Vidar", "Knut", "Sven", "Magnus", "Haldor",
                "Asmund", "Torbjorn", "Hakon", "Rolf", "Eirik", "Fenrir", "Odin", "Baldr",
                "Freyr", "Tyr", "Bragi", "Njord", "Heimdall", "Hodr", "Vali", "Vidir",
                "Agnar", "Arnfinn", "Birger", "Dag", "Egil", "Finn", "Gorm", "Halfdan",
                "Ingvar", "Jarl", "Ketil", "Leifr", "Magni", "Njal", "Orm", "Peder"
            };
            
            // Traditional Norse/Viking female names
            string[] femaleNames = {
                "Astrid", "Freya", "Ingrid", "Sigrid", "Helga", "Thora", "Brynhild", "Gudrun",
                "Ragnhild", "Solveig", "Eira", "Liv", "Saga", "Ylva", "Asa", "Hilda",
                "Sif", "Frigg", "Idunn", "Skuld", "Verdandi", "Urd", "Ran", "Skadi",
                "Gerd", "Sigyn", "Nanna", "Eir", "Var", "Vor", "Snotra", "Fulla",
                "Alfhild", "Bothild", "Dagny", "Embla", "Gunnhild", "Hervor", "Jorunn", "Kara"
            };
            
            // Giant-specific names (male)
            string[] giantMaleNames = {
                "Thrym", "Skrymir", "Utgard", "Hrungnir", "Thiazi", "Ymir", "Surtr", "Mimir",
                "Geirrod", "Vafthrudnir", "Hymir", "Bergelmir", "Angrboda", "Farbauti", "Gymir",
                "Bolthorn", "Hrimthurs", "Hraudung", "Gilling", "Baugi", "Suttung", "Thjazi",
                "Fjalar", "Galar", "Mokkurkalfi", "Grimnir"
            };
            
            // Giant-specific names (female)
            string[] giantFemaleNames = {
                "Angrboda", "Gunnlod", "Gerdr", "Grid", "Jarnsaxa", "Gjalp", "Greip", "Hyrrokkin",
                "Bestla", "Rind", "Skadi", "Gefjon", "Elli", "Fenja", "Menja", "Sinmara"
            };
            
            // Dwarf-specific names (traditionally male in Norse myth, but we'll include females)
            string[] dwarfMaleNames = {
                "Brokk", "Sindri", "Eitri", "Dvalin", "Durin", "Nyi", "Nordri", "Sudri",
                "Austri", "Vestri", "Alvis", "Andvari", "Fafnir", "Hreidmar", "Regin", "Otr",
                "Litr", "Nain", "Nidi", "Nori", "Ori", "Bifur", "Bofur", "Bombur",
                "Fili", "Kili", "Dori", "Gloin", "Thrain", "Thror", "Thorin", "Balin"
            };
            
            // Dwarf-specific names (female - adapted from Norse/fantasy traditions)
            string[] dwarfFemaleNames = {
                "Disa", "Dufa", "Nott", "Dagrun", "Gullveig", "Hlif", "Hrund", "Svanhild",
                "Thorvi", "Vigdis", "Asny", "Bergdis", "Grimhild", "Oddny", "Steinunn", "Thorunn"
            };
            
            // Standard epithets
            string[] standardEpithets = {
                "", "", "", "", "", // Empty entries for no epithet (more common)
                "the Bold", "the Brave", "the Swift", "the Strong", "the Wise",
                "the Fearless", "the Wanderer", "the Hunter", "the Shield", "the Axe",
                "Ironside", "Bloodaxe", "Fairhair", "Bluetooth", "Forkbeard",
                "the Red", "the Black", "the White", "the Grey", "the Silent"
            };
            
            // Giant-specific epithets
            string[] giantEpithets = {
                "the Colossal", "the Mighty", "the Towering", "the Thunderous", "Mountain-Born",
                "the Enormous", "the Titanic", "Stone-Crusher", "the Immense", "World-Shaker",
                "the Vast", "Cliff-Strider", "the Hulking", "the Tremendous", "Giant-Blood"
            };
            
            // Dwarf-specific epithets
            string[] dwarfEpithets = {
                "the Stout", "Iron-Forger", "Stone-Carver", "the Crafty", "Gold-Finder",
                "the Cunning", "Gem-Seeker", "the Delver", "Deep-Walker", "the Artificer",
                "Anvil-Born", "the Stubborn", "Ore-Master", "the Ingenious", "Cave-Dweller"
            };
            
            // Select name pool and epithet pool based on size
            string[] namePool;
            string[] epithetPool;
            
            // Randomly choose gender (50/50)
            bool isMale = UnityEngine.Random.value > 0.5f;
            
            if (isGiant)
            {
                namePool = isMale ? giantMaleNames : giantFemaleNames;
                epithetPool = giantEpithets;
            }
            else if (isDwarf)
            {
                namePool = isMale ? dwarfMaleNames : dwarfFemaleNames;
                epithetPool = dwarfEpithets;
            }
            else
            {
                namePool = isMale ? maleNames : femaleNames;
                epithetPool = standardEpithets;
            }
            
            string name = namePool[UnityEngine.Random.Range(0, namePool.Length)];
            string epithet = epithetPool[UnityEngine.Random.Range(0, epithetPool.Length)];
            
            if (!string.IsNullOrEmpty(epithet))
            {
                return $"{name} {epithet}";
            }
            
            return name;
        }
        
        /// <summary>
        /// Gets a random Viking-sounding name (standard version for non-scaled companions).
        /// </summary>
        public static string GetRandomVikingName()
        {
            // Traditional Norse/Viking male names
            string[] maleNames = {
                "Bjorn", "Erik", "Ragnar", "Leif", "Harald", "Olaf", "Gunnar", "Ivar",
                "Sigurd", "Thorsten", "Ulf", "Vidar", "Knut", "Sven", "Magnus", "Haldor",
                "Asmund", "Torbjorn", "Hakon", "Rolf", "Eirik", "Fenrir", "Odin", "Baldr",
                "Freyr", "Tyr", "Bragi", "Njord", "Heimdall", "Hodr", "Vali", "Vidir",
                "Agnar", "Arnfinn", "Birger", "Dag", "Egil", "Finn", "Gorm", "Halfdan",
                "Ingvar", "Jarl", "Ketil", "Leifr", "Magni", "Njal", "Orm", "Peder"
            };
            
            // Traditional Norse/Viking female names
            string[] femaleNames = {
                "Astrid", "Freya", "Ingrid", "Sigrid", "Helga", "Thora", "Brynhild", "Gudrun",
                "Ragnhild", "Solveig", "Eira", "Liv", "Saga", "Ylva", "Asa", "Hilda",
                "Sif", "Frigg", "Idunn", "Skuld", "Verdandi", "Urd", "Ran", "Skadi",
                "Gerd", "Sigyn", "Nanna", "Eir", "Var", "Vor", "Snotra", "Fulla",
                "Alfhild", "Bothild", "Dagny", "Embla", "Gunnhild", "Hervor", "Jorunn", "Kara"
            };
            
            // Optional epithets/titles
            string[] epithets = {
                "", "", "", "", "", // Empty entries for no epithet (more common)
                "the Bold", "the Brave", "the Swift", "the Strong", "the Wise",
                "the Fearless", "the Wanderer", "the Hunter", "the Shield", "the Axe",
                "Ironside", "Bloodaxe", "Fairhair", "Bluetooth", "Forkbeard",
                "the Red", "the Black", "the White", "the Grey", "the Silent"
            };
            
            // Randomly choose gender (50/50)
            bool isMale = UnityEngine.Random.value > 0.5f;
            string[] namePool = isMale ? maleNames : femaleNames;
            
            string name = namePool[UnityEngine.Random.Range(0, namePool.Length)];
            string epithet = epithets[UnityEngine.Random.Range(0, epithets.Length)];
            
            if (!string.IsNullOrEmpty(epithet))
            {
                return $"{name} {epithet}";
            }
            
            return name;
        }
        
        /// <summary>
        /// Checks if a name is from a female name pool.
        /// Considers giant and dwarf name pools as well.
        /// </summary>
        private static bool IsFemaleVikingName(string name, bool isGiant = false, bool isDwarf = false)
        {
            if (string.IsNullOrEmpty(name)) return false;
            
            // Extract first name (before any epithet)
            string firstName = name.Split(' ')[0];
            
            // Standard female names
            string[] femaleNames = {
                "Astrid", "Freya", "Ingrid", "Sigrid", "Helga", "Thora", "Brynhild", "Gudrun",
                "Ragnhild", "Solveig", "Eira", "Liv", "Saga", "Ylva", "Asa", "Hilda",
                "Sif", "Frigg", "Idunn", "Skuld", "Verdandi", "Urd", "Ran", "Skadi",
                "Gerd", "Sigyn", "Nanna", "Eir", "Var", "Vor", "Snotra", "Fulla",
                "Alfhild", "Bothild", "Dagny", "Embla", "Gunnhild", "Hervor", "Jorunn", "Kara"
            };
            
            // Giant female names
            string[] giantFemaleNames = {
                "Angrboda", "Gunnlod", "Gerdr", "Grid", "Jarnsaxa", "Gjalp", "Greip", "Hyrrokkin",
                "Bestla", "Rind", "Skadi", "Gefjon", "Elli", "Fenja", "Menja", "Sinmara"
            };
            
            // Dwarf female names
            string[] dwarfFemaleNames = {
                "Disa", "Dufa", "Nott", "Dagrun", "Gullveig", "Hlif", "Hrund", "Svanhild",
                "Thorvi", "Vigdis", "Asny", "Bergdis", "Grimhild", "Oddny", "Steinunn", "Thorunn"
            };
            
            // Check all pools
            foreach (var femaleName in femaleNames)
            {
                if (firstName.Equals(femaleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            foreach (var femaleName in giantFemaleNames)
            {
                if (firstName.Equals(femaleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            foreach (var femaleName in dwarfFemaleNames)
            {
                if (firstName.Equals(femaleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Legacy overload for backwards compatibility.
        /// </summary>
        public static bool IsFemaleVikingName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // Extract first name (before any epithet)
            string firstName = name.Split(' ')[0];

            string[] femaleNames = {
                "Astrid", "Freya", "Ingrid", "Sigrid", "Helga", "Thora", "Brynhild", "Gudrun",
                "Ragnhild", "Solveig", "Eira", "Liv", "Saga", "Ylva", "Asa", "Hilda",
                "Sif", "Frigg", "Idunn", "Skuld", "Verdandi", "Urd", "Ran", "Skadi",
                "Gerd", "Sigyn", "Nanna", "Eir", "Var", "Vor", "Snotra", "Fulla",
                "Alfhild", "Bothild", "Dagny", "Embla", "Gunnhild", "Hervor", "Jorunn", "Kara"
            };
            
            foreach (var femaleName in femaleNames)
            {
                if (firstName.Equals(femaleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Applies the female player model to this companion.
        /// Uses NpcVisEquipment.SetModel() which leverages Valheim's native model switching.
        /// Model index 0 = male, 1 = female.
        /// 
        /// Note: Hair and beard are applied separately in ApplyRandomHairAndBeard()
        /// which will batch them together with SetAppearance() for efficiency.
        /// </summary>
        private void ApplyFemaleModel()
        {
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] ApplyFemaleModel() called for {_companion?.companionName}");
            
            try
            {
                // Ensure NpcVisEquipment is initialized
                if (_npcVisEquipment == null)
                {
                    _npcVisEquipment = GetComponent<NpcVisEquipment>();
                }
                
                if (_npcVisEquipment == null)
                {
                    Debug.LogWarning($"[CompanionRandomLoadout] No NpcVisEquipment found for {_companion?.companionName}");
                    return;
                }
                
                // Just set the model here - hair/beard will be set in ApplyRandomHairAndBeard
                // which runs shortly after this via Invoke
                _npcVisEquipment.SetModel(1); // 1 = female model
                
                // Save to ZDO for persistence
                var nview = GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();
                    if (zdo != null)
                    {
                        zdo.Set(ZDOVars.s_modelIndex, 1);
                    }
                }
                
                // Only log model changes when verbose logging is enabled to reduce spam
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Successfully applied female model to {_companion?.companionName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionRandomLoadout] Failed to apply female model: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Finds all transforms with a given name (case-insensitive).
        /// </summary>
        private void FindAllTransformsWithName(Transform parent, string name, List<Transform> results)
        {
            if (parent == null) return;
            
            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(child);
                }
                FindAllTransformsWithName(child, name, results);
            }
        }
        
        /// <summary>
        /// Applies random hair, beard, hair color, skin color, and eye color to the companion.
        /// Uses NpcVisEquipment.SetAppearance() to batch model/hair/beard in one operation.
        /// Beards are only applied to male companions.
        /// </summary>
        private void ApplyRandomHairAndBeard()
        {
            try
            {
                // Ensure NpcVisEquipment is initialized
                if (_npcVisEquipment == null)
                {
                    _npcVisEquipment = GetComponent<NpcVisEquipment>();
                }
                
                if (_npcVisEquipment == null)
                {
                    Debug.LogWarning($"[CompanionRandomLoadout] No NpcVisEquipment found for hair/beard");
                    return;
                }
                
                // Generate random colors
                _hairColor = GetRandomHairColorAsColor();
                _skinColor = GetRandomSkinColorAsColor();
                _eyeColor = GetRandomEyeColorAsColor();
                
                // Apply colors using NpcVisEquipment
                _npcVisEquipment.SetHairColor(_hairColor);
                _npcVisEquipment.SetSkinColor(_skinColor);
                _npcVisEquipment.SetEyeColor(_eyeColor);
                
                // Get available hair styles
                var hairStyles = GetAvailableHairStyles();
                var beardStyles = GetAvailableBeardStyles();
                
                // Select random hair (skip HairNone sometimes for variety)
                _hairStyle = null;
                if (hairStyles.Count > 0 && UnityEngine.Random.value > 0.1f) // 90% chance to have hair
                {
                    // Filter out "HairNone"
                    var validHair = hairStyles.Where(h => h != "HairNone").ToList();
                    if (validHair.Count > 0)
                    {
                        _hairStyle = validHair[UnityEngine.Random.Range(0, validHair.Count)];
                    }
                }
                
                // Select random beard (males only, 70% chance)
                _beardStyle = null;
                if (!_isFemale && beardStyles.Count > 0 && UnityEngine.Random.value < 0.7f)
                {
                    // Filter out "BeardNone"
                    var validBeards = beardStyles.Where(b => b != "BeardNone").ToList();
                    if (validBeards.Count > 0)
                    {
                        _beardStyle = validBeards[UnityEngine.Random.Range(0, validBeards.Count)];
                    }
                }
                
                // Switch model (male/female) via NpcVisEquipment - this part still uses VisEquipment
                int modelIndex = _isFemale ? 1 : 0;
                _npcVisEquipment.SetModel(modelIndex);

                // Apply hair and beard using NpcFashionManager's direct bone-binding path.
                // This is the same code path that works for static NPCs via the dressing room.
                // VisEquipment.SetHairItem() requires m_isPlayer=true and an active update loop
                // to spawn hair prefabs, neither of which is reliable for companions.
                string hairColorStr = FiresCore.Bridge.NpcFashionBridge.ColorToString(_hairColor);
                FiresCore.Bridge.NpcFashionBridge.ApplyHair(gameObject, _hairStyle, hairColorStr);
                string beardForGender = _isFemale ? null : _beardStyle;
                FiresCore.Bridge.NpcFashionBridge.ApplyBeard(gameObject, beardForGender, hairColorStr);

                // Re-apply colors via NpcVisEquipment now that the meshes exist so it can
                // find and tint the NPC_Hair_* / NPC_Beard_* SkinnedMeshRenderers directly.
                _npcVisEquipment.SetHairColor(_hairColor);
                _npcVisEquipment.SetSkinColor(_skinColor);
                _npcVisEquipment.SetEyeColor(_eyeColor);
                
                // Save appearance data to ZDO for persistence
                var nview = GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();
                    if (zdo != null)
                    {
                        zdo.Set("companion_hair", _hairStyle ?? "");
                        zdo.Set("companion_beard", _beardStyle ?? "");
                        zdo.Set(ZDOVars.s_hairColor, new Vector3(_hairColor.r, _hairColor.g, _hairColor.b));
                        zdo.Set(ZDOVars.s_skinColor, new Vector3(_skinColor.r, _skinColor.g, _skinColor.b));
                        zdo.Set("companion_eyeColor", new Vector3(_eyeColor.r, _eyeColor.g, _eyeColor.b));
                        zdo.Set(ZDOVars.s_modelIndex, modelIndex);
                    }
                }
                
                // Only log appearance changes when verbose logging is enabled to reduce spam
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Applied appearance to {_companion?.companionName} - " +
                        $"Model: {modelIndex}, Hair: {_hairStyle ?? "none"}, Beard: {_beardStyle ?? "none"}, " +
                        $"HairColor: {_hairColor}, SkinColor: {_skinColor}, EyeColor: {_eyeColor}, Female: {_isFemale}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] Failed to apply hair/beard: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Gets a random hair color as a Color struct.
        /// </summary>
        public static Color GetRandomHairColorAsColor()
        {
            Color[] hairColors = {
                new Color(0.05f, 0.05f, 0.05f),   // Black
                new Color(0.15f, 0.12f, 0.08f),   // Dark brown
                new Color(0.3f, 0.2f, 0.15f),     // Brown
                new Color(0.4f, 0.3f, 0.2f),      // Light brown
                new Color(0.6f, 0.5f, 0.35f),     // Dirty blonde
                new Color(0.8f, 0.7f, 0.5f),      // Blonde
                new Color(0.5f, 0.3f, 0.2f),      // Auburn
                new Color(0.6f, 0.2f, 0.15f),     // Red
                new Color(0.4f, 0.4f, 0.4f),      // Grey
                new Color(0.8f, 0.8f, 0.8f),      // White/Silver
            };
            
            return hairColors[UnityEngine.Random.Range(0, hairColors.Length)];
        }
        
        /// <summary>
        /// Gets a random skin color as a Color struct.
        /// </summary>
        public static Color GetRandomSkinColorAsColor()
        {
            Color[] skinColors = {
                new Color(1.0f, 0.9f, 0.85f),     // Pale
                new Color(1.0f, 0.85f, 0.75f),    // Fair
                new Color(0.9f, 0.75f, 0.65f),    // Medium
                new Color(0.8f, 0.65f, 0.55f),    // Tan
                new Color(0.7f, 0.55f, 0.45f),    // Olive
                new Color(0.6f, 0.45f, 0.35f),    // Brown
                new Color(0.5f, 0.35f, 0.28f),    // Dark
            };
            
            return skinColors[UnityEngine.Random.Range(0, skinColors.Length)];
        }
        
        /// <summary>
        /// Gets a random eye color as a Color struct.
        /// Includes natural colors and some fantasy options.
        /// 
        /// Eye color is applied via custom eye material overlays (FiresEyes / FiresEyesFem)
        /// that are full player body materials with only the eye area visible.
        /// The material is tinted at runtime with the selected color.
        /// </summary>
        public static Color GetRandomEyeColorAsColor()
        {
            Color[] eyeColors = {
                // Natural eye colors (more common)
                new Color(0.4f, 0.3f, 0.2f),      // Brown (most common)
                new Color(0.4f, 0.3f, 0.2f),      // Brown (weighted)
                new Color(0.25f, 0.15f, 0.1f),    // Dark Brown
                new Color(0.55f, 0.45f, 0.25f),   // Hazel
                new Color(0.7f, 0.5f, 0.2f),      // Amber
                new Color(0.3f, 0.5f, 0.3f),      // Green
                new Color(0.3f, 0.5f, 0.7f),      // Blue
                new Color(0.3f, 0.5f, 0.7f),      // Blue (weighted)
                new Color(0.5f, 0.7f, 0.9f),      // Light Blue
                new Color(0.5f, 0.55f, 0.6f),     // Gray
                new Color(0.7f, 0.85f, 0.95f),    // Ice Blue
                // Fantasy colors (less common)
                new Color(0.85f, 0.7f, 0.2f),     // Gold
                new Color(0.75f, 0.78f, 0.82f),   // Silver
                new Color(0.5f, 0.3f, 0.6f),      // Violet
            };
            
            return eyeColors[UnityEngine.Random.Range(0, eyeColors.Length)];
        }
        
        // Fallback pool for the window before ObjectDB is populated -- in 1.0 the main menu runs ObjectDB.Awake with
        // zero items. GetAvailableHairStyles/GetAvailableBeardStyles prefer the live ObjectDB query over these.
        public static readonly string[] KnownHairStyles = {
            "HairNone",
            "Hair1","Hair2","Hair3","Hair4","Hair5","Hair6","Hair7","Hair8","Hair9","Hair10",
            "Hair11","Hair12","Hair13","Hair14","Hair15","Hair16","Hair17","Hair18","Hair19","Hair20",
            "Hair21","Hair22","Hair23","Hair24","Hair25","Hair26","Hair27","Hair28","Hair29","Hair30",
            "Hair31","Hair32","Hair33","Hair34","Hair35","Hair36","Hair37"
        };

        public static readonly string[] KnownBeardStyles = {
            "BeardNone","Beard1","Beard2","Beard3","Beard4","Beard5","Beard6","Beard7","Beard8",
            "Beard9","Beard10","Beard11","Beard12","Beard13","Beard14","Beard15","Beard16",
            "Beard17","Beard18","Beard19","Beard20","Beard21","Beard22","Beard23","Beard24",
            "Beard25","Beard26"
        };
        
        /// <summary>The one source of which hair styles exist, shared by the loadout roller, the static-NPC
        /// randomiser and the dressing room. Mirrors the vanilla barber exactly.</summary>
        public static List<string> GetAvailableHairStyles()
        {
            // Mirror the vanilla barber EXACTLY: enumerate ObjectDB Customization items whose prefab name
            // starts with "Hair" — the SAME pool PlayerCustomization shows, so modded customization
            // hairstyles are included automatically and stale/invalid names can never be rolled. (Case-
            // sensitive prefix excludes rig prefabs like "hair_11".) Fall back to the known vanilla list
            // only if ObjectDB isn't loaded yet.
            var objectDb = ObjectDB.instance;
            if (objectDb != null)
            {
                var names = objectDb.GetAllItems(ItemDrop.ItemData.ItemType.Customization, "Hair")
                              .Select(d => d.gameObject != null ? d.gameObject.name : null)
                              .Where(n => !string.IsNullOrEmpty(n))
                              .Distinct()
                              .ToList();
                if (names.Count > 0) return names;
            }
            return KnownHairStyles.Where(h => !string.IsNullOrEmpty(h)).ToList();
        }

        /// <summary>The one source of which beard styles exist. See GetAvailableHairStyles.</summary>
        public static List<string> GetAvailableBeardStyles()
        {
            var objectDb = ObjectDB.instance;
            if (objectDb != null)
            {
                var names = objectDb.GetAllItems(ItemDrop.ItemData.ItemType.Customization, "Beard")
                              .Select(d => d.gameObject != null ? d.gameObject.name : null)
                              .Where(n => !string.IsNullOrEmpty(n))
                              .Distinct()
                              .ToList();
                if (names.Count > 0) return names;
            }
            return KnownBeardStyles.Where(b => !string.IsNullOrEmpty(b)).ToList();
        }
        
        /// <summary>
        /// Forces regeneration of loadout (for testing/admin).
        /// </summary>
        public void ForceRegenerateLoadout()
        {
            _hasGeneratedLoadout = false;
            GenerateRandomLoadout();
        }

        /// <summary>True for AUTHORED NPC bodies — hammer-placed static NPCs and kg-migrated
        /// Marketplace NPCs. Their appearance/equipment is authored (dressing room / migration seed)
        /// and applied by the static restore path; the random generator must never touch them. Reads
        /// the ZDO flags first (reliable from the first frame on every machine), module as fallback.</summary>
        private bool IsAuthoredNpcBody()
        {
            var nview = GetComponent<ZNetView>();
            var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo != null && (zdo.GetBool("npc_static_placement", false)
                                || zdo.GetBool("npc_stationed", false)
                                || zdo.GetBool("npc_initialized", false))) return true;
            var module = GetComponent<FiresCore.Npc.NpcMode.CompanionNpcModule>();
            return module != null && (module.isStationedAsNpc || module.isStaticPlacement);
        }
        
        #endregion
        
        #region Loadout Generation
        
        private bool ShouldGenerateRandomLoadout()
        {
            if (_companion == null) return false;

            // Only generate for wild (untamed) companions
            if (_companion.isTamed) return false;

            // Authored NPC bodies (hammer-placed statics, kg-migrated Marketplace NPCs) NEVER roll
            // random gear — their look is authored and applied by the static restore path. This
            // generator was re-rolling random armor over migrated NPCs ("wild companion" false match).
            if (IsAuthoredNpcBody()) return false;
            
            // Check if already has equipment
            if (_inventory != null && _inventory.HasAnyEquipment())
            {
                return false;
            }
            
            // Check if appearance was already generated (even if equipment is missing)
            // This prevents regenerating appearance on each reload
            var nview = GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    // Only actual appearance ZDO data indicates that generation already ran.
                    // A companion name is set early by WildCompanionDresser before
                    // GenerateRandomLoadout fires, so using the name as an indicator
                    // would always short-circuit generation for wild companions.
                    bool hasAppearance = !string.IsNullOrEmpty(zdo.GetString("companion_hair", "")) ||
                                        zdo.GetBool("companion_isfemale", false);
                    
                    if (hasAppearance)
                    {
                        Debug.Log($"[CompanionRandomLoadout] Companion already has appearance data, skipping generation");
                        return false;
                    }
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Sets up wild companion behavior - they will stay near their spawn point
        /// and wander around it, engaging enemies that come near.
        /// </summary>
        private void SetupWildBehavior()
        {
            if (_companion == null) return;
            if (_companion.isTamed) return;
            
            // Set home position for idle behavior
            if (_idleBehavior != null)
            {
                // Scale wander radius based on biome
                float radius = wildWanderRadius * GetBiomeMultiplier(_currentBiome);
                _idleBehavior.SetHomePosition(_spawnPosition, radius);
                
                // Enable wandering by setting high wander chance
                _idleBehavior.idleWanderChance = 0.5f;
                _idleBehavior.useSoftWanderLimit = true;
                
                Debug.Log($"[CompanionRandomLoadout] Set up wild behavior for {_companion.companionName} " +
                    $"at {_spawnPosition}, wander radius: {radius}m");
            }
        }
        
        /// <summary>
        /// Called when the companion is tamed - clears wild behavior.
        /// </summary>
        public void OnTamed()
        {
            if (_idleBehavior != null)
            {
                _idleBehavior.ClearHomePosition();
            }
        }
        
        private Heightmap.Biome _currentBiome = Heightmap.Biome.Meadows;
        
        private void GenerateRandomArmor()
        {
            int quality = UnityEngine.Random.Range(minQuality, maxQuality + 1);
            
            // Get tier-appropriate items
            var helmets = GetTierFilteredItems(_allHelmets, _currentBiome);
            var chests = GetTierFilteredItems(_allChestArmor, _currentBiome);
            var legs = GetTierFilteredItems(_allLegArmor, _currentBiome);
            var capes = GetTierFilteredItems(_allCapes, _currentBiome);
            var shoulders = GetTierFilteredItems(_allShoulders, _currentBiome);
            
            // Helmet
            if (UnityEngine.Random.value < helmetChance && helmets.Count > 0)
            {
                string helmet = helmets[UnityEngine.Random.Range(0, helmets.Count)];
                TryEquipItem(EquipmentSlot.Helmet, helmet, quality);
            }
            
            // Chest
            if (UnityEngine.Random.value < chestChance && chests.Count > 0)
            {
                string chest = chests[UnityEngine.Random.Range(0, chests.Count)];
                TryEquipItem(EquipmentSlot.Chest, chest, quality);
            }
            
            // Legs
            if (UnityEngine.Random.value < legsChance && legs.Count > 0)
            {
                string leg = legs[UnityEngine.Random.Range(0, legs.Count)];
                TryEquipItem(EquipmentSlot.Legs, leg, quality);
            }
            
            // Cape/Shoulder
            if (UnityEngine.Random.value < capeChance && capes.Count > 0)
            {
                string cape = capes[UnityEngine.Random.Range(0, capes.Count)];
                TryEquipItem(EquipmentSlot.Shoulder, cape, quality);
            }
            else if (UnityEngine.Random.value < shoulderChance && shoulders.Count > 0)
            {
                string shoulder = shoulders[UnityEngine.Random.Range(0, shoulders.Count)];
                TryEquipItem(EquipmentSlot.Shoulder, shoulder, quality);
            }
        }
        
        private void GenerateRandomWeapons()
        {
            // Get tier-appropriate items
            var weapons = GetTierFilteredItems(_allWeapons, _currentBiome);
            var bows = GetTierFilteredItems(_allBows, _currentBiome);
            var shields = GetTierFilteredItems(_allShields, _currentBiome);
            
            if (weapons.Count == 0 && bows.Count == 0) return;
            
            int quality = UnityEngine.Random.Range(minQuality, maxQuality + 1);
            
            // Primary weapon (always) - check if it's a bow/crossbow to equip correctly
            string primaryWeapon;
            bool primaryIsBow = false;
            
            // Combine weapons and bows for selection, but track which type was selected
            var allPrimaryOptions = new List<string>(weapons);
            allPrimaryOptions.AddRange(bows);
            
            if (allPrimaryOptions.Count == 0) return;
            
            primaryWeapon = allPrimaryOptions[UnityEngine.Random.Range(0, allPrimaryOptions.Count)];
            primaryIsBow = IsBowOrCrossbow(primaryWeapon);
            
            // CRITICAL: Bows go in LeftHand (they are TwoHandedWeaponLeft), melee goes in RightHand
            if (primaryIsBow)
            {
                TryEquipItem(EquipmentSlot.LeftHand, primaryWeapon, quality);
                
                // Add biome-appropriate arrows to storage for bow users
                AddBiomeArrowsToStorage();
            }
            else
            {
                TryEquipItem(EquipmentSlot.RightHand, primaryWeapon, quality);
            }
            
            // Determine weapon type for shield compatibility
            bool primaryIsTwoHanded = IsWeaponTwoHanded(primaryWeapon);
            
            // Shield (only if primary is one-handed melee)
            if (!primaryIsTwoHanded && !primaryIsBow && UnityEngine.Random.value < shieldChance && shields.Count > 0)
            {
                string shield = shields[UnityEngine.Random.Range(0, shields.Count)];
                TryEquipItem(EquipmentSlot.LeftHand, shield, quality);
            }
            
            // Secondary weapon in back slot - prefer ranged if primary is melee, melee if primary is ranged
            if (UnityEngine.Random.value < secondaryWeaponChance)
            {
                string secondaryWeapon = null;
                bool secondaryIsBow = false;
                
                // If primary is melee, prefer a bow as secondary
                if (!primaryIsBow && bows.Count > 0 && UnityEngine.Random.value < 0.6f)
                {
                    secondaryWeapon = bows[UnityEngine.Random.Range(0, bows.Count)];
                    secondaryIsBow = true;
                }
                // Otherwise pick from regular weapons
                else if (weapons.Count > 0)
                {
                    secondaryWeapon = weapons[UnityEngine.Random.Range(0, weapons.Count)];
                }
                
                // Avoid duplicates and equip to correct back slot
                if (!string.IsNullOrEmpty(secondaryWeapon) && secondaryWeapon != primaryWeapon)
                {
                    // Bows go to LeftBack, melee to RightBack
                    if (secondaryIsBow || IsBowOrCrossbow(secondaryWeapon))
                    {
                        TryEquipItem(EquipmentSlot.LeftBack, secondaryWeapon, quality);
                        
                        // If we added a bow as secondary, also add arrows
                        if (!primaryIsBow)
                        {
                            AddBiomeArrowsToStorage();
                        }
                    }
                    else
                    {
                        TryEquipItem(EquipmentSlot.RightBack, secondaryWeapon, quality);
                    }
                }
            }
            
            // Tertiary weapon in remaining back slot
            if (UnityEngine.Random.value < tertiaryWeaponChance && weapons.Count > 0)
            {
                string tertiaryWeapon = weapons[UnityEngine.Random.Range(0, weapons.Count)];
                if (tertiaryWeapon != primaryWeapon)
                {
                    // Use whichever back slot is still free
                    if (primaryIsBow)
                    {
                        TryEquipItem(EquipmentSlot.RightBack, tertiaryWeapon, quality);
                    }
                    else
                    {
                        TryEquipItem(EquipmentSlot.LeftBack, tertiaryWeapon, quality);
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if a weapon is a bow or crossbow.
        /// </summary>
        private bool IsBowOrCrossbow(string prefabName)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return false;
                
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData?.m_shared == null) return false;
                
                var shared = itemDrop.m_itemData.m_shared;
                return shared.m_itemType == ItemDrop.ItemData.ItemType.Bow ||
                       shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                       shared.m_skillType == Skills.SkillType.Bows ||
                       shared.m_skillType == Skills.SkillType.Crossbows;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Adds biome-appropriate arrows to the companion's storage.
        /// </summary>
        private void AddBiomeArrowsToStorage()
        {
            string arrowType = GetBiomeArrowType(_currentBiome);
            if (string.IsNullOrEmpty(arrowType)) return;
            
            // Add 20-50 arrows
            int arrowCount = UnityEngine.Random.Range(20, 51);
            TryAddToStorage(arrowType, arrowCount);
            
            // Only log arrow additions when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Added {arrowCount}x {arrowType} for bow user");
        }
        
        /// <summary>
        /// Gets the appropriate arrow type for a biome.
        /// </summary>
        private string GetBiomeArrowType(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return "ArrowWood";
                case Heightmap.Biome.BlackForest:
                    return UnityEngine.Random.value < 0.5f ? "ArrowFlint" : "ArrowBronze";
                case Heightmap.Biome.Swamp:
                    return UnityEngine.Random.value < 0.5f ? "ArrowIron" : "ArrowPoison";
                case Heightmap.Biome.Mountain:
                    return UnityEngine.Random.value < 0.5f ? "ArrowObsidian" : "ArrowFrost";
                case Heightmap.Biome.Plains:
                    return UnityEngine.Random.value < 0.5f ? "ArrowNeedle" : "ArrowFire";
                case Heightmap.Biome.Mistlands:
                    return "ArrowCarapace";
                case Heightmap.Biome.AshLands:
                case Heightmap.Biome.DeepNorth:
                    return "ArrowCarapace"; // Best available
                default:
                    return "ArrowWood";
            }
        }
        
        private void GenerateRandomFood()
        {
            var food = GetTierFilteredItems(_allFood, _currentBiome);
            if (food.Count == 0) return;
            
            int foodCount = UnityEngine.Random.Range(minFoodItems, maxFoodItems + 1);
            
            var storageInventory = _inventory?.GetStorageInventory();
            if (storageInventory == null) return;
            
            for (int i = 0; i < foodCount; i++)
            {
                string foodPrefab = food[UnityEngine.Random.Range(0, food.Count)];
                TryAddToStorage(foodPrefab, UnityEngine.Random.Range(1, 5));
            }
        }
        
        private void TryEquipItem(EquipmentSlot slot, string prefabName, int quality)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            if (_inventory == null) return;
            
            // Validate the item is a proper equippable item
            if (!IsValidEquippableItem(prefabName))
            {
                Debug.LogWarning($"[CompanionRandomLoadout] Skipping invalid item {prefabName} for slot {slot}");
                return;
            }
            
            try
            {
                _inventory.EquipItem(slot, prefabName, quality);
                // Only log equipment when verbose logging is enabled to reduce spam
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Equipped {prefabName} (quality {quality}) to {slot}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] Failed to equip {prefabName} to {slot}: {ex.Message}");
            }
        }
        
        private void TryAddToStorage(string prefabName, int amount)
        {
            if (string.IsNullOrEmpty(prefabName)) return;
            
            try
            {
                var storageInventory = _inventory?.GetStorageInventory();
                if (storageInventory == null) return;
                
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return;
                
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData == null) return;
                
                // Clone the item data
                var itemData = itemDrop.m_itemData.Clone();
                itemData.m_stack = Mathf.Min(amount, itemData.m_shared.m_maxStackSize);
                
                // CRITICAL: Set m_dropPrefab so Inventory.Save() uses the correct prefab name
                // Without this, Clone() may not preserve the prefab reference, causing
                // Inventory.Save() to fall back to m_shared.m_name (the localization key like $item_xxx)
                // which then fails to load because ObjectDB.GetItemPrefab() doesn't find localized names.
                itemData.m_dropPrefab = prefab;
                
                storageInventory.AddItem(itemData);
                // Only log storage additions when verbose logging is enabled to reduce spam
                if (VerboseLogging)
                    Debug.Log($"[CompanionRandomLoadout] Added {amount}x {prefabName} to storage");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] Failed to add {prefabName} to storage: {ex.Message}");
            }
        }
        
        private bool IsWeaponTwoHanded(string prefabName)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return false;
                
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData?.m_shared == null) return false;
                
                // Check item type and properties
                var shared = itemDrop.m_itemData.m_shared;
                
                // Two-handed weapons
                if (shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                    shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                    shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                {
                    return true;
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }
        
        #endregion
        
        #region Item List Caching
        
        public static void CacheItemLists()
        {
            if (_itemListsCached) return;

            _allHelmets = new List<string>();
            _allChestArmor = new List<string>();
            _allLegArmor = new List<string>();
            _allCapes = new List<string>();
            _allShoulders = new List<string>();
            _allShields = new List<string>();
            _allWeapons = new List<string>();
            _allBows = new List<string>();
            _allArrows = new List<string>();
            _allFood = new List<string>();

            try
            {
                // Get all prefabs from ObjectDB
                var objectDB = ObjectDB.instance;
                if (objectDB == null)
                {
                    Debug.LogWarning("[CompanionRandomLoadout] ObjectDB not available for item caching");
                    return;
                }

                // ObjectDB.Awake fires once for the start-scene's stub item list
                // (only a handful of menu items) and then again with the real
                // item set once the world scene loads. Refuse to cache against
                // the stub - otherwise _itemListsCached latches as empty and
                // every wild companion spawns naked forever.
                if (objectDB.m_items == null || objectDB.m_items.Count < 50)
                {
                    Debug.Log($"[CompanionRandomLoadout] ObjectDB only has " +
                        $"{objectDB.m_items?.Count ?? 0} items - too few to cache, " +
                        $"will retry on next call (likely start-scene stub).");
                    return;
                }
                
                int skippedInvalid = 0;
                int skippedFiltered = 0;
                
                foreach (var prefab in objectDB.m_items)
                {
                    if (prefab == null) continue;
                    
                    var itemDrop = prefab.GetComponent<ItemDrop>();
                    if (itemDrop?.m_itemData?.m_shared == null) continue;
                    
                    var shared = itemDrop.m_itemData.m_shared;
                    string prefabName = prefab.name;
                    
                    // Skip certain items based on name/properties
                    if (ShouldSkipItem(shared, prefabName))
                    {
                        skippedFiltered++;
                        continue;
                    }
                    
                    // Validate the item is a proper equippable (has attach points, animations, etc.)
                    // Skip validation for consumables and ammo as they don't need visual attachment
                    if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable &&
                        shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo)
                    {
                        if (!IsValidEquippableItem(prefabName))
                        {
                            skippedInvalid++;
                            continue;
                        }
                    }
                    
                    // Categorize by item type
                    switch (shared.m_itemType)
                    {
                        case ItemDrop.ItemData.ItemType.Helmet:
                            _allHelmets.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Chest:
                            _allChestArmor.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Legs:
                            _allLegArmor.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Shoulder:
                            // Distinguish between capes and shoulder armor
                            if (IsCape(shared, prefabName))
                                _allCapes.Add(prefabName);
                            else
                                _allShoulders.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Shield:
                            _allShields.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                        case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                            _allWeapons.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                        case ItemDrop.ItemData.ItemType.Bow:
                            _allBows.Add(prefabName);
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Ammo:
                            // Only cache arrows (not bolts for now)
                            if (prefabName.ToLowerInvariant().Contains("arrow"))
                            {
                                _allArrows.Add(prefabName);
                            }
                            break;
                            
                        case ItemDrop.ItemData.ItemType.Consumable:
                            if (IsFood(shared))
                                _allFood.Add(prefabName);
                            break;
                    }
                }
                
                int totalCached =
                    _allHelmets.Count + _allChestArmor.Count + _allLegArmor.Count +
                    _allCapes.Count + _allShoulders.Count + _allShields.Count +
                    _allWeapons.Count + _allBows.Count + _allArrows.Count + _allFood.Count;

                // Only latch the cache if we actually produced something. If
                // everything got filtered out the ObjectDB was almost certainly
                // not fully populated yet - leave the flag false so the next
                // caller (e.g. wild companion spawn) re-runs the scan once a
                // real item set is available.
                if (totalCached > 0)
                    _itemListsCached = true;

                Debug.Log($"[CompanionRandomLoadout] Cached items: " +
                    $"{_allHelmets.Count} helmets, " +
                    $"{_allChestArmor.Count} chest, " +
                    $"{_allLegArmor.Count} legs, " +
                    $"{_allCapes.Count} capes, " +
                    $"{_allShoulders.Count} shoulders, " +
                    $"{_allShields.Count} shields, " +
                    $"{_allWeapons.Count} melee weapons, " +
                    $"{_allBows.Count} bows/crossbows, " +
                    $"{_allArrows.Count} arrow types, " +
                    $"{_allFood.Count} food " +
                    $"(skipped {skippedFiltered} filtered, {skippedInvalid} invalid)" +
                    (totalCached > 0 ? "" : " [NOT LATCHED - will retry]"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CompanionRandomLoadout] Failed to cache item lists: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets items appropriate for a given biome tier.
        /// </summary>
        public static List<string> GetTierFilteredItems(List<string> allItems, Heightmap.Biome biome)
        {
            int maxTier = GetBiomeTier(biome);
            
            return allItems.Where(prefabName => 
            {
                int itemTier = GetItemTier(prefabName);
                return itemTier <= maxTier;
            }).ToList();
        }
        
        public static int GetBiomeTier(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return 1;
                case Heightmap.Biome.BlackForest:
                    return 2;
                case Heightmap.Biome.Swamp:
                    return 3;
                case Heightmap.Biome.Mountain:
                    return 4;
                case Heightmap.Biome.Plains:
                    return 5;
                case Heightmap.Biome.Mistlands:
                    return 6;
                case Heightmap.Biome.AshLands:
                case Heightmap.Biome.DeepNorth:
                    return 7;
                default:
                    return 3; // Default to mid-tier
            }
        }
        
        public static int GetItemTier(string prefabName)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return 1;
                
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData?.m_shared == null) return 1;
                
                var shared = itemDrop.m_itemData.m_shared;
                string nameLower = prefabName.ToLowerInvariant();
                
                // Tier based on material/name keywords
                if (nameLower.Contains("dvergr") || nameLower.Contains("carapace") || 
                    nameLower.Contains("fenring") || nameLower.Contains("staff"))
                    return 6; // Mistlands
                    
                if (nameLower.Contains("black") || nameLower.Contains("padded") || 
                    nameLower.Contains("needle") || nameLower.Contains("fang"))
                    return 5; // Plains
                    
                if (nameLower.Contains("wolf") || nameLower.Contains("silver") || 
                    nameLower.Contains("obsidian") || nameLower.Contains("draugr"))
                    return 4; // Mountain
                    
                if (nameLower.Contains("iron") || nameLower.Contains("ancient") || 
                    nameLower.Contains("root"))
                    return 3; // Swamp
                    
                if (nameLower.Contains("bronze") || nameLower.Contains("troll"))
                    return 2; // Black Forest
                    
                if (nameLower.Contains("leather") || nameLower.Contains("rag") || 
                    nameLower.Contains("wood") || nameLower.Contains("flint") ||
                    nameLower.Contains("club") || nameLower.Contains("torch"))
                    return 1; // Meadows
                
                // Fallback: estimate by armor/damage values
                float armorValue = shared.m_armor;
                float damageValue = shared.m_damages.GetTotalDamage();
                float combinedValue = armorValue + damageValue;
                
                if (combinedValue > 100) return 6;
                if (combinedValue > 70) return 5;
                if (combinedValue > 50) return 4;
                if (combinedValue > 30) return 3;
                if (combinedValue > 15) return 2;
                return 1;
            }
            catch
            {
                return 1;
            }
        }
        
        private static bool ShouldSkipItem(ItemDrop.ItemData.SharedData shared, string prefabName)
        {
            // Skip items that aren't meant to be equipped
            if (shared == null) return true;
            
            // Skip items with no icons or invalid icons (prevents IndexOutOfRangeException in GetIcon)
            if (shared.m_icons == null || shared.m_icons.Length == 0)
            {
                return true;
            }
            
            // Skip items with empty or default names (likely internal/debug items)
            if (string.IsNullOrEmpty(shared.m_name) || shared.m_name.StartsWith("$item_"))
            {
                // Items with localization keys that don't resolve are usually internal
                var localizedName = Localization.instance?.Localize(shared.m_name);
                if (string.IsNullOrEmpty(localizedName) || localizedName == shared.m_name)
                {
                    // Allow it if it has a valid m_name that's just a localization key
                    // but skip if the name is truly empty or generic
                }
            }
            
            // Skip boss items or special items
            string nameLower = prefabName.ToLowerInvariant();
            if (nameLower.Contains("boss") || 
                nameLower.Contains("trophy") || 
                nameLower.Contains("torn") ||
                nameLower.Contains("broken") ||
                nameLower.Contains("wishbone") ||
                nameLower.Contains("megingjord") ||
                nameLower.Contains("belt"))
            {
                return true;
            }
            
            // Skip monster/creature weapons that players can't use
            // These are internal game items not meant for humanoid NPCs
            if (nameLower.Contains("troll") ||
                nameLower.Contains("twitcher") ||
                nameLower.Contains("goblin") && !nameLower.Contains("totem") ||
                nameLower.Contains("draugr") && nameLower.Contains("bow") ||
                nameLower.Contains("skeleton") ||
                nameLower.Contains("greydwarf") ||
                nameLower.Contains("blob") ||
                nameLower.Contains("wraith") ||
                nameLower.Contains("fenring") && nameLower.Contains("claw") ||
                nameLower.Contains("ulv") ||
                nameLower.Contains("hatchling") ||
                nameLower.Contains("drake") ||
                nameLower.Contains("seeker") && !nameLower.Contains("aspic") ||
                nameLower.Contains("gjall") ||
                nameLower.Contains("tick") ||
                nameLower.Contains("dvergr") && (nameLower.Contains("sting") || nameLower.Contains("throw")))
            {
                return true;
            }
            
            // Skip creature-specific attack prefabs (these have attack_ prefix or _attack suffix)
            if (nameLower.StartsWith("attack_") || nameLower.EndsWith("_attack") ||
                nameLower.Contains("_projectile") || nameLower.Contains("projectile_"))
            {
                return true;
            }
            
            // Skip items with no armor value and no damage (likely decorative)
            if (shared.m_armor <= 0 && shared.m_damages.GetTotalDamage() <= 0 && 
                shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
            {
                // Exception for capes which may have other effects
                if (shared.m_itemType != ItemDrop.ItemData.ItemType.Shoulder)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Validates that a prefab is a proper equippable item for humanoid NPCs.
        /// Checks for ItemDrop, valid shared data, proper item type, and visual attachments.
        /// </summary>
        private static bool IsValidEquippableItem(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return false;
                
                // Must have ItemDrop component
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop == null) return false;
                
                // Must have valid item data
                var itemData = itemDrop.m_itemData;
                if (itemData == null) return false;
                
                // Must have shared data
                var shared = itemData.m_shared;
                if (shared == null) return false;
                
                // Must have valid icons (player-usable items always have icons)
                if (shared.m_icons == null || shared.m_icons.Length == 0) return false;
                
                // For weapons, check for proper attack animations
                if (shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                    shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                    shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                    shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                {
                    // Player weapons have attack animations defined
                    if (shared.m_attack == null && shared.m_secondaryAttack == null)
                    {
                        return false;
                    }
                    
                    // Check that at least primary attack has animation
                    if (shared.m_attack != null && string.IsNullOrEmpty(shared.m_attack.m_attackAnimation))
                    {
                        // Some weapons only have secondary attacks, that's okay
                        if (shared.m_secondaryAttack == null || 
                            string.IsNullOrEmpty(shared.m_secondaryAttack.m_attackAnimation))
                        {
                            return false;
                        }
                    }
                }
                
                // Check for visual attachment point (player items have attach or attach_skin children)
                bool hasAttachPoint = false;
                foreach (Transform child in prefab.transform)
                {
                    string childName = child.name.ToLowerInvariant();
                    if (childName.StartsWith("attach"))
                    {
                        hasAttachPoint = true;
                        break;
                    }
                }
                
                // For armor/equipment, must have attach point
                if (!hasAttachPoint)
                {
                    // Consumables don't need attach points
                    if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable &&
                        shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo &&
                        shared.m_itemType != ItemDrop.ItemData.ItemType.Material)
                    {
                        // Check for attach_skin specifically for armor
                        bool hasAttachSkin = false;
                        foreach (Transform child in prefab.transform)
                        {
                            if (child.name == "attach_skin")
                            {
                                hasAttachSkin = true;
                                break;
                            }
                        }
                        
                        if (!hasAttachSkin && !hasAttachPoint)
                        {
                            // Some items might still be valid without attach points
                            // Only reject if it's clearly meant to be equipped
                            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet ||
                                shared.m_itemType == ItemDrop.ItemData.ItemType.Chest ||
                                shared.m_itemType == ItemDrop.ItemData.ItemType.Legs ||
                                shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder)
                            {
                                return false;
                            }
                        }
                    }
                }
                
                // Additional monster weapon check - these often have specific skill types
                // that aren't player skills
                if (shared.m_skillType == Skills.SkillType.None && 
                    shared.m_damages.GetTotalDamage() > 0 &&
                    shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
                {
                    // Weapons with damage but no skill type are suspicious
                    // Player weapons always have a skill type
                    string nameLower = prefabName.ToLowerInvariant();
                    
                    // Exception for some special items
                    if (!nameLower.Contains("torch") && 
                        !nameLower.Contains("lantern"))
                    {
                        return false;
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionRandomLoadout] Error validating item {prefabName}: {ex.Message}");
                return false;
            }
        }
        
        private static bool IsCape(ItemDrop.ItemData.SharedData shared, string prefabName)
        {
            string nameLower = prefabName.ToLowerInvariant();
            return nameLower.Contains("cape") || 
                   nameLower.Contains("cloak") || 
                   nameLower.Contains("lox") ||
                   nameLower.Contains("wolf") ||
                   nameLower.Contains("deer") ||
                   nameLower.Contains("linen") ||
                   nameLower.Contains("feather");
        }
        
        private static bool IsFood(ItemDrop.ItemData.SharedData shared)
        {
            // Check if it's consumable food (has health/stamina regen)
            return shared.m_food > 0 || shared.m_foodStamina > 0 || shared.m_foodEitr > 0;
        }
        
        /// <summary>
        /// Clears the cached item lists (call when items might have changed).
        /// </summary>
        public static void ClearItemCache()
        {
            _itemListsCached = false;
            _allHelmets = null;
            _allChestArmor = null;
            _allLegArmor = null;
            _allCapes = null;
            _allShoulders = null;
            _allShields = null;
            _allWeapons = null;
            _allBows = null;
            _allArrows = null;
            _allFood = null;
        }
        
        #endregion
        
        #region Biome-Based Scaling (Optional)
        
        /// <summary>
        /// Adjusts loadout chances and health based on biome difficulty.
        /// </summary>
        public void ScaleForBiome(Heightmap.Biome biome)
        {
            _currentBiome = biome;
            float multiplier = GetBiomeMultiplier(biome);
            
            // Scale armor chances
            helmetChance *= multiplier;
            chestChance *= multiplier;
            legsChance *= multiplier;
            capeChance *= multiplier;
            shoulderChance *= multiplier;
            shieldChance *= multiplier;
            
            // Scale quality
            minQuality = Mathf.Max(1, Mathf.RoundToInt(minQuality * multiplier));
            maxQuality = Mathf.Max(minQuality, Mathf.RoundToInt(maxQuality * multiplier));
            
            // Clamp values
            helmetChance = Mathf.Clamp01(helmetChance);
            chestChance = Mathf.Clamp01(chestChance);
            legsChance = Mathf.Clamp01(legsChance);
            capeChance = Mathf.Clamp01(capeChance);
            shoulderChance = Mathf.Clamp01(shoulderChance);
            shieldChance = Mathf.Clamp01(shieldChance);
            maxQuality = Mathf.Min(maxQuality, 4);
            
            // Scale health based on biome
            ScaleHealthForBiome(biome);
        }
        
        /// <summary>
        /// Stores the spawn biome for this companion.
        /// Health scaling based on biome is now handled by CompanionStats.
        /// This method only saves the biome to ZDO for permanent traits (e.g., Ashlands fire immunity).
        /// </summary>
        private void ScaleHealthForBiome(Heightmap.Biome biome)
        {
            // NOTE: Health scaling based on biome is now handled by CompanionStats.
            // CompanionStats reads biome from ZDO via GetHealthMultiplierFromRandomLoadout()
            // and applies the multiplier in RecalculateMaxStats().
            // This prevents the "super high health" bug where health was scaled multiple times.
            
            // Store the spawn biome on the companion for:
            // 1. Damage immunity checks (Ashlands = fire immunity, DeepNorth = frost resistance)
            // 2. CompanionStats to read and apply health multiplier
            _currentBiome = biome;
            SaveSpawnBiomeToZDO(biome);
            
            // Only log when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Set spawn biome to {biome} for {_companion?.companionName}");
        }
        
        /// <summary>
        /// Saves the spawn biome to ZDO for persistence.
        /// This allows biome-based traits (like fire immunity) to persist after taming.
        /// </summary>
        private void SaveSpawnBiomeToZDO(Heightmap.Biome biome)
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            
            zdo.Set("companion_spawn_biome", (int)biome);
            // Only log spawn biome when verbose logging is enabled to reduce spam
            if (VerboseLogging)
                Debug.Log($"[CompanionRandomLoadout] Saved spawn biome to ZDO: {biome}");
        }
        
        /// <summary>
        /// Gets the biome where this companion originally spawned.
        /// Returns None if not set (tamed companions without spawn data).
        /// </summary>
        public static Heightmap.Biome GetSpawnBiome(ZNetView nview)
        {
            if (nview == null || !nview.IsValid()) return Heightmap.Biome.None;
            
            var zdo = nview.GetZDO();
            if (zdo == null) return Heightmap.Biome.None;
            
            int biomeInt = zdo.GetInt("companion_spawn_biome", (int)Heightmap.Biome.None);
            return (Heightmap.Biome)biomeInt;
        }
        
        /// <summary>
        /// Checks if a companion has fire immunity based on their spawn biome.
        /// Companions that spawned in Ashlands are permanently immune to fire damage.
        /// This trait persists even after taming - it's a benefit of recruiting Ashlands companions.
        /// </summary>
        public static bool HasFireImmunity(ZNetView nview)
        {
            Heightmap.Biome spawnBiome = GetSpawnBiome(nview);
            return spawnBiome == Heightmap.Biome.AshLands;
        }
        
        /// <summary>
        /// Checks if a companion has frost resistance based on their spawn biome.
        /// Companions that spawned in Mountains or Deep North have frost resistance.
        /// </summary>
        public static bool HasFrostResistance(ZNetView nview)
        {
            Heightmap.Biome spawnBiome = GetSpawnBiome(nview);
            return spawnBiome == Heightmap.Biome.Mountain || spawnBiome == Heightmap.Biome.DeepNorth;
        }
        
        private float GetBiomeMultiplier(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return 0.5f;
                case Heightmap.Biome.BlackForest:
                    return 0.75f;
                case Heightmap.Biome.Swamp:
                    return 1.0f;
                case Heightmap.Biome.Mountain:
                    return 1.25f;
                case Heightmap.Biome.Plains:
                    return 1.5f;
                case Heightmap.Biome.Mistlands:
                    return 1.75f;
                case Heightmap.Biome.AshLands:
                    return 2.0f;
                case Heightmap.Biome.DeepNorth:
                    return 2.0f;
                default:
                    return 1.0f;
            }
        }
        
        #endregion
    }
}
