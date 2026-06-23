using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;
using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Handles multiplayer RPC synchronization for companion status effects and abilities.
    /// All ability applications should go through this manager to ensure proper replication
    /// across all clients on a dedicated server.
    /// 
    /// RPC FLOW:
    /// 1. Owner companion triggers ability
    /// 2. AbilityRPCManager.ApplyAbility() is called
    /// 3. RPC is sent to all clients via ZRoutedRpc
    /// 4. Each client applies the status effect locally
    /// 5. Visual FX is spawned on each client
    /// 
    /// TARGET VALIDATION:
    /// - Group buffs: Only allies (same owner companions + owner player)
    /// - AoE damage: Only enemies (monsters + enemy players in PvP)
    /// - Single effects: Validated based on effect type
    /// 
    /// SUPPORTED ABILITIES:
    /// - Group buffs (Warcry, Divine Protection, Sanctuary, etc.)
    /// - Single-target effects (Hunter's Mark, Poison, etc.)
    /// - Self-buffs (Fortify, Berserk Rage, etc.)
    /// - AoE effects (Purifying Circle, Taunt, etc.)
    /// </summary>
    public static class AbilityRPCManager
    {
        public static bool VerboseLogging = false;
        
        // RPC names for different ability types
        private const string RPC_APPLY_GROUP_BUFF = "RPC_CompanionGroupBuff";
        private const string RPC_APPLY_SINGLE_EFFECT = "RPC_CompanionSingleEffect";
        private const string RPC_APPLY_SELF_BUFF = "RPC_CompanionSelfBuff";
        private const string RPC_APPLY_AOE_EFFECT = "RPC_CompanionAoEEffect";
        private const string RPC_SPAWN_FX = "RPC_CompanionSpawnFX";
        
        private static bool _initialized = false;
        
        /// <summary>
        /// Initializes the RPC handlers. Call this during game startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            if (ZRoutedRpc.instance == null) return;
            
            // Register RPC handlers
            ZRoutedRpc.instance.Register<ZDOID, string, float, float>(RPC_APPLY_GROUP_BUFF, RPC_HandleGroupBuff);
            ZRoutedRpc.instance.Register<ZDOID, ZDOID, string, float>(RPC_APPLY_SINGLE_EFFECT, RPC_HandleSingleEffect);
            ZRoutedRpc.instance.Register<ZDOID, string, float>(RPC_APPLY_SELF_BUFF, RPC_HandleSelfBuff);
            ZRoutedRpc.instance.Register<ZDOID, string, float, float>(RPC_APPLY_AOE_EFFECT, RPC_HandleAoEEffect);
            ZRoutedRpc.instance.Register<Vector3, string, float>(RPC_SPAWN_FX, RPC_HandleSpawnFX);
            
            _initialized = true;
            
            if (VerboseLogging)
            {
                Debug.Log("[AbilityRPCManager] Initialized RPC handlers for companion abilities");
            }
        }
        
        #region Public API - Send RPCs
        
        /// <summary>
        /// Applies a group buff to all allies in range. Synced via RPC.
        /// </summary>
        /// <param name="source">The character applying the buff.</param>
        /// <param name="effectName">Name of the status effect to apply.</param>
        /// <param name="range">Range to search for allies.</param>
        /// <param name="duration">Duration of the buff.</param>
        /// <returns>Number of allies affected.</returns>
        public static int ApplyGroupBuff(Character source, string effectName, float range, float duration)
        {
            if (source == null) return 0;
            // Skip ability broadcasts during the local player's respawn / loading-screen
            // window â€” RPCs broadcast while IsTeleporting=true deadlock the zone stream
            // (documented in CompanionPatches.cs). Suppressed buffs simply re-trigger
            // from the next Update tick once the player can move again.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return 0;

            var nview = source.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            ZDOID sourceId = nview.GetZDO().m_uid;

            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_APPLY_GROUP_BUFF,
                sourceId, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent group buff RPC: {effectName} from {source.m_name}, range={range}m, duration={duration}s");
            }
            
            // Apply locally and return count
            return ApplyGroupBuffLocal(source, effectName, range, duration);
        }
        
        /// <summary>
        /// Applies a single-target effect. Synced via RPC.
        /// </summary>
        /// <param name="source">The character applying the effect.</param>
        /// <param name="target">The target to affect.</param>
        /// <param name="effectName">Name of the status effect.</param>
        /// <param name="duration">Duration of the effect.</param>
        /// <returns>True if applied successfully.</returns>
        public static bool ApplySingleEffect(Character source, Character target, string effectName, float duration)
        {
            if (source == null || target == null) return false;
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return false;

            var sourceNview = source.GetComponent<ZNetView>();
            var targetNview = target.GetComponent<ZNetView>();
            if (sourceNview == null || targetNview == null) return false;
            if (!sourceNview.IsValid() || !targetNview.IsValid()) return false;
            
            ZDOID sourceId = sourceNview.GetZDO().m_uid;
            ZDOID targetId = targetNview.GetZDO().m_uid;
            
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_APPLY_SINGLE_EFFECT,
                sourceId, targetId, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent single effect RPC: {effectName} from {source.m_name} to {target.m_name}");
            }
            
            // Apply locally
            return ApplySingleEffectLocal(source, target, effectName, duration);
        }
        
        /// <summary>
        /// Applies a self-buff. Synced via RPC.
        /// </summary>
        /// <param name="target">The character to buff.</param>
        /// <param name="effectName">Name of the status effect.</param>
        /// <param name="duration">Duration of the buff.</param>
        /// <returns>True if applied successfully.</returns>
        public static bool ApplySelfBuff(Character target, string effectName, float duration)
        {
            if (target == null) return false;
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return false;

            var nview = target.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            ZDOID targetId = nview.GetZDO().m_uid;
            
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_APPLY_SELF_BUFF,
                targetId, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent self buff RPC: {effectName} on {target.m_name}");
            }
            
            // Apply locally
            return ApplySelfBuffLocal(target, effectName, duration);
        }
        
        /// <summary>
        /// Applies an AoE effect to enemies in range. Synced via RPC.
        /// </summary>
        /// <param name="source">The character applying the effect.</param>
        /// <param name="effectName">Name of the status effect.</param>
        /// <param name="range">Range of the AoE.</param>
        /// <param name="duration">Duration of the effect.</param>
        /// <returns>Number of enemies affected.</returns>
        public static int ApplyAoEEffect(Character source, string effectName, float range, float duration)
        {
            if (source == null) return 0;
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return 0;

            var nview = source.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            ZDOID sourceId = nview.GetZDO().m_uid;

            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_APPLY_AOE_EFFECT,
                sourceId, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent AoE effect RPC: {effectName} from {source.m_name}");
            }
            
            // Apply locally and return count
            return ApplyAoEEffectLocal(source, effectName, range, duration);
        }
        
        /// <summary>
        /// Spawns a visual effect at a position. Synced via RPC.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <param name="effectName">Name of the VFX prefab.</param>
        /// <param name="scale">Scale multiplier.</param>
        public static void SpawnFX(Vector3 position, string effectName, float scale = 1f)
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_SPAWN_FX,
                position, effectName, scale);

            // Spawn locally
            AbilityFXManager.SpawnEffect(effectName, position, null, scale);
        }
        
        #endregion
        
        #region RPC Handlers
        
        private static void RPC_HandleGroupBuff(long sender, ZDOID sourceId, string effectName, float range, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            if (source == null) return;
            
            // Check if we're the sender - if so, we already applied locally
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            ApplyGroupBuffLocal(source, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received group buff RPC: {effectName} from {source.m_name}");
            }
        }
        
        private static void RPC_HandleSingleEffect(long sender, ZDOID sourceId, ZDOID targetId, string effectName, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            var target = FindCharacterByZDOID(targetId);
            if (source == null || target == null) return;
            
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            ApplySingleEffectLocal(source, target, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received single effect RPC: {effectName} on {target.m_name}");
            }
        }
        
        private static void RPC_HandleSelfBuff(long sender, ZDOID targetId, string effectName, float duration)
        {
            var target = FindCharacterByZDOID(targetId);
            if (target == null) return;
            
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            ApplySelfBuffLocal(target, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received self buff RPC: {effectName} on {target.m_name}");
            }
        }
        
        private static void RPC_HandleAoEEffect(long sender, ZDOID sourceId, string effectName, float range, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            if (source == null) return;
            
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            ApplyAoEEffectLocal(source, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received AoE effect RPC: {effectName} from {source.m_name}");
            }
        }
        
        private static void RPC_HandleSpawnFX(long sender, Vector3 position, string effectName, float scale)
        {
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            AbilityFXManager.SpawnEffect(effectName, position, null, scale);
        }
        
        #endregion
        
        #region Local Application
        
        /// <summary>
        /// Applies a group buff locally to all allies in range.
        /// TARGETING: Only allies - same-owner companions and their owner player.
        /// </summary>
        private static int ApplyGroupBuffLocal(Character source, string effectName, float range, float duration)
        {
            int count = 0;
            Vector3 sourcePos = source.transform.position;
            long ownerPlayerId = 0;
            
            // Get owner player ID for companion owner matching
            var companionController = source.GetComponent<CompanionController>();
            if (companionController != null)
            {
                ownerPlayerId = companionController.ownerPlayerId;
            }
            
            // Always log group buff applications for debugging
            Debug.Log($"[AbilityRPCManager] ApplyGroupBuffLocal: {effectName} from {source.m_name}, range={range}m, ownerPlayerId={ownerPlayerId}");
            
            // Find all allies in range
            var characters = Character.GetAllCharacters();
            int checkedCount = 0;
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                checkedCount++;
                
                // VALIDATION: Must be an ally (not an enemy)
                if (BaseAI.IsEnemy(source, character)) 
                {
                    if (VerboseLogging)
                        Debug.Log($"[AbilityRPCManager] Skipping {character.m_name} - is enemy");
                    continue;
                }
                
                float distance = Vector3.Distance(sourcePos, character.transform.position);
                if (distance > range) 
                {
                    if (VerboseLogging)
                        Debug.Log($"[AbilityRPCManager] Skipping {character.m_name} - too far ({distance:F1}m > {range}m)");
                    continue;
                }
                
                // VALIDATION: For companions, only buff same-owner companions and their owner
                if (companionController != null)
                {
                    if (character.IsPlayer())
                    {
                        var player = character as Player;
                        if (player == null)
                        {
                            Debug.Log($"[AbilityRPCManager] Skipping player - cast to Player failed");
                            continue;
                        }
                        
                        long playerId = player.GetPlayerID();
                        if (playerId != ownerPlayerId) 
                        {
                            Debug.Log($"[AbilityRPCManager] Skipping player {player.GetPlayerName()} - different owner ({playerId} != {ownerPlayerId})");
                            continue;
                        }
                        
                        Debug.Log($"[AbilityRPCManager] FOUND OWNER PLAYER: {player.GetPlayerName()} (ID: {playerId}) - applying {effectName}!");
                    }
                    else
                    {
                        // Skip monsters - they're not allies even if not "enemy" to source
                        var targetCompanion = character.GetComponent<CompanionController>();
                        if (targetCompanion == null) 
                        {
                            if (VerboseLogging)
                                Debug.Log($"[AbilityRPCManager] Skipping {character.m_name} - not a companion");
                            continue;
                        }
                        if (targetCompanion.ownerPlayerId != ownerPlayerId) 
                        {
                            if (VerboseLogging)
                                Debug.Log($"[AbilityRPCManager] Skipping companion {character.m_name} - different owner");
                            continue;
                        }
                    }
                }
                
                // Apply the effect
                if (ApplyEffectByName(character, effectName, duration, source))
                {
                    count++;
                    bool isPlayer = character.IsPlayer();
                    Debug.Log($"[AbilityRPCManager] APPLIED {effectName} to {character.m_name} (isPlayer={isPlayer})");
                    
                    // Notify player if they received a buff
                    NotifyPlayerOfBuff(character, effectName, duration, source);
                }
                else
                {
                    Debug.LogWarning($"[AbilityRPCManager] FAILED to apply {effectName} to {character.m_name}");
                }
            }
            
            Debug.Log($"[AbilityRPCManager] ApplyGroupBuffLocal complete: checked {checkedCount} characters, applied to {count}");
            
            // Play FX based on effect type
            PlayEffectFX(source, effectName, range, duration, true);
            
            return count;
        }
        
        /// <summary>
        /// Applies a single-target effect locally.
        /// </summary>
        private static bool ApplySingleEffectLocal(Character source, Character target, string effectName, float duration)
        {
            // VALIDATION: Determine if this is a buff or debuff and validate target accordingly
            bool isBuff = IsBuffEffect(effectName);
            
            if (isBuff)
            {
                // Buffs should only go on allies
                if (BaseAI.IsEnemy(source, target))
                {
                    if (VerboseLogging)
                    {
                        Debug.LogWarning($"[AbilityRPCManager] Blocked buff {effectName} on enemy {target.m_name}");
                    }
                    return false;
                }
            }
            else
            {
                // Debuffs should only go on enemies (or explicitly self for some effects)
                if (!BaseAI.IsEnemy(source, target) && target != source)
                {
                    // Allow debuffs on self (some mechanics might need this)
                    // But block debuffs on other allies
                    if (VerboseLogging)
                    {
                        Debug.LogWarning($"[AbilityRPCManager] Blocked debuff {effectName} on ally {target.m_name}");
                    }
                    return false;
                }
                
                // Never apply debuffs to players
                if (target.IsPlayer() && target != source)
                {
                    if (VerboseLogging)
                    {
                        Debug.LogWarning($"[AbilityRPCManager] Blocked debuff {effectName} on player");
                    }
                    return false;
                }
            }
            
            bool success = ApplyEffectByName(target, effectName, duration, source);
            
            if (success)
            {
                // Play FX on target
                PlayEffectFX(target, effectName, 0, duration, false);
                
                // Notify player if they received a buff
                if (isBuff)
                {
                    NotifyPlayerOfBuff(target, effectName, duration, source);
                }
            }
            
            return success;
        }
        
        /// <summary>
        /// Applies a self-buff locally.
        /// </summary>
        private static bool ApplySelfBuffLocal(Character target, string effectName, float duration)
        {
            bool success = ApplyEffectByName(target, effectName, duration, target);
            
            if (success)
            {
                // Play FX on self
                PlayEffectFX(target, effectName, 0, duration, false);
                
                // Notify player if they received a buff
                NotifyPlayerOfBuff(target, effectName, duration, target);
            }
            
            return success;
        }
        
        /// <summary>
        /// Applies an AoE effect locally to enemies in range.
        /// TARGETING: Only enemies - monsters and hostile characters (not players unless PvP).
        /// </summary>
        private static int ApplyAoEEffectLocal(Character source, string effectName, float range, float duration)
        {
            int count = 0;
            Vector3 sourcePos = source.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == source) continue;
                
                // VALIDATION: Must be an enemy
                if (!BaseAI.IsEnemy(source, character)) continue;
                
                // VALIDATION: Never hit players (unless explicit PvP - which we don't support yet)
                if (character.IsPlayer()) continue;
                
                // VALIDATION: Must have AI (is a monster/creature)
                var ai = character.GetComponent<BaseAI>();
                if (ai == null)
                {
                    // Could be a hostile companion - check if it's an enemy companion
                    var companionController = character.GetComponent<CompanionController>();
                    if (companionController == null) continue; // Unknown hostile entity without AI
                }
                
                float distance = Vector3.Distance(sourcePos, character.transform.position);
                if (distance > range) continue;
                
                if (ApplyEffectByName(character, effectName, duration, source))
                {
                    count++;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[AbilityRPCManager] Applied AoE {effectName} to enemy: {character.m_name}");
                    }
                }
            }
            
            // Play AoE FX
            PlayEffectFX(source, effectName, range, duration, true);
            
            return count;
        }
        
        #endregion
        
        #region Effect Application
        
        /// <summary>
        /// Applies a status effect by name using the StatusEffectManager.
        /// CRITICAL: All effects must have their icon set for HUD display!
        /// </summary>
        private static bool ApplyEffectByName(Character target, string effectName, float duration, Character source = null)
        {
            if (target == null || string.IsNullOrEmpty(effectName)) return false;
            
            bool success = false;
            
            // Map effect names to StatusEffectManager methods
            switch (effectName)
            {
                // Tank
                case StatusEffectManager.EFFECT_FORTIFY:
                    success = StatusEffectManager.ApplyFortify(target, duration);
                    break;
                    
                // Paladin
                case StatusEffectManager.EFFECT_DIVINE_PROTECTION:
                    success = ApplyEffectDirect<StatusEffects.Paladin.DivineProtectionEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_HOLY_SMITE:
                    success = StatusEffectManager.ApplyHolySmite(target, duration);
                    break;
                    
                // Berserker
                case StatusEffectManager.EFFECT_BERSERK_RAGE:
                    success = StatusEffectManager.ApplyBerserkRage(target, duration);
                    break;
                case StatusEffectManager.EFFECT_WARCRY:
                    success = ApplyEffectDirect<StatusEffects.Berserker.WarcryEffect>(target, effectName, duration, source);
                    break;
                    
                // Rogue
                case StatusEffectManager.EFFECT_POISON:
                    success = StatusEffectManager.ApplyPoison(target, source, duration);
                    break;
                case StatusEffectManager.EFFECT_STEALTH:
                    success = StatusEffectManager.ApplyStealth(target, duration);
                    break;
                case StatusEffectManager.EFFECT_CALTROPS:
                    success = StatusEffectManager.ApplyCaltrops(target, duration, source);
                    break;
                    
                // Ranger
                case StatusEffectManager.EFFECT_HUNTERS_MARK:
                    success = StatusEffectManager.ApplyHuntersMark(target, source, duration);
                    break;
                case StatusEffectManager.EFFECT_EAGLE_EYE:
                    success = StatusEffectManager.ApplyEagleEye(target, duration);
                    break;
                    
                // Mage
                case StatusEffectManager.EFFECT_ELEMENTAL_INFUSION:
                    success = StatusEffectManager.ApplyElementalInfusion(target, duration);
                    break;
                case StatusEffectManager.EFFECT_ARCANE_SHIELD:
                    success = StatusEffectManager.ApplyArcaneShield(target, duration);
                    break;
                    
                // Healer
                case StatusEffectManager.EFFECT_PURIFY:
                    success = StatusEffectManager.ApplyPurify(target, duration);
                    break;
                case StatusEffectManager.EFFECT_SANCTUARY:
                    success = ApplyEffectDirect<StatusEffects.Healer.SanctuaryEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_PURIFYING_CIRCLE:
                    success = ApplyEffectDirect<StatusEffects.Healer.PurifyingCircleEffect>(target, effectName, duration, source);
                    break;
                    
                // Monk
                case StatusEffectManager.EFFECT_CHI_STRIKE:
                    success = StatusEffectManager.ApplyChiStrike(target, duration);
                    break;
                case StatusEffectManager.EFFECT_INNER_PEACE:
                    success = StatusEffectManager.ApplyInnerPeace(target, duration);
                    break;
                    
                // Common
                case StatusEffectManager.EFFECT_INVULNERABLE:
                    success = StatusEffectManager.ApplyInvulnerable(target, duration);
                    break;
                case StatusEffectManager.EFFECT_ROOTED:
                    success = StatusEffectManager.ApplyRoot(target, duration, source);
                    break;
                case StatusEffectManager.EFFECT_SLOWDOWN:
                    success = StatusEffectManager.ApplySlowdown(target, duration, 0.5f, source);
                    break;
                    
                default:
                    if (VerboseLogging)
                    {
                        Debug.LogWarning($"[AbilityRPCManager] Unknown effect name: {effectName}");
                    }
                    return false;
            }
            
            if (success && VerboseLogging)
            {
                string targetType = target.IsPlayer() ? "PLAYER" : (target.GetComponent<CompanionController>() != null ? "COMPANION" : "NPC");
                Debug.Log($"[AbilityRPCManager] Applied {effectName} to {target.m_name} ({targetType}) for {duration}s");
            }
            
            return success;
        }
        
        /// <summary>
        /// Generic method to apply a status effect directly with proper icon setup.
        /// This ensures all effects have icons for HUD display.
        /// 
        /// CRITICAL: m_icon MUST be set on the StatusEffect base class BEFORE AddStatusEffect
        /// because the game clones the effect and may not copy custom C# properties.
        /// </summary>
        private static bool ApplyEffectDirect<T>(Character target, string effectName, float duration, Character source) 
            where T : StatusEffects.CompanionStatusEffectBase
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            // Create the effect
            var effect = ScriptableObject.CreateInstance<T>();
            effect.name = effectName;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            // CRITICAL: Get the icon and set it on BOTH our property AND the base class m_icon field
            // The clone process copies m_icon but may not copy our EffectIcon property
            var icon = StatusEffectManager.GetEffectIcon(effectName);
            effect.EffectIcon = icon;
            effect.m_icon = icon;  // Set directly on base class so clone gets it
            
            // Also set m_ttl directly for duration (clone should copy this)
            effect.m_ttl = duration;
            
            // Remove existing effect of same type
            int hash = effectName.GetStableHashCode();
            if (seman.HaveStatusEffect(hash))
            {
                seman.RemoveStatusEffect(hash, true);
            }
            
            // Add the new effect
            seman.AddStatusEffect(effect, true);
            
            // Log for debugging
            Debug.Log($"[AbilityRPCManager] ApplyEffectDirect: {effectName} to {target.m_name}, hasIcon={icon != null}, isPlayer={target.IsPlayer()}");
            
            return true;
        }
        
        /// <summary>
        /// Plays visual effects based on the ability type.
        /// </summary>
        private static void PlayEffectFX(Character character, string effectName, float range, float duration, bool isAoE)
        {
            if (character == null) return;
            
            switch (effectName)
            {
                case StatusEffectManager.EFFECT_FORTIFY:
                    AbilityFXManager.PlayFortifyEffect(character, duration);
                    break;
                case StatusEffectManager.EFFECT_DIVINE_PROTECTION:
                    AbilityFXManager.PlayDivineProtectionEffect(character, range, duration);
                    break;
                case StatusEffectManager.EFFECT_BERSERK_RAGE:
                    AbilityFXManager.PlayBerserkRageEffect(character, duration);
                    break;
                case StatusEffectManager.EFFECT_WARCRY:
                    AbilityFXManager.PlayWarcryEffect(character, range);
                    break;
                case StatusEffectManager.EFFECT_STEALTH:
                    AbilityFXManager.PlayStealthEffect(character);
                    break;
                case StatusEffectManager.EFFECT_POISON:
                    AbilityFXManager.PlayPoisonEffect(character);
                    break;
                case StatusEffectManager.EFFECT_HUNTERS_MARK:
                    AbilityFXManager.PlayHuntersMarkEffect(character, duration);
                    break;
                case StatusEffectManager.EFFECT_EAGLE_EYE:
                    AbilityFXManager.PlayEagleEyeEffect(character);
                    break;
                case StatusEffectManager.EFFECT_ELEMENTAL_INFUSION:
                    AbilityFXManager.PlayElementalInfusionEffect(character, duration);
                    break;
                case StatusEffectManager.EFFECT_ARCANE_SHIELD:
                    AbilityFXManager.PlayArcaneShieldEffect(character, duration);
                    break;
                case StatusEffectManager.EFFECT_PURIFY:
                    AbilityFXManager.PlayPurifyEffect(character);
                    break;
                case StatusEffectManager.EFFECT_SANCTUARY:
                    if (isAoE) AbilityFXManager.PlaySanctuaryEffect(character, range, duration);
                    break;
                case StatusEffectManager.EFFECT_PURIFYING_CIRCLE:
                    AbilityFXManager.PlayPurifyingCircleEffect(character, range);
                    break;
                case StatusEffectManager.EFFECT_CHI_STRIKE:
                    AbilityFXManager.PlayChiStrikeEffect(character);
                    break;
                case StatusEffectManager.EFFECT_INNER_PEACE:
                    AbilityFXManager.PlayInnerPeaceEffect(character, range, duration);
                    break;
                case StatusEffectManager.EFFECT_HOLY_SMITE:
                    AbilityFXManager.PlayHolySmiteEffect(character);
                    break;
            }
        }
        
        #endregion
        
        #region Helpers
        
        /// <summary>
        /// Finds a character by its ZDOID.
        /// </summary>
        private static Character FindCharacterByZDOID(ZDOID zdoid)
        {
            if (zdoid == ZDOID.None) return null;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null) continue;
                var nview = character.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;
                if (nview.GetZDO()?.m_uid == zdoid) return character;
            }
            
            return null;
        }
        
        /// <summary>
        /// Determines if an effect is a buff (beneficial) or debuff (harmful).
        /// </summary>
        private static bool IsBuffEffect(string effectName)
        {
            // Debuffs (harmful effects that go on enemies)
            switch (effectName)
            {
                case StatusEffectManager.EFFECT_POISON:
                case StatusEffectManager.EFFECT_CALTROPS:
                case StatusEffectManager.EFFECT_HUNTERS_MARK:
                case StatusEffectManager.EFFECT_ROOTED:
                case StatusEffectManager.EFFECT_SLOWDOWN:
                    return false;
            }
            
            // Everything else is a buff
            return true;
        }
        
        /// <summary>
        /// Gets a friendly display name for an effect.
        /// </summary>
        private static string GetEffectDisplayName(string effectName)
        {
            switch (effectName)
            {
                case StatusEffectManager.EFFECT_FORTIFY: return "Fortify";
                case StatusEffectManager.EFFECT_DIVINE_PROTECTION: return "Divine Protection";
                case StatusEffectManager.EFFECT_HOLY_SMITE: return "Holy Smite";
                case StatusEffectManager.EFFECT_BERSERK_RAGE: return "Berserk Rage";
                case StatusEffectManager.EFFECT_WARCRY: return "Warcry";
                case StatusEffectManager.EFFECT_STEALTH: return "Stealth";
                case StatusEffectManager.EFFECT_EAGLE_EYE: return "Eagle Eye";
                case StatusEffectManager.EFFECT_HUNTERS_MARK: return "Hunter's Mark";
                case StatusEffectManager.EFFECT_ELEMENTAL_INFUSION: return "Elemental Infusion";
                case StatusEffectManager.EFFECT_ARCANE_SHIELD: return "Arcane Shield";
                case StatusEffectManager.EFFECT_PURIFY: return "Purify";
                case StatusEffectManager.EFFECT_SANCTUARY: return "Sanctuary";
                case StatusEffectManager.EFFECT_PURIFYING_CIRCLE: return "Purifying Circle";
                case StatusEffectManager.EFFECT_CHI_STRIKE: return "Chi Strike";
                case StatusEffectManager.EFFECT_INNER_PEACE: return "Inner Peace";
                case StatusEffectManager.EFFECT_INVULNERABLE: return "Invulnerable";
                default:
                    // Convert effect name to display name (remove prefix, add spaces)
                    if (effectName.StartsWith("Companion"))
                    {
                        return System.Text.RegularExpressions.Regex.Replace(
                            effectName.Substring(9), // Remove "Companion" prefix
                            "([A-Z])", " $1").Trim();
                    }
                    return effectName;
            }
        }
        
        /// <summary>
        /// Notifies the local player when they receive a buff from a companion.
        /// Shows a HUD message and the status effect icon.
        /// </summary>
        private static void NotifyPlayerOfBuff(Character target, string effectName, float duration, Character source)
        {
            // Only notify if target is the local player
            if (target == null || !target.IsPlayer()) 
            {
                if (VerboseLogging)
                    Debug.Log($"[AbilityRPCManager] NotifyPlayerOfBuff: target is not player ({target?.m_name ?? "null"})");
                return;
            }
            if (target != Player.m_localPlayer) 
            {
                if (VerboseLogging)
                    Debug.Log($"[AbilityRPCManager] NotifyPlayerOfBuff: target is not LOCAL player");
                return;
            }
            
            // Get friendly names
            string effectDisplayName = GetEffectDisplayName(effectName);
            string sourceName = "Companion";
            
            // Get companion name if available
            if (source != null)
            {
                var companion = source.GetComponent<CompanionController>();
                if (companion != null && !string.IsNullOrEmpty(companion.companionName))
                {
                    sourceName = companion.companionName;
                }
                else
                {
                    sourceName = source.m_name;
                }
            }
            
            // Show message in top-left
            string message = $"<color=#88ff88>{sourceName}</color> granted you <color=#ffff88>{effectDisplayName}</color> ({duration:F0}s)";
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
            
            // Always log player buff notifications
            Debug.Log($"[AbilityRPCManager] PLAYER BUFF NOTIFICATION: {effectName} from {sourceName} - message shown!");
        }
        
        #endregion
    }
}
