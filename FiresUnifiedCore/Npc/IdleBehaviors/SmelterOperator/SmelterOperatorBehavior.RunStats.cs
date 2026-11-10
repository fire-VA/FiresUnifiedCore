using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    // 0.2.902 (lead for Fire 10-27: "the overnight check should have been checking smelting too ... if we were never smelting any ore then
    // we were never mining resources"): one line per smelter run - "[Chore] smelting run: <name> at <station> (<how>, s s): ore in n,
    // fuel in c, bars out b" plus, when nothing went in, WHY in plain words: no ore at base (mining produced none), no fuel source, or the
    // source chest could not be reached (named). Base stock = bag + chests within 30 m of the station.
    public partial class SmelterOperatorBehavior
    {
        private string _lastStationPick;   // 0.2.911: the station-pick line only when it changes
        private float _waitSayAt;   // 0.2.905: next waiting heartbeat
        private float _runStart = -1f;
        private int _runOreIn, _runFuelIn, _runBarsOut;
        private string _runUnreached;
        private const float RunStockRadius = 30f;

        private void RunBegin() { _runStart = Time.time; _runOreIn = _runFuelIn = _runBarsOut = 0; _runUnreached = null; }

        private int BaseStock(string prefab)
        {
            if (string.IsNullOrEmpty(prefab) || _targetSmelter == null) return 0;
            var bag = Companion?.GetInventory()?.GetStorageInventory();
            int n = bag != null ? bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack) : 0;
            return n + FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(_targetSmelter.transform.position, prefab, RunStockRadius);
        }

        private void RunEnd(string how)
        {
            if (_runStart < 0f) return;
            float took = Time.time - _runStart; _runStart = -1f;
            string station = _targetSmelter != null ? Utils.GetPrefabName(_targetSmelter.gameObject) : "no station";
            string why = "";
            try
            {
                if (_targetSmelter != null && !_isKilnOperation && _runOreIn == 0)
                {
                    int ore = _targetSmelter.m_conversion.Where(c => c?.m_from != null).Sum(c => BaseStock(c.m_from.gameObject.name));
                    string fuelName = _targetSmelter.m_fuelItem != null ? _targetSmelter.m_fuelItem.gameObject.name : null;
                    int fuel = fuelName != null ? BaseStock(fuelName) : 0;
                    var parts = new System.Collections.Generic.List<string>();
                    if (ore == 0) parts.Add($"no ore at base for {station} (mining produced none: no {string.Join(" / ", _targetSmelter.m_conversion.Where(c => c?.m_from != null).Select(c => c.m_from.gameObject.name))} in the bag or chests within {RunStockRadius:0} m)");
                    else parts.Add($"{ore} ore at base");
                    if (fuelName != null && _targetSmelter.m_maxFuel > 0) parts.Add(fuel == 0 ? $"no fuel source (no {fuelName} at base)" : $"{fuel} {fuelName} at base");
                    if (_runUnreached != null) parts.Add($"source not reached: {_runUnreached}");
                    why = " - " + string.Join("; ", parts);
                }
            }
            catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"[Chore] smelting run: {Companion?.companionName} at {station} ({how}, {took:0} s): ore in {_runOreIn}, fuel in {_runFuelIn}, bars out {_runBarsOut}{why}");
        }
    }
}
