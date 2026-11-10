using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Tags on a chest saying what belongs in it.
    ///   kinds    i:&lt;prefab&gt; (an item), c:&lt;Category&gt; (Ores, Metals, Wood, Stone, Food, Meads, Ammo, Seeds, Trophies, Valuables, Materials,
    ///            Armor, Weapons, Tools), d:&lt;Drop type&gt; (Boss drops, Creature drops, Ore drops, Plant drops); a leading '!' excludes
    ///   storage  the chest's ZDO "core_chest_tags" as "c:Ores;i:Wood;!i:Copper"; written by the chest's OWNER through the
    ///            "Core_ChestTagsSet" RPC, synced to everyone by vanilla ZDO sync, persisted with the world
    ///   match    Match(chest, item, out score): null = no opinion (untagged, storage off, or no rule); false = refused (a not-tag matches,
    ///            or the chest has includes and none match); true = wanted, score 3 item / 2 drop type / 1 category. Never throws.
    ///            Companions call it in SmartStorageOrganizer.FindBestChestForItem (null = today's behaviour).
    /// Drop types are worked out once from ZNetScene's prefabs (bosses' and creatures' CharacterDrop, DropOnDestroyed / MineRock drop
    /// tables, Pickable) and cached.
    /// </summary>
    public static class ChestTags
    {
        public const string ZdoKey = "core_chest_tags";
        internal const string RpcSet = "Core_ChestTagsSet";
        public const int MaxTags = 12;
        private const int MaxLabelNames = 3;

        public static readonly string[] Categories = { "Ores", "Metals", "Wood", "Stone", "Food", "Meads", "Ammo", "Seeds", "Trophies", "Valuables", "Materials", "Armor", "Weapons", "Tools" };
        public static readonly string[] DropTypes = { "Boss drops", "Creature drops", "Ore drops", "Plant drops" };

        private static readonly HashSet<string> Metals = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Copper", "Tin", "Bronze", "Iron", "Silver", "BlackMetal", "Flametal", "FlametalNew" };
        private static readonly HashSet<string> WoodSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Wood", "FineWood", "RoundLog", "ElderBark", "YggdrasilWood", "Blackwood", "Ashwood" };
        private static readonly HashSet<string> StoneSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Stone", "Flint", "Obsidian", "Grausten", "BlackMarble" };
        private static readonly HashSet<string> ValuableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Coins", "Amber", "AmberPearl", "Ruby", "SilverNecklace" };

        public struct Tag { public bool Not; public char Kind; public string Value; public override string ToString() => (Not ? "!" : "") + Kind + ":" + Value; }

        // ---- parse / format ----

        public static List<Tag> Parse(string raw)
        {
            var list = new List<Tag>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split(';'))
            {
                string p = part.Trim();
                bool not = p.StartsWith("!");
                if (not) p = p.Substring(1);
                if (p.Length < 3 || p[1] != ':' || "icd".IndexOf(p[0]) < 0) continue;
                list.Add(new Tag { Not = not, Kind = p[0], Value = p.Substring(2) });
                if (list.Count >= MaxTags) break;
            }
            return list;
        }

        public static string Format(IEnumerable<Tag> tags) => string.Join(";", tags.Take(MaxTags).Select(t => t.ToString()).ToArray());

        /// <summary>A player-typed list ("Ores, Wood, !Copper, Boss drops") into tags: a category or drop-type name wins, else an item
        /// prefab name (or a localized item name) found in ObjectDB; unknown words are dropped.</summary>
        public static List<Tag> FromText(string text, out List<string> unknown)
        {
            var list = new List<Tag>(); unknown = new List<string>();
            foreach (string part in (text ?? "").Split(','))
            {
                string w = part.Trim(); bool not = w.StartsWith("!") || w.StartsWith("no ", StringComparison.OrdinalIgnoreCase);
                if (w.StartsWith("!")) w = w.Substring(1).Trim(); else if (not) w = w.Substring(3).Trim();
                if (w.Length == 0) continue;
                string cat = Categories.FirstOrDefault(c => string.Equals(c, w, StringComparison.OrdinalIgnoreCase));
                string drop = DropTypes.FirstOrDefault(d => string.Equals(d, w, StringComparison.OrdinalIgnoreCase));
                if (cat != null) list.Add(new Tag { Not = not, Kind = 'c', Value = cat });
                else if (drop != null) list.Add(new Tag { Not = not, Kind = 'd', Value = drop });
                else if (ItemPrefab(w) is string prefab) list.Add(new Tag { Not = not, Kind = 'i', Value = prefab });
                else unknown.Add(w);
            }
            return list;
        }

        private static string ItemPrefab(string word)
        {
            if (ObjectDB.instance == null) return null;
            GameObject direct = ObjectDB.instance.GetItemPrefab(word);
            if (direct != null) return direct.name;
            foreach (GameObject go in ObjectDB.instance.m_items)
            {
                var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop != null && string.Equals(Localization.instance.Localize(drop.m_itemData.m_shared.m_name), word, StringComparison.OrdinalIgnoreCase)) return go.name;
            }
            return null;
        }

        /// <summary>Readable label: up to 3 tags joined by " · " (Latin-1), "no X" for not-tags, "+N" for the rest.</summary>
        public static string Label(IList<Tag> tags)
        {
            string Name(Tag t) => (t.Not ? "no " : "") + (t.Kind == 'i' ? ItemName(t.Value) : t.Value);
            if (tags.Count == 0) return "";
            string s = string.Join(" · ", tags.Take(tags.Count > MaxLabelNames ? MaxLabelNames - 1 : MaxLabelNames).Select(Name).ToArray());
            return tags.Count > MaxLabelNames ? s + " +" + (tags.Count - (MaxLabelNames - 1)) : s;
        }

        private static string ItemName(string prefab)
        {
            var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            var d = go != null ? go.GetComponent<ItemDrop>() : null;
            return d != null ? Localization.instance.Localize(d.m_itemData.m_shared.m_name) : prefab;
        }

        // ---- reading a chest ----

        public static List<Tag> TagsOf(Container c)
        {
            if (c == null || !StorageFeatures.Active) return new List<Tag>();
            ZNetView nv = ContainerRegistry.NView(c);
            return nv != null && nv.IsValid() ? Parse(nv.GetZDO().GetString(ZdoKey, "")) : new List<Tag>();
        }

        /// <summary>See the class summary. Never throws (a fault = null, logged once).</summary>
        public static bool? Match(Container chest, ItemDrop.ItemData item, out int score)
        {
            score = 0;
            try
            {
                if (chest == null || item == null || !StorageFeatures.Feature(StorageFeatures.Part.Tags)) return null;
                List<Tag> tags = TagsOf(chest);
                if (tags.Count == 0) return null;
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
                bool anyInclude = false;
                foreach (Tag t in tags)
                {
                    int s = Score(t, item, prefab);
                    if (t.Not) { if (s > 0) { score = 0; return false; } continue; }
                    anyInclude = true;
                    if (s > score) score = s;
                }
                if (score > 0) return true;
                return anyInclude ? false : (bool?)null;
            }
            catch (Exception e)
            {
                if (!s_matchFaultLogged) { s_matchFaultLogged = true; FiresLogger.LogWarning($"[Storage] ChestTags.Match fault (no opinion returned): {e.Message}"); }
                score = 0; return null;
            }
        }
        private static bool s_matchFaultLogged;

        private static int Score(Tag t, ItemDrop.ItemData item, string prefab)
        {
            switch (t.Kind)
            {
                case 'i': return prefab != null && string.Equals(prefab, t.Value, StringComparison.OrdinalIgnoreCase) ? 3 : 0;
                case 'd': return prefab != null && DropsOf(t.Value).Contains(prefab) ? 2 : 0;
                case 'c': return InCategory(t.Value, item, prefab) ? 1 : 0;
            }
            return 0;
        }

        public static bool InCategory(string cat, ItemDrop.ItemData item, string prefab)
        {
            var type = item.m_shared.m_itemType; prefab = prefab ?? "";
            switch (cat)
            {
                case "Ores": return type == ItemDrop.ItemData.ItemType.Material && (prefab.EndsWith("Ore") || prefab.EndsWith("Scrap") || prefab.Contains("Ore"));
                case "Metals": return Metals.Contains(prefab);
                case "Wood": return WoodSet.Contains(prefab);
                case "Stone": return StoneSet.Contains(prefab);
                case "Meads": return type == ItemDrop.ItemData.ItemType.Consumable && prefab.StartsWith("Mead");
                case "Food": return type == ItemDrop.ItemData.ItemType.Consumable && item.m_shared.m_food > 0f && !prefab.StartsWith("Mead");
                case "Ammo": return type == ItemDrop.ItemData.ItemType.Ammo || type == ItemDrop.ItemData.ItemType.AmmoNonEquipable;
                case "Seeds": return prefab.Contains("Seed") || prefab.StartsWith("Sapling");
                case "Trophies": return type == ItemDrop.ItemData.ItemType.Trophy;
                case "Valuables": return ValuableSet.Contains(prefab);
                case "Armor": return type == ItemDrop.ItemData.ItemType.Helmet || type == ItemDrop.ItemData.ItemType.Chest || type == ItemDrop.ItemData.ItemType.Legs
                    || type == ItemDrop.ItemData.ItemType.Shoulder || type == ItemDrop.ItemData.ItemType.Shield || type == ItemDrop.ItemData.ItemType.Utility;
                case "Weapons": return type == ItemDrop.ItemData.ItemType.OneHandedWeapon || type == ItemDrop.ItemData.ItemType.TwoHandedWeapon
                    || type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft || type == ItemDrop.ItemData.ItemType.Bow;
                case "Tools": return type == ItemDrop.ItemData.ItemType.Tool || type == ItemDrop.ItemData.ItemType.Torch;
                case "Materials": return type == ItemDrop.ItemData.ItemType.Material;
            }
            return false;
        }

        // ---- drop types (built once) ----
        private static Dictionary<string, HashSet<string>> s_drops;

        private static HashSet<string> DropsOf(string dropType)
        {
            if (s_drops == null) BuildDrops();
            return s_drops.TryGetValue(dropType, out var set) ? set : new HashSet<string>();
        }

        private static void BuildDrops()
        {
            s_drops = DropTypes.ToDictionary(d => d, d => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (ZNetScene.instance == null) { s_drops = null; return; }
            foreach (GameObject go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                var cd = go.GetComponent<CharacterDrop>();
                if (cd != null && cd.m_drops != null)
                {
                    bool boss = go.GetComponent<Character>()?.m_boss ?? false;
                    foreach (var d in cd.m_drops) if (d?.m_prefab != null) s_drops[boss ? "Boss drops" : "Creature drops"].Add(d.m_prefab.name);
                }
                AddTable(go.GetComponent<DropOnDestroyed>()?.m_dropWhenDestroyed, go.GetComponent<MineRock5>() != null || go.GetComponent<MineRock>() != null ? "Ore drops" : null);
                AddTable(go.GetComponent<MineRock5>()?.m_dropItems, "Ore drops");
                AddTable(go.GetComponent<MineRock>()?.m_dropItems, "Ore drops");
                var pick = go.GetComponent<Pickable>();
                if (pick?.m_itemPrefab != null) s_drops["Plant drops"].Add(pick.m_itemPrefab.name);
            }
            FiresLogger.LogDebug("[Storage] chest tag drop types: " + string.Join(", ", s_drops.Select(kv => kv.Key + " " + kv.Value.Count).ToArray()));
        }

        private static void AddTable(DropTable table, string dropType)
        {
            if (table?.m_drops == null || dropType == null) return;
            foreach (var d in table.m_drops) if (d.m_item != null) s_drops[dropType].Add(d.m_item.name);
        }

        // ---- Auto-lock: a tag that is not a real lock to keep players out, but keeps
        // auto sort / auto store and companions from pulling from those chests ----
        // A locked chest is skipped by every automated path both ways: hopper sort / merge, craft-from-containers, quick stack, network
        // Deposit / Deposit All, companions' haul / chores / deposit / gear-up. Players still open it, and the storage terminal still lists it.
        public const string AutoLockKey = "core_chest_autolock";
        private static readonly HashSet<string> s_lockLogged = new HashSet<string>();

        /// <summary>The chest's name for lists - a player-written sign on it, else its tags' label, else null. ADD-ONLY contract.</summary>
        public static string DisplayName(Container c)
        {
            if (c == null) return null;
            string sign = SignLabels.PlayerSignText(c);
            if (!string.IsNullOrEmpty(sign)) return sign;
            var tags = TagsOf(c);
            return tags.Count > 0 ? Label(tags) : null;
        }

        /// <summary>True when the chest is tagged Locked: no automated system puts into or takes from it.</summary>
        public static bool IsAutoLocked(Container c)
        {
            if (c == null) return false;
            ZNetView nv = ContainerRegistry.NView(c);
            return nv != null && nv.IsValid() && nv.GetZDO().GetBool(AutoLockKey, false);
        }

        /// <summary>IsAutoLocked, logging once per chest + system when it skips.</summary>
        public static bool SkipLocked(Container c, string system)
        {
            if (!IsAutoLocked(c)) return false;
            ZNetView nv = ContainerRegistry.NView(c);
            string key = (nv != null && nv.IsValid() ? nv.GetZDO().m_uid.ToString() : c.GetInstanceID().ToString()) + "|" + system;
            if (s_lockLogged.Add(key)) FiresLogger.LogDebug($"[Storage] '{c.m_name}' is auto-locked: skipped by {system}");
            return true;
        }

        /// <summary>Sets the Locked tag (the opener owns the open chest; anyone else's call is ignored).</summary>
        public static void SetAutoLock(Container c, bool on)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            nv.GetZDO().Set(AutoLockKey, on);
            FiresLogger.LogDebug($"[Storage] chest '{c.m_name}' auto-lock {(on ? "on" : "off")}");
        }

        // ---- writing (owner-applied) ----

        internal static void RegisterOn(Container c, ZNetView nv) =>
            nv.Register<string, long>(RpcSet, (sender, tags, requester) => OnSet(c, nv, tags, requester));

        private static void OnSet(Container c, ZNetView nv, string tags, long requester)
        {
            if (!StorageFeatures.Active || c == null || nv == null || !nv.IsValid() || !nv.IsOwner()) return;   // only the owner writes
            if (!ContainerRegistry.CheckAccessFn(c, requester)) return;
            string clean = Format(Parse(tags));
            nv.GetZDO().Set(ZdoKey, clean);
            FiresLogger.LogDebug($"[Storage] chest '{c.m_name}' tags set to '{clean}' by player {requester}");
            SignLabels.Refresh(c);   // stage 6 hook (no-op until then)
        }

        /// <summary>Asks the chest's owner to set its tags (vanilla routes the RPC to the ZDO owner).</summary>
        public static void RequestSet(Container c, IEnumerable<Tag> tags)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            Player me = Player.m_localPlayer;
            if (nv == null || !nv.IsValid() || me == null) return;
            nv.InvokeRPC(RpcSet, Format(tags), me.GetPlayerID());
        }
    }

}
