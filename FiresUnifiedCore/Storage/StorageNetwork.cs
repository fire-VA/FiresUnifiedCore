using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// One network over the base's
    /// existing chests, and the data API the Storage Terminal window is built on. ADD-ONLY contract.
    ///   base      the ward (PrivateArea) the anchor stands in that the player is permitted on (its whole radius); no ward: Network Radius
    ///             around the anchor (Storage Network Radius, default 20 m, server)
    ///   members   ContainerRegistry.Lendable (access, ward, a reachable owner) minus carts, ship holds, companion bags and hoppers; a
    ///             chest someone else has open is listed but read-only (Take/Deposit skip it)
    ///   index     shared item name -> (template, total, chests nearest first), rebuilt at most every 0.5 s on read and right after our own ops
    ///   writes    ContainerOps.Take / Put (owner-applied, per-chest ledger: never doubled); single items (max stack 1) move as the item
    ///             object, only into / out of a chest this peer owns (one nobody has open is claimed first; the call reports "retry")
    /// </summary>
    public static class StorageNetwork
    {
        public enum Category { All, Materials, Food, Weapons, Armor, Tools, Ammo, Trophies, Misc }

        /// <summary>One row of the terminal: an item kind across the network.</summary>
        public sealed class Entry
        {
            public string Name;                 // shared name ($item_wood) - the key for Take / Where / Favourite
            public ItemDrop.ItemData Template;  // an instance from one chest (icon, tooltip, max stack) - read-only, never moved
            public int Total;                   // count across the network
            public int Chests;                  // chests holding it
            public Category Category;
            public bool Favourite;
        }

        private sealed class Holding { public Container Chest; public int Count; }

        private static Vector3 s_anchor;
        private static float s_radius;
        private static string s_area = "";
        private static Player s_player;
        private static readonly List<Container> s_members = new List<Container>();
        private static readonly Dictionary<string, Entry> s_index = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, List<Holding>> s_where = new Dictionary<string, List<Holding>>();
        private static float s_builtAt = -999f;
        private static int s_outOfRange, s_signature;
        private const float RebuildSeconds = 0.5f;
        private const float DefaultNetworkRadius = 20f;
        private const string FavKey = "core_storage_fav";

        /// <summary>Raised (main thread) when the network's contents change after a rebuild - the window refreshes on it.</summary>
        public static event Action Changed;

        public static bool IsOpen => s_player != null;
        public static int ChestCount => s_members.Count;
        public static int OutOfRangeCount => s_outOfRange;
        /// <summary>"ward 'Name' (32 m)" or "20 m around you" - for the window's header.</summary>
        public static string AreaLabel => s_area;

        /// <summary>The network area around an anchor: the permitted ward it stands in (centre + radius), else the Network Radius around it.
        /// The one rule for a terminal's network, shared by Open and the hopper sort.</summary>
        internal static void AreaOf(Player player, Vector3 anchor, out Vector3 centre, out float radius, out string label)
        {
            PrivateArea ward = null;
            foreach (PrivateArea a in PrivateArea.m_allAreas)
                if (a != null && a.IsEnabled() && player != null && a.IsPermitted(player.GetPlayerID()) && Vector3.Distance(a.transform.position, anchor) <= a.m_radius) { ward = a; break; }
            if (ward != null) { centre = ward.transform.position; radius = ward.m_radius; label = $"ward '{ward.m_name}' ({ward.m_radius:0} m)"; }
            else { centre = anchor; radius = StorageConfig.NetworkRadius != null ? StorageConfig.NetworkRadius.Value : DefaultNetworkRadius; label = $"{radius:0} m around you"; }
        }

        // ---- storage terminals (registered by the mod that builds them, e.g. FiresStorageNetwork) ----
        private static readonly List<Component> s_terminals = new List<Component>();
        /// <summary>A storage terminal joined the world: a hopper inside its network area sorts into that whole network. Add-only.</summary>
        public static void RegisterTerminal(Component terminal) { if (terminal != null && !s_terminals.Contains(terminal)) s_terminals.Add(terminal); }
        /// <summary>The terminal left the world (destroyed / unloaded). Add-only.</summary>
        public static void UnregisterTerminal(Component terminal) { s_terminals.Remove(terminal); }

        /// <summary>The network a position belongs to: the nearest registered terminal whose network area (ward or Network Radius)
        /// contains <paramref name="pos"/>. False when no terminal's network reaches it.</summary>
        internal static bool TerminalNetworkAt(Player player, Vector3 pos, out Vector3 centre, out float radius, out Vector3 terminalPos)
        {
            centre = terminalPos = Vector3.zero; radius = 0f; bool found = false; float best = float.MaxValue;
            s_terminals.RemoveAll(t => t == null);
            foreach (var t in s_terminals)
            {
                Vector3 tp = t.transform.position;
                AreaOf(player, tp, out Vector3 c, out float r, out _);
                float d = Vector3.Distance(pos, c);
                if (d > r) continue;
                float dt = Vector3.Distance(pos, tp);
                if (dt < best) { best = dt; found = true; centre = c; radius = r; terminalPos = tp; }
            }
            return found;
        }

        /// <summary>Opens (or re-anchors) the network for <paramref name="player"/> around <paramref name="anchor"/> (a terminal's position, or the player's).
        /// False when storage features are off or there is no player.</summary>
        public static bool Open(Player player, Vector3 anchor)
        {
            if (player == null || !StorageFeatures.Active) return false;
            s_player = player;
            AreaOf(player, anchor, out s_anchor, out s_radius, out s_area);
            Rebuild(force: true);
            FiresLogger.LogInfo($"[Storage] network open: {s_members.Count} chest(s) in {s_area}, {s_outOfRange} out of range, {s_index.Count} item kind(s)");
            return true;
        }

        public static void Close() { s_player = null; s_members.Clear(); s_index.Clear(); s_where.Clear(); }

        /// <summary>The rows for the window: filtered by category and a case-insensitive search on the display name; favourites first, then name.</summary>
        public static List<Entry> Items(Category category = Category.All, string search = null)
        {
            Rebuild(force: false);
            var fav = Favourites();
            string s = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var list = new List<Entry>();
            foreach (Entry e in s_index.Values)
            {
                if (category != Category.All && e.Category != category) continue;
                string display = Localization.instance != null ? Localization.instance.Localize(e.Name) : e.Name;
                if (s != null && display.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0) continue;
                e.Favourite = fav.Contains(e.Name);
                list.Add(e);
            }
            list.Sort((a, b) => a.Favourite != b.Favourite ? (a.Favourite ? -1 : 1)
                : string.Compare(Localization.instance != null ? Localization.instance.Localize(a.Name) : a.Name,
                                 Localization.instance != null ? Localization.instance.Localize(b.Name) : b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>The chests holding an item (nearest the anchor first) - for "where is it".</summary>
        public static List<Container> Where(string sharedName)
        {
            Rebuild(force: false);
            return s_where.TryGetValue(sharedName ?? "", out var h) ? h.Select(x => x.Chest).Where(c => c != null).ToList() : new List<Container>();
        }

        /// <summary>Takes up to <paramref name="amount"/> of an item into the player's bag, nearest chests first. <paramref name="done"/> gets the
        /// amount added (0..amount) and a reason when short ("bag full", "chest in use", "retry" for a single item whose chest was just claimed).</summary>
        public static void Take(string sharedName, int amount, Action<int, string> done = null)
        {
            if (s_player == null || amount <= 0) { done?.Invoke(0, "network not open"); return; }
            s_player.StartCoroutine(TakeRun(sharedName, amount, done));
        }

        private static IEnumerator TakeRun(string sharedName, int amount, Action<int, string> done)
        {
            Rebuild(force: true);
            if (!s_where.TryGetValue(sharedName ?? "", out var holds) || holds.Count == 0) { done?.Invoke(0, "none in the network"); yield break; }
            Inventory bag = s_player.GetInventory();
            int got = 0; string why = null;
            foreach (Holding h in holds.ToList())
            {
                if (got >= amount) break;
                Container c = h.Chest;
                if (c == null) continue;
                if (c.IsInUse() && !ContainerRegistry.NView(c).IsOwner()) { why = "a chest is in use"; continue; }
                Inventory inv = ContainerRegistry.Inv(c);
                ItemDrop.ItemData sample = inv?.GetAllItems().FirstOrDefault(i => i.m_shared.m_name == sharedName);
                if (sample == null) continue;
                if (sample.m_shared.m_maxStackSize <= 1)
                {
                    // a single item keeps its own data: moved as the object, from a chest this peer owns
                    ZNetView nv = ContainerRegistry.NView(c);
                    if (!nv.IsOwner()) { if (!c.IsInUse()) nv.ClaimOwnership(); why = "retry"; continue; }
                    if (!bag.CanAddItem(sample, 1)) { why = "bag full"; break; }
                    if (bag.AddItem(sample)) { inv.RemoveItem(sample); got++; }
                    continue;
                }
                string prefab = sample.m_dropPrefab != null ? sample.m_dropPrefab.name : null;
                int want = Mathf.Min(amount - got, h.Count);
                if (!bag.CanAddItem(sample, want)) { want = FreeRoom(bag, sample, want); if (want <= 0) { why = "bag full"; break; } }
                int granted = 0;
                yield return ContainerOps.Take(c, s_player, sharedName, prefab, want, g => granted = g);
                got += granted;
            }
            Rebuild(force: true);
            FiresLogger.LogDebug($"[Storage] network take {got}/{amount} {sharedName}{(why != null ? $" ({why})" : "")}");
            done?.Invoke(got, got < amount ? (why ?? "short") : null);
        }

        /// <summary>Deposits one inventory item (its whole stack) from the player's bag into the network: a chest whose tags want it > a chest
        /// that holds it > any untagged chest with room, nearest first. <paramref name="done"/> gets the amount placed and a reason when short.</summary>
        public static void Deposit(ItemDrop.ItemData item, Action<int, string> done = null)
        {
            if (s_player == null || item == null) { done?.Invoke(0, "network not open"); return; }
            s_player.StartCoroutine(DepositRun(item, done));
        }

        /// <summary>Deposits every bag item except equipped items, the hotbar row and favourites. <paramref name="done"/> gets the stacks placed.</summary>
        public static void DepositAll(Action<int> done = null)
        {
            if (s_player == null) { done?.Invoke(0); return; }
            var fav = Favourites();
            var items = s_player.GetInventory().GetAllItems().Where(i => i != null && !i.m_equipped && i.m_gridPos.y != 0 && !fav.Contains(i.m_shared.m_name)).ToList();
            s_player.StartCoroutine(DepositAllRun(items, done));
        }

        private static IEnumerator DepositAllRun(List<ItemDrop.ItemData> items, Action<int> done)
        {
            int placed = 0;
            foreach (var it in items) { int n = 0; yield return DepositRun(it, (g, _) => n = g); if (n > 0) placed++; }
            FiresLogger.LogDebug($"[Storage] network deposit all: {placed}/{items.Count} stack(s) placed");
            done?.Invoke(placed);
        }

        // ---- bag deposit (VABackpacks' Deposit option): a separate run that never touches the open terminal network ----

        /// <summary>Deposits each stack only into a chest within <paramref name="radius"/> of <paramref name="centre"/> that already holds
        /// that item, nearest first, through the same lending filters and owner-applied puts as the terminal. The stacks are taken from the
        /// player's inventory. done(stacksFullyPlaced, stacksLeft, firstReasonOrNull). ADD-ONLY.</summary>
        public static void DepositMatching(Player player, Vector3 centre, float radius, IList<ItemDrop.ItemData> items, Action<int, int, string> done) =>
            DepositMatching(player, player != null ? player.GetInventory() : null, centre, radius, items, done);

        /// <summary>As DepositMatching, with the stacks taken from (and anything not placed returned to) <paramref name="source"/>,
        /// e.g. a bag's own inventory. ADD-ONLY.</summary>
        public static void DepositMatching(Player player, Inventory source, Vector3 centre, float radius, IList<ItemDrop.ItemData> items, Action<int, int, string> done)
        {
            if (player == null || source == null || items == null || items.Count == 0) { done?.Invoke(0, items?.Count ?? 0, "nothing to deposit"); return; }
            player.StartCoroutine(DepositMatchingRun(player, source, centre, radius, new List<ItemDrop.ItemData>(items), done));
        }

        private static IEnumerator DepositMatchingRun(Player player, Inventory source, Vector3 centre, float radius, List<ItemDrop.ItemData> items, Action<int, int, string> done)
        {
            var chests = new List<Container>();
            foreach (Container c in ContainerRegistry.Lendable(centre, radius, player, "bag deposit")) if (IsMemberCandidate(c)) chests.Add(c);
            int placedFully = 0, left = 0; string firstWhy = null;
            foreach (var item in items)
            {
                if (item == null || item.m_dropPrefab == null || item.m_stack <= 0 || !source.ContainsItem(item)) { left++; firstWhy = firstWhy ?? "item no longer in the bag"; continue; }
                string shared = item.m_shared.m_name, prefab = item.m_dropPrefab.name;
                int amount = item.m_stack, quality = item.m_quality, placed = 0;
                var holders = new List<Container>();
                foreach (var c in chests) { var inv = ContainerRegistry.Inv(c); if (inv != null && inv.CountItems(shared, -1, false) > 0) holders.Add(c); }
                holders.Sort((a, b) => (a.transform.position - centre).sqrMagnitude.CompareTo((b.transform.position - centre).sqrMagnitude));
                if (holders.Count == 0) { left++; firstWhy = firstWhy ?? $"no chest nearby holds {shared}"; continue; }
                source.RemoveItem(item);   // leaves the bag first; what no chest takes comes back below
                foreach (var c in holders)
                {
                    if (placed >= amount) break;
                    int granted = 0;
                    yield return ContainerOps.Put(c, player, prefab, quality, amount - placed, g => granted = g);
                    if (granted > 0)
                    {
                        placed += granted;
                        FiresLogger.LogDebug($"[Storage] bag deposit {granted} {shared} -> '{c.m_name}' (holds it, {Vector3.Distance(c.transform.position, centre):0.0} m)");
                    }
                }
                int back = amount - placed;
                if (back > 0) { source.AddItem(prefab, back, quality, 0, 0L, "", false, false); left++; firstWhy = firstWhy ?? "the chests holding it are full"; }
                else placedFully++;
            }
            FiresLogger.LogDebug($"[Storage] bag deposit: {placedFully} stack(s) placed, {left} left within {radius:0} m{(firstWhy != null ? " (" + firstWhy + ")" : "")}");
            done?.Invoke(placedFully, left, firstWhy);
        }
        private static IEnumerator DepositRun(ItemDrop.ItemData item, Action<int, string> done)
        {
            Rebuild(force: true);
            Inventory bag = s_player.GetInventory();
            Container target = BestTarget(item, out string reason);
            if (target == null) { done?.Invoke(0, "no chest wants it or has room"); yield break; }
            if (item.m_shared.m_maxStackSize <= 1 || item.m_dropPrefab == null)
            {
                ZNetView nv = ContainerRegistry.NView(target);
                if (!nv.IsOwner()) { if (!target.IsInUse()) nv.ClaimOwnership(); done?.Invoke(0, "retry"); yield break; }
                Inventory ti = ContainerRegistry.Inv(target);
                if (ti != null && ti.AddItem(item)) { bag.RemoveItem(item); Rebuild(force: true); done?.Invoke(1, null); }
                else done?.Invoke(0, "no room");
                yield break;
            }
            string prefab = item.m_dropPrefab.name, name = item.m_shared.m_name;
            int amount = item.m_stack, quality = item.m_quality;
            bag.RemoveItem(item);   // leaves the bag first; what the chest doesn't take comes back
            int granted = 0;
            yield return ContainerOps.Put(target, s_player, prefab, quality, amount, g => granted = g);
            int back = amount - Mathf.Max(0, granted);
            if (back > 0) bag.AddItem(prefab, back, quality, 0, 0L, "", false, false);
            Rebuild(force: true);
            FiresLogger.LogDebug($"[Storage] network deposit {granted}/{amount} {name} -> '{target.m_name}' ({reason})");
            done?.Invoke(granted, granted < amount ? "partly placed" : null);
        }

        /// <summary>Toggles an item kind as a favourite for this player (saved on the player): Deposit All never takes favourites.</summary>
        public static void Favourite(string sharedName, bool on)
        {
            if (s_player == null || string.IsNullOrEmpty(sharedName)) return;
            var fav = Favourites();
            if (on) fav.Add(sharedName); else fav.Remove(sharedName);
            s_player.m_customData[FavKey] = string.Join(";", fav.ToArray());
            Changed?.Invoke();
        }

        /// <summary>A player's favourite item kinds (shared names), without an open network (the remote window).</summary>
        public static HashSet<string> FavouritesOf(Player p)
        {
            if (p != null && p.m_customData.TryGetValue(FavKey, out string raw) && !string.IsNullOrEmpty(raw))
                return new HashSet<string>(raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            return new HashSet<string>();
        }

        public static HashSet<string> Favourites()
        {
            if (s_player != null && s_player.m_customData.TryGetValue(FavKey, out string raw) && !string.IsNullOrEmpty(raw))
                return new HashSet<string>(raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            return new HashSet<string>();
        }

        public static Category CategoryOf(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Material: return Category.Materials;
                case ItemDrop.ItemData.ItemType.Consumable: return Category.Food;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow: return Category.Weapons;
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility: return Category.Armor;
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch: return Category.Tools;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return Category.Ammo;
                case ItemDrop.ItemData.ItemType.Trophy: return Category.Trophies;
                default: return Category.Misc;
            }
        }

        // ---- internals ----

        internal static bool IsMemberCandidate(Container t) =>
            t != null && t.GetComponent<HopperChest>() == null && !HopperChest.IsHopper(t)
            && t.GetComponentInParent<Vagon>() == null && t.GetComponentInParent<Ship>() == null
            && t.GetComponentInParent<FiresCore.Npc.CompanionController>() == null;

        /// <summary>How far (m) the player may be from the network anchor and still have its chests loaded (vanilla active area less margin).</summary>
        public const float LoadedReach = 192f;   // = RemoteStore's "loaded by a player" reach, so no distance gives an empty list

        private static void Rebuild(bool force)
        {
            if (s_player == null) return;
            if (!force && Time.time - s_builtAt < RebuildSeconds) return;
            s_builtAt = Time.time;
            int before = s_members.Count; s_members.Clear();
            // Chests count only while the player is within loaded reach of the anchor: after a long jump the old zone's containers
            // linger as objects for a while and must not be listed or deposited into (10-23: listed from 4 km, then emptied).
            float away = Vector3.Distance(s_player.transform.position, s_anchor);
            var lent = away <= LoadedReach ? ContainerRegistry.Lendable(s_anchor, s_radius, s_player, null) : new List<Container>();
            if (away > LoadedReach && (before > 0 || force)) FiresLogger.LogDebug($"[Storage] network rebuild: player {away:0} m from the anchor (more than {LoadedReach:0} m): its chests are not loaded here, none listed");
            foreach (Container c in lent) if (IsMemberCandidate(c)) s_members.Add(c);
            s_outOfRange = ContainerRegistry.SkipOwner;
            if (s_members.Count != before || force)   // every change in the member list says why (Fire 10-23: remote list emptied after a few frames)
                FiresLogger.LogDebug($"[Storage] network rebuild{(force ? " (forced)" : "")}: {before} -> {s_members.Count} chest(s) at ({s_anchor.x:0}, {s_anchor.z:0}) r {s_radius:0} m, player {Vector3.Distance(s_player.transform.position, s_anchor):0} m away; skipped range {ContainerRegistry.SkipRange}, invalid {ContainerRegistry.SkipInvalid}, access {ContainerRegistry.SkipAccess}, ward {ContainerRegistry.SkipWard}, owner not loaded {ContainerRegistry.SkipOwner}; grid holds {ContainerRegistry.GridCount} chest(s)");
            s_index.Clear(); s_where.Clear();
            int sig = s_members.Count;
            foreach (Container c in s_members)
            {
                Inventory inv = ContainerRegistry.Inv(c);
                if (inv == null) continue;
                foreach (ItemDrop.ItemData it in inv.GetAllItems())
                {
                    if (it == null) continue;
                    string key = it.m_shared.m_name;
                    if (!s_index.TryGetValue(key, out Entry e)) { e = new Entry { Name = key, Template = it, Category = CategoryOf(it) }; s_index[key] = e; s_where[key] = new List<Holding>(); }
                    e.Total += it.m_stack;
                    var hl = s_where[key];
                    Holding h = hl.Find(x => x.Chest == c);
                    if (h == null) { h = new Holding { Chest = c }; hl.Add(h); e.Chests++; }
                    h.Count += it.m_stack;
                    sig = sig * 31 + key.GetHashCode() * 7 + it.m_stack;
                }
            }
            if (sig != s_signature) { s_signature = sig; Changed?.Invoke(); }
        }

        private static Container BestTarget(ItemDrop.ItemData item, out string why)
        {
            why = null; Container best = null; int bestScore = int.MinValue;
            foreach (Container t in s_members)
            {
                if (t.IsInUse() && !ContainerRegistry.NView(t).IsOwner()) continue;
                if (ChestTags.SkipLocked(t, "network deposit")) continue;   // listed, never picked automatically
                Inventory ti = ContainerRegistry.Inv(t);
                if (ti == null || !ti.CanAddItem(item, 1)) continue;
                bool? m = ChestTags.Match(t, item, out int score);
                if (m == false) continue;
                int s; string reason;
                if (m == true) { s = 10 + score; reason = score >= 3 ? "name tag" : score == 2 ? "drop tag" : "category tag"; }
                else if (ti.CountItems(item.m_shared.m_name, -1, false) > 0) { s = 5; reason = "holds it"; }
                else if (ChestTags.TagsOf(t).Count == 0) { s = 0; reason = "any (room)"; }
                else continue;
                if (s > bestScore) { bestScore = s; best = t; why = reason; }
            }
            return best;
        }

        private static int FreeRoom(Inventory bag, ItemDrop.ItemData sample, int want)
        {
            int lo = 0, hi = want;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (bag.CanAddItem(sample, mid)) lo = mid; else hi = mid - 1; }
            return lo;
        }
    }
}
