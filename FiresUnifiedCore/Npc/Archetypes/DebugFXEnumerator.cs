using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Debug utility to enumerate all available VFX, SFX, and Emotes in Valheim.
    /// 
    /// USAGE:
    /// 1. Open console (~)
    /// 2. Type command:
    ///    - listfx          - Dump all VFX to fx_dump.txt
    ///    - listsfx         - Dump all SFX to sfx_dump.txt  
    ///    - listemotes      - Dump all emotes/animations to emotes_dump.txt
    ///    - testfx <name>   - Spawn test effect
    ///    - testsfx <name>  - Play test sound
    ///    - testemote <name> - Play emote on player
    /// 3. Check BepInEx/config/FiresRPGmaker/
    /// </summary>
    public static class DebugFXEnumerator
    {
        private static bool _commandRegistered = false;
        
        /// <summary>
        /// Registers the debug console commands.
        /// </summary>
        public static void RegisterCommands()
        {
            if (_commandRegistered) return;
            
            // VFX Commands
            new Terminal.ConsoleCommand("listfx", "Lists all VFX prefabs to a file", (args) =>
            {
                EnumerateAndDumpFX();
            });
            
            new Terminal.ConsoleCommand("testfx", "Spawns a test FX at player position. Usage: testfx <prefab_name> [scale]", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: testfx <prefab_name> [scale]");
                    return;
                }
                
                string prefabName = args[1];
                float scale = args.Length > 2 ? float.Parse(args[2]) : 1f;
                TestFX(prefabName, scale);
            });
            
            new Terminal.ConsoleCommand("listfxcategory", "Lists FX by category. Usage: listfxcategory <shield|fire|ice|lightning|heal|impact|aura>", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: listfxcategory <shield|fire|ice|lightning|heal|impact|aura|poison|spirit>");
                    return;
                }
                
                ListFXByCategory(args[1].ToLowerInvariant());
            });
            
            // SFX Commands
            new Terminal.ConsoleCommand("listsfx", "Lists all SFX prefabs to a file with detailed audio info", (args) =>
            {
                EnumerateAndDumpSFX();
            });
            
            new Terminal.ConsoleCommand("testsfx", "Plays a test SFX at player position. Usage: testsfx <prefab_name>", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: testsfx <prefab_name>");
                    return;
                }
                
                TestSFX(args[1]);
            });
            
            new Terminal.ConsoleCommand("listsfxcategory", "Lists SFX by category. Usage: listsfxcategory <attack|creature|ambient|ui|building>", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: listsfxcategory <attack|creature|ambient|ui|building|weapon|footstep|voice>");
                    return;
                }
                
                ListSFXByCategory(args[1].ToLowerInvariant());
            });
            
            // Emote/Animation Commands
            new Terminal.ConsoleCommand("listemotes", "Lists all player emotes and animations to a file with durations", (args) =>
            {
                EnumerateAndDumpEmotes();
            });
            
            new Terminal.ConsoleCommand("testemote", "Plays an emote on the player. Usage: testemote <emote_name>", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: testemote <emote_name>");
                    return;
                }
                
                TestEmote(args[1]);
            });
            
            new Terminal.ConsoleCommand("listanims", "Lists all animation clips from player animator to a file", (args) =>
            {
                EnumerateAndDumpAnimations();
            });
            
            // Clutter/Grass Commands
            new Terminal.ConsoleCommand("listclutter", "Lists all clutter entries with valid materials to a file. Usage: listclutter [grass|all]", (args) =>
            {
                string filter = args.Length > 1 ? args[1].ToLowerInvariant() : "all";
                EnumerateAndDumpClutter(filter);
            });
            
            new Terminal.ConsoleCommand("setgrassmat", "Sets the grass overlay material by clutter name. Usage: setgrassmat <clutter_name>", (args) =>
            {
                if (args.Length < 2)
                {
                    Debug.Log("Usage: setgrassmat <clutter_name>");
                    Debug.Log("Use 'listclutter grass' to see available grass clutter entries");
                    return;
                }
                
                SetGrassMaterial(args[1]);
            });
            
            _commandRegistered = true;
            Debug.Log("[DebugFXEnumerator] Console commands registered: listfx, testfx, listfxcategory, listsfx, testsfx, listsfxcategory, listemotes, testemote, listanims, listclutter, setgrassmat");
        }
        
        /// <summary>
        /// Enumerates all VFX prefabs and dumps them to a file.
        /// </summary>
        public static void EnumerateAndDumpFX()
        {
            if (ZNetScene.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ZNetScene not available");
                return;
            }
            
            var allPrefabs = ZNetScene.instance.m_prefabs;
            
            var vfxPrefabs = new List<string>();
            var fxPrefabs = new List<string>();
            var sfxPrefabs = new List<string>();
            var particlePrefabs = new List<string>();
            var effectPrefabs = new List<string>();
            
            foreach (var prefab in allPrefabs)
            {
                if (prefab == null) continue;
                
                string name = prefab.name.ToLowerInvariant();
                
                // Categorize by prefix
                if (name.StartsWith("vfx_"))
                {
                    vfxPrefabs.Add(GetPrefabInfo(prefab));
                }
                else if (name.StartsWith("fx_"))
                {
                    fxPrefabs.Add(GetPrefabInfo(prefab));
                }
                else if (name.StartsWith("sfx_"))
                {
                    sfxPrefabs.Add(GetPrefabInfo(prefab));
                }
                else if (HasParticles(prefab))
                {
                    particlePrefabs.Add(GetPrefabInfo(prefab));
                }
                
                // Check for effect-related components
                if (prefab.GetComponent<TimedDestruction>() != null ||
                    prefab.GetComponent<ZSFX>() != null ||
                    name.Contains("effect") ||
                    name.Contains("hit") ||
                    name.Contains("impact"))
                {
                    if (!vfxPrefabs.Contains(prefab.name) && 
                        !fxPrefabs.Contains(prefab.name) &&
                        !effectPrefabs.Any(e => e.StartsWith(prefab.name)))
                    {
                        effectPrefabs.Add(GetPrefabInfo(prefab));
                    }
                }
            }
            
            // Sort all lists
            vfxPrefabs.Sort();
            fxPrefabs.Sort();
            sfxPrefabs.Sort();
            particlePrefabs.Sort();
            effectPrefabs.Sort();
            
            // Build output
            var output = new System.Text.StringBuilder();
            output.AppendLine("=== VALHEIM VFX PREFAB DUMP ===");
            output.AppendLine($"Generated: {System.DateTime.Now}");
            output.AppendLine($"Total prefabs scanned: {allPrefabs.Count}");
            output.AppendLine();
            
            output.AppendLine("=== VFX_ PREFABS (Visual Effects) ===");
            output.AppendLine($"Count: {vfxPrefabs.Count}");
            foreach (var fx in vfxPrefabs) output.AppendLine(fx);
            output.AppendLine();
            
            output.AppendLine("=== FX_ PREFABS (Effects/Impacts) ===");
            output.AppendLine($"Count: {fxPrefabs.Count}");
            foreach (var fx in fxPrefabs) output.AppendLine(fx);
            output.AppendLine();
            
            output.AppendLine("=== SFX_ PREFABS (Sound Effects) ===");
            output.AppendLine($"Count: {sfxPrefabs.Count}");
            foreach (var fx in sfxPrefabs) output.AppendLine(fx);
            output.AppendLine();
            
            output.AppendLine("=== OTHER EFFECT PREFABS ===");
            output.AppendLine($"Count: {effectPrefabs.Count}");
            foreach (var fx in effectPrefabs) output.AppendLine(fx);
            output.AppendLine();
            
            // Categorize by likely use
            output.AppendLine("=== CATEGORIZED BY LIKELY USE ===");
            output.AppendLine();
            
            output.AppendLine("-- SHIELD/PROTECTION EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("shield") || 
                f.ToLowerInvariant().Contains("protect") ||
                f.ToLowerInvariant().Contains("bubble") ||
                f.ToLowerInvariant().Contains("dvergr")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- FIRE EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("fire") || 
                f.ToLowerInvariant().Contains("flame") ||
                f.ToLowerInvariant().Contains("burn") ||
                f.ToLowerInvariant().Contains("surtling")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- ICE/FROST EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("ice") || 
                f.ToLowerInvariant().Contains("frost") ||
                f.ToLowerInvariant().Contains("cold") ||
                f.ToLowerInvariant().Contains("freeze")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- LIGHTNING EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("lightning") || 
                f.ToLowerInvariant().Contains("spark") ||
                f.ToLowerInvariant().Contains("electric") ||
                f.ToLowerInvariant().Contains("eikthyr")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- HEAL/BUFF EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("heal") || 
                f.ToLowerInvariant().Contains("tamed") ||
                f.ToLowerInvariant().Contains("offering") ||
                f.ToLowerInvariant().Contains("spawn") ||
                f.ToLowerInvariant().Contains("buff")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- IMPACT/SHOCKWAVE EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("hit") || 
                f.ToLowerInvariant().Contains("impact") ||
                f.ToLowerInvariant().Contains("slam") ||
                f.ToLowerInvariant().Contains("stomp") ||
                f.ToLowerInvariant().Contains("sledge") ||
                f.ToLowerInvariant().Contains("ground")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- POISON/NATURE EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("poison") || 
                f.ToLowerInvariant().Contains("blob") ||
                f.ToLowerInvariant().Contains("root") ||
                f.ToLowerInvariant().Contains("green") ||
                f.ToLowerInvariant().Contains("nature")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- SPIRIT/GHOST EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("spirit") || 
                f.ToLowerInvariant().Contains("ghost") ||
                f.ToLowerInvariant().Contains("wraith") ||
                f.ToLowerInvariant().Contains("despawn")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- BOSS EFFECTS (HIGH IMPACT) --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("eikthyr") || 
                f.ToLowerInvariant().Contains("elder") ||
                f.ToLowerInvariant().Contains("bonemass") ||
                f.ToLowerInvariant().Contains("moder") ||
                f.ToLowerInvariant().Contains("yagluth") ||
                f.ToLowerInvariant().Contains("queen")))
            {
                output.AppendLine($"  {fx}");
            }
            output.AppendLine();
            
            output.AppendLine("-- CREATURE EFFECTS --");
            foreach (var fx in vfxPrefabs.Concat(fxPrefabs).Where(f => 
                f.ToLowerInvariant().Contains("troll") || 
                f.ToLowerInvariant().Contains("draugr") ||
                f.ToLowerInvariant().Contains("skeleton") ||
                f.ToLowerInvariant().Contains("fenring") ||
                f.ToLowerInvariant().Contains("goblin") ||
                f.ToLowerInvariant().Contains("greydwarf")))
            {
                output.AppendLine($"  {fx}");
            }
            
            // Save to file
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
            Directory.CreateDirectory(configPath);
            string filePath = Path.Combine(configPath, "fx_dump.txt");
            
            File.WriteAllText(filePath, output.ToString());
            Debug.Log($"[DebugFXEnumerator] FX dump saved to: {filePath}");
            Debug.Log($"[DebugFXEnumerator] Found {vfxPrefabs.Count} vfx_, {fxPrefabs.Count} fx_, {sfxPrefabs.Count} sfx_ prefabs");
        }
        
        /// <summary>
        /// Gets detailed info about a prefab.
        /// </summary>
        private static string GetPrefabInfo(GameObject prefab)
        {
            var info = new System.Text.StringBuilder();
            info.Append(prefab.name);
            
            // Check for key components
            var components = new List<string>();
            
            if (HasParticles(prefab))
                components.Add("Particles");
            if (prefab.GetComponentInChildren<Light>(true) != null)
                components.Add("Light");
            if (prefab.GetComponent<TimedDestruction>() != null)
            {
                var td = prefab.GetComponent<TimedDestruction>();
                components.Add($"Duration:{td.m_timeout:F1}s");
            }
            if (prefab.GetComponent<ZSFX>() != null)
                components.Add("Sound");
            if (prefab.GetComponentInChildren<Animator>(true) != null)
                components.Add("Animated");
                
            if (components.Count > 0)
            {
                info.Append($" [{string.Join(", ", components)}]");
            }
            
            return info.ToString();
        }
        
        /// <summary>
        /// Checks if a prefab has particle systems without directly referencing ParticleSystem type.
        /// </summary>
        private static bool HasParticles(GameObject prefab)
        {
            // Use reflection or component search to avoid direct ParticleSystem reference
            var allComponents = prefab.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComponents)
            {
                if (comp != null && comp.GetType().Name == "ParticleSystem")
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Tests an FX prefab by spawning it at the player's position.
        /// </summary>
        private static void TestFX(string prefabName, float scale)
        {
            if (Player.m_localPlayer == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No local player");
                return;
            }
            
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"[DebugFXEnumerator] Prefab not found: {prefabName}");
                return;
            }
            
            Vector3 pos = Player.m_localPlayer.transform.position + Vector3.up * 1.5f;
            var instance = Object.Instantiate(prefab, pos, Quaternion.identity);
            
            if (scale != 1f)
            {
                instance.transform.localScale *= scale;
            }
            
            Debug.Log($"[DebugFXEnumerator] Spawned {prefabName} at {pos} with scale {scale}");
        }
        
        /// <summary>
        /// Lists FX prefabs by category to console.
        /// </summary>
        private static void ListFXByCategory(string category)
        {
            if (ZNetScene.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ZNetScene not available");
                return;
            }
            
            var allPrefabs = ZNetScene.instance.m_prefabs;
            var matches = new List<string>();
            
            string[] searchTerms;
            switch (category)
            {
                case "shield":
                    searchTerms = new[] { "shield", "protect", "bubble", "dvergr_shield", "dvergr_magic" };
                    break;
                case "fire":
                    searchTerms = new[] { "fire", "flame", "burn", "surtling", "fireball" };
                    break;
                case "ice":
                    searchTerms = new[] { "ice", "frost", "cold", "freeze", "frostbolt" };
                    break;
                case "lightning":
                    searchTerms = new[] { "lightning", "spark", "electric", "eikthyr", "thunder" };
                    break;
                case "heal":
                    searchTerms = new[] { "heal", "tamed", "offering", "heart", "rested" };
                    break;
                case "impact":
                    searchTerms = new[] { "hit", "impact", "slam", "stomp", "sledge", "ground" };
                    break;
                case "aura":
                    searchTerms = new[] { "aura", "nova", "circle", "bonemass", "breath" };
                    break;
                case "poison":
                    searchTerms = new[] { "poison", "blob", "ooze", "vilefang", "toxic" };
                    break;
                case "spirit":
                    searchTerms = new[] { "spirit", "ghost", "wraith", "despawn", "soul" };
                    break;
                default:
                    Debug.Log($"Unknown category: {category}");
                    Debug.Log("Valid categories: shield, fire, ice, lightning, heal, impact, aura, poison, spirit");
                    return;
            }
            
            foreach (var prefab in allPrefabs)
            {
                if (prefab == null) continue;
                string name = prefab.name.ToLowerInvariant();
                
                // Only include vfx_ or fx_ prefabs
                if (!name.StartsWith("vfx_") && !name.StartsWith("fx_")) continue;
                
                foreach (var term in searchTerms)
                {
                    if (name.Contains(term))
                    {
                        matches.Add(prefab.name);
                        break;
                    }
                }
            }
            
            matches.Sort();
            
            Debug.Log($"=== {category.ToUpper()} EFFECTS ({matches.Count} found) ===");
            foreach (var match in matches)
            {
                Debug.Log($"  {match}");
            }
        }
        
        #region SFX Enumeration
        
        /// <summary>
        /// Enumerates all SFX prefabs and dumps them to a file with detailed audio info.
        /// </summary>
        public static void EnumerateAndDumpSFX()
        {
            if (ZNetScene.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ZNetScene not available");
                return;
            }
            
            var allPrefabs = ZNetScene.instance.m_prefabs;
            var sfxList = new List<SFXInfo>();
            
            foreach (var prefab in allPrefabs)
            {
                if (prefab == null) continue;
                
                string name = prefab.name.ToLowerInvariant();
                
                // Get sfx_ prefixed prefabs
                if (name.StartsWith("sfx_"))
                {
                    sfxList.Add(GetSFXInfo(prefab));
                }
            }
            
            // Sort by name
            sfxList = sfxList.OrderBy(s => s.Name).ToList();
            
            // Build output
            var output = new System.Text.StringBuilder();
            output.AppendLine("=== VALHEIM SFX PREFAB DUMP ===");
            output.AppendLine($"Generated: {System.DateTime.Now}");
            output.AppendLine($"Total SFX prefabs found: {sfxList.Count}");
            output.AppendLine();
            
            output.AppendLine("=== FORMAT ===");
            output.AppendLine("Name [Duration, Volume, Pitch, 3D/2D, Loop, Components]");
            output.AppendLine();
            
            // Group by category
            var categories = new Dictionary<string, List<SFXInfo>>
            {
                { "CREATURE", sfxList.Where(s => IsCreatureSFX(s.Name)).ToList() },
                { "WEAPON/ATTACK", sfxList.Where(s => IsWeaponSFX(s.Name)).ToList() },
                { "ENVIRONMENT/AMBIENT", sfxList.Where(s => IsAmbientSFX(s.Name)).ToList() },
                { "BUILDING/CRAFTING", sfxList.Where(s => IsBuildingSFX(s.Name)).ToList() },
                { "UI/FEEDBACK", sfxList.Where(s => IsUISFX(s.Name)).ToList() },
                { "FOOTSTEPS/MOVEMENT", sfxList.Where(s => IsFootstepSFX(s.Name)).ToList() },
            };
            
            // Get uncategorized
            var categorized = categories.Values.SelectMany(x => x).Select(x => x.Name).ToHashSet();
            var uncategorized = sfxList.Where(s => !categorized.Contains(s.Name)).ToList();
            categories["OTHER"] = uncategorized;
            
            foreach (var category in categories)
            {
                output.AppendLine($"=== {category.Key} SFX ({category.Value.Count}) ===");
                foreach (var sfx in category.Value.OrderBy(s => s.Name))
                {
                    output.AppendLine(sfx.ToString());
                }
                output.AppendLine();
            }
            
            // Also output a simple list for quick reference
            output.AppendLine("=== QUICK REFERENCE (Name only) ===");
            foreach (var sfx in sfxList)
            {
                output.AppendLine(sfx.Name);
            }
            
            // Save to file
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
            Directory.CreateDirectory(configPath);
            string filePath = Path.Combine(configPath, "sfx_dump.txt");
            
            File.WriteAllText(filePath, output.ToString());
            Debug.Log($"[DebugFXEnumerator] SFX dump saved to: {filePath}");
            Debug.Log($"[DebugFXEnumerator] Found {sfxList.Count} sfx_ prefabs");
        }
        
        private class SFXInfo
        {
            public string Name;
            public float Duration;
            public float MinVolume;
            public float MaxVolume;
            public float MinPitch;
            public float MaxPitch;
            public bool Is3D;
            public bool Loop;
            public int ClipCount;
            public List<string> Components = new List<string>();
            
            public override string ToString()
            {
                string vol = MinVolume == MaxVolume ? $"Vol:{MinVolume:F1}" : $"Vol:{MinVolume:F1}-{MaxVolume:F1}";
                string pitch = MinPitch == MaxPitch ? $"Pitch:{MinPitch:F1}" : $"Pitch:{MinPitch:F1}-{MaxPitch:F1}";
                string spatial = Is3D ? "3D" : "2D";
                string loop = Loop ? "Loop" : "";
                string clips = ClipCount > 1 ? $"{ClipCount}clips" : "";
                string comps = Components.Count > 0 ? string.Join(", ", Components) : "";
                
                var parts = new List<string> { $"Dur:{Duration:F2}s", vol, pitch, spatial };
                if (!string.IsNullOrEmpty(loop)) parts.Add(loop);
                if (!string.IsNullOrEmpty(clips)) parts.Add(clips);
                if (!string.IsNullOrEmpty(comps)) parts.Add(comps);
                
                return $"{Name} [{string.Join(", ", parts)}]";
            }
        }
        
        private static SFXInfo GetSFXInfo(GameObject prefab)
        {
            var info = new SFXInfo { Name = prefab.name };
            
            // Check for ZSFX component (Valheim's custom SFX handler)
            var zsfx = prefab.GetComponent<ZSFX>();
            if (zsfx != null)
            {
                info.MinVolume = zsfx.m_minVol;
                info.MaxVolume = zsfx.m_maxVol;
                info.MinPitch = zsfx.m_minPitch;
                info.MaxPitch = zsfx.m_maxPitch;
                info.Is3D = zsfx.m_distanceReverb;
                
                // m_loop doesn't exist on ZSFX, check via reflection
                var loopField = typeof(ZSFX).GetField("m_loop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loopField != null)
                {
                    info.Loop = (bool)loopField.GetValue(zsfx);
                }
                
                // Get audio clips
                if (zsfx.m_audioClips != null)
                {
                    info.ClipCount = zsfx.m_audioClips.Length;
                    
                    // Get duration from first clip
                    if (zsfx.m_audioClips.Length > 0 && zsfx.m_audioClips[0] != null)
                    {
                        info.Duration = zsfx.m_audioClips[0].length;
                    }
                }
                
                info.Components.Add("ZSFX");
            }
            
            // Check for AudioSource
            var audioSource = prefab.GetComponent<AudioSource>();
            if (audioSource != null)
            {
                if (audioSource.clip != null)
                {
                    info.Duration = audioSource.clip.length;
                    info.ClipCount = 1;
                }
                info.MinVolume = info.MaxVolume = audioSource.volume;
                info.MinPitch = info.MaxPitch = audioSource.pitch;
                info.Is3D = audioSource.spatialBlend > 0.5f;
                info.Loop = audioSource.loop;
                info.Components.Add("AudioSource");
            }
            
            // Check for TimedDestruction
            var td = prefab.GetComponent<TimedDestruction>();
            if (td != null)
            {
                info.Duration = td.m_timeout;
                info.Components.Add($"TimedDestruction");
            }
            
            return info;
        }
        
        private static bool IsCreatureSFX(string name)
        {
            string[] creatures = { "boar", "deer", "wolf", "troll", "draugr", "skeleton", "goblin", 
                "dragon", "serpent", "blob", "leech", "wraith", "ghost", "fenring", "lox",
                "greydwarf", "greyling", "neck", "bat", "crow", "seagull", "deathsquito",
                "hatchling", "tick", "gjall", "seeker", "dverger", "charred", "morgen",
                "bear", "chicken", "hen", "hare", "vulture", "asksvin", "fader", "queen" };
            return creatures.Any(c => name.Contains(c));
        }
        
        private static bool IsWeaponSFX(string name)
        {
            string[] weapons = { "sword", "axe", "club", "mace", "hammer", "sledge", "knife", 
                "bow", "arrow", "spear", "atgeir", "battleaxe", "pickaxe", "swing", "hit",
                "attack", "slash", "blocked", "parry", "staff", "fireball", "frostbolt" };
            return weapons.Any(w => name.Contains(w));
        }
        
        private static bool IsAmbientSFX(string name)
        {
            string[] ambient = { "wind", "rain", "thunder", "water", "fire", "torch", "bonfire",
                "ocean", "river", "bird", "insect", "ambient", "environment", "weather" };
            return ambient.Any(a => name.Contains(a));
        }
        
        private static bool IsBuildingSFX(string name)
        {
            string[] building = { "build", "place", "destroy", "craft", "forge", "smelter", 
                "kiln", "fermenter", "cauldron", "workbench", "chest", "door", "portal",
                "mill", "oven", "refinery", "cartographer", "obliterator" };
            return building.Any(b => name.Contains(b));
        }
        
        private static bool IsUISFX(string name)
        {
            string[] ui = { "gui", "menu", "click", "select", "equip", "pickup", "drop",
                "inventory", "craft", "repair", "upgrade" };
            return ui.Any(u => name.Contains(u));
        }
        
        private static bool IsFootstepSFX(string name)
        {
            string[] footstep = { "footstep", "walk", "run", "jump", "land", "swim", "climb",
                "step", "foot" };
            return footstep.Any(f => name.Contains(f));
        }
        
        /// <summary>
        /// Tests an SFX by playing it at the player's position.
        /// </summary>
        private static void TestSFX(string prefabName)
        {
            if (Player.m_localPlayer == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No local player");
                return;
            }
            
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                Debug.LogWarning($"[DebugFXEnumerator] SFX prefab not found: {prefabName}");
                return;
            }
            
            Vector3 pos = Player.m_localPlayer.transform.position;
            var instance = Object.Instantiate(prefab, pos, Quaternion.identity);
            
            Debug.Log($"[DebugFXEnumerator] Playing SFX {prefabName} at {pos}");
        }
        
        /// <summary>
        /// Lists SFX prefabs by category to console.
        /// </summary>
        private static void ListSFXByCategory(string category)
        {
            if (ZNetScene.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ZNetScene not available");
                return;
            }
            
            var allPrefabs = ZNetScene.instance.m_prefabs;
            var matches = new List<string>();
            
            System.Func<string, bool> filter;
            switch (category)
            {
                case "creature":
                    filter = IsCreatureSFX;
                    break;
                case "attack":
                case "weapon":
                    filter = IsWeaponSFX;
                    break;
                case "ambient":
                case "environment":
                    filter = IsAmbientSFX;
                    break;
                case "building":
                case "crafting":
                    filter = IsBuildingSFX;
                    break;
                case "ui":
                case "feedback":
                    filter = IsUISFX;
                    break;
                case "footstep":
                case "movement":
                    filter = IsFootstepSFX;
                    break;
                default:
                    Debug.Log($"Unknown category: {category}");
                    Debug.Log("Valid categories: creature, attack/weapon, ambient/environment, building/crafting, ui/feedback, footstep/movement");
                    return;
            }
            
            foreach (var prefab in allPrefabs)
            {
                if (prefab == null) continue;
                string name = prefab.name.ToLowerInvariant();
                
                if (!name.StartsWith("sfx_")) continue;
                
                if (filter(name))
                {
                    matches.Add(prefab.name);
                }
            }
            
            matches.Sort();
            
            Debug.Log($"=== {category.ToUpper()} SFX ({matches.Count} found) ===");
            foreach (var match in matches)
            {
                Debug.Log($"  {match}");
            }
        }
        
        #endregion
        
        #region Emote/Animation Enumeration
        
        /// <summary>
        /// Enumerates all player emotes and dumps them to a file with durations.
        /// </summary>
        public static void EnumerateAndDumpEmotes()
        {
            if (Player.m_localPlayer == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No local player - need to be in game");
                return;
            }
            
            var output = new System.Text.StringBuilder();
            output.AppendLine("=== VALHEIM EMOTES DUMP ===");
            output.AppendLine($"Generated: {System.DateTime.Now}");
            output.AppendLine();
            
            // Get emotes from Player class using reflection
            output.AppendLine("=== REGISTERED EMOTES ===");
            output.AppendLine("Format: EmoteName [Duration, OneShot, FaceLookDirection]");
            output.AppendLine();
            
            // Try to get the emotes list via reflection
            var playerType = typeof(Player);
            var emotesField = playerType.GetField("m_emotes", BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (emotesField != null)
            {
                var emotes = emotesField.GetValue(Player.m_localPlayer);
                if (emotes != null)
                {
                    // m_emotes is a List<Player.EmoteData> or similar
                    var emoteList = emotes as System.Collections.IList;
                    if (emoteList != null)
                    {
                        output.AppendLine($"Found {emoteList.Count} registered emotes:");
                        output.AppendLine();
                        
                        foreach (var emote in emoteList)
                        {
                            if (emote == null) continue;
                            
                            var emoteType = emote.GetType();
                            var idField = emoteType.GetField("m_emote") ?? emoteType.GetField("m_id");
                            var animField = emoteType.GetField("m_animation") ?? emoteType.GetField("m_anim");
                            var oneShotField = emoteType.GetField("m_oneShot");
                            var faceLookField = emoteType.GetField("m_faceLookDirection");
                            
                            string emoteName = idField?.GetValue(emote)?.ToString() ?? "unknown";
                            string animName = animField?.GetValue(emote)?.ToString() ?? "";
                            bool oneShot = oneShotField != null && (bool)oneShotField.GetValue(emote);
                            bool faceLook = faceLookField != null && (bool)faceLookField.GetValue(emote);
                            
                            output.AppendLine($"  {emoteName} => {animName} [OneShot:{oneShot}, FaceLook:{faceLook}]");
                        }
                    }
                }
            }
            else
            {
                output.AppendLine("Could not find m_emotes field via reflection");
            }
            
            output.AppendLine();
            
            // Get animation clips from the player's animator
            var animator = Player.m_localPlayer.GetComponentInChildren<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                output.AppendLine("=== ANIMATION CLIPS FROM ANIMATOR ===");
                output.AppendLine("Format: ClipName [Duration, Loop, WrapMode]");
                output.AppendLine();
                
                var clips = animator.runtimeAnimatorController.animationClips;
                var emoteClips = new List<AnimationClip>();
                var otherClips = new List<AnimationClip>();
                
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    
                    string name = clip.name.ToLowerInvariant();
                    
                    // Categorize as emote or other
                    if (name.Contains("emote") || name.Contains("wave") || name.Contains("sit") ||
                        name.Contains("point") || name.Contains("bow") || name.Contains("cheer") ||
                        name.Contains("laugh") || name.Contains("dance") || name.Contains("shrug") ||
                        name.Contains("challenge") || name.Contains("despair") || name.Contains("flex") ||
                        name.Contains("blowkiss") || name.Contains("thumbsup") || name.Contains("nonono") ||
                        name.Contains("headbang") || name.Contains("cower") || name.Contains("kneel") ||
                        name.Contains("comehere") || name.Contains("roar"))
                    {
                        emoteClips.Add(clip);
                    }
                    else
                    {
                        otherClips.Add(clip);
                    }
                }
                
                output.AppendLine($"-- EMOTE ANIMATIONS ({emoteClips.Count}) --");
                foreach (var clip in emoteClips.OrderBy(c => c.name))
                {
                    output.AppendLine($"  {clip.name} [Duration:{clip.length:F2}s, Loop:{clip.isLooping}, WrapMode:{clip.wrapMode}]");
                }
                output.AppendLine();
                
                output.AppendLine($"-- OTHER ANIMATIONS ({otherClips.Count}) --");
                foreach (var clip in otherClips.OrderBy(c => c.name))
                {
                    output.AppendLine($"  {clip.name} [Duration:{clip.length:F2}s, Loop:{clip.isLooping}]");
                }
            }
            else
            {
                output.AppendLine("Could not find Animator on player");
            }
            
            output.AppendLine();
            
            // List known emote commands
            output.AppendLine("=== KNOWN EMOTE COMMANDS ===");
            output.AppendLine("These are the emotes you can use with /emote <name> or testemote <name>:");
            output.AppendLine();
            
            string[] knownEmotes = {
                "wave", "sit", "challenge", "cheer", "shrug", "dance", "despair", "flex",
                "point", "nonono", "bow", "cower", "laugh", "blowkiss", "thumbsup", 
                "comehere", "headbang", "kneel", "roar"
            };
            
            foreach (var emote in knownEmotes)
            {
                output.AppendLine($"  {emote}");
            }
            
            // Save to file
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
            Directory.CreateDirectory(configPath);
            string filePath = Path.Combine(configPath, "emotes_dump.txt");
            
            File.WriteAllText(filePath, output.ToString());
            Debug.Log($"[DebugFXEnumerator] Emotes dump saved to: {filePath}");
        }
        
        /// <summary>
        /// Enumerates all animation clips and dumps detailed info.
        /// </summary>
        public static void EnumerateAndDumpAnimations()
        {
            if (Player.m_localPlayer == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No local player - need to be in game");
                return;
            }
            
            var output = new System.Text.StringBuilder();
            output.AppendLine("=== VALHEIM ANIMATIONS DUMP ===");
            output.AppendLine($"Generated: {System.DateTime.Now}");
            output.AppendLine();
            
            // Get all animators in the scene
            var animator = Player.m_localPlayer.GetComponentInChildren<Animator>();
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No animator found on player");
                return;
            }
            
            var controller = animator.runtimeAnimatorController;
            var clips = controller.animationClips;
            
            output.AppendLine($"Controller: {controller.name}");
            output.AppendLine($"Total Clips: {clips.Length}");
            output.AppendLine();
            
            // Categorize clips
            var categories = new Dictionary<string, List<AnimationClip>>
            {
                { "EMOTES", new List<AnimationClip>() },
                { "ATTACKS/COMBAT", new List<AnimationClip>() },
                { "MOVEMENT", new List<AnimationClip>() },
                { "ITEMS/TOOLS", new List<AnimationClip>() },
                { "REACTIONS", new List<AnimationClip>() },
                { "IDLE", new List<AnimationClip>() },
                { "OTHER", new List<AnimationClip>() }
            };
            
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                
                string name = clip.name.ToLowerInvariant();
                
                if (name.Contains("emote") || name.Contains("wave") || name.Contains("sit") ||
                    name.Contains("dance") || name.Contains("cheer") || name.Contains("bow") && !name.Contains("crossbow") ||
                    name.Contains("point") || name.Contains("shrug") || name.Contains("laugh") ||
                    name.Contains("flex") || name.Contains("despair") || name.Contains("cower") ||
                    name.Contains("kneel") || name.Contains("roar") || name.Contains("thumbsup") ||
                    name.Contains("nonono") || name.Contains("headbang") || name.Contains("blowkiss") ||
                    name.Contains("comehere") || name.Contains("challenge"))
                {
                    categories["EMOTES"].Add(clip);
                }
                else if (name.Contains("attack") || name.Contains("swing") || name.Contains("slash") ||
                         name.Contains("stab") || name.Contains("throw") || name.Contains("kick") ||
                         name.Contains("punch") || name.Contains("block") || name.Contains("parry") ||
                         name.Contains("dodge") || name.Contains("roll") || name.Contains("combo"))
                {
                    categories["ATTACKS/COMBAT"].Add(clip);
                }
                else if (name.Contains("walk") || name.Contains("run") || name.Contains("sprint") ||
                         name.Contains("jump") || name.Contains("fall") || name.Contains("land") ||
                         name.Contains("swim") || name.Contains("climb") || name.Contains("crouch") ||
                         name.Contains("sneak"))
                {
                    categories["MOVEMENT"].Add(clip);
                }
                else if (name.Contains("pickup") || name.Contains("place") || name.Contains("use") ||
                         name.Contains("equip") || name.Contains("drink") || name.Contains("eat") ||
                         name.Contains("craft") || name.Contains("build") || name.Contains("fish") ||
                         name.Contains("draw") || name.Contains("aim") || name.Contains("reload"))
                {
                    categories["ITEMS/TOOLS"].Add(clip);
                }
                else if (name.Contains("hit") || name.Contains("hurt") || name.Contains("death") ||
                         name.Contains("stagger") || name.Contains("knockback") || name.Contains("stunned"))
                {
                    categories["REACTIONS"].Add(clip);
                }
                else if (name.Contains("idle"))
                {
                    categories["IDLE"].Add(clip);
                }
                else
                {
                    categories["OTHER"].Add(clip);
                }
            }
            
            // Output each category
            foreach (var category in categories)
            {
                output.AppendLine($"=== {category.Key} ({category.Value.Count}) ===");
                foreach (var clip in category.Value.OrderBy(c => c.name))
                {
                    string loopStr = clip.isLooping ? "LOOP" : "ONCE";
                    output.AppendLine($"  {clip.name} [Duration:{clip.length:F3}s, {loopStr}, FrameRate:{clip.frameRate}]");
                }
                output.AppendLine();
            }
            
            // Output summary table for emotes (most useful for movement blocking)
            output.AppendLine("=== EMOTE DURATION SUMMARY (for movement blocking) ===");
            output.AppendLine("| Emote Name | Duration (s) | Suggested Block Time |");
            output.AppendLine("|------------|--------------|---------------------|");
            
            foreach (var clip in categories["EMOTES"].OrderBy(c => c.name))
            {
                // Suggested block time is slightly less than full duration
                float blockTime = clip.isLooping ? 0f : clip.length * 0.9f;
                string blockStr = clip.isLooping ? "N/A (loop)" : $"{blockTime:F2}s";
                output.AppendLine($"| {clip.name,-40} | {clip.length:F3} | {blockStr} |");
            }
            
            // Save to file
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
            Directory.CreateDirectory(configPath);
            string filePath = Path.Combine(configPath, "animations_dump.txt");
            
            File.WriteAllText(filePath, output.ToString());
            Debug.Log($"[DebugFXEnumerator] Animations dump saved to: {filePath}");
            Debug.Log($"[DebugFXEnumerator] Found {clips.Length} animation clips");
        }
        
        /// <summary>
        /// Tests an emote by playing it on the player.
        /// </summary>
        private static void TestEmote(string emoteName)
        {
            if (Player.m_localPlayer == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] No local player");
                return;
            }
            
            // Try to trigger the emote
            try
            {
                // Use reflection to call StartEmote
                var startEmoteMethod = typeof(Player).GetMethod("StartEmote", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (startEmoteMethod != null)
                {
                    startEmoteMethod.Invoke(Player.m_localPlayer, new object[] { emoteName, false });
                    Debug.Log($"[DebugFXEnumerator] Playing emote: {emoteName}");
                }
                else
                {
                    // Fallback: try to set animator trigger
                    var animator = Player.m_localPlayer.GetComponentInChildren<Animator>();
                    if (animator != null)
                    {
                        animator.SetTrigger(emoteName);
                        Debug.Log($"[DebugFXEnumerator] Triggered animation: {emoteName}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[DebugFXEnumerator] Failed to play emote {emoteName}: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Clutter/Grass Enumeration
        
        /// <summary>
        /// Enumerates all clutter entries and dumps them to a file with material/texture info.
        /// </summary>
        public static void EnumerateAndDumpClutter(string filter = "all")
        {
            if (ClutterSystem.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ClutterSystem not available - need to be in game");
                return;
            }
            
            var clutter = ClutterSystem.instance.m_clutter;
            if (clutter == null || clutter.Count == 0)
            {
                Debug.LogWarning("[DebugFXEnumerator] No clutter entries found");
                return;
            }
            
            var output = new System.Text.StringBuilder();
            output.AppendLine("=== VALHEIM CLUTTER/GRASS DUMP ===");
            output.AppendLine($"Generated: {System.DateTime.Now}");
            output.AppendLine($"Filter: {filter}");
            output.AppendLine($"Total clutter entries: {clutter.Count}");
            output.AppendLine();
            
            var grassEntries = new List<ClutterInfo>();
            var otherEntries = new List<ClutterInfo>();
            
            foreach (var entry in clutter)
            {
                if (entry.m_prefab == null) continue;
                
                var info = GetClutterInfo(entry);
                
                string name = entry.m_prefab.name.ToLowerInvariant();
                bool isGrass = name.Contains("grass");
                
                if (filter == "grass" && !isGrass) continue;
                
                if (isGrass)
                    grassEntries.Add(info);
                else
                    otherEntries.Add(info);
            }
            
            // Sort by score (best materials first)
            grassEntries = grassEntries.OrderByDescending(e => e.Score).ThenBy(e => e.Name).ToList();
            otherEntries = otherEntries.OrderByDescending(e => e.Score).ThenBy(e => e.Name).ToList();
            
            output.AppendLine("=== FORMAT ===");
            output.AppendLine("Name [Material, Shader, Texture, Score, InstanceRenderer/Renderer]");
            output.AppendLine("Score = Higher is better for grass overlay use");
            output.AppendLine();
            
            output.AppendLine($"=== GRASS CLUTTER ({grassEntries.Count}) ===");
            output.AppendLine("** USE THESE FOR GRASS OVERLAY **");
            output.AppendLine();
            
            foreach (var info in grassEntries)
            {
                output.AppendLine(info.ToString());
            }
            output.AppendLine();
            
            if (filter != "grass")
            {
                output.AppendLine($"=== OTHER CLUTTER ({otherEntries.Count}) ===");
                foreach (var info in otherEntries)
                {
                    output.AppendLine(info.ToString());
                }
            }
            
            output.AppendLine();
            output.AppendLine("=== RECOMMENDED FOR GRASS OVERLAY ===");
            output.AppendLine("Best grass materials (sorted by score, has texture):");
            output.AppendLine();
            
            var recommended = grassEntries.Where(e => e.HasTexture).Take(10).ToList();
            foreach (var info in recommended)
            {
                output.AppendLine($"  {info.Name}");
                output.AppendLine($"    Material: {info.MaterialName}, Shader: {info.ShaderName}");
                output.AppendLine($"    Texture: {info.TextureName}, Score: {info.Score}");
                output.AppendLine();
            }
            
            output.AppendLine();
            output.AppendLine("=== TO USE A DIFFERENT GRASS MATERIAL ===");
            output.AppendLine("Run: setgrassmat <clutter_name>");
            output.AppendLine("Example: setgrassmat instanced_meadows_grass");
            
            // Save to file
            string configPath = Path.Combine(BepInEx.Paths.ConfigPath, "FiresRPGmaker");
            Directory.CreateDirectory(configPath);
            string filePath = Path.Combine(configPath, "clutter_dump.txt");
            
            File.WriteAllText(filePath, output.ToString());
            Debug.Log($"[DebugFXEnumerator] Clutter dump saved to: {filePath}");
            Debug.Log($"[DebugFXEnumerator] Found {grassEntries.Count} grass entries, {otherEntries.Count} other entries");
            
            // Also output top recommendations to console
            Debug.Log("=== TOP GRASS MATERIALS ===");
            foreach (var info in recommended.Take(5))
            {
                Debug.Log($"  {info.Name} - {info.MaterialName} ({info.TextureName})");
            }
        }
        
        private class ClutterInfo
        {
            public string Name;
            public string MaterialName;
            public string ShaderName;
            public string TextureName;
            public bool HasTexture;
            public bool IsInstanced;
            public int Score;
            public List<string> Biomes = new List<string>();
            
            public override string ToString()
            {
                string instanced = IsInstanced ? "Instanced" : "Renderer";
                string texture = HasTexture ? TextureName : "NO TEXTURE";
                string biomes = Biomes.Count > 0 ? $"Biomes: {string.Join(", ", Biomes)}" : "";
                
                return $"{Name} [Mat:{MaterialName}, Shader:{ShaderName}, Tex:{texture}, Score:{Score}, {instanced}] {biomes}";
            }
        }
        
        private static ClutterInfo GetClutterInfo(ClutterSystem.Clutter entry)
        {
            var info = new ClutterInfo
            {
                Name = entry.m_prefab.name,
                MaterialName = "none",
                ShaderName = "none",
                TextureName = "none",
                HasTexture = false,
                IsInstanced = false,
                Score = 0
            };
            
            // Get biome info
            if (entry.m_biome != Heightmap.Biome.None)
            {
                info.Biomes.Add(entry.m_biome.ToString());
            }
            
            Material mat = null;
            
            // Check for InstanceRenderer (instanced grass - preferred)
            var instanceRenderer = entry.m_prefab.GetComponent<InstanceRenderer>();
            if (instanceRenderer != null && instanceRenderer.m_material != null)
            {
                mat = instanceRenderer.m_material;
                info.IsInstanced = true;
                info.Score += 50; // Bonus for instanced rendering
            }
            else
            {
                // Check for regular Renderer
                var renderer = entry.m_prefab.GetComponent<Renderer>();
                if (renderer == null)
                    renderer = entry.m_prefab.GetComponentInChildren<Renderer>();
                
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    mat = renderer.sharedMaterial;
                }
            }
            
            if (mat != null)
            {
                info.MaterialName = mat.name;
                info.ShaderName = mat.shader != null ? mat.shader.name : "null";
                
                // Get texture
                Texture tex = GetMaterialTextureFromMat(mat);
                if (tex != null)
                {
                    info.TextureName = tex.name;
                    info.HasTexture = true;
                    info.Score += 50; // Bonus for having texture
                }
                
                // Score based on shader type
                if (info.ShaderName.Contains("Grass"))
                    info.Score += 30;
                else if (info.ShaderName.Contains("Vegetation"))
                    info.Score += 20;
                else if (info.ShaderName.Contains("Standard"))
                    info.Score += 10;
                
                // Score based on name hints
                string nameLower = info.Name.ToLowerInvariant();
                if (nameLower.Contains("meadows"))
                    info.Score += 20;
                if (nameLower.Contains("instanced"))
                    info.Score += 10;
            }
            
            return info;
        }
        
        private static Texture GetMaterialTextureFromMat(Material mat)
        {
            if (mat == null) return null;
            
            // Check common texture property names
            string[] texProps = { "_MainTex", "_BaseMap", "_Albedo", "_Diffuse", "_Texture" };
            foreach (var prop in texProps)
            {
                if (mat.HasProperty(prop))
                {
                    var tex = mat.GetTexture(prop);
                    if (tex != null) return tex;
                }
            }
            
            // Try to get any texture from the material
            var texIds = mat.GetTexturePropertyNameIDs();
            foreach (var id in texIds)
            {
                var tex = mat.GetTexture(id);
                if (tex != null) return tex;
            }
            
            return null;
        }
        
        /// <summary>
        /// Sets the grass overlay material to a specific clutter entry.
        /// </summary>
        private static void SetGrassMaterial(string clutterName)
        {
            if (ClutterSystem.instance == null)
            {
                Debug.LogWarning("[DebugFXEnumerator] ClutterSystem not available");
                return;
            }
            
            // Find the clutter entry
            ClutterSystem.Clutter found = null;
            foreach (var entry in ClutterSystem.instance.m_clutter)
            {
                if (entry.m_prefab == null) continue;
                
                if (entry.m_prefab.name.Equals(clutterName, System.StringComparison.OrdinalIgnoreCase))
                {
                    found = entry;
                    break;
                }
            }
            
            if (found == null)
            {
                Debug.LogWarning($"[DebugFXEnumerator] Clutter entry not found: {clutterName}");
                Debug.Log("Use 'listclutter grass' to see available entries");
                return;
            }
            
            // Get the material
            Material mat = null;
            
            var instanceRenderer = found.m_prefab.GetComponent<InstanceRenderer>();
            if (instanceRenderer != null && instanceRenderer.m_material != null)
            {
                mat = instanceRenderer.m_material;
            }
            else
            {
                var renderer = found.m_prefab.GetComponent<Renderer>();
                if (renderer == null)
                    renderer = found.m_prefab.GetComponentInChildren<Renderer>();
                
                if (renderer != null)
                    mat = renderer.sharedMaterial;
            }
            
            if (mat == null)
            {
                Debug.LogWarning($"[DebugFXEnumerator] No material found on clutter: {clutterName}");
                return;
            }
            
            // Set it in GrassOverlayController
            Debug.LogWarning($"[DebugFXEnumerator] grass material override unavailable - terrain decoupled: {clutterName}");
            
            Debug.Log($"[DebugFXEnumerator] Set grass material to: {clutterName} ({mat.name})");
            Debug.Log("New dirt floor pieces will use this material. Existing ones won't change.");
        }
        
        #endregion
    }
}

