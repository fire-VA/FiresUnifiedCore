using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    /// <summary>
    /// Utility class for sending chat messages from companions.
    /// Provides a consistent way for companions to communicate with their owners
    /// about completed tasks, status updates, and other notifications.
    /// 
    /// MESSAGE TYPES:
    /// - Speech bubble: Appears above companion's head (NPC text)
    /// - Chat log: Appears in the chat window (optional)
    /// - HUD message: Appears as top-left notification
    /// 
    /// RATE LIMITING:
    /// - Messages are rate-limited to prevent spam
    /// - Same message won't repeat within a cooldown period
    /// - Maximum messages per companion per minute
    /// - Global limit: Max 2 chat bubbles on screen at once
    /// - Distance check: Companions close together won't both talk at once
    /// </summary>
    public static partial class CompanionChatHelper
    {
        #region Rate Limiting
        
        private static Dictionary<string, float> _lastMessageTimes = new Dictionary<string, float>();
        private static Dictionary<string, int> _messageCountsPerMinute = new Dictionary<string, int>();
        private static float _lastCleanupTime = 0f;
        
        private const float MESSAGE_COOLDOWN = 10f;  // Same message can't repeat for 10 seconds
        private const float CLEANUP_INTERVAL = 60f;  // Clean up tracking every minute
        private const int MAX_MESSAGES_PER_MINUTE = 5;  // Max messages per companion per minute
        
        // Status display tracking - separate from regular messages
        private static Dictionary<string, float> _lastStatusUpdateTimes = new Dictionary<string, float>();
        private static Dictionary<string, string> _lastStatusText = new Dictionary<string, string>(); // Track last status text per companion
        private const float STATUS_UPDATE_INTERVAL = 15f;  // Same status can repeat every 15 seconds
        private const float STATUS_CHANGE_COOLDOWN = 5f;   // Minimum time between ANY status updates (prevents rapid flickering)
        
        // Global chat bubble limiting - prevent screen spam when multiple companions work nearby
        private static Dictionary<string, float> _activeStatusBubbles = new Dictionary<string, float>(); // companionId -> expiry time
        private static Dictionary<string, Vector3> _activeBubblePositions = new Dictionary<string, Vector3>(); // companionId -> position
        private const int MAX_CONCURRENT_BUBBLES = 2;  // Maximum bubbles on screen at once
        private const float MIN_BUBBLE_DISTANCE = 8f;   // Minimum distance between companions showing bubbles
        
        // Message cycling - track recently used messages to avoid repetition
        private static Dictionary<string, List<int>> _recentMessageIndices = new Dictionary<string, List<int>>(); // category -> list of recently used indices
        private const int MAX_RECENT_MESSAGES = 3;  // Remember last 3 messages per category to avoid immediate repeats
        
        /// <summary>
        /// The symbol/icon displayed before working status messages.
        /// Change this if the default doesn't render correctly in your game.
        /// Options: "⚒" (hammers), "⚙" (gear), "⛏" (pick), "▶" (play), "●" (bullet), "★" (star)
        /// </summary>
        public static string WorkingStatusIcon = "⚒";
        
        /// <summary>
        /// Context information for station-specific messages.
        /// Helps the chat system choose appropriate phrases for different station types.
        /// </summary>
        public enum StationContext
        {
            None,           // No specific context
            Smelter,        // Processing ore into metal bars
            Kiln,           // Processing wood into charcoal/coal
            BlastFurnace,   // Processing ore into metal (high tier)
            SpinningWheel,  // Processing flax into thread
            Windmill,       // Processing barley into flour
            Fire,           // Campfire, hearth, bonfire
            CookingStation, // Cooking stand, cauldron, etc.
            Workstation     // Workbench, forge, etc.
        }
        
        // Current station context for the active status message
        private static Dictionary<string, StationContext> _companionStationContext = new Dictionary<string, StationContext>();
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Default font size for companion chat bubbles.
        /// Valheim's default NPC text is around 20-24. We use 12 for a smaller, less intrusive look.
        /// </summary>
        public const int CHAT_BUBBLE_FONT_SIZE = 12;
        
        /// <summary>
        /// Sends a speech bubble message that appears above the companion's head.
        /// This is the primary way companions should communicate task completion.
        /// </summary>
        /// <param name="companion">The companion sending the message</param>
        /// <param name="message">The message text</param>
        /// <param name="duration">How long to display (default 5 seconds)</param>
        /// <param name="large">Use large text style (for important messages)</param>
        public static void SaySpeechBubble(CompanionController companion, string message, float duration = 5f, bool large = false)
        {
            if (companion == null || string.IsNullOrEmpty(message)) return;
            // Skip during local player respawn / loading screen — Chat.SetNpcText
            // broadcasts via ZRoutedRpc to nearby clients, and RPCs during
            // IsTeleporting=true deadlock the zone stream. Drop the bubble; the
            // companion will say something else next time.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            if (!ShouldSendMessage(companion, message)) return;

            // Only show to owner
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;

            if (Chat.instance == null) return;
            
            try
            {
                // Wrap message with TMP size tag to reduce font size
                // large=false uses smaller size, large=true uses slightly larger
                int fontSize = large ? CHAT_BUBBLE_FONT_SIZE + 4 : CHAT_BUBBLE_FONT_SIZE;
                string formattedMessage = $"<size={fontSize}>{message}</size>";
                
                Chat.instance.SetNpcText(
                    companion.gameObject,
                    Vector3.up * 2f,    // Offset above head
                    20f,                // Cull distance
                    duration,           // TTL
                    "",                 // No topic
                    formattedMessage,
                    false               // Don't use built-in large style, we control size via TMP tags
                );
                
                RecordMessageSent(companion, message);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CompanionChatHelper] Failed to send speech bubble: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Sends a task completion message - speech bubble + HUD notification.
        /// Use this when a companion finishes a commanded task.
        /// </summary>
        /// <param name="companion">The companion</param>
        /// <param name="taskDescription">What task was completed (e.g., "stored 15 items")</param>
        public static void NotifyTaskComplete(CompanionController companion, string taskDescription)
        {
            if (companion == null || string.IsNullOrEmpty(taskDescription)) return;
            
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            string companionName = companion.GetDisplayName();
            
            // Speech bubble with a conversational message
            string[] completionPhrases = new[]
            {
                $"Done! I've {taskDescription}.",
                $"All finished - {taskDescription}.",
                $"Task complete: {taskDescription}.",
                $"I've {taskDescription}.",
                $"Finished! {taskDescription}."
            };
            
            string bubbleMessage = completionPhrases[Random.Range(0, completionPhrases.Length)];
            SaySpeechBubble(companion, bubbleMessage, 4f);
            
            // HUD notification (always shows)
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                $"{companionName} {taskDescription}");
        }
        
        /// <summary>
        /// Sends a status update message - just speech bubble, no HUD.
        /// Use for non-critical updates like "I found something interesting".
        /// </summary>
        public static void SayStatus(CompanionController companion, string statusMessage)
        {
            SaySpeechBubble(companion, statusMessage, 3f, false);
        }
        
        /// <summary>
        /// Sends an alert message - larger speech bubble + HUD.
        /// Use for important events like "I'm under attack!" or "I can't carry any more".
        /// </summary>
        public static void SayAlert(CompanionController companion, string alertMessage)
        {
            if (companion == null || string.IsNullOrEmpty(alertMessage)) return;
            
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            SaySpeechBubble(companion, alertMessage, 5f, true);
            
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, 
                $"{companion.GetDisplayName()}: {alertMessage}");
        }
        
        /// <summary>
        /// Sends a comment about the current situation.
        /// Low priority, heavily rate-limited, adds personality.
        /// </summary>
        public static void SayComment(CompanionController companion, string comment)
        {
            // Extra rate limiting for casual comments
            if (companion == null) return;
            
            string key = $"{companion.companionId}_comment";
            if (_lastMessageTimes.TryGetValue(key, out float lastTime))
            {
                if (Time.time - lastTime < 30f) return; // 30 second cooldown for comments
            }
            
            SaySpeechBubble(companion, comment, 3f, false);
            _lastMessageTimes[key] = Time.time;
        }
        
        /// <summary>
        /// Shows a working status above the companion's head while they're performing a task.
        /// Rate-limited to show the same status every 15 seconds, but state CHANGES show immediately.
        /// This provides visual feedback without spamming.
        /// 
        /// GLOBAL LIMITING:
        /// - Maximum of 2 chat bubbles displayed at once across all companions
        /// - Companions within 8m of each other won't both display bubbles
        /// - This prevents screen clutter when multiple companions work nearby
        /// </summary>
        /// <param name="companion">The companion performing the task</param>
        /// <param name="statusText">Current status (e.g., "Filling smelter", "Waiting for output")</param>
        /// <param name="forceUpdate">If true, bypasses rate limiting to show immediately</param>
        /// <param name="context">Optional station context for smarter message generation</param>
        public static void ShowWorkingStatus(CompanionController companion, string statusText, bool forceUpdate = false, StationContext context = StationContext.None)
        {
            if (companion == null || string.IsNullOrEmpty(statusText)) return;
            
            // Only show to owner
            var owner = companion.GetOwner();
            if (owner == null || owner != Player.m_localPlayer) return;
            
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            float currentTime = Time.time;
            Vector3 companionPos = companion.transform.position;
            
            // Store context for this companion (used by ConvertStatusToIdleChatter)
            if (context != StationContext.None)
            {
                _companionStationContext[companionId] = context;
            }
            
            // Check if this is a STATE CHANGE (different status text)
            bool isStateChange = false;
            if (_lastStatusText.TryGetValue(companionId, out string lastText))
            {
                isStateChange = !string.Equals(lastText, statusText, System.StringComparison.Ordinal);
            }
            else
            {
                // First status for this companion - treat as state change
                isStateChange = true;
            }
            
            // Rate limiting logic:
            // - State changes: Enforce STATUS_CHANGE_COOLDOWN (5s) to prevent rapid flickering
            // - Same status: Only repeat every STATUS_UPDATE_INTERVAL (15s) seconds
            // This prevents the companion from spamming status changes when rapidly switching states
            if (!forceUpdate)
            {
                if (_lastStatusUpdateTimes.TryGetValue(companionId, out float lastTime))
                {
                    // Always enforce minimum cooldown between ANY status updates
                    if (currentTime - lastTime < STATUS_CHANGE_COOLDOWN) return;
                    
                    // For same status, enforce the longer interval
                    if (!isStateChange && currentTime - lastTime < STATUS_UPDATE_INTERVAL) return;
                }
            }
            
            // Clean up expired bubbles before checking limits
            CleanupExpiredBubbles(currentTime);
            
            // GLOBAL LIMITING: Check if this companion can show a bubble
            // Skip this check if this companion already has an active bubble (allow updates)
            bool hasActiveBubble = _activeStatusBubbles.ContainsKey(companionId);
            if (!hasActiveBubble)
            {
                // Check concurrent bubble count
                if (_activeStatusBubbles.Count >= MAX_CONCURRENT_BUBBLES)
                {
                    return; // Too many bubbles on screen
                }
                
                // Check distance to other active bubbles
                foreach (var kvp in _activeBubblePositions)
                {
                    if (kvp.Key == companionId) continue;
                    float dist = Vector3.Distance(companionPos, kvp.Value);
                    if (dist < MIN_BUBBLE_DISTANCE)
                    {
                        return; // Another companion too close is already showing a bubble
                    }
                }
            }
            
            // Update tracking
            _lastStatusUpdateTimes[companionId] = currentTime;
            _lastStatusText[companionId] = statusText;
            
            // Register this bubble as active
            float bubbleExpiry = currentTime + STATUS_UPDATE_INTERVAL;
            _activeStatusBubbles[companionId] = bubbleExpiry;
            _activeBubblePositions[companionId] = companionPos;
            
            if (Chat.instance == null) return;
            
            try
            {
                // Get station context for this companion (if set)
                StationContext msgContext = StationContext.None;
                _companionStationContext.TryGetValue(companionId, out msgContext);
                
                // Convert technical status to immersive idle chatter
                string idleChatter = ConvertStatusToIdleChatter(statusText, msgContext);
                
                // UNICODE SYMBOL SELECTION:
                // Valheim's TMP fonts have fallback support via Noto fonts (including NotoEmoji-Regular SDF).
                // Using WorkingStatusIcon (default: ⚒ U+2692 HAMMER AND PICK)
                string displayText = string.IsNullOrEmpty(WorkingStatusIcon) 
                    ? idleChatter 
                    : $"{WorkingStatusIcon} {idleChatter}";
                
                // Wrap with TMP size tag for smaller font
                string formattedText = $"<size={CHAT_BUBBLE_FONT_SIZE}>{displayText}</size>";
                
                Chat.instance.SetNpcText(
                    companion.gameObject,
                    Vector3.up * 2.2f,    // Slightly higher offset
                    25f,                  // Slightly larger cull distance
                    STATUS_UPDATE_INTERVAL - 0.5f,  // Display until next update
                    "",                   // No topic
                    formattedText,
                    false                 // Not large text
                );
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[CompanionChatHelper] Failed to show working status: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Converts technical status descriptions into immersive idle chatter.
        /// Makes companions feel more alive by using varied, natural-sounding phrases.
        /// Uses message cycling to avoid repetition.
        /// Applies context awareness to avoid nonsensical phrases.
        /// 
        /// FIXES APPLIED:
        /// - Removes "add" prefix from targets (e.g., "add ore" -> "ore")
        /// - Replaces "output" with friendly terms like "finished goods"
        /// - Uses kiln-specific messages (wood/charcoal) instead of smelter messages (ore/metal)
        /// - Cleans up internal Switch names like "m_addWoodSwitch"
        /// </summary>
        private static string ConvertStatusToIdleChatter(string technicalStatus, StationContext context = StationContext.None)
        {
            if (string.IsNullOrEmpty(technicalStatus)) return technicalStatus;
            
            // CLEANUP: Remove internal naming artifacts
            // Switch names like "m_addOreSwitch" should become just "ore"
            string cleanedStatus = CleanupTechnicalTerms(technicalStatus);
            string lowerStatus = cleanedStatus.ToLowerInvariant();
            
            // Walking to resource - need special context handling
            if (lowerStatus.StartsWith("walking to"))
            {
                string target = cleanedStatus.Substring("Walking to ".Length);
                string lowerTarget = target.ToLowerInvariant();
                
                // CONTEXT: Fire-related targets - companion is going to tend the fire
                if (lowerTarget.Contains("fire") || lowerTarget.Contains("campfire") || 
                    lowerTarget.Contains("hearth") || lowerTarget.Contains("bonfire"))
                {
                    string[] firePhrases = new[]
                    {
                        "Let me check on the fire.",
                        "The fire needs some attention.",
                        "Going to keep the fire going.",
                        "Time to add some wood to the fire.",
                        "Can't let the fire go out.",
                        "Heading to tend the flames."
                    };
                    return GetCycledMessage("walking_to_fire", firePhrases);
                }
                
                // CONTEXT: Kiln - companion is going to check the kiln
                if (lowerTarget.Contains("kiln") || lowerTarget.Contains("charcoal"))
                {
                    string[] kilnPhrases = new[]
                    {
                        "Let me check on the kiln.",
                        "Going to see how the charcoal is coming.",
                        "The kiln might need more wood.",
                        "Time to tend the kiln.",
                        "Checking on the charcoal production."
                    };
                    return GetCycledMessage("walking_to_kiln", kilnPhrases);
                }
                
                // CONTEXT: Smelter/Furnace - companion is going to work the smelter
                if (lowerTarget.Contains("smelter") || lowerTarget.Contains("furnace") || 
                    lowerTarget.Contains("blast") || lowerTarget.Contains("forge"))
                {
                    string[] smelterPhrases = new[]
                    {
                        "Let me check on the smelter.",
                        "Time to see how the ore is doing.",
                        "The smelter might need attention.",
                        "Going to tend the furnace.",
                        "Checking on the metal production."
                    };
                    return GetCycledMessage("walking_to_smelter", smelterPhrases);
                }
                
                // CONTEXT: Cooking station
                if (lowerTarget.Contains("cook") || lowerTarget.Contains("cauldron") || 
                    lowerTarget.Contains("oven"))
                {
                    string[] cookPhrases = new[]
                    {
                        "Let me check on the food.",
                        "Something might be ready to eat.",
                        "Time to see how the cooking is going.",
                        "Going to check the cooking station.",
                        "Smells like something's cooking!"
                    };
                    return GetCycledMessage("walking_to_cook", cookPhrases);
                }
                
                // CONTEXT: Workstation/Workbench
                if (lowerTarget.Contains("workbench") || lowerTarget.Contains("bench") ||
                    lowerTarget.Contains("station") || lowerTarget.Contains("table"))
                {
                    string[] workbenchPhrases = new[]
                    {
                        "Time for some crafting work.",
                        "Let me use the workbench.",
                        "Going to do some repairs.",
                        "The workbench is calling.",
                        "Some gear needs maintenance."
                    };
                    return GetCycledMessage("walking_to_workbench", workbenchPhrases);
                }
                
                // CONTEXT: Chest/Storage
                if (lowerTarget.Contains("chest") || lowerTarget.Contains("storage") ||
                    lowerTarget.Contains("container") || lowerTarget.Contains("box"))
                {
                    string[] chestPhrases = new[]
                    {
                        "Let me check the storage.",
                        "Going to organize the chests.",
                        "Time to sort through supplies.",
                        "Need to grab something from storage.",
                        "Checking what we have in stock."
                    };
                    return GetCycledMessage("walking_to_chest", chestPhrases);
                }
                
                // CONTEXT: Tree variants - swap for "wood"
                if (lowerTarget.Contains("tree") || lowerTarget.Contains("beech") || 
                    lowerTarget.Contains("birch") || lowerTarget.Contains("oak") || 
                    lowerTarget.Contains("pine") || lowerTarget.Contains("fir") ||
                    lowerTarget.Contains("ancient") || lowerTarget.Contains("yggdrasil"))
                {
                    target = "wood";
                }
                
                // CONTEXT: Ore/Rock - swap for friendlier names
                if (lowerTarget.Contains("rock") || lowerTarget.Contains("mudpile") ||
                    lowerTarget.Contains("deposit"))
                {
                    if (lowerTarget.Contains("copper")) target = "copper ore";
                    else if (lowerTarget.Contains("tin")) target = "tin ore";
                    else if (lowerTarget.Contains("iron")) target = "iron ore";
                    else if (lowerTarget.Contains("silver")) target = "silver ore";
                    else if (lowerTarget.Contains("black")) target = "black metal";
                    else target = "ore";
                }
                
                // CONTEXT: Pickable items - make names friendlier
                if (lowerTarget.Contains("pickable"))
                {
                    if (lowerTarget.Contains("mushroom")) target = "mushrooms";
                    else if (lowerTarget.Contains("berry") || lowerTarget.Contains("blueberry")) target = "berries";
                    else if (lowerTarget.Contains("carrot")) target = "carrots";
                    else if (lowerTarget.Contains("turnip")) target = "turnips";
                    else if (lowerTarget.Contains("thistle")) target = "thistle";
                    else if (lowerTarget.Contains("flax")) target = "flax";
                    else if (lowerTarget.Contains("barley")) target = "barley";
                    else target = "plants";
                }
                
                // Generic walking phrases for resources
                string[] walkingPhrases = new[]
                {
                    $"Heading over to grab some {target}...",
                    $"Let me get the {target}.",
                    $"I see some {target} over there.",
                    $"On my way to the {target}.",
                    $"Just going to fetch some {target}.",
                    $"{target}? I'm on it.",
                    $"Time to get some {target}.",
                    $"Off to collect {target}."
                };
                return GetCycledMessage("walking_to", walkingPhrases);
            }
            
            // Walking to collect output/drops/finished goods
            if (lowerStatus.Contains("walking to collect") || lowerStatus.Contains("walking to output") ||
                lowerStatus.Contains("collect output") || lowerStatus.Contains("gather output") ||
                lowerStatus.Contains("picking up output"))
            {
                // Context-aware phrases
                if (context == StationContext.Kiln)
                {
                    string[] kilnCollectPhrases = new[]
                    {
                        "Let me grab the charcoal.",
                        "Charcoal's ready!",
                        "Time to collect the coal.",
                        "I'll get that charcoal.",
                        "The coal is done."
                    };
                    return GetCycledMessage("collect_kiln", kilnCollectPhrases);
                }
                
                if (context == StationContext.Smelter || context == StationContext.BlastFurnace)
                {
                    string[] smelterCollectPhrases = new[]
                    {
                        "Let me grab the metal bars.",
                        "The bars are ready!",
                        "Time to collect the metal.",
                        "Fresh from the forge.",
                        "Nice and hot!"
                    };
                    return GetCycledMessage("collect_smelter", smelterCollectPhrases);
                }
                
                // Generic collect phrases
                string[] collectPhrases = new[]
                {
                    "Let me grab the finished goods.",
                    "Time to collect what's done.",
                    "I'll get that for you.",
                    "It's ready!",
                    "Picking up the results.",
                    "Let me fetch what's finished.",
                    "The work is done, time to collect."
                };
                return GetCycledMessage("collect_output", collectPhrases);
            }
            
            // Gathering resources
            if (lowerStatus.Contains("gathering") || lowerStatus.Contains("attacking"))
            {
                string[] gatherPhrases = new[]
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
                return GetCycledMessage("gathering", gatherPhrases);
            }
            
            // Picking berries/plants specifically
            if (lowerStatus.Contains("picking"))
            {
                string[] pickingPhrases = new[]
                {
                    "These look ripe.",
                    "Nice harvest here.",
                    "Adding these to my pack.",
                    "Good foraging spot.",
                    "Nature provides!",
                    "Found some good ones."
                };
                return GetCycledMessage("picking", pickingPhrases);
            }
            
            // Waiting for station
            if (lowerStatus.Contains("waiting for"))
            {
                string[] waitingPhrases = new[]
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
                return GetCycledMessage("waiting", waitingPhrases);
            }
            
            // Checking station
            if (lowerStatus.Contains("checking"))
            {
                string[] checkPhrases = new[]
                {
                    "Let me see how this is going...",
                    "Checking on the progress.",
                    "How's this coming along?",
                    "Looking good so far.",
                    "Is this thing still on?",
                    "Let me take a look.",
                    "Hmm, let's see..."
                };
                return GetCycledMessage("checking", checkPhrases);
            }
            
            // Adding fuel
            if (lowerStatus.Contains("adding fuel"))
            {
                string[] fuelPhrases = new[]
                {
                    "Keeping the fire fed.",
                    "In goes the wood...",
                    "That should keep it burning.",
                    "More fuel for the flames.",
                    "There we go.",
                    "Keep it burning hot.",
                    "Adding more fuel."
                };
                return GetCycledMessage("adding_fuel", fuelPhrases);
            }
            
            // Adding ore - BUT for kilns, we're adding WOOD, not ore!
            // Context-aware: kiln says "wood", smelter says "ore"
            if (lowerStatus.Contains("adding ore") || lowerStatus.Contains("adding wood to") || 
                lowerStatus.Contains("adding material"))
            {
                // Kiln context - we're adding wood to make charcoal
                if (context == StationContext.Kiln || lowerStatus.Contains("kiln"))
                {
                    string[] kilnPhrases = new[]
                    {
                        "Adding wood to the kiln.",
                        "This will make good charcoal.",
                        "In goes the wood...",
                        "More wood for the kiln.",
                        "Feeding the kiln.",
                        "Making some charcoal."
                    };
                    return GetCycledMessage("adding_wood_kiln", kilnPhrases);
                }
                
                // Smelter/Furnace context - we're adding ore to make metal
                string[] orePhrases = new[]
                {
                    "Adding ore to the smelter.",
                    "In it goes...",
                    "More ore for the furnace.",
                    "This should make good metal.",
                    "Feeding the smelter.",
                    "Processing more ore."
                };
                return GetCycledMessage("adding_ore", orePhrases);
            }
            
            // Getting materials from chests
            if (lowerStatus.Contains("getting materials") || lowerStatus.Contains("from chests") ||
                lowerStatus.Contains("pulling"))
            {
                string[] chestPhrases = new[]
                {
                    "Just grabbing some supplies.",
                    "Let me check the chests.",
                    "Should have what we need here.",
                    "Restocking from storage.",
                    "Getting more materials.",
                    "Let's see what we have..."
                };
                return GetCycledMessage("from_chests", chestPhrases);
            }
            
            // Storing items
            if (lowerStatus.Contains("storing") || lowerStatus.Contains("depositing"))
            {
                string[] storePhrases = new[]
                {
                    "Putting these away safely.",
                    "Organizing the storage.",
                    "Everything in its place.",
                    "Making room for more.",
                    "Can never have too many of these.",
                    "Tidying up the chests.",
                    "Stashing the goods."
                };
                return GetCycledMessage("storing", storePhrases);
            }
            
            // Picking up items
            if (lowerStatus.Contains("picking up"))
            {
                string[] pickupPhrases = new[]
                {
                    "Got it!",
                    "I'll take that.",
                    "Mine now.",
                    "Into the pack.",
                    "Yoink!",
                    "Grabbed it.",
                    "Adding to my collection."
                };
                return GetCycledMessage("picking_up", pickupPhrases);
            }
            
            // Collecting drops
            if (lowerStatus.Contains("collecting"))
            {
                string[] collectPhrases = new[]
                {
                    "Gathering up the goods.",
                    "Don't want to leave this behind.",
                    "Nice haul!",
                    "Collecting the spoils.",
                    "One man's trash they say...",
                    "Waste not, want not.",
                    "Good loot here."
                };
                return GetCycledMessage("collecting", collectPhrases);
            }
            
            // Operating/Working at station
            if (lowerStatus.Contains("operating") || lowerStatus.Contains("working at"))
            {
                string[] operatePhrases = new[]
                {
                    "Keeping things running smoothly.",
                    "I've got this covered.",
                    "Steady work here.",
                    "Just doing my job.",
                    "Somebody's gotta do it!",
                    "Hard at work.",
                    "Keeping busy."
                };
                return GetCycledMessage("operating", operatePhrases);
            }
            
            // Looking for something
            if (lowerStatus.Contains("looking for") || lowerStatus.Contains("finding"))
            {
                string[] lookingPhrases = new[]
                {
                    "Let me find something...",
                    "Looking around...",
                    "There's gotta be something here.",
                    "Searching the area.",
                    "What do we have nearby?"
                };
                return GetCycledMessage("looking", lookingPhrases);
            }
            
            // Tending fire specifically
            if (lowerStatus.Contains("tending fire") || lowerStatus.Contains("tending the fire"))
            {
                string[] tendPhrases = new[]
                {
                    "Keeping the home fires burning.",
                    "The fire needs some love.",
                    "Stoking the flames.",
                    "A warm fire is a happy fire.",
                    "Tending to the hearth."
                };
                return GetCycledMessage("tending_fire", tendPhrases);
            }
            
            // Cooking food
            if (lowerStatus.Contains("cooking"))
            {
                string[] cookPhrases = new[]
                {
                    "Something smells good!",
                    "Cooking up a meal.",
                    "Food's almost ready.",
                    "The chef is at work.",
                    "Nothing like a hot meal."
                };
                return GetCycledMessage("cooking", cookPhrases);
            }
            
            // Default: return original but slightly cleaned up
            return cleanedStatus;
        }
        
        /// <summary>
        /// Cleans up technical/internal terms from status messages.
        /// Handles things like:
        /// - "add ore" -> "ore" (removes action prefix)
        /// - "m_addWoodSwitch" -> "wood" (removes internal naming)
        /// - "output" -> context-appropriate term
        /// - Removes "(Clone)" suffix from prefab names
        /// </summary>
        private static string CleanupTechnicalTerms(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            string result = input;
            
            // Remove (Clone) suffix
            if (result.EndsWith("(Clone)"))
                result = result.Substring(0, result.Length - 7).Trim();
            
            // Remove m_ prefix from variable names
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\bm_", "");
            
            // Clean up Switch component names
            result = result.Replace("addOreSwitch", "ore")
                          .Replace("addWoodSwitch", "wood")
                          .Replace("emptyOreSwitch", "output")
                          .Replace("AddOre", "ore")
                          .Replace("AddWood", "wood")
                          .Replace("AddFuel", "fuel");
            
            // Remove "add " prefix when followed by material names
            // "Walking to add ore" -> "Walking to ore"
            // But preserve "Adding ore" as that's a valid action
            result = System.Text.RegularExpressions.Regex.Replace(
                result, 
                @"(?i)\bwalking to add (\w+)", 
                "Walking to $1");
            
            // "Going to collect the add ore" -> "Going to collect ore"
            result = System.Text.RegularExpressions.Regex.Replace(
                result, 
                @"(?i)\bcollect the add (\w+)", 
                "collect the $1");
            
            // Remove stray "add " before materials
            result = System.Text.RegularExpressions.Regex.Replace(
                result, 
                @"(?i)\bthe add (\w+)", 
                "the $1");
            
            // Clean up "output" to be more natural in context
            // "collect output" -> "collect the finished goods"
            // But we handle this in ConvertStatusToIdleChatter for context awareness
            
            return result.Trim();
        }
        
        /// <summary>
        /// Gets a message from the array while cycling through to avoid repetition.
        /// Tracks recently used messages per category and picks from unused ones first.
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
        
        /// <summary>
        /// Removes expired bubbles from the active tracking dictionaries.
        /// </summary>
        private static void CleanupExpiredBubbles(float currentTime)
        {
            var expiredKeys = new List<string>();
            foreach (var kvp in _activeStatusBubbles)
            {
                if (currentTime > kvp.Value)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }
            foreach (var key in expiredKeys)
            {
                _activeStatusBubbles.Remove(key);
                _activeBubblePositions.Remove(key);
            }
        }
        
        /// <summary>
        /// Clears any active working status display for a companion.
        /// Call this when the companion stops working.
        /// </summary>
        public static void ClearWorkingStatus(CompanionController companion)
        {
            if (companion == null) return;
            
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            _lastStatusUpdateTimes.Remove(companionId);
            _lastStatusText.Remove(companionId); // Clear tracked status text
            _companionStationContext.Remove(companionId); // Clear station context
            
            // Clear from active bubble tracking
            _activeStatusBubbles.Remove(companionId);
            _activeBubblePositions.Remove(companionId);
            
            // Clear the NPC text by setting empty text with very short duration
            if (Chat.instance != null)
            {
                try
                {
                    Chat.instance.SetNpcText(
                        companion.gameObject,
                        Vector3.up * 2.2f,
                        1f,
                        0.01f,  // Very short TTL to clear immediately
                        "",
                        "",
                        false
                    );
                }
                catch { }
            }
        }
        
        /// <summary>
        /// Determines the StationContext based on a Smelter component.
        /// Kilns produce coal from wood, smelters produce metal from ore.
        /// </summary>
        public static StationContext GetStationContextFromSmelter(Smelter smelter)
        {
            if (smelter == null) return StationContext.None;
            
            string smelterName = smelter.m_name?.ToLowerInvariant() ?? "";
            string prefabName = smelter.gameObject.name?.ToLowerInvariant() ?? "";
            
            // Check for kiln - produces coal/charcoal from wood
            if (smelterName.Contains("kiln") || smelterName.Contains("charcoal") ||
                prefabName.Contains("kiln") || prefabName.Contains("charcoal"))
            {
                return StationContext.Kiln;
            }
            
            // Check for blast furnace
            if (smelterName.Contains("blast") || prefabName.Contains("blast"))
            {
                return StationContext.BlastFurnace;
            }
            
            // Check for spinning wheel
            if (smelterName.Contains("spinning") || prefabName.Contains("spinning"))
            {
                return StationContext.SpinningWheel;
            }
            
            // Check for windmill
            if (smelterName.Contains("windmill") || prefabName.Contains("windmill"))
            {
                return StationContext.Windmill;
            }
            
            // Also check the conversion - kilns convert wood to coal
            // Smelters convert ore to bars
            if (smelter.m_conversion != null && smelter.m_conversion.Count > 0)
            {
                foreach (var conversion in smelter.m_conversion)
                {
                    if (conversion.m_from != null)
                    {
                        string inputName = conversion.m_from.name?.ToLowerInvariant() ?? "";
                        // If input is wood, it's a kiln
                        if (inputName.Contains("wood") || inputName.Contains("roundlog") || 
                            inputName.Contains("finewood") || inputName.Contains("elderbark"))
                        {
                            return StationContext.Kiln;
                        }
                    }
                    if (conversion.m_to != null)
                    {
                        string outputName = conversion.m_to.name?.ToLowerInvariant() ?? "";
                        // If output is coal/charcoal, it's a kiln
                        if (outputName.Contains("coal") || outputName.Contains("charcoal"))
                        {
                            return StationContext.Kiln;
                        }
                    }
                }
            }
            
            // Default to smelter
            return StationContext.Smelter;
        }
        
        /// <summary>
        /// Quick notification messages for various common situations.
        /// Now supports personalized messages (20% chance) using owner's name.
        /// </summary>
        public static class QuickMessages
        {
            public static void InventoryFull(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.InventoryFull,
                    Phrases.InventoryFullPersonal,
                    "inventory_full");
                SaySpeechBubble(companion, message, 4f);
            }
            
            public static void FoundLoot(CompanionController companion, int itemCount)
            {
                if (itemCount <= 0) return;
                
                string message = itemCount == 1 
                    ? "Found something!" 
                    : $"Picked up {itemCount} items.";
                SaySpeechBubble(companion, message, 3f);
            }
            
            public static void StartingTask(CompanionController companion, string taskName)
            {
                // 20% chance to use personalized response
                if (ShouldUseOwnerName())
                {
                    string ownerName = GetOwnerName(companion);
                    if (!string.IsNullOrEmpty(ownerName))
                    {
                        string message = GetPhrase(Phrases.StartingTaskPersonal, "starting_task_personal")
                            .Replace("{owner}", ownerName);
                        SaySpeechBubble(companion, message, 3f);
                        return;
                    }
                }
                
                string[] messages = new[]
                {
                    $"On it - {taskName}.",
                    $"Starting {taskName}.",
                    $"I'll handle the {taskName}.",
                    $"{taskName}? I'm on it."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void CantDoTask(CompanionController companion, string reason)
            {
                SaySpeechBubble(companion, $"I can't do that - {reason}.", 4f);
            }
            
            public static void FollowingOwner(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.Following,
                    Phrases.FollowingPersonal,
                    "following");
                SaySpeechBubble(companion, message, 2f);
            }
            
            public static void StayingHere(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.Staying,
                    Phrases.StayingPersonal,
                    "staying");
                SaySpeechBubble(companion, message, 2f);
            }
            
            public static void EnteringCombat(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.EnteringCombat,
                    Phrases.EnteringCombatPersonal,
                    "entering_combat");
                SaySpeechBubble(companion, message, 2f);
            }
            
            public static void MovingToPosition(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.MovingToPosition,
                    Phrases.MovingToPositionPersonal,
                    "moving_to_position");
                SaySpeechBubble(companion, message, 2f);
            }
            
            public static void GatheringResources(CompanionController companion, string resourceType = null)
            {
                // 20% chance to use personalized response
                if (ShouldUseOwnerName())
                {
                    string ownerName = GetOwnerName(companion);
                    if (!string.IsNullOrEmpty(ownerName))
                    {
                        string message = GetPhrase(Phrases.GatheringPersonal, "gathering_personal")
                            .Replace("{owner}", ownerName);
                        SaySpeechBubble(companion, message, 3f);
                        return;
                    }
                }
                
                string[] messages;
                if (!string.IsNullOrEmpty(resourceType))
                {
                    messages = new[]
                    {
                        $"I'll gather some {resourceType}.",
                        $"Getting {resourceType}.",
                        $"On it - {resourceType}."
                    };
                }
                else
                {
                    messages = new[]
                    {
                        "I'll gather some resources.",
                        "Time to work.",
                        "Gathering materials."
                    };
                }
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void OperatingSmelter(CompanionController companion)
            {
                string[] messages = new[]
                {
                    "I'll tend the smelter.",
                    "Keeping the forge running.",
                    "Processing ore.",
                    "I've got the smelter."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            /// <summary>
            /// Says a message about operating a kiln (making charcoal from wood).
            /// Distinct from smelter messages to avoid confusion.
            /// </summary>
            public static void OperatingKiln(CompanionController companion)
            {
                string[] messages = new[]
                {
                    "I'll tend the kiln.",
                    "Making some charcoal.",
                    "Processing wood into coal.",
                    "I've got the kiln covered."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void TendingFire(CompanionController companion)
            {
                string[] messages = new[]
                {
                    "I'll keep the fire going.",
                    "Adding fuel.",
                    "Tending the fire.",
                    "Keeping it warm."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void DepositingItems(CompanionController companion)
            {
                string[] messages = new[]
                {
                    "Storing these away.",
                    "Putting items in the chest.",
                    "Organizing storage.",
                    "Making room in my pack."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void PickingUpLoot(CompanionController companion)
            {
                string[] messages = new[]
                {
                    "I'll grab that.",
                    "Picking up loot.",
                    "Got it.",
                    "Collecting drops."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 2f);
            }
            
            public static void WorkingAtStation(CompanionController companion, string stationName = null)
            {
                string[] messages;
                if (!string.IsNullOrEmpty(stationName))
                {
                    messages = new[]
                    {
                        $"Working at the {stationName}.",
                        $"I'll use the {stationName}.",
                        $"Let me work on this."
                    };
                }
                else
                {
                    messages = new[]
                    {
                        "Let me work on this.",
                        "I'll keep busy here.",
                        "Time to get to work."
                    };
                }
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 3f);
            }
            
            public static void CombatVictory(CompanionController companion)
            {
                // Only show occasionally - don't spam after every kill
                if (Random.value > 0.3f) return;
                
                string message = GetPhrase(Phrases.Victory, "victory");
                SaySpeechBubble(companion, message, 2f);
            }
            
            public static void TrainingBow(CompanionController companion)
            {
                string message = GetPhrase(Phrases.BowTraining, "bow_training");
                SaySpeechBubble(companion, message, 3f);
            }
            
            public static void LowHealth(CompanionController companion)
            {
                string message = GetPersonalizedPhrase(
                    companion,
                    Phrases.LowHealth,
                    Phrases.LowHealthPersonal,
                    "low_health");
                SayAlert(companion, message);
            }
            
            public static void ItemRepaired(CompanionController companion, string itemName, float repairPercent)
            {
                // Only show occasionally - don't spam repair messages
                // Higher chance to show if repair was significant (>10%)
                float showChance = repairPercent > 0.10f ? 0.5f : 0.25f;
                if (Random.value > showChance) return;
                
                string percentText = $"{repairPercent * 100f:F0}%";
                
                string[] messages = new[]
                {
                    $"Fixed up the {itemName} a bit. (+{percentText})",
                    $"Repaired {itemName}. (+{percentText})",
                    $"{itemName} is in better shape now. (+{percentText})",
                    $"Gave the {itemName} some care. (+{percentText})",
                    $"Mended the {itemName}. (+{percentText})"
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 4f);
            }
            
            public static void NoMaterialsAvailable(CompanionController companion, string materialType)
            {
                string[] messages = new[]
                {
                    $"I don't have any {materialType}.",
                    $"No {materialType} in my pack or nearby chests.",
                    $"Can't find any {materialType} to use.",
                    $"I need {materialType} but can't find any."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 5f);
            }
            
            public static void NoFuelAvailable(CompanionController companion)
            {
                string message = GetPhrase(Phrases.NoFuelAvailable, "no_fuel");
                SaySpeechBubble(companion, message, 5f);
            }
            
            public static void NoOreAvailable(CompanionController companion)
            {
                string message = GetPhrase(Phrases.NoOreAvailable, "no_ore");
                SaySpeechBubble(companion, message, 5f);
            }
            
            public static void StationFull(CompanionController companion, string stationName)
            {
                string[] messages = new[]
                {
                    $"The {stationName} is full, waiting for output.",
                    $"{stationName} can't take any more right now.",
                    $"Waiting for the {stationName} to finish."
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 4f);
            }
            
            public static void NoToolAvailable(CompanionController companion, string toolType)
            {
                string[] messages = new[]
                {
                    $"I need a {toolType} but I don't have one.",
                    $"Can't do this without a {toolType}.",
                    $"I don't have the right tool - I need a {toolType}.",
                    $"Where's a {toolType} when you need one?"
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 5f);
            }
            
            public static void ItemUpgraded(CompanionController companion, string itemName, int newQuality)
            {
                string[] messages = new[]
                {
                    $"I improved the {itemName}! It's now quality {newQuality}!",
                    $"Through careful work, I upgraded the {itemName}!",
                    $"The {itemName} is better than before! Level {newQuality}!",
                    $"I enhanced the {itemName}! Quality {newQuality}!",
                    $"Look! I made the {itemName} stronger!"
                };
                SayAlert(companion, messages[Random.Range(0, messages.Length)]);
            }
            
            public static void ItemFullyRepaired(CompanionController companion, string itemName)
            {
                string[] messages = new[]
                {
                    $"Good as new! I fully repaired the {itemName}.",
                    $"The {itemName} is completely fixed!",
                    $"I restored the {itemName} to perfect condition.",
                    $"All done - {itemName} is like new!"
                };
                SaySpeechBubble(companion, messages[Random.Range(0, messages.Length)], 5f);
            }
        }
        
        #endregion
        
        #region Rate Limiting Implementation
        
        private static bool ShouldSendMessage(CompanionController companion, string message)
        {
            if (companion == null) return false;
            
            CleanupOldRecords();
            
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            
            // Check per-companion message count
            if (_messageCountsPerMinute.TryGetValue(companionId, out int count))
            {
                if (count >= MAX_MESSAGES_PER_MINUTE) return false;
            }
            
            // Check for duplicate message
            string messageKey = $"{companionId}_{message.GetHashCode()}";
            if (_lastMessageTimes.TryGetValue(messageKey, out float lastTime))
            {
                if (Time.time - lastTime < MESSAGE_COOLDOWN) return false;
            }
            
            return true;
        }
        
        private static void RecordMessageSent(CompanionController companion, string message)
        {
            if (companion == null) return;
            
            string companionId = companion.companionId ?? companion.GetInstanceID().ToString();
            string messageKey = $"{companionId}_{message.GetHashCode()}";
            
            _lastMessageTimes[messageKey] = Time.time;
            
            if (_messageCountsPerMinute.ContainsKey(companionId))
                _messageCountsPerMinute[companionId]++;
            else
                _messageCountsPerMinute[companionId] = 1;
        }
        
        private static void CleanupOldRecords()
        {
            if (Time.time - _lastCleanupTime < CLEANUP_INTERVAL) return;
            _lastCleanupTime = Time.time;
            
            // Clear message counts
            _messageCountsPerMinute.Clear();
            
            // Clear old message times
            var keysToRemove = new List<string>();
            foreach (var kvp in _lastMessageTimes)
            {
                if (Time.time - kvp.Value > MESSAGE_COOLDOWN * 2)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            
            foreach (var key in keysToRemove)
            {
                _lastMessageTimes.Remove(key);
            }
        }
        
        #endregion
    }
}
