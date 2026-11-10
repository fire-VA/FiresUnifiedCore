using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    // 0.2.908 (Fire 10-27: "Yes, raise it when ore is needed"): ResourceGathering scores +6 (like Taming; FireTending's 10 still wins when a
    // fire needs fuel) while a homestead smelter (ore -> bars, fuel-burning) is low on ore and the companion has or can get a pickaxe. The
    // pick line says why: "mining +6: smelter at (x, z) low on ore (q/max)". Smelters are found from the loaded Smelter instances, not a
    // physics overlap: EBM bakes building pieces into stand-ins, so an overlap never reached the homestead smelter (round #28).
    public partial class ResourceGatheringBehavior
    {
        private const int OreNeedBonus = 6;
        private static readonly List<Smelter> s_smelters = new List<Smelter>();
        private static float s_smeltersAt = -1f;

        internal static IEnumerable<Smelter> SmeltersNear(Vector3 at, float radius)
        {
            if (s_smeltersAt < 0f || Time.time - s_smeltersAt > 5f)
            {
                s_smeltersAt = Time.time;
                s_smelters.Clear();
                var sw = FiresCore.Npc.Core.SceneScanCensus.Start();   // 0.2.1221 [ghost]: registry, not a scene search
                if (FiresCore.Npc.Core.SceneScanCensus.UseRegistries) s_smelters.AddRange(FiresCore.Npc.JobBoard.HomesteadStations.Live(FiresCore.Npc.JobBoard.HomesteadStations.Smelters));
                else s_smelters.AddRange(Object.FindObjectsByType<Smelter>(FindObjectsSortMode.None));
                FiresCore.Npc.Core.SceneScanCensus.Note("OreNeed.SmeltersNear", sw, s_smelters.Count);
            }
            foreach (var s in s_smelters)
            {
                if (s == null) continue;
                Vector3 d = s.transform.position - at; d.y = 0f;
                if (d.magnitude <= radius) yield return s;
            }
        }

        private float _oreNeedAt = -1f;
        private int _oreNeed;
        private string _oreNeedWhy;

        private int OreNeedPriority()
        {
            if (_oreNeedAt >= 0f && Time.time - _oreNeedAt < 5f) return _oreNeed;
            _oreNeedAt = Time.time; _oreNeed = 0; _oreNeedWhy = null;
            if (IdleBehavior == null || !IdleBehavior.HasHomePosition || Companion == null || Companion.ShouldBeFollowing) return 0;
            Vector3 home = IdleBehavior.HomePosition;
            float radius = CompanionSettings.GetStayModeWorkSearchRadius(home);
            foreach (var s in SmeltersNear(home, radius))
            {
                if (s.m_maxFuel <= 0 || s.m_fuelItem == null || PieceDataHelper.IsCharcoalKiln(s) || !PieceDataHelper.IsOperableStation(s)) continue;
                var nv = s.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                int queued = nv.GetZDO().GetInt(ZDOVars.s_queued, 0);
                if (queued * 2 >= s.m_maxOre) continue;   // "low on ore" = under half full
                if (GetBestObtainableToolTier(ResourceDataHelper.ToolType.Pickaxe) < 0) { _oreNeedWhy = $"no bonus: {Utils.GetPrefabName(s.gameObject)} low on ore but no pickaxe to get"; return 0; }
                _oreNeed = OreNeedBonus;
                _oreNeedWhy = $"mining +{OreNeedBonus}: {Utils.GetPrefabName(s.gameObject)} at ({s.transform.position.x:0}, {s.transform.position.z:0}) low on ore ({queued}/{s.m_maxOre})";
                return _oreNeed;
            }
            return 0;
        }

        public override string PriorityReason => _oreNeed > 0 ? _oreNeedWhy : null;
    }
}
