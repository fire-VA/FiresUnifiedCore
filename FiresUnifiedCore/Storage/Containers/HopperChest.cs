using System.Collections.Generic;
using FiresCore.Logging;
using FiresCore.Pieces;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// No global vacuum; a HOPPER CHEST instead (design §14).
    /// A hopper is a VANILLA wood chest whose ZDO carries "core_hopper" = true:
    ///   build    the Hammer gets a clone of piece_chest_wood that keeps the vanilla prefab NAME (so a placed hopper's ZDO has the vanilla
    ///            chest's hash) with its own piece name/description/recipe (FineWood 10, BronzeNails 10, Resin 4, workbench); a marker on the
    ///            clone stamps core_hopper onto the new ZDO when it is placed
    ///   load     storage on: Container.Awake (CraftPullPatches) sees the flag and adds HopperChest; storage OFF: it simply loads as the plain
    ///            chest it is (no patch, no prefab registration needed - the storage-off no-op rule holds)
    ///   pull     only the hopper's ZDO OWNER runs it. New drops near a hopper are queued by ItemDrop.Awake (event-driven); a staggered sweep
    ///            every Hopper Sweep Seconds (OverlapSphereNonAlloc, reused buffer) catches the rest. Each pickup is vanilla's own handshake
    ///            (Humanoid.Pickup does the same): not the item's owner yet -> ItemDrop.RequestOwn and try again later; owner -> AddItem +
    ///            ZNetScene.Destroy. A race with a player: whoever owns the item first picks it up; never both.
    ///   filters  per hopper on its ZDO: radius core_hopper_r (1..Hopper Max Radius, default 4), mode core_hopper_mode (All, Inside = only
    ///            items already inside (default), Tags = what its chest tags want); never: items younger than 2 s, items with
    ///            placed pieces, items the hopper can't fit (m_autoPickup is ignored - vanilla clears it on every player drop)
    ///   budget   at most 8 pickups per sweep per hopper, at most 4 hopper sweeps per frame on this peer
    /// </summary>
    public class HopperChest : MonoBehaviour
    {
        public const string FlagKey = "core_hopper", RadiusKey = "core_hopper_r", ModeKey = "core_hopper_mode", SortKey = "core_hopper_sort";
        public enum Mode { Inside = 0, All = 1, Tags = 2 }
        internal const string PieceName = "$piece_core_hopper";

        private static readonly List<HopperChest> s_all = new List<HopperChest>();
        private const int MaxOverlapHits = 64;
        private const int MaxSweepsPerFrame = 4;
        private const int PullsPerFrame = 8;
        private const int MaxSortsInFlight = 4;
        private const int MaxSortsPerSweep = 8;
        private const float DefaultSweepSeconds = 5f, DefaultMaxRadius = 8f, DefaultRadius = 4f, MinRadius = 1f, DefaultSortRadius = 10f;
        private const float MinFirstSweepDelay = 0.5f;
        private const int TagMatchScoreBase = 10, HoldsItemScore = 5;

        private static readonly Collider[] s_hits = new Collider[MaxOverlapHits];
        private static int s_itemMask = -1, s_sweepsThisFrame, s_frame;
        private static readonly Dictionary<ItemDrop, float> s_seen = new Dictionary<ItemDrop, float>();

        private Container _c;
        private ZNetView _nv;
        private float _nextSweep;
        private readonly Queue<ItemDrop> _queue = new Queue<ItemDrop>();
        private int _pulledSinceSweep;
        // Drops already pulled (until they are destroyed), and one Debug line per item type and skip reason per hopper
        private static readonly HashSet<ItemDrop> s_pulled = new HashSet<ItemDrop>();
        private readonly HashSet<string> _skipSaid = new HashSet<string>();
        private void Skipped(ItemDrop d, string why)
        {
            _lastSkip = why;
            string item = d.m_itemData?.m_shared?.m_name ?? d.name;
            if (_skipSaid.Add(item + "|" + why)) FiresLogger.LogDebug($"[Storage] hopper '{_c.m_name}' skips {item}: {why}");
        }
        private string _lastSkip;

        public static bool IsHopper(Container c)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            return nv != null && nv.IsValid() && nv.GetZDO().GetBool(FlagKey, false);
        }

        internal static void TryAttach(Container c)
        {
            if (!On || c == null || c.GetComponent<HopperChest>() != null || !IsHopper(c)) return;
            c.gameObject.AddComponent<HopperChest>();
        }

        internal static bool On => StorageFeatures.Feature(StorageFeatures.Part.Hopper) && StorageConfig.Hoppers != null && StorageConfig.Hoppers.Value;

        private void Awake()
        {
            _c = GetComponent<Container>();
            _nv = ContainerRegistry.NView(_c);
            s_all.Add(this);
            _nextSweep = Time.time + Random.Range(MinFirstSweepDelay, Sweep);   // staggered
            if (s_itemMask < 0) s_itemMask = LayerMask.GetMask("item");
        }

        private void OnDestroy() => s_all.Remove(this);

        private static float Sweep => StorageConfig.HopperSweepSeconds != null ? StorageConfig.HopperSweepSeconds.Value : DefaultSweepSeconds;

        internal float Radius
        {
            get
            {
                float max = StorageConfig.HopperMaxRadius != null ? StorageConfig.HopperMaxRadius.Value : DefaultMaxRadius;
                return Mathf.Clamp(_nv != null && _nv.IsValid() ? _nv.GetZDO().GetFloat(RadiusKey, DefaultRadius) : DefaultRadius, MinRadius, max);
            }
        }

        // The default filter is All - "Inside" on a new, empty hopper pulled nothing by design
        internal Mode CurrentMode => _nv != null && _nv.IsValid() ? (Mode)_nv.GetZDO().GetInt(ModeKey, (int)Mode.All) : Mode.All;

        /// <summary>Sets or clears the hopper flag on a container this peer owns
        /// (the opener owns an open chest) and attaches / removes the behaviour at once. True when it changed.</summary>
        internal static bool SetFlag(Container c, bool on)
        {
            ZNetView nv = c != null ? ContainerRegistry.NView(c) : null;
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
            nv.GetZDO().Set(FlagKey, on);
            if (on) TryAttach(c);
            else { var h = c.GetComponent<HopperChest>(); if (h != null) Destroy(h); }
            FiresLogger.LogInfo($"[Storage] hopper flag {(on ? "set on" : "cleared from")} '{c.m_name}' at ({c.transform.position.x:0.0}, {c.transform.position.y:0.0}, {c.transform.position.z:0.0})");
            return true;
        }

        /// <summary>ItemDrop.Awake (event-driven): a new drop is remembered (for the 2 s delay) and queued on owned hoppers in range.</summary>
        internal static void OnItemSpawned(ItemDrop drop)
        {
            if (!On || drop == null) return;
            s_seen[drop] = Time.time;
            foreach (HopperChest h in s_all)
                if (h != null && h._nv != null && h._nv.IsOwner() && (h.transform.position - drop.transform.position).sqrMagnitude <= h.Radius * h.Radius)
                    h._queue.Enqueue(drop);
        }

        private void Update()
        {
            if (!On) { Destroy(this); return; }
            if (_nv == null || !_nv.IsValid() || !_nv.IsOwner()) return;
            if (Time.frameCount != s_frame) { s_frame = Time.frameCount; s_sweepsThisFrame = 0; }
            if (Time.time >= _nextSweep && s_sweepsThisFrame < MaxSweepsPerFrame)
            {
                s_sweepsThisFrame++;
                _nextSweep = Time.time + Sweep;
                int n = Physics.OverlapSphereNonAlloc(transform.position, Radius, s_hits, s_itemMask);
                for (int i = 0; i < n; i++) { var d = s_hits[i] != null ? s_hits[i].GetComponentInParent<ItemDrop>() : null; if (d != null) _queue.Enqueue(d); }
                // One Debug line per sweep that saw anything - the last sweep's pulls and why items were skipped
                if (n > 0 || _pulledSinceSweep > 0)
                {
                    Vector3 p = transform.position;
                    FiresLogger.LogDebug($"[Storage] hopper '{_c.m_name}' at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}): radius {Radius:0}, filter {CurrentMode}, sweep: {n} candidate(s), {_pulledSinceSweep} pulled since the last sweep{(_lastSkip != null ? ", last skip: " + _lastSkip : "")}");
                }
                _pulledSinceSweep = 0; _lastSkip = null;
                SortSweep();
            }
            int budget = PullsPerFrame;
            int guard = _queue.Count;
            while (_queue.Count > 0 && budget > 0 && guard-- > 0)
            {
                ItemDrop d = _queue.Dequeue();
                switch (TryPull(d))
                {
                    case Pull.Done: budget--; _pulledSinceSweep++; break;
                    case Pull.Later: _queue.Enqueue(d); break;
                }
            }
        }

        private enum Pull { Done, Later, Skip }

        private Pull TryPull(ItemDrop d)
        {
            // Vanilla
            // clears m_autoPickup on every item a PLAYER drops (so it is not re-picked at once) - that flag is for the player's own pickup, not a
            // container's; the hopper ignores it. A drop already pulled this frame is skipped by its own set (that guard used to be the flag).
            if (d == null || d.m_itemData == null) return Pull.Skip;
            if (s_pulled.Count > 256) s_pulled.RemoveWhere(x => x == null);   // destroyed drops
            if (s_pulled.Contains(d)) return Pull.Skip;
            if (d.IsPiece()) { Skipped(d, "a placed piece"); return Pull.Skip; }
            if ((d.transform.position - transform.position).sqrMagnitude > Radius * Radius) return Pull.Skip;
            if (s_seen.TryGetValue(d, out float at) && Time.time - at < 2f) return Pull.Later;   // just dropped: a player may still want it
            Inventory inv = ContainerRegistry.Inv(_c);
            if (inv == null || !Wants(inv, d.m_itemData) || !inv.CanAddItem(d.m_itemData)) { Skipped(d, inv == null ? "no inventory" : !Wants(inv, d.m_itemData) ? $"filter {CurrentMode} refuses it" : "no room"); return Pull.Skip; }
            if (!d.CanPickup(true)) { d.RequestOwn(); return Pull.Later; }   // vanilla's handshake: the item's owner hands it over
            var copy = d.m_itemData.Clone();
            if (!inv.AddItem(copy)) return Pull.Skip;
            s_seen.Remove(d);
            s_pulled.Add(d);   // the same drop can sit in the queue twice (ItemDrop.Awake + a sweep) and ZNetScene.Destroy lands later
                               // - without this the second dequeue that frame pulled it again (own set; m_autoPickup is left alone)
            ZNetScene.instance.Destroy(d.gameObject);   // we own the item's ZDO now (CanPickup said so)
            FiresLogger.LogDebug($"[Storage] hopper '{_c.m_name}' pulled {copy.m_stack} {copy.m_shared.m_name}");
            return Pull.Done;
        }

        private bool Wants(Inventory inv, ItemDrop.ItemData item)
        {
            switch (CurrentMode)
            {
                case Mode.All: return true;
                case Mode.Tags: return ChestTags.Match(_c, item, out _) == true;
                default: return inv.CountItems(item.m_shared.m_name, -1, false) > 0;
            }
        }

        /// <summary>A hopper wins over a companion standing within its pull range; the companion does not
        /// vacuum the loot. True when an active hopper loaded on this peer has <paramref name="drop"/> inside its pull radius, its filter accepts
        /// the item and it has room. Companion pickup / loot behaviours call it and leave the item; the player's own pickup stays vanilla.
        /// Public, add-only.</summary>
        public static bool IsReserved(ItemDrop drop, out string hopperName)
        {
            hopperName = null;
            if (!On || drop == null || drop.m_itemData == null) return false;
            foreach (HopperChest h in s_all)
            {
                if (h == null || h._c == null) continue;
                if ((h.transform.position - drop.transform.position).sqrMagnitude > h.Radius * h.Radius) continue;
                Inventory inv = ContainerRegistry.Inv(h._c);
                if (inv == null || !h.Wants(inv, drop.m_itemData) || !inv.CanAddItem(drop.m_itemData)) continue;
                hopperName = h._c.m_name;
                return true;
            }
            return false;
        }

        private static readonly HashSet<string> s_leftSaid = new HashSet<string>();
        /// <summary>The companion side - true (and one log line per companion + item type) when a hopper has <paramref name="drop"/>.</summary>
        public static bool LeaveForHopper(ItemDrop drop, string companionName)
        {
            if (!IsReserved(drop, out string hopper)) return false;
            string item = drop.m_itemData?.m_shared?.m_name ?? drop.name;
            if (s_leftSaid.Add(companionName + "|" + item)) FiresLogger.LogDebug($"[Storage] companion {companionName} left {item} for hopper '{hopper}'");
            return true;
        }

        internal void SetRadius(float r) { if (_nv != null && _nv.IsOwner()) _nv.GetZDO().Set(RadiusKey, r); }

        // ---- The auto-sorter: pulls any items dropped near the hopper, then sorts them into other containers within
        // Hopper Sort Radius ----
        //   who      containers within Hopper Sort Radius that this player may use (ContainerRegistry.Lendable: access, ward, a reachable
        //            owner), never: itself, other hoppers, carts / ship holds, companion bags, a container someone has open (IsInUse)
        //   choose   per stack: an exact item-name tag (score 3) > a drop-type tag (2) > a category tag (1) > no tags but already holds it (0);
        //            a tag that excludes it (!Name) never; nearest first within a tier (Lendable is nearest-first); unwanted stays here
        //   move     ContainerOps.Put: the target's owner applies it with its ledger (never doubled); the stack leaves this hopper first and
        //            whatever the target didn't take comes back. Stackable items only (Put's rule). Owner of this hopper only.
        //   caps     at most 8 stacks started per sweep, at most 4 transfers in flight per hopper
        internal bool SortOn => _nv == null || !_nv.IsValid() || _nv.GetZDO().GetBool(SortKey, true);
        /// <summary>Where the last sort sweep sorted into (shown in the Chest tags popup).</summary>
        internal string SortScope { get; private set; } = "";
        internal string SortScopeShort { get; private set; } = "";

        internal void SetSort(bool on) { if (_nv != null && _nv.IsOwner()) _nv.GetZDO().Set(SortKey, on); }
        private int _sortInFlight;

        private void SortSweep()
        {
            // The hopper works as a dump chest linked to the base's storage: everything
            // leaves, not only stackables:
            // tagged / matching chests first, then ANY chest with room; stackables via ContainerOps.Put (owner ledger), single items
            // (tools, weapons, armour) straight into a chest this peer owns (one nobody has open is claimed first, moved next sweep). A sweep
            // that moved nothing says why (Debug).
            if (!SortOn) return;
            Player me = Player.m_localPlayer;
            Inventory inv = ContainerRegistry.Inv(_c);
            if (me == null || inv == null || inv.NrOfItems() == 0) return;
            if (_sortInFlight >= MaxSortsInFlight) return;
            float radius = StorageConfig.HopperSortRadius != null ? StorageConfig.HopperSortRadius.Value : DefaultSortRadius;
            // Fire 10-23: a hopper inside a Storage Terminal's network sorts into that WHOLE network (the chests the terminal lists, minus
            // Locked and other hoppers); outside every terminal's network it keeps the Hopper Sort Radius around itself.
            Vector3 centre = transform.position;
            bool network = StorageNetwork.TerminalNetworkAt(me, transform.position, out Vector3 netCentre, out float netRadius, out Vector3 termPos);
            if (network) { centre = netCentre; radius = netRadius; }
            var targets = new List<Container>(ContainerRegistry.Lendable(centre, radius, me, "hopper sort"));
            targets.RemoveAll(t => t == null || t == _c || t.GetComponent<HopperChest>() != null || IsHopper(t) || t.IsInUse()
                || t.GetComponentInParent<Vagon>() != null || t.GetComponentInParent<Ship>() != null
                || t.GetComponentInParent<FiresCore.Npc.CompanionController>() != null);
            SortScope = network ? $"terminal network at ({termPos.x:0}, {termPos.z:0}), {targets.Count} chest(s)" : $"nearby chests within {radius:0} m, {targets.Count} chest(s)";
            SortScopeShort = network ? $"terminal network ({targets.Count} chests)" : $"chests within {radius:0} m ({targets.Count})";
            FiresLogger.LogDebug($"[Storage] hopper '{_c.m_name}' sorts into: {SortScope}");
            if (targets.Count == 0) { SortQuiet($"no containers {(network ? "in the terminal network" : $"within {radius:0} m")} to sort into (other hoppers, carts, ships, companion bags and open or no-access chests don't count)"); return; }
            int started = 0, refused = 0, waiting = 0;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (started >= MaxSortsPerSweep || _sortInFlight >= MaxSortsInFlight) break;
                if (item == null || item.m_dropPrefab == null) continue;
                Container best = null; int bestScore = int.MinValue; string why = null;
                foreach (Container t in targets)
                {
                    Inventory ti = ContainerRegistry.Inv(t);
                    if (ti == null || !ti.CanAddItem(item, 1)) continue;   // no room: never a target
                    bool? m = ChestTags.Match(t, item, out int score);
                    if (m == false) continue;   // its tags exclude this item
                    int s; string reason;
                    if (m == true) { s = TagMatchScoreBase + score; reason = score >= 3 ? "name tag" : score == 2 ? "drop tag" : "category tag"; }
                    else if (ti.CountItems(item.m_shared.m_name, -1, false) > 0) { s = HoldsItemScore; reason = "holds it"; }
                    else if (ChestTags.TagsOf(t).Count == 0) { s = 0; reason = "any (room)"; }   // a tagged chest that doesn't want it is not "any"
                    else continue;
                    if (s > bestScore) { bestScore = s; best = t; why = reason; }   // nearest first: only a higher tier replaces
                }
                if (best == null) { refused++; continue; }
                if (item.m_shared.m_maxStackSize > 1)
                {
                    string prefab = item.m_dropPrefab.name, name = item.m_shared.m_name;
                    int amount = item.m_stack, quality = item.m_quality;
                    inv.RemoveItem(item);   // leaves this hopper first; what the target doesn't take comes back below
                    started++; _sortInFlight++;
                    Container target = best; string reasonFinal = why;
                    StartCoroutine(ContainerOps.Put(target, me, prefab, quality, amount, granted =>
                    {
                        _sortInFlight--;
                        int back = amount - Mathf.Max(0, granted);
                        if (back > 0) inv.AddItem(prefab, back, quality, 0, 0L, "", false, false);
                        if (granted > 0) FiresLogger.LogInfo($"[Storage] hopper sorted {granted} {name} -> ''{target.m_name}'' ({reasonFinal}){(back > 0 ? $", {back} came back" : "")}");
                    }));
                }
                else
                {
                    // a single item keeps its own data (quality, durability, crafter): moved as the object, only into a chest this peer owns
                    ZNetView tnv = ContainerRegistry.NView(best);
                    if (tnv == null || !tnv.IsValid()) continue;
                    if (!tnv.IsOwner()) { if (!best.IsInUse()) tnv.ClaimOwnership(); waiting++; continue; }
                    Inventory ti = ContainerRegistry.Inv(best);
                    if (ti == null || !ti.AddItem(item)) { refused++; continue; }
                    inv.RemoveItem(item);
                    started++;
                    FiresLogger.LogInfo($"[Storage] hopper sorted 1 {item.m_shared.m_name} -> ''{best.m_name}'' ({why})");
                }
            }
            if (started == 0) SortQuiet(waiting > 0 ? $"{waiting} single item(s) waiting for a target chest''s ownership (claimed; next sweep)" : $"{refused} item(s), no container within {radius:0} m wants them or has room");
        }

        private string _sortQuietSaid;
        private void SortQuiet(string why)
        {
            if (why == _sortQuietSaid) return;   // once per change of reason
            _sortQuietSaid = why;
            FiresLogger.LogDebug($"[Storage] hopper ''{_c.m_name}'' sorted nothing: {why}");
        }        internal void SetMode(Mode m) { if (_nv != null && _nv.IsOwner()) _nv.GetZDO().Set(ModeKey, (int)m); }

        // ---- the Hammer piece ----
        private static GameObject s_holder, s_piece;

        /// <summary>Adds the Hopper Chest to the Hammer (once per ObjectDB). Called from Enable and from the ObjectDB.Awake patch.</summary>
        internal static void EnsurePiece()
        {
            if (!On || ObjectDB.instance == null || HammerPieces.IsMainMenuObjectDB(ObjectDB.instance) || ZNetScene.instance == null) return;
            PieceTable table = HammerPieces.HammerTable();
            if (table == null) return;
            if (s_piece == null)
            {
                GameObject chest = ZNetScene.instance.GetPrefab("piece_chest_wood");
                if (chest == null) return;
                if (s_holder == null) { s_holder = new GameObject("CoreHopperHolder"); s_holder.SetActive(false); Object.DontDestroyOnLoad(s_holder); }
                s_piece = Object.Instantiate(chest, s_holder.transform);
                s_piece.name = chest.name;   // the VANILLA name: placed hoppers carry the vanilla chest hash (see the class summary)
                var piece = s_piece.GetComponent<Piece>();
                piece.m_name = PieceName;
                piece.m_description = "A chest that pulls nearby dropped items into itself. Set its radius and filter on its Tags panel.";
                piece.m_resources = new[] { Req("FineWood", 10), Req("BronzeNails", 10), Req("Resin", 4) };
                s_piece.GetComponent<Container>().m_name = PieceName;
                s_piece.AddComponent<HopperMarker>();
                Localization.instance?.AddWord(PieceName.TrimStart('$'), "Hopper chest");
            }
            if (HammerPieces.AddToTable(table, s_piece)) FiresLogger.LogDebug("[Storage] Hopper Chest added to the Hammer");
        }

        internal static void RemovePiece()
        {
            PieceTable table = HammerPieces.HammerTable();
            if (table != null && s_piece != null) table.m_pieces.Remove(s_piece);
        }

        private static Piece.Requirement Req(string item, int amount) => new Piece.Requirement
        {
            m_resItem = ObjectDB.instance.GetItemPrefab(item)?.GetComponent<ItemDrop>(), m_amount = amount, m_recover = true
        };
    }

    /// <summary>On the Hammer clone only: stamps the hopper flag onto the ZDO a placement creates (then removes itself).</summary>
    internal class HopperMarker : MonoBehaviour
    {
        private void Start()
        {
            var nv = GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.IsOwner() && !nv.GetZDO().GetBool(HopperChest.FlagKey, false))
            {
                nv.GetZDO().Set(HopperChest.FlagKey, true);
                var c = GetComponent<Container>();
                if (c != null) { c.m_name = HopperChest.PieceName; HopperChest.TryAttach(c); }
            }
            Destroy(this);
        }
    }
}
