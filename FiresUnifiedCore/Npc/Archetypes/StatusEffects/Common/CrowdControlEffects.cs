using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Common
{
    // The class kit's crowd control (Fire, popup 2026-09-28 21:3x: "build them simply"). Each is a status effect applied on
    // the TARGET's owner through AbilityRPCManager.ApplySingleEffect (ClassTargeting decides who may be hit), so it holds
    // on every peer; ClassCrowdControl is the API. No Harmony hooks: every one works through vanilla's own paths.

    /// <summary>
    /// Stun: vanilla's stagger, held for the duration. A staggering character can neither move nor start an attack
    /// (Humanoid.StartAttack refuses while IsStaggering), creature or player.
    /// </summary>
    public class StunEffect : CompanionStatusEffectBase
    {
        public override string Description => "Stunned: cannot move or attack";

        public StunEffect()
        {
            m_name = "Stunned";
            m_tooltip = "Cannot move or attack";
            Duration = 2f;
        }

        protected override void OnEffectApplied() => Hold();

        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            Hold();
        }

        private void Hold()
        {
            if (m_character == null || m_character.IsDead() || m_character.IsStaggering()) return;
            Vector3 away = SourceCharacter != null ? m_character.transform.position - SourceCharacter.transform.position : -m_character.transform.forward;
            m_character.Stagger(away);
        }

        public new StunEffect Clone()
        {
            var clone = (StunEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }

    /// <summary>Knockdown: one stagger and a push away from the caster (vanilla's own pushback), at once.</summary>
    public class KnockdownEffect : CompanionStatusEffectBase
    {
        // Vanilla's medium push: a club's (Fire's default rule for unstated knockback).
        public const float DefaultForce = 60f;

        public float Force { get; set; } = DefaultForce;

        public override string Description => "Knocked down";

        public KnockdownEffect()
        {
            m_name = "Knocked Down";
            m_tooltip = "Knocked off your feet";
            Duration = 1f;
        }

        protected override void OnEffectApplied()
        {
            if (m_character == null || m_character.IsDead()) return;
            Vector3 away = SourceCharacter != null ? m_character.transform.position - SourceCharacter.transform.position : -m_character.transform.forward;
            m_character.Stagger(away);
            m_character.ApplyPushback(away, Force);
        }

        public new KnockdownEffect Clone()
        {
            var clone = (KnockdownEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
                clone.Force = Force;
            }
            return clone;
        }
    }

    /// <summary>
    /// Flee: a monster runs from the caster for the duration, through MonsterAI's own low-health flee branch (forced on
    /// while the effect lasts, the prefab's value put back after). Creatures without a MonsterAI ignore it.
    /// </summary>
    public class FleeEffect : CompanionStatusEffectBase
    {
        private const float AlwaysFlee = 1.01f;

        private MonsterAI _ai;
        private float _savedFleeIfLowHealth;

        public override string Description => "Terrified: fleeing";

        public FleeEffect()
        {
            m_name = "Fleeing";
            m_tooltip = "Running in terror";
            Duration = 4f;
        }

        protected override void OnEffectApplied()
        {
            _ai = m_character != null ? m_character.GetBaseAI() as MonsterAI : null;
            if (_ai == null) return;
            _savedFleeIfLowHealth = _ai.m_fleeIfLowHealth < AlwaysFlee ? _ai.m_fleeIfLowHealth : PrefabFleeIfLowHealth();
            Force();
        }

        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            Force();
        }

        // MonsterAI.UpdateAI flees from its target when m_fleeIfLowHealth > health share and it was hurt recently.
        private void Force()
        {
            if (_ai == null) return;
            _ai.m_fleeIfLowHealth = AlwaysFlee;
            _ai.m_timeSinceHurt = 0f;
            if (_ai.m_targetCreature == null && SourceCharacter != null) _ai.m_targetCreature = SourceCharacter;
        }

        protected override void OnEffectRemoved()
        {
            if (_ai != null) _ai.m_fleeIfLowHealth = _savedFleeIfLowHealth;
        }

        // Another flee already forced this one's value: the value to put back is the prefab's, never the forced one, or the
        // creature would flee for good once the effect ends.
        private float PrefabFleeIfLowHealth()
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(m_character.gameObject)) : null;
            MonsterAI prefabAi = prefab != null ? prefab.GetComponent<MonsterAI>() : null;
            return prefabAi != null ? prefabAi.m_fleeIfLowHealth : 0f;
        }

        public new FleeEffect Clone()
        {
            var clone = (FleeEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }

    /// <summary>Lose target: a monster forgets what it was after and calms down (it may find a target again later).</summary>
    public class LoseTargetEffect : CompanionStatusEffectBase
    {
        public override string Description => "Lost its target";

        public LoseTargetEffect()
        {
            m_name = "Confused";
            m_tooltip = "Lost track of its target";
            Duration = 0.5f;
        }

        protected override void OnEffectApplied()
        {
            MonsterAI ai = m_character != null ? m_character.GetBaseAI() as MonsterAI : null;
            if (ai == null) return;
            ai.m_targetCreature = null;
            ai.m_targetStatic = null;
            ai.SetAlerted(false);
        }

        public new LoseTargetEffect Clone()
        {
            var clone = (LoseTargetEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }

    /// <summary>
    /// Stagger immunity: hits neither stagger nor push the carrier. OnDamaged runs in Character.RPC_Damage before the
    /// stagger damage and the pushback are applied. (A hit with a stagger multiplier of 100+ staggers earlier in
    /// RPC_Damage, before any status effect sees it; those are rare scripted hits.)
    /// </summary>
    public class StaggerImmuneEffect : CompanionStatusEffectBase
    {
        public override string Description => "Unshakable: no stagger or knockback";

        public StaggerImmuneEffect()
        {
            m_name = "Unshakable";
            m_tooltip = "Cannot be staggered or knocked back";
            Duration = 8f;
        }

        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            if (hit == null) return;
            hit.m_staggerMultiplier = 0f;
            hit.m_pushForce = 0f;
        }

        public new StaggerImmuneEffect Clone()
        {
            var clone = (StaggerImmuneEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
