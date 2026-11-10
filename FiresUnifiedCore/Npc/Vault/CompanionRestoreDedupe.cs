using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// 0.2.984 (Fire 10-29: "yes we now have duplicate companions" after the 0.2.983 relog restore; [ghost]: the dormant pass only knew
    /// LOADED companions, so on the dedicated server every logged-out follower whose world ZDO was adopted but not loaded spawned again).
    /// Server side, ZDO level:
    ///  - <see cref="LiveIds"/>: every companion id with a non-defeated world ZDO of this player (loaded or not); the dormant pass
    ///    skips those ids and clears their kennel entry instead of spawning a second copy.
    ///  - <see cref="Dedupe"/>: two or more ZDOs with one id = keep the OLDEST (the adopted original, lowest ZDO id of the same creator,
    ///    else the one already loaded), merge the extra's bag into the kept one (items the kept one does not already hold, so a copy
    ///    of the same gear is not doubled), drop what does not fit at the owner, then destroy the extra's ZDO (never the Dismiss
    ///    path: that writes a Dismissed kennel entry under the shared id).
    /// [Companions] "Restore dedupe" (default on).
    /// </summary>
    internal static class CompanionRestoreDedupe
    {
        private const string LogPrefix = "[CompanionRestoreService]";
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default ON without it).
        internal static bool On => FiresCore.Config.CoreSettings.Get("Companions", "Restore dedupe", true);
        internal static void Bind(BepInEx.Configuration.ConfigFile cfg) { }

        private static Dictionary<string, List<ZDO>> ById(long playerId)
        {
            var byId = new Dictionary<string, List<ZDO>>();
            if (ZDOMan.instance == null) return byId;
            var temp = new List<ZDO>();
            foreach (var prefabName in Core.CompanionZdoCensus.PrefabNames)
            {
                temp.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, temp, ref idx)) { }
                foreach (var zdo in temp)
                {
                    if (zdo == null || !zdo.IsValid() || zdo.GetLong("companion_owner", 0L) != playerId) continue;
                    if (zdo.GetBool("companion_defeated", false)) continue;
                    string cid = zdo.GetString("companion_id", "");
                    if (string.IsNullOrEmpty(cid)) continue;
                    if (!byId.TryGetValue(cid, out var list)) byId[cid] = list = new List<ZDO>();
                    if (!list.Contains(zdo)) list.Add(zdo);
                }
            }
            return byId;
        }

        /// <summary>Ids of this player's companions that have a live (non-defeated) world ZDO, loaded or not.</summary>
        internal static HashSet<string> LiveIds(long playerId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in ById(playerId)) set.Add(kv.Key);
            return set;
        }

        private static bool Loaded(ZDO z) => ZNetScene.instance != null && ZNetScene.instance.FindInstance(z) != null;

        // the original: a loaded instance first (the one the player is using), else the lowest id of the earliest creator
        private static ZDO PickKeeper(List<ZDO> dups)
        {
            foreach (var z in dups) if (Loaded(z)) return z;
            ZDO best = dups[0];
            foreach (var z in dups)
                if (z.m_uid.UserID == best.m_uid.UserID ? z.m_uid.ID < best.m_uid.ID : z.m_uid.UserID < best.m_uid.UserID) best = z;
            return best;
        }

        private static Inventory LoadBag(ZDO z)
        {
            var bag = new Inventory("CompanionDedupe", null, CompanionGridLayout.Width, CompanionGridLayout.Height);
            string data = z.GetString("companion_inventory", "");
            if (!string.IsNullOrEmpty(data)) bag.Load(new ZPackage(data));
            return bag;
        }

        private static string Key(ItemDrop.ItemData it) => $"{it.m_dropPrefab?.name ?? it.m_shared?.m_name}|{it.m_quality}|{it.m_stack}|{it.m_variant}|{it.m_crafterID}";

        internal static int Dedupe(long playerId, Vector3 ownerPos)
        {
            if (!On || ZNet.instance == null || !ZNet.instance.IsServer()) return 0;
            int removed = 0;
            foreach (var kv in ById(playerId))
            {
                var dups = kv.Value;
                if (dups.Count < 2) continue;
                var keeper = PickKeeper(dups);
                string name = keeper.GetString("companion_name", kv.Key);
                foreach (var extra in dups)
                {
                    if (extra == keeper) continue;
                    int moved = 0, dropped = 0, same = 0;
                    try
                    {
                        var keepBag = LoadBag(keeper);
                        var extraBag = LoadBag(extra);
                        var have = new Dictionary<string, int>();
                        foreach (var it in keepBag.GetAllItems()) { string k = Key(it); have[k] = have.TryGetValue(k, out int n) ? n + 1 : 1; }
                        foreach (var it in new List<ItemDrop.ItemData>(extraBag.GetAllItems()))
                        {
                            string k = Key(it);
                            if (have.TryGetValue(k, out int n) && n > 0) { have[k] = n - 1; same++; continue; }   // the same gear: not doubled
                            var cell = CompanionGridLayout.FirstFreeStorageCell(keepBag);
                            var copy = it.Clone(); copy.m_equipped = false;
                            if (cell.x >= 0 && keepBag.AddItem(copy, cell)) { moved++; continue; }
                            if (ownerPos != Vector3.zero) { ItemDrop.DropItem(copy, copy.m_stack, ownerPos + Vector3.up, Quaternion.identity); dropped++; }
                        }
                        if (moved > 0)
                        {
                            var pkg = new ZPackage(); keepBag.Save(pkg);
                            keeper.SetOwner(ZDOMan.GetSessionID());
                            keeper.Set("companion_inventory", pkg.GetBase64());
                            keeper.DataRevision++;
                        }
                        extra.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.DestroyZDO(extra);
                        removed++;
                        FiresCore.Logging.FiresLogger.LogInfo($"{LogPrefix} duplicate {name} ({kv.Key}): kept {keeper.m_uid}, removed {extra.m_uid}, moved {moved} item(s) ({same} identical kept once, {dropped} dropped at the owner)");
                    }
                    catch (Exception ex) { Debug.LogWarning($"{LogPrefix} dedupe of {name} ({kv.Key}) failed: {ex.Message}"); }
                }
            }
            return removed;
        }
    }
}
