using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    // Core's own settings, as a player sees them with "Show advanced" off, are keybinds plus a short list of
    // basics; everything else is advanced. Internal, migration and screen-position keys never show (the UI writes them
    // itself). Applied by CfgDiscovery when it lists an entry and again on RefreshTags, so a later retag cannot undo it.
    // Every Core setting shows one plain line; the full original text is kept for the hover.
    // VABackpacks gets the same treatment from here, so its window rows follow the same rules without a
    // VABackpacks build. A "Section.*|Key" row covers every section starting "Section." (one per bag).
    // The window only: the .cfg file and ConfigurationManager are unchanged.
    internal static class CoreConfigTiers
    {
        private const string CoreGuid = "com.Fire.FiresUnifiedCore";
        private const string BackpacksGuid = "com.Fire.VA_Backpacks";

        private static readonly HashSet<string> Basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "00 - Config UI|07 MenuButton", "00 - Config UI|08 WindowScale", "00 - Config UI|10 PauseGame",
            "General|Skip Intro Cinematic", "Help|Show Discord button",
            "UI|UI scroll speed", "UI|Reduce motion", "UI|Minimap size",
            "Building|New piece faces away from you", "Combat|Melee aims with the crosshair",
            "Storage|Craft From Containers", "Storage|Range", "Storage|Auto Sort", "Storage|Sort Mode",
            "Storage|Chest Sign Labels", "Storage|Lock Hotbar",
            "GroupHud|ShowGroupHud", "GroundRings|ColorBlindPalette",
            "Movement|0 Custom movement (master)", "Movement|First person when crawling",
        };

        private static readonly Dictionary<string, string> Short = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "00 - Config UI|00 Open Key", "Opens this settings window." },
            { "00 - Config UI|07 MenuButton", "Adds a Mod Settings button to the main and pause menus." },
            { "00 - Config UI|08 WindowScale", "Size of this window." },
            { "00 - Config UI|09 UseGameGuiScale", "Follow the game's own UI scale setting." },
            { "00 - Config UI|10 PauseGame", "Pause single-player while this window is open." },
            { "00 - Config UI|11 WarnKeybindConflicts", "Warn when two things use the same key." },
            { "Audio|Route mod sounds to the game mixer", "Mod sounds follow the game's volume sliders." },
            { "Barber|Appearance change cost", "Coins a companion's new look costs. 0 = free." },
            { "Barber|Size minimum", "Smallest size the barber allows." },
            { "Barber|Size maximum", "Largest size the barber allows." },
            { "Building|New piece faces away from you", "New build pieces face away from you." },
            { "Camera|Camera collision", "Pull the camera in when something blocks the view." },
            { "Combat|Companion damage vs players", "How hard companions hit players. 1 = normal." },
            { "Combat|Companion damage vs companions", "How hard companions hit other players' companions. 1 = normal." },
            { "Combat|Locational Damage", "Headshots and limb hits do different damage." },
            { "Combat|Locational Damage Log Every Hit", "Log every body-part hit (for testing)." },
            { "Combat|Melee aims with the crosshair", "Melee swings go where the crosshair points." },
            { "Combat|Melee aim max pitch", "How far up or down a swing can aim, in degrees." },
            { "Companions|Core attachment rescale", "Gear stays in proportion on big or small companions." },
            { "Companions|Pass through friendly companions", "You can walk through friendly companions." },
            { "Companions|Pillion auto-board", "A companion hops on behind you when you ride." },
            { "Companions|HuntableCreatures", "Animals companions leave alone unless told to hunt." },
            { "Companions|EnforceSingleWriterMovement", "Developer check for companion movement. Leave off." },
            { "Companions|SmoothSpeedRamp", "Companions speed up and slow down smoothly." },
            { "Companions|SpeedRampSeconds", "How long companions take to reach full speed." },
            { "Companions|Rebuild body skin on reject", "Fixes a companion body that turns invisible." },
            { "Companions.Races|Allowed races", "Races a wild companion can be." },
            { "Companions.Races|Race weights", "How often each race appears." },
            { "ContextMenu|Enabled", "Hold the keys below and right-click for companion commands." },
            { "ContextMenu|ModifierKey1", "First key to hold for the right-click menu." },
            { "ContextMenu|ModifierKey2", "Second key to hold. None = only the first." },
            { "Crawling|Crawling", "Crawl under ceilings too low to crouch." },
            { "Crawling|Prone hold seconds", "Hold Crouch this long to lie down. 0 = never." },
            { "Crawling|Crawl stride length", "Matches the crawl animation to the ground." },
            { "Debug|CompanionFollowDiagnostics", "Developer log. Leave off." },
            { "Debug|CompanionVerboseLogging", "Developer log. Leave off." },
            { "Debug|PeriodicDiagnostics", "Developer log. Leave off." },
            { "Debug|CompanionJobVerboseLogging", "Developer log. Leave off." },
            { "File Watching|Enabled", "Apply config file edits while the game runs." },
            { "File Watching|Check Interval Seconds", "How long to wait before applying file edits." },
            { "File Watching|Fold Into Config Root", "Watch the config folder with one handle. Leave as is." },
            { "FiresRPGClasses.Progression|XP multiplier", "Scales all XP." },
            { "FiresRPGClasses.Progression|Kill XP multiplier", "Scales XP from kills." },
            { "FiresRPGClasses.Progression|Non-combat XP multiplier", "Scales XP from gathering, crafting and building." },
            { "FiresRPGClasses.Progression|Quest XP multiplier", "Scales XP from quests." },
            { "FiresRPGClasses.Progression|Skill points per level", "Skill points each level." },
            { "FiresRPGClasses.Progression|Extra skill points every tenth level", "Bonus skill points at levels 10, 20, 30..." },
            { "FiresRPGClasses.Progression|Skill points per Valor level", "Skill points each level past the cap." },
            { "FiresRPGClasses.Progression|Valor level XP", "XP needed for each level past the cap." },
            { "FiresRPGClasses.Progression|Attribute points per level", "Attribute points each level. 0.5 = one every two levels." },
            { "FiresRPGClasses.Progression|Attribute points per Valor level", "Attribute points each level past the cap." },
            { "FiresRPGClasses.Progression|Attribute points maximum", "Most attribute points a character can have. 0 = no limit." },
            { "FiresRPGClasses.Progression|Max character level", "Highest character level." },
            { "FiresRPGClasses.Progression|Paragon points per Valor level", "Paragon points each level past the cap." },
            { "FiresRPGClasses.Progression|Paragon cost growth", "How much more XP each level past the cap needs." },
            { "FiresRPGClasses.Progression|Fires skill max level", "Highest level Fires skills can reach." },
            { "FiresRPGClasses.Progression|Expected XP per hour", "Sets the pace of levelling. Leave as is." },
            { "FiresRPGClasses.Progression|When points drop", "What happens to spent points if rates are lowered." },
            { "Flight|Yaw left key", "Turn left while flying." },
            { "Flight|Yaw right key", "Turn right while flying." },
            { "Flight|Preset", "How flying creatures steer." },
            { "Flight|Ability 1 key", "First flying ability." },
            { "Flight|Ability 2 key", "Second flying ability." },
            { "Flight|Stop key", "Stop while flying." },
            { "General|StatusBanner", "Print one combined status box in the log." },
            { "General|StatusBannerSeconds", "Seconds between status boxes." },
            { "General|Skip Intro Cinematic", "Skip the intro videos." },
            { "General|VerboseLogging", "Extra log detail." },
            { "General|ServerAuthority", "The server's settings override yours." },
            { "GroundRings|RingOpacity", "How see-through ability rings on the ground are." },
            { "GroundRings|ColorBlindPalette", "Colour-blind friendly ground ring colours." },
            { "GroundRings|ShowOwnDamageRings", "Show the red ring under your own attacks." },
            { "GroundRings|MaxConcurrentRings", "Most ground rings shown at once." },
            { "GroupHud|ShowGroupHud", "Show your group's health bars." },
            { "GroupHud|MaxRowsPerColumn", "Members per column before it scrolls." },
            { "GroupHud|Columns", "Number of columns in the group list." },
            { "HeightmapOverride|Enabled", "Raise or dig terrain past the normal 8 m limit." },
            { "HeightmapOverride|MaxHeight", "How high terrain can be raised, in metres." },
            { "HeightmapOverride|MinHeight", "How deep terrain can be dug, in metres." },
            { "Help|Discord invite URL", "Link the Discord button opens. Empty = ours." },
            { "Help|Show Discord button", "Show the Discord help button in the pause menu." },
            { "Logging|Log Level", "How much goes into the log." },
            { "Logging|DebugTags", "Extra log detail for these topics only." },
            { "Logging|QuietOtherMods", "Also apply the log level to other mods." },
            { "Logging|QuietOtherModsKeepLevel", "No longer used." },
            { "Logging|QuietOtherModsSources", "Which other mods to quiet. Empty = all." },
            { "Logging|QuietOtherModsExempt", "Mods never quieted." },
            { "Logging|PerModLogLevel", "Log level for single mods, as Name=Level." },
            { "Logging|RouteOtherModsThroughQueue", "Keep other mods' log lines in order." },
            { "Logging|UnresolvedShaderSampleLimit", "How many examples a shader warning lists." },
            { "Loot|Location loot refresh", "Dungeon and cave chests refill over time." },
            { "Loot|Refresh after days", "Days before loot comes back." },
            { "Loot|No refresh within metres", "Loot won't refill while a player is this close." },
            { "MonsterArmorFit|Measured helmet fit", "Fit helmets to each monster's head." },
            { "MonsterArmorFit|Paint armour textures on monsters", "Show armour paint on monsters' bodies." },
            { "MonsterArmorFit|Vanilla bind on identical rigs", "Use the game's own armour fit where it already fits." },
            { "MonsterArmorFit|Split unmapped neck", "Better collar fit on monsters with no neck." },
            { "MonsterArmorFit|Name-match unmapped neck", "Better collar fit on monsters with no neck." },
            { "Movement|0 Custom movement (master)", "Climbing, vaulting, sliding and the other movement extras." },
            { "Movement|Climbing", "Players and companions can climb." },
            { "Movement|Climb building pieces", "Walls you build can be climbed too." },
            { "Movement|Player climb input", "Hold Jump while walking into a wall to climb." },
            { "Movement|Jump clear height", "Ledges this low are just jumped. 0 = automatic." },
            { "Movement|Vault max height", "Highest ledge you vault over, in metres." },
            { "Movement|Pull-up max height", "Highest ledge you pull yourself onto, in metres." },
            { "Movement|Edge pull-up", "Grab ledges in mid-air and pull up." },
            { "Movement|Scale with character size", "Bigger characters reach higher ledges." },
            { "Movement|Clip slots", "Lets other mods add animations." },
            { "Movement|Double jump", "Jump again in mid-air." },
            { "Movement|Run slide", "Tap Crouch while sprinting to slide." },
            { "Movement|Movement styles", "Class and race jump and dodge styles." },
            { "Movement|Unlock all", "Unlock every move (for testing)." },
            { "Movement|Double jump at Jump skill", "Jump skill needed for the double jump." },
            { "Movement|Run slide at Run skill", "Run skill needed for the slide." },
            { "Movement|Double jump stamina", "Stamina the double jump costs." },
            { "Movement|Run slide stamina", "Stamina the slide costs." },
            { "Movement|Run slide seconds", "How long a slide lasts." },
            { "Movement|Run slide speed", "How fast a slide starts." },
            { "Movement|Stand down for plugins", "Turn off our jump and slide when these mods are installed." },
            { "Movement|Low ceilings", "Crouch automatically under low ceilings." },
            { "Movement|Monster fall damage", "Monsters take fall damage like players." },
            { "Movement|First person when crawling", "Switch to first person while crawling." },
            { "Performance|Reuse Collision Callbacks", "Faster physics. Leave on." },
            { "Performance|Reuse Ground Checks", "Faster ground checks. Leave on." },
            { "Performance|Skip Empty Distant Sectors", "Faster distant scenery. Leave on." },
            { "Performance|Kinematic sync sweep", "Cheaper network body checks. Needs a restart." },
            { "Possession|Flyer attack auto-lock", "A possessed flyer aims at the nearest enemy." },
            { "Possession|Enable admin possession", "Admins can control the creature under the crosshair." },
            { "Possession|Allow raven possession", "Admins can fly as the tutorial ravens." },
            { "Possession|Possess key", "Take control of the creature you look at. Press again to let go." },
            { "Possession|Release key", "Let go of a possessed creature." },
            { "Possession|Attack cycle modifier", "Hold and scroll to pick the creature's attack." },
            { "Possession|Admin max range m", "How far away you can possess, in metres." },
            { "Possession|Allow bosses", "Admins can possess bosses." },
            { "Storage|Storage Features", "Turns all the storage features on or off." },
            { "Storage|Craft From Containers", "Craft with materials from nearby chests." },
            { "Storage|Range", "How far away chests count for crafting, in metres." },
            { "Storage|Item Blacklist", "Items never taken from chests." },
            { "Storage|Ignore Ship Containers", "Don't use ship storage for crafting." },
            { "Storage|Ignore Cart Containers", "Don't use cart storage for crafting." },
            { "Storage|Auto Sort", "Sort, quick stack and restock buttons." },
            { "Storage|Quick Stack Range", "How far quick stack and restock reach, in metres." },
            { "Storage|Hopper Chests", "Hopper chests pick up items dropped nearby." },
            { "Storage|Hopper Max Radius", "Furthest a hopper can reach, in metres." },
            { "Storage|Hopper Sweep Seconds", "How often hoppers check for items." },
            { "Storage|Chest Sign Labels", "Show what a chest holds on a sign." },
            { "Storage|Sort Mode", "How sorting orders items." },
            { "Storage|Lock Hotbar", "Sorting and quick stack leave the hotbar alone." },
            { "Storage|Restock Items", "Items Restock tops up, as Name:count." },
            { "Storage|Sort Key", "Sort your bag (Shift: sort the open chest)." },
            { "Storage|Quick Stack Key", "Put items into nearby chests that already hold them." },
            { "Storage|Restock Key", "Top up your Restock Items from nearby chests." },
            { "Swimming|Swimming", "Dive underwater. Crouch sinks, Jump rises." },
            { "Swimming|Vertical swim speed", "How fast you rise or sink underwater." },
            { "Tutorial|SkipKey", "Skip a tutorial step." },
            { "Tutorial|SkipKey_Ctrl", "Hold Ctrl with the skip key." },
            { "Tutorial|SkipKey_Shift", "Hold Shift with the skip key." },
            { "Tutorial|SkipKey_Alt", "Hold Alt with the skip key." },
            { "Tutorial|BackKey", "Go back a tutorial step." },
            { "Tutorial|BackKey_Ctrl", "Hold Ctrl with the back key." },
            { "Tutorial|BackKey_Shift", "Hold Shift with the back key." },
            { "Tutorial|BackKey_Alt", "Hold Alt with the back key." },
            { "Tutorial|LeaveKey", "Leave the tutorial." },
            { "Tutorial|LeaveKey_Ctrl", "Hold Ctrl with the leave key." },
            { "Tutorial|LeaveKey_Shift", "Hold Shift with the leave key." },
            { "Tutorial|LeaveKey_Alt", "Hold Alt with the leave key." },
            { "UI|UI scroll speed", "How fast lists scroll with the mouse wheel." },
            { "UI|Fires UI override", "Use the new Fires menus and panels." },
            { "UI|Fires hub key", "Opens the Fires hub." },
            { "UI|Reduce motion", "Fewer moving effects in menus." },
            { "UI|Sky drift speed", "How fast the hub's night sky moves." },
            { "UI|Minimap size", "Size of the minimap." },
            { "UI|Layout pills modifier", "Hold to show the Edit layout and Reset layout buttons." },
            { "Undo|Undo key", "Undo your last build tool edit." },
            { "Undo|Redo key", "Redo the edit you undid." },
            { "Vault|Roll back with the world", "After a crash, the bank goes back with the world save." },
            { "WildSpawn|No-park radius", "How far wild companions keep their homes from blocked spots." },
        };

        // Written by the UI itself (positions, window toggles) or one-off migration data: never a player setting.
        private static readonly HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "00 - Config UI|05 FiresModsOnly",
            "00 - Config UI|06 ShowAdvanced",
            "00 - Config UI|06b KeybindsOnly",
            "GroupHud|DefaultPosX",
            "GroupHud|DefaultPosY",
            "WildSpawn|No-park spots",
            "Logging|BannerEmojiColumns",
        };

        private static readonly HashSet<string> BackpacksBasics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Backpacks.Camp|Enabled", "Camp|CustomCamps",
        };

        private static readonly Dictionary<string, string> BackpacksShort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Backpack.*|Rows", "Rows of slots in this bag." },
            { "Backpack.*|Columns", "Columns of slots in this bag." },
            { "Backpack.*|WeightReductionMultiplier", "How much the items inside weigh. 0.5 = half." },
            { "Backpack.*|ColdResistance", "Wearing it protects from cold." },
            { "Backpack.*|WetResistance", "Wearing it protects from getting wet." },
            { "Backpack.*|Teleportable", "Can be taken through portals while full." },
            { "Backpack.*|FeatherFall", "Wearing it stops fall damage." },
            { "Backpack.*|SpeedModifier", "Changes your speed while worn. 0 = normal." },
            { "Backpack.*|CampBlueprint", "PlanBuild blueprint used for this bag's camp. Empty = the normal camp." },
            { "Backpack.*|CampBlueprintYOffset", "Raise or sink the blueprint camp, in metres." },
            { "Backpack.*|StrengthBonus", "Wearing it lets you carry more." },
            { "Backpacks|OpenHotkey", "Opens your backpack." },
            { "Backpacks.Camp|Enabled", "Drop a bag and hold the camp keys to set up camp." },
            { "Backpacks.Camp|GestureKey", "Hold Shift and this key to set up or pack a camp." },
            { "Backpacks.Camp|HoldDurationSeconds", "How long to hold the camp keys." },
            { "Backpacks.Camp|PieceSpawnRadius", "How far camp pieces are placed from the fire." },
            { "Backpacks.Camp|PieceMinSeparation", "Smallest gap between camp pieces." },
            { "Backpacks.Camp|CampfirePrefab", "Campfire the camp uses." },
            { "Backpacks.Camp|WorkbenchPrefab", "Workbench the camp uses." },
            { "Backpacks.Camp|ContainerPrefab", "Chest the camp uses." },
            { "Backpacks.Camp|BedPrefab", "Bed the camp uses (first one found)." },
            { "Backpacks.Camp|GroundTrueMaxSpread", "How uneven the ground can be under a camp piece." },
            { "Backpacks.Camp|WoodpilePrefab", "Wood pile the camp uses. Empty = none." },
            { "Backpacks.Camp|ChairPrefab", "Seat the camp uses (first one found)." },
            { "Backpacks.Camp|CookingStationPrefab", "Cooking grill the camp uses. Empty = none." },
            { "Backpacks.Camp|TerrainModifierPrefab", "Ground patch under the camp. Empty = none." },
            { "Backpacks.Camp|FireOffsetForward", "How far in front of you the fire goes." },
            { "Backpacks.Camp|HoboStickDeployDelaySeconds", "Delay before the Hobo Stick camp appears." },
            { "Backpacks.Camp|DeployInteractRadius", "How close you must be to set up or pack a camp." },
            { "Backpacks.Camp.Blueprint|SpawnFire", "Add a campfire to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnWorkbench", "Add a workbench to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnBed", "Add a bed to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnWoodpile", "Add a wood pile to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnChair", "Add a seat to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnCookingStation", "Add a cooking grill to blueprint camps." },
            { "Backpacks.Camp.Blueprint|SpawnTerrainPatch", "Level the ground under blueprint camps." },
            { "Camp|Radius", "Camp size in metres for bag levels 1 to 4." },
            { "Camp|PieceCap", "Most pieces a camp can hold, for bag levels 1 to 4." },
            { "Camp|Level3Extra", "Extra cost to upgrade a bag to level 3." },
            { "Camp|Level4Extra", "Extra cost to upgrade a bag to level 4." },
            { "Camp|CustomCamps", "Packing a camp also packs your own buildings nearby." },
            { "CampStorage|Food", "How much food weighs in a packed camp. 0 = nothing." },
            { "CampStorage|Gear", "How much gear weighs in a packed camp." },
            { "CampStorage|OreBars", "How much ore and metal weigh in a packed camp." },
            { "CampStorage|Materials", "How much materials weigh in a packed camp." },
            { "CampStorage|Other", "How much everything else weighs in a packed camp." },
            { "CampStorage|OreBarList", "Items counted as ore and metal." },
            { "Early Magic|Enabled", "Adds the early magic staffs and eitr foods." },
            { "Early Magic|Staff Projectiles", "Each staff fires its own spell, or all share one." },
            { "Early Magic|Staff Attack Styles", "Each staff attacks its own way, with a second attack." },
            { "Early Magic|Staff secondary clips", "Staff second attacks play their own animation." },
            { "General|VerboseLogging", "Extra log detail." },
            { "General|EnableRecipes", "Bags and staffs can be crafted." },
            { "Loot|One chest in", "About one chest in this many holds a bag. 0 = never." },
            { "Loot|Roll FLO rarity on chest bags", "Bags found in chests get a loot rarity." },
            { "Recipes.*|Recipe", "What crafting this costs. Empty = can't be crafted." },
            { "Recipes.*|Upgrade", "What upgrading this costs. Empty = half the recipe." },
        };

        private static bool InSet(HashSet<string> set, string section, string key) =>
            set.Contains(section + "|" + key) || (WildSection(section, out string wild) && set.Contains(wild + "|" + key));

        private static bool InMap(Dictionary<string, string> map, string section, string key, out string text) =>
            map.TryGetValue(section + "|" + key, out text) || (WildSection(section, out string wild) && map.TryGetValue(wild + "|" + key, out text));

        private static bool WildSection(string section, out string wild)
        {
            int dot = section == null ? -1 : section.IndexOf('.');
            wild = dot > 0 ? section.Substring(0, dot) + ".*" : null;
            return wild != null;
        }

        public static bool IsCore(string guid) => string.Equals(guid, CoreGuid, StringComparison.OrdinalIgnoreCase);

        public static bool Has(string guid) => IsCore(guid) || string.Equals(guid, BackpacksGuid, StringComparison.OrdinalIgnoreCase);

        public static bool IsHidden(string guid, ConfigDefinition definition)
        {
            if (definition == null || !IsCore(guid)) return false;
            string key = definition.Key ?? "";
            return Hidden.Contains(definition.Section + "|" + key)
                || key.IndexOf("migrat", StringComparison.OrdinalIgnoreCase) >= 0
                || key.StartsWith("_", StringComparison.Ordinal);
        }

        public static string ShortDescription(string guid, ConfigDefinition definition)
        {
            if (definition == null || !Has(guid)) return null;
            return InMap(IsCore(guid) ? Short : BackpacksShort, definition.Section, definition.Key, out string text) ? text : null;
        }

        public static CfgTags Overlay(string guid, ConfigDefinition definition, ConfigEntryBase entry, CfgTags tags)
        {
            if (definition == null || entry == null || !Has(guid)) return tags;
            if (IsKeybind(definition, entry) || InSet(IsCore(guid) ? Basics : BackpacksBasics, definition.Section, definition.Key)) return tags;
            if (tags.IsAdvanced) return tags;
            if (ReferenceEquals(tags, CfgTags.None)) tags = new CfgTags();
            tags.IsAdvanced = true;
            return tags;
        }
        private static bool IsKeybind(ConfigDefinition definition, ConfigEntryBase entry)
        {
            Type type = entry.SettingType;
            if (type == typeof(KeyCode) || type == typeof(KeyboardShortcut)) return true;
            string key = definition.Key ?? "";
            if (key.EndsWith("_Ctrl", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_Shift", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("_Alt", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_Modifier", StringComparison.OrdinalIgnoreCase))   // was any "Modifier" (VABackpacks SpeedModifier / TerrainModifierPrefab showed as basics)
                return true;
            return type == typeof(string) && KeybindStrings.IsKeybindEntry(key, entry.Description?.Description, entry.BoxedValue as string);
        }
    }
}
