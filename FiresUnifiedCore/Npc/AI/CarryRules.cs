using System.Collections.Generic;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>What <see cref="CarryRules.Plan"/> says a body should do with its load (the caller walks, opens, moves, drops).</summary>
    public sealed class CarryPlan
    {
        /// <summary>Something to store or drop now.</summary>
        public bool Act;
        /// <summary>Carried weight, the body's max carry weight and the threshold weight this plan used.</summary>
        public float Weight, Max, Threshold;
        /// <summary>The chest to store in (writable for the owner, wards allow), or null: none within reach.</summary>
        public Container Chest;
        /// <summary>Stacks and amounts to move into <see cref="Chest"/> (what is above the keep counts; move what fits).</summary>
        public readonly List<(ItemDrop.ItemData item, int amount)> Store = new List<(ItemDrop.ItemData, int)>();
        /// <summary>Stacks and amounts to drop: junk nothing uses, or with no chest while overweight the least valuable per weight.</summary>
        public readonly List<(ItemDrop.ItemData item, int amount, string why)> Drop = new List<(ItemDrop.ItemData, int, string)>();
        /// <summary>Why it acts or not: the "carry: …" line.</summary>
        public string Reason = "";
    }

    /// <summary>
    /// What a body carries and what it puts away, one rule (Fire, 2026-09-29: "put things away in a chest when it has too much
    /// weight or drops things on the ground that it doesn't need"; BASE_BUILDING.md §7). Always kept: equipped items, quest items,
    /// ammo for a carried bow or crossbow, weapons / armour / tools / torches (the keep types), food, and up to keep[prefab] of
    /// anything the caller still needs (the planner's counts for the run). At <see cref="StoreAt"/> of max carry
    /// (<see cref="TripAheadAt"/> before a trip, or the caller's storeAt, 0 on a base visit), always when encumbered: junk (no
    /// known recipe uses it, no trade value, not a trophy) is dropped when the body may drop, the rest above keep goes to the
    /// chest SmartStorageOrganizer picks among the chests within reach. No chest: a body that may drop sheds the least valuable
    /// per weight once over <see cref="DropAt"/>, down to <see cref="DropTo"/>, and no further.
    /// </summary>
    public static class CarryRules
    {
        public const float StoreAt = 0.80f, TripAheadAt = 0.40f, DropAt = 0.90f, DropTo = 0.85f;

        private static readonly HashSet<ItemDrop.ItemData.ItemType> KeepTypes = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.OneHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Shield,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Utility,
            ItemDrop.ItemData.ItemType.Tool,
            ItemDrop.ItemData.ItemType.Torch
        };

        /// <summary>Whether the whole stack stays with the body whatever the counts (never stored, never dropped), and why.</summary>
        public static bool AlwaysKeep(Inventory inventory, ItemDrop.ItemData item, out string why)
        {
            why = null;
            if (item?.m_shared == null) { why = "no item"; return true; }
            if (item.m_equipped) { why = "equipped"; return true; }
            if (item.m_shared.m_questItem) { why = "quest item"; return true; }
            if (IsAmmoForCarriedWeapon(inventory, item)) { why = "ammo for a carried bow/crossbow"; return true; }
            var type = item.m_shared.m_itemType;
            if (type == ItemDrop.ItemData.ItemType.Consumable) { why = "food"; return true; }
            if (KeepTypes.Contains(type)) { why = $"keep type {type}"; return true; }
            return false;
        }

        /// <summary>
        /// Junk: nothing known uses it (no recipe), it has no trade value and it isn't a trophy. Dropped rather than stored.
        /// </summary>
        public static bool IsJunk(ItemDrop.ItemData item) =>
            item?.m_shared != null && item.m_shared.m_value <= 0 && item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Trophy
            && UsesKnown && RecipeUses(item.m_shared.m_name) == 0;

        // The use counts can only say "unused" once both ObjectDB and ZNetScene are up (else nothing is junk).
        private static bool UsesKnown => ObjectDB.instance != null && ZNetScene.instance != null;

        /// <summary>
        /// The plan for <paramref name="body"/>'s load. <paramref name="maxWeight"/> &lt;= 0 reads the body's own (a player's max carry
        /// weight). <paramref name="keep"/>: prefab name → how many to keep carrying (the planner's needs; null = none).
        /// <paramref name="ownerId"/>: the player whose chests and wards count (the bot: its own id; a companion: its owner).
        /// <paramref name="allowDrop"/>: may drop (companions carry for the player: false). <paramref name="tripAhead"/>: before a
        /// dungeon / hunt / voyage, act from <see cref="TripAheadAt"/>. <paramref name="storeAt"/> &gt;= 0 overrides the threshold
        /// share (0 = a base visit: put away everything above keep).
        /// </summary>
        public static CarryPlan Plan(Humanoid body, Inventory inventory, float maxWeight, IReadOnlyDictionary<string, int> keep,
            long ownerId, bool allowDrop = true, bool tripAhead = false, float storeAt = -1f, float chestRadius = -1f)
        {
            var plan = new CarryPlan();
            if (body == null || inventory == null) { plan.Reason = "carry: no body"; return plan; }
            Player player = body as Player;
            plan.Max = maxWeight > 0f ? maxWeight : player != null ? player.GetMaxCarryWeight() : 300f;
            plan.Weight = inventory.GetTotalWeight();
            float at = storeAt >= 0f ? storeAt : tripAhead ? TripAheadAt : StoreAt;
            plan.Threshold = plan.Max * at;
            bool encumbered = player != null ? player.IsEncumbered() : plan.Weight > plan.Max;
            float share = plan.Max > 0f ? plan.Weight / plan.Max : 0f;
            string load = $"{plan.Weight:0} of {plan.Max:0} ({share * 100f:0}%)";
            if (!encumbered && plan.Weight < plan.Threshold && storeAt != 0f)
            {
                plan.Reason = $"carry: {load} under the {(tripAhead ? "pre-trip " : "")}threshold {plan.Threshold:0}";
                return plan;
            }

            // What is loose: each stack not always kept, less what keep still holds back (the first stacks of a prefab fill keep).
            s_loose.Clear();
            s_held.Clear();
            foreach (var item in inventory.GetAllItems())
            {
                if (AlwaysKeep(inventory, item, out _)) continue;
                string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
                int wanted = keep != null && keep.TryGetValue(prefab, out int k) ? k : 0;
                s_held.TryGetValue(prefab, out int held);
                int hold = Mathf.Clamp(wanted - held, 0, item.m_stack);
                s_held[prefab] = held + hold;
                if (item.m_stack - hold > 0) s_loose.Add((item, item.m_stack - hold));
            }
            if (s_loose.Count == 0)
            {
                plan.Reason = $"carry: {load}, but everything carried is kept";
                return plan;
            }

            // Junk goes on the ground (a body that may drop), whatever the chests.
            var dropped = new System.Text.StringBuilder();
            if (allowDrop)
            {
                for (int i = s_loose.Count - 1; i >= 0; i--)
                {
                    var (item, amount) = s_loose[i];
                    if (!IsJunk(item)) continue;
                    plan.Drop.Add((item, amount, "junk: no recipe uses it, no value"));
                    Append(dropped, $"junk {Utils.GetPrefabName(item.m_dropPrefab)} x{amount}");
                    s_loose.RemoveAt(i);
                }
            }

            Vector3 from = body.transform.position;
            float radius = chestRadius > 0f ? chestRadius : ChestHelper.DEFAULT_SEARCH_RADIUS;
            s_loose.Sort((a, b) => (b.item.m_shared.m_weight * b.amount).CompareTo(a.item.m_shared.m_weight * a.amount));
            plan.Chest = s_loose.Count > 0 ? PickChest(s_loose, from, radius, ownerId) : null;
            if (plan.Chest != null)
            {
                plan.Store.AddRange(s_loose);
                float moved = 0f;
                foreach (var (item, amount) in s_loose) moved += item.m_shared.m_weight * amount;
                Vector3 c = plan.Chest.transform.position;
                plan.Act = true;
                plan.Reason = $"carry: {load} -> store {plan.Store.Count} stack(s) in {Utils.GetPrefabName(plan.Chest.gameObject)} at ({c.x:0}, {c.y:0}, {c.z:0}) (weight {plan.Weight:0} -> {plan.Weight - moved:0})"
                              + (dropped.Length > 0 ? $"; drop {dropped}" : "");
                Log(body, plan.Reason);
                return plan;
            }

            // No chest: over DropAt (or encumbered), shed the least valuable per weight down to DropTo, no further.
            float left = plan.Weight;
            foreach (var d in plan.Drop) left -= d.item.m_shared.m_weight * d.amount;
            if (allowDrop && s_loose.Count > 0 && (encumbered || left >= plan.Max * DropAt))
            {
                float over = left - Mathf.Min(plan.Max * DropTo, plan.Max - 0.01f);
                s_loose.Sort((a, b) => ValuePerWeight(a.item).CompareTo(ValuePerWeight(b.item)));
                foreach (var (item, amount) in s_loose)
                {
                    if (over <= 0f) break;
                    float unit = item.m_shared.m_weight;
                    if (unit <= 0f) continue;
                    int n = Mathf.Min(amount, Mathf.CeilToInt(over / unit));
                    if (n <= 0) continue;
                    float vpw = ValuePerWeight(item);
                    plan.Drop.Add((item, n, $"overweight, no chest; value/weight {vpw:0.00}"));
                    Append(dropped, $"{Utils.GetPrefabName(item.m_dropPrefab)} x{n} (value/weight {vpw:0.00})");
                    over -= n * unit;
                    left -= n * unit;
                }
            }
            plan.Act = plan.Drop.Count > 0;
            plan.Reason = plan.Act
                ? $"carry: no chest within {radius:0} m -> drop {dropped} (weight {plan.Weight:0} -> {left:0} of {plan.Max:0})"
                : $"carry: {load}, no chest within {radius:0} m" + (allowDrop ? $", under {DropAt * 100f:0}%: nothing dropped" : "; this body doesn't drop");
            if (plan.Act) Log(body, plan.Reason);
            return plan;
        }

        /// <summary>
        /// Whether to pick up <paramref name="amount"/> of <paramref name="item"/> (Fire, R74: "understand inventory management and
        /// weight management"). Fits under the max carry weight: yes. Over it: only an item the caller still needs (in
        /// <paramref name="keep"/>), and then <paramref name="makeRoom"/> says to store or drop first (<see cref="Plan"/> with
        /// storeAt 0); anything else is left lying. <paramref name="maxWeight"/> &lt;= 0 reads a player's own.
        /// </summary>
        public static bool CanTake(Humanoid body, Inventory inventory, ItemDrop.ItemData item, int amount,
            IReadOnlyDictionary<string, int> keep, float maxWeight, out bool makeRoom, out string why)
        {
            makeRoom = false;
            why = null;
            if (body == null || inventory == null || item?.m_shared == null) { why = "no body or item"; return false; }
            float max = maxWeight > 0f ? maxWeight : body is Player player ? player.GetMaxCarryWeight() : 300f;
            float weight = inventory.GetTotalWeight();
            float adds = item.m_shared.m_weight * Mathf.Max(1, amount);
            if (weight + adds <= max)
            {
                why = $"fits ({weight + adds:0} of {max:0})";
                return true;
            }
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
            if (keep != null && keep.TryGetValue(prefab, out int wanted) && wanted > 0)
            {
                makeRoom = true;
                why = $"needed ({wanted}), but {weight + adds:0} of {max:0} would be over: store or drop first";
                return true;
            }
            why = $"not needed and {weight + adds:0} of {max:0} would be over the limit: leave it";
            return false;
        }

        /// <summary>
        /// Worth per kilogram, for what to drop first: vanilla's trade value (most materials have none) + 1 + how many known
        /// recipes use it (wood and stone go before iron), over its unit weight.
        /// </summary>
        public static float ValuePerWeight(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return float.MaxValue;
            float weight = Mathf.Max(0.01f, item.m_shared.m_weight);
            return (item.m_shared.m_value + 1f + RecipeUses(item.m_shared.m_name)) / weight;
        }

        private static void Append(System.Text.StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(part);
        }

        // The chest SmartStorageOrganizer recommends for the heaviest loose stack, among the chests within reach the owner may
        // write to; the nearest writable one when it recommends none.
        private static Container PickChest(List<(ItemDrop.ItemData item, int amount)> loose, Vector3 at, float radius, long ownerId)
        {
            s_chests.Clear();
            foreach (var chest in ChestHelper.FindNearbyChests(at, radius))
                if (chest != null && ChestHelper.OwnerMayWrite(chest, ownerId)) s_chests.Add(chest);
            if (s_chests.Count == 0) return null;
            foreach (var (item, _) in loose)
            {
                var pick = SmartStorageOrganizer.FindBestChestForItem(item, s_chests, at);
                if (pick != null && pick.Chest != null) return pick.Chest;
            }
            Container nearest = null;
            float best = float.MaxValue;
            foreach (var chest in s_chests)
            {
                float d = Vector3.Distance(at, chest.transform.position);
                if (d < best) { best = d; nearest = chest; }
            }
            return nearest;
        }

        private static bool IsAmmoForCarriedWeapon(Inventory inventory, ItemDrop.ItemData item)
        {
            var type = item.m_shared.m_itemType;
            if (type != ItemDrop.ItemData.ItemType.Ammo && type != ItemDrop.ItemData.ItemType.AmmoNonEquipable) return false;
            string ammo = item.m_shared.m_ammoType;
            if (string.IsNullOrEmpty(ammo) || inventory == null) return false;
            foreach (var other in inventory.GetAllItems())
                if (other?.m_shared != null && other.IsWeapon() && other.m_shared.m_ammoType == ammo) return true;
            return false;
        }

        // How many known uses an item has (by its shared name): item recipes, build pieces' resources, and the inputs of smelters,
        // kilns, cooking stations and fermenters (Stone and ores have no item recipe). Counted once per ObjectDB + ZNetScene.
        private static Dictionary<string, int> s_recipeUses;
        private static ObjectDB s_usesFrom;
        private static ZNetScene s_usesScene;

        private static int RecipeUses(string sharedName)
        {
            ObjectDB db = ObjectDB.instance;
            ZNetScene scene = ZNetScene.instance;
            if (db == null || sharedName == null) return 0;
            if (s_recipeUses == null || s_usesFrom != db || s_usesScene != scene)
            {
                s_usesFrom = db;
                s_usesScene = scene;
                s_recipeUses = new Dictionary<string, int>();
                foreach (Recipe recipe in db.m_recipes)
                    if (recipe != null) CountRequirements(recipe.m_resources);
                if (scene != null)
                {
                    foreach (GameObject prefab in scene.m_prefabs)
                    {
                        if (prefab == null) continue;
                        Piece piece = prefab.GetComponent<Piece>();
                        if (piece != null) CountRequirements(piece.m_resources);
                        Smelter smelter = prefab.GetComponent<Smelter>();
                        if (smelter != null)
                        {
                            if (smelter.m_conversion != null)
                                foreach (var c in smelter.m_conversion) CountItem(c?.m_from);
                            CountItem(smelter.m_fuelItem);
                        }
                        CookingStation cooking = prefab.GetComponent<CookingStation>();
                        if (cooking != null && cooking.m_conversion != null)
                            foreach (var c in cooking.m_conversion) CountItem(c?.m_from);
                        Fermenter fermenter = prefab.GetComponent<Fermenter>();
                        if (fermenter != null && fermenter.m_conversion != null)
                            foreach (var c in fermenter.m_conversion) CountItem(c?.m_from);
                    }
                }
            }
            return s_recipeUses.TryGetValue(sharedName, out int uses) ? uses : 0;
        }

        private static void CountRequirements(Piece.Requirement[] requirements)
        {
            if (requirements == null) return;
            foreach (var req in requirements) CountItem(req?.m_resItem);
        }

        private static void CountItem(ItemDrop item)
        {
            string name = item != null ? item.m_itemData?.m_shared?.m_name : null;
            if (name == null) return;
            s_recipeUses.TryGetValue(name, out int n);
            s_recipeUses[name] = n + 1;
        }

        // The same line from the same body at most every LogRepeat s (a caller may ask every tick).
        private const float LogRepeat = 10f;
        private static readonly Dictionary<Humanoid, (string line, float at)> s_logged = new Dictionary<Humanoid, (string, float)>();

        private static void Log(Humanoid body, string line)
        {
            if (s_logged.TryGetValue(body, out var last) && last.line == line && Time.time - last.at < LogRepeat) return;
            if (s_logged.Count > 64) s_logged.Clear();
            s_logged[body] = (line, Time.time);
            Debug.Log($"[CarryRules] {body.m_name}: {line}");
        }

        private static readonly List<(ItemDrop.ItemData item, int amount)> s_loose = new List<(ItemDrop.ItemData, int)>();
        private static readonly Dictionary<string, int> s_held = new Dictionary<string, int>();
        private static readonly List<Container> s_chests = new List<Container>();
    }
}
