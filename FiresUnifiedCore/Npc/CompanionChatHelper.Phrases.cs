using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    /// <summary>
    /// Phrase collections and conversion logic for companion chat messages.
    /// This partial class handles all the message content while the main class handles delivery.
    /// 
    /// DESIGN PRINCIPLES:
    /// 1. Phrases should sound natural and conversational
    /// 2. Avoid technical jargon (no "output", "Switch", "m_addOre", etc.)
    /// 3. Context-aware: kilns talk about charcoal, smelters talk about metal
    /// 4. Varied: cycle through phrases to avoid repetition
    /// 5. Appropriate tone: working companions sound busy, not robotic
    /// 6. Personal: Sometimes (20%) use owner's name to feel more connected
    /// </summary>
    public static partial class CompanionChatHelper
    {
        #region Player Name Integration
        
        /// <summary>
        /// Chance (0-1) for a companion to use the owner's name in their response.
        /// Default 20% - keeps it special without being annoying.
        /// </summary>
        public static float OwnerNameChance = 0.20f;
        
        /// <summary>
        /// Gets the owner's display name for use in companion phrases.
        /// Returns null if owner not found or not local player.
        /// </summary>
        public static string GetOwnerName(CompanionController companion)
        {
            if (companion == null) return null;
            
            var owner = companion.GetOwner();
            if (owner == null) return null;
            
            // Only use name for local player's companions
            if (owner != Player.m_localPlayer) return null;
            
            string name = owner.GetPlayerName();
            if (string.IsNullOrEmpty(name)) return null;
            
            return name;
        }
        
        /// <summary>
        /// Determines if the companion should use the owner's name in this message.
        /// Uses random chance based on OwnerNameChance.
        /// </summary>
        public static bool ShouldUseOwnerName()
        {
            return Random.value < OwnerNameChance;
        }
        
        /// <summary>
        /// Gets a phrase, optionally personalized with the owner's name.
        /// If personalization is triggered (20% chance), uses a phrase that includes {owner}.
        /// </summary>
        public static string GetPersonalizedPhrase(
            CompanionController companion,
            string[] normalPhrases,
            string[] personalizedPhrases,
            string cycleCategory = null)
        {
            // Check if we should personalize
            if (ShouldUseOwnerName() && personalizedPhrases != null && personalizedPhrases.Length > 0)
            {
                string ownerName = GetOwnerName(companion);
                if (!string.IsNullOrEmpty(ownerName))
                {
                    string template = GetPhrase(personalizedPhrases, cycleCategory + "_personal");
                    return template.Replace("{owner}", ownerName);
                }
            }
            
            // Fall back to normal phrase
            return GetPhrase(normalPhrases, cycleCategory);
        }
        
        #endregion
        /// <summary>
        /// Phrase collections organized by activity type.
        /// Each collection has multiple variations to keep chat interesting.
        /// 
        /// PERSONALIZED VARIANTS:
        /// Some phrase categories have a "Personal" variant that includes {owner} placeholder.
        /// These are used 20% of the time when the companion knows their owner's name.
        /// </summary>
        public static class Phrases
        {
            #region Personalized Command Responses
            
            /// <summary>
            /// Responses when companion is commanded to follow - includes owner's name.
            /// </summary>
            public static readonly string[] FollowingPersonal = new[]
            {
                "Right behind you, {owner}.",
                "Lead the way, {owner}!",
                "I'm with you, {owner}.",
                "Following you, {owner}.",
                "After you, {owner}.",
                "Ready when you are, {owner}.",
                "Let's go, {owner}!"
            };
            
            /// <summary>
            /// Responses when companion is commanded to stay - includes owner's name.
            /// </summary>
            public static readonly string[] StayingPersonal = new[]
            {
                "I'll wait here for you, {owner}.",
                "Holding position, {owner}.",
                "I'll guard this spot, {owner}.",
                "Be careful out there, {owner}.",
                "Come back soon, {owner}.",
                "I'll keep watch, {owner}.",
                "Don't worry {owner}, I've got this."
            };
            
            /// <summary>
            /// Responses when commanded to move somewhere - includes owner's name.
            /// </summary>
            public static readonly string[] MovingToPositionPersonal = new[]
            {
                "On my way, {owner}.",
                "Got it, {owner}!",
                "Heading there now, {owner}.",
                "As you wish, {owner}.",
                "Right away, {owner}.",
                "Consider it done, {owner}."
            };
            
            /// <summary>
            /// Task completion with owner acknowledgment.
            /// </summary>
            public static readonly string[] TaskCompletePersonal = new[]
            {
                "All done, {owner}!",
                "Finished, {owner}.",
                "Task complete, {owner}.",
                "That's done, {owner}.",
                "Mission accomplished, {owner}!",
                "Done and done, {owner}."
            };
            
            /// <summary>
            /// Entering combat with owner acknowledgment.
            /// </summary>
            public static readonly string[] EnteringCombatPersonal = new[]
            {
                "I've got your back, {owner}!",
                "Stand behind me, {owner}!",
                "Let me handle this, {owner}!",
                "Stay safe, {owner}!",
                "Enemies ahead, {owner}!",
                "To battle, {owner}!",
                "For you, {owner}!"
            };
            
            /// <summary>
            /// Low health warnings with owner acknowledgment.
            /// </summary>
            public static readonly string[] LowHealthPersonal = new[]
            {
                "I need help, {owner}!",
                "I'm hurt badly, {owner}!",
                "{owner}, I can't take much more!",
                "Help me, {owner}!",
                "{owner}, I'm in trouble!"
            };
            
            /// <summary>
            /// Starting a task with owner acknowledgment.
            /// </summary>
            public static readonly string[] StartingTaskPersonal = new[]
            {
                "On it, {owner}!",
                "I'll handle this, {owner}.",
                "Leave it to me, {owner}.",
                "Consider it done, {owner}.",
                "Right away, {owner}."
            };
            
            /// <summary>
            /// Gathering resources with owner acknowledgment.
            /// </summary>
            public static readonly string[] GatheringPersonal = new[]
            {
                "Found some good stuff, {owner}!",
                "This'll come in handy, {owner}.",
                "Look what I found, {owner}!",
                "Adding to our supplies, {owner}.",
                "Got some materials for us, {owner}."
            };
            
            /// <summary>
            /// Inventory full warnings with owner acknowledgment.
            /// </summary>
            public static readonly string[] InventoryFullPersonal = new[]
            {
                "I can't carry any more, {owner}.",
                "My pack is full, {owner}.",
                "{owner}, I need to drop some things off.",
                "Too heavy, {owner}. Need to unload.",
                "Bags are full, {owner}!"
            };
            
            #endregion
            #region Movement Phrases
            
            public static readonly string[] GoingToFire = new[]
            {
                "Let me check on the fire.",
                "The fire needs some attention.",
                "Going to keep the fire going.",
                "Time to add some wood.",
                "Can't let the fire go out.",
                "Heading to tend the flames.",
                "The hearth needs tending."
            };
            
            public static readonly string[] GoingToKiln = new[]
            {
                "Let me check on the kiln.",
                "Going to see how the charcoal is coming.",
                "The kiln might need more wood.",
                "Time to tend the kiln.",
                "Checking on the charcoal production.",
                "Making sure the kiln's running."
            };
            
            public static readonly string[] GoingToSmelter = new[]
            {
                "Let me check on the smelter.",
                "Time to see how the ore is doing.",
                "The smelter might need attention.",
                "Going to tend the furnace.",
                "Checking on the metal production.",
                "The forge needs me."
            };
            
            public static readonly string[] GoingToCookingStation = new[]
            {
                "Let me check on the food.",
                "Something might be ready to eat.",
                "Time to see how the cooking is going.",
                "Going to check the cooking station.",
                "Smells like something's cooking!",
                "Hope nothing's burning..."
            };
            
            public static readonly string[] GoingToWorkbench = new[]
            {
                "Time for some crafting work.",
                "Let me use the workbench.",
                "Going to do some repairs.",
                "The workbench is calling.",
                "Some gear needs maintenance.",
                "Time to fix things up."
            };
            
            public static readonly string[] GoingToChest = new[]
            {
                "Let me check the storage.",
                "Going to organize the chests.",
                "Time to sort through supplies.",
                "Need to grab something from storage.",
                "Checking what we have in stock.",
                "Let me see what's in here."
            };
            
            public static readonly string[] GoingToGatherWood = new[]
            {
                "Time to chop some wood.",
                "I see a good tree over there.",
                "Going to gather some timber.",
                "Let me get some wood.",
                "Off to do some logging.",
                "Those trees won't chop themselves."
            };
            
            public static readonly string[] GoingToMineOre = new[]
            {
                "Time to mine some ore.",
                "I see a deposit over there.",
                "Going to gather some ore.",
                "Let me get some minerals.",
                "Off to do some mining.",
                "There's ore to be had."
            };
            
            public static readonly string[] GoingToGatherPlants = new[]
            {
                "I see something to pick.",
                "Going to gather some plants.",
                "Let me collect those.",
                "Nature provides!",
                "Time to do some foraging.",
                "Found something useful."
            };
            
            public static readonly string[] GoingToGenericResource = new[]
            {
                "Heading over there...",
                "Let me get that.",
                "I see something useful.",
                "On my way.",
                "Just going to grab this.",
                "I'm on it."
            };
            
            #endregion
            
            #region Collection Phrases
            
            public static readonly string[] CollectingCharcoal = new[]
            {
                "Let me grab the charcoal.",
                "Charcoal's ready!",
                "Time to collect the coal.",
                "I'll get that charcoal.",
                "The coal is done.",
                "Good batch of charcoal here."
            };
            
            public static readonly string[] CollectingMetal = new[]
            {
                "Let me grab the metal bars.",
                "The bars are ready!",
                "Time to collect the metal.",
                "Fresh from the forge.",
                "Nice and hot!",
                "Good metal here."
            };
            
            public static readonly string[] CollectingGeneric = new[]
            {
                "Let me grab what's ready.",
                "Time to collect the goods.",
                "I'll get that.",
                "It's ready!",
                "Picking up the results.",
                "Let me fetch that.",
                "All done here."
            };
            
            public static readonly string[] CollectingLoot = new[]
            {
                "Got it!",
                "I'll take that.",
                "Mine now.",
                "Into the pack.",
                "Yoink!",
                "Grabbed it.",
                "Adding to my collection."
            };
            
            public static readonly string[] CollectingDrops = new[]
            {
                "Gathering up the goods.",
                "Don't want to leave this behind.",
                "Nice haul!",
                "Collecting the spoils.",
                "Waste not, want not.",
                "Good loot here."
            };
            
            #endregion
            
            #region Working Phrases
            
            public static readonly string[] AddingFuel = new[]
            {
                "Keeping the fire fed.",
                "In goes the wood...",
                "That should keep it burning.",
                "More fuel for the flames.",
                "There we go.",
                "Keep it burning hot.",
                "Adding more fuel."
            };
            
            public static readonly string[] AddingWoodToKiln = new[]
            {
                "Adding wood to the kiln.",
                "This will make good charcoal.",
                "In goes the wood...",
                "More wood for the kiln.",
                "Feeding the kiln.",
                "Making some charcoal."
            };
            
            public static readonly string[] AddingOreToSmelter = new[]
            {
                "Adding ore to the smelter.",
                "In it goes...",
                "More ore for the furnace.",
                "This should make good metal.",
                "Feeding the smelter.",
                "Processing more ore."
            };
            
            public static readonly string[] GettingFromChests = new[]
            {
                "Just grabbing some supplies.",
                "Let me check the chests.",
                "Should have what we need here.",
                "Restocking from storage.",
                "Getting more materials.",
                "Let's see what we have..."
            };
            
            public static readonly string[] StoringItems = new[]
            {
                "Putting these away safely.",
                "Organizing the storage.",
                "Everything in its place.",
                "Making room for more.",
                "Can never have too many of these.",
                "Tidying up the chests.",
                "Stashing the goods."
            };
            
            public static readonly string[] Operating = new[]
            {
                "Keeping things running smoothly.",
                "I've got this covered.",
                "Steady work here.",
                "Just doing my job.",
                "Somebody's gotta do it!",
                "Hard at work.",
                "Keeping busy."
            };
            
            public static readonly string[] TendingFire = new[]
            {
                "Keeping the home fires burning.",
                "The fire needs some love.",
                "Stoking the flames.",
                "A warm fire is a happy fire.",
                "Tending to the hearth.",
                "Nice and warm now."
            };
            
            public static readonly string[] Cooking = new[]
            {
                "Something smells good!",
                "Cooking up a meal.",
                "Food's almost ready.",
                "The chef is at work.",
                "Nothing like a hot meal.",
                "Dinner's coming along."
            };
            
            #endregion
            
            #region Gathering Phrases
            
            public static readonly string[] Gathering = new[]
            {
                "This looks useful...",
                "Found something good here.",
                "Adding this to the pile.",
                "Nice find!",
                "This'll come in handy.",
                "Into the pack it goes.",
                "Good stuff here.",
                "Getting the good stuff."
            };
            
            public static readonly string[] Picking = new[]
            {
                "These look ripe.",
                "Nice harvest here.",
                "Adding these to my pack.",
                "Good foraging spot.",
                "Nature provides!",
                "Found some good ones."
            };
            
            public static readonly string[] Mining = new[]
            {
                "Good ore here.",
                "Swing after swing...",
                "This rock's got potential.",
                "Mining away.",
                "There's the good stuff.",
                "Hitting the deposit."
            };
            
            public static readonly string[] Chopping = new[]
            {
                "Timber!",
                "Good wood here.",
                "Chopping away.",
                "This tree's coming down.",
                "Swing after swing...",
                "Quality lumber."
            };
            
            #endregion
            
            #region Waiting Phrases
            
            public static readonly string[] Waiting = new[]
            {
                "Just waiting for this to finish...",
                "Almost done...",
                "Patience is a virtue, they say.",
                "Won't be long now.",
                "Any moment now...",
                "Hmm, still working...",
                "Taking its time...",
                "Should be ready soon."
            };
            
            public static readonly string[] Checking = new[]
            {
                "Let me see how this is going...",
                "Checking on the progress.",
                "How's this coming along?",
                "Looking good so far.",
                "Is this thing still on?",
                "Let me take a look.",
                "Hmm, let's see..."
            };
            
            public static readonly string[] Looking = new[]
            {
                "Let me find something...",
                "Looking around...",
                "There's gotta be something here.",
                "Searching the area.",
                "What do we have nearby?",
                "Scanning for resources."
            };
            
            #endregion
            
            #region Combat Phrases
            
            public static readonly string[] EnteringCombat = new[]
            {
                "Enemies!",
                "To battle!",
                "I've got this!",
                "Time to fight!",
                "Watch out!",
                "Here they come!",
                "For glory!",
                "Stand ready!"
            };
            
            public static readonly string[] Victory = new[]
            {
                "Got 'em!",
                "Down!",
                "That's one.",
                "Next!",
                "And stay down!",
                "Too easy.",
                "One less problem."
            };
            
            public static readonly string[] LowHealth = new[]
            {
                "I'm hurt badly!",
                "Need healing!",
                "Can't take much more!",
                "I'm wounded!",
                "Help!",
                "Getting overwhelmed!"
            };
            
            #endregion
            
            #region Status Phrases
            
            public static readonly string[] Following = new[]
            {
                "Following you.",
                "Right behind you.",
                "Lead the way.",
                "I'm with you.",
                "After you.",
                "Staying close."
            };
            
            public static readonly string[] Staying = new[]
            {
                "I'll wait here.",
                "Staying put.",
                "Holding position.",
                "I'll guard this spot.",
                "On watch here.",
                "Waiting for orders."
            };
            
            public static readonly string[] MovingToPosition = new[]
            {
                "On my way.",
                "Moving there.",
                "Heading over.",
                "Got it.",
                "Going now.",
                "I'm on it."
            };
            
            public static readonly string[] InventoryFull = new[]
            {
                "I can't carry any more.",
                "My pack is full.",
                "I need to drop something off.",
                "Too heavy to carry more.",
                "Bags are full!",
                "No more room."
            };
            
            public static readonly string[] TaskComplete = new[]
            {
                "Done!",
                "All finished.",
                "Task complete.",
                "That's done.",
                "Finished up.",
                "All done here."
            };
            
            public static readonly string[] CantDoTask = new[]
            {
                "I can't do that.",
                "That's not possible.",
                "Something's wrong.",
                "I'm having trouble with this.",
                "This isn't working.",
                "Need to try something else."
            };
            
            #endregion
            
            #region Material-Specific Phrases
            
            public static readonly string[] NoFuelAvailable = new[]
            {
                "No fuel available - I need coal or wood.",
                "Can't find any fuel in chests or my pack.",
                "I need fuel but there's none around.",
                "No coal or wood to keep this running.",
                "Out of fuel!",
                "Need more wood or coal."
            };
            
            public static readonly string[] NoOreAvailable = new[]
            {
                "No ore to smelt.",
                "I can't find any ore to process.",
                "Need ore but there's none available.",
                "The chests are empty of ore.",
                "Out of ore!",
                "Need more ore to smelt."
            };
            
            public static readonly string[] NoWoodForKiln = new[]
            {
                "No wood for the kiln.",
                "I can't find wood to make charcoal.",
                "Need wood but there's none available.",
                "Out of wood for the kiln!",
                "Need logs for charcoal."
            };
            
            public static readonly string[] NoToolAvailable = new[]
            {
                "I need the right tool for this.",
                "Can't do this without the proper tool.",
                "Where's my tool?",
                "I don't have the right equipment.",
                "Need a tool for this job."
            };
            
            public static readonly string[] StationFull = new[]
            {
                "It's full, waiting for output.",
                "Can't add any more right now.",
                "Waiting for it to finish.",
                "All full up!",
                "Need to wait for this batch."
            };
            
            #endregion
            
            #region Training Phrases
            
            public static readonly string[] BowTraining = new[]
            {
                "Time for target practice.",
                "Practicing my aim.",
                "Let me train a bit.",
                "Working on my archery.",
                "Sharpening my skills.",
                "Need to stay sharp."
            };
            
            public static readonly string[] MeleeTraining = new[]
            {
                "Time to practice.",
                "Working on my form.",
                "Training my combat skills.",
                "Staying combat ready.",
                "Practice makes perfect."
            };
            
            #endregion
            
            #region Repair/Upgrade Phrases
            
            public static readonly string[] ItemRepaired = new[]
            {
                "Fixed it up!",
                "Good as new.",
                "Repaired and ready.",
                "That's better.",
                "All patched up.",
                "Back in working order."
            };
            
            public static readonly string[] ItemUpgraded = new[]
            {
                "Made it stronger!",
                "Upgraded successfully!",
                "Better than before!",
                "Enhanced!",
                "Quality improved!",
                "Excellent work!"
            };
            
            #endregion
        }
        
        /// <summary>
        /// Gets a random phrase from an array, with optional cycling to avoid immediate repeats.
        /// </summary>
        public static string GetPhrase(string[] phrases, string cycleCategory = null)
        {
            if (phrases == null || phrases.Length == 0) return string.Empty;
            if (phrases.Length == 1) return phrases[0];
            
            if (!string.IsNullOrEmpty(cycleCategory))
            {
                return GetCycledMessage(cycleCategory, phrases);
            }
            
            return phrases[Random.Range(0, phrases.Length)];
        }
        
        /// <summary>
        /// Gets a phrase with a variable inserted.
        /// </summary>
        public static string GetPhraseWithVariable(string[] templates, string variable, string cycleCategory = null)
        {
            string template = GetPhrase(templates, cycleCategory);
            return template.Replace("{0}", variable);
        }
    }
}
