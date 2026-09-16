using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Archetypes;

using FiresCore.Logging;
namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Server-side per-instance roll for wild-companion spawns: faction
    /// (biome-driven), archetype, gear, stars, name. Idempotent and gated
    /// off for tamed / admin / placed companions via ZDO flags. RNG is
    /// seeded from the ZDO UID so rolls are stable across reloads.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WildCompanionDresser : MonoBehaviour
    {
        // --- ZDO keys -------------------------------------------------
        public const string ZDO_DRESSED_FLAG     = "companion_wild_dressed";
        public const string ZDO_ROLLED_ARCHETYPE = "companion_wild_archetype";
        public const string ZDO_ROLLED_STARS     = "companion_wild_stars";
        public const string ZDO_FACTION          = "companion_wild_faction";

        private void Start()
        {
            // ALWAYS run on every peer so the client-side Character.m_faction
            // matches what the server rolled. Without this, clients see the
            // prefab-baseline m_faction = Players (set in
            // CompanionPrefabManager.ConfigureHumanoid), which means the
            // local player swinging at a wild companion is treated as
            // Player-vs-Player and the PvP gate blocks the hit. The full
            // rolling pipeline (faction/archetype/stars/gear) is still
            // server-only \u2014 see DressRoutine.
            StartCoroutine(InitRoutine());
        }

        private IEnumerator InitRoutine()
        {
            // Yield once so other Start()s (Controller/Inventory/Archetype) finish first.
            yield return null;

            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                yield break;
            }

            var zdo = nview.GetZDO();
            if (zdo == null) yield break;

            // CLIENT + SERVER: re-apply the rolled faction from ZDO to this
            // peer's Character so faction-based gates (PvP, AI targeting,
            // friendly fire) line up across the network. Tamed companions
            // are handled by CompanionController.LoadFromZDO and we don't
            // touch them here.
            if (!zdo.GetBool("companion_tamed", false))
            {
                ApplyCachedFactionFromZDO(zdo);
            }

            // SERVER: run the dressing pipeline if it hasn't already.
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                yield return DressRoutine(nview, zdo);
            }

            // The initial ApplyCachedFactionFromZDO above runs BEFORE the server's
            // DressRoutine writes ZDO_FACTION (both happen at ~frame 1 from the same
            // coroutine yield). That means it always reads -1 on first spawn and
            // leaves m_faction = Players, which gates player hits behind PvP.
            // Schedule two retries so the ZDO value is picked up after it propagates.
            if (!zdo.GetBool("companion_tamed", false))
            {
                StartCoroutine(RetryFactionApply(zdo, 1.5f));
                StartCoroutine(RetryFactionApply(zdo, 4.0f));
            }
        }

        private IEnumerator RetryFactionApply(ZDO zdo, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (this == null || zdo == null) yield break;
            if (!zdo.GetBool("companion_tamed", false))
                ApplyCachedFactionFromZDO(zdo);
        }

        /// <summary>
        /// Reads the cached <c>ZDO_FACTION</c> entry written by the server-side
        /// dresser and applies it to the local <see cref="Character"/>. Runs
        /// on both server and clients so PvP / AI-targeting gates agree.
        /// No-op if the ZDO entry isn't present yet (very early after spawn,
        /// before the server's DressInstance has run).
        /// </summary>
        private void ApplyCachedFactionFromZDO(ZDO zdo)
        {
            try
            {
                // The server writes ZDO_FACTION as part of DressInstance. If
                // it's missing we leave m_faction alone \u2014 the next refresh
                // (after the server dresses) will pick it up via ZDO sync.
                var character = GetComponent<Character>();
                if (character == null) return;

                int factionInt = zdo.GetInt(ZDO_FACTION, -1);
                if (factionInt < 0) return; // dresser hasn't run yet on the server

                var faction = (CompanionFaction)factionInt;
                var vanilla = faction.ToValheim();
                if (character.m_faction != vanilla)
                {
                    character.m_faction = vanilla;

                    // The base Companion prefab also has m_group = "player",
                    // which makes vanilla treat them as same-team. Wild
                    // companions must NOT have a player group or we hit the
                    // same friendly-fire-block path.
                    var humanoid = character as Humanoid;
                    if (humanoid != null) humanoid.m_group = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionDresser] ApplyCachedFactionFromZDO failed: {ex.Message}");
            }
        }

        private IEnumerator DressRoutine(ZNetView nview, ZDO zdo)
        {
            if (!string.IsNullOrEmpty(zdo.GetString(ZDO_DRESSED_FLAG, string.Empty)))
            {
                FiresLogger.LogVerbose($"[WildCompanionDresser] {gameObject.name}: already dressed; skip.");
                yield break;
            }

            string existingId = zdo.GetString("companion_id", string.Empty);
            if (!string.IsNullOrEmpty(existingId))
            {
                Debug.Log($"[WildCompanionDresser] {gameObject.name}: skipping ? already has companion_id '{existingId}'.");
                yield break;
            }

            if (zdo.GetBool("companion_tamed", false))
            {
                FiresLogger.LogVerbose($"[WildCompanionDresser] {gameObject.name}: skipping ? companion_tamed=true.");
                yield break;
            }

            try { DressInstance(nview, zdo); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionDresser] Dress failed for " +
                                 $"{gameObject.name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void DressInstance(ZNetView nview, ZDO zdo)
        {
            int rngSeed = unchecked(zdo.m_uid.GetHashCode() ^ 0x7F51_1D23);
            var rng = new System.Random(rngSeed);

            Heightmap.Biome biome = Heightmap.Biome.Meadows;
            try
            {
                biome = WorldGenerator.instance != null
                    ? WorldGenerator.instance.GetBiome(transform.position)
                    : Heightmap.Biome.Meadows;
            }
            catch { }

            BiomeFactionProfile profile = GetBiomeProfile(biome);
            CompanionFaction faction    = WeightedPickFaction(profile.FactionWeights, rng);
            ArchetypeClass archetype    = RollArchetype(faction, rng);
            int stars                   = WeightedPickIndex(profile.StarWeights, rng);

            var controller = GetComponent<CompanionController>();
            var character  = GetComponent<Character>();
            var archCtrl   = GetComponent<ArchetypeController>();

            // NOTE: We deliberately do NOT touch companionId, companionName,
            // inventory, or scale here. CompanionRandomLoadout (already on
            // the prefab) handles random gear / appearance / scale / viking
            // name the same way it does for hammer-placed companions. The
            // dresser's job is strictly faction + archetype + stars + the
            // hostile/neutral flag.
            if (controller != null)
            {
                controller.isTamed       = false;
                controller.ownerPlayerId = 0;
                // Hostile factions fight on sight; Neutrals stay passive
                // until attacked (Dverger-style retaliation comes from
                // vanilla MonsterAI faction handling once m_faction is set).
                controller.canFight      = faction != CompanionFaction.Neutral;
            }

            if (archCtrl != null)
                archCtrl.SetArchetype(archetype);

            if (character != null)
            {
                character.m_faction = faction.ToValheim();
                if (stars > 0) character.SetLevel(1 + stars);
            }

            zdo.Set(ZDO_DRESSED_FLAG, "1");
            zdo.Set(ZDO_ROLLED_ARCHETYPE, (int)archetype);
            zdo.Set(ZDO_ROLLED_STARS, stars);
            zdo.Set(ZDO_FACTION, (int)faction);

            // GenerateRandomLoadout is left to CompanionRandomLoadout's own delayed call: running it this early, before
            // NpcVisEquipment sets up the player models, silently skipped hair and beards.

            WildCompanionSquad.ElectAndAssign(this, GetComponent<WildCompanionSeed>());

            Debug.Log($"[WildCompanionDresser] DRESSED ({faction} {archetype}, {stars}*, biome={biome}) " +
                      $"at {transform.position.ToString("F0")}. m_faction = {character?.m_faction}");
        }

        // FactionWeights = [Neutral, Bandit, Cultist]; StarWeights = [0,1,2]; GearTier = biome gear tier for the loadout roll.
        private struct BiomeFactionProfile
        {
            public int[] FactionWeights; // [Neutral, Bandit, Cultist]
            public int[] StarWeights;    // [0s, 1s, 2s]
            public int   GearTier;
        }

        private static BiomeFactionProfile GetBiomeProfile(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 100, 0, 0 },
                        StarWeights    = new[] { 92, 7, 1 },
                        GearTier       = 0,
                    };
                case Heightmap.Biome.BlackForest:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 70, 30, 0 },
                        StarWeights    = new[] { 85, 12, 3 },
                        GearTier       = 1,
                    };
                case Heightmap.Biome.Swamp:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 50, 50, 0 },
                        StarWeights    = new[] { 80, 15, 5 },
                        GearTier       = 2,
                    };
                case Heightmap.Biome.Mountain:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 30, 50, 20 },
                        StarWeights    = new[] { 75, 18, 7 },
                        GearTier       = 3,
                    };
                case Heightmap.Biome.Plains:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 20, 60, 20 },
                        StarWeights    = new[] { 70, 22, 8 },
                        GearTier       = 4,
                    };
                case Heightmap.Biome.Mistlands:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 10, 40, 50 },
                        StarWeights    = new[] { 65, 25, 10 },
                        GearTier       = 5,
                    };
                case Heightmap.Biome.AshLands:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 5, 40, 55 },
                        StarWeights    = new[] { 55, 30, 15 },
                        GearTier       = 6,
                    };
                case Heightmap.Biome.DeepNorth:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 5, 45, 50 },
                        StarWeights    = new[] { 55, 30, 15 },
                        GearTier       = 6,
                    };
                default:
                    return new BiomeFactionProfile
                    {
                        FactionWeights = new[] { 100, 0, 0 },
                        StarWeights    = new[] { 95, 5, 0 },
                        GearTier       = 0,
                    };
            }
        }

        private static ArchetypeClass RollArchetype(CompanionFaction faction, System.Random rng)
        {
            ArchetypeClass[] pool;
            switch (faction)
            {
                case CompanionFaction.Neutral:
                    pool = new[]
                    {
                        ArchetypeClass.Tank, ArchetypeClass.Paladin, ArchetypeClass.Berserker,
                        ArchetypeClass.Rogue, ArchetypeClass.Monk, ArchetypeClass.Ranger,
                        ArchetypeClass.Mage, ArchetypeClass.Healer,
                    };
                    break;
                case CompanionFaction.Bandit:
                    pool = new[]
                    {
                        ArchetypeClass.Berserker, ArchetypeClass.Rogue,
                        ArchetypeClass.Ranger, ArchetypeClass.Tank,
                    };
                    break;
                case CompanionFaction.Cultist:
                    pool = new[]
                    {
                        ArchetypeClass.Mage, ArchetypeClass.Healer,
                        ArchetypeClass.Monk, ArchetypeClass.Rogue,
                    };
                    break;
                default:
                    pool = new[] { ArchetypeClass.Berserker };
                    break;
            }
            return pool[rng.Next(pool.Length)];
        }

        private static CompanionFaction WeightedPickFaction(int[] weights, System.Random rng)
        {
            int idx = WeightedPickIndex(weights, rng);
            switch (idx)
            {
                case 1: return CompanionFaction.Bandit;
                case 2: return CompanionFaction.Cultist;
                default: return CompanionFaction.Neutral;
            }
        }

        private static int WeightedPickIndex(int[] weights, System.Random rng)
        {
            if (weights == null || weights.Length == 0) return 0;
            int total = 0;
            for (int i = 0; i < weights.Length; i++) total += Math.Max(0, weights[i]);
            if (total <= 0) return 0;

            int pick = rng.Next(total);
            int running = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                running += Math.Max(0, weights[i]);
                if (pick < running) return i;
            }
            return 0;
        }
    }
}
