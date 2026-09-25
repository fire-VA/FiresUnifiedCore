using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresCore.Storage;
using FiresCore.Sync;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.Creatures
{
    /// <summary>
    /// Server-authoritative per-creature rules (loot, stars, health and damage) for every instance of a creature
    /// prefab. The server owns creature_overrides.json and re-checks admin on every change; every machine holds the
    /// set, because loot is rolled and levels are picked by whichever machine owns the creature. Nothing is written into
    /// prefabs: four hooks read the active rules at the moment vanilla needs them, so switching a creature off (or
    /// removing its rules) hands it straight back to vanilla or its owning mod. Rules win over any other spawn mod.
    /// </summary>
    public static class CreatureOverrides
    {
        public const int MaxStars = 10;

        private const string FileName = "creature_overrides.json";
        private const int MaxDropsPerCreature = 32;
        private const int MaxAmount = 9999;
        private const float MinMultiplier = 0.01f;
        private const float MaxMultiplier = 100f;
        private const float VanillaStarChance = 0.1f;
        private const int VanillaMaxStars = 2;
        private const float VanillaHealthPerStar = 2f;
        private const string PlayerPrefabName = "Player";

        private static readonly int RolledLevelKey = "FiresCreatureRolledLevel".GetStableHashCode();
        private static readonly Dictionary<int, CreatureOverride> ActiveByPrefabHash = new Dictionary<int, CreatureOverride>();
        private static readonly HashSet<string> ReportedMissingPrefabs = new HashSet<string>();
        private static readonly System.Reflection.FieldInfo GhostInitField = AccessTools.Field(typeof(ZNetView), "m_ghostInit");
        private static int _starRuleCount;

        private static readonly ServerOverrideStore<CreatureOverride> Store = new ServerOverrideStore<CreatureOverride>(
            "CreatureOverrides", FileName, () => FiresConfigPaths.Creatures, entry => entry.Name, TrySanitize,
            entries => JsonConvert.SerializeObject(new CreatureOverrideDocument { Creatures = entries }, Formatting.Indented),
            json => JsonConvert.DeserializeObject<CreatureOverrideDocument>(json)?.Creatures ?? new List<CreatureOverride>(),
            entry => entry.Clone(),
            entry => $"'{entry.Name}' is back to vanilla.",
            RebuildActiveRules);

        public static event Action Changed
        {
            add => Store.Changed += value;
            remove => Store.Changed -= value;
        }

        public static event Action<bool, string> SubmitResult
        {
            add => Store.SubmitResult += value;
            remove => Store.SubmitResult -= value;
        }

        public static IReadOnlyList<CreatureOverride> All => Store.All;

        public static bool TryGet(string creaturePrefab, out CreatureOverride entry) => Store.TryGet(creaturePrefab, out entry);

        /// <summary>Starting values for the editor: the prefab's own loot and vanilla's spawn and stat defaults.</summary>
        public static CreatureOverride DescribeDefaults(GameObject creaturePrefab)
        {
            var entry = new CreatureOverride
            {
                Name = creaturePrefab.name,
                MinStars = 0,
                MaxStars = VanillaMaxStars,
                StarChance = VanillaStarChance,
                HealthValue = creaturePrefab.TryGetComponent(out Character character) ? character.m_health : 0f,
            };
            if (creaturePrefab.TryGetComponent(out CharacterDrop characterDrop))
            {
                entry.Drops = characterDrop.m_drops
                    .Where(drop => drop.m_prefab != null)
                    .Select(drop => new CreatureDropOverride
                    {
                        Item = drop.m_prefab.name,
                        AmountMin = drop.m_amountMin,
                        AmountMax = drop.m_amountMax,
                        Chance = drop.m_chance,
                        LevelMultiplier = drop.m_levelMultiplier,
                        OnePerPlayer = drop.m_onePerPlayer,
                        DontScale = drop.m_dontScale,
                    })
                    .ToList();
            }
            return entry;
        }

        /// <summary>The loot this creature rolls right now: its rules when their drops section is live, else null.</summary>
        public static IReadOnlyList<CreatureDropOverride> ActiveDropsFor(string creaturePrefab)
        {
            return ActiveByPrefabHash.TryGetValue(creaturePrefab.GetStableHashCode(), out CreatureOverride entry) && entry.OverrideDrops
                ? entry.Drops
                : null;
        }

        public static void Submit(CreatureOverride entry) => Store.Submit(entry);

        public static void Remove(string creaturePrefab) => Store.Remove(creaturePrefab);

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class RegisterOnSessionStart
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                ActiveByPrefabHash.Clear();
                _starRuleCount = 0;
                Store.OnSessionStart(__instance);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class PullOnLocalSpawn
        {
            [HarmonyPostfix]
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer)
                    Store.OnLocalPlayerSpawned();
            }
        }

        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static class ReplaceLootAtDeath
        {
            // THE one hook on GenerateDropList, so precedence is decided here rather than by
            // load order. A per-instance preset is more specific than a per-species override
            // and wins outright.
            //
            // FiresAdminPrefabs 1.1.107-1.1.112 had its own prefix on this method assigning
            // m_drops too, with neither patch declaring a priority — so for a creature that had
            // both a preset and an override, which one survived depended on which mod Harmony
            // happened to run last. It now answers through the bridge instead.
            [HarmonyPrefix]
            private static void Prefix(CharacterDrop __instance)
            {
                if (FiresCore.Bridge.MonsterPresetBridge.TryApplyInstanceDrops(__instance)) return;
                if (TryGetActive(__instance.gameObject, out CreatureOverride entry) && entry.OverrideDrops)
                    __instance.m_drops = BuildDrops(entry);
            }
        }

        /// <summary>
        /// Vanilla spawners only call SetLevel for levels above 1, so a star rule has to claim the creature when its
        /// ZDO is first created here (m_initZDO null = a fresh spawn, not a load); Character.Awake then reads the level.
        /// </summary>
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class RollStarsOnFreshSpawn
        {
            [HarmonyPrefix]
            private static void Prefix(out bool __state) => __state = ZNetView.m_initZDO == null;

            [HarmonyPostfix]
            private static void Postfix(ZNetView __instance, bool __state)
            {
                if (!__state || _starRuleCount == 0)
                    return;
                ZDO zdo = __instance.GetZDO();
                if (zdo == null || !zdo.IsOwner())
                    return;
                // This site looks the entry up directly rather than through TryGetActive, so it
                // needs the preset exemption spelled out. It matters most on an EWP respawn, where
                // the preset's data is injected BEFORE Awake — so without this we would roll stars
                // straight over a level the preset had already set.
                if (FiresCore.Bridge.MonsterPresetBridge.IsPresetInstance(zdo))
                    return;
                if (!ActiveByPrefabHash.TryGetValue(zdo.GetPrefab(), out CreatureOverride entry) || !entry.OverrideStars)
                    return;
                if (__instance.GetComponent<Character>() == null || (bool)GhostInitField.GetValue(null))
                    return;
                int rolled = RollLevel(entry);
                zdo.Set(RolledLevelKey, rolled);
                zdo.Set(ZDOVars.s_level, rolled);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.SetLevel))]
        private static class PickLevelAtSpawn
        {
            [HarmonyPrefix]
            private static void Prefix(Character __instance, ref int level)
            {
                if (level < 1 || __instance.IsPlayer())
                    return;
                if (!TryGetActive(__instance.gameObject, out CreatureOverride entry) || !entry.OverrideStars)
                    return;
                ZDO zdo = __instance.GetComponent<ZNetView>()?.GetZDO();
                if (zdo == null)
                    return;
                int rolled = zdo.GetInt(RolledLevelKey);
                if (rolled < 1)
                {
                    rolled = RollLevel(entry);
                    zdo.Set(RolledLevelKey, rolled);
                }
                level = rolled;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealthBase))]
        private static class ScaleHealth
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (__instance.IsPlayer() || !TryGetActive(__instance.gameObject, out CreatureOverride entry) || !entry.OverrideStats)
                    return;
                // An absolute value replaces the prefab's m_health. Scaling __result by the ratio rather than
                // assigning keeps vanilla's world-level multiplier, which is already folded into __result.
                if (entry.HealthValue > 0f && __instance.m_health > 0f)
                    __result *= entry.HealthValue / __instance.m_health;
                else
                    __result *= entry.HealthMultiplier;
            }
        }

        [HarmonyPatch(typeof(Attack), "GetLevelDamageFactor")]
        private static class ScaleDamage
        {
            [HarmonyPostfix]
            private static void Postfix(Humanoid ___m_character, ref float __result)
            {
                if (___m_character == null || ___m_character.IsPlayer()
                    || !TryGetActive(___m_character.gameObject, out CreatureOverride entry) || !entry.OverrideStats)
                    return;
                // Vanilla returns 1 + (level - 1) * 0.5. Restate it with the rule's own per-star value so the
                // editor's number IS what one star does, then keep the flat multiplier for older saved rules.
                __result = StarFactor(entry.DamagePerStar, ___m_character.GetLevel()) * entry.DamageMultiplier;
            }
        }

        // Health's star scaling lives in Character.SetupMaxHealth (base x level), not in GetMaxHealthBase, so
        // changing what a star is worth has to happen here. Level 1 is vanilla's own answer, so leave it alone.
        [HarmonyPatch(typeof(Character), "SetupMaxHealth")]
        private static class ScaleHealthPerStar
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance)
            {
                if (__instance == null || __instance.IsPlayer()
                    || !TryGetActive(__instance.gameObject, out CreatureOverride entry) || !entry.OverrideStats)
                    return;
                int level = Mathf.Max(1, __instance.GetLevel());
                if (level == 1 || Mathf.Approximately(entry.HealthPerStar, VanillaHealthPerStar))
                    return;
                __instance.SetMaxHealth(__instance.GetMaxHealthBase() * StarFactor(entry.HealthPerStar, level));
            }
        }

        /// <summary>One star is worth <paramref name="perStar"/>; each further star adds the same again.</summary>
        private static float StarFactor(float perStar, int level) =>
            1f + Mathf.Max(0, level - 1) * (perStar - 1f);

        private static void RebuildActiveRules()
        {
            ActiveByPrefabHash.Clear();
            foreach (CreatureOverride entry in Store.Entries.Where(entry => entry.Enabled))
                ActiveByPrefabHash[entry.Name.GetStableHashCode()] = entry;
            _starRuleCount = ActiveByPrefabHash.Values.Count(entry => entry.OverrideStars);
        }

        // A creature spawned from a preset is exempt from every per-species rule here — stars,
        // stat multipliers and drops alike. A preset is authored for that instance and IS the
        // whole answer for it: "pre-set" means the admin's numbers, not the admin's numbers plus
        // a species multiplier. Without this, a species rule for Draugr re-rolled the stars of a
        // preset Draugr whose preset had set them explicitly.
        private static bool TryGetActive(GameObject creature, out CreatureOverride entry)
        {
            entry = null;
            if (ActiveByPrefabHash.Count == 0 || creature == null)
                return false;
            ZDO zdo = creature.GetComponent<ZNetView>()?.GetZDO();
            if (FiresCore.Bridge.MonsterPresetBridge.IsPresetInstance(zdo))
                return false;
            int prefabHash = zdo != null ? zdo.GetPrefab() : Utils.GetPrefabName(creature).GetStableHashCode();
            return ActiveByPrefabHash.TryGetValue(prefabHash, out entry);
        }

        private static int RollLevel(CreatureOverride entry)
        {
            int level = entry.MinStars + 1;
            while (level < entry.MaxStars + 1 && UnityEngine.Random.value < entry.StarChance)
                level++;
            return level;
        }

        private static List<CharacterDrop.Drop> BuildDrops(CreatureOverride entry)
        {
            var drops = new List<CharacterDrop.Drop>();
            foreach (CreatureDropOverride line in entry.Drops)
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(line.Item) : null;
                if (prefab == null)
                {
                    if (ReportedMissingPrefabs.Add(line.Item))
                        FiresLogger.LogWarning($"[CreatureOverrides] skipped drop '{line.Item}' on {entry.Name}: not loaded");
                    continue;
                }
                drops.Add(new CharacterDrop.Drop
                {
                    m_prefab = prefab,
                    m_amountMin = line.AmountMin,
                    m_amountMax = line.AmountMax,
                    m_chance = line.Chance,
                    m_levelMultiplier = line.LevelMultiplier,
                    m_onePerPlayer = line.OnePerPlayer,
                    m_dontScale = line.DontScale,
                });
            }
            return drops;
        }

        private static bool TrySanitize(CreatureOverride entry, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(entry.Name))
                error = "The creature has no name.";
            else if (entry.Name == PlayerPrefabName)
                error = "Players can't be edited.";
            else if (entry.Drops != null && entry.Drops.Count > MaxDropsPerCreature)
                error = $"A creature can list at most {MaxDropsPerCreature} drops.";
            if (error != null)
                return false;

            entry.Drops = (entry.Drops ?? new List<CreatureDropOverride>())
                .Where(drop => drop != null && !string.IsNullOrWhiteSpace(drop.Item))
                .ToList();
            foreach (CreatureDropOverride drop in entry.Drops)
            {
                drop.AmountMin = Mathf.Clamp(drop.AmountMin, 0, MaxAmount);
                drop.AmountMax = Mathf.Clamp(drop.AmountMax, drop.AmountMin, MaxAmount);
                drop.Chance = Mathf.Clamp01(drop.Chance);
            }
            entry.MinStars = Mathf.Clamp(entry.MinStars, 0, MaxStars);
            entry.MaxStars = Mathf.Clamp(entry.MaxStars, entry.MinStars, MaxStars);
            entry.StarChance = Mathf.Clamp01(entry.StarChance);
            entry.HealthMultiplier = Mathf.Clamp(entry.HealthMultiplier, MinMultiplier, MaxMultiplier);
            entry.DamageMultiplier = Mathf.Clamp(entry.DamageMultiplier, MinMultiplier, MaxMultiplier);
            return true;
        }
    }
}
