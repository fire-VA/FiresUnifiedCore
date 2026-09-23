using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    /// <summary>
    /// Manages skills for companion NPCs.
    /// Skills increase through use like player skills, but don't decrease on death.
    /// Skills are persisted to ZDO for saving/loading.
    /// </summary>
    public class CompanionSkills : MonoBehaviour
    {
        [Header("Skill Settings")]
        public float skillGainMultiplier = 1.0f;
        public float maxSkillLevel = 100f;
    
  [Header("Debug")]
        public static bool VerboseLogging = false;
        
    private CompanionController _companion;
        private CompanionProgression _progression;
        private ZNetView _nview;
        private bool _initialized;
    
        // Level up effect prefab reference (cached)
        private static GameObject _levelUpEffectPrefab;

        // Skills storage - maps skill type to level and accumulator
    private Dictionary<Skills.SkillType, SkillData> _skills = new Dictionary<Skills.SkillType, SkillData>();

        // Skill data structure
   [Serializable]
        public class SkillData
        {
            public float level;
   public float accumulator; // Progress toward next level
    
   public SkillData()
  {
       level = 0f;
   accumulator = 0f;
 }
      
 public SkillData(float lvl, float acc)
       {
   level = lvl;
     accumulator = acc;
            }
        }

#region Unity Lifecycle

        private void Awake()
  {
          _companion = GetComponent<CompanionController>();
            _progression = GetComponent<CompanionProgression>();
            _nview = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
  }

        private Character _character;

        // Companion weapon skill adds 0.5 % damage per level (skill 0 deals full damage).
        private const float DamageBonusPerSkillLevel = 0.005f;

        private void Start()
    {
      Initialize();
        }

 #endregion

        #region Initialization

        private void Initialize()
        {
if (_initialized) return;

     // Initialize default skills at level 0
   InitializeDefaultSkills();

   // Load from ZDO
       LoadFromZDO();

         // Cache level up effect
   CacheLevelUpEffect();

       _initialized = true;
  if (VerboseLogging)
    Debug.Log($"[CompanionSkills] Initialized skills for {_companion?.companionName}");
}

        private void InitializeDefaultSkills()
     {
// Combat skills
 AddSkillIfMissing(Skills.SkillType.Swords);
         AddSkillIfMissing(Skills.SkillType.Knives);
            AddSkillIfMissing(Skills.SkillType.Clubs);
    AddSkillIfMissing(Skills.SkillType.Polearms);
        AddSkillIfMissing(Skills.SkillType.Spears);
         AddSkillIfMissing(Skills.SkillType.Axes);
            AddSkillIfMissing(Skills.SkillType.Bows);
         AddSkillIfMissing(Skills.SkillType.Crossbows);
     AddSkillIfMissing(Skills.SkillType.Unarmed);
            AddSkillIfMissing(Skills.SkillType.Blocking);
    AddSkillIfMissing(Skills.SkillType.ElementalMagic);
          AddSkillIfMissing(Skills.SkillType.BloodMagic);

            // Utility skills
         AddSkillIfMissing(Skills.SkillType.Run);
            AddSkillIfMissing(Skills.SkillType.Swim);
   AddSkillIfMissing(Skills.SkillType.Jump);
            AddSkillIfMissing(Skills.SkillType.Sneak);
        }

    private void AddSkillIfMissing(Skills.SkillType skillType)
     {
       if (!_skills.ContainsKey(skillType))
 {
     _skills[skillType] = new SkillData();
  }
     }
        
        /// <summary>The player's own skill level-up effect (vfx_skilllevelup), taken from the Player prefab: it is
        /// not registered in ZNetScene, and it carries no ZNetView, so it stays local like the player's.</summary>
        private void CacheLevelUpEffect()
        {
            if (_levelUpEffectPrefab != null) return;
            var levelUpEffects = ZNetScene.instance?.GetPrefab("Player")?.GetComponent<Player>()?.m_skillLevelupEffects?.m_effectPrefabs;
            if (levelUpEffects != null && levelUpEffects.Length > 0)
                _levelUpEffectPrefab = levelUpEffects[0].m_prefab;
        }

#endregion

   #region Skill Operations

      /// <summary>
     /// Gets the current level of a skill.
        /// </summary>
        /// <summary>The level with status-effect skill bonuses (armor sets, meads), floored, like vanilla Skills.GetSkillLevel.</summary>
  public float GetSkillLevel(Skills.SkillType skillType)
      {
            float level = _skills.TryGetValue(skillType, out var data) ? data.level : 0f;
            var seman = _character != null ? _character.GetSEMan() : null;
            if (seman != null && skillType != Skills.SkillType.None) seman.ModifySkillLevel(skillType, ref level);
            return Mathf.Floor(level);
   }

        /// <summary>The weapon damage factor the companion's skill gives, on native and fallback hits alike.</summary>
        public float GetDamageSkillFactor(Skills.SkillType skillType) => 1f + GetSkillLevel(skillType) * DamageBonusPerSkillLevel;

        /// <summary>
        /// Sets the level of a skill directly.
    /// </summary>
     public void SetSkillLevel(Skills.SkillType skillType, float level)
        {
            level = Mathf.Clamp(level, 0f, maxSkillLevel);

   if (!_skills.ContainsKey(skillType))
   {
           _skills[skillType] = new SkillData();
     }

   _skills[skillType].level = level;
           _skills[skillType].accumulator = 0f;

        SaveToZDO();
         Debug.Log($"[CompanionSkills] Set {skillType} to level {level}");
  }

     /// <summary>
    /// Raises a skill by the specified amount.
 /// This is the main method called during combat/activities.
        /// </summary>
        public void RaiseSkill(Skills.SkillType skillType, float factor = 1f)
{
 if (!_skills.ContainsKey(skillType))
  {
       _skills[skillType] = new SkillData();
  }

      var data = _skills[skillType];

            // Don't gain XP if already at max
          if (data.level >= maxSkillLevel) return;

     // Calculate XP gain (similar to player skills), with status-effect raise bonuses like Player.RaiseSkill
            float raiseMultiplier = 1f;
            var seman = _character != null ? _character.GetSEMan() : null;
            if (seman != null) seman.ModifyRaiseSkill(skillType, ref raiseMultiplier);
  float gainAmount = factor * skillGainMultiplier * raiseMultiplier;

          // Higher skill levels require more XP to level up
   float levelFactor = 1f + (data.level * 0.05f);
       float requiredForLevel = 10f * levelFactor;

  float previousLevel = data.level;
       data.accumulator += gainAmount;
      
       if (VerboseLogging)
 Debug.Log($"[CompanionSkills] {_companion?.companionName} gained {gainAmount:F2} XP in {skillType} ({data.accumulator:F2}/{requiredForLevel:F2})");

            // Check for level up
          while (data.accumulator >= requiredForLevel && data.level < maxSkillLevel)
      {
          data.accumulator -= requiredForLevel;
        data.level = Mathf.Min(data.level + 1f, maxSkillLevel);
         
 // Recalculate requirement for next level
            levelFactor = 1f + (data.level * 0.05f);
            requiredForLevel = 10f * levelFactor;

// Only log level-ups for tamed companions to reduce log spam
if (_companion?.isTamed == true)
{
    Debug.Log($"[CompanionSkills] {_companion?.companionName} leveled up {skillType} to {data.level}!");
}
       
       // Show level up effect and message
      ShowLevelUpEffect(skillType, data.level);
      
       // Award XP through progression system
       if (_progression != null)
       {
           _progression.OnSkillLevelUp(skillType, data.level);
       }
    }

      // Periodically save (not every skill gain for performance)
     if (Time.frameCount % 100 == 0)
  {
       SaveToZDO();
      }
   }
        
        /// <summary>
      /// Shows visual and text feedback when a skill levels up.
   /// </summary>
        private void ShowLevelUpEffect(Skills.SkillType skillType, float newLevel)
  {
          // Skip during local player respawn / loading-screen window — the level-up
          // VFX prefab is a Valheim FX object with a ZNetView, and Instantiating
          // a ZNetView during IsTeleporting=true creates a ZDO that races zone
          // streaming and deadlocks the load. Skill XP/level changes already
          // happened in memory; we only suppress the visual.
          if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

          // A local effect like the player's own, so only for a player close enough to see it.
            var effectPos = transform.position + Vector3.up * 1f;
            if (_levelUpEffectPrefab != null && Core.NpcFxRange.NearLocalPlayer(effectPos))
                UnityEngine.Object.Instantiate(_levelUpEffectPrefab, effectPos, Quaternion.identity);
      
    // Show floating text above companion
           ShowSkillLevelUpText(skillType, newLevel);
        }
        
        private void ShowSkillLevelUpText(Skills.SkillType skillType, float level)
 {
         // Use Valheim's DamageText system to show floating text
            if (DamageText.instance != null)
{
             string skillName = GetSkillDisplayName(skillType);
     string text = $"{skillName} {level:F0}";
       
         // Show as heal/positive text (green)
         DamageText.instance.ShowText(
     DamageText.TextType.Heal,
         transform.position + Vector3.up * 2f,
          text,
             true
);
        }
   
 // Also show message to owner
      var owner = _companion?.GetOwner();
   if (owner != null && owner == Player.m_localPlayer)
  {
       string skillName = GetSkillDisplayName(skillType);
     MessageHud.instance?.ShowMessage(
         MessageHud.MessageType.TopLeft,
           $"{_companion?.GetDisplayName()} learned {skillName} level {level:F0}!"
         );
  }
        }
  
      private string GetSkillDisplayName(Skills.SkillType skillType)
    {
            // Try to get localized name
     string locKey = "$skill_" + skillType.ToString().ToLower();
           string localized = Localization.instance?.Localize(locKey);
            
 if (!string.IsNullOrEmpty(localized) && !localized.StartsWith("$"))
    {
           return localized;
 }
 
         // Fallback to enum name
            return skillType.ToString();
   }

  /// <summary>
        /// Gets all skills and their levels.
   /// </summary>
   public Dictionary<Skills.SkillType, float> GetAllSkills()
  {
        var result = new Dictionary<Skills.SkillType, float>();
    foreach (var kvp in _skills)
      {
          result[kvp.Key] = kvp.Value.level;
            }
       return result;
   }

        /// <summary>
     /// Resets all skills to zero.
 /// </summary>
        public void ResetAllSkills()
        {
            foreach (var skillType in _skills.Keys)
   {
_skills[skillType] = new SkillData();
     }
    SaveToZDO();
       Debug.Log($"[CompanionSkills] Reset all skills for {_companion?.companionName}");
     }

        /// <summary>
        /// Gets the skill factor (0-1 based on level) for damage/effectiveness calculations.
        /// </summary>
        public float GetSkillFactor(Skills.SkillType skillType)
   {
   float level = GetSkillLevel(skillType);
         return Mathf.Clamp01(level / maxSkillLevel);
 }

 /// <summary>
    /// Gets the random skill factor for variable effects (like Valheim does).
        /// Returns a value between min and max based on skill level.
        /// </summary>
        public float GetRandomSkillFactor(Skills.SkillType skillType, float min = 0.4f, float max = 1f)
    {
  float skillFactor = GetSkillFactor(skillType);
   float baseFactor = Mathf.Lerp(min, max, skillFactor);
   
         // Add some randomness
            float variance = (1f - skillFactor) * 0.15f;
     return baseFactor + UnityEngine.Random.Range(-variance, variance);
        }

        #endregion

    #region Persistence

        public void LoadFromZDO()
      {
      var zdo = _nview?.GetZDO();
   if (zdo == null) return;

 try
       {
         string skillsData = zdo.GetString("companion_skills", "");
   if (string.IsNullOrEmpty(skillsData)) return;

    ParseSkillsData(skillsData);

  if (VerboseLogging)
   Debug.Log($"[CompanionSkills] Loaded {_skills.Count} skills from ZDO");
  }
        catch (Exception ex)
   {
      Debug.LogWarning($"[CompanionSkills] Failed to load skills: {ex.Message}");
    }
        }

        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            var zdo = _nview?.GetZDO();
  if (zdo == null) return;

     try
  {
     string skillsData = SerializeSkillsData();
             zdo.Set("companion_skills", skillsData);

        if (VerboseLogging)
    Debug.Log($"[CompanionSkills] Saved skills to ZDO");
        }
      catch (Exception ex)
            {
        Debug.LogWarning($"[CompanionSkills] Failed to save skills: {ex.Message}");
        }
    }
  
        /// <summary>
        /// Gets skills data serialized for vault storage.
      /// </summary>
    public string GetSkillsDataForVault()
    {
 return SerializeSkillsData();
        }
        
        /// <summary>
        /// Restores skills from vault data string.
        /// </summary>
  public void RestoreSkillsFromVault(string skillsData)
        {
            if (string.IsNullOrEmpty(skillsData)) return;
            
    try
{
          ParseSkillsData(skillsData);
      SaveToZDO(); // Also save to ZDO so it persists
   
              if (VerboseLogging)
  Debug.Log($"[CompanionSkills] Restored skills from vault for {_companion?.companionName}");
            }
 catch (Exception ex)
       {
Debug.LogWarning($"[CompanionSkills] Failed to restore skills from vault: {ex.Message}");
    }
        }
        
    /// <summary>
        /// Serializes skills data to string format.
        /// Format: "SkillType:level:accumulator;SkillType:level:accumulator;..."
        /// </summary>
        private string SerializeSkillsData()
        {
            var parts = new List<string>();
      foreach (var kvp in _skills)
  {
                if (kvp.Value.level > 0 || kvp.Value.accumulator > 0)
                {
         parts.Add($"{kvp.Key}:{kvp.Value.level:F2}:{kvp.Value.accumulator:F2}");
   }
            }
       return string.Join(";", parts);
        }
        
        /// <summary>
        /// Parses skills data from string format.
   /// </summary>
        private void ParseSkillsData(string skillsData)
      {
            // Parse skills data (format: "SkillType:level:accumulator;SkillType:level:accumulator;...")
    var pairs = skillsData.Split(';');
  foreach (var pair in pairs)
            {
if (string.IsNullOrEmpty(pair)) continue;

             var parts = pair.Split(':');
    if (parts.Length >= 2)
         {
        if (Enum.TryParse<Skills.SkillType>(parts[0], out var skillType))
              {
     float level = 0f;
    float accumulator = 0f;

    float.TryParse(parts[1], out level);
    if (parts.Length >= 3)
             float.TryParse(parts[2], out accumulator);

                   _skills[skillType] = new SkillData(level, accumulator);
        }
         }
     }
        }

        #endregion

        #region Skill Info Helpers

        /// <summary>
      /// Information about a skill including progress toward next level.
        /// </summary>
    public struct SkillInfo
   {
   public Skills.SkillType skillType;
  public string displayName;
   public float level;
public float accumulator;
      public float progressPercent; // 0-100
        }

      /// <summary>
 /// Gets all skills with detailed progress information.
     /// </summary>
        public List<SkillInfo> GetAllSkillsWithProgress()
     {
    var result = new List<SkillInfo>();
 foreach (var kvp in _skills)
       {
      float levelFactor = 1f + (kvp.Value.level * 0.05f);
            float requiredForLevel = 10f * levelFactor;
         float progress = (kvp.Value.accumulator / requiredForLevel) * 100f;
 
         result.Add(new SkillInfo
     {
  skillType = kvp.Key,
   displayName = GetSkillDisplayName(kvp.Key),
       level = kvp.Value.level,
   accumulator = kvp.Value.accumulator,
  progressPercent = Mathf.Clamp(progress, 0f, 100f)
      });
       }
     
           // Sort by level (highest first), then by name
     result.Sort((a, b) =>
       {
      int levelCompare = b.level.CompareTo(a.level);
     if (levelCompare != 0) return levelCompare;
 return a.displayName.CompareTo(b.displayName);
       });
    
   return result;
        }
  
        /// <summary>
 /// Gets a formatted string of all skills for display.
 /// </summary>
     public string GetSkillsSummary()
 {
   var lines = new List<string>();
       foreach (var kvp in _skills)
        {
  if (kvp.Value.level > 0)
 {
        string skillName = GetSkillDisplayName(kvp.Key);
          lines.Add($"{skillName}: {kvp.Value.level:F0}");
       }
       }
return lines.Count > 0 ? string.Join("\n", lines) : "No skills trained";
}

  /// <summary>
        /// Gets the total combined skill levels.
     /// </summary>
      public float GetTotalSkillLevels()
   {
   float total = 0f;
    foreach (var kvp in _skills)
    {
          total += kvp.Value.level;
   }
     return total;
        }

        /// <summary>
        /// Gets the highest skill type and level.
     /// </summary>
      public (Skills.SkillType skillType, float level) GetHighestSkill()
      {
         Skills.SkillType highest = Skills.SkillType.None;
         float highestLevel = 0f;

foreach (var kvp in _skills)
            {
        if (kvp.Value.level > highestLevel)
 {
      highest = kvp.Key;
     highestLevel = kvp.Value.level;
        }
   }

  return (highest, highestLevel);
    }

  #endregion
    }
}
