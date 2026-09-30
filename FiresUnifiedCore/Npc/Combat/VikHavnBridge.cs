using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Companions use VikHavn Combat Moveset Additions' moves when it is installed. Core never references VikHavn: its RPC
    /// names, ZDO keys and config keys are the contract (Tools\VIKHAVN_COMPANION_CONTRACT.md). Every RPC is invoked on the
    /// target's ZNetView and handled by whichever machine owns the target, and settings come from VikHavn's own config
    /// entries, which its ConfigSync keeps at the server's values.
    /// </summary>
    public static class VikHavnBridge
    {
        private const string PluginGuid = "VikHavn.CombatMovesetAdditions";

        private const string LaunchRpc = "VikHavnCombat_Launch";            // (Vector3 velocity), any non-player character
        private const string TauntRpc = "VikHavnCombat_Taunt";              // (ZDOID taunter, float seconds), MonsterAI only
        private const string GrabRpc = "VikHavnCombat_Grab";                // (ZDOID holder, float seconds), MonsterAI only
        private const string GrabReleaseRpc = "VikHavnCombat_GrabRelease";  // (), MonsterAI only

        private static readonly KeyValuePair<int, int> HeldByKey = ZDO.GetHashZDOID("VikHavnCombat_HeldBy");
        private static readonly int HoldEndsKey = "VikHavnCombat_HoldEndsMs".GetStableHashCode();
        private static readonly int StunEndsKey = "VikHavnCombat_StunEndsMs".GetStableHashCode();
        private const double MillisecondsPerSecond = 1000.0;

        internal const string TauntSection = "Taunt";
        internal const string KickSection = "Kick On Demand";
        internal const string KnockbackSection = "Knockback";
        internal const string GrabSection = "Grab";
        internal const string StanceSection = "Sneak Stance";
        private const string DefaultGrabbableCreatures =
            "Greyling, Greydwarf, Greydwarf_Shaman, Skeleton, Skeleton_NoArcher, Draugr, Draugr_Ranged, Goblin, GoblinArcher, " +
            "GoblinShaman, Dverger, DvergerAshlands, DvergerMage, DvergerMageFire, DvergerMageIce, DvergerMageSupport";
        private const string DefaultGrabEffects = "sfx_unarmed_hit";
        private const char ListSeparator = ',';

        private static bool? _installed;
        private static ConfigFile _config;
        private static readonly Dictionary<(string Section, string Key), ConfigEntryBase> Entries = new Dictionary<(string, string), ConfigEntryBase>();
        private static readonly Dictionary<ZDOID, (Character Kicker, Character HeldFoe)> Kicks = new Dictionary<ZDOID, (Character, Character)>();
        private static readonly HashSet<string> GrabbablePrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _grabbableSetting;

        public static bool Installed
        {
            get
            {
                if (_installed.HasValue) return _installed.Value;
                _installed = Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) && info.Instance != null;
                if (_installed.Value) _config = info.Instance.Config;
                return _installed.Value;
            }
        }

        /// <summary>A VikHavn setting by section and key, or <paramref name="fallback"/> (VikHavn's default).</summary>
        internal static T Setting<T>(string section, string key, T fallback)
        {
            if (!Installed || _config == null) return fallback;
            var id = (section, key);
            if (!Entries.TryGetValue(id, out var entry))
            {
                _config.TryGetEntry(new ConfigDefinition(section, key), out ConfigEntry<T> typed);
                entry = typed;
                Entries[id] = entry;
            }
            return entry is ConfigEntry<T> value ? value.Value : fallback;
        }

        private static ZNetView ViewOf(Character character) => character != null ? character.m_nview : null;

        private static bool IsMonster(Character character) => character != null && !character.IsPlayer() && character.GetBaseAI() is MonsterAI;

        private static long NowMilliseconds() => (long)(ZNet.instance.GetTimeSeconds() * MillisecondsPerSecond);

        private static void Invoke(Character target, string rpc, params object[] args)
        {
            var view = ViewOf(target);
            if (view != null && view.IsValid()) view.InvokeRPC(rpc, args);
        }

        #region Taunt

        /// <summary>
        /// Taunts one enemy through VikHavn. Its owner records the taunt on the enemy's ZDO (the rune every client shows),
        /// applies VikHavn's taunted effect and holds the enemy's target on the taunter.
        /// </summary>
        public static void Taunt(Character taunter, Character enemy, float seconds)
        {
            if (!Installed || !Setting(TauntSection, "Enabled", true) || taunter == null || !IsMonster(enemy) || enemy.IsDead()) return;
            if (enemy.IsBoss() && !Setting(TauntSection, "Taunt Bosses", false)) return;
            Invoke(enemy, TauntRpc, taunter.GetZDOID(), seconds);
        }

        #endregion

        #region Kick and launch

        /// <summary>A companion's kick (its unarmed secondary) starts; the hits it lands get VikHavn's kick strength and
        /// launch. <paramref name="heldFoe"/> is the foe it is holding, which the kick throws.</summary>
        public static void BeginKick(Character kicker, Character heldFoe)
        {
            if (Installed && kicker != null) Kicks[kicker.GetZDOID()] = (kicker, heldFoe);
        }

        public static void EndKick(Character kicker)
        {
            if (kicker != null) Kicks.Remove(kicker.GetZDOID());
        }

        /// <summary>On the kicker's machine, before the hit goes to the target's owner (VikHavn's KickHitPatch for companions).</summary>
        internal static void OnDamage(Character target, HitData hit)
        {
            if (Kicks.Count == 0 || !Kicks.TryGetValue(hit.m_attacker, out var kick) || kick.Kicker == null || target == kick.Kicker) return;

            bool thrown = kick.HeldFoe != null && kick.HeldFoe == target;
            if (thrown) ReleaseGrab(target);
            else if (!Setting(KickSection, "Enabled", true)) return;

            float strength = KickStrength(kick.Kicker, target) * (thrown ? Setting(GrabSection, "Throw Launch Multiplier", 2f) : 1f);
            hit.m_pushForce *= strength;
            if (!CanBeLaunched(target)) return;

            Vector3 forward = hit.m_dir;
            forward.y = 0f;
            forward.Normalize();
            Vector3 velocity = (forward * Setting(KickSection, "Launch Forward Speed", 6f) + Vector3.up * Setting(KickSection, "Launch Upward Speed", 4f))
                * strength * MassScale(target);
            Invoke(target, LaunchRpc, velocity);
        }

        /// <summary>VikHavn's kick strength: the Unarmed skill, heavy legwear, and a staggered or unaware target.</summary>
        private static float KickStrength(Character kicker, Character target)
        {
            var skills = kicker.GetComponent<CompanionSkills>();
            float strength = 1f + Setting(KickSection, "Unarmed Bonus At Max Skill", 0.5f) * (skills != null ? skills.GetSkillFactor(Skills.SkillType.Unarmed) : 0f);
            var legs = kicker.GetComponent<CompanionInventory>()?.GetEquippedItem(CompanionInventory.EquipmentSlot.Legs);
            if (legs != null) strength += legs.m_shared.m_weight * Setting(KickSection, "Legwear Bonus Per Weight", 0.03f);
            if (target.IsStaggering()) strength *= Setting(KickSection, "Staggered Target Multiplier", 1.5f);
            var ai = target.GetBaseAI();
            if (ai != null && !ai.IsAlerted()) strength *= Setting(KickSection, "Unaware Target Multiplier", 1.5f);
            return strength;
        }

        private static bool CanBeLaunched(Character target) =>
            !target.IsPlayer() && !target.IsDead() && (!target.IsBoss() || Setting(KnockbackSection, "Launch Bosses", false));

        private static float MassScale(Character target) =>
            1f / Mathf.Max(1f, target.GetMass() / Setting(KnockbackSection, "Reference Mass", 10f));

        #endregion

        #region Grab

        public static bool GrabEnabled => Installed && Setting(GrabSection, "Enabled", true);

        public static float HoldSeconds(float unarmedSkill) =>
            Mathf.Lerp(Setting(GrabSection, "Hold Seconds", 5f), Setting(GrabSection, "Hold Seconds At Max Skill", 10f), unarmedSkill);

        public static float GrabStaminaCost(float unarmedSkill) => Setting(GrabSection, "Grab Stamina Cost", 20f) * StaminaScale(unarmedSkill);

        public static float HoldStaminaPerSecond(float unarmedSkill) => Setting(GrabSection, "Hold Stamina Per Second", 5f) * StaminaScale(unarmedSkill);

        private static float StaminaScale(float unarmedSkill) => 1f - Setting(GrabSection, "Stamina Reduction At Max Skill", 0.5f) * unarmedSkill;

        /// <summary>VikHavn's rule for a grab (GrabTargets.CanGrab): a listed, hostile, non-boss foe in reach that is
        /// staggering or inside its grab window after a stagger, and not already held.</summary>
        public static bool CanGrab(Character holder, Character foe)
        {
            if (!GrabEnabled || holder == null || !IsMonster(foe) || foe.IsDead() || foe.IsBoss() || !BaseAI.IsEnemy(holder, foe)) return false;
            if (!IsGrabbable(foe) || Vector3.Distance(holder.transform.position, foe.transform.position) > Setting(GrabSection, "Grab Range", 2f)) return false;
            var view = ViewOf(foe);
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null && (foe.IsStaggering() || zdo.GetLong(StunEndsKey) > NowMilliseconds()) && !IsHeld(zdo);
        }

        /// <summary>Asks the foe's owner for the grab. If it agrees it records the hold and hands the foe to this machine.</summary>
        public static void RequestGrab(Character holder, Character foe, float seconds)
        {
            Invoke(foe, GrabRpc, holder.GetZDOID(), seconds);
            foreach (string effect in Setting(GrabSection, "Grab Effects", DefaultGrabEffects).Split(ListSeparator))
            {
                string name = effect.Trim();
                if (name.Length > 0) Archetypes.AbilityFXManager.SpawnEffect(name, foe.GetCenterPoint());
            }
        }

        public static bool IsHeldBy(Character foe, Character holder)
        {
            var view = ViewOf(foe);
            if (holder == null || view == null || !view.IsValid()) return false;
            var zdo = view.GetZDO();
            return zdo.GetLong(HoldEndsKey) > NowMilliseconds() && zdo.GetZDOID(HeldByKey) == holder.GetZDOID();
        }

        public static void ReleaseGrab(Character foe)
        {
            if (IsMonster(foe)) Invoke(foe, GrabReleaseRpc);
        }

        private static bool IsHeld(ZDO zdo) => zdo.GetLong(HoldEndsKey) > NowMilliseconds() && zdo.GetZDOID(HeldByKey) != ZDOID.None;

        private static bool IsGrabbable(Character foe)
        {
            string setting = Setting(GrabSection, "Grabbable Creatures", DefaultGrabbableCreatures);
            if (setting != _grabbableSetting)
            {
                GrabbablePrefabs.Clear();
                foreach (string listed in setting.Split(ListSeparator))
                {
                    string name = listed.Trim();
                    if (name.Length > 0) GrabbablePrefabs.Add(name);
                }
                _grabbableSetting = setting;
            }
            return GrabbablePrefabs.Contains(Utils.GetPrefabName(foe.gameObject));
        }

        #endregion
    }

    /// <summary>Runs on the attacker's machine, before the hit is sent to the target's owner, like VikHavn's own kick patch. Core's
    /// one Character.Damage hook: the companion kick, then locational damage (dormant unless a mod enables it).</summary>
    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class CompanionKickHitPatch
    {
        [HarmonyPrefix]
        private static void AmplifyCompanionKick(Character __instance, HitData hit)
        {
            VikHavnBridge.OnDamage(__instance, hit);
            FiresCore.Combat.LocationalDamage.OnAttackerHit(__instance, hit);
            CompanionPvpDamage.OnDamage(__instance, hit);
            CompanionStaffShieldCount.OnDamage(__instance, hit);
            // Last, after every scaling above: the hit as it leaves, for the drills' damage shares.
            CombatStats.OnHit(__instance, hit);
            OwnerAssist.OnHit(__instance, hit);
            ParryTiming.OnAttackerHit(__instance, hit);
            CombatAdvisor.NoteStaffHit(__instance, hit);
        }
    }

    /// <summary>
    /// Counts a healer companion's vanilla StaffShield bubble as a buff (ArchetypeStatistics.BuffsApplied; [ghost]: companion_test's
    /// healer carries StaffShield, so the bubble is its main action). Vanilla hands the shield out as an Aoe hit carrying
    /// Staff_shield with the companion as attacker, on the caster's machine: one count per character it lands on, self included.
    /// </summary>
    internal static class CompanionStaffShieldCount
    {
        private static readonly int StaffShieldHash = "Staff_shield".GetStableHashCode();

        internal static void OnDamage(Character target, HitData hit)
        {
            if (hit == null || target == null || hit.m_statusEffectHash != StaffShieldHash) return;
            Character caster = hit.GetAttacker();
            var companion = caster != null && !(caster is Player) ? caster.GetComponent<CompanionController>() : null;
            var archetype = companion != null ? companion.GetArchetypeController() : null;
            if (archetype != null && archetype.Statistics != null) archetype.Statistics.RecordBuffApplied();
        }
    }

    /// <summary>
    /// Scales a companion's hits on players, and on companions of other owners (Fire's popup 2026-09-29: "Lower, x0.3"; R54d a companion slash did 351 to a
    /// 271 HP player). SERVER-synced [Combat] Companion damage vs players. Runs on the attacker's machine before the hit goes
    /// to the victim's owner, for native attacks, the fallback swing and projectiles alike.
    /// </summary>
    internal static class CompanionPvpDamage
    {
        private static ConfigEntry<float> s_scale;
        private static ConfigEntry<float> s_companionScale;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_scale = config.Bind("Combat", "Companion damage vs players", 0.3f,
                new ConfigDescription("SERVER. Multiplies every hit a companion lands on a player (PvP companion fights). 1 = unscaled.",
                    new AcceptableValueRange<float>(0f, 2f)));
            configSync?.AddConfigEntry(s_scale);
            // Fire's popup 2026-09-29: companion-on-companion hits of different owners at 0.5 (R73 threeway: ~197 per hit on a
            // ~389 HP companion, two swings to a kill).
            s_companionScale = config.Bind("Combat", "Companion damage vs companions", 0.5f,
                new ConfigDescription("SERVER. Multiplies every hit a companion lands on another player's companion (companions of the same owner are never scaled). 1 = unscaled.",
                    new AcceptableValueRange<float>(0f, 2f)));
            configSync?.AddConfigEntry(s_companionScale);
        }

        internal static void OnDamage(Character victim, HitData hit)
        {
            if (s_scale == null || hit == null || victim == null) return;
            Character attacker = hit.GetAttacker();
            CompanionController companion = attacker != null && !(attacker is Player) ? attacker.GetComponent<CompanionController>() : null;
            if (companion == null) return;
            float scale;
            if (victim is Player) scale = s_scale.Value;
            else if (s_companionScale != null && victim.GetComponent<CompanionController>() != null)
            {
                long mine = Archetypes.ClassTargeting.PartyOwner(attacker), theirs = Archetypes.ClassTargeting.PartyOwner(victim);
                if (mine == 0L || theirs == 0L || mine == theirs) return;
                scale = s_companionScale.Value;
            }
            else return;
            float before = hit.GetTotalDamage();
            if (!Mathf.Approximately(scale, 1f)) hit.m_damage.Modify(scale);
            LogHit(companion, attacker, victim, hit, before, scale);
        }

        // One line per companion per second at most (pve fights make lots of hits; this only sees hits on players and on other owners' companions).
        private const float LogInterval = 1f;
        private static readonly Dictionary<Character, float> s_nextLog = new Dictionary<Character, float>();

        /// <summary>
        /// "[CompanionHit] <name> -> <victim>: <skill>, level L (x level factor), archetype xA, crit C% xM, pre-armour B x0.3 = T"
        /// ([ghost]: companion_test quotes it next to a WOULD-DIE). B already holds weapon x skill x level x archetype x crit x
        /// status effects; armour and resistances come off afterwards on the victim.
        /// </summary>
        private static void LogHit(CompanionController companion, Character attacker, Character victim, HitData hit, float before, float scale)
        {
            if (s_nextLog.TryGetValue(attacker, out float next) && Time.time < next) return;
            if (s_nextLog.Count > 64) s_nextLog.Clear();
            s_nextLog[attacker] = Time.time + LogInterval;

            int level = companion.GetEffectiveLevel();
            var gear = companion.EquipmentData;
            // Did THIS hit crit ([ghost], R73: the line printed the crit stats only)? A melee hit is the same HitData the crit
            // amplified; a projectile's is a copy made on impact, so it reads "?".
            string crit = gear == null ? "?" : ReferenceEquals(gear.LastCritHit, hit) ? "yes"
                : hit.m_skill == Skills.SkillType.Bows || hit.m_skill == Skills.SkillType.Crossbows
                  || hit.m_skill == Skills.SkillType.ElementalMagic || hit.m_skill == Skills.SkillType.BloodMagic ? "?" : "no";
            string archetype = gear != null
                ? $"archetype x{gear.ArchetypeDamageMultiplier:0.00}, crit {gear.ArchetypeCritChance * 100f:0}% x{gear.ArchetypeCritDamageMultiplier:0.00} (this hit: crit {crit})"
                : "archetype ?";
            Debug.Log($"[CompanionHit] {companion.companionName} -> {victim.m_name}: {hit.m_skill}, level {level} "
                      + $"(x{1f + Mathf.Max(0, level - 1) * 0.5f:0.0}), {archetype}, pre-armour {before:0} x{scale:0.00} = {hit.GetTotalDamage():0}");
        }
    }
}
