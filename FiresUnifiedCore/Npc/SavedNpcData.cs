using System;
using UnityEngine;

namespace FiresCore.Npc
{
    // Persisted definition of a placed server NPC: position, profile bindings, equipment slots, and a
    // serialized fashion blob. Plain field/property JSON (no type-name handling), so relocating it to Core
    // does not change the on-disk shape — existing saved-NPC files deserialize unchanged.
    [Serializable]
    public class SavedNpcData
    {
        public string NpcId { get; set; }
        public Vector3 Position { get; set; }
        public string NpcProfileName { get; set; }
        public string DisplayNameOverride { get; set; }
        public string QuestProfile { get; set; }
        public string DialogueProfile { get; set; }
        public string InfoProfile { get; set; }
        public string TraderProfile { get; set; }
        public string ModelOverride { get; set; }
        public NpcType NpcType { get; set; }
        public DateTime LastModified { get; set; }

        public string EquipHelmet { get; set; }
        public string EquipChest { get; set; }
        public string EquipLegs { get; set; }
        public string EquipShoulder { get; set; }
        public string EquipUtility { get; set; }
        public string EquipRightHand { get; set; }
        public string EquipLeftHand { get; set; }
        public string EquipRightBack { get; set; }
        public string EquipLeftBack { get; set; }

        // Full serialized ItemData (base64) for the hand weapons — preserves real quality/upgrades/customData
        // through save + respawn so a respawned static NPC fights with the same weapon, not a quality-1 clone.
        public string EquipRightHandData { get; set; }
        public string EquipLeftHandData { get; set; }

        public string FashionJson { get; set; }

        /// <summary>Base64 PNG hammer icon — a 128px render of the dressed NPC captured at save time
        /// (Marketplace-style), shown on the template's build piece. Null on records saved headless
        /// or before icons existed; the piece falls back to the shield placeholder until backfilled.</summary>
        public string IconPng { get; set; }

        /// <summary>JSON snapshot of the VANILLA VisEquipment look read from the live body's ZDO at
        /// save time (model index, hair/beard prefab names, hair/skin colors, item prefab names per
        /// slot). Randomly-dressed NPCs carry their whole look in these vanilla vars — NOT in
        /// FashionJson or the NpcController equip fields, which are empty for them — so without this
        /// a template saved from such an NPC contains no appearance at all (naked ghost, blank icon,
        /// naked placement). Additive field: old template files deserialize with it null.</summary>
        public string LookJson { get; set; }
    }
}
