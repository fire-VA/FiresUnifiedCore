using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;
using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// The multiplayer path for every companion ability and status effect: the owner triggers ApplyAbility, a
    /// routed RPC reaches all clients, and each applies the effect and spawns the FX locally. Targets are
    /// validated per effect: group buffs reach only the owner and their companions, AoE damage only enemies.
    /// </summary>
    public static class AbilityRPCManager
    {
        public static bool VerboseLogging = false;
        
        // RPC names for different ability types
        private const string RpcApplyGroupBuff = "RPC_CompanionGroupBuff";
        private const string RpcApplySingleEffect = "RPC_CompanionSingleEffect";
        private const string RpcApplySelfBuff = "RPC_CompanionSelfBuff";
        private const string RpcApplyAoeEffect = "RPC_CompanionAoEEffect";
        private const string RpcSpawnFX = "RPC_CompanionSpawnFX";
        private const string RpcRemoveSelfBuff = "RPC_CompanionRemoveSelfBuff";

        private static ZRoutedRpc _registeredOn;

        /// <summary>
        /// Registers the RPC handlers on the current session's ZRoutedRpc. ZNet rebuilds ZRoutedRpc every session, so
        /// this runs from the ZNet.Start postfix (TamedCompanionZoneLoader); repeat calls within a session are no-ops.
        /// </summary>
        public static void Initialize()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(_registeredOn, rpc)) return;

            TryRegister(() => rpc.Register<ZDOID, string, float, float>(RpcApplyGroupBuff, RPC_HandleGroupBuff), RpcApplyGroupBuff);
            TryRegister(() => rpc.Register<ZDOID, ZDOID, string, float>(RpcApplySingleEffect, RPC_HandleSingleEffect), RpcApplySingleEffect);
            TryRegister(() => rpc.Register<ZDOID, string, float>(RpcApplySelfBuff, RPC_HandleSelfBuff), RpcApplySelfBuff);
            TryRegister(() => rpc.Register<ZDOID, string, float, float>(RpcApplyAoeEffect, RPC_HandleAoEEffect), RpcApplyAoeEffect);
            TryRegister(() => rpc.Register<Vector3, string, float>(RpcSpawnFX, RPC_HandleSpawnFX), RpcSpawnFX);
            TryRegister(() => rpc.Register<ZDOID, string>(RpcRemoveSelfBuff, RPC_HandleRemoveSelfBuff), RpcRemoveSelfBuff);
            TryRegister(() => AbilityHeals.RegisterRpc(rpc), AbilityHeals.RpcApplyHeal);

            _registeredOn = rpc;

            if (VerboseLogging)
            {
                Debug.Log("[AbilityRPCManager] Initialized RPC handlers for companion abilities");
            }
        }

        private static void TryRegister(System.Action register, string rpcName)
        {
            try { register(); }
            catch (System.Exception ex) { Debug.LogWarning($"[AbilityRPCManager] Could not register {rpcName}: {ex.Message}"); }
        }

        /// <summary>
        /// When a class cast must not go out: only while the local player is being rebuilt (respawn, loading: the respawn
        /// freeze CompanionPatches guards) or teleporting. Not while merely staggered: AreCompanionTeleportsSuppressed's
        /// !CanMove branch (and the timer it re-arms) made a staggered player's skills fail silently (R49: ChiStrike right
        /// after a stagger). The companion paths keep that gate.
        /// </summary>
        public static bool CastBlocked()
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return false;
            if (Game.instance == null) return false;
            Player local = Player.m_localPlayer;
            return local == null || local.IsTeleporting();
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
            // window — RPCs broadcast while IsTeleporting=true deadlock the zone stream
            // (documented in CompanionPatches.cs). Suppressed buffs simply re-trigger
            // from the next Update tick once the player can move again.
            if (CastBlocked()) return 0;

            var nview = source.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            ZDOID sourceId = nview.GetZDO().m_uid;

            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcApplyGroupBuff,
                sourceId, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent group buff RPC: {effectName} from {source.m_name}, range={range}m, duration={duration}s");
            }
            
            NoteBuffCast(source);
            // Apply locally and return count
            using (AbilityFXManager.LocalCopies()) return ApplyGroupBuffLocal(source, effectName, range, duration);
        }

        // ArchetypeStatistics.BuffsApplied (read by companion_test's skills step on the caster's owner) was never fed: one per buff a
        // companion casts, on the peer that owns it (the one that casts).
        private static void NoteBuffCast(Character caster)
        {
            var companion = caster != null ? caster.GetComponent<CompanionController>() : null;
            var archetype = companion != null ? companion.GetArchetypeController() : null;
            if (archetype != null && archetype.Statistics != null) archetype.Statistics.RecordBuffApplied();
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
            if (CastBlocked()) return false;

            var sourceNview = source.GetComponent<ZNetView>();
            var targetNview = target.GetComponent<ZNetView>();
            if (sourceNview == null || targetNview == null) return false;
            if (!sourceNview.IsValid() || !targetNview.IsValid()) return false;
            
            ZDOID sourceId = sourceNview.GetZDO().m_uid;
            ZDOID targetId = targetNview.GetZDO().m_uid;
            
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcApplySingleEffect,
                sourceId, targetId, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent single effect RPC: {effectName} from {source.m_name} to {target.m_name}");
            }
            
            if (IsBuffEffect(effectName)) NoteBuffCast(source);
            // Apply locally
            using (AbilityFXManager.LocalCopies()) return ApplySingleEffectLocal(source, target, effectName, duration);
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
            if (CastBlocked()) return false;

            var nview = target.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            ZDOID targetId = nview.GetZDO().m_uid;
            
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcApplySelfBuff,
                targetId, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent self buff RPC: {effectName} on {target.m_name}");
            }
            
            NoteBuffCast(target);
            // Apply locally
            using (AbilityFXManager.LocalCopies()) return ApplySelfBuffLocal(target, effectName, duration);
        }
        
        /// <summary>Removes a self-buff (for example a passive the companion's new archetype does not have) on every client.</summary>
        public static void RemoveSelfBuff(Character target, string effectName)
        {
            if (target == null) return;
            var nview = target.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRemoveSelfBuff, nview.GetZDO().m_uid, effectName);
            StatusEffectManager.RemoveEffect(target, effectName);
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
            if (CastBlocked()) return 0;

            var nview = source.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;

            ZDOID sourceId = nview.GetZDO().m_uid;

            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcApplyAoeEffect,
                sourceId, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Sent AoE effect RPC: {effectName} from {source.m_name}");
            }
            
            // Apply locally and return count
            using (AbilityFXManager.LocalCopies()) return ApplyAoEEffectLocal(source, effectName, range, duration);
        }
        
        /// <summary>
        /// Spawns a visual effect at a position. Synced via RPC.
        /// </summary>
        /// <param name="position">World position.</param>
        /// <param name="effectName">Name of the VFX prefab.</param>
        /// <param name="scale">Scale multiplier.</param>
        public static void SpawnFX(Vector3 position, string effectName, float scale = 1f)
        {
            if (CastBlocked()) return;
            // Send RPC to all clients
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcSpawnFX,
                position, effectName, scale);

            // Spawn locally
            using (AbilityFXManager.LocalCopies()) AbilityFXManager.SpawnEffect(effectName, position, null, scale);
        }
        
        #endregion
        
        #region RPC Handlers
        
        private static void RPC_HandleGroupBuff(long sender, ZDOID sourceId, string effectName, float range, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            if (source == null) return;
            
            // Check if we're the sender - if so, we already applied locally
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            using (AbilityFXManager.LocalCopies()) ApplyGroupBuffLocal(source, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received group buff RPC: {effectName} from {source.m_name}");
            }
        }
        
        private static void RPC_HandleSingleEffect(long sender, ZDOID sourceId, ZDOID targetId, string effectName, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            var target = FindCharacterByZDOID(targetId);
            if (target == null) return;

            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;

            if (source == null)
            {
                // The caster isn't loaded here (out of this peer's zones), but the target is and may be ours: the owner must still
                // apply it or nobody does (class_test R54 se_sync: Caltrops / Death Mark / Hunter's Mark never reached the owner).
                // The sender already checked who it may hit before sending.
                if (IsOwnedHere(target))
                {
                    Debug.Log($"[AbilityRPCManager] {effectName} on {target.m_name}: caster not loaded here, applying as the target's owner");
                    using (AbilityFXManager.LocalCopies()) ApplyOnOwner(target, effectName, duration, null);
                }
                return;
            }

            using (AbilityFXManager.LocalCopies()) ApplySingleEffectLocal(source, target, effectName, duration);
            
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
            
            using (AbilityFXManager.LocalCopies()) ApplySelfBuffLocal(target, effectName, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received self buff RPC: {effectName} on {target.m_name}");
            }
        }
        
        private static void RPC_HandleRemoveSelfBuff(long sender, ZDOID targetId, string effectName)
        {
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            var target = FindCharacterByZDOID(targetId);
            if (target == null) return;
            StatusEffectManager.RemoveEffect(target, effectName);
        }

        private static void RPC_HandleAoEEffect(long sender, ZDOID sourceId, string effectName, float range, float duration)
        {
            var source = FindCharacterByZDOID(sourceId);
            if (source == null) return;
            
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            using (AbilityFXManager.LocalCopies()) ApplyAoEEffectLocal(source, effectName, range, duration);
            
            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Received AoE effect RPC: {effectName} from {source.m_name}");
            }
        }
        
        private static void RPC_HandleSpawnFX(long sender, Vector3 position, string effectName, float scale)
        {
            // Check if we're the sender
            if (ZNet.instance != null && ZNet.GetUID() == sender) return;
            
            using (AbilityFXManager.LocalCopies()) AbilityFXManager.SpawnEffect(effectName, position, null, scale);
        }
        
        #endregion
        
        #region Local Application

        // Vanilla keeps a status effect only where its character is owned: SEMan.Update (ticks, expiry, Stop) runs only on
        // the owner. The routed RPC still reaches every peer so every peer draws the FX, but only the target's owner adds the
        // effect. A copy on any other peer never ticked, never expired, and ran OnEffectApplied once per peer (Lay on Hands
        // healed and Chi Explosion hit once per peer). The caster's own local call covers the targets it owns and each RPC
        // receiver covers its own, so every target gets the effect exactly once. An ownership handover mid-effect drops it,
        // as in vanilla. Returns "applied here, or the target isn't ours", so group counts see every valid target once.
        // Setup runs on this peer only, so the FX it spawns are networked copies (every peer sees them once), not the
        // local copies the surrounding RPC scope makes.
        private static bool ApplyOnOwner(Character target, string effectName, float duration, Character source)
        {
            if (!IsOwnedHere(target)) return true;
            using (AbilityFXManager.NetworkedCopies()) return ApplyEffectByName(target, effectName, duration, source);
        }

        private static bool IsOwnedHere(Character character)
        {
            var nview = character != null ? character.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() && nview.IsOwner();
        }

        /// <summary>
        /// Applies a group buff to every party member of the caster in range (Fire: heals and buffs only the party:
        /// see <see cref="ClassTargeting"/>).
        /// </summary>
        private static int ApplyGroupBuffLocal(Character source, string effectName, float range, float duration)
        {
            int count = 0;
            Vector3 sourcePos = source.transform.position;

            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (Vector3.Distance(sourcePos, character.transform.position) > range) continue;
                if (!ClassTargeting.IsPartyMember(source, character)) continue;

                if (ApplyOnOwner(character, effectName, duration, source))
                {
                    count++;
                    NotifyPlayerOfBuff(character, effectName, duration, source);
                }
                else
                {
                    Debug.LogWarning($"[AbilityRPCManager] FAILED to apply {effectName} to {character.m_name}");
                }
            }

            if (VerboseLogging)
            {
                Debug.Log($"[AbilityRPCManager] Group {effectName} from {source.m_name}: {count} party member(s) within {range}m");
            }

            // Play FX based on effect type
            PlayEffectFX(source, effectName, range, duration, true);

            return count;
        }

        /// <summary>
        /// Applies a single-target effect: a buff only on the caster's party, a debuff only on an enemy (a player only
        /// when both have PvP on).
        /// </summary>
        private static bool ApplySingleEffectLocal(Character source, Character target, string effectName, float duration)
        {
            bool isBuff = IsBuffEffect(effectName);
            bool allowed = isBuff ? ClassTargeting.IsPartyMember(source, target) : ClassTargeting.IsEnemyTarget(source, target);
            if (!allowed)
            {
                if (VerboseLogging)
                {
                    Debug.LogWarning($"[AbilityRPCManager] Blocked {(isBuff ? "buff" : "debuff")} {effectName} on {target.m_name}");
                }
                return false;
            }

            bool success = ApplyOnOwner(target, effectName, duration, source);

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
        /// Applies a self-buff.
        /// </summary>
        private static bool ApplySelfBuffLocal(Character target, string effectName, float duration)
        {
            bool success = ApplyOnOwner(target, effectName, duration, target);

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
        /// Applies an AoE effect to every enemy in range: hostile creatures, and players only when both have PvP on.
        /// </summary>
        private static int ApplyAoEEffectLocal(Character source, string effectName, float range, float duration)
        {
            int count = 0;
            Vector3 sourcePos = source.transform.position;

            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead() || character == source) continue;
                if (Vector3.Distance(sourcePos, character.transform.position) > range) continue;
                if (!ClassTargeting.IsEnemyTarget(source, character)) continue;

                if (ApplyOnOwner(character, effectName, duration, source))
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

                // Permanent passives (duration 0)
                case StatusEffectManager.EFFECT_IRON_WALL:
                    success = ApplyEffectDirect<StatusEffects.Expert.IronWallEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_UNYIELDING:
                    success = ApplyEffectDirect<StatusEffects.Master.UnyieldingEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_EXECUTE:
                    success = ApplyEffectDirect<StatusEffects.Expert.ExecuteEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_RAMPAGE:
                    success = ApplyEffectDirect<StatusEffects.Expert.RampageEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_DEATH_WISH:
                    success = ApplyEffectDirect<StatusEffects.Master.DeathWishEffect>(target, effectName, duration, source);
                    break;
                case StatusEffectManager.EFFECT_CHAIN_CASTING:
                    success = ApplyEffectDirect<StatusEffects.Expert.ChainCastingEffect>(target, effectName, duration, source);
                    break;

                default:
                    // Every other class effect StatusEffectManager registers (Avatar of Light, Divine Shield, Consecration,
                    // Lay on Hands, Meteor, Death Mark ... 24 of the 50) had no case here, so a companion casting one
                    // applied nothing (class_test R37). They go on through the type they were registered with.
                    success = ApplyRegisteredEffect(target, effectName, duration, source);
                    if (!success)
                    {
                        if (VerboseLogging)
                        {
                            Debug.LogWarning($"[AbilityRPCManager] Unknown effect name: {effectName}");
                        }
                        return false;
                    }
                    break;
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
            return ApplyEffectOfType(typeof(T), target, effectName, duration, source);
        }

        /// <summary>A class effect with no case of its own: the type StatusEffectManager registered under its name.</summary>
        private static bool ApplyRegisteredEffect(Character target, string effectName, float duration, Character source)
        {
            if (ObjectDB.instance == null) return false;
            StatusEffect template = ObjectDB.instance.GetStatusEffect(effectName.GetStableHashCode());
            if (!(template is StatusEffects.CompanionStatusEffectBase)) return false;
            return ApplyEffectOfType(template.GetType(), target, effectName, duration, source);
        }

        private static bool ApplyEffectOfType(System.Type effectType, Character target, string effectName, float duration, Character source)
        {
            if (target == null) return false;

            var seman = target.GetSEMan();
            if (seman == null) return false;

            // Create the effect
            var effect = (StatusEffects.CompanionStatusEffectBase)ScriptableObject.CreateInstance(effectType);
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
        internal static Character FindCharacterByZDOID(ZDOID zdoid)
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
                case StatusEffectManager.EFFECT_DEATH_MARK: // the Rogue marks an enemy; as a "buff" it was refused on one
                case StatusEffectManager.EFFECT_CLASS_STUN:
                case StatusEffectManager.EFFECT_CLASS_KNOCKDOWN:
                case StatusEffectManager.EFFECT_CLASS_FLEE:
                case StatusEffectManager.EFFECT_CLASS_LOSE_TARGET:
                case StatusEffectManager.EFFECT_CLASS_SLOW30:
                case StatusEffectManager.EFFECT_CLASS_SLOW20:
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
