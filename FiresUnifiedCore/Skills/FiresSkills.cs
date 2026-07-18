using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.SkillSystem
{
    /// <summary>One registered custom skill: a real Valheim <see cref="Skills.SkillType"/> derived from its name hash.</summary>
    public sealed class FiresSkill
    {
        public Skills.SkillType SkillType { get; }
        public string Name { get; }
        public string Description { get; }
        public Sprite Icon { get; internal set; }

        // Vanilla builds skill names/messages from the token "$skill_" + m_info.m_skill.ToString().ToLower()
        // (Skills.RaiseSkill and SkillsDialog.Setup). We register a Localization word under exactly that key so
        // the game renders our name, its level-up message, and its skills-dialog entry itself — no bolt-on patches.
        internal string LocalizationKey { get; }
        internal string InternalName { get; }   // lowercase, no spaces — for the raiseskill/resetskill console cheats

        internal FiresSkill(string name, string description, Sprite icon)
        {
            Name = name;
            Description = description;
            Icon = icon;
            SkillType = (Skills.SkillType)Math.Abs(name.GetStableHashCode());
            LocalizationKey = "skill_" + SkillType.ToString().ToLower();
            InternalName = (name ?? "").ToLowerInvariant().Replace(" ", "");
        }
    }

    /// <summary>
    /// Shared custom-skill system for the Fires mod family — the core logic from VAExtraSkills, adapted to the
    /// current Valheim build so every Fires mod registers a real Valheim skill the same, proven way. A mod calls
    /// <see cref="Register"/> (once, at Setup/WorldStart, on both peers); after that vanilla treats the skill as
    /// first-class with only three seams:
    ///   • <see cref="FiresSkillPatches.Skills_GetSkillDef"/> supplies the SkillDef (icon + description + increase step)
    ///     on demand — vanilla's <c>Skills.GetSkill</c> then creates/levels/persists the skill through its own code;
    ///   • <see cref="FiresSkillPatches.Skills_IsSkillValid"/> accepts the type so vanilla's own ZPackage save/load
    ///     (<c>Player.Save</c>→<c>Skills.Save</c>/<c>Load</c>) persists it natively — no side ZDO store needed;
    ///   • a Localization word under the "$skill_&lt;type&gt;" key vanilla already uses lets vanilla render the name,
    ///     the "$msg_skillup" level-up message (Center on first level, TopLeft after), and the skills-dialog entry.
    /// The console <c>raiseskill</c>/<c>resetskill</c> cheats get a friendly-name seam so admins can type the skill's
    /// name instead of its hash. Nothing here is version-specific to the old ObjectDB.m_skills list (this build has no
    /// such field); the def is delivered purely through GetSkillDef.
    /// </summary>
    public static class FiresSkillRegistry
    {
        private static readonly Dictionary<Skills.SkillType, FiresSkill> _byType = new Dictionary<Skills.SkillType, FiresSkill>();
        private static readonly Dictionary<string, Skills.SkillType> _byInternalName = new Dictionary<string, Skills.SkillType>(StringComparer.Ordinal);

        public static IReadOnlyCollection<FiresSkill> All => _byType.Values;

        /// <summary>
        /// Register (or fetch) a custom skill by display name. Idempotent by name-hash: re-registering the same name
        /// returns the existing skill and refreshes its icon if a non-null one is supplied (lets a mod register early
        /// — before its icon sprite has loaded — then again once it has, without creating a second skill).
        /// </summary>
        public static FiresSkill Register(string name, string description, Sprite icon)
        {
            var skill = new FiresSkill(name, description, icon);
            if (_byType.TryGetValue(skill.SkillType, out var existing))
            {
                if (icon != null) existing.Icon = icon;
                RegisterWord(existing);
                return existing;
            }
            _byType[skill.SkillType] = skill;
            _byInternalName[skill.InternalName] = skill.SkillType;
            RegisterWord(skill);
            FiresUnifiedCore.Log?.LogInfo($"[FiresSkills] registered '{name}' (type {skill.SkillType}, cheat name '{skill.InternalName}').");
            return skill;
        }

        public static bool TryGet(Skills.SkillType type, out FiresSkill skill) => _byType.TryGetValue(type, out skill);
        public static bool TryGetByInternalName(string internalName, out Skills.SkillType type) => _byInternalName.TryGetValue(internalName, out type);

        // ── player convenience (used by the owning mod) ──────────────────────────
        public static void RaiseFiresSkill(this Player player, Skills.SkillType type, float amount = 1f) => player?.GetSkills()?.RaiseSkill(type, amount);
        public static float GetFiresSkillLevel(this Player player, Skills.SkillType type) => player != null ? player.GetSkills().GetSkillLevel(type) : 0f;
        /// <summary>Skill level as a 0..1 factor (level/100) — the standard vanilla "skill factor" effectiveness scales on.</summary>
        public static float GetFiresSkillFactor(this Player player, Skills.SkillType type) => player != null ? Mathf.Clamp01(player.GetSkills().GetSkillLevel(type) / 100f) : 0f;

        internal static Skills.SkillDef MakeDef(FiresSkill s) =>
            new Skills.SkillDef { m_skill = s.SkillType, m_icon = s.Icon, m_description = s.Description, m_increseStep = 1f };

        // Push this skill's display name into the active Localization under the key vanilla derives from the type,
        // so vanilla's own name/message/dialog code resolves it. Called on register (covers the live session) and
        // re-applied by the LoadCSV postfix (covers a later language switch, which rebuilds m_translations).
        internal static void RegisterWord(FiresSkill s)
        {
            var loc = Localization.instance;
            if (loc == null || s == null || string.IsNullOrEmpty(s.Name)) return;
            loc.AddWord(s.LocalizationKey, s.Name);
        }
    }

    /// <summary>The three Harmony seams that make a registered <see cref="FiresSkill"/> behave as a real vanilla skill.</summary>
    internal static class FiresSkillPatches
    {
        // Supply a SkillDef for our custom types (icon + description) when vanilla has none. This is the load-bearing
        // seam: Skills.GetSkill() calls GetSkillDef, so every downstream path (create, level, save, load, dialog) works.
        [HarmonyPatch(typeof(Skills), "GetSkillDef")]
        internal static class Skills_GetSkillDef
        {
            private static void Postfix(Skills.SkillType type, ref Skills.SkillDef __result)
            {
                if (__result != null) return;
                if (FiresSkillRegistry.TryGet(type, out var s)) __result = FiresSkillRegistry.MakeDef(s);
            }
        }

        // Accept our custom skill types. Vanilla's IsSkillValid is Enum.IsDefined(...), which rejects our hash-derived
        // type and would drop it on Skills.Load. Returning true here lets vanilla's own ZPackage persistence keep it.
        [HarmonyPatch(typeof(Skills), "IsSkillValid")]
        internal static class Skills_IsSkillValid
        {
            private static bool Prefix(Skills.SkillType type, ref bool __result)
            {
                if (FiresSkillRegistry.TryGet(type, out _)) { __result = true; return false; }
                return true;
            }
        }

        // Re-apply our skill words whenever Localization rebuilds its table (initial load + every language switch).
        // Same target kg_Blueprint's LocalizationManager uses, so it's known-good on this build; we only read __instance.
        [HarmonyPatch(typeof(Localization), "LoadCSV")]
        internal static class Localization_LoadCSV
        {
            private static void Postfix(Localization __instance)
            {
                if (__instance == null) return;
                foreach (var s in FiresSkillRegistry.All)
                    if (!string.IsNullOrEmpty(s.Name))
                        __instance.AddWord(s.LocalizationKey, s.Name);
            }
        }

        // Console: raiseskill/resetskill <friendlyName> <value>. Vanilla matches skills by enum-ToString (our hash
        // number), so without this an admin would have to type the number. Mirrors vanilla CheatRaiseSkill's body.
        [HarmonyPatch(typeof(Skills), "CheatRaiseSkill")]
        internal static class Skills_CheatRaiseSkill
        {
            private static bool Prefix(Skills __instance, string name, float value)
            {
                if (!FiresSkillRegistry.TryGetByInternalName(name.ToLowerInvariant(), out var type)) return true;
                FiresSkillRegistry.TryGet(type, out var s);
                var skill = __instance.GetSkill(type);   // creates via our GetSkillDef seam if absent
                skill.m_level = Mathf.Clamp(skill.m_level + value, 0f, 100f);
                skill.m_accumulator = 0f;
                __instance.m_player?.Message(MessageHud.MessageType.TopLeft, $"Skill increased {s.Name}: {(int)skill.m_level}", 0, skill.m_info.m_icon);
                Console.instance?.Print($"Skill {s.Name} = {skill.m_level}");
                return false;
            }
        }

        [HarmonyPatch(typeof(Skills), "CheatResetSkill")]
        internal static class Skills_CheatResetSkill
        {
            private static bool Prefix(Skills __instance, string name)
            {
                if (!FiresSkillRegistry.TryGetByInternalName(name.ToLowerInvariant(), out var type)) return true;
                FiresSkillRegistry.TryGet(type, out var s);
                __instance.ResetSkill(type);
                Console.instance?.Print($"Skill {s.Name} reset");
                return false;
            }
        }
    }
}
