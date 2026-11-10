using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// A chest's tags auto-print onto signs attached to it (design §17).
    ///   attached  a vanilla Sign whose collider bounds come within 0.15 m of the chest's collider bounds
    ///   when      a tag change (ChestTags.OnSet, on the chest's owner), a sign placed (Sign.Awake on its new ZDO's owner), a tagged chest
    ///             loading on its owner (once) - never per frame
    ///   never     a player's text: a sign is written only if it is empty OR it still shows exactly the label we wrote last (the sign's ZDO
    ///             mark "core_sign_auto"); a player's edit breaks that match for good. Per chest: "core_chest_signlabel" (default on).
    ///   writing   owner-applied: our own sign -> set directly; else "Core_SignLabel" {text, expectedOld} to the sign's owner,
    ///             which re-checks the rule there (compare-and-set) before writing ZDOVars.s_text + author display name "Chest tags" + mark.
    ///             Vanilla's Sign.SetText is never called (it claims ownership).
    /// </summary>
    internal static class SignLabels
    {
        internal const string MarkKey = "core_sign_auto", ChestToggleKey = "core_chest_signlabel", Rpc = "Core_SignLabel";
        private const float SignTouchDistance = 0.15f;
        private const int MaxOverlapHits = 32;
        private const float NameCacheSeconds = 5f;
        private const int NameCacheMaxEntries = 512;
        private static readonly Collider[] s_hits = new Collider[MaxOverlapHits];
        private static readonly HashSet<ZNetView> s_registered = new HashSet<ZNetView>();

        internal static bool On => StorageFeatures.Feature(StorageFeatures.Part.SignLabels) && StorageConfig.SignLabels != null && StorageConfig.SignLabels.Value;

        internal static bool ChestWants(Container c)
        {
            ZNetView nv = ContainerRegistry.NView(c);
            return nv != null && nv.IsValid() && nv.GetZDO().GetBool(ChestToggleKey, true);
        }

        /// <summary>Re-labels every sign attached to the chest from its current tags (empty tags = our labels cleared).</summary>
        internal static void Refresh(Container c)
        {
            if (!On || c == null || !ChestWants(c)) return;
            string label = ChestTags.Label(ChestTags.TagsOf(c));
            foreach (Sign s in Attached(c)) Write(s, label);
        }

        internal static List<Sign> Attached(Container c)
        {
            var list = new List<Sign>();
            Bounds b = BoundsOf(c.gameObject);
            if (b.size == Vector3.zero) return list;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * SignTouchDistance, s_hits, Quaternion.identity);
            for (int i = 0; i < n; i++)
            {
                Sign s = s_hits[i] != null ? s_hits[i].GetComponentInParent<Sign>() : null;
                if (s != null && !list.Contains(s)) list.Add(s);
            }
            return list;
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>();
            if (cols.Length == 0) return new Bounds();
            Bounds b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
            return b;
        }

        private static readonly AccessTools.FieldRef<Sign, ZNetView> SignView = AccessTools.FieldRefAccess<Sign, ZNetView>("m_nview");

        private static void Write(Sign s, string label)
        {
            ZNetView nv = SignView(s);
            if (nv == null || !nv.IsValid()) return;
            string current = nv.GetZDO().GetString(ZDOVars.s_text, "");
            if (!Ours(nv.GetZDO(), current)) return;   // a player's text: never
            if (current == label) return;
            if (nv.IsOwner()) Apply(nv, s, label, current);
            else { RegisterOn(s); nv.InvokeRPC(Rpc, label, current); }
        }

        // The text of a sign on the chest that a PLAYER wrote
        // (never our auto label), rich-text tags stripped; null when none. Cached 5 s per chest (a physics overlap).
        private static readonly Dictionary<int, (string text, float at)> s_nameCache = new Dictionary<int, (string, float)>();
        internal static string PlayerSignText(Container c)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (s_nameCache.TryGetValue(id, out var hit) && Time.time - hit.at < NameCacheSeconds) return hit.text;
            string found = null;
            foreach (Sign s in Attached(c))
            {
                ZNetView nv = SignView(s);
                if (nv == null || !nv.IsValid()) continue;
                string text = nv.GetZDO().GetString(ZDOVars.s_text, "");
                if (text.Length == 0 || Ours(nv.GetZDO(), text)) continue;
                text = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "").Trim();
                if (text.Length > 0) { found = text; break; }
            }
            if (s_nameCache.Count > NameCacheMaxEntries) s_nameCache.Clear();
            s_nameCache[id] = (found, Time.time);
            return found;
        }

        private static bool Ours(ZDO zdo, string current) => current.Length == 0 || zdo.GetString(MarkKey, "\u0001") == current;

        internal static void RegisterOn(Sign s)
        {
            ZNetView nv = SignView(s);
            if (nv == null || !nv.IsValid() || s_registered.Contains(nv)) return;
            s_registered.Add(nv);
            nv.Register<string, string>(Rpc, (sender, label, expectedOld) =>
            {
                if (!StorageFeatures.Active || !nv.IsValid() || !nv.IsOwner()) return;
                string now = nv.GetZDO().GetString(ZDOVars.s_text, "");
                if (now != expectedOld || !Ours(nv.GetZDO(), now)) return;   // compare-and-set: changed since, or a player's text
                Apply(nv, s, label, now);
            });
        }

        private static void Apply(ZNetView nv, Sign s, string label, string old)
        {
            if (label.Length > s.m_characterLimit) label = label.Substring(0, s.m_characterLimit);
            nv.GetZDO().Set(ZDOVars.s_text, label);
            nv.GetZDO().Set(ZDOVars.s_authorDisplayName, label.Length > 0 ? "Chest tags" : "");
            nv.GetZDO().Set(MarkKey, label);
            FiresLogger.LogDebug($"[Storage] sign label '{old}' -> '{label}'");
        }

        /// <summary>A sign placed next to a tagged chest: label it (on the sign's new owner = the builder).</summary>
        internal static void OnSignAwake(Sign s)
        {
            if (!On || s == null) return;
            RegisterOn(s);
            ZNetView nv = SignView(s);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            Bounds b = BoundsOf(s.gameObject);
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * SignTouchDistance, s_hits, Quaternion.identity);
            for (int i = 0; i < n; i++)
            {
                Container c = s_hits[i] != null ? s_hits[i].GetComponentInParent<Container>() : null;
                if (c != null && ChestWants(c) && ChestTags.TagsOf(c).Count > 0) { Write(s, ChestTags.Label(ChestTags.TagsOf(c))); return; }
            }
        }

        internal static void SetChestToggle(Container c, bool on)
        {
            ZNetView nv = ContainerRegistry.NView(c);
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            nv.GetZDO().Set(ChestToggleKey, on);
            if (on) Refresh(c);
        }
    }
}
