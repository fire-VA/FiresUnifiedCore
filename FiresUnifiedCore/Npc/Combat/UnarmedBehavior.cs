using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Combat behavior for unarmed combat.
    /// Simple punching attacks when no weapon is equipped.
    /// </summary>
    public class UnarmedBehavior : WeaponBehavior
    {
        public override void ConfigureAI()
        {
            // CompanionAI handles combat behavior directly - no MonsterAI settings needed
            Context.AttackRange = 2f;
            Context.AttackAngle = 90f;
            
            if (CompanionCombat.VerboseLogging)
            {
                Debug.Log("[UnarmedBehavior] Configured for UNARMED combat: attackRange=2");
            }
        }
  
 public override void ExecuteAttack(Character target)
        {
FaceTarget(target);
      
         _lastAttackTime = Time.time;
   _isAttacking = true;

 string animTrigger = "unarmed_attack0";
        int animIndex = 0;
   
     if (CompanionCombat.VerboseLogging)
    {
      Debug.Log($"[UnarmedBehavior] === Starting Unarmed Attack ===");
     Debug.Log($"  Target: {target.m_name}");
   }
  
     Context.PlayAttackAnimation(animTrigger, animIndex);
          
  // Use coroutine for delayed damage and finish
       _attackCoroutine = Owner.StartCoroutine(UnarmedAttackCoroutine(target));
           
      Context.BroadcastRPC("RPC_CompanionAttack", animTrigger, animIndex);
    }
     
        private System.Collections.IEnumerator UnarmedAttackCoroutine(Character target)
   {
       yield return new WaitForSeconds(0.2f);
      
     if (!(Context.Companion?.isDefeated ?? true) && target != null && !target.IsDead())
         {
   DealUnarmedDamage(target);
         }
     
          yield return new WaitForSeconds(0.6f);
        FinishAttack();
       _attackCoroutine = null;
        }
  
       private void DealUnarmedDamage(Character target)
  {
      if (target == null || target.IsDead()) return;
 
     HitData hit = new HitData();
     hit.m_point = target.GetCenterPoint();
       hit.m_dir = (target.transform.position - Context.Transform.position).normalized;
            hit.m_attacker = Context.Character.GetZDOID();
            hit.m_skill = global::Skills.SkillType.Unarmed;
    hit.m_damage.m_blunt = 5f;
  hit.m_pushForce = 10f;
         
            if (CompanionCombat.VerboseLogging)
          {
       Debug.Log($"[UnarmedBehavior] Hit {target.m_name} for 5 blunt damage");
            }

      target.Damage(hit);
 Context.CompanionSkills?.RaiseSkill(global::Skills.SkillType.Unarmed, 1f);
      }
    }
}
