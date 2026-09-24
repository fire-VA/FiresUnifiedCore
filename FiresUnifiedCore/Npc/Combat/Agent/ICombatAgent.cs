using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// One brain, two bodies: everything a combat behaviour needs from the thing it is driving.
    /// CompanionAgent wraps CompanionAI; FiresMonsters' MonsterAgent wraps vanilla MonsterAI.
    /// Plan: Docs/PLAN_CombatAgent.md
    /// </summary>
    public interface ICombatAgent
    {
        Character Body { get; }
        Humanoid Humanoid { get; }
        BaseAI Ai { get; }
        ZNetView NView { get; }
        Transform Transform { get; }

        Vector3 Position { get; }
        Vector3 EyePosition { get; }
        float Radius { get; }

        Character.Faction Faction { get; }
        string FactionGroup { get; }

        bool IsOwner { get; }
        bool IsAlive { get; }
        float HealthFraction { get; }
        bool IsStaggering { get; }

        CombatAgentCapability Capabilities { get; }
        bool Can(CombatAgentCapability capability);

        Character Target { get; }
        bool TrySetTarget(Character target);

        /// <summary>Drives one frame of movement toward a point. True once the point is reached.</summary>
        bool MoveToward(Vector3 point, bool run, float reachDistance);
        void LookToward(Vector3 point);
        void StopMoving();

        bool IsAttacking { get; }
        bool TryAttack(Character target);

        /// <summary>Swings now, outside the behaviour's own cooldown. The parry counter-attack.</summary>
        bool ForceAttackNow();

        /// <summary>The body's stamina, or null when it has none.</summary>
        ICombatStamina Stamina { get; }

        /// <summary>The body's weapon skills, or null when it keeps none.</summary>
        ICombatSkills Skills { get; }

        /// <summary>True when something arbitrates this body's facing, so a behaviour must not rotate it itself.</summary>
        bool HasFacingAuthority { get; }

        /// <summary>
        /// Asks to face a point through that arbiter. False when an equal or higher claim holds facing, in
        /// which case the caller parks rather than rotating the body anyway.
        /// </summary>
        bool TryFaceThrough(string owner, Vector3 point, CombatFacingPriority priority, float holdSeconds);

        bool HasClearShotTo(Character target);
        bool HasClearShotToCurrentTarget();
        void BlacklistTargetForLineOfSight(Character target);

        bool IsBlocking { get; }
        bool TryBlock();
        void ReleaseBlock();

        /// <summary>Rolls or sidesteps clear of a threat, whichever the body has an animation for.</summary>
        bool TryDodgeAwayFrom(Character threat);

        /// <summary>Applies a Core status effect through the ability RPC pipeline. Null target means self.</summary>
        bool TryCast(string statusEffectName, Character target, float duration);

        /// <summary>Fills <paramref name="into"/> with friendly characters in range. Returns the count added.</summary>
        int CollectAllies(float range, List<Character> into);

        bool IsEnemy(Character other);
        void Stagger(Vector3 forceDirection);
    }
}
