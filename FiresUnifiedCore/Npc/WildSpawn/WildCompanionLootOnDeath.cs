using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Server-authoritative gear-drop-on-death for wild companions. Reads the
    /// per-instance gear roll from ZDO (written by <see cref="WildCompanionDresser"/>)
    /// and spawns equipped pieces as ItemDrops with star-scaled chances.
    /// Neutrals never drop gear; Bandit/Cultist drop weapons at 40% / armor at 25%,
    /// scaled x(1+stars). RNG is seeded from ZDO UID so kills are reproducible.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WildCompanionLootOnDeath : MonoBehaviour
    {
        private Character _character;
        private ZNetView _nview;
        private bool _dropped;

        private void Awake()
        {
            _character = GetComponent<Character>();
            _nview     = GetComponent<ZNetView>();
            if (_character != null)
                _character.m_onDeath += HandleDeath;
        }

        private void OnDestroy()
        {
            if (_character != null)
                _character.m_onDeath -= HandleDeath;
        }

        private void HandleDeath()
        {
            if (_dropped) return;
            _dropped = true;

            try { DropRolledGear(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionLootOnDeath] Drop roll failed for {gameObject.name}: {ex.Message}");
            }
        }

        private void DropRolledGear()
        {
            // Spawning ItemDrop prefabs via Object.Instantiate is safe: the
            // prefab's ZNetView.Awake registers with ZNetScene, and the items
            // ride normal vanilla cleanup. The previous force-disable of this
            // method was a precaution while we hunted the
            // ZNetScene.RemoveObjects NRE \u2014 that turned out to be caused by
            // raw Object.Destroy calls on ZNetView'd things elsewhere, which
            // have since been migrated to FiresCore.Net.NetworkObjectHelper.SafeDestroy /
            // ZNetScene.Destroy. This drop-on-death path is fine to run.
            if (_nview == null || !_nview.IsValid()) return;
            if (!_nview.IsOwner()) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            int factionInt = zdo.GetInt(WildCompanionDresser.ZDO_FACTION, (int)CompanionFaction.Neutral);
            var faction = (CompanionFaction)factionInt;
            if (faction == CompanionFaction.Neutral) return;

            int stars = zdo.GetInt(WildCompanionDresser.ZDO_ROLLED_STARS, 0);
            float starMultiplier = 1f + (stars * 1.0f);

            int rngSeed = unchecked(zdo.m_uid.GetHashCode() ^ 0x49E4_57A3);
            var rng = new System.Random(rngSeed);

            var inventory = GetComponent<CompanionInventory>();
            if (inventory == null) return;

            int drops = 0;
            foreach (CompanionInventory.EquipmentSlot slot in Enum.GetValues(typeof(CompanionInventory.EquipmentSlot)))
            {
                string prefabName = inventory.GetEquipmentPrefabNamePublic(slot);
                if (string.IsNullOrEmpty(prefabName)) continue;

                float baseChance = IsWeaponSlot(slot) ? 0.40f : 0.25f;
                float effectiveChance = Mathf.Clamp01(baseChance * starMultiplier);

                if (rng.NextDouble() > effectiveChance) continue;

                if (SpawnDropAtSelf(prefabName))
                    drops++;
            }

            if (drops > 0)
            {
                Debug.Log($"[WildCompanionLootOnDeath] {faction} '{_character?.m_name}' dropped {drops} piece(s) (stars={stars}).");
            }
        }

        private bool SpawnDropAtSelf(string prefabName)
        {
            if (ZNetScene.instance == null) return false;

            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"[WildCompanionLootOnDeath] Gear prefab '{prefabName}' not in ZNetScene; skipping drop.");
                return false;
            }

            Vector3 dropPos = transform.position
                            + Vector3.up * 0.6f
                            + new Vector3(UnityEngine.Random.Range(-0.4f, 0.4f), 0f,
                                          UnityEngine.Random.Range(-0.4f, 0.4f));

            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, dropPos, Quaternion.identity);
                return go != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WildCompanionLootOnDeath] Instantiate '{prefabName}' failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsWeaponSlot(CompanionInventory.EquipmentSlot s) =>
            s == CompanionInventory.EquipmentSlot.RightHand
         || s == CompanionInventory.EquipmentSlot.LeftHand
         || s == CompanionInventory.EquipmentSlot.RightBack
         || s == CompanionInventory.EquipmentSlot.LeftBack;
    }
}
