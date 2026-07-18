using System.Collections.Generic;
using Newtonsoft.Json;

namespace FiresCore.Npc
{
    /// <summary>
    /// The world-agnostic intrinsic state of a Fires NPC — everything needed to recreate one
    /// (identity, ownership, stationing, appearance, inventory, stats). The Core NPC runtime
    /// captures/applies this and persists it on the NPC's own world ZDO. Higher layers (e.g. the
    /// companion kennel in FiresCompanions) store this blob keyed by their own policy
    /// (owner / roster / character-world scoping) but do NOT own these fields. "Owner" is a
    /// generic NPC concept here — OwnerPlayerId == 0 means an unowned NPC (e.g. a Marketplace StaticNpc).
    /// </summary>
    public class NpcSaveState
    {
        // ── Identity ──
        public string NpcId;
        public string PrefabName;
        public string DisplayName;      // effective shown name (override ?? base) — kept for back-compat consumers
        public string BaseName;         // the underlying companionName, so a rename (override) round-trips distinctly

        // ── Ownership / follow (generic NPC concepts) ──
        public long OwnerPlayerId;
        public bool IsFollowing;

        // ── Stationing / home ──
        public bool IsStationed;
        public float StationedPositionX, StationedPositionY, StationedPositionZ;
        public float StationedRotationY;
        public bool AllowIdleWandering;
        public bool HasHomePosition;
        public float HomePositionX, HomePositionY, HomePositionZ;

        // ── Appearance ──
        public int ModelIndex;            // 0 = male, 1 = female
        public string HairStyle;
        public string BeardStyle;
        public float HairColorR, HairColorG, HairColorB;
        public float SkinColorR, SkinColorG, SkinColorB;
        public float EyeColorR, EyeColorG, EyeColorB;
        public bool HasAppearanceData;
        public float Scale = 1f;          // precise body scale (e.g. 0.5–1.3) — restores exact size on recall
        public bool IsGiant;              // coarse derived flag: scale >= 1.15
        public bool IsDwarf;              // coarse derived flag: scale <= 0.65

        // ── Worn equipment ──
        // Captured separately from StorageInventoryData (which is the non-worn storage bag):
        // the runtime reads worn slots via the inventory's equipment accessor and restores
        // each slot by prefab + quality on recall. Without these, a dormant NPC loses its
        // equipped gear when re-spawned from a roster.
        public Dictionary<string, string> EquipmentPrefabs = new Dictionary<string, string>();  // equip slot → item prefab name
        public Dictionary<string, int> EquipmentQualities = new Dictionary<string, int>();       // equip slot → item quality/upgrade level
        public Dictionary<string, int> EquipmentStacks = new Dictionary<string, int>();          // equip slot → stack count (throwables/bombs)

        // ── Serialized sub-systems (opaque blobs the runtime round-trips) ──
        public string StorageInventoryData;
        public string SkillsData;
        public string ProgressionData;
        public string StatsData;
        public string KillsData;
        public string LuckData;
        public string ArchetypeSkillsData;   // "skill:level:xp,..." — archetype skill XP, else lost on kennel respawn

        public string ToJson() => JsonConvert.SerializeObject(this);

        public static NpcSaveState FromJson(string json) =>
            string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<NpcSaveState>(json);
    }
}
