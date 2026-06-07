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
        public string FashionJson { get; set; }
    }
}
