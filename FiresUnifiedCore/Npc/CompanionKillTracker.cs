using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc
{
    /// <summary>
    /// Tracks kill statistics for companion NPCs.
    /// Similar to player kill tracking but persisted with companion data.
    /// 
    /// Tracks:
    /// - Total kills
    /// - Kills by creature type
    /// - Death count
    /// - Session statistics
    /// 
    /// Data is saved to ZDO for persistence across sessions.
    /// Uses caching to batch saves and reduce ZDO write spam.
    /// </summary>
    public class CompanionKillTracker : MonoBehaviour
    {
        #region Fields

        private CompanionController _companion;
        private CompanionProgression _progression;
        private Character _character;
        private ZNetView _nview;

        // Kill statistics
        private int _totalKills = 0;
        private int _deaths = 0;
        private Dictionary<string, int> _killsByCreature = new Dictionary<string, int>();

        // Session tracking (not persisted)
        private int _sessionKills = 0;
        private float _lastKillTime = 0f;

        // ZDO keys for persistence
        private const string ZDO_TOTAL_KILLS = "companion_total_kills";
        private const string ZDO_DEATHS = "companion_deaths";
        private const string ZDO_KILLS_DATA = "companion_kills_data";
        
        // Caching for batched saves
        private bool _isDirty = false;
        private float _lastSaveTime = 0f;
        private const float SAVE_INTERVAL = 30f; // Save at most every 30 seconds
        private const float SAVE_DELAY_AFTER_CHANGE = 5f; // Wait 5 seconds after last change before saving
        private float _lastChangeTime = 0f;
        
        // Logging control - only log on owner's client, not server
        public static bool VerboseLogging = false;

        #endregion

        #region Properties

        public int TotalKills => _totalKills;
        public int Deaths => _deaths;
        public int SessionKills => _sessionKills;
        public float TimeSinceLastKill => Time.time - _lastKillTime;
        
        /// <summary>
        /// Returns true if this is the local player's companion (should show UI notifications).
        /// </summary>
        private bool IsLocalOwnerCompanion
        {
            get
            {
                if (_companion == null) return false;
                var localPlayer = Player.m_localPlayer;
                if (localPlayer == null) return false;
                return _companion.ownerPlayerId == localPlayer.GetPlayerID();
            }
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _progression = GetComponent<CompanionProgression>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }

        private void Start()
        {
            LoadFromZDO();
        }
        
        private void Update()
        {
            // Check if we need to flush cached changes
            if (_isDirty)
            {
                float timeSinceChange = Time.time - _lastChangeTime;
                float timeSinceSave = Time.time - _lastSaveTime;
                
                // Save if: enough time since last change AND enough time since last save
                if (timeSinceChange >= SAVE_DELAY_AFTER_CHANGE && timeSinceSave >= SAVE_INTERVAL)
                {
                    FlushToZDO();
                }
            }
        }
        
        private void OnDestroy()
        {
            // Always save on destroy if dirty
            if (_isDirty)
            {
                FlushToZDO();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Records a kill by this companion.
        /// Called by CompanionCombat when the companion kills an enemy.
        /// </summary>
        /// <param name="victim">The character that was killed.</param>
        public void RecordKill(Character victim)
        {
            if (victim == null) return;

            _totalKills++;
            _sessionKills++;
            _lastKillTime = Time.time;

            // Track by creature type
            string creatureType = GetCreatureType(victim);
            if (!string.IsNullOrEmpty(creatureType))
            {
                if (_killsByCreature.ContainsKey(creatureType))
                {
                    _killsByCreature[creatureType]++;
                }
                else
                {
                    _killsByCreature[creatureType] = 1;
                }
            }

            // Award XP through progression system
            if (_progression != null)
            {
                _progression.OnKill(victim);
            }

            // Mark dirty for batched save (don't save immediately)
            MarkDirty();

            // ONLY log when verbose logging is explicitly enabled
            // This keeps logs clean while still available for debugging
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionKillTracker] {_companion?.GetDisplayName()} killed {creatureType}. Total kills: {_totalKills}");
            }
        }

        /// <summary>
        /// Records a death of this companion.
        /// Called by CompanionDeathHandler.
        /// </summary>
        public void OnCompanionDeath()
        {
            _deaths++;
            
            // Deaths are important - save immediately
            FlushToZDO();
            
            // Only log on owner's client when verbose
            if (VerboseLogging && IsLocalOwnerCompanion)
            {
                Debug.Log($"[CompanionKillTracker] {_companion?.GetDisplayName()} died. Deaths: {_deaths}");
            }
        }

        /// <summary>
        /// Gets the top N creatures killed by kill count.
        /// </summary>
        public List<KeyValuePair<string, int>> GetTopKills(int count)
        {
            return _killsByCreature
                .OrderByDescending(kvp => kvp.Value)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Gets kills for a specific creature type.
        /// </summary>
        public int GetKillsForCreature(string creatureType)
        {
            if (string.IsNullOrEmpty(creatureType)) return 0;
            return _killsByCreature.TryGetValue(creatureType, out int kills) ? kills : 0;
        }

        /// <summary>
        /// Resets session statistics (called on companion summon/respawn).
        /// </summary>
        public void ResetSessionStats()
        {
            _sessionKills = 0;
            _lastKillTime = Time.time;
        }

        /// <summary>
        /// Gets a formatted summary of kill statistics.
        /// </summary>
        public string GetStatsSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Total Kills: {_totalKills}");
            sb.AppendLine($"Deaths: {_deaths}");

            if (_deaths > 0)
            {
                float kd = (float)_totalKills / _deaths;
                sb.AppendLine($"K/D Ratio: {kd:F2}");
            }

            if (_killsByCreature.Count > 0)
            {
                sb.AppendLine("Top kills:");
                foreach (var kvp in GetTopKills(3))
                {
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
                }
            }

            return sb.ToString();
        }
        
        /// <summary>
        /// Forces an immediate save of all cached data.
        /// Call this before important events like logout or death.
        /// </summary>
        public void ForceSave()
        {
            if (_isDirty)
            {
                FlushToZDO();
            }
        }

        #endregion

        #region Caching & Persistence
        
        /// <summary>
        /// Marks the tracker as having unsaved changes.
        /// </summary>
        private void MarkDirty()
        {
            _isDirty = true;
            _lastChangeTime = Time.time;
        }
        
        /// <summary>
        /// Flushes all cached changes to ZDO.
        /// </summary>
        private void FlushToZDO()
        {
            if (!_isDirty) return;
            
            SaveToZDO();
            _isDirty = false;
            _lastSaveTime = Time.time;
        }

        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            if (_nview == null || !_nview.IsValid()) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            try
            {
                zdo.Set(ZDO_TOTAL_KILLS, _totalKills);
                zdo.Set(ZDO_DEATHS, _deaths);

                // Serialize kills by creature as a simple string format
                // Format: "creature1:count1;creature2:count2;..."
                var killsData = string.Join(";", _killsByCreature.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                zdo.Set(ZDO_KILLS_DATA, killsData);
                
                // Only log save on owner's client or if verbose
                if (VerboseLogging && IsLocalOwnerCompanion)
                {
                    Debug.Log($"[CompanionKillTracker] Saved stats for {_companion?.GetDisplayName()}: {_totalKills} kills, {_deaths} deaths");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKillTracker] Failed to save: {ex.Message}");
            }
        }

        public void LoadFromZDO()
        {
            if (_nview == null || !_nview.IsValid()) return;
            
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            try
            {
                _totalKills = zdo.GetInt(ZDO_TOTAL_KILLS, 0);
                _deaths = zdo.GetInt(ZDO_DEATHS, 0);

                // Deserialize kills by creature
                string killsData = zdo.GetString(ZDO_KILLS_DATA, "");
                _killsByCreature.Clear();

                if (!string.IsNullOrEmpty(killsData))
                {
                    var entries = killsData.Split(';');
                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry)) continue;

                        var parts = entry.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out int count))
                        {
                            _killsByCreature[parts[0]] = count;
                        }
                    }
                }

                // Only log on owner's client
                if (VerboseLogging && IsLocalOwnerCompanion)
                {
                    Debug.Log($"[CompanionKillTracker] Loaded stats - Kills: {_totalKills}, Deaths: {_deaths}, Types: {_killsByCreature.Count}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKillTracker] Failed to load: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets kill tracker data for vault storage.
        /// Format: "totalKills|deaths|creature1:count1;creature2:count2"
        /// </summary>
        public string GetKillsDataForVault()
        {
            var killsData = string.Join(";", _killsByCreature.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            return $"{_totalKills}|{_deaths}|{killsData}";
        }
        
        /// <summary>
        /// Restores kill tracker data from vault.
        /// </summary>
        public void RestoreKillsFromVault(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                Debug.Log($"[CompanionKillTracker] RestoreKillsFromVault called with null/empty data for {_companion?.companionName}");
                return;
            }
            
            Debug.Log($"[CompanionKillTracker] RestoreKillsFromVault: raw data = '{data}' for {_companion?.companionName}");
            
            try
            {
                var parts = data.Split('|');
                if (parts.Length >= 2)
                {
                    if (int.TryParse(parts[0], out int kills))
                        _totalKills = kills;
                    if (int.TryParse(parts[1], out int deaths))
                        _deaths = deaths;
                }
                
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
                {
                    _killsByCreature.Clear();
                    var entries = parts[2].Split(';');
                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrEmpty(entry)) continue;
                        var kvpParts = entry.Split(':');
                        if (kvpParts.Length == 2 && int.TryParse(kvpParts[1], out int count))
                        {
                            _killsByCreature[kvpParts[0]] = count;
                        }
                    }
                }
                
                // Always log restoration result
                Debug.Log($"[CompanionKillTracker] Restored from vault: {_totalKills} kills, {_deaths} deaths for {_companion?.companionName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKillTracker] Failed to restore from vault: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets a consistent creature type identifier from a Character.
        /// </summary>
        private string GetCreatureType(Character character)
        {
            if (character == null) return null;

            // Try to get the prefab name
            string name = character.gameObject.name;

            // Remove (Clone) suffix
            if (name.EndsWith("(Clone)"))
            {
                name = name.Substring(0, name.Length - 7);
            }

            // Remove any instance numbers
            int parenIndex = name.IndexOf('(');
            if (parenIndex > 0)
            {
                name = name.Substring(0, parenIndex);
            }

            return name.Trim();
        }

        #endregion
    }
}
