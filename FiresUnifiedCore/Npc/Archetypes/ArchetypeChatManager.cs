using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Manages chat bubbles for archetype-related events:
    /// - When a companion is assigned an archetype
    /// - When a companion uses a special ability
    /// 
    /// Uses the same rate-limiting and message cycling system as CompanionChatHelper
    /// to prevent spam and keep dialogue fresh.
    /// </summary>
    public static class ArchetypeChatManager
    {
        #region Rate Limiting
        
        // Track last message times to prevent spam
        private static Dictionary<string, float> _lastArchetypeAnnounceTimes = new Dictionary<string, float>();
        private static Dictionary<string, float> _lastAbilityAnnounceTimes = new Dictionary<string, float>();
        
        // Message cycling - track recently used messages to avoid repetition
        private static Dictionary<string, List<int>> _recentMessageIndices = new Dictionary<string, List<int>>();
        
        // Cooldowns
        private const float ARCHETYPE_ANNOUNCE_COOLDOWN = 120f; // Only announce archetype change every 2 minutes
        private const float ABILITY_ANNOUNCE_COOLDOWN = 30f;    // Only announce same ability every 30 seconds
        private const float GLOBAL_ABILITY_COOLDOWN = 5f;       // Global cooldown between ANY ability announcements
        private const int MAX_RECENT_MESSAGES = 3;              // Remember last 3 messages per category
        
        // Global tracking
        private static float _lastGlobalAbilityAnnounce = -100f;
        
        #endregion
        
        #region Archetype Announcements
        
        /// <summary>
        /// Announces when a companion is assigned a new archetype.
        /// Only announces if the archetype actually changed and cooldown has passed.
        /// </summary>
        public static void AnnounceArchetypeAssigned(CompanionController companion, ArchetypeClass archetype, ArchetypeClass previousArchetype)
        {
            if (companion == null) return;
            if (archetype == ArchetypeClass.None) return;
            if (archetype == previousArchetype) return; // No actual change
            
            // Only announce to owner
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            // Check cooldown
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            string key = $"{companionId}_archetype";
            
            if (_lastArchetypeAnnounceTimes.TryGetValue(key, out float lastTime))
            {
                if (Time.time - lastTime < ARCHETYPE_ANNOUNCE_COOLDOWN) return;
            }
            
            _lastArchetypeAnnounceTimes[key] = Time.time;
            
            // Get appropriate phrases for this archetype
            string[] phrases = GetArchetypeAnnouncePhrases(archetype);
            string message = GetCycledMessage($"archetype_{archetype}", phrases);
            
            // Say via chat bubble
            CompanionChatHelper.SaySpeechBubble(companion, message, 5f, false);
        }
        
        /// <summary>
        /// Gets archetype-specific announcement phrases.
        /// These are said when a companion first adopts an archetype.
        /// </summary>
        private static string[] GetArchetypeAnnouncePhrases(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                    return new[]
                    {
                        "Shield up! I'll hold the line.",
                        "I'll protect you. Stay behind me.",
                        "Time to take the hits for the team.",
                        "Nothing gets past my shield.",
                        "I'll draw their attention.",
                        "Let them come. I'm ready."
                    };
                    
                case ArchetypeClass.Paladin:
                    return new[]
                    {
                        "By the light, I shall protect us all.",
                        "My shield and my faith will see us through.",
                        "I'll guard and heal where needed.",
                        "The righteous path is clear.",
                        "Both sword and blessing are mine to give.",
                        "Let virtue guide my blade."
                    };
                    
                case ArchetypeClass.Berserker:
                    return new[]
                    {
                        "Blood and fury! Let's DO THIS!",
                        "I feel the rage building...",
                        "No mercy! No retreat!",
                        "Pain only makes me STRONGER!",
                        "They won't know what hit them!",
                        "RAAAAAGH! Who's first?!"
                    };
                    
                case ArchetypeClass.Rogue:
                    return new[]
                    {
                        "I'll stick to the shadows...",
                        "They won't see me coming.",
                        "A quick blade solves most problems.",
                        "Watch their backs. I'll be there.",
                        "Stealth and precision. My specialty.",
                        "One well-placed strike is all I need."
                    };
                    
                case ArchetypeClass.Monk:
                    return new[]
                    {
                        "My body is my weapon.",
                        "Inner peace... outer destruction.",
                        "The flow of chi guides my fists.",
                        "Balance in all things.",
                        "Discipline is true power.",
                        "Strike swift, strike true."
                    };
                    
                case ArchetypeClass.Ranger:
                    return new[]
                    {
                        "I'll pick them off from afar.",
                        "My arrows never miss.",
                        "Keep them at range. I'll handle the rest.",
                        "Eyes sharp, bow ready.",
                        "They won't get close enough to hurt you.",
                        "One shot, one down."
                    };
                    
                case ArchetypeClass.Mage:
                    return new[]
                    {
                        "The elements obey my command.",
                        "Arcane power flows through me.",
                        "Fire, ice, or lightning? Your choice.",
                        "Stand back - this could get explosive.",
                        "Magic is my sword and shield.",
                        "Let's see how they handle THIS!"
                    };
                    
                case ArchetypeClass.Healer:
                    return new[]
                    {
                        "I'll keep everyone alive.",
                        "Stay close - my magic will protect you.",
                        "Healing light, at your service.",
                        "No one dies on my watch.",
                        "Rest easy, I'll mend your wounds.",
                        "The light of restoration guides me."
                    };
                    
                default:
                    return new[]
                    {
                        "Ready for whatever comes.",
                        "I'll do my best.",
                        "Count on me."
                    };
            }
        }
        
        #endregion
        
        #region Ability Announcements
        
        /// <summary>
        /// Announces when a companion uses a special ability.
        /// Rate-limited per ability type and globally.
        /// </summary>
        public static void AnnounceAbilityUsed(CompanionController companion, string abilityName, int targetsAffected = 0)
        {
            if (companion == null || string.IsNullOrEmpty(abilityName)) return;
            
            // Only announce to owner
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            // Check global cooldown first
            if (Time.time - _lastGlobalAbilityAnnounce < GLOBAL_ABILITY_COOLDOWN) return;
            
            // Check per-ability cooldown
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            string key = $"{companionId}_{abilityName}";
            
            if (_lastAbilityAnnounceTimes.TryGetValue(key, out float lastTime))
            {
                if (Time.time - lastTime < ABILITY_ANNOUNCE_COOLDOWN) return;
            }
            
            _lastAbilityAnnounceTimes[key] = Time.time;
            _lastGlobalAbilityAnnounce = Time.time;
            
            // Get appropriate phrases for this ability
            string[] phrases = GetAbilityAnnouncePhrases(abilityName, targetsAffected);
            string message = GetCycledMessage($"ability_{abilityName}", phrases);
            
            // Say via chat bubble
            CompanionChatHelper.SaySpeechBubble(companion, message, 3f, false);
        }
        
        /// <summary>
        /// Gets ability-specific announcement phrases.
        /// These are said when a companion uses a special ability.
        /// </summary>
        private static string[] GetAbilityAnnouncePhrases(string abilityName, int targetsAffected)
        {
            // Normalize ability name for matching
            string normalizedName = abilityName.ToLowerInvariant().Replace("_", "").Replace(" ", "");
            
            switch (normalizedName)
            {
                // Tank abilities
                case "fortify":
                    return new[]
                    {
                        "Bracing for impact!",
                        "I won't fall!",
                        "Steel yourself!",
                        "Hold the line!",
                        "Not... going... down!",
                        "Fortified!"
                    };
                    
                case "taunt":
                    return new[]
                    {
                        "HEY! Over here, ugly!",
                        "Come and get me!",
                        "Fight ME, cowards!",
                        "I'm your opponent!",
                        "Leave them alone!",
                        "FACE ME!"
                    };
                    
                // Paladin abilities
                case "holysmite":
                    return new[]
                    {
                        "By the light!",
                        "Feel divine wrath!",
                        "Smite!",
                        "Holy judgment!",
                        "Purifying strike!",
                        "Light guide my blade!"
                    };
                    
                case "divineprotection":
                    return targetsAffected > 1
                        ? new[]
                        {
                            $"Divine protection shields us all!",
                            $"The light protects {targetsAffected} of us!",
                            "Stay within my aura!",
                            "By grace, we are shielded!",
                            "None shall harm my allies!"
                        }
                        : new[]
                        {
                            "The light shields me!",
                            "Divine protection!",
                            "I am guarded!",
                            "Light, be my armor!"
                        };
                    
                // Berserker abilities
                case "berserkrage":
                    return new[]
                    {
                        "RAAAARGH!!!",
                        "BLOOD AND FURY!",
                        "UNSTOPPABLE!",
                        "I... AM... RAGE!",
                        "NO PAIN! NO FEAR!",
                        "DESTROY THEM ALL!"
                    };
                    
                case "warcry":
                    return targetsAffected > 1
                        ? new[]
                        {
                            $"FOR GLORY! {targetsAffected} warriors empowered!",
                            "WARRIORS! TO BATTLE!",
                            "FIGHT WITH ME!",
                            "LET THEM HEAR OUR FURY!",
                            "CHARGE!!!"
                        }
                        : new[]
                        {
                            "FOR GLORY!",
                            "TO BATTLE!",
                            "HYAAAAAH!",
                            "CHARGE!"
                        };
                    
                // Rogue abilities
                case "stealth":
                    return new[]
                    {
                        "Now you see me...",
                        "*vanishes*",
                        "Into the shadows...",
                        "Disappearing...",
                        "Can't hit what you can't see.",
                        "Going dark."
                    };
                    
                case "poison":
                    return new[]
                    {
                        "Taste my poison!",
                        "A little something extra...",
                        "Poisoned blade!",
                        "That'll sting.",
                        "Enjoy the toxin.",
                        "Venomous strike!"
                    };
                    
                case "caltrops":
                    return new[]
                    {
                        "Watch your step!",
                        "Caltrops out!",
                        "Nasty surprise for you.",
                        "Mind the spikes!",
                        "That'll slow them down.",
                        "Trap set!"
                    };
                    
                // Monk abilities
                case "chistrike":
                    return new[]
                    {
                        "Chi flowing!",
                        "Focus... STRIKE!",
                        "Inner power!",
                        "By the way of the fist!",
                        "HIYAH!",
                        "Feel my chi!"
                    };
                    
                case "innerpeace":
                    return targetsAffected > 1
                        ? new[]
                        {
                            "Find peace, friends.",
                            "Let calm restore us.",
                            "Tranquility surrounds us.",
                            "Breathe... heal... fight.",
                            "Inner peace flows to all."
                        }
                        : new[]
                        {
                            "Finding my center...",
                            "Inner peace...",
                            "Calm restores me.",
                            "Balance restored."
                        };
                    
                // Ranger abilities
                case "eagleeye":
                    return new[]
                    {
                        "Target locked!",
                        "I see everything.",
                        "Eagle eye activated!",
                        "Perfect aim...",
                        "Nothing escapes my sight.",
                        "Marked and ready."
                    };
                    
                case "huntersmark":
                    return new[]
                    {
                        "You're marked!",
                        "Hunting you now.",
                        "Nowhere to hide!",
                        "Mark of the hunter!",
                        "Target designated!",
                        "You can't escape!"
                    };
                    
                // Mage abilities
                case "elementalinfusion":
                    return new[]
                    {
                        "Power surging!",
                        "Elements, flow through me!",
                        "Magical infusion!",
                        "Feel the arcane!",
                        "Power overwhelming!",
                        "The elements obey!"
                    };
                    
                case "arcaneshield":
                    return new[]
                    {
                        "Barrier up!",
                        "Arcane shield!",
                        "Magic protects me!",
                        "Can't touch this!",
                        "Shielded by magic!",
                        "Force field active!"
                    };
                    
                // Healer abilities
                case "purify":
                    return new[]
                    {
                        "Cleansing light!",
                        "Purified!",
                        "Be cleansed!",
                        "Away, foul magic!",
                        "Restoration!",
                        "Afflictions, begone!"
                    };
                    
                case "sanctuary":
                    return targetsAffected > 1
                        ? new[]
                        {
                            $"Sanctuary protects {targetsAffected} of us!",
                            "Safe within my aura!",
                            "Healing light surrounds us!",
                            "Find safety in the light!",
                            "All are welcome in my sanctuary!"
                        }
                        : new[]
                        {
                            "Sanctuary!",
                            "Healing aura active!",
                            "The light restores!",
                            "Safety in the light!"
                        };
                    
                case "purifyingcircle":
                    return targetsAffected > 1
                        ? new[]
                        {
                            $"Purifying {targetsAffected} allies!",
                            "Circle of cleansing!",
                            "All shall be purified!",
                            "Light, cleanse us all!",
                            "Purification spreads!"
                        }
                        : new[]
                        {
                            "Purifying circle!",
                            "Cleansing all!",
                            "Be restored!",
                            "Pure light!"
                        };
                    
                // Generic fallback
                default:
                    return new[]
                    {
                        "Ability activated!",
                        "Take this!",
                        "Here goes!",
                        "Special move!",
                        "Watch out!",
                        "Power up!"
                    };
            }
        }
        
        #endregion
        
        #region Combat Event Announcements
        
        /// <summary>
        /// Announces a perfect block or parry.
        /// </summary>
        public static void AnnounceParry(CompanionController companion)
        {
            if (companion == null) return;
            if (!ShouldAnnounceGeneric(companion, "parry", 15f)) return;
            
            string[] phrases = new[]
            {
                "Parried!",
                "Nice try!",
                "Too slow!",
                "Blocked!",
                "Not today!",
                "Deflected!"
            };
            
            string message = GetCycledMessage("parry", phrases);
            CompanionChatHelper.SaySpeechBubble(companion, message, 2f, false);
        }
        
        /// <summary>
        /// Announces a successful dodge.
        /// </summary>
        public static void AnnounceDodge(CompanionController companion)
        {
            if (companion == null) return;
            if (!ShouldAnnounceGeneric(companion, "dodge", 15f)) return;
            
            string[] phrases = new[]
            {
                "Missed me!",
                "Too slow!",
                "Can't hit this!",
                "Dodged!",
                "Nope!",
                "Ha!"
            };
            
            string message = GetCycledMessage("dodge", phrases);
            CompanionChatHelper.SaySpeechBubble(companion, message, 2f, false);
        }
        
        /// <summary>
        /// Announces a critical hit.
        /// </summary>
        public static void AnnounceCriticalHit(CompanionController companion)
        {
            if (companion == null) return;
            if (!ShouldAnnounceGeneric(companion, "crit", 20f)) return;
            
            string[] phrases = new[]
            {
                "Critical!",
                "That HAD to hurt!",
                "Direct hit!",
                "Perfect strike!",
                "Devastating blow!",
                "BOOM!"
            };
            
            string message = GetCycledMessage("crit", phrases);
            CompanionChatHelper.SaySpeechBubble(companion, message, 2f, false);
        }
        
        /// <summary>
        /// Announces a backstab.
        /// </summary>
        public static void AnnounceBackstab(CompanionController companion)
        {
            if (companion == null) return;
            if (!ShouldAnnounceGeneric(companion, "backstab", 10f)) return;
            
            string[] phrases = new[]
            {
                "Gotcha!",
                "From the shadows!",
                "Surprise!",
                "Backstab!",
                "Never saw it coming.",
                "Right in the back!"
            };
            
            string message = GetCycledMessage("backstab", phrases);
            CompanionChatHelper.SaySpeechBubble(companion, message, 2f, false);
        }
        
        /// <summary>
        /// Helper to check if a generic announcement should be made.
        /// </summary>
        private static bool ShouldAnnounceGeneric(CompanionController companion, string eventType, float cooldown)
        {
            // Only announce to owner
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return false;
            
            // Check cooldown
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            string key = $"{companionId}_event_{eventType}";
            
            if (_lastAbilityAnnounceTimes.TryGetValue(key, out float lastTime))
            {
                if (Time.time - lastTime < cooldown) return false;
            }
            
            // Random chance to not spam every event
            if (Random.value > 0.3f) return false; // 30% chance to announce
            
            _lastAbilityAnnounceTimes[key] = Time.time;
            return true;
        }
        
        #endregion
        
        #region Message Cycling
        
        /// <summary>
        /// Gets a message from the array while cycling through to avoid repetition.
        /// </summary>
        private static string GetCycledMessage(string category, string[] messages)
        {
            if (messages == null || messages.Length == 0) return string.Empty;
            if (messages.Length == 1) return messages[0];
            
            // Get or create the recent indices list for this category
            if (!_recentMessageIndices.TryGetValue(category, out var recentIndices))
            {
                recentIndices = new List<int>();
                _recentMessageIndices[category] = recentIndices;
            }
            
            // Build list of available indices (not recently used)
            var availableIndices = new List<int>();
            for (int i = 0; i < messages.Length; i++)
            {
                if (!recentIndices.Contains(i))
                {
                    availableIndices.Add(i);
                }
            }
            
            // If all messages have been used recently, reset and use all
            if (availableIndices.Count == 0)
            {
                recentIndices.Clear();
                for (int i = 0; i < messages.Length; i++)
                {
                    availableIndices.Add(i);
                }
            }
            
            // Pick a random index from available ones
            int selectedIndex = availableIndices[Random.Range(0, availableIndices.Count)];
            
            // Track this index as recently used
            recentIndices.Add(selectedIndex);
            
            // Keep the recent list from growing too large
            while (recentIndices.Count > MAX_RECENT_MESSAGES && recentIndices.Count > 0)
            {
                recentIndices.RemoveAt(0);
            }
            
            return messages[selectedIndex];
        }
        
        #endregion
        
        #region Cleanup
        
        /// <summary>
        /// Clears all tracking data. Call on game unload.
        /// </summary>
        public static void ClearTracking()
        {
            _lastArchetypeAnnounceTimes.Clear();
            _lastAbilityAnnounceTimes.Clear();
            _recentMessageIndices.Clear();
            _lastGlobalAbilityAnnounce = -100f;
        }
        
        #endregion
        
        #region Combo Announcements
        
        /// <summary>
        /// Announces when a group combo is triggered.
        /// Shows a special chat bubble from the triggering companion.
        /// </summary>
        public static void AnnounceCombo(string companionName, string comboName)
        {
            if (string.IsNullOrEmpty(companionName) || string.IsNullOrEmpty(comboName)) return;
            
            // Get combo-specific phrases
            string[] phrases = GetComboAnnouncePhrases(comboName);
            string message = GetCycledMessage($"combo_{comboName}", phrases);
            
            // Show as a top-left message since we may not have companion reference
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                $"<color=#ffcc00>{companionName}:</color> {message}");
        }
        
        /// <summary>
        /// Gets combo-specific announcement phrases.
        /// </summary>
        private static string[] GetComboAnnouncePhrases(string comboName)
        {
            switch (comboName)
            {
                case "Coordinated Assault":
                    return new[]
                    {
                        "Together we're UNSTOPPABLE!",
                        "Combined attack!",
                        "Now THAT'S teamwork!",
                        "COMBO!",
                        "Perfect coordination!"
                    };
                    
                case "Holy Bastion":
                    return new[]
                    {
                        "Divine protection for ALL!",
                        "The light shields us together!",
                        "United in faith!",
                        "Holy synergy!",
                        "Sacred barrier!"
                    };
                    
                case "Primal Storm":
                    return new[]
                    {
                        "ELEMENTAL FURY!",
                        "Feel the storm!",
                        "Magic and rage COMBINED!",
                        "DEVASTATION!",
                        "Nature's wrath unleashed!"
                    };
                    
                case "Shadow Dance":
                    return new[]
                    {
                        "Now you see us, now you don't!",
                        "Shadow synergy!",
                        "Swift and silent!",
                        "Deadly dance!",
                        "Strike from nowhere!"
                    };
                    
                case "Nature's Fury":
                    return new[]
                    {
                        "Nature empowers us!",
                        "The wild blesses our strikes!",
                        "Healing and fury combined!",
                        "Forest's might!",
                        "Natural synergy!"
                    };
                    
                case "Arcane Convergence":
                    return new[]
                    {
                        "Power AMPLIFIED!",
                        "Arcane resonance!",
                        "Magic multiplied!",
                        "Spellweave!",
                        "Ultimate magical power!"
                    };
                    
                case "Divine Harmony":
                    return new[]
                    {
                        "Light and life together!",
                        "Divine harmony!",
                        "Blessed restoration!",
                        "Holy synergy complete!",
                        "Perfect healing!"
                    };
                    
                case "Chi Resonance":
                    return new[]
                    {
                        "Chi flows between us!",
                        "Energy restored!",
                        "Inner peace shared!",
                        "Spiritual harmony!",
                        "Balance achieved!"
                    };
                    
                default:
                    return new[]
                    {
                        "COMBO!",
                        "Perfect timing!",
                        "Teamwork!",
                        "Synergy!",
                        "Together we're stronger!"
                    };
            }
        }
        
        #endregion
    }
}
