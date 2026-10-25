using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Remote access to a base's chests from far away (the bag upgrades' Remote Access, Send home and Refill). The SERVER is the authority:
    /// a client asks, the server checks the range against the anchor (a storage terminal), finds the chests around it in its own world data,
    /// and changes them. A chest that no player has loaded is edited directly in its saved data; a chest another player has loaded right now
    /// is left out (that player's game owns it), so two writers never touch one chest. Every take or deposit stays in a journal until the
    /// client confirms it; without a confirmation (a disconnect, a lost reply) the server undoes it, so items are never duplicated or lost.
    /// CONTRACT: Deposit / Withdraw / Totals are add-only.
    /// </summary>
    public static class RemoteStore
    {
        private const string RpcRequest = "Core_RemoteStoreRequest", RpcReply = "Core_RemoteStoreReply", RpcAck = "Core_RemoteStoreAck";
        private const byte KindTotals = 0, KindWithdraw = 1, KindDeposit = 2;
        private const float ClientTimeoutSeconds = 8f, AckTimeoutSeconds = 10f;
        private const string TrueStackKey = "VAInv.TrueStack";   // VAInventory's real count of a stack above the vanilla max

        private static ConfigEntry<float> s_maxRange, s_networkRadius;
        private static ZRoutedRpc s_registeredOn;
        private static readonly System.Random s_rng = new System.Random();

        internal static void Bind(ConfigFile cfg)
        {
            if (s_maxRange != null) return;
            s_maxRange = cfg.Bind("Storage", "Remote storage max range", 7500f, new ConfigDescription(
                "SERVER. The farthest (horizontal metres) a player may reach their base's chests remotely, whatever a client asks for.", new AcceptableValueRange<float>(0f, 20000f)));
            s_networkRadius = cfg.Bind("Storage", "Remote network radius", 30f, new ConfigDescription(
                "SERVER. Chests within this many metres of the remote anchor (the home storage terminal) belong to its network.", new AcceptableValueRange<float>(5f, 100f)));
        }

        private static float MaxRange => s_maxRange != null ? s_maxRange.Value : 7500f;
        private static float NetworkRadius => s_networkRadius != null ? s_networkRadius.Value : 30f;

        // ================= client side =================

        private sealed class ClientPending { public byte Kind; public float At; public Action<int> Done; public Action<Dictionary<string, int>> Totals; public Player Player; public List<ItemDrop.ItemData> Items; }
        private static readonly Dictionary<long, ClientPending> s_clientPending = new Dictionary<long, ClientPending>();

        /// <summary>Stores <paramref name="items"/> in the anchor's chests. The items leave the bag only after the server committed them;
        /// <paramref name="committed"/> gets the number of items stored (what didn't fit stays in the bag).</summary>
        public static void Deposit(Player p, ZDOID anchor, float maxRange, List<ItemDrop.ItemData> items, Action<int> committed)
            => Deposit(p, anchor, Vector3.zero, maxRange, items, committed);

        /// <summary>As Deposit; <paramref name="anchorPos"/> = where the anchor terminal stands, used by the server when the ZDOID is stale
        /// (ZDOIDs change across world loads). ADD-ONLY overload.</summary>
        public static void Deposit(Player p, ZDOID anchor, Vector3 anchorPos, float maxRange, List<ItemDrop.ItemData> items, Action<int> committed)
        {
            if (!Ready(p, committed) || items == null || items.Count == 0) { committed?.Invoke(0); return; }
            var pkg = Header(KindDeposit, anchor, anchorPos, maxRange, out long opId);
            var sent = new List<ItemDrop.ItemData>();
            foreach (var it in items) if (it?.m_dropPrefab != null && it.m_stack > 0) sent.Add(it);
            pkg.Write(sent.Count);
            foreach (var it in sent) { pkg.Write(it.m_dropPrefab.name); pkg.Write(it.m_stack); pkg.Write(it.m_quality); }
            Send(opId, pkg, new ClientPending { Kind = KindDeposit, Done = committed, Player = p, Items = sent });
        }

        /// <summary>Takes up to <paramref name="amount"/> of an item (by prefab name) from the anchor's chests into the bag;
        /// <paramref name="received"/> gets the number added.</summary>
        public static void Withdraw(Player p, ZDOID anchor, float maxRange, string prefabName, int amount, Action<int> received)
            => Withdraw(p, anchor, Vector3.zero, maxRange, prefabName, amount, received);

        /// <summary>As Withdraw, with the anchor's position for a stale ZDOID. ADD-ONLY overload.</summary>
        public static void Withdraw(Player p, ZDOID anchor, Vector3 anchorPos, float maxRange, string prefabName, int amount, Action<int> received)
        {
            if (!Ready(p, received) || string.IsNullOrEmpty(prefabName) || amount <= 0) { received?.Invoke(0); return; }
            var pkg = Header(KindWithdraw, anchor, anchorPos, maxRange, out long opId);
            pkg.Write(prefabName); pkg.Write(amount);
            Send(opId, pkg, new ClientPending { Kind = KindWithdraw, Done = received, Player = p });
        }

        /// <summary>The anchor network's item counts by prefab name (empty when out of range or no network).</summary>
        public static void Totals(Player p, ZDOID anchor, float maxRange, Action<Dictionary<string, int>> totals)
            => Totals(p, anchor, Vector3.zero, maxRange, totals);

        /// <summary>As Totals, with the anchor's position for a stale ZDOID. ADD-ONLY overload.</summary>
        public static void Totals(Player p, ZDOID anchor, Vector3 anchorPos, float maxRange, Action<Dictionary<string, int>> totals)
        {
            if (p == null || ZRoutedRpc.instance == null) { totals?.Invoke(new Dictionary<string, int>()); return; }
            EnsureRegistered();
            var pkg = Header(KindTotals, anchor, anchorPos, maxRange, out long opId);
            Send(opId, pkg, new ClientPending { Kind = KindTotals, Totals = totals, Player = p });
        }

        private static bool Ready(Player p, Action<int> done)
        {
            if (p == null || p != Player.m_localPlayer || ZRoutedRpc.instance == null) return false;
            EnsureRegistered();
            return true;
        }

        private static ZPackage Header(byte kind, ZDOID anchor, Vector3 anchorPos, float maxRange, out long opId)
        {
            opId = ((long)s_rng.Next() << 31) ^ s_rng.Next() ^ DateTime.UtcNow.Ticks;
            var pkg = new ZPackage();
            pkg.Write(opId); pkg.Write(kind); pkg.Write(anchor); pkg.Write(maxRange);
            pkg.Write(Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L);
            pkg.Write(anchorPos);   // 0.2.772 tail: the anchor's position (zero = none)
            return pkg;
        }

        private static void Send(long opId, ZPackage pkg, ClientPending pending)
        {
            pending.At = Time.time;
            s_clientPending[opId] = pending;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcRequest, pkg);
            if (pending.Player != null) pending.Player.StartCoroutine(ClientTimeout(opId));
        }

        private static System.Collections.IEnumerator ClientTimeout(long opId)
        {
            yield return new WaitForSeconds(ClientTimeoutSeconds);
            if (!s_clientPending.TryGetValue(opId, out var p)) yield break;
            s_clientPending.Remove(opId);
            FiresLogger.LogDebug($"[RemoteStore] op {opId}: no answer from the server in {ClientTimeoutSeconds:0} s (nothing changed here)");
            p.Done?.Invoke(0); p.Totals?.Invoke(new Dictionary<string, int>());
        }

        // reply: {opId, kind, status text, then per kind: totals (n, prefab, count) | withdraw (prefab, quality, granted) | deposit (n, stored per item)}
        private static void OnReply(long sender, ZPackage pkg)
        {
            long opId = pkg.ReadLong(); byte kind = pkg.ReadByte(); string status = pkg.ReadString();
            if (!s_clientPending.TryGetValue(opId, out var p)) return;   // timed out: the server undoes it when no ack arrives
            s_clientPending.Remove(opId);
            Player me = Player.m_localPlayer;
            if (kind == KindTotals)
            {
                var totals = new Dictionary<string, int>(); int n = pkg.ReadInt();
                for (int i = 0; i < n; i++) totals[pkg.ReadString()] = pkg.ReadInt();
                FiresLogger.LogDebug($"[RemoteStore] op {opId}: totals {totals.Count} item kind(s) ({status})");
                p.Totals?.Invoke(totals);
                return;
            }
            int moved = 0;
            if (kind == KindWithdraw)
            {
                string prefab = pkg.ReadString(); int quality = pkg.ReadInt(); int granted = pkg.ReadInt();
                GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
                if (granted > 0 && me != null && itemPrefab != null)
                {
                    var drop = itemPrefab.GetComponent<ItemDrop>();
                    int left = granted;
                    while (left > 0)
                    {
                        int n = Mathf.Min(left, Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize));
                        var clone = drop.m_itemData.Clone(); clone.m_stack = n; clone.m_quality = quality; clone.m_dropPrefab = itemPrefab;
                        if (!me.GetInventory().AddItem(clone)) ItemDrop.DropItem(clone, n, me.transform.position + Vector3.up, Quaternion.identity);   // full bag: at the feet, never lost
                        left -= n;
                    }
                    moved = granted;
                }
            }
            else if (kind == KindDeposit)
            {
                int n = pkg.ReadInt();
                for (int i = 0; i < n && p.Items != null && i < p.Items.Count; i++)
                {
                    int stored = pkg.ReadInt();
                    var it = p.Items[i];
                    if (stored <= 0 || me == null || it == null) continue;
                    me.GetInventory().RemoveItem(it, Mathf.Min(stored, it.m_stack));
                    moved += stored;
                }
            }
            var ack = new ZPackage(); ack.Write(opId);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAck, ack);
            FiresLogger.LogDebug($"[RemoteStore] op {opId}: {(kind == KindWithdraw ? "withdrew" : "deposited")} {moved} ({status}); confirmed to the server");
            p.Done?.Invoke(moved);
        }

        // ================= server side =================

        private sealed class JournalEntry { public long Peer; public byte Kind; public float At; public List<(ZDOID chest, string prefab, int quality, int count)> Moves = new List<(ZDOID, string, int, int)>(); }
        private static readonly Dictionary<long, JournalEntry> s_journal = new Dictionary<long, JournalEntry>();
        private static HashSet<int> s_containerPrefabs;
        private static readonly Dictionary<int, Vector2i> s_containerSizes = new Dictionary<int, Vector2i>();

        private static void OnRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            long opId = pkg.ReadLong(); byte kind = pkg.ReadByte(); ZDOID anchor = pkg.ReadZDOID(); float askRange = pkg.ReadSingle(); long playerId = pkg.ReadLong();
            Vector3 hint = pkg.GetPos() < pkg.Size() ? pkg.ReadVector3() : Vector3.zero;
            var reply = new ZPackage(); reply.Write(opId); reply.Write(kind);
            string why = CheckRange(sender, anchor, hint, askRange, out Vector3 anchorPos);
            List<ZDO> chests = why == null ? NetworkChests(anchorPos, out int skipped, out string skipNote) : new List<ZDO>();
            if (why == null && chests.Count == 0) why = "no chests in the home network";
            switch (kind)
            {
                case KindTotals:
                {
                    var totals = new Dictionary<string, int>();
                    foreach (var z in chests) foreach (var it in Load(z).GetAllItems()) if (it?.m_dropPrefab != null) { totals.TryGetValue(it.m_dropPrefab.name, out int c); totals[it.m_dropPrefab.name] = c + it.m_stack; }
                    reply.Write(why ?? $"{chests.Count} chest(s)"); reply.Write(totals.Count);
                    foreach (var kv in totals) { reply.Write(kv.Key); reply.Write(kv.Value); }
                    break;
                }
                case KindWithdraw:
                {
                    string prefab = pkg.ReadString(); int amount = pkg.ReadInt();
                    int granted = 0, quality = 1;
                    var entry = new JournalEntry { Peer = sender, Kind = kind, At = Time.time };
                    if (why == null)
                        foreach (var z in chests)
                        {
                            if (granted >= amount) break;
                            Inventory inv = Load(z); bool changed = false;
                            foreach (var it in new List<ItemDrop.ItemData>(inv.GetAllItems()))
                            {
                                if (granted >= amount) break;
                                if (it?.m_dropPrefab == null || it.m_dropPrefab.name != prefab) continue;
                                int n = Mathf.Min(it.m_stack, amount - granted);
                                quality = it.m_quality;
                                inv.RemoveItem(it, n); granted += n; changed = true;
                                entry.Moves.Add((z.m_uid, prefab, it.m_quality, n));
                            }
                            if (changed) Save(z, inv);
                        }
                    if (granted > 0) s_journal[opId] = entry;
                    reply.Write(why ?? $"took {granted} from {entry.Moves.Count} stack(s)"); reply.Write(prefab); reply.Write(quality); reply.Write(granted);
                    FiresLogger.LogDebug($"[RemoteStore] server op {opId} withdraw {prefab} x{amount} for player {playerId}: {granted} taken{(why != null ? " - " + why : "")}");
                    break;
                }
                case KindDeposit:
                {
                    int n = pkg.ReadInt();
                    var entry = new JournalEntry { Peer = sender, Kind = kind, At = Time.time };
                    var stored = new int[n]; int total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        string prefab = pkg.ReadString(); int amount = pkg.ReadInt(); int quality = pkg.ReadInt();
                        if (why != null) continue;
                        stored[i] = Place(chests, prefab, quality, amount, entry);
                        total += stored[i];
                    }
                    if (total > 0) s_journal[opId] = entry;
                    reply.Write(why ?? $"stored {total}"); reply.Write(n);
                    for (int i = 0; i < n; i++) reply.Write(stored[i]);
                    FiresLogger.LogDebug($"[RemoteStore] server op {opId} deposit {n} stack(s) for player {playerId}: {total} stored{(why != null ? " - " + why : "")}");
                    break;
                }
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReply, reply);
        }

        private static void OnAck(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            long opId = pkg.ReadLong();
            if (s_journal.Remove(opId)) FiresLogger.LogDebug($"[RemoteStore] server op {opId} confirmed by {sender}");
        }

        // An op the client never confirmed is undone: a withdraw goes back into its chests, a deposit comes back out of them.
        private static void SweepJournal()
        {
            if (s_journal.Count == 0) return;
            var expired = new List<long>();
            foreach (var kv in s_journal) if (Time.time - kv.Value.At > AckTimeoutSeconds) expired.Add(kv.Key);
            foreach (long opId in expired)
            {
                var e = s_journal[opId]; s_journal.Remove(opId);
                int undone = 0, failed = 0;
                foreach (var m in e.Moves)
                {
                    ZDO z = ZDOMan.instance.GetZDO(m.chest);
                    if (z == null || LoadedByAnotherPeer(z, out _)) { failed += m.count; continue; }
                    Inventory inv = Load(z);
                    if (e.Kind == KindWithdraw) { int back = ContainerOps.AddSplit(inv, ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(m.prefab) : null, m.quality, m.count, $"rollback op {opId}"); undone += back; failed += m.count - back; }
                    else { int took = RemovePrefab(inv, m.prefab, m.count); undone += took; failed += m.count - took; }
                    Save(z, inv);
                }
                FiresLogger.LogWarning($"[RemoteStore] server op {opId} ({(e.Kind == KindWithdraw ? "withdraw" : "deposit")}) never confirmed by {e.Peer}: rolled back {undone}{(failed > 0 ? $", {failed} could not be (chest gone or now loaded by a player)" : "")}");
            }
        }

        private static string CheckRange(long sender, ZDOID anchor, Vector3 hint, float askRange, out Vector3 anchorPos)
        {
            anchorPos = Vector3.zero;
            ZDO a = anchor.IsNone() ? null : ZDOMan.instance.GetZDO(anchor);
            bool hasHint = hint.sqrMagnitude > 0.01f;
            // ZDOIDs change across world loads (10-23: the same terminal was 1:9549 then 1:9374), so a stale id falls back to the
            // piece standing at the stored position (the client's terminal); the id is used only when it still sits there.
            if (hasHint && (a == null || Vector2.Distance(new Vector2(a.GetPosition().x, a.GetPosition().z), new Vector2(hint.x, hint.z)) > AnchorMatch))
            {
                ZDO found = PieceAt(hint);
                FiresLogger.LogDebug($"[RemoteStore] anchor {anchor} {(a == null ? "not found" : "moved")}: {(found != null ? $"using the piece at ({hint.x:0.0}, {hint.z:0.0}) = {found.m_uid}" : $"no piece at ({hint.x:0.0}, {hint.z:0.0})")}");
                a = found;
            }
            if (a == null) return "home terminal not found";
            anchorPos = a.GetPosition();
            Vector3 from = PeerPosition(sender);
            float d = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(anchorPos.x, anchorPos.z));
            float limit = Mathf.Min(askRange > 0f ? askRange : MaxRange, MaxRange);
            return d > limit ? $"out of range ({d:0} m, reach {limit:0} m)" : null;
        }

        private const float AnchorMatch = 1.5f;   // m: the terminal is the piece within this of the stored position

        // The networked object standing at a position (its 3x3 zones), nearest first; null when none is within AnchorMatch.
        private static ZDO PieceAt(Vector3 pos)
        {
            EnsurePrefabs();
            var near = new List<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>();
            var zone = FiresCore.World.RingZones.ObjectSector(pos);   // halo ring: object sectors are ring-keyed (FindObjects wraps the seam)
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                ZDOMan.instance.FindObjects(new Vector2s((short)(zone.x + dx), (short)(zone.y + dz)), near, visited);
            ZDO best = null; float bestD = AnchorMatch;
            foreach (var z in near)
            {
                if (z == null || s_containerPrefabs != null && s_containerPrefabs.Contains(z.GetPrefab())) continue;   // never a chest
                Vector3 p = z.GetPosition(); float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(pos.x, pos.z));
                if (d <= bestD) { bestD = d; best = z; }
            }
            return best;
        }

        private static Vector3 PeerPosition(long peerId)
        {
            if (peerId == ZDOMan.GetSessionID() && Player.m_localPlayer != null) return Player.m_localPlayer.transform.position;   // a host's own request
            ZNetPeer peer = ZNet.instance.GetPeer(peerId);
            return peer != null ? peer.m_refPos : new Vector3(float.MaxValue, 0f, float.MaxValue);
        }

        // The chests around the anchor in the server's world data: container prefabs only, never hoppers, Locked chests or chests a
        // player has loaded right now.
        private static List<ZDO> NetworkChests(Vector3 anchor, out int skipped, out string note)
        {
            EnsurePrefabs();
            var near = new List<ZDO>(); var found = new List<ZDO>();
            var zone = FiresCore.World.RingZones.ObjectSector(anchor);   // halo ring: object sectors are ring-keyed
            var visited = new HashSet<ZoneSystem.SectorIndex>();
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)   // the 3x3 zones around it cover the radius
                ZDOMan.instance.FindObjects(new Vector2s((short)(zone.x + dx), (short)(zone.y + dz)), near, visited);
            float r2 = NetworkRadius * NetworkRadius; int locked = 0, loaded = 0, containers = 0, far = 0;
            foreach (var z in near)
            {
                if (z == null || !s_containerPrefabs.Contains(z.GetPrefab())) continue;
                containers++;
                Vector3 p = z.GetPosition(); float d = Mathf.Sqrt((p.x - anchor.x) * (p.x - anchor.x) + (p.z - anchor.z) * (p.z - anchor.z));
                string name = ZNetScene.instance.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString();
                if (d * d > r2) { far++; continue; }
                if (z.GetBool(ChestTags.AutoLockKey, false)) { locked++; FiresLogger.LogDebug($"[RemoteStore]   skip {name} {z.m_uid} at {d:0.0} m: Locked tag"); continue; }
                if (z.GetBool(HopperChest.FlagKey, false)) { locked++; FiresLogger.LogDebug($"[RemoteStore]   skip {name} {z.m_uid} at {d:0.0} m: hopper"); continue; }
                if (LoadedByAnotherPeer(z, out string who)) { loaded++; FiresLogger.LogDebug($"[RemoteStore]   skip {name} {z.m_uid} at {d:0.0} m: loaded by {who}"); continue; }
                found.Add(z);
            }
            found.Sort((x, y) => (x.GetPosition() - anchor).sqrMagnitude.CompareTo((y.GetPosition() - anchor).sqrMagnitude));
            skipped = locked + loaded; note = $"{locked} locked/hopper, {loaded} loaded by a player";
            FiresLogger.LogDebug($"[RemoteStore] network at ({anchor.x:0}, {anchor.z:0}) radius {NetworkRadius:0} m: {near.Count} ZDO(s) scanned in 3x3 zones, {containers} container(s), {far} beyond the radius, {found.Count} chest(s) used, skipped {note}");
            return found;
        }

        // A chest is live in a player's game only while it is inside that player's active area: then the server leaves it alone.
        // Owning its ZDO is not enough: vanilla never clears the owner of a ZDO the player walked far away from (4 km away, the
        // chest still named the player as owner and every remote read skipped it).
        private const float LoadedReach = 3f * 64f;   // vanilla active area (2 zones around the player's zone) plus a zone of margin
        private static bool LoadedByAnotherPeer(ZDO z, out string who)
        {
            who = null;
            long owner = z.GetOwner();
            if (owner == 0L || owner == ZDOMan.GetSessionID()) return false;
            ZNetPeer peer = ZNet.instance.GetPeer(owner);
            if (peer == null) return false;
            Vector3 cp = z.GetPosition(), pp = peer.m_refPos;
            if (Mathf.Max(Mathf.Abs(cp.x - pp.x), Mathf.Abs(cp.z - pp.z)) > LoadedReach) return false;   // outside that player's loaded zones
            who = $"{peer.m_playerName} ({Vector3.Distance(peer.m_refPos, z.GetPosition()):0} m away)";
            return true;
        }

        private static void EnsurePrefabs()
        {
            if (s_containerPrefabs != null) return;
            s_containerPrefabs = new HashSet<int>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                var c = go != null ? go.GetComponentInChildren<Container>(true) : null;   // some modded chests carry it on a child
                if (c == null || go.GetComponent<Vagon>() != null || go.GetComponentInChildren<Ship>() != null || go.GetComponent<Player>() != null || go.GetComponent<Character>() != null) continue;
                int hash = go.name.GetStableHashCode();
                s_containerPrefabs.Add(hash); s_containerSizes[hash] = new Vector2i(c.m_width, c.m_height);
            }
        }

        private static Inventory Load(ZDO z)
        {
            Vector2i size = s_containerSizes.TryGetValue(z.GetPrefab(), out var s) ? s : new Vector2i(8, 8);
            var inv = new Inventory("remote", null, size.x, size.y);
            byte[] bytes = z.GetByteArray(ZDOVars.s_items, null);
            if (bytes != null && bytes.Length > 0) inv.Load(new ZPackage(bytes));
            else { string text = z.GetString(ZDOVars.s_items, ""); if (text.Length > 0) inv.Load(new ZPackage(text)); }
            // A big stack (VAInventory's Compactor / Trophy Pouch) keeps its real count in custom data, because vanilla Load clamps it.
            foreach (var it in inv.GetAllItems())
                if (it != null && it.m_customData.TryGetValue(TrueStackKey, out string raw) && int.TryParse(raw, out int trueCount) && trueCount > it.m_stack) it.m_stack = trueCount;
            return inv;
        }

        private static void Save(ZDO z, Inventory inv)
        {
            // Over-vanilla stacks belong only in a capped bag cell: a chest gets them as vanilla stacks, the total exact, no TrueStack left.
            foreach (var it in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (it == null || !it.m_customData.ContainsKey(TrueStackKey)) continue;
                int total = it.m_stack, quality = it.m_quality; GameObject prefab = it.m_dropPrefab;
                it.m_customData.Remove(TrueStackKey);
                int max = Mathf.Max(1, it.m_shared.m_maxStackSize);
                if (total <= max || prefab == null) continue;
                inv.RemoveItem(it);
                int landed = ContainerOps.AddSplit(inv, prefab, quality, total, $"normalising a big stack in chest {z.m_uid}");
                if (landed < total)
                {
                    // no room for every vanilla stack: keep the rest rather than lose it, and say so
                    var rest = it.Clone(); rest.m_stack = total - landed; rest.m_customData[TrueStackKey] = rest.m_stack.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    inv.AddItem(rest);
                    FiresLogger.LogWarning($"[RemoteStore] chest {z.m_uid}: {prefab.name} x{total} split into vanilla stacks, but {total - landed} did not fit; kept as one big stack");
                }
            }
            var pkg = new ZPackage(); inv.Save(pkg);
            z.Set(ZDOVars.s_items, pkg.GetArray());
        }

        // Network deposit rules on world data: a chest already holding the item first, then untagged chests with room.
        private static int Place(List<ZDO> chests, string prefab, int quality, int amount, JournalEntry entry)
        {
            GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (itemPrefab == null) return 0;
            string shared = itemPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            int left = amount;
            for (int pass = 0; pass < 2 && left > 0; pass++)
                foreach (var z in chests)
                {
                    if (left <= 0) break;
                    Inventory inv = Load(z);
                    bool holds = inv.CountItems(shared) > 0, untagged = string.IsNullOrEmpty(z.GetString(ChestTags.ZdoKey, ""));
                    if (pass == 0 ? !holds : (holds || !untagged)) continue;
                    int fit = 0, lo = 0, hi = left;
                    while (lo < hi) { int mid = (lo + hi + 1) / 2; if (inv.CanAddItem(itemPrefab, mid)) lo = mid; else hi = mid - 1; }
                    fit = lo;
                    if (fit <= 0) continue;
                    fit = ContainerOps.AddSplit(inv, itemPrefab, quality, fit, $"remote deposit into chest {z.m_uid}");
                    if (fit <= 0) continue;
                    Save(z, inv); left -= fit;
                    entry.Moves.Add((z.m_uid, prefab, quality, fit));
                }
            return amount - left;
        }

        private static int RemovePrefab(Inventory inv, string prefab, int amount)
        {
            int took = 0;
            foreach (var it in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (took >= amount) break;
                if (it?.m_dropPrefab == null || it.m_dropPrefab.name != prefab) continue;
                int n = Mathf.Min(it.m_stack, amount - took);
                inv.RemoveItem(it, n); took += n;
            }
            return took;
        }

        // ================= registration =================

        internal static void EnsureRegistered()
        {
            if (ZRoutedRpc.instance == null || ReferenceEquals(s_registeredOn, ZRoutedRpc.instance)) return;
            s_registeredOn = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register<ZPackage>(RpcRequest, OnRequest);
            ZRoutedRpc.instance.Register<ZPackage>(RpcReply, OnReply);
            ZRoutedRpc.instance.Register<ZPackage>(RpcAck, OnAck);
            s_clientPending.Clear(); s_journal.Clear(); s_containerPrefabs = null;
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class GameStart { private static void Postfix() => EnsureRegistered(); }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class ServerSweep
        {
            private static float s_next;
            private static void Postfix(ZNet __instance)
            {
                if (!__instance.IsServer() || Time.time < s_next) return;
                s_next = Time.time + 1f;
                SweepJournal();
            }
        }
    }
}
