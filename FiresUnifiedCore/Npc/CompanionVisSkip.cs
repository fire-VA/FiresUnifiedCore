using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.946 ([ghost], lead 10-27, Fire's profile: "VisEquipment (companions) 10.0 ms/s (204/s)" = every frame for 3 companions, while it only
    /// has work on a gear change): a companion's VisEquipment.CustomUpdate runs only when its ZDO data changed since the last full run
    /// (every equip / visual change writes the ZDO: SetItem, model, colours), or at least every <see cref="MaxSkipSeconds"/> as a safety net.
    /// Players and creatures are untouched. [Companions] Skip unchanged equipment visuals (default off; the rig turns it on).
    /// "[CompanionVisSkip] last 60 s: n run, m skipped (k %)" at Debug.
    /// </summary>
    internal static class CompanionVisSkip
    {
        private const float MaxSkipSeconds = 0.5f;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default OFF without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Companions", "Skip unchanged equipment visuals", false);
        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg) { }

        private sealed class Seen { public int Hash; public float At; }
        private static readonly Dictionary<int, Seen> s_seen = new Dictionary<int, Seen>();
        private static int s_run, s_skip; private static float s_logAt;

        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.CustomUpdate))]
        private static class CompanionVisSkip_CustomUpdate
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(VisEquipment __instance)
            {
                if (!s_on.Value || __instance == null) return true;
                if (__instance.m_isPlayer || __instance.GetComponent<CompanionController>() == null) return true;
                var nv = __instance.GetComponent<ZNetView>();
                var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                if (zdo == null) return true;
                int id = __instance.GetInstanceID();
                if (!s_seen.TryGetValue(id, out var s)) { if (s_seen.Count > 256) s_seen.Clear(); s_seen[id] = s = new Seen { Hash = int.MinValue }; }
                float now = Time.time;
                int hash = EquipHash(__instance, zdo);
                if (hash == s.Hash && now - s.At < MaxSkipSeconds) { s_skip++; Log(); return false; }
                s.Hash = hash; s.At = now; s_run++; Log();
                return true;
            }
        }

        // 0.2.947 (lead 10-28: 946's "10219 run, 479 skipped (4 %)" - the companion ZDO's DataRevision changes almost every frame with position /
        // state sync, the wrong signal): the hash of what VisEquipment draws - its own item / look fields (the owner sets these) AND the ZDO's
        // synced equipment ints (a non-owner reads those into the fields inside CustomUpdate, so a change there must not be skipped)
        private static int EquipHash(VisEquipment v, ZDO z)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + S(v.m_rightItem); h = h * 31 + S(v.m_leftItem); h = h * 31 + S(v.m_chestItem); h = h * 31 + S(v.m_legItem);
                h = h * 31 + S(v.m_helmetItem); h = h * 31 + S(v.m_shoulderItem); h = h * 31 + S(v.m_utilityItem); h = h * 31 + S(v.m_trinketItem);
                h = h * 31 + S(v.m_beardItem); h = h * 31 + S(v.m_hairItem); h = h * 31 + S(v.m_rightBackItem); h = h * 31 + S(v.m_leftBackItem);
                h = h * 31 + v.m_leftItemVariant; h = h * 31 + v.m_shoulderItemVariant; h = h * 31 + v.m_leftBackItemVariant; h = h * 31 + v.m_modelIndex;
                h = h * 31 + v.m_skinColor.GetHashCode(); h = h * 31 + v.m_hairColor.GetHashCode();
                h = h * 31 + z.GetInt(ZDOVars.s_rightItem); h = h * 31 + z.GetInt(ZDOVars.s_leftItem); h = h * 31 + z.GetInt(ZDOVars.s_chestItem);
                h = h * 31 + z.GetInt(ZDOVars.s_legItem); h = h * 31 + z.GetInt(ZDOVars.s_helmetItem); h = h * 31 + z.GetInt(ZDOVars.s_shoulderItem);
                h = h * 31 + z.GetInt(ZDOVars.s_utilityItem); h = h * 31 + z.GetInt(ZDOVars.s_trinketItem); h = h * 31 + z.GetInt(ZDOVars.s_beardItem);
                h = h * 31 + z.GetInt(ZDOVars.s_hairItem); h = h * 31 + z.GetInt(ZDOVars.s_rightBackItem); h = h * 31 + z.GetInt(ZDOVars.s_leftBackItem);
                h = h * 31 + z.GetInt(ZDOVars.s_leftItemVariant); h = h * 31 + z.GetInt(ZDOVars.s_shoulderItemVariant); h = h * 31 + z.GetInt(ZDOVars.s_leftBackItemVariant);
                h = h * 31 + z.GetInt(ZDOVars.s_modelIndex); h = h * 31 + z.GetVec3(ZDOVars.s_skinColor, Vector3.zero).GetHashCode(); h = h * 31 + z.GetVec3(ZDOVars.s_hairColor, Vector3.zero).GetHashCode();
                return h;
            }
        }
        private static int S(string s) => string.IsNullOrEmpty(s) ? 0 : s.GetHashCode();
        private static int S(int i) => i;   // fields that are already item hashes in this game version

        private static void Log()
        {
            if (Time.unscaledTime < s_logAt) return;
            bool first = s_logAt == 0f; s_logAt = Time.unscaledTime + 60f;
            if (!first && s_run + s_skip > 0) FiresCore.Logging.FiresLogger.LogDebug($"[CompanionVisSkip] last 60 s: {s_run} run, {s_skip} skipped ({s_skip * 100 / (s_run + s_skip)} %)");
            s_run = s_skip = 0;
        }
    }
}
