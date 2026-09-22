using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc
{
    /// <summary>
    /// Manages companion NPC prefabs and their registration with Valheim systems.
    /// Sets up the CompanionNpc prefab with proper AI components at runtime.
    /// </summary>
    public static class CompanionPrefabManager
    {
   // Companion prefab names (from asset bundle)
public static readonly List<string> CompanionPrefabNames = new List<string>
        {
            "CompanionNpc",  // Will be searched as lowercase: companionnpc.prefab
            "BaseNpc"        // Secondary companion prefab for hammer placement
    };

        private static readonly List<GameObject> _loadedCompanions = new List<GameObject>();

        // Hidden, permanently-inactive parent for runtime-built prefabs
        // (currently just CompanionNpc_Wild). Children parked under an
        // inactive parent have activeInHierarchy == false even when their
        // own activeSelf is true, so Awake never fires on the prefab or on
        // components AddComponent'd to it. When Object.Instantiate(prefab,
        // pos, rot) runs at spawn time, the clone is created WITHOUT this
        // parent - activeInHierarchy flips to true and Awake fires fresh
        // with ZNetScene alive. This is the same "prefab container" trick
        // used by Jotunn and other Valheim mods to keep runtime-built
        // prefabs lifecycle-equivalent to bundle-loaded ones.
        private static GameObject _prefabHolder;

        private static GameObject GetPrefabHolder()
        {
            if (_prefabHolder == null)
            {
                _prefabHolder = new GameObject("VA_PrefabHolder");
                _prefabHolder.SetActive(false);
                Object.DontDestroyOnLoad(_prefabHolder);
            }
            return _prefabHolder;
        }

   /// <summary>
  /// Loads and configures companion prefabs from the asset bundle.
        /// </summary>
    public static void LoadCompanionPrefabs(string mainPath)
     {
            if (!FiresCore.Bridge.NpcAssetBridge.IsBundleReady)
    {
             Debug.LogError($"[CompanionPrefabManager] AssetBundle is null.");
          return;
         }

            _loadedCompanions.Clear();

      foreach (var prefabName in CompanionPrefabNames)
         {
        var prefab = LoadCompanionPrefab(prefabName);
    if (prefab != null)
                {
    SetupCompanionPrefab(prefab, prefabName);
   _loadedCompanions.Add(prefab);
    }
            }

            // Build the wild-companion variant by cloning CompanionNpc. We do
            // this here, alongside the asset-bundle loads, so the variant
            // rides the SAME pipeline as the other companion prefabs:
            // SetupCompanionPrefab gives it the full Character/AI/Tameable/...
            // stack, FixCompanionShaders fixes its body shader, and
            // RegisterWithZNetScene adds it to both m_prefabs and
            // m_namedPrefabs. The clone is produced while the CompanionNpc
            // source is still inactive (asset-bundle prefabs load inactive),
            // so Awake is queued - not fired - on the clone. This is the
            // same lifecycle invariant the bundle-loaded prefabs rely on.
            BuildWildCompanionVariant();

Debug.Log($"[CompanionPrefabManager] Loaded {_loadedCompanions.Count} companion prefabs");
        }

        /// <summary>
        /// Clones <c>CompanionNpc</c> into <c>CompanionNpc_Wild</c> and attaches
        /// the four wild-spawn components. The clone follows the standard
        /// companion-prefab pattern: it goes through <see cref="SetupCompanionPrefab"/>
        /// (idempotent - the cloned components are already there, we only
        /// re-run configuration) and is added to <c>_loadedCompanions</c> so
        /// <see cref="RegisterWithZNetScene"/> registers it like every other
        /// companion prefab. No alias / reflection hacks required.
        /// </summary>
        private static void BuildWildCompanionVariant()
        {
            const string wildName = WildSpawn.WildCompanionPrefabs.WildPrefabName;

            // Preferred path: the wild variant ships as a real prefab in the bundle
            // (FiresNpcPrefabBuilder), which gets the "activeSelf=true / Awake queued"
            // lifecycle for free — no holder dance needed. The legacy runtime clone
            // below stays as a fallback for a version-skewed older bundle.
            var bundledWild = LoadCompanionPrefab(wildName);
            if (bundledWild != null)
            {
                SetupCompanionPrefab(bundledWild, wildName);
                AttachWildCompanionComponents(bundledWild);
                ApplyWildFactionBaseline(bundledWild);
                _loadedCompanions.Add(bundledWild);
                Debug.Log($"[CompanionPrefabManager] Loaded wild-companion prefab '{wildName}' from bundle.");
                return;
            }

            var basePrefab = _loadedCompanions.Find(p =>
                p != null && string.Equals(p.name, "CompanionNpc", System.StringComparison.OrdinalIgnoreCase));
            if (basePrefab == null)
            {
                Debug.LogWarning("[CompanionPrefabManager] CompanionNpc base prefab missing; cannot build wild variant.");
                return;
            }

            // The wild prefab must be activeSelf at registration, because Terminal.spawn and
            // ZNetScene.CreateObject instantiate without ever activating the clone, yet Awake must not run on
            // it this early (ZNetView.Awake throws before ZNetScene exists and every clone inherits the broken
            // state). Parking it under a permanently inactive holder gives exactly the state bundle-loaded
            // prefabs have: active itself, inactive in the hierarchy.
            var holder = GetPrefabHolder();

            // While we Instantiate, force the source inactive too. Instantiate
            // copies activeSelf from source; we don't want the clone briefly
            // active before we can parent it. Restore the source afterwards.
            bool baseWasActive = basePrefab.activeSelf;
            if (baseWasActive) basePrefab.SetActive(false);
            GameObject wildPrefab;
            try
            {
                // Parent directly to the inactive holder. activeInHierarchy
                // is guaranteed false on the clone regardless of activeSelf.
                wildPrefab = Object.Instantiate(basePrefab, holder.transform);
            }
            finally
            {
                if (baseWasActive) basePrefab.SetActive(true);
            }

            wildPrefab.name = wildName;
            // Set activeSelf=true so that when ZNetScene/Terminal Instantiate
            // this prefab without a parent, the clone's activeInHierarchy
            // becomes true and Awake fires. The holder being inactive keeps
            // activeInHierarchy false on the prefab itself.
            wildPrefab.SetActive(true);

            // Re-run setup. SetupCompanionPrefab is idempotent (every step is
            // GetComponent-then-AddComponent guarded), so the cloned
            // components are reused and only their configuration is
            // re-applied. This keeps the wild variant identical to the base
            // prefab from the engine's perspective.
            SetupCompanionPrefab(wildPrefab, wildName);

            // Attach the four wild-spawn components. These are server-side,
            // self-gating MonoBehaviours; they live ONLY on the wild variant
            // (never on CompanionNpc), so tamed / recruited / placed
            // companions are completely untouched by them.
            AttachWildCompanionComponents(wildPrefab);

            ApplyWildFactionBaseline(wildPrefab);

            _loadedCompanions.Add(wildPrefab);
            Debug.Log($"[CompanionPrefabManager] Built wild-companion variant '{wildName}' from CompanionNpc " +
                      $"(faction baseline = Dverger, dresser will override per-instance).");
        }

        // Wild variants must NOT ride the player team: ConfigureHumanoid (via SetupCompanionPrefab)
        // stamps m_group="player" on every companion prefab, which makes player attacks count as
        // friendly fire and CompanionAI.IsEnemy(attacker) return false — "I can't hit them and they
        // don't react." Dverger + empty group restores the vanilla-Dverger baseline (hittable
        // without PvP, retaliates); WildCompanionDresser still writes per-instance factions later.
        // Runs AFTER SetupCompanionPrefab on both the bundled prefab and the legacy clone.
        private static void ApplyWildFactionBaseline(GameObject wildPrefab)
        {
            var wildHumanoid = wildPrefab.GetComponent<Humanoid>();
            if (wildHumanoid == null) return;
            wildHumanoid.m_faction = Character.Faction.Dverger;
            wildHumanoid.m_group = string.Empty;
        }

        private static void AttachWildCompanionComponents(GameObject prefab)
        {
            if (prefab.GetComponent<WildSpawn.WildCompanionSeed>() == null)
            {
                var seed = prefab.AddComponent<WildSpawn.WildCompanionSeed>();
                // Cohesion toggles only - faction / archetype / gear / stars
                // are biome-driven inside WildCompanionDresser.
                seed.Faction = WildSpawn.CompanionFaction.Neutral;
                seed.AllowedArchetypesMask = ~0;
                seed.AllowedGearTiers = new[] { 0 };
                seed.StarWeights = new[] { 85, 12, 3 };
                seed.EnableGroupCohesion = true;
                seed.HardMaxGroupSize = 10;
            }
            if (prefab.GetComponent<WildSpawn.WildCompanionDresser>() == null)
                prefab.AddComponent<WildSpawn.WildCompanionDresser>();
            if (prefab.GetComponent<WildSpawn.WildCompanionSquadFollower>() == null)
                prefab.AddComponent<WildSpawn.WildCompanionSquadFollower>();
            if (prefab.GetComponent<WildSpawn.WildCompanionLootOnDeath>() == null)
                prefab.AddComponent<WildSpawn.WildCompanionLootOnDeath>();
        }

   private static GameObject LoadCompanionPrefab(string prefabName)
        {
            // Try multiple paths where the companion might be located
            string[] searchPaths = {
  $"assets/custom/vaitems/npc/{prefabName}.prefab",
 $"assets/custom/vaitems/npc/{prefabName.ToLower()}.prefab",
   $"assets/custom/companions/{prefabName}.prefab",
 $"assets/custom/companions/{prefabName.ToLower()}.prefab",
         $"{prefabName}.prefab",
       prefabName
        };

   foreach (var path in searchPaths)
     {
                string normalizedPath = path.Replace('\\', '/').ToLower();
    GameObject prefab = FiresCore.Bridge.NpcAssetBridge.Load(normalizedPath);
      if (prefab != null)
  {
                    // Protect from scene-unload destruction. Without DDOL, the
                    // static _loadedCompanions list (and downstream registries
                    // like ZNetScene.m_namedPrefabs / pieceTable.m_pieces) end
                    // up holding Unity fake-null references on the next world
                    // load, which crashes other mods that iterate those
                    // collections before our re-registration runs.
                    Object.DontDestroyOnLoad(prefab);
             return prefab;
        }
       }

            Debug.LogWarning($"[CompanionPrefabManager] Could not find prefab: {prefabName}");
   return null;
 }

        /// <summary>
        /// Sets up a companion prefab with all required Valheim components.
        /// This turns a basic humanoid model into a fully functional AI companion.
        /// Also used by VAPieceManager for StaticNpc so it gets the same component stack.
        /// </summary>
        public static void SetupCompanionPrefab(GameObject prefab, string prefabName)
        {
       if (prefab == null) return;

  // ========================================
          // 1. ZNetView - Required for networking
    // ========================================
            var zNetView = prefab.GetComponent<ZNetView>();
  if (zNetView == null)
            {
           zNetView = prefab.AddComponent<ZNetView>();
       }
       zNetView.m_persistent = true;
            zNetView.m_type = ZDO.ObjectType.Default;

   // ========================================
            // 2. Rigidbody - Required for physics
  // ========================================
         var rigidbody = prefab.GetComponent<Rigidbody>();
            if (rigidbody == null)
     {
  rigidbody = prefab.AddComponent<Rigidbody>();
            }
          rigidbody.mass = 50f;
            rigidbody.useGravity = true;
   rigidbody.isKinematic = false;
      rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
        rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

     // ========================================
            // 3. CapsuleCollider - Required for collision
            // ========================================
            var collider = prefab.GetComponent<CapsuleCollider>();
  if (collider == null)
         {
    collider = prefab.AddComponent<CapsuleCollider>();
            }
  collider.height = 1.8f;
         collider.radius = 0.3f;
     collider.center = new Vector3(0, 0.9f, 0);

          // ========================================
     // 4. ZSyncTransform - Network position sync
            // ========================================
    var syncTransform = prefab.GetComponent<ZSyncTransform>();
         if (syncTransform == null)
        {
                syncTransform = prefab.AddComponent<ZSyncTransform>();
          }
       syncTransform.m_syncPosition = true;
            syncTransform.m_syncRotation = true;
       syncTransform.m_syncScale = false;
    syncTransform.m_syncBodyVelocity = true;

   // ========================================
   // 5. ZSyncAnimation - Network animation sync
 // ========================================
            var animator = prefab.GetComponentInChildren<Animator>(true);
          if (animator != null)
            {
     var syncAnimation = prefab.GetComponent<ZSyncAnimation>();
             if (syncAnimation == null)
     {
         syncAnimation = prefab.AddComponent<ZSyncAnimation>();
    }
            }

         // ========================================
            // 6. Humanoid - Required for equipment and combat
            // ========================================
            var humanoid = prefab.GetComponent<Humanoid>();
          if (humanoid == null)
     {
        humanoid = prefab.AddComponent<Humanoid>();
 }
   ConfigureHumanoid(humanoid, rigidbody);

            // ========================================
            // 7. CompanionAI - Our custom AI that replaces MonsterAI
            // ========================================
            // Check for and disable any existing MonsterAI
            var existingMonsterAI = prefab.GetComponent<MonsterAI>();
            if (existingMonsterAI != null)
            {
                Object.DestroyImmediate(existingMonsterAI);
            }
            
            var companionAI = prefab.GetComponent<CompanionAI>();
            if (companionAI == null)
            {
                companionAI = prefab.AddComponent<CompanionAI>();
            }
            ConfigureCompanionAI(companionAI);

            // Aggravatable so PLAYERS can hit companions/NPCs WITHOUT enabling PvP. The melee hit-filter
            // (Attack.DoMeleeAttack) only lands a player's hit on a non-enemy target when that target's
            // BaseAI.IsAggravatable() is true - the vanilla Dverger model. Companions are Dverger faction
            // (neutral, not player-enemies), so without this the ONLY way to hit them was to toggle PvP.
            // Ally/owner damage protection still lives in CompanionController.ShouldAllowDamage.
            companionAI.m_aggravatable = true;

            // ========================================
   // 8. Tameable - Required for ownership and commands
     // ========================================
        var tameable = prefab.GetComponent<Tameable>();
     if (tameable == null)
            {
                tameable = prefab.AddComponent<Tameable>();
            }
         ConfigureTameable(tameable);

            // ========================================
            // 9. CharacterAnimEvent - Required for attack events
            // This MUST be on the same GameObject as the Animator.
            // CharacterAnimEvent.Awake() uses GetComponentInParent<Character>() to find the Character.
            // 
            // IMPORTANT: The Visual child must be a child of the root GameObject where Character lives.
            // If Visual is not properly parented, GetComponentInParent won't find Character.
            // We manually set the m_character reference via reflection to guarantee it works.
            // ========================================
            if (animator != null)
            {
                // CharacterAnimEvent must be on the Animator's GameObject
                var animEvent = animator.GetComponent<CharacterAnimEvent>();
                if (animEvent == null)
                {
                    animEvent = animator.gameObject.AddComponent<CharacterAnimEvent>();
                }
                
                // Get the Character on the root (which is the Humanoid we added earlier)
                var rootCharacter = prefab.GetComponent<Character>();
                
                if (rootCharacter != null)
                {
                    // Use reflection to manually set the m_character reference
                    // This ensures it's set correctly regardless of hierarchy issues
                    try
                    {
                        var charField = typeof(CharacterAnimEvent).GetField("m_character", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (charField != null)
                        {
                            charField.SetValue(animEvent, rootCharacter);
                        }
                        
                        // Also set the ZNetView reference
                        var nviewField = typeof(CharacterAnimEvent).GetField("m_nview", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (nviewField != null)
                        {
                            var znetview = prefab.GetComponent<ZNetView>();
                            if (znetview != null)
                            {
                                nviewField.SetValue(animEvent, znetview);
                            }
                        }
                        
                        // Set the Animator reference
                        var animField = typeof(CharacterAnimEvent).GetField("m_animator", 
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (animField != null)
                        {
                            animField.SetValue(animEvent, animator);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[CompanionPrefabManager] Could not set CharacterAnimEvent references: {ex.Message}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[CompanionPrefabManager] No Character component on root - CharacterAnimEvent may not work!");
                }
                
                // Verify parent chain
                if (animator.gameObject != prefab)
                {
                    // Animator is on a child - verify the parent chain
                    Transform current = animator.transform;
                    bool foundRoot = false;
                    while (current != null)
                    {
                        if (current.gameObject == prefab)
                        {
                            foundRoot = true;
                            break;
                        }
                        current = current.parent;
                    }
                    
                    if (!foundRoot)
                    {
                        Debug.LogError($"[CompanionPrefabManager] Animator on '{animator.gameObject.name}' is NOT a child of root '{prefab.name}'! " +
                            "This will break GetComponentInParent<Character>()! Please fix the prefab hierarchy.");
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[CompanionPrefabManager] No Animator found on {prefabName} - CharacterAnimEvent not added");
            }

     // ========================================
     // 10. FootStep - the BAKED component ships fully wired (FiresNpcPrefabBuilder copies the
     // Player's per-material step table, and the Animator+ZNetView its Start() dereferences are
     // baked too). Only a LEGACY bundle's FootStep - unconfigured or with broken rip refs, the
     // original NRE source - still gets destroyed.
  // ========================================
   var existingFootStep = prefab.GetComponent<FootStep>();
   if (existingFootStep != null && IsLegacyBrokenFootStep(existingFootStep))
       {
        Object.DestroyImmediate(existingFootStep);
            }

   // ========================================
  // 11. CompanionController - Our custom controller
        // ========================================
     var companionController = prefab.GetComponent<CompanionController>();
            if (companionController == null)
  {
    companionController = prefab.AddComponent<CompanionController>();
          }
          companionController.companionName = prefabName;

 // ========================================
          // 12. CompanionInventory - Inventory system
   // ========================================
            var companionInventory = prefab.GetComponent<CompanionInventory>();
          if (companionInventory == null)
   {
      companionInventory = prefab.AddComponent<CompanionInventory>();
            }

 // ========================================
          // 13. NpcVisEquipment - Visual equipment (same as static NPCs)
   // ========================================
 var visEquipment = prefab.GetComponent<NpcVisEquipment>();
    if (visEquipment == null)
     {
   visEquipment = prefab.AddComponent<NpcVisEquipment>();
      }
            
            // CRITICAL: Configure the underlying VisEquipment to prevent NullReferenceException
            // The VisEquipment.Start() method calls UpdateBaseModel() which requires m_models
            // to be properly set when m_isPlayer = true. We must either:
            // 1. Set m_isPlayer = false initially (NpcVisEquipment will enable it later)
            // 2. Or properly populate m_models before Start() runs
            // 3. DISABLE the component until NpcVisEquipment is ready to use it
            // We do ALL THREE for maximum safety - NpcVisEquipment.Initialize() will set up everything
            var baseVisEquipment = prefab.GetComponent<VisEquipment>();
            if (baseVisEquipment != null)
            {
                // CRITICAL: Disable the component to prevent MonoUpdaters from calling
                // UpdateEquipmentVisuals() and GetModelIndex() before NpcVisEquipment initializes it.
                // NpcVisEquipment.Initialize() will re-enable it when ready.
                baseVisEquipment.enabled = false;
                
                // NOTE: m_models is now properly initialized in FixCompanionShaders() by copying
                // the Player's m_models array. This prevents NRE in GetModelIndex() during loading.
                // We no longer use an empty array since that still causes issues when UpdateBaseModel
                // tries to access m_models[0].
                
                // Disable player mode initially to prevent UpdateBaseModel crash
                // NpcVisEquipment.Initialize() will properly set this up with valid models
                baseVisEquipment.m_isPlayer = false;
                
                // Clear any invalid model references that might have come from the prefab
                // (check is now redundant since we just set it, but keep for clarity)
            }

            // ========================================
          // 14. CompanionCombat - Combat mechanics and weapon use
            // ========================================
       var companionCombat = prefab.GetComponent<CompanionCombat>();
     if (companionCombat == null)
 {
   companionCombat = prefab.AddComponent<CompanionCombat>();
            }

   // ========================================
     // 14.5. CompanionAttackBridge - Bridges attack callbacks
          // ========================================
        var attackBridge = prefab.GetComponent<Combat.CompanionAttackBridge>();
       if (attackBridge == null)
      {
       attackBridge = prefab.AddComponent<Combat.CompanionAttackBridge>();
            }

   // ========================================
     // 15. CompanionSkills - Skill progression system
            // ========================================
     var companionSkills = prefab.GetComponent<CompanionSkills>();
     if (companionSkills == null)
            {
        companionSkills = prefab.AddComponent<CompanionSkills>();
  }
            
            // ========================================
            // 15.5. CompanionProgression - Level/XP and attribute system
            // ========================================
            var companionProgression = prefab.GetComponent<CompanionProgression>();
            if (companionProgression == null)
            {
                companionProgression = prefab.AddComponent<CompanionProgression>();
            }
            
            // ========================================
            // 15.6. CompanionStats - Health/Stamina/Eitr pool management
            // CRITICAL: CompanionStats handles all health calculations including scale/biome multipliers.
            // CompanionRandomLoadout saves scale/biome to ZDO, CompanionStats reads and applies them.
            // ========================================
            var companionStats = prefab.GetComponent<CompanionStats>();
            if (companionStats == null)
            {
                companionStats = prefab.AddComponent<CompanionStats>();
            }
            
            // ========================================
            // 15.7. CompanionLuck - Luck stat that affects level scaling
            // Each companion gets a random luck value (0-100) that determines
            // how much their abilities scale with level (25-50% at max level)
            // ========================================
            var companionLuck = prefab.GetComponent<CompanionLuck>();
            if (companionLuck == null)
            {
                companionLuck = prefab.AddComponent<CompanionLuck>();
            }
  
            // ========================================
            // 16. EnemyAttackRecognition - Threat detection for blocking/dodging
            // ========================================
            var attackRecognition = prefab.GetComponent<Combat.EnemyAttackRecognition>();
            if (attackRecognition == null)
            {
                attackRecognition = prefab.AddComponent<Combat.EnemyAttackRecognition>();
            }
            
            // ========================================
            // 17. ThreatAnalyzer - Comprehensive threat assessment system
            // ========================================
            var threatAnalyzer = prefab.GetComponent<Combat.ThreatAnalyzer>();
            if (threatAnalyzer == null)
            {
                threatAnalyzer = prefab.AddComponent<Combat.ThreatAnalyzer>();
            }
            
            // ========================================
            // 18. CombatMemory - Remembers dangerous enemies and learns from deaths
            // ========================================
            var combatMemory = prefab.GetComponent<Combat.CombatMemory>();
            if (combatMemory == null)
            {
                combatMemory = prefab.AddComponent<Combat.CombatMemory>();
            }
            
            // ========================================
            // 19. CombatExperience - Level-based combat improvements
            // As companions level up, they get better at blocking, parrying, dodging
            // ========================================
            var combatExperience = prefab.GetComponent<Combat.CombatExperience>();
            if (combatExperience == null)
            {
                combatExperience = prefab.AddComponent<Combat.CombatExperience>();
            }
            
            // ========================================
            // 20. CompanionRandomLoadout - Random equipment for wild companions
            // ========================================
            var randomLoadout = prefab.GetComponent<CompanionRandomLoadout>();
            if (randomLoadout == null)
            {
                randomLoadout = prefab.AddComponent<CompanionRandomLoadout>();
            }
            
            // ========================================
            // 20.5. CompanionWeaponScaler - Scales weapons/items to match companion size
            // ========================================
            var weaponScaler = prefab.GetComponent<CompanionWeaponScaler>();
            if (weaponScaler == null)
            {
                weaponScaler = prefab.AddComponent<CompanionWeaponScaler>();
            }
            
            // ========================================
            // 20.6. ArchetypeController - Manages companion archetype/role (Tank, Healer, etc.)
            // ========================================
            var archetypeController = prefab.GetComponent<Archetypes.ArchetypeController>();
            if (archetypeController == null)
            {
                archetypeController = prefab.AddComponent<Archetypes.ArchetypeController>();
            }
            
            // ========================================
            // 20.7. ArchetypeAbilitySystem - Triggers archetype abilities in combat
            // ========================================
            var archetypeAbilities = prefab.GetComponent<Archetypes.ArchetypeAbilitySystem>();
            if (archetypeAbilities == null)
            {
                archetypeAbilities = prefab.AddComponent<Archetypes.ArchetypeAbilitySystem>();
            }
            
            // ========================================
            // 20.8. ArchetypeSkillSystem - Skill leveling for archetype abilities
            // Each ability has independent skill level (1-100) that increases effectiveness
            // ========================================
            var archetypeSkills = prefab.GetComponent<Archetypes.ArchetypeSkillSystem>();
            if (archetypeSkills == null)
            {
                archetypeSkills = prefab.AddComponent<Archetypes.ArchetypeSkillSystem>();
            }
            
            // ========================================
            // 20.9. SkillDecisionSystem - Intelligent skill timing and situational usage
            // Instead of using abilities immediately, evaluates combat situation
            // ========================================
            var skillDecision = prefab.GetComponent<Archetypes.SkillDecisionSystem>();
            if (skillDecision == null)
            {
                skillDecision = prefab.AddComponent<Archetypes.SkillDecisionSystem>();
            }
            
            // ========================================
            // 21. CompanionAutoPickup - Automatic item pickup as companion moves
            // ========================================
            var autoPickup = prefab.GetComponent<CompanionAutoPickup>();
            if (autoPickup == null)
            {
                autoPickup = prefab.AddComponent<CompanionAutoPickup>();
            }

            // ========================================
            // 22. CompanionDoorHandler - Open/close doors that block pathing
            // ========================================
            var doorHandler = prefab.GetComponent<CompanionDoorHandler>();
            if (doorHandler == null)
            {
                prefab.AddComponent<CompanionDoorHandler>();
            }

            // Set layer to "character"
            prefab.layer = LayerMask.NameToLayer("character");

            // Unity layers don't inherit: the bundle rig's CHILD objects (the visual meshes)
            // come in on Default, unlike the vanilla Player whose whole rig is on "character".
            // Anything layer-driven that should treat an NPC as a character missed them —
            // e.g. the FiresGlass sun-occlusion camera (character/piece/static_solid allowlist)
            // rendered the player's shadow but not companions'/NPCs'. Lift ONLY Default-layer
            // children so intentional special layers (triggers, hitboxes, UI) keep theirs.
            int characterLayer = prefab.layer;
            foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                if (child.gameObject.layer == 0)
                    child.gameObject.layer = characterLayer;

            // Late-setup path (StaticNpc via VAPieceManager runs at piece-registration time, when
            // ZNetScene is already up): rebind baked effect refs to live prefabs immediately.
            // Companion prefabs set up before ZNetScene get the same pass in RegisterWithZNetScene.
            if (ZNetScene.instance != null)
                NpcPrefabSetup.RebindEffectPrefabsToLive(prefab);
            
            // ========================================
            // 19. Fix Body Material Shader - CRITICAL
            // The asset bundle's Custom/Player shader is broken (no binary data).
            // We must copy the REAL shader from Valheim's Player prefab at runtime.
            // This is deferred until ZNetScene is ready via FixCompanionShaders().
            // ========================================
        }

        private static bool IsLegacyBrokenFootStep(FootStep footStep)
        {
            if (footStep.m_effects == null || footStep.m_effects.Count == 0) return true;
            foreach (var step in footStep.m_effects)
            {
                if (step == null || step.m_effectPrefabs == null) return true;
                foreach (var fx in step.m_effectPrefabs)
                    if (fx == null) return true;
            }
            return false;
        }

        private static void ConfigureHumanoid(Humanoid humanoid, Rigidbody rigidbody)
        {
            if (humanoid == null) return;

            var prefab = humanoid.gameObject;

            // Basic humanoid stats for a companion
            humanoid.m_name = "Companion";
            humanoid.m_group = "player";
            // Dverger, not Players: companions (and prefab-shared static NPCs) must be attackable with
            // no engine PvP gate. Owner/ally immunity is enforced in CompanionController.ShouldAllowDamage.
            humanoid.m_faction = Character.Faction.Dverger;
            humanoid.m_health = 200f;
            humanoid.m_walkSpeed = 2f;
            humanoid.m_speed = 4f;
            humanoid.m_runSpeed = 7f;
            humanoid.m_turnSpeed = 300f;
            humanoid.m_acceleration = 1f;
            humanoid.m_jumpForce = 8f;
            humanoid.m_staggerWhenBlocked = true;
            humanoid.m_staggerDamageFactor = 0.3f;
            humanoid.m_tolerateWater = true;
            humanoid.m_tolerateFire = false;
            humanoid.m_tolerateSmoke = true;
            
            // CRITICAL: Swimming settings - companions must be able to swim
            humanoid.m_canSwim = true;
            humanoid.m_swimSpeed = 2f;  // Standard swim speed
            humanoid.m_swimTurnSpeed = 100f;
            humanoid.m_swimAcceleration = 0.05f;

            // Respect a baked m_eye (FiresNpcPrefabBuilder wires EyePos — the vanilla Player
            // convention). Only search when the prefab didn't ship one. EyePos before Head:
            // Character.UpdateEyeRotation writes m_eye.rotation every frame, so pointing it
            // at the Head BONE fights the animator.
            if (humanoid.m_eye == null)
            {
                Transform eyeTransform = FindTransformByNames(prefab.transform, new[] { "EyePos", "Eye", "eye", "Head", "head" });
                if (eyeTransform == null)
                {
                    var eyeObj = new GameObject("EyePos");
                    eyeObj.transform.SetParent(prefab.transform);
                    eyeObj.transform.localPosition = new Vector3(0, 1.6f, 0.1f);
                    eyeObj.transform.localRotation = Quaternion.identity;
                    eyeTransform = eyeObj.transform;
                }
                humanoid.m_eye = eyeTransform;
            }

       // Find visual transform
         Transform visualTransform = FindTransformByNames(prefab.transform, new[] { "Visual", "visual" });
            if (visualTransform == null)
     {
         visualTransform = prefab.transform;
            }

        // Use reflection to set protected fields
   try
     {
      var characterType = typeof(Character);

     var bodyField = characterType.GetField("m_body", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
          if (bodyField != null && bodyField.FieldType == typeof(Rigidbody))
        {
           bodyField.SetValue(humanoid, rigidbody);
         }

   var visualField = characterType.GetField("m_visual", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
      if (visualField != null && visualField.FieldType == typeof(Transform))
      {
      visualField.SetValue(humanoid, visualTransform);
    }
    }
    catch (System.Exception ex)
            {
           Debug.LogWarning($"[CompanionPrefabManager] Failed to set protected Character fields: {ex.Message}");
      }
        }

        private static Transform FindTransformByNames(Transform root, string[] names)
        {
            foreach (var name in names)
   {
              var found = FindTransformRecursive(root, name);
    if (found != null) return found;
        }
            return null;
  }

     private static Transform FindTransformRecursive(Transform parent, string name)
   {
 if (parent.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
       return parent;

     foreach (Transform child in parent)
     {
       var found = FindTransformRecursive(child, name);
       if (found != null) return found;
            }
            return null;
        }

        private static void ConfigureCompanionAI(CompanionAI companionAI)
        {
            if (companionAI == null) return;

            // Detection ranges
            companionAI.m_viewRange = 30f;
            companionAI.m_viewAngle = 120f;
            companionAI.m_hearRange = 45f;
            
            // CRITICAL: Pathfinding settings for swimming
            // AgentType.Humanoid allows swimming, HumanoidNoSwim does not
            companionAI.m_pathAgentType = Pathfinding.AgentType.Humanoid;
            
            // Allow companions to path through water - don't avoid it
            companionAI.m_avoidWater = false;
            
            // CompanionAI-specific settings
            companionAI.aggroRange = 30f;
            companionAI.attackRange = 2.5f;
            
            // Follow settings - buffer zones. Tight run threshold so the companion runs to catch up the
            // moment it trails ~6m (see CompanionController/CompanionAI — keep all three in sync).
            companionAI.stopDistanceInner = 2f;
            companionAI.stopDistanceOuter = 4f;
            companionAI.walkDistanceOuter = 5f;
            companionAI.runDistanceInner = 4f;
            companionAI.runDistanceOuter = 6f;
            companionAI.catchUpDistance = 12f;
            // teleportDistance removed - recall is owned by CompanionController.CheckFollowTeleport()
            // Escort-scale leash (mirrors ConfigureCompanionAI in CompanionController): fights stay
            // around the owner; a chase breaks off 15 m out instead of 40.
            companionAI.combatLeashDistance = 15f;
            companionAI.maxChaseDistance = 30f;
            companionAI.giveUpTime = 15f;
            
            // Protection settings
            companionAI.ownerProtectionRange = 20f;
            companionAI.proactiveProtection = true;
            companionAI.interceptPriority = 2f;
            
            // Self-preservation
            companionAI.fleeHealthPercent = 0.2f;
            companionAI.fleeTime = 10f;
            companionAI.healReturnPercent = 0.5f;
            
            // Idle settings
            companionAI.idleWanderRadius = 5f;
            companionAI.idleWanderInterval = 10f;
        }
        
        private static void ConfigureTameable(Tameable tameable)
        {
            if (tameable == null) return;

    tameable.m_commandable = true;
  tameable.m_tamingTime = 0f;
     tameable.m_fedDuration = 600f;
            tameable.m_startsTamed = false;
        }

        /// <summary>
        /// Registers companion prefabs with ZNetScene for spawning.
        /// Call this after ZNetScene is initialized.
        /// </summary>
        public static void RegisterWithZNetScene()
        {
            if (ZNetScene.instance == null)
            {
           Debug.LogWarning("[CompanionPrefabManager] ZNetScene not ready");
             return;
            }

            // Purge fake-null entries from ZNetScene's shared collections before adding.
            // On a second world load in a session, leftover dead refs from any mod
            // crash downstream iterators (BalrondExtendedAnimals.SpawnerBuilder etc.)
            // during ZNetScene.Awake. Unity's overloaded == catches both null and
            // fake-null with a single predicate.
            int dead = FiresCore.World.NetworkPrefabs.ScrubDeadEntries(ZNetScene.instance);
            if (dead > 0)
                Debug.Log($"[CompanionPrefabManager] Scrubbed {dead} fake-null entries from ZNetScene's prefab lists");

            // CRITICAL: Fix shaders BEFORE registering prefabs
            // The asset bundle has broken Custom/Player shader references.
            // We must copy the real shader from Valheim's Player prefab.
            FixCompanionShaders();

            // Swap baked FootStep/effect-list prefab refs to the live game's copies (correct audio
            // mixer routing, tracks game updates). Runs here because ZNetScene is up; the rip-time
            // refs shipped in the bundle remain as the fallback wherever no live match exists.
            foreach (var prefab in _loadedCompanions)
                NpcPrefabSetup.RebindEffectPrefabsToLive(prefab);

          foreach (var prefab in _loadedCompanions)
       {
                if (prefab == null) continue;

    string prefabName = prefab.name;

        if (ZNetScene.instance.GetPrefab(prefabName) != null)
              {
   continue;
           }

                // Add to prefab list
        ZNetScene.instance.m_prefabs.Add(prefab);

        // Calculate hash for the prefab lookup
         int stableHashCode = prefabName.GetStableHashCode();

         // Try to add to the hash lookup dictionary using reflection
                try
   {
           var namedPrefabsField = typeof(ZNetScene).GetField("m_namedPrefabs",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (namedPrefabsField != null)
    {
          var dict = namedPrefabsField.GetValue(ZNetScene.instance) as Dictionary<int, GameObject>;
             if (dict != null && !dict.ContainsKey(stableHashCode))
              {
      dict.Add(stableHashCode, prefab);
    continue;
    }
         }

     var prefabsByHashField = typeof(ZNetScene).GetField("m_prefabsByHash",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

          if (prefabsByHashField != null)
      {
          var dict = prefabsByHashField.GetValue(ZNetScene.instance) as Dictionary<int, GameObject>;
  if (dict != null && !dict.ContainsKey(stableHashCode))
            {
 dict.Add(stableHashCode, prefab);
      continue;
      }
   }
 }
  catch (System.Exception ex)
 {
   Debug.LogWarning($"[CompanionPrefabManager] Failed to add {prefabName} to hash lookup: {ex.Message}");
     }
            }
        }

 /// <summary>
        /// Spawns a companion at the specified position.
        /// </summary>
     public static GameObject SpawnCompanion(string prefabName, Vector3 position, Quaternion rotation)
     {
  // Case-insensitive search for the prefab
 var prefab = _loadedCompanions.Find(p =>
            p != null && string.Equals(p.name, prefabName, System.StringComparison.OrdinalIgnoreCase));

            if (prefab == null)
      {
                Debug.LogError($"[CompanionPrefabManager] Prefab not found: {prefabName}. Loaded prefabs: {string.Join(", ", GetLoadedCompanionNames())}");
      return null;
            }

var companion = CompanionNetworkHelper.Spawn(prefab, position, rotation);
return companion;
      }

        /// <summary>
        /// Gets list of all loaded companion prefab names.
        /// </summary>
        public static List<string> GetLoadedCompanionNames()
     {
            var names = new List<string>();
 foreach (var prefab in _loadedCompanions)
            {
  if (prefab != null)
          names.Add(prefab.name);
    }
 return names;
        }

   /// <summary>
        /// Checks if a companion prefab with the given name is loaded.
     /// </summary>
        public static bool HasCompanionPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
 
         return _loadedCompanions.Exists(p => 
              p != null && string.Equals(p.name, prefabName, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
     /// Gets the list of all loaded companion prefab GameObjects.
        /// Used by VAPieceManager to add companions to the hammer.
        /// </summary>
        public static List<GameObject> GetLoadedCompanionPrefabs()
        {
          return new List<GameObject>(_loadedCompanions);
        }

        /// <summary>
        /// Returns the registered base <c>CompanionNpc</c> prefab (the shader-fixed, fully-attached
        /// humanoid), or null if it isn't loaded yet. Used by the mannequin preview builder, which clones
        /// it and strips the gameplay machinery for a static, ZDO-free portrait. Falls back to
        /// ZNetScene if the in-memory list is somehow empty but the prefab is registered.
        /// </summary>
        public static GameObject GetCompanionNpcPrefab()
        {
            var fromList = _loadedCompanions.Find(p =>
                p != null && string.Equals(p.name, "CompanionNpc", System.StringComparison.OrdinalIgnoreCase));
            if (fromList != null) return fromList;
            return ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("CompanionNpc") : null;
        }

        /// <summary>
        /// Exposes the permanently-inactive prefab holder so callers (e.g. the mannequin builder) can
        /// instantiate clones whose Awake is queued, not fired, while they strip components.
        /// </summary>
        public static GameObject GetInactivePrefabHolder() => GetPrefabHolder();
        
        /// <summary>
        /// Cached eye emission texture extracted from companion prefab before shader swap.
        /// This texture has only the eye area visible and can be tinted for eye color.
        /// </summary>
        private static Texture _cachedEyeEmissionTexture;
        
        /// <summary>
        /// Gets the cached eye emission texture for use by NpcVisEquipment.
        /// </summary>
        public static Texture GetCachedEyeEmissionTexture()
        {
            return _cachedEyeEmissionTexture;
        }
        
        /// <summary>True when the prefab ships a body-model table usable for male/female switching —
        /// both entries present with meshes (the baked bundles serialize rip-consistent body/bodyfem).</summary>
        private static bool HasUsableModelTable(VisEquipment visEquipment)
        {
            if (visEquipment.m_models == null || visEquipment.m_models.Length < 2) return false;
            for (int i = 0; i < 2; i++)
            {
                if (visEquipment.m_models[i] == null || visEquipment.m_models[i].m_mesh == null) return false;
            }
            return true;
        }

        /// <summary>Per-prefab copy of the Player's model table. Never hand out the Player's own array
        /// reference — a later mutation on either side would silently alias the other.</summary>
        private static VisEquipment.PlayerModel[] ClonePlayerModels(VisEquipment.PlayerModel[] source)
        {
            var copy = new VisEquipment.PlayerModel[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = source[i] == null ? null : new VisEquipment.PlayerModel
                {
                    m_mesh = source[i].m_mesh,
                    m_baseMaterial = source[i].m_baseMaterial
                };
            }
            return copy;
        }

        /// <summary>
        /// Gives every companion prefab a working body material. The bundle's Custom/Player shader ships without
        /// compiled bytecode, so the real material is copied from Valheim's Player prefab; the eye emission texture is
        /// captured first for the tintable eye overlay.
        /// </summary>
        private static void FixCompanionShaders()
        {
            // Get the Player prefab from ZNetScene
            var playerPrefab = ZNetScene.instance?.GetPrefab("Player");
            if (playerPrefab == null)
            {
                Debug.LogWarning("[CompanionPrefabManager] Could not find Player prefab to copy shader from!");
                return;
            }

            // Copy hit / death effects from the Player prefab so companions play
            // the correct flesh-hit VFX and sounds when struck. Without this the
            // m_hitEffects EffectList stays empty and Character.ApplyDamage() has
            // nothing to spawn - the player sees no visual or audio feedback.
            var playerHumanoid = playerPrefab.GetComponent<Humanoid>();
            if (playerHumanoid != null)
            {
                foreach (var prefab in _loadedCompanions)
                {
                    if (prefab == null) continue;
                    var humanoid = prefab.GetComponent<Humanoid>();
                    if (humanoid == null) continue;
                    humanoid.m_hitEffects   = playerHumanoid.m_hitEffects;
                    humanoid.m_deathEffects = playerHumanoid.m_deathEffects;
                }
            }
            
            // Get the player's body material and models
            var playerVisEquip = playerPrefab.GetComponent<VisEquipment>();
            Material playerBodyMaterial = null;
            
            // Fill VisEquipment.m_models ONLY on legacy bundles that shipped no usable table.
            // CharacterAnimEvent.CustomLateUpdate() calls VisEquipment.GetModelIndex() every frame and
            // NREs when m_models is null/empty, so legacy prefabs still need a runtime copy — but it
            // must be a CLONE of the Player's array, never the array reference itself. Baked prefabs
            // ship rip-consistent m_models (rig + body + bodyfem all at 53 bones/bindposes) and MUST
            // keep them: unconditionally stomping them with the live Player's models here was the root
            // cause of the female-NPC "mesh data size and vertex stride" render-stop spam — the live
            // game's bodyfem no longer skins on the ripped rig even though its bindpose count still
            // matches, so every female spawn assigned a mesh Unity rejects (invisible body).
            if (playerVisEquip?.m_models != null && playerVisEquip.m_models.Length > 0)
            {
                foreach (var prefab in _loadedCompanions)
                {
                    if (prefab == null) continue;
                    var companionVisEquip = prefab.GetComponent<VisEquipment>();
                    if (companionVisEquip == null || HasUsableModelTable(companionVisEquip)) continue;
                    companionVisEquip.m_models = ClonePlayerModels(playerVisEquip.m_models);
                    Debug.Log($"[CompanionPrefabManager] {prefab.name}: legacy bundle without baked m_models — copied {companionVisEquip.m_models.Length} live Player models as fallback");
                }
            }
            
            if (playerVisEquip?.m_bodyModel != null)
            {
                playerBodyMaterial = playerVisEquip.m_bodyModel.sharedMaterial;
            }
            
            // Fallback: search for body renderer
            if (playerBodyMaterial == null)
            {
                var bodyTransform = FindTransformRecursive(playerPrefab.transform, "body");
                if (bodyTransform != null)
                {
                    var skinnedRenderer = bodyTransform.GetComponent<SkinnedMeshRenderer>();
                    if (skinnedRenderer != null)
                    {
                        playerBodyMaterial = skinnedRenderer.sharedMaterial;
                    }
                }
            }
            
            if (playerBodyMaterial == null)
            {
                Debug.LogWarning("[CompanionPrefabManager] Could not find Player body material!");
                return;
            }
            
            Shader playerShader = playerBodyMaterial.shader;
            
            // Now fix all companion prefabs
            foreach (var prefab in _loadedCompanions)
            {
                if (prefab == null) continue;
                
                // Find the body renderer on the companion
                var visualTransform = FindTransformRecursive(prefab.transform, "Visual");
                if (visualTransform == null) visualTransform = prefab.transform;
                
                var bodyTransform = FindTransformRecursive(visualTransform, "body");
                if (bodyTransform == null)
                {
                    Debug.LogWarning($"[CompanionPrefabManager] No 'body' transform found on {prefab.name}");
                    continue;
                }
                
                var bodyRenderer = bodyTransform.GetComponent<SkinnedMeshRenderer>();
                if (bodyRenderer == null)
                {
                    Debug.LogWarning($"[CompanionPrefabManager] No SkinnedMeshRenderer on body of {prefab.name}");
                    continue;
                }
                
                // Check current shader
                var currentMaterial = bodyRenderer.sharedMaterial;
                if (currentMaterial == null)
                {
                    Debug.LogWarning($"[CompanionPrefabManager] No material on body of {prefab.name}");
                    continue;
                }
                
                string currentShaderName = currentMaterial.shader?.name ?? "null";
                
                // CAPTURE EYE EMISSION TEXTURE before we swap shaders!
                // The Standard shader has _EmissionMap which contains our eye texture
                if (_cachedEyeEmissionTexture == null)
                {
                    if (currentMaterial.HasProperty("_EmissionMap"))
                    {
                        _cachedEyeEmissionTexture = currentMaterial.GetTexture("_EmissionMap");
                    }
                    
                    // Also check _EmissionTex (alternate naming)
                    if (_cachedEyeEmissionTexture == null && currentMaterial.HasProperty("_EmissionTex"))
                    {
                        _cachedEyeEmissionTexture = currentMaterial.GetTexture("_EmissionTex");
                    }
                }
                
                // Create a new material based on the player's material
                // This gives us the REAL Custom/Player shader with working bytecode
                var newMaterial = new Material(playerBodyMaterial);
                
                // Copy textures from the original companion material if they exist
                // The companion may have different body textures than the player
                if (currentMaterial.HasProperty("_MainTex"))
                {
                    var companionTex = currentMaterial.GetTexture("_MainTex");
                    if (companionTex != null)
                    {
                        newMaterial.SetTexture("_MainTex", companionTex);
                    }
                }
                
                if (currentMaterial.HasProperty("_BumpMap"))
                {
                    var companionBump = currentMaterial.GetTexture("_BumpMap");
                    if (companionBump != null && newMaterial.HasProperty("_SkinBumpMap"))
                    {
                        newMaterial.SetTexture("_SkinBumpMap", companionBump);
                    }
                }
                
                // Set a default skin color (fair skin)
                if (newMaterial.HasProperty("_SkinColor"))
                {
                    newMaterial.SetColor("_SkinColor", new Color(1f, 0.87f, 0.77f, 1f));
                }
                
                // Apply the fixed material
                bodyRenderer.sharedMaterial = newMaterial;
            }
        }
    }
}
