using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Hunting / fishing trips (Fire 10-24, design Tools\DESIGN_companion_trips_20261024.md + Fire's answers: 300 m, shared loot into
    /// the home network, raw loot home, on command plus an optional "every morning" schedule).
    /// A trip: walk out to the spot (40 m legs), work there - Hunt: the nearest prey of the chosen kind (or any huntable game) inside
    /// the area is force-targeted the way the Attack command does it, then its drops are picked up; Fish: the companion's own
    /// FishingBehavior runs session after session at the spot - until the bag is full, the time limit (one in-game day), health under
    /// 40 %, or 5 min without a target; then walk home, unload into the chests there (ChestHelper.SmartDeposit) and report in chat.
    /// A party is simply up to 3 of the owner's companions on trips at once; each unloads into the same home network.
    /// </summary>
    public class TripBehavior : IdleSubBehavior
    {
        public enum TripKind { Hunt, Fish, Mine }
        private enum Phase { Out, Working, Collecting, Home, Unloading, Done }

        public const float DefaultArea = 60f;
        /// <summary>[Trips] Max range from home (default 1000 m): with the server keeping trip areas alive the owner's distance no longer matters (Fire 10-24).</summary>
        public static float MaxRange => s_maxRange != null ? s_maxRange.Value : 1000f;
        private static BepInEx.Configuration.ConfigEntry<float> s_maxRange;
        public const int MaxParty = 3;
        private const float LegLength = 40f, ArriveDist = 4f, NoTargetGiveUp = 300f, LowHealth = 0.4f, LegStuck = 25f;
        private const float DropRadius = 8f, PickupDist = 1.6f, CollectTimeout = 20f, ScheduleFrom = 0.22f, ScheduleTo = 0.40f;
        private const string PlanKey = "companion_tripplan", PlanDayKey = "companion_tripday";

        public override string BehaviorName => "Trip";
        public override bool AvailableForIdleRotation => true;      // only the schedule starts it idle (CanStart is false otherwise)
        public override bool SupportsResumption => true;            // a hunt IS combat: paused while fighting, resumed after

        private TripKind _kind;
        private string _prey;            // prefab name, or "" = any huntable game
        private Vector3 _spot, _home;
        private float _area = DefaultArea;
        private bool _armed, _scheduledRun;
        private Phase _phase;
        private float _phaseStart, _workStart, _lastTargetTime, _legStart, _timeLimit;
        private Vector3 _legEnd;
        private Character _target;
        private readonly Dictionary<string, int> _kills = new Dictionary<string, int>();
        private Dictionary<string, int> _bagBefore = new Dictionary<string, int>();
        private readonly List<ItemDrop> _drops = new List<ItemDrop>();
        private FishingBehavior _fishing;
        private string _stopWhy = "";
        private float _tripStart;
        private float _deadline;

        /// <summary>The base hands back half of MaxDuration after a fight; a trip keeps its own deadline instead.</summary>
        public override void ResumeAfterCombat()
        {
            float maxBefore = MaxDuration;
            base.ResumeAfterCombat();
            MaxDuration = Mathf.Max(60f, _deadline - Time.time + maxBefore * 0.5f);
        }
        private static readonly Dictionary<Character, float> s_claimedPrey = new Dictionary<Character, float>();

        // ---- starting ------------------------------------------------------------------------------------------------------
        /// <summary>Arms the next start (command or schedule). False with a reason when the trip can't go.</summary>
        public bool Arm(TripKind kind, Vector3 spot, string prey, float area, out string why)
        {
            why = null;
            Vector3 home = IdleBehavior != null && IdleBehavior.HasHomePosition ? IdleBehavior.HomePosition
                         : (FiresCore.Npc.AI.HomeBase.TryGet(out var hb, out _) ? hb : Transform.position);
            float d = Vector3.Distance(new Vector3(home.x, 0f, home.z), new Vector3(spot.x, 0f, spot.z));
            if (d > MaxRange) { why = $"that's {d:0} m from home; trips go up to {MaxRange:0} m"; return false; }
            int onTrips = CompanionController.AllCompanions.Count(c => c != null && c != Companion && c.ownerPlayerId == Companion.ownerPlayerId
                && c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior is TripBehavior);
            if (onTrips >= MaxParty) { why = $"{onTrips} companions are already on trips (a party is {MaxParty})"; return false; }
            if (kind == TripKind.Fish)
            {
                var probe = NewFishing();
                probe.SetCommandedSpot(spot);
                if (!HasFishingGear()) { why = "no fishing rod and bait to take"; return false; }
            }
            if (kind == TripKind.Mine && !HasPickaxe())
            {
                why = "no pickaxe to take"; return false;
            }
            _kind = kind; _spot = spot; _home = home; _prey = prey ?? ""; _area = Mathf.Clamp(area, 30f, 100f); _armed = true;
            return true;
        }

        public override bool CanStart()
        {
            if (Companion == null) return false;
            if (_armed) return true;
            // the daily schedule: once a day in the morning window, from the plan saved on the companion
            if (!TryReadPlan(out var kind, out var spot, out var prey, out var area)) return false;
            if (EnvMan.instance == null || !CanStartBase()) return false;
            float f = EnvMan.instance.GetDayFraction();
            if (f < ScheduleFrom || f > ScheduleTo) return false;
            int today = EnvMan.instance.GetDay();
            var zdo = Companion.GetComponent<ZNetView>()?.GetZDO();
            if (zdo == null || zdo.GetInt(PlanDayKey, -1) == today) return false;
            if (!Arm(kind, spot, prey, area, out string why))
            {
                zdo.Set(PlanDayKey, today);   // not again today
                FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: scheduled {kind} skipped today ({why})");
                return false;
            }
            zdo.Set(PlanDayKey, today);
            _scheduledRun = true;
            return true;
        }

        public override void Start()
        {
            base.Start();
            _armed = false;
            _timeLimit = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;
            MaxDuration = _timeLimit + 900f;   // the work limit + walking out and back
            _deadline = Time.time + MaxDuration;
            bool resumed = _resumePhase.HasValue;
            _kills.Clear(); _drops.Clear(); _target = null; _stopWhy = ""; _skipRocks.Clear(); if (!resumed) _teleports = 0; _walkWatch.Reset();
            _bagBefore = BagCounts();
            _tripStart = Time.time;
            bool homeward = resumed && (_resumePhase == Phase.Home || _resumePhase == Phase.Unloading);
            Companion.CommandStay(homeward ? _home : _spot);      // its stay point is where it works: no follow leash / recall pulls it back to the owner
            string what = _kind == TripKind.Hunt ? (_prey.Length > 0 ? $"hunt {_prey}" : "hunt any game") : _kind == TripKind.Mine ? "mine ore" : "fish";
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: {what} at ({_spot.x:0},{_spot.z:0}) area {_area:0} m, home ({_home.x:0},{_home.z:0}), limit {_timeLimit:0} s{(_scheduledRun ? " (scheduled)" : "")}");
            if (resumed) FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: trip resumed here ({_resumePhase}, {(ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client")} simulates it now)");
            else CompanionChatHelper.SayStatus(Companion, $"Off to {what} - back by nightfall.");
            FiresCore.Npc.AI.Trips.RaiseStarted(new FiresCore.Npc.AI.Trips.TripInfo("companion-trip", 1, Companion.companionName, what, ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0, Companion.GetComponent<Character>()));
            SetPhase(_resumePhase ?? Phase.Out);
            if (_resumePhase.HasValue && _resumePhase != Phase.Out && _resumePhase != Phase.Home) { _workStart = Time.time; _lastTargetTime = Time.time; }
            _resumePhase = null;
        }

        // ---- loop ----------------------------------------------------------------------------------------------------------
        public override bool Update()
        {
            if (!IsActive) return !WasInterruptedByCombat;   // paused by a fight (the hunt itself): not done
            var me = Companion != null ? Companion.GetComponent<Character>() : null;
            if (me == null || me.IsDead()) { Finish("down"); return true; }
            switch (_phase)
            {
                case Phase.Out:
                    if (Walk(_spot)) { _workStart = Time.time; _lastTargetTime = Time.time; SetPhase(Phase.Working); }
                    break;
                case Phase.Working:
                case Phase.Collecting:
                    string stop = StopReason(me);
                    if (stop != null) { EndWork(stop); break; }
                    if (_kind == TripKind.Fish) UpdateFishing(); else if (_kind == TripKind.Mine) UpdateMining(); else UpdateHunting();
                    break;
                case Phase.Home:
                    if (Walk(_home)) SetPhase(Phase.Unloading);
                    break;
                case Phase.Unloading:
                    if (UpdateUnload()) { Finish(_stopWhy); return true; }
                    break;
                case Phase.Done:
                    return true;
            }
            return false;
        }

        private string StopReason(Character me)
        {
            if (IsInventoryCompletelyFull()) return "bag full";
            if (Time.time - _workStart > _timeLimit) return "time's up";
            if (me.GetHealthPercentage() < LowHealth) return $"hurt ({me.GetHealthPercentage() * 100f:0} % health)";
            if (Time.time - _lastTargetTime > NoTargetGiveUp) return _kind == TripKind.Hunt ? "no game left here" : _kind == TripKind.Mine ? "no ore left here" : "nothing biting";
            return null;
        }

        private void EndWork(string why)
        {
            _stopWhy = why;
            if (_fishing != null && _fishing.IsActive) _fishing.Cancel();
            if (_mining != null && _mining.IsActive) _mining.Cancel();
            ReleaseTarget();
            Companion.CommandStay(_home);
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: heading home ({why})");
            SetPhase(Phase.Home);
        }

        // ---- hunt ----------------------------------------------------------------------------------------------------------
        private void UpdateHunting()
        {
            if (_phase == Phase.Collecting) { UpdateCollecting(); return; }
            if (_target != null)
            {
                if (_target.IsDead() || !_target.gameObject.activeInHierarchy)
                {
                    string n = Localization.instance != null ? Localization.instance.Localize(_target.m_name) : _target.m_name;
                    _kills.TryGetValue(n, out int k); _kills[n] = k + 1;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: killed {n}; collecting drops");
                    Vector3 at = _target.transform.position;
                    ReleaseTarget();
                    _lastTargetTime = Time.time;
                    StartCollecting(at);
                    return;
                }
                if (Vector3.Distance(_target.transform.position, _spot) > _area + 30f) { ReleaseTarget(); return; }   // it fled the area
                _lastTargetTime = Time.time;
                if (Vector3.Distance(Transform.position, _target.transform.position) > 6f) TryMoveToPosition(_target.transform.position, walk: false, run: true);
                return;
            }
            var prey = FindPrey();
            if (prey == null)
            {
                // wander the area so game nearby comes into view
                if (Time.time - _phaseStart > 20f) { _phaseStart = Time.time; TryMoveToPosition(RandomIn(_spot, _area * 0.6f)); }
                return;
            }
            _target = prey;
            s_claimedPrey[prey] = Time.time;
            Companion.GetComponent<CompanionCombatMovement>()?.SetPriorityTarget(prey, 60f);
            Companion.ForceTarget(prey);
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: hunting {Utils.GetPrefabName(prey.gameObject)} {Vector3.Distance(Transform.position, prey.transform.position):0} m off");
        }

        private Character FindPrey()
        {
            Character best = null; float bestD = float.MaxValue;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c.IsPlayer() || c.IsTamed() || c.GetComponent<CompanionController>() != null) continue;
                if (Vector3.Distance(c.transform.position, _spot) > _area) continue;
                string prefab = Utils.GetPrefabName(c.gameObject);
                if (_prey.Length > 0 ? !string.Equals(prefab, _prey, System.StringComparison.OrdinalIgnoreCase)
                                     : !(c.m_faction == Character.Faction.AnimalsVeg || HuntListConfig.IsHuntable((c.m_name ?? "").ToLower(), prefab.ToLower()))) continue;
                if (s_claimedPrey.TryGetValue(c, out float t) && Time.time - t < 60f) continue;   // a party member is on it
                if (!ChestHelper.WardsAllow(c.transform.position, Companion)) continue;
                float d = Vector3.Distance(Transform.position, c.transform.position);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private void ReleaseTarget()
        {
            if (_target != null) s_claimedPrey.Remove(_target);
            _target = null;
            var ai = Companion != null ? Companion.GetComponent<FiresCore.Npc.AI.CompanionAI>() : null;
            ai?.ClearForceTarget();
        }

        private void StartCollecting(Vector3 at)
        {
            _drops.Clear();
            foreach (var col in Physics.OverlapSphere(at, DropRadius))
            {
                var d = col != null ? col.GetComponentInParent<ItemDrop>() : null;
                if (d != null && !_drops.Contains(d) && d.CanPickup(true)) _drops.Add(d);
            }
            SetPhase(Phase.Collecting);
        }

        private void UpdateCollecting()
        {
            if (Time.time - _phaseStart < 1f) return;   // drops spawn a moment after the death
            if (Time.time - _phaseStart > CollectTimeout) _drops.Clear();
            _drops.RemoveAll(d => d == null || !d.gameObject.activeInHierarchy);
            if (_drops.Count == 0)
            {
                if (Time.time - _phaseStart < 2.5f) return;
                SetPhase(Phase.Working); return;
            }
            var next = _drops[0];
            if (Vector3.Distance(Transform.position, next.transform.position) > PickupDist) { TryMoveToPosition(next.transform.position); return; }
            StopMovement();
            var storage = Companion.GetInventory()?.GetStorageInventory();
            if (storage != null && ChestHelper.TryTakeLooseItem(next, storage) > 0) { Companion.GetInventory().SaveToZDO(); _drops.RemoveAt(0); }
            else if (next.CanPickup()) _drops.RemoveAt(0);   // no room / not ours: skip it
        }

        // ---- mine (Fire 10-24: "we could also send them off on mining trips") ------------------------------------------------
        // The companion's own ResourceGatheringBehavior strikes each deposit (pickaxe from its bag or chests, tier checks); the trip
        // only picks the next deposit inside the area and keeps count.
        private ResourceGatheringBehavior _mining;
        private float _nextRockScan;
        private readonly HashSet<int> _skipRocks = new HashSet<int>();

        private bool HasPickaxe()
        {
            var storage = Companion.GetInventory()?.GetStorageInventory();
            var hand = Companion.GetComponent<Humanoid>()?.GetRightItem();
            var items = new List<ItemDrop.ItemData>();
            if (storage != null) items.AddRange(storage.GetAllItems());
            if (hand != null) items.Add(hand);
            return ResourceDataHelper.BestToolTier(items, ResourceDataHelper.ToolType.Pickaxe) >= 0;
        }

        private void UpdateMining()
        {
            if (_mining == null) { _mining = new ResourceGatheringBehavior(); _mining.Initialize(Companion, IdleBehavior); }
            if (_mining.IsActive)
            {
                if (_mining.Update()) { _lastTargetTime = Time.time; }   // one deposit done (or given up); the next scan picks another
                return;
            }
            if (Time.time < _nextRockScan) return;
            _nextRockScan = Time.time + 3f;
            GameObject best = null; float bestD = float.MaxValue;
            foreach (var col in Physics.OverlapSphere(_spot, _area))
            {
                if (col == null) continue;
                var rock = (Component)col.GetComponentInParent<MineRock5>() ?? col.GetComponentInParent<MineRock>();
                if (rock == null || _skipRocks.Contains(rock.gameObject.GetInstanceID())) continue;
                if (!ChestHelper.WardsAllow(rock.transform.position, Companion)) continue;
                float d = Vector3.Distance(Transform.position, rock.transform.position);
                if (d < bestD) { bestD = d; best = rock.gameObject; }
            }
            if (best == null)
            {
                if (Time.time - _phaseStart > 20f) { _phaseStart = Time.time; TryMoveToPosition(RandomIn(_spot, _area * 0.6f)); }
                return;
            }
            string why = ResourceDataHelper.WhyNotGatherable(best);
            _mining.SetCommandedTarget(best);
            if (!string.IsNullOrEmpty(why) || !_mining.CanStart())
            {
                _skipRocks.Add(best.GetInstanceID());
                FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: skipping {Utils.GetPrefabName(best)} ({(string.IsNullOrEmpty(why) ? "can't start mining it" : why)})");
                return;
            }
            ReleaseMovementAuthority();   // the gathering run moves the body now
            _mining.Start();
            _lastTargetTime = Time.time;
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: mining {Utils.GetPrefabName(best)} {bestD:0} m off");
        }

        // ---- fish ----------------------------------------------------------------------------------------------------------
        private FishingBehavior NewFishing()
        {
            if (_fishing == null) { _fishing = new FishingBehavior(); _fishing.Initialize(Companion, IdleBehavior); }
            return _fishing;
        }

        private bool HasFishingGear()
        {
            var storage = Companion.GetInventory()?.GetStorageInventory();
            var hand = Companion.GetComponent<Humanoid>()?.GetRightItem();
            var rod = FiresCore.Npc.AI.FishingBrain.RodIn(storage) ?? (hand != null && hand.m_shared.m_name.Contains("fishingrod") ? hand : null);
            return rod != null && storage != null && FiresCore.Npc.AI.FishingBrain.BestBait(storage, null, Transform.position, rod) != null;
        }

        private void UpdateFishing()
        {
            var f = NewFishing();
            if (!f.IsActive)
            {
                f.SetCommandedSpot(_spot);
                if (!f.CanStart()) { EndWork("no rod or bait left"); return; }
                ReleaseMovementAuthority();   // the fishing session moves the body now
                f.Start();
                return;
            }
            if (f.Update())
            {
                // a session ended (its own cast / catch caps); a catch lands in the bag
                if (BagGained()) _lastTargetTime = Time.time;
            }
        }

        private int _lastBagTotal = -1;
        private bool BagGained()
        {
            int total = BagCounts().Values.Sum();
            bool gained = _lastBagTotal >= 0 && total > _lastBagTotal;
            _lastBagTotal = total;
            return gained;
        }

        // ---- home + unload -------------------------------------------------------------------------------------------------
        private Container _chest;
        private bool UpdateUnload()
        {
            var inv = Companion.GetInventory();
            if (inv == null) return true;
            if (_chest == null)
            {
                var chests = ChestHelper.FindNearbyChests(_home, 30f);
                if (chests.Count == 0) { _stopWhy += "; no chest at home, kept it in my bag"; return true; }
                _chest = chests.OrderBy(c => Vector3.Distance(Transform.position, c.transform.position)).First();
            }
            Vector3 stand = InteractionPointHelper.GetContainerInteractionPoint(_chest, Transform.position);
            if (Vector3.Distance(Transform.position, stand) > 2.5f && Time.time - _phaseStart < 40f) { TryMoveToPosition(stand); return false; }
            StopMovement();
            var res = ChestHelper.SmartDeposit(inv, _chest.transform.position, 30f);
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: unloaded {res.ItemsDeposited} item(s) into the home chests ({res.Message})");
            return true;
        }

        private void Finish(string why)
        {
            if (_fishing != null && _fishing.IsActive) _fishing.Cancel();
            if (_mining != null && _mining.IsActive) _mining.Cancel();
            ReleaseTarget();
            string report = Report(why);
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion?.companionName}: {report}");
            if (Companion != null)
            {
                CompanionChatHelper.SaySpeechBubble(Companion, report, 8f);
                if (Player.m_localPlayer != null && Companion.IsOwner(Player.m_localPlayer))
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"{Companion.GetDisplayName()}: {report}");
            }
            FiresCore.Npc.AI.Trips.RaiseEnded(new FiresCore.Npc.AI.Trips.TripInfo("companion-trip", 1, Companion?.companionName, why, ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0, Companion?.GetComponent<Character>()));
            _scheduledRun = false; _chest = null; _lastBagTotal = -1;
            ClearTrip();
            _phase = Phase.Done;
            Complete();
        }

        private string Report(string why)
        {
            var now = BagCounts();
            var gained = new List<string>();
            foreach (var kv in now) { _bagBefore.TryGetValue(kv.Key, out int was); if (kv.Value > was) gained.Add($"{kv.Value - was} {kv.Key}"); }
            string kills = _kills.Count > 0 ? string.Join(", ", _kills.Select(k => $"{k.Value} {k.Key}")) : "";
            string what = _kind == TripKind.Hunt ? "hunting" : _kind == TripKind.Mine ? "mining" : "fishing";
            int mins = Mathf.RoundToInt((Time.time - _tripStart) / 60f);
            if (s_showStuck != null && s_showStuck.Value && _teleports > 0) why += $", stuck {_teleports}x";
            return $"Back from {what} at ({_spot.x:0},{_spot.z:0}) after {mins} min ({why})"
                 + (kills.Length > 0 ? $": {kills}" : "")
                 + (gained.Count > 0 ? $" - brought {string.Join(", ", gained)}" : " - brought nothing");
        }

        private Dictionary<string, int> BagCounts()
        {
            var d = new Dictionary<string, int>();
            var storage = Companion?.GetInventory()?.GetStorageInventory();
            if (storage == null) return d;
            foreach (var it in storage.GetAllItems())
            {
                string n = Localization.instance != null ? Localization.instance.Localize(it.m_shared.m_name) : it.m_shared.m_name;
                d.TryGetValue(n, out int c); d[n] = c + it.m_stack;
            }
            return d;
        }

        // ---- walking (40 m legs; vanilla FindPath can't plan 300 m in one go) ------------------------------------------------
        // ---- stuck failsafe (Fire 10-24: "if a companion gets stuck for x amount of time on the way to the trip destination or home it
        // can teleport to a safe spot"). No progress of 2 m toward the destination for [Trips] Stuck seconds (the same ProgressWatch the
        // intent view shows) -> the shared companion teleport to a safe spot ahead along the way; after [Trips] Stuck teleports per trip
        // the trip ends at home ("stuck on the way, came home"). The count stays in the Debug log; reports show it only with
        // [Trips] Show stuck count.
        private static BepInEx.Configuration.ConfigEntry<float> s_stuckSeconds;
        private static BepInEx.Configuration.ConfigEntry<int> s_stuckTeleports;
        private static BepInEx.Configuration.ConfigEntry<bool> s_showStuck;
        private readonly FiresCore.Npc.Core.ProgressWatch _walkWatch = new FiresCore.Npc.Core.ProgressWatch(2f);
        private int _teleports;
        public int StuckTeleports => _teleports;

        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg)
        {
            s_maxRange = cfg.Bind("Trips", "Max range from home", 1000f, new BepInEx.Configuration.ConfigDescription("How far from home a trip may go (m). The server keeps trip areas running with no player near (FGN Companion Keep-Alive).", new BepInEx.Configuration.AcceptableValueRange<float>(100f, 5000f)));
            s_stuckSeconds = cfg.Bind("Trips", "Stuck seconds", 60f, new BepInEx.Configuration.ConfigDescription("A companion on its way to a trip or home that gets no closer (2 m) for this long is moved to a safe spot ahead.", new BepInEx.Configuration.AcceptableValueRange<float>(15f, 600f)));
            s_stuckTeleports = cfg.Bind("Trips", "Stuck teleports", 3, new BepInEx.Configuration.ConfigDescription("Safe-spot moves per trip before the trip ends at home.", new BepInEx.Configuration.AcceptableValueRange<int>(0, 10)));
            s_showStuck = cfg.Bind("Trips", "Show stuck count", false, "Show how many times a companion got stuck in its trip report (always in the Debug log).");
        }

        private bool HandleStuck(Vector3 to)
        {
            Vector3 p = Transform.position;
            float stuck = _walkWatch.Seconds;
            _walkWatch.Reset();
            _teleports++;
            string dest = _phase == Phase.Out ? $"the trip center ({to.x:0},{to.z:0})" : $"home ({to.x:0},{to.z:0})";
            int cap = s_stuckTeleports != null ? s_stuckTeleports.Value : 3;
            if (_teleports > cap)
            {
                Vector3 home = SafeSpotNear(_home, out string whyHome) ?? _home;
                Companion.TeleportForTrip(home);
                _stopWhy = "stuck on the way, came home";
                if (_fishing != null && _fishing.IsActive) _fishing.Cancel();
                if (_mining != null && _mining.IsActive) _mining.Cancel();
                ReleaseTarget();
                Companion.CommandStay(_home);
                FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: stuck {stuck:0} s at ({p.x:0},{p.z:0}) heading {dest}, {_teleports - 1} safe-spot move(s) used; teleported home ({home.x:0},{home.z:0}) ({whyHome ?? "home"}), trip ends");
                SetPhase(Phase.Unloading);
                return true;
            }
            Vector3 dir = to - p; dir.y = 0f;
            Vector3? spot = null; string why = null;
            foreach (float ahead in new[] { 12f, 24f, 36f })
            {
                if (dir.magnitude < ahead) break;
                foreach (float ang in new[] { 0f, 30f, -30f, 60f, -60f })
                {
                    Vector3 c = p + Quaternion.Euler(0f, ang, 0f) * dir.normalized * ahead;
                    spot = SafeSpotAt(c, out why);
                    if (spot != null) break;
                }
                if (spot != null) break;
            }
            if (spot == null) spot = SafeSpotNear(to, out why);
            if (spot == null)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: stuck {stuck:0} s at ({p.x:0},{p.z:0}) heading {dest}; no safe spot found yet, keeps trying");
                return false;
            }
            Companion.TeleportForTrip(spot.Value);
            _legEnd = Vector3.zero;
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {Companion.companionName}: stuck {stuck:0} s at ({p.x:0},{p.z:0}) heading {dest}; teleported to ({spot.Value.x:0},{spot.Value.z:0}) ({why}) [{_teleports}/{cap}]");
            return true;
        }

        private Vector3? SafeSpotNear(Vector3 c, out string why)
        {
            why = null;
            for (int i = 0; i < 12; i++)
            {
                var s = SafeSpotAt(i == 0 ? c : c + new Vector3(Random.Range(-6f, 6f), 0f, Random.Range(-6f, 6f)), out why);
                if (s != null) return s;
            }
            return null;
        }

        private static readonly int s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        /// <summary>Safe = in a loaded zone, on solid ground, not deeper in water than wading, not inside a piece / rock, no hostile within 15 m.</summary>
        private Vector3? SafeSpotAt(Vector3 c, out string why)
        {
            why = null;
            var zs = ZoneSystem.instance;
            if (zs == null || !zs.IsZoneLoaded(c)) return null;
            if (!zs.GetSolidHeight(c, out float h)) return null;
            c.y = h;
            if (h < zs.m_waterLevel - 0.6f) return null;
            if (Physics.CheckCapsule(c + Vector3.up * 0.6f, c + Vector3.up * 1.6f, 0.4f, s_solidMask, QueryTriggerInteraction.Ignore)) return null;
            var me = Companion.GetComponent<Character>();
            foreach (var ch in Character.GetAllCharacters())
                if (ch != null && ch != me && !ch.IsDead() && !ch.IsPlayer() && !ch.IsTamed() && Vector3.Distance(ch.transform.position, c) < 15f && BaseAI.IsEnemy(me, ch))
                    return null;
            why = "on ground, dry, clear, no hostile within 15 m, zone loaded";
            return c;
        }

        private bool Walk(Vector3 to)
        {
            Vector3 p = Transform.position;
            if (Flat(p - to) <= ArriveDist) { StopMovement(); return true; }
            if (_walkWatch.Tick(p, to) > (s_stuckSeconds != null ? s_stuckSeconds.Value : 60f) && HandleStuck(to)) return false;
            if (_legEnd == Vector3.zero || Flat(p - _legEnd) <= ArriveDist || Time.time - _legStart > LegStuck)
            {
                bool stuck = _legEnd != Vector3.zero && Time.time - _legStart > LegStuck;
                Vector3 dir = to - p; dir.y = 0f;
                float step = Mathf.Min(LegLength, dir.magnitude);
                Vector3 e = p + dir.normalized * step;
                if (stuck) e += new Vector3(Random.Range(-8f, 8f), 0f, Random.Range(-8f, 8f));   // side-step a blocked leg
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(e, out float h)) e.y = h;
                _legEnd = e; _legStart = Time.time;
            }
            TryMoveToPosition(_legEnd, walk: false, run: false);
            return false;
        }

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }
        private static Vector3 RandomIn(Vector3 c, float r) { var o = Random.insideUnitCircle * r; return c + new Vector3(o.x, 0f, o.y); }

        private void SetPhase(Phase p) { _phase = p; _phaseStart = Time.time; _legEnd = Vector3.zero; SaveTrip(); }

        // ---- a trip that outlives its simulator (keep-alive, Fire 10-24: "the companion can BE its own player") ------------------
        // The trip is written on the companion's ZDO at every phase; whoever owns the ZDO (the player's client, or the server while FGN
        // keeps the area alive) resumes it (CompanionKeepAlive's tick). The server keeps its own table from TripState RPCs.
        internal const string TripKey = "companion_trip";
        internal bool Yielding;   // set when the ZDO moved to another simulator: stop here without ending the trip

        private void SaveTrip()
        {
            var zdo = Companion != null ? Companion.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo == null || _phase == Phase.Done) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string line = string.Join("|", _kind, _prey.Replace("|", ""), _spot.x.ToString("0.#", ci), _spot.y.ToString("0.#", ci), _spot.z.ToString("0.#", ci),
                _area.ToString("0", ci), _home.x.ToString("0.#", ci), _home.y.ToString("0.#", ci), _home.z.ToString("0.#", ci), _phase, _teleports.ToString(ci));
            zdo.Set(TripKey, line);
            FiresCore.Npc.Core.CompanionKeepAlive.NotifyTrip(Companion, true, line);
        }

        private void ClearTrip()
        {
            var zdo = Companion != null ? Companion.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo != null && !string.IsNullOrEmpty(zdo.GetString(TripKey, ""))) zdo.Set(TripKey, "");
            FiresCore.Npc.Core.CompanionKeepAlive.NotifyTrip(Companion, false, "");
        }

        /// <summary>Picks a saved trip up where its last simulator left it. False when the saved line is bad.</summary>
        internal bool Resume(string saved)
        {
            var p = (saved ?? "").Split('|');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length < 11 || !System.Enum.TryParse(p[0], out TripKind kind) || !System.Enum.TryParse(p[9], out Phase phase)) return false;
            try
            {
                _kind = kind; _prey = p[1];
                _spot = new Vector3(float.Parse(p[2], ci), float.Parse(p[3], ci), float.Parse(p[4], ci)); _area = float.Parse(p[5], ci);
                _home = new Vector3(float.Parse(p[6], ci), float.Parse(p[7], ci), float.Parse(p[8], ci)); _teleports = int.Parse(p[10], ci);
            }
            catch { return false; }
            _resumePhase = phase == Phase.Collecting ? Phase.Working : phase;
            _armed = true;
            return true;
        }
        private Phase? _resumePhase;

        public override void Cancel()
        {
            if (_fishing != null && _fishing.IsActive) _fishing.Cancel();
            if (_mining != null && _mining.IsActive) _mining.Cancel();
            ReleaseTarget();
            _armed = false; _scheduledRun = false; _chest = null;
            if (!Yielding) ClearTrip();
            Yielding = false;
            base.Cancel();
        }

        // ---- read-only trip state for the map view (Fire 10-24: "show where they are and what stage of progress") ---------------
        public bool OnTrip => IsActive || WasInterruptedByCombat;
        public Vector3 TripCenter => _spot;
        public float TripArea => _area;
        public Vector3 TripHome => _home;
        public float TripMinutes => (Time.time - _tripStart) / 60f;
        public string TripStage
        {
            get
            {
                switch (_phase)
                {
                    case Phase.Out: return "Travelling to site";
                    case Phase.Working:
                    case Phase.Collecting:
                        if (_kind == TripKind.Hunt) { int k = _kills.Values.Sum(); return k > 0 ? $"Hunting ({k} {(k == 1 ? "kill" : "kills")} so far)" : "Hunting"; }
                        return _kind == TripKind.Mine ? "Mining" : "Fishing";
                    case Phase.Home: return "Travelling home";
                    case Phase.Unloading: return "Unloading";
                    default: return "Done";
                }
            }
        }
        /// <summary>The stuck count, only when [Trips] Show stuck count is on (Fire: "normally we would not show that").</summary>
        public string StuckNote => s_showStuck != null && s_showStuck.Value && _teleports > 0 ? $", stuck {_teleports}x" : "";

        public override string GetStatusDescription()
        {
            string what = _kind == TripKind.Hunt ? "Hunting" : _kind == TripKind.Mine ? "Mining" : "Fishing";
            switch (_phase)
            {
                case Phase.Out: return $"{what} trip: heading out";
                case Phase.Working: return _kind == TripKind.Hunt && _target != null ? $"Hunting {Utils.GetPrefabName(_target.gameObject)}" : $"{what}";
                case Phase.Collecting: return "Collecting the kill";
                case Phase.Home: return $"{what} trip: heading home";
                case Phase.Unloading: return "Unloading the catch";
                default: return $"{what} trip";
            }
        }

        // ---- the daily plan (saved on the companion) ---------------------------------------------------------------------
        /// <summary>Saves "every morning" for this trip on the companion (cleared with <see cref="ClearPlan"/>).</summary>
        public static void SavePlan(CompanionController c, TripKind kind, Vector3 spot, string prey, float area)
        {
            var zdo = c != null ? c.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo == null) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            zdo.Set(PlanKey, $"{kind}|{(prey ?? "").Replace("|", "")}|{spot.x.ToString("0.#", ci)}|{spot.y.ToString("0.#", ci)}|{spot.z.ToString("0.#", ci)}|{area.ToString("0", ci)}");
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {c.companionName}: every morning: {kind} {(string.IsNullOrEmpty(prey) ? "any" : prey)} at ({spot.x:0},{spot.z:0})");
        }

        public static void ClearPlan(CompanionController c)
        {
            var zdo = c != null ? c.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo != null) zdo.Set(PlanKey, "");
        }

        public static bool HasPlan(CompanionController c) => !string.IsNullOrEmpty(c?.GetComponent<ZNetView>()?.GetZDO()?.GetString(PlanKey, ""));

        private bool TryReadPlan(out TripKind kind, out Vector3 spot, out string prey, out float area)
        {
            kind = TripKind.Hunt; spot = Vector3.zero; prey = ""; area = DefaultArea;
            string s = Companion.GetComponent<ZNetView>()?.GetZDO()?.GetString(PlanKey, "");
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.Split('|');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length < 6 || !System.Enum.TryParse(p[0], out kind)) return false;
            prey = p[1];
            if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out float x) || !float.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out float y)
                || !float.TryParse(p[4], System.Globalization.NumberStyles.Float, ci, out float z) || !float.TryParse(p[5], System.Globalization.NumberStyles.Float, ci, out area)) return false;
            spot = new Vector3(x, y, z);
            return true;
        }
    }
}
