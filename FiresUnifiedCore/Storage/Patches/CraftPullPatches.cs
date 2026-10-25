using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FiresCore.Storage.Patches
{
    /// <summary>
    /// Craft from containers. Applied only while Storage Features is on (this namespace is
    /// dormant for Core's loader; StorageFeatures.Enable patches it with its own Harmony instance).
    ///   counts   Player.HaveRequirementItems (items only - vanilla's station check is untouched): a missing material counts as present
    ///            when the containers within Range of the current station hold the rest
    ///   craft    InventoryGui.DoCrafting: when the player's own bag is short, the shortfall is TAKEN from those containers first (owner-
    ///            applied ops, ContainerOps), nearest first, then vanilla's DoCrafting runs on the now-full bag. A short result (a race,
    ///            a refusal) leaves what arrived in the bag and cancels this craft - nothing duplicated, nothing lost.
    ///   ui       InventoryGui.SetupRequirement: "(+N)" after a requirement's amount when the containers hold N of it
    ///   registry Container.Awake / OnDestroyed keep ContainerRegistry and the per-container RPC route current
    /// </summary>
    internal static class CraftPull
    {
        internal static bool On => StorageFeatures.Feature(StorageFeatures.Part.CraftFromContainers)
            && StorageConfig.CraftFromContainers != null && StorageConfig.CraftFromContainers.Value;

        internal static Vector3 Centre(Player p)
        {
            CraftingStation s = p != null ? p.GetCurrentCraftingStation() : null;
            return s != null ? s.transform.position : (p != null ? p.transform.position : Vector3.zero);
        }

        private const float DefaultRangeMeters = 20f;
        internal static float Range => StorageConfig.Range != null ? StorageConfig.Range.Value : DefaultRangeMeters;

        internal struct Need { public string Shared, Prefab; public int Missing; }

        /// <summary>Vanilla 1.0.15's own filter from
        /// Player.HaveRequirementItems - an upgrader resource only counts at an upgrader station, never with no station, and an
        /// ordinary resource never at an upgrader station.</summary>
        internal static bool Applies(Piece.Requirement req, CraftingStation station)
        {
            if (req == null || req.m_resItem == null) return false;
            if (station != null && station.m_upgrader != req.m_upgraderResource) return false;
            if (station == null && req.m_upgraderResource) return false;
            return true;
        }

        /// <summary>The materials the player's own inventory lacks for this recipe; false when the containers can't cover them all.</summary>
        internal static bool Shortfall(Player p, Recipe recipe, int quality, int multiplier, List<Need> needs)
        {
            needs.Clear();
            if (p == null || recipe == null || recipe.m_resources == null) return false;
            Vector3 centre = Centre(p);
            CraftingStation station = p.GetCurrentCraftingStation();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (!Applies(req, station)) continue;
                int want = req.GetAmount(quality) * Mathf.Max(1, multiplier);
                if (want <= 0) continue;
                string shared = req.m_resItem.m_itemData.m_shared.m_name;
                int have = p.GetInventory().CountItems(shared, -1, false);
                if (have >= want) continue;
                string prefab = req.m_resItem.gameObject.name;
                int lend = ContainerRegistry.CountInContainers(shared, prefab, centre, Range, p);
                if (have + lend < want) return false;
                needs.Add(new Need { Shared = shared, Prefab = prefab, Missing = want - have });
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class ContainerAwakeRegistry
    {
        private static void Postfix(Container __instance)
        {
            ContainerRegistry.Add(__instance); ContainerOps.RegisterOn(__instance); HopperChest.TryAttach(__instance);
            // A tagged chest loading on its owner relabels its signs once (only the owner, so peers don't all send)
            ZNetView nv = ContainerRegistry.NView(__instance);
            if (nv != null && nv.IsValid() && nv.IsOwner() && ChestTags.TagsOf(__instance).Count > 0) SignLabels.Refresh(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), "OnDestroyed")]
    internal static class ContainerDestroyedRegistry
    {
        private static void Prefix(Container __instance) => ContainerRegistry.Remove(__instance);
    }

    [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
    internal static class HaveRequirementItemsFromContainers
    {
        private static readonly List<CraftPull.Need> s_needs = new List<CraftPull.Need>();

        private static void Postfix(Player __instance, Recipe piece, bool discover, int qualityLevel, int amount, ref bool __result)
        {
            if (__instance == Player.m_localPlayer && !discover) StationLine(__instance);
            if (__result || discover || __instance != Player.m_localPlayer) return;
            // Containers stay out of it
            bool on = CraftPull.On, nocost = __instance.NoCostCheat();
            if (!on || nocost) { Trace(piece, $"skipped (On {on}, nocost {nocost})"); return; }
            try
            {
                bool covered = CraftPull.Shortfall(__instance, piece, qualityLevel, amount, s_needs);
                if (covered) __result = true;
                Trace(piece, $"shortfall covered {covered}; {Detail(__instance, piece, qualityLevel, amount)}");
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Storage] requirement check skipped: {e.Message}"); }
        }

        // ONE Info line each time the player
        // starts using a different station: the feature state, the station, and how many containers lend vs why the others are dropped
        // (out of range / not loaded / private / ward / owner not reachable). Info level, so it shows whatever the log level.
        private static int s_lastStation;
        private static void StationLine(Player p)
        {
            try
            {
                CraftingStation st = p.GetCurrentCraftingStation();
                int id = st != null ? st.GetInstanceID() : 0;
                if (id == s_lastStation) return;
                s_lastStation = id;
                string name = st != null ? $"{st.m_name} ({Utils.GetPrefabName(st.gameObject)})" : "no station (hand crafting)";
                if (!CraftPull.On) { FiresLogger.LogInfo($"[Storage] craft from containers OFF at {name} (Storage Features {StorageFeatures.Feature(StorageFeatures.Part.CraftFromContainers)}, Craft From Containers {StorageConfig.CraftFromContainers?.Value})"); return; }
                int lend = ContainerRegistry.Lendable(CraftPull.Centre(p), CraftPull.Range, p).Count;
                FiresLogger.LogInfo($"[Storage] craft from containers at {name}: {lend} container(s) lend within {CraftPull.Range:0} m of the {(st != null ? "station" : "player")}; dropped: {ContainerRegistry.SkipRange} out of range, {ContainerRegistry.SkipInvalid} not loaded, {ContainerRegistry.SkipAccess} private, {ContainerRegistry.SkipWard} ward, {ContainerRegistry.SkipOwner} owner not reachable; {ContainerRegistry.Registered} registered in total");
            }
            catch (Exception e) { FiresLogger.LogInfo($"[Storage] craft-from-containers station line failed: {e.Message}"); }
        }

        //
        // one Debug line per recipe at most every 5 s, saying why the containers did or didn't cover it.
        private const float TraceIntervalSeconds = 5f;
        private static readonly Dictionary<string, float> s_lastTrace = new Dictionary<string, float>();

        private static void Trace(Recipe piece, string what)
        {
            string key = piece != null ? piece.name : "?";
            if (s_lastTrace.TryGetValue(key, out float t) && Time.time - t < TraceIntervalSeconds) return;
            s_lastTrace[key] = Time.time;
            FiresLogger.LogDebug($"[Storage] reqs {key}: vanilla False, {what}");
        }

        private static string Detail(Player p, Recipe recipe, int quality, int amount)
        {
            if (recipe?.m_resources == null) return "no resources";
            var parts = new List<string>();
            Vector3 centre = CraftPull.Centre(p);
            CraftingStation station = p.GetCurrentCraftingStation();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (!CraftPull.Applies(req, station)) continue;
                int want = req.GetAmount(quality) * Mathf.Max(1, amount);
                if (want <= 0) continue;
                string shared = req.m_resItem.m_itemData.m_shared.m_name;
                int have = p.GetInventory().CountItems(shared, -1, false);
                int lend = ContainerRegistry.CountInContainers(shared, req.m_resItem.gameObject.name, centre, CraftPull.Range, p);
                parts.Add($"{req.m_resItem.gameObject.name} want {want} have {have} lend {lend}");
            }
            return $"centre {(p.GetCurrentCraftingStation() != null ? "station" : "player")} range {CraftPull.Range:0}; " + string.Join(", ", parts.ToArray());
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class DoCraftingFromContainers
    {
        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> UpgradeItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");
        private static readonly AccessTools.FieldRef<InventoryGui, float> CraftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");
        private static readonly System.Action<InventoryGui, Player> VanillaDoCrafting =
            AccessTools.MethodDelegate<System.Action<InventoryGui, Player>>(AccessTools.Method(typeof(InventoryGui), "DoCrafting"));

        private static bool s_bypass, s_inFlight;
        private static readonly List<CraftPull.Need> s_needs = new List<CraftPull.Need>();

        private static bool Prefix(InventoryGui __instance, Player player)
        {
            if (s_bypass || !CraftPull.On || player != Player.m_localPlayer || player.NoCostCheat()) return true;   // Nocost = vanilla's free craft, nothing pulled
            if (s_inFlight) return false;   // vanilla calls again while our takes are in flight: wait for them
            Recipe recipe = CraftRecipe(__instance);
            if (recipe == null) return true;
            ItemDrop.ItemData upgrade = UpgradeItem(__instance);
            int quality = upgrade != null ? upgrade.m_quality + 1 : 1;
            int multiplier = Mathf.Max(1, __instance.m_multiCraftAmount);
            if (!CraftPull.Shortfall(player, recipe, quality, multiplier, s_needs) || s_needs.Count == 0) return true;   // vanilla decides
            __instance.StartCoroutine(PullThenCraft(__instance, player, new List<CraftPull.Need>(s_needs)));
            return false;
        }

        private static IEnumerator PullThenCraft(InventoryGui gui, Player player, List<CraftPull.Need> needs)
        {
            s_inFlight = true;
            bool complete = true;
            try
            {
                foreach (CraftPull.Need n in needs)
                {
                    int left = n.Missing;
                    foreach (Container c in new List<Container>(ContainerRegistry.Lendable(CraftPull.Centre(player), CraftPull.Range, player, "craft from containers")))
                    {
                        if (left <= 0) break;
                        int held = ContainerRegistry.Inv(c)?.CountItems(n.Shared, -1, false) ?? 0;
                        if (held <= 0) continue;
                        int got = 0;
                        yield return ContainerOps.Take(c, player, n.Shared, n.Prefab, Mathf.Min(left, held), g => got = g);
                        left -= got;
                    }
                    if (left > 0) { complete = false; break; }
                }
            }
            finally { s_inFlight = false; }
            if (player == null || gui == null) yield break;
            if (complete)
            {
                s_bypass = true;
                try { VanillaDoCrafting(gui, player); }
                finally { s_bypass = false; }
            }
            else
            {
                // a race or a refusal: what arrived stays in the bag, this craft is cancelled cleanly
                CraftRecipe(gui) = null;
                CraftTimer(gui) = -1f;
                player.Message(MessageHud.MessageType.Center, "Not enough in nearby containers any more");
                FiresLogger.LogDebug("[Storage] craft cancelled: the containers no longer held enough");
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "SetupRequirement")]
    internal static class SetupRequirementCounts
    {
        private static void Postfix(Transform elementRoot, Piece.Requirement req, Player player, bool craft)
        {
            if (!craft || req?.m_resItem == null || player != Player.m_localPlayer || !CraftPull.On || elementRoot == null) return;
            int n = ContainerRegistry.CountInContainers(req.m_resItem.m_itemData.m_shared.m_name, req.m_resItem.gameObject.name,
                CraftPull.Centre(player), CraftPull.Range, player);
            if (n <= 0) return;
            var text = elementRoot.Find("res_amount")?.GetComponent<TMP_Text>();
            if (text != null && !text.text.EndsWith(")")) text.text += $" (+{n})";
        }
    }
}

namespace FiresCore.Storage.Patches
{
    // New drops notify hoppers (event-driven), and the Hopper Chest returns to the Hammer whenever ObjectDB reloads.
    [HarmonyLib.HarmonyPatch(typeof(ItemDrop), "Awake")]
    internal static class ItemDropAwakeHopper
    {
        private static void Postfix(ItemDrop __instance) => HopperChest.OnItemSpawned(__instance);
    }

    [HarmonyLib.HarmonyPatch(typeof(ObjectDB), "Awake")]
    internal static class ObjectDBAwakeHopperPiece
    {
        private static void Postfix() => HopperChest.EnsurePiece();
    }
}
namespace FiresCore.Storage.Patches
{
    // A placed / loaded sign gets the label route, and a NEW sign next to a tagged chest is labelled.
    [HarmonyLib.HarmonyPatch(typeof(Sign), "Awake")]
    internal static class SignAwakeLabels
    {
        private static void Postfix(Sign __instance) => SignLabels.OnSignAwake(__instance);
    }
}
namespace FiresCore.Storage
{
    /// <summary>
    /// FiresLootOverhaul's enchant / socket costs count and take from the bag directly, so craft-from-containers never
    /// ran for them. This is the same lending crafting uses, for another mod's own costs. Contract, ADD-ONLY, called by reflection.
    /// Both honour Storage Features + Craft From Containers, the Range around the current station (or the player), the item
    /// blacklist and every container filter (private, ward, ship / cart, owner reachable). REUSED from CraftPull / PullThenCraft.
    /// </summary>
    public static class CraftPullApi
    {
        /// <summary>How many of the item the nearby lendable containers hold right now (0 when the feature is off). Not the bag.</summary>
        public static int CountNearby(Player p, string sharedName, string prefabName)
        {
            if (p == null || !Patches.CraftPull.On) return 0;
            return ContainerRegistry.CountInContainers(sharedName, prefabName, Patches.CraftPull.Centre(p), Patches.CraftPull.Range, p);
        }

        /// <summary>Moves <paramref name="amount"/> of the item from nearby containers INTO the player's inventory (owner-applied, the
        /// crafting pull's own ContainerOps.Take), nearest container first. Yield on it; <paramref name="done"/> gets true only when all of
        /// it arrived (what did arrive stays in the bag either way). The caller then takes its cost from the bag as usual.</summary>
        public static IEnumerator TakeNearby(Player p, string sharedName, string prefabName, int amount, Action<bool> done)
        {
            int left = amount;
            if (p != null && amount > 0 && Patches.CraftPull.On)
            {
                foreach (Container c in new List<Container>(ContainerRegistry.Lendable(Patches.CraftPull.Centre(p), Patches.CraftPull.Range, p, "craft from containers")))
                {
                    if (left <= 0) break;
                    int held = ContainerRegistry.Inv(c)?.CountItems(sharedName, -1, false) ?? 0;
                    if (held <= 0) continue;
                    int got = 0;
                    yield return ContainerOps.Take(c, p, sharedName, prefabName, Mathf.Min(left, held), g => got = g);
                    left -= got;
                }
            }
            FiresLogger.LogDebug($"[Storage] CraftPullApi.TakeNearby {prefabName} x{amount}: {(left <= 0 ? "all taken" : left + " short")}");
            try { done?.Invoke(left <= 0); } catch (Exception e) { FiresLogger.LogWarning("[Storage] CraftPullApi done callback threw " + e.Message); }
        }

        // The same two calls centred on
        // a given station (a bench with its own window is not the player's current crafting station). station null = around the player.
        private static Vector3 At(Player p, CraftingStation station) => station != null ? station.transform.position : (p != null ? p.transform.position : Vector3.zero);

        /// <summary>CountNearby around <paramref name="station"/> (null = the player). 0 when the feature is off. Not the bag.</summary>
        public static int CountNearby(Player p, CraftingStation station, string sharedName, string prefabName)
        {
            if (p == null || !Patches.CraftPull.On) return 0;
            return ContainerRegistry.CountInContainers(sharedName, prefabName, At(p, station), Patches.CraftPull.Range, p);
        }

        /// <summary>TakeNearby around <paramref name="station"/>; <paramref name="done"/> gets the amount that actually arrived in the bag.</summary>
        public static IEnumerator TakeNearby(Player p, CraftingStation station, string sharedName, string prefabName, int amount, Action<int> done)
        {
            int left = amount;
            if (p != null && amount > 0 && Patches.CraftPull.On)
            {
                foreach (Container c in new List<Container>(ContainerRegistry.Lendable(At(p, station), Patches.CraftPull.Range, p, "craft from containers")))
                {
                    if (left <= 0) break;
                    int held = ContainerRegistry.Inv(c)?.CountItems(sharedName, -1, false) ?? 0;
                    if (held <= 0) continue;
                    int got = 0;
                    yield return ContainerOps.Take(c, p, sharedName, prefabName, Mathf.Min(left, held), g => got = g);
                    left -= got;
                }
            }
            int moved = Mathf.Max(0, amount - Mathf.Max(0, left));
            FiresLogger.LogDebug($"[Storage] CraftPullApi.TakeNearby {prefabName} x{amount} at {(station != null ? station.name : "the player")}: {moved} moved");
            try { done?.Invoke(moved); } catch (Exception e) { FiresLogger.LogWarning("[Storage] CraftPullApi done callback threw " + e.Message); }
        }
    }
}