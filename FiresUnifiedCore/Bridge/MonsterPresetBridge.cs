using System;
using System.Collections.Generic;

namespace FiresCore.Bridge
{
    /// <summary>
    /// One row of a monster preset's death-drop table. Field shape mirrors Expand World's
    /// <c>ItemData</c>, which is what the owner writes to yaml.
    /// </summary>
    /// <remarks>
    /// <see cref="Quality"/> and <see cref="Variant"/> are carried because the yaml and the
    /// chest path support them, but a CREATURE drop cannot: vanilla's
    /// <c>CharacterDrop.Drop</c> has no field for either, so the owner logs a warning once
    /// per preset and those items drop at default quality and variant.
    /// </remarks>
    public class MonsterPresetItem
    {
        public string Prefab;
        public float Chance = 1f;

        /// <summary>A count (<c>"3"</c>) or an inclusive range (<c>"2;4"</c>). Null means one.</summary>
        public string Stack;

        public string Quality;
        public string Variant;
    }

    /// <summary>
    /// A monster preset to hand to whichever mod owns them (FiresAdminPrefabs). The key/value
    /// lists carry Expand World's own data-entry format so a caller can set fields the bridge
    /// does not name.
    /// </summary>
    public class MonsterPresetRequest
    {
        /// <summary>Required. Also the ZDO int key that scopes the preset's rules, so it must be unique.</summary>
        public string Name;

        /// <summary>
        /// Prefab this preset targets. Optional, but without it the owner cannot name a prefab
        /// in its generated rules and falls back to matching every prefab, which is slower and
        /// gives no restart recovery.
        /// </summary>
        public string Creature;

        /// <summary>Lines in <c>"key, value"</c> form. Split on the FIRST comma, so values may contain commas.</summary>
        public List<string> Ints = new List<string>();

        /// <summary>Lines in <c>"key, value"</c> form.</summary>
        public List<string> Floats = new List<string>();

        /// <summary>Lines in <c>"key, value"</c> form.</summary>
        public List<string> Strings = new List<string>();

        /// <summary>Lines in <c>"key, x,y,z"</c> form.</summary>
        public List<string> Vecs = new List<string>();

        public List<MonsterPresetItem> Items = new List<MonsterPresetItem>();

        /// <summary>
        /// Pick-N-from-M over <see cref="Items"/>, weighted by each row's chance: a count
        /// (<c>"3"</c>) or a range (<c>"2;4"</c>). Null means every row rolls independently.
        /// </summary>
        public string ItemAmount;

        /// <summary>
        /// Seconds before the creature respawns where it died. 0 or less means no respawn rule.
        /// Written as the owner's own float key, so setting it here and in <see cref="Floats"/>
        /// is not necessary.
        /// </summary>
        public float RespawnSeconds;
    }

    /// <summary>
    /// Host-services seam for monster presets, so a mod with its own creature editor
    /// (FiresAllTheItems) can save one without referencing the mod that owns them. The owner
    /// registers handlers at load; while none is registered every call is a no-op returning
    /// false or an empty list, so a caller needs no soft-dependency plumbing of its own.
    /// </summary>
    /// <remarks>
    /// A save is the whole chain: the owner writes its preset yaml, which triggers its own
    /// push of that file to the connected server and, on the receiving side, a hot reload that
    /// regenerates the rule yaml. The caller does not sequence any of that.
    /// </remarks>
    public static class MonsterPresetBridge
    {
        // Single-owner seams, NOT chains: assigning one replaces whatever was there. Exactly
        // one mod owns monster presets, so a second registration is a mistake rather than a
        // second participant — if a third writer to creature drops ever appears, it belongs
        // behind the owner, not behind a second handler here.
        public static Func<MonsterPresetRequest, bool> SaveHandler;
        public static Func<string, bool> DeleteHandler;
        public static Func<List<string>> ListNamesHandler;

        /// <summary>
        /// Reads a stored preset back in the same shape <see cref="Save"/> takes, so a caller can
        /// load one into an editor, change it and pass it straight back.
        /// </summary>
        public static Func<string, MonsterPresetRequest> GetHandler;

        /// <summary>
        /// Asked at death, before any per-species drop override: the owner replaces this
        /// <c>CharacterDrop</c>'s instance list from the preset stamped on its ZDO and returns
        /// true, or returns false if that creature carries no preset.
        /// </summary>
        public static Func<CharacterDrop, bool> ApplyInstanceDropsHandler;

        /// <summary>
        /// Whether this ZDO carries a preset. A preset is authored per instance and is the whole
        /// answer for that creature, so anything applying per-SPECIES rules — stars, stat
        /// multipliers, drop tables — must leave it alone.
        /// </summary>
        public static Func<ZDO, bool> IsPresetInstanceHandler;

        /// <summary>Whether a mod owning monster presets is present and has registered.</summary>
        public static bool IsAvailable => SaveHandler != null;

        /// <summary>
        /// Creates or overwrites the named preset. False when no owner is registered, the
        /// request is unusable, the caller is not admin, or the write failed — the owner logs
        /// the reason.
        /// </summary>
        public static bool Save(MonsterPresetRequest request)
        {
            if (request == null) return false;
            try { return SaveHandler != null && SaveHandler(request); } catch { return false; }
        }

        /// <summary>
        /// The named preset as a request you can edit and hand back to <see cref="Save"/>, or null
        /// when it does not exist or no owner is registered. Round-trips: saving the returned
        /// object unchanged leaves the preset as it was.
        /// </summary>
        public static MonsterPresetRequest Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { return GetHandler != null ? GetHandler(name) : null; } catch { return null; }
        }

        /// <summary>Convenience wrapper over <see cref="Get"/> for call sites that want a bool.</summary>
        public static bool TryGet(string name, out MonsterPresetRequest preset)
        {
            preset = Get(name);
            return preset != null;
        }

        /// <summary>Removes the named preset. False when absent, not registered, or not permitted.</summary>
        public static bool Delete(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try { return DeleteHandler != null && DeleteHandler(name); } catch { return false; }
        }

        /// <summary>
        /// Lets the preset owner claim a dying creature's drop list. Callers must treat true as
        /// "handled, stop" — a per-instance preset is more specific than a per-species override.
        /// </summary>
        public static bool TryApplyInstanceDrops(CharacterDrop drop)
        {
            if (drop == null) return false;
            try { return ApplyInstanceDropsHandler != null && ApplyInstanceDropsHandler(drop); } catch { return false; }
        }

        /// <summary>
        /// True when this creature was spawned from a preset, so per-species rules must skip it.
        /// False when no owner is registered, which is correct: with no preset system present
        /// nothing is preset-stamped.
        /// </summary>
        public static bool IsPresetInstance(ZDO zdo)
        {
            if (zdo == null) return false;
            try { return IsPresetInstanceHandler != null && IsPresetInstanceHandler(zdo); } catch { return false; }
        }

        /// <summary>Every known preset name, for populating a picker. Empty when unavailable.</summary>
        public static List<string> ListNames()
        {
            try { return ListNamesHandler != null ? (ListNamesHandler() ?? new List<string>()) : new List<string>(); }
            catch { return new List<string>(); }
        }
    }
}
