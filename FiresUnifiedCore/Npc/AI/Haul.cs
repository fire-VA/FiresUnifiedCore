using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Core;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>What a haul has done so far (Tools\COMPANION_HAUL.md §2). The evidence is still the chests; this is the job's view.</summary>
    public sealed class HaulProgress
    {
        internal readonly Dictionary<string, int> gathered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, int> deposited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per item prefab: picked up since the start (in the bag now + deposited - in the bag at the start).</summary>
        public IReadOnlyDictionary<string, int> Gathered => gathered;
        /// <summary>Per item prefab: what the home chests gained from this body's deposits (chest counts before and after).</summary>
        public IReadOnlyDictionary<string, int> Deposited => deposited;
        public int ChestsBuilt { get; internal set; }
        public int Trips { get; internal set; }
        /// <summary>outbound, homebound, at home, quota met, gave up, stopped.</summary>
        public string State { get; internal set; } = "starting";
        public string LastLine { get; internal set; } = "";
    }

    /// <summary>
    /// The haul for any body (Core 0.2.220; Fire: "gather resources and when it gets close to its weight limit wander back towards
    /// its base and build a chest if it needs to"): gather to a quota, turn home at <see cref="CarryRules.StoreAt"/> of the carry
    /// limit, deposit into chests within <see cref="HaulJob.HomeRadius"/> of home, build a chest there when none has room, and go
    /// again. One brain: a companion (CompanionController.StartHaul) and the FDT bot bodies (HaulVerb) run the same job.
    /// </summary>
    public static class Haul
    {
        private static readonly Dictionary<Humanoid, HaulJob> s_jobs = new Dictionary<Humanoid, HaulJob>();

        /// <summary>
        /// Starts a haul for <paramref name="body"/> (replacing one it was on). <paramref name="quota"/>: item prefab → count
        /// (e.g. Wood 100, Stone 100). <paramref name="maxWeight"/>: the body's carry limit (null: a player's own, else 300).
        /// </summary>
        public static HaulJob Start(ITaskBody body, Vector3 home, IReadOnlyDictionary<string, int> quota, bool pickFood,
            Func<float> maxWeight = null)
        {
            if (body == null || body.Character == null || quota == null) return null;
            foreach (Humanoid gone in s_jobs.Keys.Where(k => k == null).ToList()) s_jobs.Remove(gone);
            if (s_jobs.TryGetValue(body.Character, out HaulJob old) && old.Active) old.Stop("replaced by a new haul");
            var job = new HaulJob(body, home, quota, pickFood, maxWeight);
            s_jobs[body.Character] = job;
            return job;
        }

        /// <summary>The body's haul (running or finished), or null.</summary>
        public static HaulJob For(Humanoid body) => body != null && s_jobs.TryGetValue(body, out HaulJob job) ? job : null;

        /// <summary>
        /// The body's ZDO string with its haul's progress: "State|Trips|ChestsBuilt|item:gathered/deposited,…|LastLine", written by the
        /// owning machine on change, at most once a second (and at the end).
        /// </summary>
        public const string ZdoKey = "fires_haul";

        /// <summary>
        /// A body's haul progress as its owner last published it, readable on any peer (a judge on the bot while the companion is
        /// owned by another client); null when there is none. Where the job runs locally, <see cref="For"/>'s Progress is live.
        /// </summary>
        public static HaulProgress ReadPublished(Character body)
        {
            ZNetView nview = body != null ? body.GetComponent<ZNetView>() : null;
            string text = nview != null && nview.IsValid() ? nview.GetZDO().GetString(ZdoKey, "") : "";
            string[] parts = text.Split(new[] { '|' }, 5);
            if (parts.Length < 5) return null;
            var progress = new HaulProgress { State = parts[0], LastLine = parts[4] };
            progress.Trips = int.TryParse(parts[1], out int trips) ? trips : 0;
            progress.ChestsBuilt = int.TryParse(parts[2], out int chests) ? chests : 0;
            foreach (string item in parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = item.LastIndexOf(':'), slash = item.LastIndexOf('/');
                if (colon <= 0 || slash <= colon) continue;
                string name = item.Substring(0, colon);
                if (int.TryParse(item.Substring(colon + 1, slash - colon - 1), out int g)) progress.gathered[name] = g;
                if (int.TryParse(item.Substring(slash + 1), out int d)) progress.deposited[name] = d;
            }
            return progress;
        }
    }

    /// <summary>
    /// One body's haul. The driver asks <see cref="Next"/> whenever the body isn't busy and isn't fighting, and carries the order out
    /// (Move / Equip / Swing / Interact as for ChoreBrain's chores, plus Build and Craft). Main thread, on the machine that owns the body.
    /// </summary>
    public sealed class HaulJob
    {
        /// <summary>Home chests are the ones within this of home (m).</summary>
        public const float HomeRadius = 10f;
        /// <summary>How far from the body a gather target may be (m); a second look goes round home.</summary>
        public const float GatherRange = 50f;
        /// <summary>Food pickables within this of the body are picked on the way (m).</summary>
        public const float FoodDetour = 15f;
        /// <summary>A haul keeps this many food stacks (the best) and deposits the rest.</summary>
        public const int KeepFoodStacks = 2;
        public const string ChestPiece = "piece_chest_wood";
        public const string Pickaxe = "PickaxeAntler";
        public const string Hammer = "Hammer";

        private const float UseDistance = 2.5f;
        private const float StationRadius = 20f;
        private const float LegMetres = 48f;
        private const float NoProgressSeconds = 15f;
        private const float FoodLookSeconds = 3f;
        private const float DangerRadius = 20f, DangerSkip = 0.5f;
        private const float LooseRadius = 8f;
        private const float LooseReach = 1.2f;
        private const float LooseWaitSeconds = 3f;
        private const int BuildTries = 3;
        private const int CraftTries = 2;

        // Quota items a rock gives: they want a pickaxe (the §2a tool-for-quota rule).
        private static readonly HashSet<string> MinedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Stone", "CopperOre", "TinOre", "IronScrap", "SilverOre", "Obsidian" };
        // Parts a body may gather itself for a craft; anything else must be in its bag or a home chest.
        private static readonly HashSet<string> GatherableParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Wood", "Stone", "Resin", "Flint" };
        private static HashSet<string> s_foodItems;

        private enum Phase { Outbound, Homebound, AtHome }

        // One item the haul crafts for itself (the pickaxe for Stone, a player body's hammer for the chest).
        private sealed class CraftPlan
        {
            public string Item;
            public bool Done, Refused, Pending;
            public int Tries;
            public string Have = "", Station = "";
            public readonly Dictionary<string, string> From = new Dictionary<string, string>();
            public HashSet<string> Gather;
        }

        private readonly ITaskBody _body;
        private readonly Func<float> _maxWeight;
        private readonly Dictionary<string, int> _quota = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _startCarried = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _tripStored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foodSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _skip = new HashSet<int>();
        private readonly HashSet<int> _homeChests = new HashSet<int>();
        // Chests that took nothing this home visit (open, claimed elsewhere): not asked again until the next visit.
        private readonly HashSet<int> _refusedChests = new HashSet<int>();
        private readonly Dictionary<string, CraftPlan> _plans = new Dictionary<string, CraftPlan>(StringComparer.OrdinalIgnoreCase);

        private Phase _phase = Phase.Outbound;
        private ResourceDataHelper.ResourceData _target;
        private bool _tripOpen;
        private string _endReason = "";
        private float _nextFoodLook;
        private ItemDrop _loose;
        private float _looseReachedAt = -1f;

        private Vector3 _moveGoal = Vector3.positiveInfinity;
        private float _moveBest, _moveBestAt;
        private int _homeStalls;

        private bool _buildPending, _chestOwed;
        private Vector3 _buildAt;
        private int _buildWoodBefore, _buildTries;
        private string _buildWhy = "";
        private HashSet<string> _gatherFor;

        public ITaskBody Body => _body;
        public Vector3 Home { get; }
        public bool PickFood { get; }
        public IReadOnlyDictionary<string, int> Quota => _quota;
        public HaulProgress Progress { get; } = new HaulProgress();
        public bool Active { get; private set; } = true;
        /// <summary>
        /// Why it ended, "" while running: "quota met: Wood 100/100, Stone 100/100", "gave up: &lt;why&gt;" or "stopped: &lt;why&gt;".
        /// <see cref="HaulProgress.State"/> carries the first words (quota met / gave up / stopped).
        /// </summary>
        public string StopReason => Active ? "" : _endReason;
        /// <summary>Trips.Started/Ended carry this.</summary>
        public string Session { get; }
        /// <summary>The co-op partner (Party.Regroup before going home, Party.Leash on trips); null alone.</summary>
        public Character Partner { get; set; }

        internal HaulJob(ITaskBody body, Vector3 home, IReadOnlyDictionary<string, int> quota, bool pickFood, Func<float> maxWeight)
        {
            _body = body;
            _maxWeight = maxWeight;
            Home = home;
            PickFood = pickFood;
            foreach (var kv in quota)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) _quota[kv.Key] = kv.Value;
            foreach (string item in _quota.Keys)
            {
                _startCarried[item] = Carried(item);
                Progress.deposited[item] = 0;
                Progress.gathered[item] = 0;
            }
            Session = $"haul {Name} {NetTime():0}";
            Say($"start quota {string.Join(", ", _quota.Select(kv => $"{kv.Key} {kv.Value}"))}, food {(pickFood ? "yes" : "no")}, home ({home.x:0}, {home.z:0})");
            Progress.State = "outbound";
        }

        public void Stop(string why)
        {
            if (Active) Finish("stopped", $"stopped: {why}");
        }

        /// <summary>The next order for the body. Done / Failed end the haul (<see cref="Active"/> false).</summary>
        public TaskOrder Next()
        {
            TaskOrder order = NextStep();
            Publish(force: !Active);
            return order;
        }

        private TaskOrder NextStep()
        {
            if (!Active) return TaskOrder.Done(_endReason);
            Humanoid me = _body.Character;
            if (me == null || me.IsDead()) return GiveUp("the body is dead or gone");
            if (_body.Inventory == null) return GiveUp("no bag");

            UpdateGathered();
            CheckBuild();
            foreach (CraftPlan plan in _plans.Values) CheckCraft(plan);

            if (QuotaDeposited())
            {
                Finish("quota met", "quota met: " + QuotaLine());
                return TaskOrder.Done(_endReason);
            }

            if (_phase == Phase.Outbound)
            {
                TaskOrder order = NextOutbound(out string turnWhy);
                if (!Active) return order ?? TaskOrder.Failed(_endReason);
                if (order != null) return order;
                _phase = Phase.Homebound;
                Progress.State = "homebound";
                Say($"turning home at {Weight():0}/{Max():0} ({Share() * 100f:0} %){(turnWhy.Length > 0 ? ": " + turnWhy : "")}");
            }

            if (_phase == Phase.Homebound)
            {
                if (Partner != null)
                {
                    var state = Party.Regroup(me, Partner, $"going home (bag: {Share() * 100f:0} %)", out Vector3 meet);
                    if (state == Party.RegroupState.Walking) return new TaskOrder { Kind = TaskKind.Move, Point = meet };
                }
                if (Flat(_body.Position, Home) > HomeRadius * 0.5f)
                {
                    TaskOrder walk = MoveTo(Home, null);
                    if (Stalled())
                    {
                        ResetMove();
                        if (++_homeStalls >= 3) return GiveUp($"stuck on the way home at ({_body.Position.x:0}, {_body.Position.z:0})");
                    }
                    return walk;
                }
                _phase = Phase.AtHome;
                Progress.State = "at home";
                _homeStalls = 0;
            }

            return NextAtHome();
        }

        // ---- Out: gather ----

        private TaskOrder NextOutbound(out string turnWhy)
        {
            turnWhy = "";
            _gatherFor = null;
            if (Share() >= CarryRules.StoreAt) return null;
            if (!_tripOpen) OpenTrip();

            if (Partner != null && Party.Leash(_body.Character, Partner, out _))
                return MoveTo(Partner.transform.position, Partner.gameObject);

            // A player body owes a chest and has no hammer: make one first. Then the pickaxe the mined quota items need.
            if (_chestOwed && NeedsHammer())
            {
                TaskOrder hammer = NextCraft(Plan(Hammer));
                if (hammer != null) return hammer;
                if (Plan(Hammer).Refused) return GiveUp($"a chest is needed and there's no {Hammer} to build it with");
            }
            if (_gatherFor == null && WantsMined() && !HasPickaxe())
            {
                TaskOrder pick = NextCraft(Plan(Pickaxe));
                if (pick != null) return pick;
            }

            HashSet<string> wanted = _gatherFor ?? Wanted();
            if (wanted.Count == 0)
            {
                if (Weight() <= 0f) return GiveUp("nothing left to gather for the quota and nothing to carry home");
                turnWhy = "the quota is in the bag";
                return null;
            }

            // What a felled tree or a broken rock dropped lies on the ground: walk over it (auto-pickup takes it) before the next node.
            if (_target == null)
            {
                TaskOrder loose = NextLooseDrop(wanted);
                if (loose != null) return loose;
            }

            if (PickFood && _target == null && Time.time >= _nextFoodLook)
            {
                _nextFoodLook = Time.time + FoodLookSeconds;
                var food = FindTarget(_body.Position, FoodDetour, FoodItems(), pickablesOnly: true);
                if (food != null) SetTarget(food);
            }

            for (int tries = 0; tries < 3; tries++)
            {
                if (_target == null || _target.GameObject == null || _skip.Contains(_target.GameObject.GetInstanceID()))
                {
                    var found = FindTarget(_body.Position, GatherRange, wanted, false) ?? FindTarget(Home, GatherRange + HomeRadius, wanted, false);
                    if (found == null)
                    {
                        if (Weight() <= 0f) return GiveUp($"nothing gatherable for {string.Join("/", wanted)} within {GatherRange:0} m");
                        turnWhy = $"nothing left to gather within {GatherRange:0} m";
                        return null;
                    }
                    SetTarget(found);
                }

                TaskOrder order = ChoreBrain.NextGather(_body, _target);
                if (order.Kind == TaskKind.Done) { _target = null; continue; }
                if (order.Kind == TaskKind.Failed) { Skip(_target, order.Reason); continue; }
                if (order.Kind == TaskKind.Move)
                {
                    TaskOrder walk = MoveTo(order.Point, order.Target);
                    if (Stalled()) { Skip(_target, $"no progress in {NoProgressSeconds:0} s"); continue; }
                    return walk;
                }
                ResetMove();
                return order;
            }
            return new TaskOrder { Kind = TaskKind.Move, Point = _body.Position };
        }

        private HashSet<string> Wanted()
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _quota)
                if (Progress.deposited[kv.Key] + Carried(kv.Key) < kv.Value) wanted.Add(kv.Key);
            if (_chestOwed && Carried("Wood") < ChestWoodCost()) wanted.Add("Wood");
            return wanted;
        }

        private bool WantsMined() => _quota.Keys.Any(k => MinedItems.Contains(k) && Progress.deposited[k] + Carried(k) < _quota[k]);
        private bool HasPickaxe() =>
            ResourceDataHelper.BestToolTier(TaskBodyHands.Carried(_body), ResourceDataHelper.ToolType.Pickaxe) != ResourceDataHelper.NoToolTier;
        // Only a player places pieces by hand; a companion's piece is placed by Core (ChoreBrain.PlacePiece).
        private bool NeedsHammer() => _body.Character is Player && Carried(Hammer) == 0;

        private ResourceDataHelper.ResourceData FindTarget(Vector3 at, float range, ICollection<string> wanted, bool pickablesOnly)
        {
            if (wanted == null || wanted.Count == 0) return null;
            var candidates = ChoreBrain.GatherCandidates(at, range, TaskBodyHands.Carried(_body), _body.OwnerPlayerId, wanted, max: 24);
            ResourceDataHelper.ResourceData noPath = null;
            foreach (var c in candidates)
            {
                if (c.Object == null || _skip.Contains(c.Object.GetInstanceID())) continue;
                if (pickablesOnly && !c.Data.IsPickable) continue;
                // Not where a remembered killer stood (0.2.221, World.EnemyMemory.DangerAt): skipped once, with the reason.
                if (World.EnemyMemory.DangerAt(c.Position, DangerRadius, out string danger) >= DangerSkip)
                {
                    _skip.Add(c.Object.GetInstanceID());
                    Say($"skipped {c.Prefab} at ({c.Position.x:0}, {c.Position.z:0}): danger near it ({danger})");
                    continue;
                }
                if (c.Usable) return c.Data;
                if (noPath == null && c.Reason == "no path") noPath = c.Data;
            }
            return noPath;
        }

        private void SetTarget(ResourceDataHelper.ResourceData target)
        {
            _target = target;
            ResetMove();
            if (!target.IsPickable) return;
            var drops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ResourceDataHelper.DropsOf(target.GameObject, drops);
            foreach (string d in drops)
                if (FoodItems().Contains(d) && _foodSeen.Add(d)) _startCarried[d] = Carried(d);
        }

        private void Skip(ResourceDataHelper.ResourceData target, string why)
        {
            if (target?.GameObject != null)
            {
                _skip.Add(target.GameObject.GetInstanceID());
                Vector3 p = target.GameObject.transform.position;
                Say($"skipped {Utils.GetPrefabName(target.GameObject)} at ({p.x:0}, {p.z:0}): {why}");
            }
            _target = null;
            ResetMove();
        }

        // The nearest loose drop of a wanted item (or food, with PickFood) within LooseRadius: walk onto it and give the body's
        // auto-pickup LooseWaitSeconds to take it (a player's vanilla pickup; a companion's HaulBehavior pickup check).
        private TaskOrder NextLooseDrop(HashSet<string> wanted)
        {
            if (_loose == null || !_loose || _skip.Contains(_loose.GetInstanceID()))
            {
                _loose = null;
                _looseReachedAt = -1f;
                float best = LooseRadius * LooseRadius;
                foreach (ItemDrop drop in ItemDrop.s_instances)
                {
                    if (drop == null || _skip.Contains(drop.GetInstanceID())) continue;
                    string prefab = Utils.GetPrefabName(drop.gameObject);
                    if (!wanted.Contains(prefab) && !(PickFood && FoodItems().Contains(prefab))) continue;
                    float d = (drop.transform.position - _body.Position).sqrMagnitude;
                    if (d < best) { best = d; _loose = drop; }
                }
                if (_loose == null) return null;
                ResetMove();
            }
            Vector3 at = _loose.transform.position;
            if (Vector3.Distance(_body.Position, at) > LooseReach)
            {
                TaskOrder walk = MoveTo(at, _loose.gameObject);
                if (!Stalled()) return walk;
                SkipLoose("no progress");
                return null;
            }
            if (_looseReachedAt < 0f) _looseReachedAt = Time.time;
            if (Time.time - _looseReachedAt < LooseWaitSeconds) return new TaskOrder { Kind = TaskKind.Move, Point = at, Target = _loose.gameObject };
            SkipLoose($"not picked up in {LooseWaitSeconds:0} s");
            return null;
        }

        private void SkipLoose(string why)
        {
            if (_loose)
            {
                _skip.Add(_loose.GetInstanceID());
                Vector3 p = _loose.transform.position;
                Say($"left {Utils.GetPrefabName(_loose.gameObject)} x{_loose.m_itemData.m_stack} at ({p.x:0}, {p.z:0}): {why}");
            }
            _loose = null;
            _looseReachedAt = -1f;
            ResetMove();
        }

        // ---- Crafting what the haul needs (§2a) ----

        private CraftPlan Plan(string item)
        {
            if (!_plans.TryGetValue(item, out CraftPlan plan)) _plans[item] = plan = new CraftPlan { Item = item };
            return plan;
        }

        /// <summary>
        /// The next step of crafting <paramref name="plan"/>'s item: parts from the bag, then the home chests, then (Wood, Stone, …)
        /// gathered (sets <see cref="_gatherFor"/> and returns null), then the station and a Craft order. Null when done or refused.
        /// </summary>
        private TaskOrder NextCraft(CraftPlan plan)
        {
            if (plan.Done || plan.Refused || plan.Pending) return null;
            if (Carried(plan.Item) > 0) { plan.Done = true; return null; }

            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(plan.Item) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            Recipe recipe = drop != null ? ObjectDB.instance.GetRecipe(drop.m_itemData) : null;
            if (recipe == null || !recipe.m_enabled) return Refuse(plan, $"missing: no {plan.Item} recipe");

            string refusal = ChoreBrain.CraftStationRefusal(recipe, Home, StationRadius, out CraftingStation station);
            if (refusal.Length > 0) return Refuse(plan, refusal);

            bool firstLook = plan.Have.Length == 0;
            var have = new List<string>();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;
                int need = req.GetAmount(1);
                if (need <= 0) continue;
                string item = req.m_resItem.name;
                int bag = _body.Inventory.CountItems(req.m_resItem.m_itemData.m_shared.m_name);
                Container chest = ChoreBrain.FindChestWith(Home, HomeRadius, _body.OwnerPlayerId, item);
                int inChests = chest != null ? InventoryTransferService.CountItem(chest.GetInventory(), item) : 0;
                have.Add(bag > 0 && inChests > 0 ? $"{item} {Math.Min(need, bag + inChests)}/{need} (bag {bag}, chests {inChests})"
                    : bag > 0 ? $"{item} {Math.Min(need, bag)}/{need} (bag)"
                    : inChests > 0 ? $"{item} {Math.Min(need, inChests)}/{need} (chests)" : $"{item} 0/{need}");
                if (bag >= need) continue;

                if (chest != null)
                {
                    if (Vector3.Distance(_body.Position, chest.transform.position) > UseDistance)
                    {
                        TaskOrder walk = MoveTo(chest.transform.position, chest.gameObject);
                        return Stalled() ? Refuse(plan, $"missing: can't reach the chest holding {item}") : walk;
                    }
                    int took = ChoreBrain.Withdraw(chest, _body.Inventory, item, need - bag, _body.OwnerPlayerId);
                    if (took > 0) plan.From[item] = $"{took} from {Utils.GetPrefabName(chest.gameObject)}";
                    if (firstLook) plan.Have = string.Join(", ", have);
                    return new TaskOrder { Kind = TaskKind.Move, Point = _body.Position };
                }
                if (GatherableParts.Contains(item))
                {
                    if (firstLook) plan.Have = string.Join(", ", have);
                    _gatherFor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item };
                    return null;
                }
                return Refuse(plan, $"missing: {item} {bag}/{need} (bag {bag}, chests 0)");
            }
            if (firstLook) plan.Have = string.Join(", ", have);

            if (station != null && Vector3.Distance(_body.Position, station.transform.position) > UseDistance + 0.5f)
            {
                TaskOrder walk = MoveTo(station.transform.position, station.gameObject);
                return Stalled() ? Refuse(plan, $"station: can't reach {Utils.GetPrefabName(station.gameObject)}") : walk;
            }
            plan.Pending = true;
            plan.Station = station != null ? Utils.GetPrefabName(station.gameObject) : "the inventory";
            ResetMove();
            return new TaskOrder { Kind = TaskKind.Craft, Recipe = recipe, Target = station != null ? station.gameObject : null,
                Point = station != null ? station.transform.position : _body.Position };
        }

        private TaskOrder Refuse(CraftPlan plan, string reason)
        {
            plan.Refused = true;
            Log($"[Craft] {Name} craft refused: {reason}");
            return null;
        }

        // A Craft order was carried out last time: did the item arrive?
        private void CheckCraft(CraftPlan plan)
        {
            if (!plan.Pending) return;
            plan.Pending = false;
            if (Carried(plan.Item) > 0)
            {
                plan.Done = true;
                Log($"[Craft] {Name} craft plan: {plan.Item}: have {plan.Have} -> crafted at {(plan.Station == "piece_workbench" ? "the workbench" : plan.Station)}");
                string from = string.Join(", ", plan.From.Select(kv => $"{kv.Key} {kv.Value}"));
                Log($"[Craft] {Name} crafted {plan.Item} at {plan.Station} ({(from.Length > 0 ? from + "; the rest from bag" : "all from bag")})");
                return;
            }
            if (++plan.Tries >= CraftTries) Refuse(plan, $"station: crafting {plan.Item} at {plan.Station} made nothing ({plan.Tries} tries)");
        }

        // ---- Home: deposit, build a chest ----

        private TaskOrder NextAtHome()
        {
            var toStore = ToDeposit(Keep());
            if (toStore.Count == 0) return LeaveHome();

            RememberHomeChests();
            Container chest = FindRoom(toStore);
            if (chest != null)
            {
                if (Vector3.Distance(_body.Position, chest.transform.position) > UseDistance)
                {
                    TaskOrder walk = MoveTo(chest.transform.position, chest.gameObject);
                    if (Stalled()) return GiveUp($"can't reach {Utils.GetPrefabName(chest.gameObject)} at ({chest.transform.position.x:0}, {chest.transform.position.z:0})");
                    return walk;
                }
                ResetMove();
                Deposit(chest, toStore);
                return new TaskOrder { Kind = TaskKind.Move, Point = _body.Position };
            }

            // No chest at home has room: build one (§2.3), with Wood from the bag or a home chest, and a player body's hammer.
            if (!_chestOwed) Say($"no home chest has room: building {ChestPiece}");
            _chestOwed = true;
            int cost = ChestWoodCost();
            if (Carried("Wood") < cost)
            {
                Container woodChest = ChoreBrain.FindChestWith(Home, HomeRadius, _body.OwnerPlayerId, "Wood");
                if (woodChest != null)
                {
                    if (Vector3.Distance(_body.Position, woodChest.transform.position) > UseDistance)
                        return MoveTo(woodChest.transform.position, woodChest.gameObject);
                    ChoreBrain.Withdraw(woodChest, _body.Inventory, "Wood", cost - Carried("Wood"), _body.OwnerPlayerId);
                    return new TaskOrder { Kind = TaskKind.Move, Point = _body.Position };
                }
                return GoGather($"{cost} Wood for {ChestPiece}");
            }
            if (NeedsHammer())
            {
                TaskOrder hammer = NextCraft(Plan(Hammer));
                if (hammer != null) return hammer;
                if (_gatherFor != null) return GoGather($"{string.Join("/", _gatherFor)} for a {Hammer}");
                if (Plan(Hammer).Refused || NeedsHammer()) return GiveUp($"a chest is needed and there's no {Hammer} to build it with");
            }
            if (_buildTries >= BuildTries) return GiveUp($"could not build {ChestPiece} ({_buildTries} tries): {_buildWhy}");
            if (!ChestSpot(out Vector3 spot, out Quaternion rot, out string noSpot)) return GiveUp(noSpot);
            if (Vector3.Distance(_body.Position, spot) > UseDistance + 1f)
            {
                TaskOrder walk = MoveTo(spot, null);
                return Stalled() ? GiveUp($"can't reach the chest spot at ({spot.x:0}, {spot.z:0})") : walk;
            }
            ResetMove();
            _buildPending = true;
            _buildAt = spot;
            _buildWoodBefore = Carried("Wood");
            _buildTries++;
            return new TaskOrder { Kind = TaskKind.Build, PieceName = ChestPiece, Point = spot, Rotation = rot };
        }

        // Out again for what a chest or its hammer needs; with a full bag and nowhere to put it that can't work.
        private TaskOrder GoGather(string what)
        {
            if (Share() >= CarryRules.StoreAt) return GiveUp($"the bag is full, no home chest has room, and gathering {what} needs room");
            Say($"going out for {what}");
            return LeaveHome();
        }

        private TaskOrder LeaveHome()
        {
            if (_tripOpen)
                CloseTrip(_tripStored.Count > 0 ? "deposited " + string.Join(", ", _tripStored.Select(kv => $"{kv.Key} x{kv.Value}")) : "nothing deposited");
            if (QuotaDeposited())
            {
                Finish("quota met", "quota met: " + QuotaLine());
                return TaskOrder.Done(_endReason);
            }
            _refusedChests.Clear();
            _phase = Phase.Outbound;
            Progress.State = "outbound";
            return new TaskOrder { Kind = TaskKind.Move, Point = _body.Position };
        }

        // A Build order was carried out last time: is there a new chest of ours at the spot?
        private void CheckBuild()
        {
            if (!_buildPending) return;
            _buildPending = false;
            foreach (Container chest in ChestHelper.FindNearbyChests(_buildAt, 2f))
            {
                if (chest == null || _homeChests.Contains(chest.GetInstanceID())) continue;
                Piece piece = chest.GetComponent<Piece>();
                if (piece == null || piece.GetCreator() != _body.OwnerPlayerId) continue;
                _homeChests.Add(chest.GetInstanceID());
                Progress.ChestsBuilt++;
                _chestOwed = false;
                int paid = Math.Max(0, _buildWoodBefore - Carried("Wood"));
                Vector3 p = chest.transform.position;
                Say($"built {ChestPiece} at ({p.x:0}, {p.z:0}) for {paid} Wood");
                return;
            }
            _buildWhy = $"nothing placed at ({_buildAt.x:0}, {_buildAt.z:0})";
            Say($"{ChestPiece} at ({_buildAt.x:0}, {_buildAt.z:0}) didn't take (try {_buildTries}/{BuildTries})");
        }

        private void RememberHomeChests()
        {
            foreach (Container chest in ChestHelper.FindNearbyChests(Home, HomeRadius))
                if (chest != null) _homeChests.Add(chest.GetInstanceID());
        }

        private Container FindRoom(Dictionary<string, int> toStore)
        {
            Container best = null;
            float bestDistance = float.MaxValue;
            foreach (Container chest in ChestHelper.FindNearbyChests(Home, HomeRadius))
            {
                if (chest == null || _refusedChests.Contains(chest.GetInstanceID()) || !ChestHelper.OwnerMayWrite(chest, _body.OwnerPlayerId)) continue;
                Inventory inv = chest.GetInventory();
                if (inv == null || !toStore.Keys.Any(item => HasRoomFor(inv, item))) continue;
                float d = Vector3.Distance(_body.Position, chest.transform.position);
                if (d < bestDistance) { bestDistance = d; best = chest; }
            }
            return best;
        }

        private static bool HasRoomFor(Inventory chest, string item)
        {
            if (chest.GetEmptySlots() > 0) return true;
            foreach (ItemDrop.ItemData have in chest.GetAllItems())
                if (have.m_dropPrefab != null && have.m_dropPrefab.name.Equals(item, StringComparison.OrdinalIgnoreCase)
                    && have.m_stack < have.m_shared.m_maxStackSize) return true;
            return false;
        }

        private void Deposit(Container chest, Dictionary<string, int> toStore)
        {
            Inventory inv = chest.GetInventory();
            var moved = new List<string>();
            foreach (var kv in toStore)
            {
                int before = InventoryTransferService.CountItem(inv, kv.Key);
                InventoryTransferService.DepositItem(_body.Inventory, chest, kv.Key, kv.Value, _body.OwnerPlayerId);
                int gained = InventoryTransferService.CountItem(inv, kv.Key) - before;
                if (gained <= 0) continue;
                moved.Add($"{kv.Key} x{gained}");
                Progress.deposited.TryGetValue(kv.Key, out int total);
                Progress.deposited[kv.Key] = total + gained;
                _tripStored.TryGetValue(kv.Key, out int trip);
                _tripStored[kv.Key] = trip + gained;
            }
            Vector3 p = chest.transform.position;
            if (moved.Count == 0) _refusedChests.Add(chest.GetInstanceID());
            Say(moved.Count > 0 ? $"deposited {string.Join(", ", moved)} into {Utils.GetPrefabName(chest.gameObject)} at ({p.x:0}, {p.z:0})"
                : $"{Utils.GetPrefabName(chest.gameObject)} at ({p.x:0}, {p.z:0}) took nothing");
        }

        // What stays in the bag at home: an unfinished craft's parts, and the chest's Wood while one is owed.
        private Dictionary<string, int> Keep()
        {
            var keep = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_chestOwed) keep["Wood"] = ChestWoodCost();
            foreach (CraftPlan plan in _plans.Values)
            {
                if (plan.Done || plan.Refused || ObjectDB.instance == null) continue;
                ItemDrop drop = ObjectDB.instance.GetItemPrefab(plan.Item)?.GetComponent<ItemDrop>();
                Recipe recipe = drop != null ? ObjectDB.instance.GetRecipe(drop.m_itemData) : null;
                if (recipe == null) continue;
                foreach (Piece.Requirement req in recipe.m_resources)
                {
                    if (req.m_resItem == null) continue;
                    keep.TryGetValue(req.m_resItem.name, out int k);
                    keep[req.m_resItem.name] = k + req.GetAmount(1);
                }
            }
            return keep;
        }

        // Item prefab → units to deposit: what ChestHelper calls loot, plus food beyond the best KeepFoodStacks stacks (§2.4), less Keep().
        private Dictionary<string, int> ToDeposit(Dictionary<string, int> keep)
        {
            var units = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ItemDrop.ItemData item in ChestHelper.GetDepositableItems(_body.Inventory))
                Add(units, item);
            var food = _body.Inventory.GetAllItems().Where(i => i.m_shared.m_food > 0f && i.m_dropPrefab != null)
                .OrderByDescending(i => i.m_shared.m_food + i.m_shared.m_foodStamina + i.m_shared.m_foodEitr).ToList();
            foreach (ItemDrop.ItemData item in food.Skip(KeepFoodStacks))
                Add(units, item);
            foreach (var kv in keep)
            {
                if (!units.TryGetValue(kv.Key, out int n)) continue;
                int alreadyStaying = Carried(kv.Key) - n;          // units of it not counted above (e.g. in a kept food stack)
                units[kv.Key] = n - Math.Min(n, Math.Max(0, kv.Value - alreadyStaying));
            }
            foreach (string none in units.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList()) units.Remove(none);
            return units;
        }

        private static void Add(Dictionary<string, int> units, ItemDrop.ItemData item)
        {
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            if (string.IsNullOrEmpty(prefab)) return;
            units.TryGetValue(prefab, out int n);
            units[prefab] = n + item.m_stack;
        }

        private static int ChestWoodCost()
        {
            Piece piece = ChestPrefab();
            if (piece == null) return 10;
            foreach (Piece.Requirement req in piece.m_resources)
                if (req.m_resItem != null && req.m_resItem.name == "Wood") return req.m_amount;
            return 0;
        }

        private static Piece ChestPrefab()
        {
            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(ChestPiece) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        private static readonly int s_groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
        private static readonly int s_blockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "vehicle", "character");

        // Beside home and clear of doors (§2, agreed): 3 then 4.5 m out, 8 ways round, level with home, nothing in the way, inside the
        // build range of the station the chest needs (vanilla's rule for a player), and wards allow.
        private bool ChestSpot(out Vector3 spot, out Quaternion rot, out string why)
        {
            spot = Home;
            rot = Quaternion.identity;
            Piece chest = ChestPrefab();
            string station = chest != null && chest.m_craftingStation != null ? chest.m_craftingStation.m_name : null;
            bool anyInRange = false;
            foreach (float r in new[] { 3f, 4.5f })
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    Vector3 probe = Home + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (!Physics.Raycast(probe + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 12f, s_groundMask)) continue;
                    if (Mathf.Abs(hit.point.y - Home.y) > 1.5f || hit.normal.y < 0.8f) continue;
                    if (station != null && CraftingStation.HaveBuildStationInRange(station, hit.point) == null) continue;
                    anyInRange = true;
                    Vector3 face = Home - hit.point;
                    face.y = 0f;
                    Quaternion q = face.sqrMagnitude > 0.01f ? Quaternion.LookRotation(face.normalized) : Quaternion.identity;
                    if (Physics.CheckBox(hit.point + Vector3.up * 0.6f, new Vector3(0.85f, 0.5f, 0.55f), q, s_blockMask)) continue;
                    if (Physics.OverlapSphere(hit.point, 2.5f).Any(c => c != null && c.GetComponentInParent<Door>() != null)) continue;
                    if (!ChestHelper.WardsAllow(hit.point, _body.OwnerPlayerId)) continue;
                    if (Hazards.Inside(hit.point, out _)) continue;   // not in the campfire's flames
                    spot = hit.point;
                    rot = q;
                    why = "";
                    return true;
                }
            why = station != null && !anyInRange
                ? $"no {Localization.instance.Localize(station)} in build range of home, so {ChestPiece} can't be placed"
                : $"no clear spot for {ChestPiece} within 5 m of home";
            return false;
        }

        // ---- Walking ----

        // A walk order toward goal, in legs of LegMetres; remembers the best distance so Stalled() can tell no progress.
        private TaskOrder MoveTo(Vector3 goal, GameObject target)
        {
            // Never into a fire's damage (Hazards): a companion's vanilla pathing doesn't know them, and home is often by the campfire.
            goal = Hazards.SafeGoal(goal, _body.Position, out _);
            float d = Flat(_body.Position, goal);
            if ((goal - _moveGoal).sqrMagnitude > 1f) { _moveGoal = goal; _moveBest = d; _moveBestAt = Time.time; }
            else if (d < _moveBest - 1f) { _moveBest = d; _moveBestAt = Time.time; }
            Vector3 point = goal;
            if (d > LegMetres)
            {
                Vector3 flat = goal - _body.Position;
                flat.y = 0f;
                point = _body.Position + flat.normalized * LegMetres;
                point.y = goal.y;
            }
            return new TaskOrder { Kind = TaskKind.Move, Point = point, Target = target };
        }

        private bool Stalled() => !float.IsInfinity(_moveGoal.x) && Time.time - _moveBestAt > NoProgressSeconds;
        private void ResetMove() => _moveGoal = Vector3.positiveInfinity;

        // ---- Bookkeeping ----

        private void OpenTrip()
        {
            _tripOpen = true;
            _tripStored.Clear();
            Progress.Trips++;
            Trips.RaiseStarted(new Trips.TripInfo(Session, Progress.Trips, Name, "haul", NetTime(), _body.Character));
        }

        private void CloseTrip(string why)
        {
            _tripOpen = false;
            Trips.RaiseEnded(new Trips.TripInfo(Session, Progress.Trips, Name, why, NetTime(), _body.Character));
        }

        private void UpdateGathered()
        {
            foreach (string item in _quota.Keys.Concat(_foodSeen))
            {
                _startCarried.TryGetValue(item, out int start);
                Progress.deposited.TryGetValue(item, out int stored);
                Progress.gathered[item] = Math.Max(0, Carried(item) + stored - start);
            }
        }

        private bool QuotaDeposited() => _quota.All(kv => Progress.deposited[kv.Key] >= kv.Value);
        private string QuotaLine() => string.Join(", ", _quota.Select(kv => $"{kv.Key} {Progress.deposited[kv.Key]}/{kv.Value}"));

        private TaskOrder GiveUp(string why)
        {
            Finish("gave up", "gave up: " + why);
            return TaskOrder.Failed(why);
        }

        private void Finish(string state, string line)
        {
            Active = false;
            _endReason = line;
            Progress.State = state;
            if (_tripOpen) CloseTrip(line);
            Say(line);
            Publish(force: true);
        }

        // ---- The progress on the body's ZDO, for a judge on another peer (Haul.ReadPublished) ----

        private string _published = "";
        private float _nextPublish;

        private void Publish(bool force)
        {
            if (!force && Time.time < _nextPublish) return;
            ZNetView nview = _body.Character != null ? _body.Character.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            string text = string.Join("|", Progress.State, Progress.Trips, Progress.ChestsBuilt,
                string.Join(",", Progress.gathered.Keys.Select(k => $"{k}:{Progress.gathered[k]}/{(Progress.deposited.TryGetValue(k, out int d) ? d : 0)}")),
                Progress.LastLine.Replace('|', '/'));
            if (text == _published) return;
            _published = text;
            _nextPublish = Time.time + 1f;
            nview.GetZDO().Set(Haul.ZdoKey, text);
        }

        private int Carried(string item) => InventoryTransferService.CountItem(_body.Inventory, item);
        private float Weight() => _body.Inventory != null ? _body.Inventory.GetTotalWeight() : 0f;
        private float Max()
        {
            float max = _maxWeight != null ? _maxWeight() : _body.Character is Player p ? p.GetMaxCarryWeight() : 300f;
            return max > 0f ? max : 300f;
        }
        private float Share() => Weight() / Max();

        private string Name => _body.Character is Player p ? p.GetPlayerName() : _body.Character != null ? _body.Character.m_name : "?";

        private void Say(string text) => Log($"[Haul] {Name} {text}");

        private void Log(string line)
        {
            Progress.LastLine = line;
            Debug.Log(line);
        }

        private static double NetTime() => ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : Time.time;

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static HashSet<string> FoodItems()
        {
            if (s_foodItems != null && s_foodItems.Count > 0) return s_foodItems;
            s_foodItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ObjectDB.instance != null)
                foreach (GameObject go in ObjectDB.instance.m_items)
                {
                    ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                    if (drop != null && drop.m_itemData.m_shared.m_food > 0f) s_foodItems.Add(go.name);
                }
            return s_foodItems;
        }
    }
}
