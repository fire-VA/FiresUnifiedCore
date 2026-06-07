using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc
{
    /// <summary>
    /// Tracks all companions that are following the local player on the minimap.
    ///
    /// Uses Minimap.AddPin / RemovePin so position math (UV rect, zoom, both map modes)
    /// is handled entirely by the vanilla system â€” no manual coordinate replication needed.
    ///
    /// Each following companion appears as a blue Player pin labelled with the companion's name.
    /// Pins are cleaned up when companions stop following or when the component is destroyed.
    /// </summary>
    public class CompanionMapMarker : MonoBehaviour
    {
        public static CompanionMapMarker Instance { get; private set; }

        // Pin colours by combat role
        private static readonly Color TankColor    = new Color(0.95f, 0.80f, 0.25f, 1f); // gold
        private static readonly Color HealerColor  = new Color(0.25f, 0.90f, 0.35f, 1f); // green
        private static readonly Color DpsColor     = new Color(0.95f, 0.35f, 0.20f, 1f); // red-orange
        private static readonly Color DefaultColor = new Color(0.30f, 0.60f, 1.00f, 1f); // blue

        // (companion, its live PinData in Minimap.m_pins)
        private readonly List<(CompanionController companion, Minimap.PinData pin)> _pins =
            new List<(CompanionController, Minimap.PinData)>();

        // Cached so we don't allocate per-frame.
        private readonly List<CompanionController> _followingBuffer = new List<CompanionController>();

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CleanupAllPins();
        }

        private void Update()
        {
            if (Minimap.instance == null) return;
            var local = Player.m_localPlayer;
            if (local == null) { CleanupAllPins(); return; }

            BuildFollowingList(local);
            SyncPins();
            UpdatePositionsAndColors();
        }

        // â”€â”€ Following companion query â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void BuildFollowingList(Player player)
        {
            _followingBuffer.Clear();
            long pid = player.GetPlayerID();
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null || c.isDefeated || !c.isTamed) continue;
                if (c.ownerPlayerId != pid) continue;
                if (c.ShouldBeFollowing) _followingBuffer.Add(c);
            }
        }

        // â”€â”€ Pin sync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void SyncPins()
        {
            // Remove pins whose companion is no longer following.
            for (int i = _pins.Count - 1; i >= 0; i--)
            {
                if (!_followingBuffer.Contains(_pins[i].companion))
                {
                    Minimap.instance?.RemovePin(_pins[i].pin);
                    _pins.RemoveAt(i);
                }
            }

            // Add pins for companions not yet tracked.
            foreach (var c in _followingBuffer)
            {
                bool exists = false;
                foreach (var (ec, _) in _pins) { if (ec == c) { exists = true; break; } }
                if (exists) continue;

                var pin = Minimap.instance.AddPin(
                    c.transform.position,
                    Minimap.PinType.Player,
                    c.companionName,
                    save: false,
                    isChecked: false);
                _pins.Add((c, pin));
            }
        }

        // â”€â”€ Position / colour update â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void UpdatePositionsAndColors()
        {
            if (_pins.Count == 0) return;

            bool moved = false;
            foreach (var (companion, pin) in _pins)
            {
                if (companion == null || pin == null) continue;

                var pos = companion.transform.position;
                if (pin.m_pos != pos)
                {
                    pin.m_pos = pos;
                    moved = true;
                }

                // Re-apply color each frame; the Minimap resets icons to white during UpdatePins.
                if (pin.m_iconElement != null)
                    pin.m_iconElement.color = GetRoleColor(companion);
            }

            if (moved && Minimap.instance != null)
                AccessTools.FieldRefAccess<Minimap, bool>(Minimap.instance, "m_pinUpdateRequired") = true;
        }

        // â”€â”€ Role colour â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static Color GetRoleColor(CompanionController companion)
        {
            var ac = companion.GetArchetypeController();
            if (ac == null) return DefaultColor;

            switch (ac.CurrentArchetypeClass)
            {
                case ArchetypeClass.Tank:
                case ArchetypeClass.Paladin:
                    return TankColor;

                case ArchetypeClass.Healer:
                    return HealerColor;

                case ArchetypeClass.Berserker:
                case ArchetypeClass.Rogue:
                case ArchetypeClass.Monk:
                case ArchetypeClass.Ranger:
                case ArchetypeClass.Mage:
                    return DpsColor;

                default:
                    return DefaultColor;
            }
        }

        // â”€â”€ Cleanup â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void CleanupAllPins()
        {
            if (Minimap.instance != null)
                foreach (var (_, pin) in _pins) Minimap.instance.RemovePin(pin);
            _pins.Clear();
        }
    }
}
