using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// 0.2.873 ([ghost], Fire 10-27 shot 590: "companions could try to tame wild tameable animals as well... to build up the farm"): a staying
    /// companion tames wild animals near home the VANILLA way - no new taming system:
    ///  1 Fetch: take one of the animal's own MonsterAI.m_consumeItems from a base chest (ChoreBrain.Withdraw, the shared withdraw), or use
    ///    one it carries;
    ///  2 Approach: walk to FeedDistance (7 m) from it, never closer than 4 m (calm: it doesn't spook), never attacking it;
    ///  3 Feed: drop the food 1.5 m in front of the animal (ItemDrop.DropItem). Vanilla MonsterAI eats it within its m_consumeSearchRange,
    ///    which feeds Tameable, whose own timer counts down while it is fed;
    ///  4 Watch: wait at FeedDistance; when vanilla says it is hungry again (Tameable.IsHungry) feed again (up to MaxFeeds per run);
    ///  5 Lead: once vanilla tames it (Character.IsTamed), MonsterAI.SetFollowTarget(companion) and walk it home, then release it there.
    /// Picks: a wild Tameable with consume items within TameRadius (60 m) of home, not claimed by another companion, whose food is carried or
    /// in a chest within 40 m of home; checked every 30 s. Board: the "Tame animals" job starts it. Lines:
    /// "[Chore] pick <name>: taming: picked <animal> at D m (food <item> from chest / carried) / skipped: reason",
    /// "[Chore] tame <animal> at D m: food <item> (from chest / carried), progress P%, <phase / result>".
    /// </summary>
    public class TamingBehavior : IdleSubBehavior
    {
        private enum Phase { Fetch, Approach, Feed, Watch, Lead, Done }
        private const float TameRadius = 60f, ChestRadius = 40f, FeedDistance = 7f, TooClose = 4f, CheckEvery = 30f, ProgressLogEvery = 20f;
        private const int MaxFeeds = 4;

        public override string BehaviorName => "Taming";
        public override bool AvailableForIdleRotation => true;
        public override int InventoryPriority => 6;

        private static readonly Dictionary<Character, float> s_claimed = new Dictionary<Character, float>();
        // 0.2.883 (round #9: Bergelmir and Grimhild HUNTED the boar Gymir had picked to tame): an animal a companion picked to tame is claimed from
        // the pick (2 min, refreshed while taming), and the hunts skip it
        internal static bool IsBeingTamed(Character c) => c != null && s_claimed.TryGetValue(c, out float t) && Time.time - t < 120f;
        private Character _animal; private Tameable _tame; private MonsterAI _ai;
        private string _food; private Container _chest; private bool _fromChest;
        private Phase _phase; private float _phaseStart, _nextCheck, _nextProgress, _skipSaidAt; private string _skipSaid;
        private int _feeds;

        private Inventory Bag => Companion != null ? Companion.GetComponent<CompanionInventory>()?.GetStorageInventory() : null;
        private Vector3 Home => IdleBehavior != null && IdleBehavior.HasHomePosition ? IdleBehavior.HomePosition : Transform.position;
        private static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        private void Skip(string why)
        {
            if (why == _skipSaid && Time.time - _skipSaidAt < 60f) return;
            _skipSaid = why; _skipSaidAt = Time.time;
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] pick {Companion?.companionName}: taming: skipped: {why}");
        }

        public override bool CanStart()
        {
            if (Companion == null || Time.time < _nextCheck) return false;
            _nextCheck = Time.time + CheckEvery;
            if (Companion.ShouldBeFollowing || IdleBehavior == null || !IdleBehavior.HasHomePosition) { Skip("not staying at base"); return false; }
            if (!CanStartBase()) return false;
            var bag = Bag;
            if (bag == null) { Skip("no bag"); return false; }
            Vector3 home = IdleBehavior.HasWorkZone ? IdleBehavior.WorkCenter : Home;   // Fire 10-27: a taming zone (chests still at base)
            var chestsAt = Home;
            var chests = ChestHelper.FindNearbyChests(chestsAt, ChestRadius);
            int wild = 0, noFood = 0, claimed = 0;
            Character best = null; float bd = float.MaxValue; string bestFood = null; Container bestChest = null;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c.IsPlayer() || c.IsTamed() || c.GetComponent<CompanionController>() != null) continue;
                var tm = c.GetComponent<Tameable>(); var ai = c.GetComponent<MonsterAI>();
                if (tm == null || ai == null || ai.m_consumeItems == null || ai.m_consumeItems.Count == 0) continue;
                if (Flat(c.transform.position, home) > (IdleBehavior.HasWorkZone ? IdleBehavior.WorkZoneRadius : TameRadius)) continue;
                wild++;
                if (c != _animal && s_claimed.TryGetValue(c, out float t) && Time.time - t < 120f) { claimed++; continue; }   // 0.2.883: its own pick is not "taken by another"
                string food = null; Container from = null;
                foreach (var it in ai.m_consumeItems)
                {
                    if (it == null) continue;
                    string pf = it.gameObject.name;
                    if (ChestHelper.CountPrefabInInventory(bag, pf) > 0) { food = pf; from = null; break; }
                    var ch = chests.FirstOrDefault(x => x != null && ChestHelper.CountPrefabInInventory(x.GetInventory(), pf) > 0);
                    if (ch != null && food == null) { food = pf; from = ch; }
                }
                if (food == null) { noFood++; continue; }
                float d = Flat(c.transform.position, Transform.position);
                if (d < bd) { bd = d; best = c; bestFood = food; bestChest = from; }
            }
            if (best == null)
            {
                Skip(wild == 0 ? $"no wild tameable animal within {TameRadius:0} m of home" : $"{wild} wild tameable(s) in range: {noFood} without their food in the bag or a chest within {ChestRadius:0} m, {claimed} being tamed by others");
                return false;
            }
            _animal = best; _tame = best.GetComponent<Tameable>(); _ai = best.GetComponent<MonsterAI>(); _food = bestFood; _chest = bestChest; _fromChest = bestChest != null;
            s_claimed[best] = Time.time;   // 0.2.883: claimed from the pick, so no companion hunts it meanwhile
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] pick {Companion.companionName}: taming: picked {Utils.GetPrefabName(best.gameObject)} at {bd:0} m (food {_food} {(_fromChest ? "from chest" : "carried")})");
            return true;
        }

        public override void Start()
        {
            base.Start();
            MaxDuration = 900f;
            _feeds = 0; _nextProgress = 0f;
            if (_animal != null) s_claimed[_animal] = Time.time;
            SetPhase(_fromChest ? Phase.Fetch : Phase.Approach);
        }

        private void SetPhase(Phase p) { _phase = p; _phaseStart = Time.time; }

        private float Progress()
        {
            var nv = _animal != null ? _animal.GetComponent<ZNetView>() : null;
            if (_tame == null || nv == null || !nv.IsValid()) return 0f;
            float left = nv.GetZDO().GetFloat(ZDOVars.s_tameTimeLeft, _tame.m_tamingTime);
            return Mathf.Clamp01(1f - left / Mathf.Max(1f, _tame.m_tamingTime)) * 100f;
        }

        private void Line(string what)
        {
            float d = _animal != null ? Flat(_animal.transform.position, Transform.position) : 0f;
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] tame {(_animal != null ? Utils.GetPrefabName(_animal.gameObject) : "?")} at {d:0} m: food {_food} ({(_fromChest ? "from chest" : "carried")}), progress {Progress():0}%, {what}");
        }

        private bool End(string result)
        {
            Line(result);
            if (_animal != null) s_claimed.Remove(_animal);
            if (_ai != null && _ai.GetFollowTarget() == Companion?.gameObject) _ai.SetFollowTarget(null);
            _phase = Phase.Done;
            return true;
        }

        public override bool Update()
        {
            if (!IsActive) return true;
            if (_animal == null || _animal.IsDead()) return End("the animal is gone");
            if (_animal.IsTamed() && _phase != Phase.Lead) { SetPhase(Phase.Lead); Line("TAMED - leading it home"); if (_ai != null) _ai.SetFollowTarget(Companion.gameObject); }
            if (s_claimed.ContainsKey(_animal)) s_claimed[_animal] = Time.time;
            Vector3 a = _animal.transform.position;
            float d = Flat(a, Transform.position);
            switch (_phase)
            {
                case Phase.Fetch:
                    if (_chest == null) return End("the chest is gone");
                    if (Flat(_chest.transform.position, Transform.position) > 2.5f) { TryMoveToPosition(_chest.transform.position); if (Time.time - _phaseStart > 60f) return End("couldn't reach the chest"); return false; }
                    if (AI.ChoreBrain.Withdraw(_chest, Bag, _food, 2, 0L) <= 0) return End($"no {_food} left in the chest");
                    Companion.GetComponent<CompanionInventory>()?.TriggerSaveToZDO();
                    Line("took food from the chest");
                    SetPhase(Phase.Approach);
                    return false;
                case Phase.Approach:
                    if (d > FeedDistance) { TryMoveToPosition(a + (Transform.position - a).normalized * (FeedDistance - 1f)); if (Time.time - _phaseStart > 90f) return End("couldn't get near it"); return false; }
                    SetPhase(Phase.Feed);
                    return false;
                case Phase.Feed:
                {
                    var bag = Bag;
                    var item = bag?.GetAllItems().FirstOrDefault(i => i != null && i.m_dropPrefab != null && i.m_dropPrefab.name == _food);
                    if (item == null) return End($"out of {_food}");
                    Vector3 drop = a + (Transform.position - a).normalized * 1.5f;
                    if (ZoneSystem.instance != null) drop.y = ZoneSystem.instance.GetGroundHeight(drop) + 0.3f;
                    ItemDrop.DropItem(item, 1, drop, Quaternion.identity);
                    bag.RemoveItem(item, 1);
                    Companion.GetComponent<CompanionInventory>()?.TriggerSaveToZDO();
                    _feeds++;
                    Line($"fed it ({_feeds}/{MaxFeeds})");
                    SetPhase(Phase.Watch);
                    return false;
                }
                case Phase.Watch:
                    if (d < TooClose) TryMoveToPosition(a + (Transform.position - a).normalized * FeedDistance);   // calm: give it room
                    if (Time.time >= _nextProgress) { _nextProgress = Time.time + ProgressLogEvery; Line(_tame != null && _tame.IsHungry() ? "hungry" : "fed, taming"); }
                    if (_tame != null && _tame.IsHungry() && Time.time - _phaseStart > 10f)
                    {
                        if (_feeds >= MaxFeeds) return End($"fed {MaxFeeds} times this run; will come back");
                        SetPhase(d > FeedDistance ? Phase.Approach : Phase.Feed);
                    }
                    return false;
                case Phase.Lead:
                    if (Flat(Transform.position, Home) > 4f) { TryMoveToPosition(Home); if (Time.time - _phaseStart > 120f) return End("tamed; it stays where it is (couldn't lead it home)"); return false; }
                    if (_ai != null) _ai.SetFollowTarget(null);
                    return End("tamed and brought home");
            }
            return true;
        }

        public override void Cancel()
        {
            if (_animal != null) s_claimed.Remove(_animal);
            if (_ai != null && Companion != null && _ai.GetFollowTarget() == Companion.gameObject) _ai.SetFollowTarget(null);
            base.Cancel();
        }

        public override string GetStatusDescription()
        {
            string n = _animal != null ? Localization.instance.Localize(_animal.m_name) : "an animal";
            switch (_phase)
            {
                case Phase.Fetch: return $"Getting food to tame {n}";
                case Phase.Approach: return $"Approaching {n} calmly";
                case Phase.Feed: case Phase.Watch: return $"Taming {n} ({Progress():0}%)";
                case Phase.Lead: return $"Leading {n} home";
                default: return "Taming";
            }
        }
    }
}
