using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Farming build 2 (Fire 10-24, approved design): the companion tends its bed and learns each crop's spacing.
    ///   A) health pass: an unhealthy plant in the bed is walked to and fixed the way a player would -
    ///      NoSpace -> uproot (refund) and replant later at a free spot; NotCultivated (skill 25+) -> cultivate + uproot (refund);
    ///      WrongBiome / TooHot / TooCold (skill 40+) -> uproot (refund) and never plant that crop on that spot again;
    ///      NoSun -> a one-line note to the owner (a roof is the player's call).
    ///   B) learned spacing per crop, saved on the companion: a novice starts at growRadius x 2, each NoSpace widens it 0.1 m,
    ///      each healthy harvest narrows it toward the floor (FarmingDataHelper.GrowSpacing) faster with skill. GrowSpaceFree stays
    ///      the hard check, so "learned" never plants too tight.
    ///   C) speed: the interact pause runs from 1.0x at skill 0 down to 0.5x at skill 100.
    /// </summary>
    public partial class FarmingBehavior
    {
        private const float TendScanInterval = 20f, NoSpaceWiden = 0.1f, HarvestNarrow = 0.05f, BadSpotCell = 2f;
        private const float SkillNotCultivated = 25f, SkillWrongPlace = 40f;
        private float _nextTendScan;
        private Plant _tendPlant;
        private readonly HashSet<Vector2Int> _badSpots = new HashSet<Vector2Int>();      // spots that failed the crop's check this session
        private static readonly HashSet<int> s_noSunReported = new HashSet<int>();
        private readonly HashSet<Vector2Int> _rejectedSpots = new HashSet<Vector2Int>();  // spots a planting check turned down; cleared when the bed changes
        private static Vector2Int FineCell(Vector3 p) => new Vector2Int(Mathf.RoundToInt(p.x * 2f), Mathf.RoundToInt(p.z * 2f));

        private float FarmingSkill => Companion?.GetSkills()?.GetSkillLevel(Skills.SkillType.Farming) ?? 0f;
        private void RaiseFarming(float factor = 1f) => Companion?.GetSkills()?.RaiseSkill(Skills.SkillType.Farming, factor);

        /// <summary>C) the interact pause scaled by farming skill (1.0x at 0, 0.5x at 100).</summary>
        private float SkillPause => Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(FarmingSkill / 100f));

        private static Vector2Int Cell(Vector3 p) => new Vector2Int(Mathf.RoundToInt(p.x / BadSpotCell), Mathf.RoundToInt(p.z / BadSpotCell));

        // ---- B) learned spacing --------------------------------------------------------------------------------------------
        private float LearnedSpacing(FarmingDataHelper.CropSapling crop)
        {
            float floor = FarmingDataHelper.GrowSpacing(crop);
            if (Companion == null || crop?.Prefab == null) return floor;
            if (!Companion.CropSpacing.TryGetValue(crop.Prefab.name, out float s))
                s = Mathf.Max(floor, (crop.Plant != null && crop.Plant.m_growRadius > 0f ? crop.Plant.m_growRadius : 0.5f) * 2f);
            return Mathf.Max(floor, s);
        }

        private void AdjustSpacing(string cropName, float floor, float delta, string why)
        {
            if (Companion == null || string.IsNullOrEmpty(cropName)) return;
            float before = Companion.CropSpacing.TryGetValue(cropName, out float s) ? s : floor * 1.5f;
            float after = Mathf.Max(floor, before + delta);
            if (Mathf.Abs(after - before) < 0.001f) return;
            Companion.CropSpacing[cropName] = after;
            Companion.SaveCropSpacing();
            FiresCore.Logging.FiresLogger.LogDebug($"[Farming] {Companion.companionName} {cropName} spacing {before:0.00} -> {after:0.00} ({why}, skill {FarmingSkill:0})");
        }

        /// <summary>B) a healthy harvest of <paramref name="pickable"/> narrows that crop's learned spacing.</summary>
        private void LearnFromHarvest(Pickable pickable)
        {
            if (pickable == null) return;
            string name = Utils.GetPrefabName(pickable.gameObject);
            var sapling = FindSaplingForPickable(name);
            if (sapling == null) return;
            float floor = FarmingDataHelper.GrowSpacing(sapling);
            AdjustSpacing(sapling.Prefab.name, floor, -HarvestNarrow * (1f + FarmingSkill / 50f), "healthy harvest");
            RaiseFarming(0.5f);
        }

        private static FarmingDataHelper.CropSapling FindSaplingForPickable(string pickableName)
        {
            // sapling prefabs grow into the pickable named in Plant.m_grownPrefabs
            foreach (var crop in FarmingDataHelper.AllSaplings())
                if (crop?.Plant?.m_grownPrefabs != null)
                    foreach (var g in crop.Plant.m_grownPrefabs) if (g != null && g.name == pickableName) return crop;
            return null;
        }

        // ---- A) health pass ------------------------------------------------------------------------------------------------
        /// <summary>Picks the next unhealthy plant in the bed this companion's skill can fix and walks to it. True when one was queued.</summary>
        private bool TryStartTending(Vector3 home)
        {
            if (Time.time < _nextTendScan) return false;
            _nextTendScan = Time.time + TendScanInterval;
            float skill = FarmingSkill;
            Plant best = null; float bestD = float.MaxValue;
            foreach (var c in Physics.OverlapSphere(home, CropDetectionRange))
            {
                var pl = c != null ? c.GetComponentInParent<Plant>() : null;
                if (pl == null || !ChestHelper.WardsAllow(pl.transform.position, Companion)) continue;
                var st = pl.GetStatus();
                if (st == Plant.Status.Healthy) continue;
                if (st == Plant.Status.NoSun) { ReportNoSun(pl); continue; }
                if (st == Plant.Status.NotCultivated && (skill < SkillNotCultivated || !HasCultivator())) continue;
                if ((st == Plant.Status.WrongBiome || st == Plant.Status.TooHot || st == Plant.Status.TooCold) && skill < SkillWrongPlace) continue;
                float d = (pl.transform.position - Transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = pl; }
            }
            if (best == null) return false;
            _tendPlant = best;
            _targetPosition = best.transform.position;
            TryMoveToPosition(_targetPosition);
            SetPhase(FarmPhase.MovingToTend);
            return true;
        }

        private void ReportNoSun(Plant pl)
        {
            if (!s_noSunReported.Add(pl.GetInstanceID())) return;
            Vector3 p = pl.transform.position;
            FiresCore.Logging.FiresLogger.LogDebug($"[Farming] {Utils.GetPrefabName(pl.gameObject)} at ({p.x:0},{p.z:0}): NoSun -> report (skill {FarmingSkill:0})");
            CompanionChatHelper.SayStatus(Companion, $"The {Localization.instance.Localize(pl.m_name)} at ({p.x:0}, {p.z:0}) gets no sun - something is over it.");
        }

        private bool UpdateMovingToTend()
        {
            if (_tendPlant == null) { SetPhase(FarmPhase.Idle); return false; }
            TryMoveToPosition(_targetPosition);
            if (Vector3.Distance(Transform.position, _targetPosition) <= ArrivalDistance) { StopMovement(); SetPhase(FarmPhase.Tending); return false; }
            if (MovementTimedOut()) { _tendPlant = null; SetPhase(FarmPhase.Idle); }
            return false;
        }

        private bool UpdateTending()
        {
            if (_tendPlant == null) { SetPhase(FarmPhase.Idle); return false; }
            StopMovement();
            FaceTarget(_tendPlant.transform.position);
            float age = Time.time - _phaseStartTime;
            if (age < PhaseEntryWindow) { PlayInteractAnimation(); return false; }
            if (age < SkillPause) return false;

            var pl = _tendPlant; _tendPlant = null;
            var st = pl.GetStatus();
            Vector3 p = pl.transform.position;
            string name = Utils.GetPrefabName(pl.gameObject);
            string action;
            switch (st)
            {
                case Plant.Status.NoSpace:
                    AdjustSpacing(name, pl.m_growRadius + 0.3f, NoSpaceWiden, "NoSpace");
                    action = Uproot(pl) ? "uprooted (refunded); replanted later at a free spot" : "uproot failed";
                    break;
                case Plant.Status.NotCultivated:
                    bool tilled = HasCultivator() && Cultivate(p);
                    action = (Uproot(pl) ? "uprooted (refunded)" : "uproot failed") + (tilled ? ", ground cultivated" : ", no cultivator to till with");
                    break;
                case Plant.Status.WrongBiome:
                case Plant.Status.TooHot:
                case Plant.Status.TooCold:
                    _badSpots.Add(Cell(p));
                    action = (Uproot(pl) ? "uprooted (refunded)" : "uproot failed") + ", not planting it here again";
                    break;
                default:
                    action = "nothing to do";
                    break;
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[Farming] {name} at ({p.x:0},{p.z:0}): {st} -> {action} (skill {FarmingSkill:0})");
            RaiseFarming(0.5f);
            SetPhase(FarmPhase.Idle);
            return false;
        }

        /// <summary>Removes the plant the way a player's removal does: its piece resources (the seed cost) go back, here into the
        /// companion's storage, then the plant is destroyed by its owner.</summary>
        private bool Uproot(Plant pl)
        {
            var nv = pl != null ? pl.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid()) return false;
            var storage = _inventory?.GetStorageInventory();
            var piece = pl.GetComponent<Piece>();
            if (storage != null && piece != null && piece.m_resources != null)
                foreach (var req in piece.m_resources)
                    if (req?.m_resItem != null && req.m_amount > 0 && req.m_recover)
                        storage.AddItem(req.m_resItem.gameObject, req.m_amount);
            _inventory?.SaveToZDO();
            nv.ClaimOwnership();
            ZNetScene.instance.Destroy(pl.gameObject);
            _rejectedSpots.Clear();
            return true;
        }

        /// <summary>Cultivates the ground at a spot with vanilla's own cultivator terrain op (cultivate_v2).</summary>
        private static bool Cultivate(Vector3 p)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("cultivate_v2") : null;
            if (prefab == null) return false;
            Object.Instantiate(prefab, p, Quaternion.identity);
            return true;
        }
    }
}
