using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.UI.ContextMenu;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Commands
{
    /// <summary>
    /// Contributes companion command rows to the hold-Alt+Shift right-click menu, mirroring the Shift+MMB ping
    /// commands. It resolves the target exactly the way <see cref="CompanionCommandSystem.ResolveCommand"/> does,
    /// so right-clicking the ground offers "Move here", a chest "Deposit items" (+ "Organize"), a kiln
    /// "Operate", a monster "Attack", and so on — issued to every commandable companion or one chosen companion.
    /// Registered once from <see cref="CompanionCommandSystem.EnsureInitialized"/>.
    /// </summary>
    public sealed class CompanionContextProvider : IContextMenuProvider
    {
        public string TitleFor(ContextTarget target) => "Companions";

        public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
        {
            var sys = CompanionCommandSystem.Instance;
            if (sys == null || target == null || target.GameObject == null) return null;

            var player = Player.m_localPlayer;
            if (player == null) return null;

            var companions = sys.GetCommandableCompanions(player);
            if (companions == null || companions.Count == 0) return null;

            var resolved = sys.ResolveCommand(target.GameObject, target.Point);
            if (resolved == null) return null;
            var cmd = resolved.Value;

            string verb = VerbFor(cmd.type);
            var rows = new List<ContextMenuItem>();

            if (companions.Count == 1)
            {
                var only = companions[0];
                rows.Add(ContextMenuItem.Row($"{only.GetDisplayName()}: {verb}",
                    () => sys.IssueManualCommand(only, cmd.type, cmd.position, cmd.targetObj, cmd.targetChar)));
            }
            else
            {
                rows.Add(ContextMenuItem.Row($"All companions: {verb}", () =>
                {
                    foreach (var companion in companions)
                        if (companion != null) sys.IssueManualCommand(companion, cmd.type, cmd.position, cmd.targetObj, cmd.targetChar);
                }));
                rows.Add(ContextMenuItem.Sep());
                foreach (var companion in companions)
                {
                    if (companion == null) continue;
                    var single = companion;
                    rows.Add(ContextMenuItem.Row($"{single.GetDisplayName()}: {verb}",
                        () => sys.IssueManualCommand(single, cmd.type, cmd.position, cmd.targetObj, cmd.targetChar)));
                }
            }

            if (cmd.type == CompanionCommandSystem.CommandType.DepositToChest && cmd.targetObj != null)
            {
                rows.Add(ContextMenuItem.Sep());
                Vector3 chestPos = cmd.targetObj.transform.position;
                rows.Add(ContextMenuItem.Row("Organize nearby chests", () => OrganizeChests(chestPos)));
            }

            AddTripRows(rows, companions, target, cmd.type);
            return rows;
        }

        // Hunting / fishing trips (TripBehavior): "now" or "every morning", per companion; up to TripBehavior.MaxParty go at once.
        private static void AddTripRows(List<ContextMenuItem> rows, List<CompanionController> companions, ContextTarget target, CompanionCommandSystem.CommandType type)
        {
            var kind = type == CompanionCommandSystem.CommandType.Fish ? TripBehavior.TripKind.Fish : TripBehavior.TripKind.Hunt;
            if (kind == TripBehavior.TripKind.Hunt && type != CompanionCommandSystem.CommandType.MoveToPosition && type != CompanionCommandSystem.CommandType.AttackTarget) return;
            string prey = "", preyLabel = "any game";
            var ch = target.GameObject.GetComponentInParent<Character>();
            if (kind == TripBehavior.TripKind.Hunt && ch != null && !ch.IsPlayer() && ch.GetComponent<CompanionController>() == null)
            {
                prey = Utils.GetPrefabName(ch.gameObject);
                preyLabel = Localization.instance != null ? Localization.instance.Localize(ch.m_name) : ch.m_name;
            }
            Vector3 spot = target.Point;
            string what = kind == TripBehavior.TripKind.Fish ? "Fishing trip here" : $"Hunting trip: {preyLabel}";
            rows.Add(ContextMenuItem.Sep());
            foreach (var companion in companions)
            {
                if (companion == null) continue;
                var c = companion;
                rows.Add(ContextMenuItem.Row($"{c.GetDisplayName()}: {what}", () => TripAreaPicker.Begin(c, kind, spot, prey, what, false)));
                rows.Add(ContextMenuItem.Row($"{c.GetDisplayName()}: {what}, every morning", () => TripAreaPicker.Begin(c, kind, spot, prey, what, true)));
                if (TripBehavior.HasPlan(c))
                    rows.Add(ContextMenuItem.Row($"{c.GetDisplayName()}: stop the morning trips", () =>
                    {
                        TripBehavior.ClearPlan(c);
                        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, $"{c.GetDisplayName()}: no more morning trips");
                    }));
            }
        }

        internal static void StartTrip(CompanionController c, TripBehavior.TripKind kind, Vector3 spot, string prey, float area, bool everyMorning)
        {
            var idle = c != null ? c.GetComponent<CompanionIdleBehavior>() : null;
            var trip = idle?.GetSubBehavior<TripBehavior>();
            if (trip == null) return;
            if (!trip.Arm(kind, spot, prey, area, out string why))
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"{c.GetDisplayName()} can't go: {why}");
                return;
            }
            if (everyMorning) TripBehavior.SavePlan(c, kind, spot, prey, area);
            if (!idle.TryStartSubBehavior<TripBehavior>())
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, $"{c.GetDisplayName()} can't go right now");
        }

        private static void OrganizeChests(Vector3 center)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                var chests = ChestHelper.FindNearbyChests(center, CompanionSettings.ChestAutoSortRadius);
                chests.RemoveAll(chest => !ChestHelper.TryClaimForWrite(chest, player.GetPlayerID()));
                if (chests.Count < 2)
                {
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "No chest cluster to organize here");
                    return;
                }
                var result = SmartStorageOrganizer.OrganizeChestCluster(chests, center, stationSearchRadius: 25f);
                int moved = result.ItemsMoved + result.StacksConsolidated;
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    moved > 0 ? $"Organized {moved} item(s) across {chests.Count} chest(s)" : "Chests already organized");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionContextProvider] Organize failed: {ex.Message}");
            }
        }

        private static string VerbFor(CompanionCommandSystem.CommandType type)
        {
            switch (type)
            {
                case CompanionCommandSystem.CommandType.AttackTarget:   return "Attack";
                case CompanionCommandSystem.CommandType.MoveToPosition: return "Move here";
                case CompanionCommandSystem.CommandType.SitOnChair:     return "Sit here";
                case CompanionCommandSystem.CommandType.TrainArchery:   return "Train archery";
                case CompanionCommandSystem.CommandType.GatherResource: return "Gather";
                case CompanionCommandSystem.CommandType.OperateSmelter: return "Operate";
                case CompanionCommandSystem.CommandType.TendFire:       return "Tend fire";
                case CompanionCommandSystem.CommandType.UseWorkstation: return "Use station";
                case CompanionCommandSystem.CommandType.DepositToChest: return "Deposit items";
                case CompanionCommandSystem.CommandType.CookFood:       return "Cook";
                case CompanionCommandSystem.CommandType.RepairBuilding: return "Repair";
                case CompanionCommandSystem.CommandType.Fish:           return "Fish here";
                case CompanionCommandSystem.CommandType.Farm:           return "Farm";
                default:                                                return "Go here";
            }
        }
    }
}
