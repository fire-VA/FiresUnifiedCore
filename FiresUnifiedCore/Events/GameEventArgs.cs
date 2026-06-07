using UnityEngine;

namespace FiresCore.Events
{
    // Payload for GameEvents.EntityKilled. KillerCompanionId is set when a tamed companion landed the
    // killing blow (so quest trackers can attribute the kill to the companion's owner); KillerPlayerId is
    // set for a direct player kill. Both may be null for environmental/unattributed deaths.
    public struct KillEventArgs
    {
        public string PrefabName;
        public Vector3 Position;
        public long? KillerPlayerId;
        public string KillerCompanionId;
        public string WeaponPrefab;
        public float Damage;
    }

    public struct ItemPickupArgs
    {
        public string PrefabName;
        public int Amount;
        public long PlayerId;
        public Vector3 Position;
    }

    public struct CraftEventArgs
    {
        public string PrefabName;
        public int Amount;
        public long PlayerId;
        public int Quality;
    }
}
