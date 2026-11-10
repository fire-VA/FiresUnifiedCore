using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    // 0.2.902 (lead for Fire 10-27: "every screenshot ... shows basically the same exact amount of mine rock"): one line per gathering
    // run, so the whole mining link is visible: "[Chore] mining run: <name> (<how>, <s> s): <swings> swing(s) on <targets>, broke b,
    // no-damage skips n, ore +o (<prefab> xN; bag + base chests)" - "wood run" when no pickaxe target was worked. Ore is counted as the
    // companion's bag plus the base chests (30 m of home) at the start and at the end, so ore it deposited mid-run still counts.
    public partial class ResourceGatheringBehavior
    {
        private float _runStart = -1f;
        private int _runSwings, _runBroke, _runNoDamage;
        private bool _runPickaxe;
        private readonly List<string> _runTargets = new List<string>();
        private Dictionary<string, int> _runOreStart;
        private const float RunChestRadius = 30f;

        private Vector3 RunHome => IdleBehavior != null && IdleBehavior.HasHomePosition ? IdleBehavior.HomePosition : Transform.position;

        private Dictionary<string, int> OreStock()
        {
            var r = new Dictionary<string, int>();
            var bag = Companion?.GetInventory()?.GetStorageInventory();
            foreach (string ore in ResourceDataHelper.SmeltableItems.Where(o => o.EndsWith("Ore") || o.Contains("Scrap")))   // 0.2.905: ore only (the list also holds kiln / windmill inputs)
            {
                int n = 0;
                if (bag != null) n += bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == ore).Sum(i => i.m_stack);
                n += FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(RunHome, ore, RunChestRadius);
                if (n > 0) r[ore] = n;
            }
            return r;
        }

        private void RunBegin()
        {
            _runStart = Time.time; _runSwings = _runBroke = _runNoDamage = 0; _runPickaxe = false; _runTargets.Clear();
            try { _runOreStart = OreStock(); } catch { _runOreStart = null; }
        }

        private void RunSwing(string target, bool pickaxe)
        {
            _runSwings++;
            if (pickaxe) _runPickaxe = true;
            if (!string.IsNullOrEmpty(target) && !_runTargets.Contains(target) && _runTargets.Count < 6) _runTargets.Add(target);
        }

        private void RunEnd(string how)
        {
            if (_runStart < 0f) return;
            float took = Time.time - _runStart; _runStart = -1f;
            if (_runSwings == 0) return;   // a run that never swung already says why on its own "[Chore] pick" / no-hit lines
            string ore = "ore not counted";
            try
            {
                var end = OreStock(); var start = _runOreStart ?? new Dictionary<string, int>();
                var deltas = end.Keys.Union(start.Keys).Select(k => (k, d: (end.TryGetValue(k, out int e) ? e : 0) - (start.TryGetValue(k, out int s) ? s : 0)))
                                .Where(x => x.d != 0).ToList();
                int gained = deltas.Where(x => x.d > 0).Sum(x => x.d);
                ore = $"ore {(gained > 0 ? "+" : "")}{gained}" + (deltas.Count > 0 ? $" ({string.Join(", ", deltas.Select(x => $"{x.k} {(x.d > 0 ? "+" : "")}{x.d}"))})" : "") + " (bag + base chests)";
            }
            catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] {(_runPickaxe ? "mining" : "wood")} run: {Companion?.companionName} ({how}, {took:0} s): {_runSwings} swing(s) on {string.Join(", ", _runTargets)}, broke {_runBroke}, no-damage skips {_runNoDamage}, {ore}");
        }
    }
}
