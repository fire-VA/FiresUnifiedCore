using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Tracks archetype-related statistics and preferences for a companion.
    /// This data persists across sessions and is displayed in the companion stat screen.
    /// 
    /// TRACKED STATISTICS:
    /// - Time spent in each archetype role
    /// - Favorite weapon combinations
    /// - Combat action counts (blocks, parries, dodges, backstabs, etc.)
    /// - Archetype effectiveness metrics
    /// </summary>
    [Serializable]
    public class ArchetypeStatistics
    {
        #region Archetype Usage
        
        /// <summary>Time spent in each archetype (seconds)</summary>
        public Dictionary<ArchetypeClass, float> TimeInArchetype = new Dictionary<ArchetypeClass, float>();
        
        /// <summary>Number of times each archetype was selected</summary>
        public Dictionary<ArchetypeClass, int> ArchetypeSelectionCount = new Dictionary<ArchetypeClass, int>();
        
        /// <summary>Current active archetype</summary>
        public ArchetypeClass CurrentArchetype = ArchetypeClass.None;
        
        /// <summary>When current archetype became active</summary>
        public float CurrentArchetypeStartTime;
        
        #endregion
        
        #region Combat Statistics
        
        /// <summary>Total blocks performed</summary>
        public int TotalBlocks;
        
        /// <summary>Successful blocks (reduced damage)</summary>
        public int SuccessfulBlocks;
        
        /// <summary>Perfect parries</summary>
        public int PerfectParries;
        
        /// <summary>Damage blocked total</summary>
        public float TotalDamageBlocked;
        
        /// <summary>Time spent blocking (seconds)</summary>
        public float TimeSpentBlocking;
        
        /// <summary>Dodges performed</summary>
        public int DodgesPerformed;
        
        /// <summary>Successful dodges (avoided damage)</summary>
        public int SuccessfulDodges;
        
        /// <summary>Backstab hits landed (rogue)</summary>
        public int BackstabHits;
        
        /// <summary>Critical hits landed</summary>
        public int CriticalHits;
        
        /// <summary>Taunts used (tank)</summary>
        public int TauntsUsed;
        
        /// <summary>Enemies taunted total</summary>
        public int EnemiesTaunted;
        
        /// <summary>Allies healed (healer/paladin)</summary>
        public int AlliesHealed;
        
        /// <summary>Total healing done</summary>
        public float TotalHealingDone;
        
        /// <summary>Buffs applied to allies</summary>
        public int BuffsApplied;
        
        /// <summary>Eitr spent on spells</summary>
        public float TotalEitrSpent;
        
        /// <summary>Berserk mode activations</summary>
        public int BerserkActivations;
        
        #endregion
        
        #region Weapon Statistics
        
        /// <summary>Time spent with each weapon skill type</summary>
        public Dictionary<string, float> TimeWithWeaponType = new Dictionary<string, float>();
        
        /// <summary>Kills with each weapon skill type</summary>
        public Dictionary<string, int> KillsWithWeaponType = new Dictionary<string, int>();
        
        /// <summary>Current weapon skill type being used</summary>
        public string CurrentWeaponType;
        
        /// <summary>When current weapon type started being used</summary>
        public float CurrentWeaponStartTime;
        
        #endregion
        
        #region Derived Statistics
        
        /// <summary>
        /// Gets the companion's favored archetype (most time spent).
        /// </summary>
        public ArchetypeClass GetFavoredArchetype()
        {
            if (TimeInArchetype == null || TimeInArchetype.Count == 0)
                return ArchetypeClass.None;
            
            return TimeInArchetype.OrderByDescending(kvp => kvp.Value).First().Key;
        }
        
        /// <summary>
        /// Gets total time spent in archetype roles.
        /// </summary>
        public float GetTotalArchetypeTime()
        {
            return TimeInArchetype?.Values.Sum() ?? 0f;
        }
        
        /// <summary>
        /// Gets the percentage of time spent in a specific archetype.
        /// </summary>
        public float GetArchetypePercentage(ArchetypeClass archetype)
        {
            float total = GetTotalArchetypeTime();
            if (total <= 0) return 0f;
            
            return TimeInArchetype.TryGetValue(archetype, out float time) ? (time / total * 100f) : 0f;
        }
        
        /// <summary>
        /// Gets the companion's favorite weapon type (most time spent).
        /// </summary>
        public string GetFavoriteWeaponType()
        {
            if (TimeWithWeaponType == null || TimeWithWeaponType.Count == 0)
                return "None";
            
            return TimeWithWeaponType.OrderByDescending(kvp => kvp.Value).First().Key;
        }
        
        /// <summary>
        /// Gets the companion's most lethal weapon type (most kills).
        /// </summary>
        public string GetMostLethalWeaponType()
        {
            if (KillsWithWeaponType == null || KillsWithWeaponType.Count == 0)
                return "None";
            
            return KillsWithWeaponType.OrderByDescending(kvp => kvp.Value).First().Key;
        }
        
        /// <summary>
        /// Gets block success rate as percentage.
        /// </summary>
        public float GetBlockSuccessRate()
        {
            if (TotalBlocks <= 0) return 0f;
            return (float)SuccessfulBlocks / TotalBlocks * 100f;
        }
        
        /// <summary>
        /// Gets parry rate (parries / total blocks).
        /// </summary>
        public float GetParryRate()
        {
            if (TotalBlocks <= 0) return 0f;
            return (float)PerfectParries / TotalBlocks * 100f;
        }
        
        /// <summary>
        /// Gets dodge success rate as percentage.
        /// </summary>
        public float GetDodgeSuccessRate()
        {
            if (DodgesPerformed <= 0) return 0f;
            return (float)SuccessfulDodges / DodgesPerformed * 100f;
        }
        
        /// <summary>
        /// Gets average taunt effectiveness (enemies taunted per taunt).
        /// </summary>
        public float GetTauntEffectiveness()
        {
            if (TauntsUsed <= 0) return 0f;
            return (float)EnemiesTaunted / TauntsUsed;
        }
        
        #endregion
        
        #region Update Methods
        
        /// <summary>
        /// Updates the current archetype and records time spent.
        /// Call this when archetype changes.
        /// </summary>
        public void SetCurrentArchetype(ArchetypeClass newArchetype)
        {
            // Record time in previous archetype
            if (CurrentArchetype != ArchetypeClass.None && CurrentArchetypeStartTime > 0)
            {
                float timeSpent = Time.time - CurrentArchetypeStartTime;
                AddArchetypeTime(CurrentArchetype, timeSpent);
            }
            
            // Set new archetype
            CurrentArchetype = newArchetype;
            CurrentArchetypeStartTime = Time.time;
            
            // Track selection count
            if (newArchetype != ArchetypeClass.None)
            {
                if (!ArchetypeSelectionCount.ContainsKey(newArchetype))
                    ArchetypeSelectionCount[newArchetype] = 0;
                ArchetypeSelectionCount[newArchetype]++;
            }
        }
        
        /// <summary>
        /// Adds time to an archetype's total.
        /// </summary>
        public void AddArchetypeTime(ArchetypeClass archetype, float seconds)
        {
            if (archetype == ArchetypeClass.None || seconds <= 0) return;
            
            if (!TimeInArchetype.ContainsKey(archetype))
                TimeInArchetype[archetype] = 0;
            TimeInArchetype[archetype] += seconds;
        }
        
        /// <summary>
        /// Updates the current weapon type and records time spent.
        /// </summary>
        public void SetCurrentWeaponType(string weaponType)
        {
            // Record time with previous weapon
            if (!string.IsNullOrEmpty(CurrentWeaponType) && CurrentWeaponStartTime > 0)
            {
                float timeSpent = Time.time - CurrentWeaponStartTime;
                AddWeaponTime(CurrentWeaponType, timeSpent);
            }
            
            CurrentWeaponType = weaponType;
            CurrentWeaponStartTime = Time.time;
        }
        
        /// <summary>
        /// Adds time to a weapon type's total.
        /// </summary>
        public void AddWeaponTime(string weaponType, float seconds)
        {
            if (string.IsNullOrEmpty(weaponType) || seconds <= 0) return;
            
            if (!TimeWithWeaponType.ContainsKey(weaponType))
                TimeWithWeaponType[weaponType] = 0;
            TimeWithWeaponType[weaponType] += seconds;
        }
        
        /// <summary>
        /// Records a kill with the current weapon type.
        /// </summary>
        public void RecordKillWithWeapon(string weaponType = null)
        {
            string type = weaponType ?? CurrentWeaponType;
            if (string.IsNullOrEmpty(type)) return;
            
            if (!KillsWithWeaponType.ContainsKey(type))
                KillsWithWeaponType[type] = 0;
            KillsWithWeaponType[type]++;
        }
        
        /// <summary>
        /// Records a block event.
        /// </summary>
        public void RecordBlock(bool successful, float damageBlocked, bool wasParry)
        {
            TotalBlocks++;
            if (successful)
            {
                SuccessfulBlocks++;
                TotalDamageBlocked += damageBlocked;
            }
            if (wasParry)
            {
                PerfectParries++;
            }
        }
        
        /// <summary>
        /// Records time spent blocking.
        /// </summary>
        public void AddBlockingTime(float seconds)
        {
            TimeSpentBlocking += seconds;
        }
        
        /// <summary>
        /// Records a dodge event.
        /// </summary>
        public void RecordDodge(bool successful)
        {
            DodgesPerformed++;
            if (successful)
                SuccessfulDodges++;
        }
        
        /// <summary>
        /// Records a taunt use.
        /// </summary>
        public void RecordTaunt(int enemiesAffected)
        {
            TauntsUsed++;
            EnemiesTaunted += enemiesAffected;
        }
        
        /// <summary>
        /// Records a backstab hit (rogue).
        /// </summary>
        public void RecordBackstab()
        {
            BackstabHits++;
        }
        
        /// <summary>
        /// Records a critical hit.
        /// </summary>
        public void RecordCriticalHit()
        {
            CriticalHits++;
        }
        
        /// <summary>
        /// Records healing done.
        /// </summary>
        public void RecordHealing(float amount)
        {
            AlliesHealed++;
            TotalHealingDone += amount;
        }
        
        /// <summary>
        /// Records a buff applied.
        /// </summary>
        public void RecordBuffApplied()
        {
            BuffsApplied++;
        }
        
        /// <summary>
        /// Records eitr spent.
        /// </summary>
        public void RecordEitrSpent(float amount)
        {
            TotalEitrSpent += amount;
        }
        
        /// <summary>
        /// Records berserk mode activation.
        /// </summary>
        public void RecordBerserkActivation()
        {
            BerserkActivations++;
        }
        
        /// <summary>
        /// Finalizes current tracking (call before save).
        /// Records any in-progress time accumulation.
        /// </summary>
        public void FinalizeTracking()
        {
            // Record current archetype time
            if (CurrentArchetype != ArchetypeClass.None && CurrentArchetypeStartTime > 0)
            {
                float timeSpent = Time.time - CurrentArchetypeStartTime;
                AddArchetypeTime(CurrentArchetype, timeSpent);
                CurrentArchetypeStartTime = Time.time; // Reset for continued tracking
            }
            
            // Record current weapon time
            if (!string.IsNullOrEmpty(CurrentWeaponType) && CurrentWeaponStartTime > 0)
            {
                float timeSpent = Time.time - CurrentWeaponStartTime;
                AddWeaponTime(CurrentWeaponType, timeSpent);
                CurrentWeaponStartTime = Time.time; // Reset for continued tracking
            }
        }
        
        #endregion
        
        #region Serialization
        
        /// <summary>
        /// Serializes statistics to a string for vault storage.
        /// Format: key=value pairs separated by |
        /// </summary>
        public string Serialize()
        {
            FinalizeTracking();
            
            var parts = new List<string>();
            
            // Archetype times
            foreach (var kvp in TimeInArchetype)
            {
                parts.Add($"AT_{(int)kvp.Key}={kvp.Value:F1}");
            }
            
            // Archetype selection counts
            foreach (var kvp in ArchetypeSelectionCount)
            {
                parts.Add($"AC_{(int)kvp.Key}={kvp.Value}");
            }
            
            // Combat stats
            parts.Add($"TB={TotalBlocks}");
            parts.Add($"SB={SuccessfulBlocks}");
            parts.Add($"PP={PerfectParries}");
            parts.Add($"TDB={TotalDamageBlocked:F1}");
            parts.Add($"TSB={TimeSpentBlocking:F1}");
            parts.Add($"DP={DodgesPerformed}");
            parts.Add($"SD={SuccessfulDodges}");
            parts.Add($"BH={BackstabHits}");
            parts.Add($"CH={CriticalHits}");
            parts.Add($"TU={TauntsUsed}");
            parts.Add($"ET={EnemiesTaunted}");
            parts.Add($"AH={AlliesHealed}");
            parts.Add($"THD={TotalHealingDone:F1}");
            parts.Add($"BA={BuffsApplied}");
            parts.Add($"TES={TotalEitrSpent:F1}");
            parts.Add($"BK={BerserkActivations}");
            
            // Weapon times
            foreach (var kvp in TimeWithWeaponType)
            {
                parts.Add($"WT_{kvp.Key}={kvp.Value:F1}");
            }
            
            // Weapon kills
            foreach (var kvp in KillsWithWeaponType)
            {
                parts.Add($"WK_{kvp.Key}={kvp.Value}");
            }
            
            return string.Join("|", parts);
        }
        
        /// <summary>
        /// Deserializes statistics from vault storage string.
        /// </summary>
        public static ArchetypeStatistics Deserialize(string data)
        {
            var stats = new ArchetypeStatistics();
            
            if (string.IsNullOrEmpty(data))
                return stats;
            
            var parts = data.Split('|');
            foreach (var part in parts)
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                
                string key = kv[0];
                string value = kv[1];
                
                try
                {
                    if (key.StartsWith("AT_"))
                    {
                        int archInt = int.Parse(key.Substring(3));
                        stats.TimeInArchetype[(ArchetypeClass)archInt] = float.Parse(value);
                    }
                    else if (key.StartsWith("AC_"))
                    {
                        int archInt = int.Parse(key.Substring(3));
                        stats.ArchetypeSelectionCount[(ArchetypeClass)archInt] = int.Parse(value);
                    }
                    else if (key.StartsWith("WT_"))
                    {
                        string weaponType = key.Substring(3);
                        stats.TimeWithWeaponType[weaponType] = float.Parse(value);
                    }
                    else if (key.StartsWith("WK_"))
                    {
                        string weaponType = key.Substring(3);
                        stats.KillsWithWeaponType[weaponType] = int.Parse(value);
                    }
                    else
                    {
                        switch (key)
                        {
                            case "TB": stats.TotalBlocks = int.Parse(value); break;
                            case "SB": stats.SuccessfulBlocks = int.Parse(value); break;
                            case "PP": stats.PerfectParries = int.Parse(value); break;
                            case "TDB": stats.TotalDamageBlocked = float.Parse(value); break;
                            case "TSB": stats.TimeSpentBlocking = float.Parse(value); break;
                            case "DP": stats.DodgesPerformed = int.Parse(value); break;
                            case "SD": stats.SuccessfulDodges = int.Parse(value); break;
                            case "BH": stats.BackstabHits = int.Parse(value); break;
                            case "CH": stats.CriticalHits = int.Parse(value); break;
                            case "TU": stats.TauntsUsed = int.Parse(value); break;
                            case "ET": stats.EnemiesTaunted = int.Parse(value); break;
                            case "AH": stats.AlliesHealed = int.Parse(value); break;
                            case "THD": stats.TotalHealingDone = float.Parse(value); break;
                            case "BA": stats.BuffsApplied = int.Parse(value); break;
                            case "TES": stats.TotalEitrSpent = float.Parse(value); break;
                            case "BK": stats.BerserkActivations = int.Parse(value); break;
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip malformed entries
                }
            }
            
            return stats;
        }
        
        #endregion
    }
}
