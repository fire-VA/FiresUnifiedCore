using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Bare-handed combat with vanilla's own unarmed weapon (the Player's PlayerUnarmed, given to every NPC prefab): punch
    /// combos and the kick secondary go through Attack.Start. With VikHavn Combat Moveset Additions installed, kicks land
    /// with its strength and launch, and a stunned foe is grabbed, held for allies to beat on, then thrown with a kick.
    /// </summary>
    public class UnarmedBehavior : WeaponBehavior
    {
        private const float UnarmedAttackRange = 2f;
        private const float UnarmedAttackAngle = 90f;
        private const float GrabAnswerSeconds = 1f;
        private const float HoldBeforeThrowSeconds = 2.5f;

        private Character _heldFoe;
        private float _holdStartedAt;
        private Character _grabRequestedFoe;
        private float _grabRequestedAt;
        private bool _kicking;

        protected override ItemDrop.ItemData AttackWeapon
        {
            get
            {
                var unarmed = Context.Humanoid != null ? Context.Humanoid.m_unarmedWeapon : null;
                return unarmed != null ? unarmed.m_itemData : null;
            }
        }

        private float UnarmedSkill => Context.CompanionSkills != null ? Context.CompanionSkills.GetSkillFactor(Skills.SkillType.Unarmed) : 0f;

        public override void ConfigureAI()
        {
            Context.AttackRange = UnarmedAttackRange;
            Context.AttackAngle = UnarmedAttackAngle;
        }

        public override void Update()
        {
            base.Update();
            UpdateGrab();
        }

        public override void ExecuteAttack(Character target)
        {
            if (_heldFoe != null)
            {
                if (Time.time - _holdStartedAt >= HoldBeforeThrowSeconds) Kick(_heldFoe);
                return;
            }
            if (_grabRequestedFoe != null) return;

            FaceTarget(target);
            if (TryGrab(target)) return;
            if (ShouldUseSecondaryAttack(target)) Kick(target);
            else TryStartNativeAttack(target, false);
        }

        private void Kick(Character target)
        {
            FaceTarget(target);
            VikHavnBridge.BeginKick(Context.Character, _heldFoe);
            _kicking = TryStartNativeAttack(target, true);
            if (!_kicking) VikHavnBridge.EndKick(Context.Character);
        }

        private bool TryGrab(Character target)
        {
            if (!HandsFree() || !VikHavnBridge.CanGrab(Context.Character, target)) return false;
            var stats = Context.Companion != null ? Context.Companion.GetStats() : null;
            if (stats != null && !stats.UseStamina(VikHavnBridge.GrabStaminaCost(UnarmedSkill))) return false;

            VikHavnBridge.RequestGrab(Context.Character, target, VikHavnBridge.HoldSeconds(UnarmedSkill));
            _grabRequestedFoe = target;
            _grabRequestedAt = Time.time;
            _lastAttackTime = Time.time;
            return true;
        }

        /// <summary>The foe's owner answers a grab by recording the hold on the foe's ZDO; the hold then lasts until it
        /// expires, the companion throws it, runs out of stamina, or the foe breaks free.</summary>
        private void UpdateGrab()
        {
            if (_grabRequestedFoe != null)
            {
                if (VikHavnBridge.IsHeldBy(_grabRequestedFoe, Context.Character))
                {
                    _heldFoe = _grabRequestedFoe;
                    _holdStartedAt = Time.time;
                    _grabRequestedFoe = null;
                }
                else if (Time.time - _grabRequestedAt > GrabAnswerSeconds)
                {
                    _grabRequestedFoe = null;
                }
            }

            if (_heldFoe == null) return;
            if (_heldFoe.IsDead() || !VikHavnBridge.IsHeldBy(_heldFoe, Context.Character))
            {
                _heldFoe = null;
                return;
            }

            var stats = Context.Companion != null ? Context.Companion.GetStats() : null;
            if (!_kicking && stats != null && !stats.UseStamina(VikHavnBridge.HoldStaminaPerSecond(UnarmedSkill) * Time.deltaTime))
                LetGo();
        }

        private bool HandsFree()
        {
            var inventory = Context.Inventory;
            return inventory == null
                || inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.RightHand) == null
                && inventory.GetEquippedItem(CompanionInventory.EquipmentSlot.LeftHand) == null;
        }

        private void EndKick()
        {
            if (!_kicking) return;
            _kicking = false;
            VikHavnBridge.EndKick(Context.Character);
        }

        private void LetGo()
        {
            if (_heldFoe != null) VikHavnBridge.ReleaseGrab(_heldFoe);
            _heldFoe = null;
            _grabRequestedFoe = null;
        }

        protected override void FinishAttack()
        {
            base.FinishAttack();
            EndKick();
        }

        public override void CancelAttack()
        {
            base.CancelAttack();
            EndKick();
        }

        public override void OnDeactivate()
        {
            base.OnDeactivate();
            LetGo();
        }
    }
}
