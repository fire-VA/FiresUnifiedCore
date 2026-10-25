using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Taking items out of a container
    /// another peer may own.
    ///   request  "Core_StorageTake" on the CONTAINER's own ZNetView (vanilla routes it to the ZDO owner the server knows):
    ///            ZPackage {opId, shared name, amount, requester playerID}
    ///   owner    applies it only if it IS the owner now (owner changes are common: every chest open moves ownership); checks the
    ///            requester's access (vanilla CheckAccess) and the item, removes min(amount, held) with vanilla Inventory.RemoveItem, and
    ///            writes "opId:granted" into the chest's ZDO ledger (core_sop, last 16) BEFORE replying - the ledger moves with ownership, so
    ///            a retry that reaches a new owner after an owner change is answered from the ledger, never removed twice
    ///   reply    "Core_StorageTakeReply" (global, registered on Game.Start on every peer) {opId, granted, status}
    ///   requester adds only what was granted (vanilla AddItem; full bag = dropped at the feet, never lost); 5 s timeout, then ONE retry
    ///            with the same opId; a second timeout gives up (the ledger makes a late double impossible).
    /// </summary>
    public static class ContainerOps
    {
        internal const string RpcTake = "Core_StorageTake", RpcReply = "Core_StorageTakeReply", RpcPut = "Core_StoragePut";
        private const string LedgerKey = "core_sop";
        private const int LedgerSize = 16;
        private const float Timeout = 5f;
        private const float PutGiveUpSeconds = 30f;
        private const float OwnerMovedRetryDelay = 0.25f;
        private const float LedgerSyncWaitSeconds = 2f;
        private const int TakeAttempts = 2;
        internal enum Status { Granted = 0, NotOwner = 1, NoAccess = 2, Off = 3 }

        private sealed class Pending { public int Granted = -1; public Status Status; public int Asked = int.MaxValue; }
        private static readonly Dictionary<long, Pending> s_pending = new Dictionary<long, Pending>();
        private static readonly HashSet<ZNetView> s_registered = new HashSet<ZNetView>();
        private static bool s_replyRegistered;
        private static readonly System.Random s_rng = new System.Random();

        // ---- registration ----

        /// <summary>Global reply route: on every peer, once per Game (Patches-independent: StorageRpcHooks calls it on Game.Start).</summary>
        internal static void RegisterReply()
        {
            if (s_replyRegistered || ZRoutedRpc.instance == null) return;
            s_replyRegistered = true;
            ZRoutedRpc.instance.Register<ZPackage>(RpcReply, OnReply);
        }

        internal static void ResetForNewGame() { s_replyRegistered = false; s_registered.Clear(); s_pending.Clear(); }

        /// <summary>The per-container request route (vanilla's ZNetView pattern), on every peer that loads the container.</summary>
        internal static void RegisterOn(Container c)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            if (nv == null || !nv.IsValid() || s_registered.Contains(nv)) return;
            s_registered.Add(nv);
            nv.Register<ZPackage>(RpcTake, (sender, pkg) => OnTake(c, nv, sender, pkg));
            nv.Register<ZPackage>(RpcPut, (sender, pkg) => OnPut(c, nv, sender, pkg));
            ChestTags.RegisterOn(c, nv);
        }

        // ---- owner side ----

        private static void OnTake(Container c, ZNetView nv, long sender, ZPackage pkg)
        {
            long opId = pkg.ReadLong(); string name = pkg.ReadString(); int amount = pkg.ReadInt(); pkg.ReadLong();   // the packet's player id is ignored
            if (!StorageFeatures.Active) { Reply(sender, opId, 0, Status.Off); return; }
            long requester = RequesterOf(sender, c, out string why);
            if (requester == 0L || amount <= 0) { FiresLogger.LogDebug($"[Storage] take op {opId} refused: {why ?? "amount " + amount}"); Reply(sender, opId, 0, Status.NoAccess); return; }
            if (c == null || nv == null || !nv.IsValid() || !nv.IsOwner()) { Reply(sender, opId, 0, Status.NotOwner); return; }
            ZDO zdo = nv.GetZDO();
            int already = LedgerGet(zdo, opId);
            if (already >= 0) { Reply(sender, opId, already, Status.Granted); return; }   // a retry: answered, never removed twice
            if (!ContainerRegistry.CheckAccessFn(c, requester))
            { Reply(sender, opId, 0, Status.NoAccess); return; }
            Inventory inv = ContainerRegistry.Inv(c);
            int held = inv != null ? inv.CountItems(name, -1, false) : 0;
            int granted = Mathf.Clamp(Mathf.Min(amount, held), 0, int.MaxValue);
            if (granted > 0) inv.RemoveItem(name, granted, -1, false);   // vanilla: the container's m_onChanged saves it (we own it)
            LedgerPut(zdo, opId, granted);
            FiresLogger.LogDebug($"[Storage] take op {opId}: {granted}/{amount} {name} from '{c.m_name}' for player {requester}");
            Reply(sender, opId, granted, Status.Granted);
        }

        // A PUT. The owner adds as many as fit (vanilla CanAddItem, binary search) and records the grant in the same ledger,
        // so a retry never adds twice. Only stackable items are put (no per-item data to lose).
        private static void OnPut(Container c, ZNetView nv, long sender, ZPackage pkg)
        {
            long opId = pkg.ReadLong(); string prefabName = pkg.ReadString(); int quality = pkg.ReadInt(); int amount = pkg.ReadInt(); pkg.ReadLong();   // the packet's player id is ignored
            if (!StorageFeatures.Active) { Reply(sender, opId, 0, Status.Off); return; }
            long requester = RequesterOf(sender, c, out string why);
            if (requester == 0L || amount <= 0) { FiresLogger.LogDebug($"[Storage] put op {opId} refused: {why ?? "amount " + amount}"); Reply(sender, opId, 0, Status.NoAccess); return; }
            if (c == null || nv == null || !nv.IsValid() || !nv.IsOwner()) { Reply(sender, opId, 0, Status.NotOwner); return; }
            ZDO zdo = nv.GetZDO();
            int already = LedgerGet(zdo, opId);
            if (already >= 0) { Reply(sender, opId, already, Status.Granted); return; }
            if (!ContainerRegistry.CheckAccessFn(c, requester)) { Reply(sender, opId, 0, Status.NoAccess); return; }
            Inventory inv = ContainerRegistry.Inv(c);
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            int fit = 0;
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            if (shared != null) { quality = Mathf.Clamp(quality, 1, Mathf.Max(1, shared.m_maxQuality)); amount = Mathf.Min(amount, Mathf.Max(1, shared.m_maxStackSize) * MaxPutStacks); }
            if (inv != null && prefab != null)
            {
                int lo = 0, hi = amount;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; if (inv.CanAddItem(prefab, mid)) lo = mid; else hi = mid - 1; }
                fit = lo;
                if (fit > 0) fit = AddSplit(inv, prefab, quality, fit, $"put op {opId} into '{c.m_name}'");   // the ledger counts only what landed
            }
            LedgerPut(zdo, opId, fit);
            FiresLogger.LogDebug($"[Storage] put op {opId}: {fit}/{amount} {prefabName} into '{c.m_name}' from player {requester}");
            Reply(sender, opId, fit, Status.Granted);
        }

        /// <summary>Adds <paramref name="amount"/> of an item to a chest inventory as stacks of at most the item's vanilla max stack (a chest
        /// never holds an over-size stack), and returns how many actually landed. One Debug line when the write is split.</summary>
        internal static int AddSplit(Inventory inv, GameObject prefab, int quality, int amount, string what)
        {
            if (inv == null || prefab == null || amount <= 0) return 0;
            var template = prefab.GetComponent<ItemDrop>()?.m_itemData;
            if (template == null) return 0;
            int max = Mathf.Max(1, template.m_shared.m_maxStackSize), added = 0, writes = 0;
            while (added < amount)
            {
                int n = Mathf.Min(max, amount - added);
                var stack = template.Clone(); stack.m_stack = n; stack.m_quality = quality; stack.m_dropPrefab = prefab;
                if (!inv.AddItem(stack)) break;
                added += n; writes++;
            }
            if (writes > 1 || added < amount) FiresLogger.LogDebug($"[Storage] {what}: {amount} {prefab.name} written as {writes} stack(s) of max {max}; {added} landed{(added < amount ? $", {amount - added} did not fit" : "")}");
            return added;
        }

        private const int MaxPutStacks = 64;          // one PUT never carries more than a bag's worth of stacks
        private const float RequesterReach = 64f;     // a chest is reached from its own base (terminal network radius + margin)

        // The requesting player is always the RPC's real sender (RoutedSenderGuard makes that id trustworthy), never an id the packet
        // names, and must stand near the chest. 0 = refused (why says so).
        private static long RequesterOf(long sender, Container c, out string why)
        {
            why = null;
            long id = FiresCore.Possession.PossessionServer.PlayerIdOf(sender, out Vector3 pos);
            if (id == 0L) { why = $"peer {sender} has no player"; return 0L; }
            if (c != null && Vector3.Distance(pos, c.transform.position) > RequesterReach) { why = $"player {id} is {Vector3.Distance(pos, c.transform.position):0} m from the chest"; return 0L; }
            return id;
        }

        private static void Reply(long peer, long opId, int granted, Status status)
        {
            var pkg = new ZPackage(); pkg.Write(opId); pkg.Write(granted); pkg.Write((int)status);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcReply, pkg);
        }

        private static int LedgerGet(ZDO zdo, long opId)
        {
            string key = opId.ToString() + ":";
            foreach (string e in (zdo.GetString(LedgerKey, "") ?? "").Split(';'))
                if (e.StartsWith(key, StringComparison.Ordinal) && int.TryParse(e.Substring(key.Length), out int g)) return g;
            return -1;
        }

        private static void LedgerPut(ZDO zdo, long opId, int granted)
        {
            var list = new List<string>((zdo.GetString(LedgerKey, "") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            list.Add(opId + ":" + granted);
            if (list.Count > LedgerSize) list.RemoveRange(0, list.Count - LedgerSize);
            zdo.Set(LedgerKey, string.Join(";", list.ToArray()));
        }

        // ---- requester side ----

        private static void OnReply(long sender, ZPackage pkg)
        {
            long opId = pkg.ReadLong(); int granted = pkg.ReadInt(); var status = (Status)pkg.ReadInt();
            if (s_pending.TryGetValue(opId, out var p) && p.Granted < 0) { p.Granted = Mathf.Clamp(granted, 0, p.Asked); p.Status = status; }   // never more than asked
        }

        /// <summary>A chest whose ZDO owner doesn't have it loaded - the dedicated server, or a peer far away -
        /// never answers, so only chests owned by this peer or by a peer whose player character is loaded here are worth asking.</summary>
        internal static bool OwnerReachable(Container c)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            if (nv == null || !nv.IsValid()) return false;
            long owner = nv.GetZDO().GetOwner();
            if (owner == 0L) return false;
            if (ZNet.instance != null && owner == ZNet.GetUID()) return true;
            foreach (Player p in Player.GetAllPlayers())
            {
                ZNetView pv = p != null ? p.GetComponent<ZNetView>() : null;
                if (pv != null && pv.IsValid() && pv.GetZDO().GetOwner() == owner) return true;
            }
            return false;
        }

        /// <summary>Puts <paramref name="amount"/> of a stackable item (already REMOVED from the player's bag by the caller)
        /// into <paramref name="c"/>. <paramref name="done"/> gets the amount the chest took; the caller gives the rest back. Retries to the
        /// current owner with the same opId for up to 30 s (the ledger makes a retry safe); after that the caller gives everything back
        /// and a Warning is logged (the one case that could double: an owner that applied it and never answered for 30 s).</summary>
        internal static IEnumerator Put(Container c, Player player, string prefabName, int quality, int amount, Action<int> done)
        {
            ZNetView nv = ContainerRegistry.NView(c);
            if (nv == null || !nv.IsValid() || amount <= 0) { done(0); yield break; }
            RegisterReply();
            long opId = ((long)s_rng.Next() << 31) ^ s_rng.Next() ^ DateTime.UtcNow.Ticks;
            var p = new Pending { Asked = amount };
            s_pending[opId] = p;
            float giveUp = Time.time + PutGiveUpSeconds;
            while (p.Granted < 0 && Time.time < giveUp)
            {
                var pkg = new ZPackage(); pkg.Write(opId); pkg.Write(prefabName); pkg.Write(quality); pkg.Write(amount); pkg.Write(player.GetPlayerID());
                nv.InvokeRPC(RpcPut, pkg);
                float until = Mathf.Min(giveUp, Time.time + Timeout);
                while (p.Granted < 0 && Time.time < until) yield return null;
                if (p.Granted >= 0 && p.Status == Status.NotOwner) { p.Granted = -1; yield return new WaitForSeconds(OwnerMovedRetryDelay); }
            }
            s_pending.Remove(opId);
            // No reply in 30 s - before giving the items back, read the chest's OWN ledger (core_sop is a ZDO
            // field, synced to every peer that has the chest loaded): if the owner did apply it, that grant stands and nothing doubles.
            if (p.Granted < 0 && nv != null && nv.IsValid())
            {
                float sync = Time.time + LedgerSyncWaitSeconds;
                int seen = LedgerGet(nv.GetZDO(), opId);
                while (seen < 0 && Time.time < sync) { yield return null; seen = LedgerGet(nv.GetZDO(), opId); }
                if (seen >= 0) { p.Granted = seen; p.Status = Status.Granted; FiresLogger.LogDebug($"[Storage] put op {opId}: reply lost, ledger shows {seen} applied"); }
            }
            if (p.Granted < 0) FiresLogger.LogWarning($"[Storage] put op {opId} ({amount} {prefabName} into '{c?.m_name}'): no answer in 30 s, items given back");
            done(p.Status == Status.Granted && p.Granted > 0 ? p.Granted : 0);
        }

        /// <summary>Takes up to <paramref name="amount"/> of an item (shared name) out of <paramref name="c"/> into the player's inventory.
        /// <paramref name="done"/> gets the amount actually added (0 on refusal/timeout).</summary>
        internal static IEnumerator Take(Container c, Player player, string sharedName, string prefabName, int amount, Action<int> done)
        {
            ZNetView nv = ContainerRegistry.NView(c);
            if (nv == null || !nv.IsValid() || amount <= 0) { done(0); yield break; }
            RegisterReply();   // lazily too, in case the feature came on after Game.Start
            long opId = ((long)s_rng.Next() << 31) ^ s_rng.Next() ^ DateTime.UtcNow.Ticks;
            var p = new Pending { Asked = amount };
            s_pending[opId] = p;
            for (int attempt = 0; attempt < TakeAttempts && p.Granted < 0; attempt++)
            {
                var pkg = new ZPackage(); pkg.Write(opId); pkg.Write(sharedName); pkg.Write(amount); pkg.Write(player.GetPlayerID());
                nv.InvokeRPC(RpcTake, pkg);
                float until = Time.time + Timeout;
                while (p.Granted < 0 && Time.time < until) yield return null;
                if (p.Granted >= 0 && p.Status == Status.NotOwner) { p.Granted = -1; yield return new WaitForSeconds(OwnerMovedRetryDelay); }   // owner moved: once more
            }
            s_pending.Remove(opId);
            // A lost take reply - the chest's synced ledger says whether the owner removed them; if it did, they are ours
            if (p.Granted < 0 && nv != null && nv.IsValid())
            {
                float sync = Time.time + LedgerSyncWaitSeconds;
                int seen = LedgerGet(nv.GetZDO(), opId);
                while (seen < 0 && Time.time < sync) { yield return null; seen = LedgerGet(nv.GetZDO(), opId); }
                if (seen >= 0) { p.Granted = seen; p.Status = Status.Granted; FiresLogger.LogDebug($"[Storage] take op {opId}: reply lost, ledger shows {seen} removed"); }
            }
            int got = p.Status == Status.Granted && p.Granted > 0 ? p.Granted : 0;
            if (got > 0 && player != null)
            {
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
                if (prefab != null && !player.GetInventory().AddItem(prefab, got))
                {
                    // the bag is full: the items are dropped at the player's feet, never lost
                    var drop = UnityEngine.Object.Instantiate(prefab, player.transform.position + Vector3.up, Quaternion.identity);
                    drop.GetComponent<ItemDrop>()?.SetStack(got);
                }
            }
            if (p.Granted < 0) FiresLogger.LogWarning($"[Storage] take op {opId} for {amount} {sharedName} from '{c?.m_name}': no reply after 2 tries");
            done(got);
        }
    }

    /// <summary>The RPC routes exist on every peer whatever the toggle says, so flipping it mid-session works (this
    /// patch lives OUTSIDE the dormant FiresCore.Storage.Patches namespace; Core's loader always applies it). The handlers check Active.</summary>
    [HarmonyPatch(typeof(Game), "Start")]
    internal static class StorageRpcHooks
    {
        private static void Postfix()
        {
            ContainerOps.ResetForNewGame();
            ContainerOps.RegisterReply();
        }
    }
}
