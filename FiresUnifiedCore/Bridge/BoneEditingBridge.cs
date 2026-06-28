using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Seam for bone-editing (Valheim Customization Studio). VCS registers the delegates in its plugin Awake;
    /// consumers (e.g. FiresAdminPrefabs) call the invoker methods, which no-op / return defaults when VCS is
    /// absent. No assembly reference exists between provider and consumer — both depend only on FiresUnifiedCore.
    /// Same pattern as the other FiresCore.Bridge.* seams (host registers, optional consumers invoke).
    /// </summary>
    public static class BoneEditingBridge
    {
        // VCS sets this true at registration. Consumers gate UI affordances (e.g. an "Edit Bones" button) on it.
        public static bool IsAvailable;

        // Open the VCS bone editor on a live Character. Returns false if it could not open.
        public static Func<Character, bool> OpenEditorFn;

        // Read a live Character's current bone modifiers as the compact legacy-vector string.
        public static Func<Character, string> GetBoneDataFn;

        // Apply a compact legacy-vector string to a live Character. Returns false on failure/empty.
        public static Func<Character, string, bool> SetBoneDataFn;

        // True while the VCS editor screen is open (so consumers can poll for "edit finished").
        public static Func<bool> IsEditorOpenFn;

        public static bool OpenEditor(Character c)
        {
            try { return OpenEditorFn != null && OpenEditorFn(c); } catch { return false; }
        }

        public static string GetBoneData(Character c)
        {
            try { return GetBoneDataFn != null ? GetBoneDataFn(c) : string.Empty; } catch { return string.Empty; }
        }

        public static bool SetBoneData(Character c, string data)
        {
            try { return SetBoneDataFn != null && SetBoneDataFn(c, data); } catch { return false; }
        }

        public static bool IsEditorOpen()
        {
            try { return IsEditorOpenFn != null && IsEditorOpenFn(); } catch { return false; }
        }
    }
}
