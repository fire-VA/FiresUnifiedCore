using System.Collections.Generic;
using FiresCore.Npc.Utilities;
using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Rogue
{
    /// <summary>
    /// Stealth that every peer sees (Fire, R49 stream: an invisible player was hidden only on their own screen). A status
    /// effect lives only on its owner (Core 0.2.138), so StealthEffect writes its alpha to the character's ZDO; this
    /// watcher, on every peer, ghosts each other character carrying it with the same CharacterVisibilityController the
    /// owner uses, and puts it back when the value clears. The HUD (EnemyHud hooks in CompanionPatches) and creature AI
    /// (ClassTargeting's IsEnemy hook) read <see cref="IsStealthed"/>.
    /// </summary>
    public sealed class StealthSync : MonoBehaviour
    {
        public const string ZdoKey = "FiresStealthAlpha";
        private const float ScanSeconds = 0.25f;

        private static readonly int ZdoHash = ZdoKey.GetStableHashCode();
        private static StealthSync _instance;

        private readonly Dictionary<Character, CharacterVisibilityController> _ghosted =
            new Dictionary<Character, CharacterVisibilityController>();
        private readonly Dictionary<Character, bool> _asGhost = new Dictionary<Character, bool>();
        private readonly List<Character> _gone = new List<Character>();
        private float _nextScan;

        /// <summary>Starts the watcher once (StatusEffectManager.RegisterAllEffects calls it on every peer).</summary>
        public static void Ensure()
        {
            if (_instance != null) return;
            var host = new GameObject("FiresStealthSync");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<StealthSync>();
        }

        /// <summary>The owner's stealth alpha on the character's ZDO, 0 when not in stealth.</summary>
        public static float StealthAlpha(Character character)
        {
            if (character == null) return 0f;
            ZNetView view = character.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return 0f;
            ZDO zdo = view.GetZDO();
            return zdo != null ? zdo.GetFloat(ZdoHash, 0f) : 0f;
        }

        // BaseAI.IsEnemy asks for every AI against every candidate, many times a second ([generator]): a set the sweep keeps,
        // no GetComponent or ZDO read per call. Up to one sweep (0.25 s) behind the ZDO.
        private static readonly HashSet<Character> s_stealthed = new HashSet<Character>();

        public static bool IsStealthed(Character character) => character != null && s_stealthed.Contains(character);

        /// <summary>Owner side: StealthEffect sets the alpha on apply and 0 on removal.</summary>
        public static void Publish(Character character, float alpha)
        {
            if (character == null) return;
            ZNetView view = character.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            ZDO zdo = view.GetZDO();
            if (zdo != null) zdo.Set(ZdoHash, alpha > 0f ? alpha : 0f);
        }

        private void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + ScanSeconds;

            Player local = Player.m_localPlayer;
            bool headless = ZNet.instance != null && ZNet.instance.IsDedicated();
            s_stealthed.Clear();
            foreach (Character character in Character.GetAllCharacters())
            {
                if (character == null) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                float alpha = StealthAlpha(character);
                if (alpha > 0f) s_stealthed.Add(character);
                // The owner's own StealthEffect does its look; a dedicated server keeps only the set (its AI reads it).
                if (view.IsOwner() || headless)
                {
                    // Ownership moved here (e.g. the owner logged out mid-stealth): our remote look must not stay on it.
                    if (_ghosted.TryGetValue(character, out CharacterVisibilityController mine))
                    {
                        mine.Restore();
                        _ghosted.Remove(character);
                        _asGhost.Remove(character);
                    }
                    continue;
                }
                bool tracked = _ghosted.TryGetValue(character, out CharacterVisibilityController controller);
                if (alpha <= 0f)
                {
                    if (tracked)
                    {
                        controller.Restore();
                        _ghosted.Remove(character);
                        _asGhost.Remove(character);
                    }
                    continue;
                }

                // Fire (2026-09-28): the party sees a ghost, everyone else sees nothing.
                bool ghost = local != null && ClassTargeting.IsPartyMember(local, character);
                if (tracked && _asGhost.TryGetValue(character, out bool wasGhost) && wasGhost == ghost) continue;
                if (tracked) controller.Restore();
                controller = new CharacterVisibilityController(character);
                if (ghost) controller.SetGhostMode(true, alpha);
                else controller.SetHidden(true);
                _ghosted[character] = controller;
                _asGhost[character] = ghost;
            }

            _gone.Clear();
            foreach (KeyValuePair<Character, CharacterVisibilityController> entry in _ghosted)
                if (entry.Key == null) _gone.Add(entry.Key);
            foreach (Character character in _gone)
            {
                _ghosted.Remove(character);
                _asGhost.Remove(character);
            }
        }

        /// <summary>
        /// Whether this peer should draw the character's nameplate and health bar: not while it is stealthed, unless the
        /// local player is in its party (who see it as a ghost). The EnemyHud hooks in CompanionPatches ask here.
        /// </summary>
        public static bool HidesHudFor(Character character)
        {
            if (character == null || character == Player.m_localPlayer || !IsStealthed(character)) return false;
            Player local = Player.m_localPlayer;
            return local == null || !ClassTargeting.IsPartyMember(local, character);
        }
    }
}
