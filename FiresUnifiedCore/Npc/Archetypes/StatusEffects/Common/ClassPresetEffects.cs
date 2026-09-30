using FiresCore.Npc.Archetypes.StatusEffects.Base;
using FiresCore.Npc.Archetypes.StatusEffects.Mage;

namespace FiresCore.Npc.Archetypes.StatusEffects.Common
{
    // Presets for class skills whose numbers differ from the companion effects (Tools\CLASS_KIT_DESIGN_GAPS.md). A Core effect
    // travels as a name and a duration only, so each number is its own registered effect. The constructors set the numbers:
    // ApplyEffectOfType builds a fresh instance of the registered type for every apply.

    /// <summary>tank_earthshaker: "30% slow for 4 s" (CompanionSlowed is 50%).</summary>
    public class ClassSlow30Effect : SlowdownEffect
    {
        public ClassSlow30Effect()
        {
            SpeedMultiplier = 0.7f;
        }
    }

    /// <summary>monk_tailwind rank 1: "move 15% faster".</summary>
    public class ClassHaste15Effect : BuffEffect
    {
        public ClassHaste15Effect()
        {
            m_name = "Tailwind";
            SpeedMultiplier = 1.15f;
        }
    }

    /// <summary>monk_tailwind rank 2: "move 20% faster".</summary>
    public class ClassHaste20Effect : BuffEffect
    {
        public ClassHaste20Effect()
        {
            m_name = "Tailwind";
            SpeedMultiplier = 1.2f;
        }
    }

    /// <summary>monk_tailwind rank 3: "move 25% faster".</summary>
    public class ClassHaste25Effect : BuffEffect
    {
        public ClassHaste25Effect()
        {
            m_name = "Tailwind";
            SpeedMultiplier = 1.25f;
        }
    }

    /// <summary>
    /// healer_aegis: "a shield that absorbs 25% of their max health". Sized on the owner when it lands, from the carrier's own
    /// max health; a carrier whose max health isn't known yet keeps the Arcane Shield's default.
    /// </summary>
    public class ClassAegisEffect : ArcaneShieldEffect
    {
        private const float ShareOfMaxHealth = 0.25f;

        public ClassAegisEffect()
        {
            m_name = "Aegis";
        }

        protected override void OnEffectApplied()
        {
            float maxHealth = m_character != null ? m_character.GetMaxHealth() : 0f;
            if (maxHealth > 0f) MaxShieldHealth = maxHealth * ShareOfMaxHealth;
            base.OnEffectApplied();
        }
    }

    // ---- The lingering zones (Fire, popup 2026-09-28 21:3x): a zone re-applies these every tick for a little over a tick,
    // so they last while the carrier stays inside and fall off shortly after it leaves. ----

    /// <summary>rogue_toxic_cloud: "slows enemies 20%".</summary>
    public class ClassSlow20Effect : SlowdownEffect
    {
        public ClassSlow20Effect()
        {
            SpeedMultiplier = 0.8f;
        }
    }

    /// <summary>healer_warding_totem: "take {dr} less damage" (10/15/20% by rank).</summary>
    public class ClassWardEffect : BuffEffect
    {
        protected ClassWardEffect(float damageTaken)
        {
            m_name = "Warding Totem";
            DefenseMultiplier = damageTaken;
        }

        public ClassWardEffect() : this(0.9f) { }
    }

    public class ClassWard15Effect : ClassWardEffect { public ClassWard15Effect() : base(0.85f) { } }

    public class ClassWard20Effect : ClassWardEffect { public ClassWard20Effect() : base(0.8f) { } }

    /// <summary>tank_hold_the_line rank 3: "take 25% less damage" (ranks 1-2 reuse Ward15 / Ward20).</summary>
    public class ClassWard25Effect : ClassWardEffect
    {
        public ClassWard25Effect() : base(0.75f)
        {
            m_name = "Hold the Line";
        }
    }

    /// <summary>berserker_horn_of_valhalla: "allies deal 25% more damage for 15 s".</summary>
    public class ClassDamage25Effect : BuffEffect
    {
        public ClassDamage25Effect()
        {
            m_name = "Horn of Valhalla";
            DamageMultiplier = 1.25f;
        }
    }

    /// <summary>A boss's slow in place of a hard control where the skill asks for more than CompanionSlowed's 50% (mage_absolute_zero: 60%).</summary>
    public class ClassSlow60Effect : SlowdownEffect
    {
        public ClassSlow60Effect()
        {
            SpeedMultiplier = 0.4f;
        }
    }

    /// <summary>tank_rallying_banner: "take {dr} less damage and regain stamina {regen} faster" (10%/25%, 15%/40%, 20%/50%).</summary>
    public class ClassBannerEffect : BuffEffect
    {
        private readonly float _staminaRegen;

        protected ClassBannerEffect(float damageTaken, float staminaRegen)
        {
            m_name = "Rallying Banner";
            DefenseMultiplier = damageTaken;
            _staminaRegen = staminaRegen;
        }

        public ClassBannerEffect() : this(0.9f, 1.25f) { }

        public override void ModifyStaminaRegen(ref float staminaRegen)
        {
            base.ModifyStaminaRegen(ref staminaRegen);
            staminaRegen *= _staminaRegen;
        }
    }

    public class ClassBanner15Effect : ClassBannerEffect { public ClassBanner15Effect() : base(0.85f, 1.4f) { } }

    public class ClassBanner20Effect : ClassBannerEffect { public ClassBanner20Effect() : base(0.8f, 1.5f) { } }

    /// <summary>mage_eitr_well: "regenerate eitr {regen} faster" (+100/150/200%).</summary>
    public class ClassEitrWellEffect : BuffEffect
    {
        private readonly float _eitrRegen;

        protected ClassEitrWellEffect(float eitrRegen)
        {
            m_name = "Eitr Well";
            _eitrRegen = eitrRegen;
        }

        public ClassEitrWellEffect() : this(2f) { }

        public override void ModifyEitrRegen(ref float eitrRegen)
        {
            base.ModifyEitrRegen(ref eitrRegen);
            eitrRegen *= _eitrRegen;
        }
    }

    public class ClassEitrWell150Effect : ClassEitrWellEffect { public ClassEitrWell150Effect() : base(2.5f) { } }

    public class ClassEitrWell200Effect : ClassEitrWellEffect { public ClassEitrWell200Effect() : base(3f) { } }
}
