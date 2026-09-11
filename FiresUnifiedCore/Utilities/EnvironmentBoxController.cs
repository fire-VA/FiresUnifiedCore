using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using FiresCore.Bridge;

namespace FiresCore.Utilities
{
    /// <summary>
    /// Placeable environment box that acts as a custom EnvZone.
    /// 
    /// Features:
    /// - Scalable with Shift+MouseWheel (uses Structure Tweaks style keybinds)
    /// - Toggleable environment, weather, biome override
    /// - Toggleable skybox (inherit world, black void, custom)
    /// - Toggleable visibility (full, edges only, hidden with marker sign)
    /// - Does NOT affect heightmap - only terrain paint, weather, and environment
    /// 
    /// This is a hammer-placeable piece that creates dungeon-interior-like zones anywhere.
    /// </summary>
    public class EnvironmentBoxController : MonoBehaviour, Hoverable, Interactable
    {
        #region Static Registry

        private static List<EnvironmentBoxController> _allBoxes = new List<EnvironmentBoxController>();
        private static EnvironmentBoxController _playerInsideBox = null;

        /// <summary>
        /// Gets the environment box the player is currently inside, if any.
        /// </summary>
        public static EnvironmentBoxController GetPlayerEnvBox() => _playerInsideBox;

        /// <summary>
        /// Gets an environment box at the given position.
        /// </summary>
        public static EnvironmentBoxController GetEnvBoxAtPosition(Vector3 pos)
        {
            foreach (var box in _allBoxes)
            {
                if (box != null && box.ContainsPoint(pos))
                    return box;
            }
            return null;
        }

        // True on a headless/dedicated server (no graphics device). The env-box ZNetView still instantiates
        // there for ZDO persistence, but every RenderSettings/EnvMan/Shader/mesh path must early-return or it NREs.
        private static bool IsClientOnly()
            => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        #endregion

        #region Configuration

        /// <summary>
        /// Available environment names from vanilla game.
        /// Complete list from EnvMan.m_environments dump.
        /// </summary>
        public static readonly string[] EnvironmentNames = {
            "",  // No override (use world environment)
            // Clear weather
            "Clear",
            "Twilight_Clear",
            "Heath clear",
            "Clear Winter",
            "Clear Summer",
            "Heath clear Winter",
            "Heath clear Summer",
            // Forest/Mist
            "Misty",
            "DeepForest Mist",
            "Misty Winter",
            "Misty Summer",
            "DeepForest Mist Winter",
            "DeepForest Mist Summer",
            // Dark/Swamp
            "Darklands_dark",
            "Darklands_dark Winter",
            "Swamp Summer",
            // Rain
            "Rain",
            "LightRain",
            "ThunderStorm",
            "SwampRain",
            "Rain Winter",
            "LightRain Winter",
            "ThunderStorm Winter",
            "ThunderStorm Fall",
            "SwampRain Winter",
            "SwampRain Fall",
            "SwampRain Summer",
            // Snow/Mountain
            "Snow",
            "Twilight_Snow",
            "Twilight_SnowStorm",
            "SnowStorm",
            // Dungeon interiors
            "Crypt",
            "SunkenCrypt",
            "Caves",
            "CavesHildir",
            "InfectedMine",
            "CryptHildir",
            "Ghosts",
            // Mistlands
            "Mistlands_clear",
            "Mistlands_rain",
            "Mistlands_thunder",
            "Mistlands_clear Winter",
            "Mistlands_clear Summer",
            "Mistlands_rain Winter",
            "Mistlands_thunder Winter",
            // Ashlands
            "Ashlands_ashrain",
            "Ashlands_ashrain_clear",
            "Ashlands_storm",
            "Ashlands_meteorshower",
            "Ashlands_misty",
            "Ashlands_CinderRain",
            "Ashlands_SeaStorm",
            // Boss environments
            "Eikthyr",
            "GDKing",
            "Bonemass",
            "Moder",
            "GoblinKing",
            "Queen",
            "Fader",
            // Special
            "nofogts",
            "DeepNorth_Freezing"
        };

        /// <summary>
        /// Available biomes for override.
        /// </summary>
        public static readonly Heightmap.Biome[] BiomeOptions = {
            Heightmap.Biome.None,  // No override
            Heightmap.Biome.Meadows,
            Heightmap.Biome.BlackForest,
            Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains,
            Heightmap.Biome.Mistlands,
            Heightmap.Biome.AshLands,
            Heightmap.Biome.DeepNorth,
            Heightmap.Biome.Ocean
        };

        #endregion

        #region Serialized Settings

        [Header("Box Settings")]
        public Vector3 BoxSize = new Vector3(10f, 5f, 10f);
        public Vector3 MinSize = new Vector3(2f, 2f, 2f);
        public Vector3 MaxSize = new Vector3(200f, 100f, 200f);

        [Header("Lock State")]
        // When true, all configuration cycles (E + modifiers, Shift+H visibility,
        // Shift+Scroll scaling) are blocked even for admins. Toggle with Ctrl+L
        // while hovering. Default false so existing boxes stay editable on
        // upgrade; admins explicitly lock once configured.
        public bool Locked = false;

        [Header("Environment")]
        public string EnvironmentName = "";  // Empty = no override
        public bool ForceEnvironment = true;  // If true, forces env regardless of time/weather

        [Header("Biome Override")]
        public Heightmap.Biome ForcedBiome = Heightmap.Biome.None;
        public bool AffectsTerrainPaint = true;  // Affects our custom terrain paint
        public bool AffectsVanillaPaint = true;  // Affects vanilla heightmap paint queries (default true for biome visual override)

        [Header("Skybox")]
        public SkyboxMode CurrentSkyboxMode = SkyboxMode.Inherit;
        public Color AmbientColorOverride = new Color(0.1f, 0.1f, 0.15f);

        [Header("Visibility")]
        public VisibilityMode CurrentVisibility = VisibilityMode.Full;

        [Header("Wind")]
        public WindIntensity CurrentWindIntensity = WindIntensity.Inherit;

        [Header("Time of Day")]
        public ForcedTimeOfDay CurrentTimeOfDay = ForcedTimeOfDay.Inherit;

        #endregion

        #region Enums

        public enum SkyboxMode
        {
            Inherit,        // Use world skybox
            DungeonBlack,   // Pure black void (like dungeon interiors)
            DarkCave,       // Very dark with slight ambient
            Custom          // Custom ambient color
        }

        public enum VisibilityMode
        {
            Full,           // Semi-transparent box visible
            EdgesOnly,      // Only wireframe edges
            Hidden          // Invisible, spawns marker sign at top
        }

        public enum WindIntensity
        {
            Inherit,        // Use world wind (no override)
            None,           // No wind (0.0)
            Light,          // Light breeze (0.1)
            Slight,         // Slight wind (0.2)
            Moderate,       // Moderate wind (0.4)
            Strong,         // Strong wind (0.6)
            Heavy           // Heavy wind (0.8)
        }

        /// <summary>
        /// Forced time of day options.
        /// These correspond to day fractions (0-1) where:
        /// - 0.0 = Midnight
        /// - 0.25 = Morning/Dawn
        /// - 0.5 = Midday/Noon
        /// - 0.75 = Evening/Dusk
        /// </summary>
        public enum ForcedTimeOfDay
        {
            Inherit,        // Use world time (no override)
            Midnight,       // 0.0 - Deep night
            Dawn,           // 0.21 - Early morning, sun rising
            Morning,        // 0.30 - Mid morning
            Midday,         // 0.50 - High noon, brightest
            Afternoon,      // 0.65 - Late afternoon
            Dusk,           // 0.79 - Evening, sun setting
            Night           // 0.875 - Night time
        }

        #endregion

        #region Runtime State

        private ZNetView m_nview;
        private BoxCollider m_trigger;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private GameObject m_hiddenMarker;
        private bool m_playerInside = false;

        // Original environment state for restoration
        private Material m_originalSkybox;
        private AmbientMode m_originalAmbientMode;
        private Color m_originalAmbientColor;

        // ZDO hash codes
        private static readonly int HASH_SIZE_X = "envbox_sx".GetStableHashCode();
        private static readonly int HASH_SIZE_Y = "envbox_sy".GetStableHashCode();
        private static readonly int HASH_SIZE_Z = "envbox_sz".GetStableHashCode();
        private static readonly int HASH_ENV = "envbox_env".GetStableHashCode();
        private static readonly int HASH_FORCE_ENV = "envbox_fenv".GetStableHashCode();
        private static readonly int HASH_BIOME = "envbox_biome".GetStableHashCode();
        private static readonly int HASH_SKYBOX = "envbox_sky".GetStableHashCode();
        private static readonly int HASH_VISIBILITY = "envbox_vis".GetStableHashCode();
        private static readonly int HASH_AMB_R = "envbox_ar".GetStableHashCode();
        private static readonly int HASH_AMB_G = "envbox_ag".GetStableHashCode();
        private static readonly int HASH_AMB_B = "envbox_ab".GetStableHashCode();
        private static readonly int HASH_WIND = "envbox_wind".GetStableHashCode();
        private static readonly int HASH_TIME = "envbox_time".GetStableHashCode();
        private static readonly int HASH_AFFECTS_VANILLA = "envbox_avp".GetStableHashCode();
        private static readonly int HASH_LOCKED = "envbox_locked".GetStableHashCode();

        // Original wind state for restoration
        private float m_originalWindMin;
        private float m_originalWindMax;
        private bool m_windOverrideActive = false;

        // Time override state
        private bool m_timeOverrideActive = false;

        // Enclosure mesh for blocking outside light/sky
        private GameObject m_enclosureMesh;
        private MeshFilter m_enclosureMeshFilter;
        private MeshRenderer m_enclosureMeshRenderer;

        // Original fog state for restoration
        private bool m_originalFog;
        private Color m_originalFogColor;
        private float m_originalFogDensity;

        // Snow shader state
        private bool m_snowShaderActive = false;
        private float? m_origSnowAmount;

        // Original box material, stored on skybox-override entry (original RPGMaker behaviour).
        private Material m_originalBoxMaterial;

        // The real vanilla dungeon environment whose EnvSetup (dark ambient + low directional intensity, no fill
        // light) defines the crypt look. Forcing this through EnvMan makes SetEnv apply the genuine vanilla
        // ambient/sun/fog numbers every frame — the exact mechanism EnvZone uses inside real crypts.
        private const string VANILLA_DUNGEON_ENV = "Crypt";

        // The forced-environment string we pushed onto EnvMan for the dark interior (so exit can clear it
        // without stomping an explicit per-box EnvironmentName the admin also configured).
        private bool m_dungeonEnvForced = false;

        // Throttle for the ~1/sec [ENVBOX-DBG] inside-position diagnostic (see LogInsideDiagnostics).
        private float m_dbgNextInsideLogTime = 0f;

        // True only for skybox modes that fully enclose the player and must read as a dim lit interior
        // (not surface-lit). DungeonBlack/DarkCave qualify; Inherit/Custom do not drive the environment.
        private bool IsDarkInteriorMode =>
            CurrentSkyboxMode == SkyboxMode.DungeonBlack || CurrentSkyboxMode == SkyboxMode.DarkCave;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            m_nview = GetComponent<ZNetView>();

            // Setup trigger collider
            m_trigger = GetComponent<BoxCollider>();
            if (m_trigger == null)
            {
                m_trigger = gameObject.AddComponent<BoxCollider>();
            }
            m_trigger.isTrigger = true;
            m_trigger.size = BoxSize;

            // Setup mesh components
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();

            if (m_meshFilter == null)
            {
                m_meshFilter = gameObject.AddComponent<MeshFilter>();
            }
            if (m_meshRenderer == null)
            {
                m_meshRenderer = gameObject.AddComponent<MeshRenderer>();
            }

            // Register
            _allBoxes.Add(this);
            EnvironmentBoxContextMenu.EnsureRegistered();   // Shift+Alt+RMB config menu (client-only, lazy, once)

            // Ensure the scaler singleton exists for Shift+H and Shift+Scroll keybinds
            // (hover scroll-scaling removed — scaling is the panel's 0–1000% slider now; no EnvironmentBoxScaler.)

            // Load from ZDO
            if (m_nview != null && m_nview.GetZDO() != null)
            {
                LoadFromZDO();
            }

            // Apply initial state
            UpdateBoxMesh();
            ApplyVisibility();
        }


        private void OnDestroy()
        {
            _allBoxes.Remove(this);

            // Clean up if player was inside
            if (_playerInsideBox == this)
            {
                OnPlayerExit();
                _playerInsideBox = null;
            }

            // Clean up marker
            if (m_hiddenMarker != null)
            {
                Destroy(m_hiddenMarker);
            }

            // Clean up enclosure mesh
            if (m_enclosureMesh != null)
            {
                Destroy(m_enclosureMesh);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                OnPlayerEnter();
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                OnPlayerExit();
            }
        }

        private void OnTriggerStay(Collider other)
        {
            // Maintain state while player is inside
            var player = other.GetComponent<Player>();
            if (player != null && player == Player.m_localPlayer)
            {
                if (!m_playerInside)
                {
                    OnPlayerEnter();
                }

                // Keep forcing environment while inside
                if (ForceEnvironment && !string.IsNullOrEmpty(EnvironmentName))
                {
                    EnvMan.instance?.SetForceEnvironment(EnvironmentName);
                }
            }
        }

        private void LateUpdate()
        {
            // Keep hidden marker at top of box (handles external scaling like Structure Tweaks)
            if (m_hiddenMarker != null && CurrentVisibility == VisibilityMode.Hidden)
            {
                UpdateHiddenMarkerPosition();
            }

            // A TELEPORT into the box (our dungeon case: you arrive at y+5000) does NOT fire OnTriggerEnter/OnTriggerStay
            // reliably, so poll the local player's position here — ENTER when inside; ValidatePlayerInside handles exit.
            // This is what makes SetForceEnvironment actually run after teleporting in (the darkness never applied before).
            var lp = Player.m_localPlayer;
            if (lp != null && !m_playerInside && ContainsPoint(lp.transform.position))
                OnPlayerEnter();
            ValidatePlayerInside();

            // Re-assert the dark interior AFTER EnvMan's Update has rewritten the sun + RenderSettings this
            // frame. Without this the surface sun floods straight back in next frame.
            if (m_playerInside && _playerInsideBox == this)
            {
                ReassertDarkInterior();
                LogInsideDiagnostics();
            }
        }

        /// <summary>
        /// Throttled (~1/sec) diagnostic while the local player is inside THIS box. Logs the player's world
        /// position, whether it is actually inside the enclosure WORLD bounds, and the signed distance to each of
        /// the 6 walls (+ = inside the wall, - = past it). A negative distance to any wall means the play area
        /// spills OUT of the box on that side — the exact condition that lets the sky/mausoleum show through.
        /// CLIENT-LOCAL, cheap, gated to dark-interior modes so it doesn't spam for atmosphere-only boxes.
        /// </summary>
        private void LogInsideDiagnostics()
        {
            if (IsClientOnly()) return;
            if (Time.unscaledTime < m_dbgNextInsideLogTime) return;
            m_dbgNextInsideLogTime = Time.unscaledTime + 1f;

            var player = Player.m_localPlayer;
            if (player == null) return;

            Vector3 p = player.transform.position;
            Bounds wb = GetWorldBounds();
            bool inside = ContainsPoint(p);

            // Signed distance from the player to each wall plane (world-axis-aligned box; env boxes are unrotated).
            float dMinusX = p.x - wb.min.x; // + when right of the -X wall (inside)
            float dPlusX = wb.max.x - p.x; // + when left  of the +X wall (inside)
            float dMinusY = p.y - wb.min.y; // + when above  the floor
            float dPlusY = wb.max.y - p.y; // + when below  the ceiling
            float dMinusZ = p.z - wb.min.z;
            float dPlusZ = wb.max.z - p.z;

            Debug.Log($"[ENVBOX-DBG] inside-check: box='{gameObject.name}' size={BoxSize} playerPos={p} " +
                      $"INSIDE={inside} worldMin={wb.min} worldMax={wb.max} " +
                      $"distWalls[-X={dMinusX:F1} +X={dPlusX:F1} -Y={dMinusY:F1} +Y={dPlusY:F1} -Z={dMinusZ:F1} +Z={dPlusZ:F1}]");
        }

        /// <summary>
        /// While the local player is inside a dark-interior box, keep the dark dungeon environment forced and
        /// re-pin the suppressed reflection. ALL lighting (sun intensity/color, ambient, fog) is now produced the
        /// vanilla way — by EnvMan.SetEnv applying the forced "Crypt" EnvSetup each frame — so we do NOT touch
        /// the sun or ambient/fog here; that would fight the environment and diverge from vanilla. We only
        /// re-pin reflection to black, because EnvMan re-drives reflection each frame and our single-sided
        /// enclosure mesh cannot block the reflection probe's outward capture of the world sky. CLIENT-LOCAL.
        /// </summary>
        private void ReassertDarkInterior()
        {
            if (IsClientOnly()) return;        // dedi: no environment to drive
            if (!IsDarkInteriorMode) return;   // Inherit / Custom keep the world environment

            // VANILLA-EXACT: a dark interior is JUST a forced dark ENVIRONMENT. Vanilla EnvZone.OnTriggerStay calls
            // EnvMan.SetForceEnvironment(m_environment) every frame the player is inside and does nothing else — the
            // env's own EnvSetup (dark ambient, low/no sun, dense dark fog, its skybox) produces the whole look via
            // EnvMan.SetEnv. We re-force it each frame for the same reason vanilla does (EnvMan re-selects the env
            // every frame; another zone can clear the force). We do NOT touch the sun, ambient, reflection, or skybox
            // RenderSettings — that was the improvisation that fought EnvMan and caused the lit surfaces / sky leaks.
            if (EnvMan.instance != null)
            {
                string env = !string.IsNullOrEmpty(EnvironmentName) ? EnvironmentName : VANILLA_DUNGEON_ENV;
                EnvMan.instance.SetForceEnvironment(env);
            }
        }

        /// <summary>
        /// Validates that the player marked as "inside" is actually still inside the box.
        /// This catches cases where the player teleported or flew out without triggering OnTriggerExit.
        /// </summary>
        private void ValidatePlayerInside()
        {
            // Only check if we think the player is inside THIS box
            if (!m_playerInside) return;
            if (_playerInsideBox != this) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                // Player doesn't exist anymore, clean up
                OnPlayerExit();
                return;
            }

            // Check if player is ACTUALLY inside the box bounds
            if (!ContainsPoint(player.transform.position))
            {
                Debug.Log($"[EnvironmentBox] Player no longer inside box (teleported/flew out?), triggering exit");
                OnPlayerExit();
            }
        }

        #endregion

        #region Player Enter/Exit

        private void OnPlayerEnter()
        {
            if (m_playerInside) return;
            m_playerInside = true;
            _playerInsideBox = this;

            Debug.Log($"[EnvironmentBox] Player entered box: {gameObject.name}");
            Debug.Log($"[EnvironmentBox] ForcedBiome={ForcedBiome}, AffectsVanillaPaint={AffectsVanillaPaint}");

            // Notify the terrain provider (FAT) that this box's biome override is active. No-op without a
            // provider — a Core-only dark-crypt box (ForcedBiome=None) needs no terrain work at all.
            if (ForcedBiome != Heightmap.Biome.None)
                EnvironmentBoxBridge.OnBoxEnter(this, ForcedBiome, AffectsTerrainPaint, AffectsVanillaPaint);

            // Apply environment
            if (!string.IsNullOrEmpty(EnvironmentName) && EnvMan.instance != null)
            {
                if (ForceEnvironment)
                {
                    EnvMan.instance.SetForceEnvironment(EnvironmentName);
                }
            }

            // Apply skybox override
            ApplySkyboxOverride();

            // Apply wind override
            ApplyWindOverride();

            // Apply time of day override
            ApplyTimeOverride();

            // Apply biome shader effects (snow for Mountain/DeepNorth)
            ApplyBiomeShaders();

            // (the provider's OnBoxEnter above performs the terrain + clutter refresh.)

            // Apply snow to building pieces (Mountain/DeepNorth)
            ApplySnowToBuildingPieces();
        }

        private void OnPlayerExit()
        {
            if (!m_playerInside) return;
            m_playerInside = false;

            if (_playerInsideBox == this)
            {
                _playerInsideBox = null;
            }

            Debug.Log($"[EnvironmentBox] Player exited box: {gameObject.name}");

            // Clear forced environment
            if (ForceEnvironment && EnvMan.instance != null)
            {
                EnvMan.instance.SetForceEnvironment("");
            }

            // Restore skybox
            RestoreSkybox();

            // Restore wind
            RestoreWind();

            // Restore time of day
            RestoreTime();

            // Remove snow from building pieces (before restoring shaders)
            RemoveSnowFromBuildingPieces();

            // Restore biome shader effects
            RestoreBiomeShaders();

            // Terrain teardown via the provider (FAT). No-op without a provider.
            if (ForcedBiome != Heightmap.Biome.None)
                EnvironmentBoxBridge.OnBoxExit(this);
        }

        // ORIGINAL RPGMaker behaviour restored (this body was gutted once and it broke the placeable boxes): store the
        // RenderSettings originals, then per-mode build the full enclosure AND set the dark ambient/fog. The one KEPT
        // addition for dungeon use: dark modes also force the real vanilla dungeon ENVIRONMENT (ApplyDungeonEnvironment)
        // so EnvMan's own SetEnv keeps producing authentic crypt lighting every frame — vanilla EnvZone's mechanism.
        private void ApplySkyboxOverride()
        {
            if (IsClientOnly()) return; // dedi: no meshes/environment
            if (CurrentSkyboxMode == SkyboxMode.Inherit)
            {
                // Destroy enclosure if switching back to inherit
                DestroyEnclosureMesh();
                return;
            }

            // Store original settings
            m_originalSkybox = RenderSettings.skybox;
            m_originalAmbientMode = RenderSettings.ambientMode;
            m_originalAmbientColor = RenderSettings.ambientLight;
            m_originalFog = RenderSettings.fog;
            m_originalFogColor = RenderSettings.fogColor;
            m_originalFogDensity = RenderSettings.fogDensity;

            // Store original box material before changing it
            if (m_meshRenderer != null) m_originalBoxMaterial = m_meshRenderer.material;

            switch (CurrentSkyboxMode)
            {
                case SkyboxMode.DungeonBlack:
                    // FULL 6-sided enclosure - blocks ALL light/sky like vanilla dungeons
                    ApplyDungeonEnvironment();
                    CreateEnclosureMesh(true, Color.black);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.01f, 0.01f, 0.015f); // Very dark ambient
                    RenderSettings.fog = true;
                    RenderSettings.fogColor = Color.black;
                    RenderSettings.fogDensity = 0.2f; // Dense fog
                    break;

                case SkyboxMode.DarkCave:
                    // Same structure as DungeonBlack, slightly brighter ambient
                    ApplyDungeonEnvironment();
                    CreateEnclosureMesh(true, new Color(0.005f, 0.005f, 0.01f));
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.05f);
                    RenderSettings.fog = true;
                    RenderSettings.fogColor = new Color(0.01f, 0.01f, 0.015f);
                    RenderSettings.fogDensity = 0.12f;
                    break;

                case SkyboxMode.Custom:
                    // Full enclosure with custom ambient
                    CreateEnclosureMesh(true, AmbientColorOverride * 0.1f);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = AmbientColorOverride;
                    break;
            }

            Debug.Log($"[ENVBOX-DBG] ApplySkyboxOverride: box='{gameObject.name}' mode={CurrentSkyboxMode} " +
                      $"enclosure={(m_enclosureMesh != null ? "built" : "NULL")} dungeonEnvForced={m_dungeonEnvForced}.");
        }

        /// <summary>
        /// Forces the real vanilla dungeon environment (<see cref="VANILLA_DUNGEON_ENV"/>) through EnvMan so its
        /// EnvSetup drives all interior lighting. We only force it ourselves when the box has no explicit
        /// EnvironmentName configured; if the admin set one, that forced environment already runs and we leave it.
        /// </summary>
        private void ApplyDungeonEnvironment()
        {
            if (IsClientOnly()) return; // dedi: no EnvMan environment to drive
            if (EnvMan.instance == null) return;

            // Don't override an explicit per-box environment the admin chose.
            if (!string.IsNullOrEmpty(EnvironmentName)) return;

            // "Crypt" is a guaranteed vanilla environment; SetForceEnvironment is a no-op for an unknown name.
            EnvMan.instance.SetForceEnvironment(VANILLA_DUNGEON_ENV);
            m_dungeonEnvForced = true;
        }

        private void RestoreSkybox()
        {
            if (IsClientOnly()) return; // dedi: no meshes/environment
            // Always destroy enclosure when exiting
            DestroyEnclosureMesh();

            // Clear the forced dark environment so EnvMan re-selects the world environment (vanilla
            // EnvZone.OnTriggerExit behaviour).
            if (m_dungeonEnvForced)
            {
                EnvMan.instance?.SetForceEnvironment("");
                m_dungeonEnvForced = false;
            }

            if (CurrentSkyboxMode == SkyboxMode.Inherit) return;

            // Restore original render settings (original RPGMaker behaviour)
            if (m_originalSkybox != null)
            {
                RenderSettings.skybox = m_originalSkybox;
            }
            RenderSettings.ambientMode = m_originalAmbientMode;
            RenderSettings.ambientLight = m_originalAmbientColor;
            RenderSettings.fog = m_originalFog;
            RenderSettings.fogColor = m_originalFogColor;
            RenderSettings.fogDensity = m_originalFogDensity;
        }

        /// <summary>
        /// Makes the main env box mesh solid and opaque to block light.
        /// This ensures no light leaks through the box boundaries. (Original RPGMaker helper, restored.)
        /// </summary>
        private void MakeBoxMeshSolid(Color color)
        {
            if (m_meshRenderer == null) return;

            // Create opaque solid material
            var solidMat = CreateSolidMaterial(color);
            m_meshRenderer.material = solidMat;
            m_meshRenderer.enabled = true;
        }

        /// <summary>
        /// Restores the original box mesh material. (Original RPGMaker helper, restored.)
        /// </summary>
        private void RestoreBoxMeshMaterial()
        {
            if (m_meshRenderer == null) return;

            // Restore original material and apply visibility settings
            ApplyVisibility();
        }

        // The original shader chain (Standard → Diffuse → Unlit/Color), hardened with Custom/Piece as the final
        // fallback — a vanilla shader guaranteed present at runtime — so the chain can never yield a null shader.
        private static Shader FindOpaqueShader()
        {
            return Shader.Find("Standard")
                ?? Shader.Find("Diffuse")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Custom/Piece");
        }

        /// <summary>
        /// Creates a solid opaque material for blocking light and skybox. (Original RPGMaker helper, restored.)
        /// Uses Standard shader in opaque mode with proper depth writing.
        /// </summary>
        private Material CreateSolidMaterial(Color color)
        {
            var shader = FindOpaqueShader();
            var mat = new Material(shader) { name = "EnvBoxSolid" };

            // Configure for opaque rendering (original body)
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0); // Opaque mode
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 1); // Enable depth writing
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry; // Render with opaque geometry

            // Set the color - use emission for unlit appearance
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            if (mat.HasProperty("_EmissionColor")) { mat.SetColor("_EmissionColor", color * 0.1f); mat.EnableKeyword("_EMISSION"); }

            // Make it not receive lighting (appear flat)
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            return mat;
        }

        /// <summary>
        /// Creates the enclosure mesh that visually blocks outside light and sky.
        /// </summary>
        /// <param name="includeTop">If true, creates a fully enclosed box. If false, leaves top open.</param>
        /// <param name="color">The color of the enclosure walls (usually near-black).</param>
        private void CreateEnclosureMesh(bool includeTop, Color color)
        {
            if (IsClientOnly()) return; // dedi: no enclosure mesh / material
            // Destroy existing enclosure first
            DestroyEnclosureMesh();

            m_enclosureMesh = new GameObject("EnvBox_Enclosure");
            m_enclosureMesh.transform.SetParent(transform);
            m_enclosureMesh.transform.localPosition = Vector3.zero;
            m_enclosureMesh.transform.localRotation = Quaternion.identity;
            m_enclosureMesh.transform.localScale = Vector3.one;

            m_enclosureMeshFilter = m_enclosureMesh.AddComponent<MeshFilter>();
            m_enclosureMeshRenderer = m_enclosureMesh.AddComponent<MeshRenderer>();

            // Create the enclosure mesh
            Mesh mesh = new Mesh();
            mesh.name = "EnvBoxEnclosure";

            Vector3 half = BoxSize / 2f;
            // Make enclosure slightly larger than box to avoid z-fighting
            half *= 1.01f;

            // Vertices for box faces (viewed from inside)
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            // Bottom face (floor)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(-half.x, -half.y, half.z));

            // Front face (-Z)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z));

            // Back face (+Z)
            AddQuadInward(vertices, triangles,
                new Vector3(half.x, -half.y, half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
                new Vector3(-half.x, -half.y, half.z));

            // Left face (-X)
            AddQuadInward(vertices, triangles,
                new Vector3(-half.x, -half.y, half.z),
                new Vector3(-half.x, half.y, half.z),
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(-half.x, -half.y, -half.z));

            // Right face (+X)
            AddQuadInward(vertices, triangles,
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(half.x, -half.y, half.z));

            // Top face (ceiling) - only if includeTop is true
            if (includeTop)
            {
                AddQuadInward(vertices, triangles,
                    new Vector3(-half.x, half.y, half.z),
                    new Vector3(half.x, half.y, half.z),
                    new Vector3(half.x, half.y, -half.z),
                    new Vector3(-half.x, half.y, -half.z));
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            m_enclosureMeshFilter.mesh = mesh;

            // Heavy diagnostics: BoxSize, top-cap flag, WORLD center + world min/max bounds, and geometry counts.
            // If the crypt/play area sits outside these world bounds, the sky WILL leak past a wall.
            Bounds wb = GetWorldBounds();
            Debug.Log($"[ENVBOX-DBG] CreateEnclosureMesh: box='{gameObject.name}' BoxSize={BoxSize} includeTop={includeTop} " +
                      $"localScale={transform.localScale} color={color} cull=Off(double-sided) " +
                      $"worldCenter={wb.center} worldMin={wb.min} worldMax={wb.max} " +
                      $"verts={vertices.Count} tris={triangles.Count / 3}");

            // Create material - opaque shader that blocks skybox
            m_enclosureMeshRenderer.material = CreateEnclosureMaterial(color);

            // Important: Don't cast/receive shadows for performance
            m_enclosureMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_enclosureMeshRenderer.receiveShadows = false;

            // Set layer - use Default layer so it renders properly and blocks skybox
            m_enclosureMesh.layer = 0; // Default layer
        }

        /// <summary>
        /// Adds a quad to the mesh with vertices wound so the face is visible from INSIDE the box.
        /// Uses REVERSED winding (clockwise when viewed from outside = counter-clockwise from inside).
        /// This means the quad will be visible from inside, invisible from outside due to backface culling.
        /// </summary>
        private void AddQuadInward(List<Vector3> vertices, List<int> triangles,
            Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
        {
            int startIndex = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);

            // Two triangles forming the quad
            // REVERSED winding order: clockwise when viewed from outside = front face from INSIDE
            // This makes the face visible from inside, invisible from outside
            triangles.Add(startIndex + 2);
            triangles.Add(startIndex + 1);
            triangles.Add(startIndex);

            triangles.Add(startIndex + 3);
            triangles.Add(startIndex + 2);
            triangles.Add(startIndex);
        }

        /// <summary>
        /// Creates an opaque material for the enclosure walls that blocks skybox/light. (Original body, restored.)
        /// Only renders inner faces (backface culling enabled) so the enclosure is invisible from outside —
        /// outside visibility is the BOX MESH's job (VisibilityMode), exactly like the original.
        /// </summary>
        private Material CreateEnclosureMaterial(Color color)
        {
            var shader = FindOpaqueShader();
            var mat = new Material(shader) { name = "EnvBoxEnclosure" };

            // Configure for opaque rendering - this is critical for blocking skybox
            if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0); // Opaque mode
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 1); // Enable depth writing - blocks everything behind
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 1; // Render before other geometry

            // Set color - use pure color with NO emission for true black
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            if (mat.HasProperty("_EmissionColor")) { mat.SetColor("_EmissionColor", Color.black); mat.DisableKeyword("_EMISSION"); }

            // Flat, unlit appearance
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            // IMPORTANT: Keep default backface culling (CullMode.Back)
            // This makes the mesh invisible from outside, only visible from inside
            // The triangle winding in AddQuadInward ensures inner faces are front-facing
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);

            return mat;
        }

        /// <summary>
        /// Destroys the enclosure mesh if it exists.
        /// </summary>
        private void DestroyEnclosureMesh()
        {
            if (m_enclosureMesh != null)
            {
                Destroy(m_enclosureMesh);
                m_enclosureMesh = null;
                m_enclosureMeshFilter = null;
                m_enclosureMeshRenderer = null;
            }
        }

        private void ApplyWindOverride()
        {
            if (IsClientOnly()) return; // dedi: no EnvMan environment to mutate
            if (CurrentWindIntensity == WindIntensity.Inherit) return;
            if (EnvMan.instance == null) return;

            // Store original wind values from current environment
            var currentEnv = EnvMan.instance.GetCurrentEnvironment();
            if (currentEnv != null && !m_windOverrideActive)
            {
                m_originalWindMin = currentEnv.m_windMin;
                m_originalWindMax = currentEnv.m_windMax;
                m_windOverrideActive = true;
            }

            // Get wind values for our intensity level
            float windValue = GetWindValue(CurrentWindIntensity);

            // Override the current environment's wind (this affects EnvMan's wind calculation)
            if (currentEnv != null)
            {
                currentEnv.m_windMin = windValue;
                currentEnv.m_windMax = windValue + 0.1f;
            }
        }

        private void RestoreWind()
        {
            if (IsClientOnly()) return; // dedi: no EnvMan environment to mutate
            if (!m_windOverrideActive) return;
            if (EnvMan.instance == null) return;

            var currentEnv = EnvMan.instance.GetCurrentEnvironment();
            if (currentEnv != null)
            {
                currentEnv.m_windMin = m_originalWindMin;
                currentEnv.m_windMax = m_originalWindMax;
            }
            m_windOverrideActive = false;
        }

        private static float GetWindValue(WindIntensity intensity)
        {
            switch (intensity)
            {
                case WindIntensity.None: return 0.0f;
                case WindIntensity.Light: return 0.1f;
                case WindIntensity.Slight: return 0.2f;
                case WindIntensity.Moderate: return 0.4f;
                case WindIntensity.Strong: return 0.6f;
                case WindIntensity.Heavy: return 0.8f;
                default: return 0.3f; // Default moderate
            }
        }

        /// <summary>
        /// Gets the wind intensity name for display.
        /// </summary>
        public static string GetWindIntensityName(WindIntensity intensity)
        {
            switch (intensity)
            {
                case WindIntensity.Inherit: return "World Default";
                case WindIntensity.None: return "No Wind";
                case WindIntensity.Light: return "Light Breeze";
                case WindIntensity.Slight: return "Slight Wind";
                case WindIntensity.Moderate: return "Moderate Wind";
                case WindIntensity.Strong: return "Strong Wind";
                case WindIntensity.Heavy: return "Heavy Wind";
                default: return intensity.ToString();
            }
        }

        #endregion

        #region Time of Day Override

        private void ApplyTimeOverride()
        {
            if (IsClientOnly()) return; // dedi: no EnvMan debug-time
            if (CurrentTimeOfDay == ForcedTimeOfDay.Inherit) return;
            if (EnvMan.instance == null) return;

            float dayFraction = GetDayFraction(CurrentTimeOfDay);

            // Use EnvMan's debug time feature to force time of day
            // This is the same approach used by devcommands
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = dayFraction;

            m_timeOverrideActive = true;
            Debug.Log($"[EnvironmentBox] Applied time override: {CurrentTimeOfDay} (fraction: {dayFraction:F2})");
        }

        private void RestoreTime()
        {
            if (IsClientOnly()) return; // dedi: no EnvMan debug-time
            if (!m_timeOverrideActive) return;
            if (EnvMan.instance == null) return;

            // Disable debug time override
            EnvMan.instance.m_debugTimeOfDay = false;

            m_timeOverrideActive = false;
            Debug.Log("[EnvironmentBox] Restored world time");
        }

        /// <summary>
        /// Gets the day fraction (0-1) for a time of day preset.
        /// </summary>
        private static float GetDayFraction(ForcedTimeOfDay timeOfDay)
        {
            switch (timeOfDay)
            {
                case ForcedTimeOfDay.Midnight: return 0.0f;
                case ForcedTimeOfDay.Dawn: return 0.21f;  // Just before sunrise
                case ForcedTimeOfDay.Morning: return 0.30f;  // Mid-morning
                case ForcedTimeOfDay.Midday: return 0.50f;  // High noon
                case ForcedTimeOfDay.Afternoon: return 0.65f;  // Late afternoon
                case ForcedTimeOfDay.Dusk: return 0.79f;  // Just before sunset
                case ForcedTimeOfDay.Night: return 0.875f; // Night time
                default: return 0.5f;
            }
        }

        /// <summary>
        /// Gets the display name for a time of day preset.
        /// </summary>
        public static string GetTimeOfDayName(ForcedTimeOfDay timeOfDay)
        {
            switch (timeOfDay)
            {
                case ForcedTimeOfDay.Inherit: return "World Default";
                case ForcedTimeOfDay.Midnight: return "Midnight (00:00)";
                case ForcedTimeOfDay.Dawn: return "Dawn (05:00)";
                case ForcedTimeOfDay.Morning: return "Morning (07:00)";
                case ForcedTimeOfDay.Midday: return "Midday (12:00)";
                case ForcedTimeOfDay.Afternoon: return "Afternoon (15:30)";
                case ForcedTimeOfDay.Dusk: return "Dusk (19:00)";
                case ForcedTimeOfDay.Night: return "Night (21:00)";
                default: return timeOfDay.ToString();
            }
        }

        #endregion

        #region Biome Shaders

        /// <summary>
        /// Applies shader effects for the forced biome (e.g., snow for Mountain/DeepNorth).
        /// Based on TerritoryRuntime.Environment.cs pattern.
        /// </summary>
        private void ApplyBiomeShaders()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // Check if this biome requires snow shader
            if (BiomeRequiresSnowShader(ForcedBiome))
            {
                // Store original snow amount if not already stored
                if (!m_origSnowAmount.HasValue)
                {
                    try { m_origSnowAmount = Shader.GetGlobalFloat("_GlobalSnowAmount"); }
                    catch { m_origSnowAmount = 0f; }
                }

                // Apply snow shader globals - same as TerritoryRuntime
                Shader.SetGlobalFloat("_GlobalSnowAmount", 1f);
                Shader.SetGlobalFloat("_SnowAmount", 1f);
                Shader.SetGlobalFloat("_AddSnow", 1f);

                m_snowShaderActive = true;
                Debug.Log($"[EnvironmentBox] Applied snow shaders for biome: {ForcedBiome}");
            }

            // Note: SetHasBiomeOverrideBoxes is now called in OnPlayerEnter before this method
        }

        /// <summary>
        /// Restores shader effects to their original state.
        /// </summary>
        private void RestoreBiomeShaders()
        {
            if (m_snowShaderActive)
            {
                Shader.SetGlobalFloat("_GlobalSnowAmount", m_origSnowAmount ?? 0f);
                Shader.SetGlobalFloat("_SnowAmount", m_origSnowAmount ?? 0f);
                Shader.SetGlobalFloat("_AddSnow", 0f);

                m_snowShaderActive = false;
                Debug.Log("[EnvironmentBox] Restored original snow shader values");
            }

            // (the biome-override flag for FAT's paint patches is cleared by the provider OnBoxExit.)
        }

        /// <summary>
        /// Checks if a biome requires snow shader effects.
        /// </summary>
        private static bool BiomeRequiresSnowShader(Heightmap.Biome biome)
        {
            return biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain;
        }

        #endregion

        #region Terrain Visual Override

        /// <summary>
        /// Refreshes vanilla terrain paint within and around this box.
        /// Forces heightmaps to regenerate, which triggers our RebuildRenderMesh patch.
        /// Does NOT affect custom DirtFloor terrain.
        /// </summary>
        private void RefreshTerrainInBounds()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // Get world bounds of this box
            Bounds boxBounds = GetWorldBounds();

            // Expand bounds slightly to catch nearby heightmaps
            boxBounds.Expand(10f);

            // Route to the terrain provider (FAT). No-op without a provider.
            EnvironmentBoxBridge.RefreshTerrainInBounds(boxBounds);
        }

        /// <summary>
        /// Refreshes vegetation/clutter (grass) to match the biome override.
        /// Uses our custom clutter spawner to bypass vanilla altitude restrictions.
        /// </summary>
        private void RefreshClutterInBounds()
        {
            if (ForcedBiome == Heightmap.Biome.None) return;

            // Route to the terrain provider (FAT): clears vanilla clutter + spawns biome clutter. No-op without
            // a provider.
            Bounds boxBounds = GetWorldBounds();
            EnvironmentBoxBridge.RefreshClutterInBounds(boxBounds);
        }

        /// <summary>
        /// Restores terrain to original biome appearance when exiting.
        /// </summary>
        private void RestoreTerrainInBounds()
        {
            // Route to the terrain provider (FAT): clears spawned clutter, restores modified heightmaps, and
            // refreshes vanilla clutter/vegetation. No-op without a provider.
            EnvironmentBoxBridge.RestoreTerrainInBounds(this);
        }

        /// <summary>
        /// Gets the world-space bounds of this env box, accounting for transform scale.
        /// </summary>
        public Bounds GetWorldBounds()
        {
            Vector3 scale = transform.localScale;
            Vector3 scaledSize = new Vector3(
                BoxSize.x * scale.x,
                BoxSize.y * scale.y,
                BoxSize.z * scale.z
            );
            return new Bounds(transform.position, scaledSize);
        }

        #endregion

        #region Building Piece Snow

        /// <summary>
        /// Applies snow tinting to building pieces within this box (for Mountain/DeepNorth biomes).
        /// Routes through the terrain provider (FAT) which only affects exposed pieces (no roof coverage).
        /// </summary>
        private void ApplySnowToBuildingPieces()
        {
            if (!BiomeRequiresSnowShader(ForcedBiome)) return;

            try
            {
                // Use our instance ID as the "territory ID" for tracking
                int boxId = GetInstanceID();

                // Get box world bounds
                Bounds bounds = GetWorldBounds();
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;

                // Apply snow to exposed pieces within our bounds via the provider (FAT). No-op without one.
                EnvironmentBoxBridge.ApplySnowToPieces(boxId, new Bounds((min + max) * 0.5f, max - min));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBox] Failed to apply snow to building pieces: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes snow tinting from building pieces when exiting.
        /// </summary>
        private void RemoveSnowFromBuildingPieces()
        {
            try
            {
                int boxId = GetInstanceID();
                EnvironmentBoxBridge.RemoveSnowFromPieces(boxId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EnvironmentBox] Failed to remove snow from building pieces: {ex.Message}");
            }
        }

        #endregion

        #region Scaling

        /// <summary>
        /// Scales the box uniformly.
        /// </summary>
        public void ScaleUniform(float delta)
        {
            Vector3 newSize = BoxSize + Vector3.one * delta;
            SetSize(ClampSize(newSize));
        }

        /// <summary>
        /// Scales only the height (Y axis).
        /// </summary>
        public void ScaleHeight(float delta)
        {
            Vector3 newSize = BoxSize;
            newSize.y += delta;
            SetSize(ClampSize(newSize));
        }

        /// <summary>
        /// Scales width and depth (X and Z axes).
        /// </summary>
        public void ScaleHorizontal(float delta)
        {
            Vector3 newSize = BoxSize;
            newSize.x += delta;
            newSize.z += delta;
            SetSize(ClampSize(newSize));
        }

        private Vector3 ClampSize(Vector3 size)
        {
            return new Vector3(
                Mathf.Clamp(size.x, MinSize.x, MaxSize.x),
                Mathf.Clamp(size.y, MinSize.y, MaxSize.y),
                Mathf.Clamp(size.z, MinSize.z, MaxSize.z)
            );
        }

        public void SetSize(Vector3 size)
        {
            BoxSize = size;
            m_trigger.size = size;
            UpdateBoxMesh();
            UpdateHiddenMarkerPosition();

            // Update enclosure mesh if it exists. All enclosing modes (dark interior + custom) get a full
            // top cap so no real sky leaks onto upward-facing surfaces through an open ceiling.
            if (m_enclosureMesh != null && m_playerInside)
            {
                bool includeTop = IsDarkInteriorMode || CurrentSkyboxMode == SkyboxMode.Custom;
                Color color = CurrentSkyboxMode == SkyboxMode.Custom
                    ? AmbientColorOverride * 0.2f
                    : (CurrentSkyboxMode == SkyboxMode.DungeonBlack
                        ? Color.black
                        : new Color(0.005f, 0.005f, 0.01f));
                CreateEnclosureMesh(includeTop, color);
            }

            SaveToZDO();
        }

        /// <summary>
        /// Re-CENTER and RESIZE the box so it fully encompasses a target world-space AABB (e.g. all placed dungeon
        /// rooms). Moves the box's world position to <paramref name="worldCenter"/> and sets <see cref="BoxSize"/>
        /// to <paramref name="size"/> (clamped to Min/Max), then rebuilds trigger + box mesh + enclosure so the
        /// solid wall, enclosure mesh, and trigger all match the new extents. Server-authoritative: the owner moves
        /// the ZNetView (position replicates via the ZDO) and persists BoxSize to the ZDO; remote clients pick up
        /// the new position from the ZNetView and the new size from LoadFromZDO. Used by the dungeon engine after
        /// generation to grow the fixed spawn box to the real room bounds — the fixed size was the light leak.
        /// </summary>
        public void ReconfigureBoxToBounds(Vector3 worldCenter, Vector3 size)
        {
            // Move the box (owner drives ZNetView position; it replicates to clients).
            transform.position = worldCenter;

            // Resize everything (trigger + box mesh + enclosure) and persist BoxSize to the ZDO. Dungeon boxes
            // must wrap the whole dungeon, so this path is NOT capped by MaxSize (the manual-scaling limit) — it
            // clamps only to MinSize. The caller (dungeon engine) already floors the size to the vanilla minimum.
            SetSize(Vector3.Max(size, MinSize));

            // SetSize already rebuilds the enclosure shell at the new extents when the player is inside. No opaque
            // main-mesh shell to rebuild anymore (removed — the enclosure shell + forced env are the whole interior).

            Bounds wb = GetWorldBounds();
            Debug.Log($"[ENVBOX-DBG] ReconfigureBoxToBounds: box='{gameObject.name}' newCenter={worldCenter} " +
                      $"requestedSize={size} appliedBoxSize={BoxSize} worldMin={wb.min} worldMax={wb.max}.");
        }

        /// <summary>
        /// Persist the currently-set public fields to the ZDO and apply the runtime state (trigger size, box
        /// mesh, visibility). Used by <see cref="FiresCore.Bridge.EnvironmentBoxBridge.SpawnBox"/> right after it
        /// configures a freshly-created box, since the box's Awake already ran (reading defaults) before the
        /// caller set the fields. Server-authoritative: only the ZDO owner writes.
        /// </summary>
        public void ApplyConfiguredState()
        {
            if (m_trigger != null) m_trigger.size = BoxSize;
            if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner())
                SaveToZDO();
            UpdateBoxMesh();
            ApplyVisibility();
        }

        #endregion

        #region Cycling Options

        // Direct setters — the EnvironmentBoxPanel dropdowns pick exact values; each runs the SAME apply-if-inside +
        // SaveToZDO + message path the hover-key cycles always did. The Cycle* methods delegate to these.

        public void SetEnvironment(string name)
        {
            EnvironmentName = name ?? "";

            // Apply immediately if player is inside
            if (m_playerInside)
                EnvMan.instance?.SetForceEnvironment(string.IsNullOrEmpty(EnvironmentName) ? "" : EnvironmentName);

            SaveToZDO();
            ShowMessage($"Environment: {(string.IsNullOrEmpty(EnvironmentName) ? "None (World Default)" : EnvironmentName)}");
        }

        public void CycleEnvironment()
        {
            int currentIndex = Array.IndexOf(EnvironmentNames, EnvironmentName);
            SetEnvironment(EnvironmentNames[(currentIndex + 1) % EnvironmentNames.Length]);
        }

        public void SetBiome(Heightmap.Biome biome)
        {
            ForcedBiome = biome;

            // Update biome shaders and terrain if player is inside
            if (m_playerInside)
            {
                RestoreBiomeShaders();

                // Re-arm / disarm the terrain provider for the new biome.
                if (ForcedBiome != Heightmap.Biome.None)
                {
                    ApplyBiomeShaders();
                    EnvironmentBoxBridge.OnBoxEnter(this, ForcedBiome, AffectsTerrainPaint, AffectsVanillaPaint);
                }
                else
                {
                    EnvironmentBoxBridge.OnBoxExit(this);
                }
            }

            SaveToZDO();
            ShowMessage($"Biome Override: {(ForcedBiome == Heightmap.Biome.None ? "None" : ForcedBiome.ToString())}");
        }

        public void CycleBiome()
        {
            int currentIndex = Array.IndexOf(BiomeOptions, ForcedBiome);
            SetBiome(BiomeOptions[(currentIndex + 1) % BiomeOptions.Length]);
        }

        public void SetSkyboxMode(SkyboxMode mode)
        {
            CurrentSkyboxMode = mode;

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreSkybox();
                ApplySkyboxOverride();
            }

            SaveToZDO();
            ShowMessage($"Skybox: {CurrentSkyboxMode}");
        }

        public void CycleSkyboxMode()
            => SetSkyboxMode((SkyboxMode)(((int)CurrentSkyboxMode + 1) % Enum.GetValues(typeof(SkyboxMode)).Length));

        public void SetVisibility(VisibilityMode mode)
        {
            CurrentVisibility = mode;
            ApplyVisibility();
            SaveToZDO();
            ShowMessage($"Visibility: {CurrentVisibility}");
        }

        public void CycleVisibility()
            => SetVisibility((VisibilityMode)(((int)CurrentVisibility + 1) % Enum.GetValues(typeof(VisibilityMode)).Length));

        public void SetWindIntensity(WindIntensity intensity)
        {
            CurrentWindIntensity = intensity;

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreWind();
                ApplyWindOverride();
            }

            SaveToZDO();
            ShowMessage($"Wind: {GetWindIntensityName(CurrentWindIntensity)}");
        }

        public void CycleWindIntensity()
            => SetWindIntensity((WindIntensity)(((int)CurrentWindIntensity + 1) % Enum.GetValues(typeof(WindIntensity)).Length));

        public void SetTimeOfDay(ForcedTimeOfDay time)
        {
            CurrentTimeOfDay = time;

            // Apply immediately if inside
            if (m_playerInside)
            {
                RestoreTime();
                ApplyTimeOverride();
            }

            SaveToZDO();
            ShowMessage($"Time: {GetTimeOfDayName(CurrentTimeOfDay)}");
        }

        public void CycleTimeOfDay()
            => SetTimeOfDay((ForcedTimeOfDay)(((int)CurrentTimeOfDay + 1) % Enum.GetValues(typeof(ForcedTimeOfDay)).Length));

        /// <summary>
        /// Toggle the lock state. While locked, all configuration cycles and
        /// scaling are blocked — this guards against accidental E/scroll
        /// presses on a finished box. Saves to ZDO immediately.
        /// </summary>
        public void ToggleLock()
        {
            Locked = !Locked;
            SaveToZDO();
            ShowMessage(Locked
                ? "<color=#ff8888>Environment Box: LOCKED</color>"
                : "<color=#88ff88>Environment Box: UNLOCKED</color>");
        }

        private void ShowMessage(string msg)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, msg);
        }

        #endregion

        #region Visibility

        private void ApplyVisibility()
        {
            if (IsClientOnly()) return; // dedi: no renderer/material visibility
            switch (CurrentVisibility)
            {
                case VisibilityMode.Full:
                    m_meshRenderer.enabled = true;
                    UpdateBoxMesh();   // mode-aware: restores the solid box after EdgesOnly
                    SetMaterial(CreateBoxMaterial(new Color(0.2f, 0.5f, 0.8f, 0.15f)));
                    DestroyHiddenMarker();
                    break;

                case VisibilityMode.EdgesOnly:
                    m_meshRenderer.enabled = true;
                    UpdateBoxMesh();   // mode-aware: builds the 12-bar edge mesh
                    SetMaterial(CreateWireframeMaterial());
                    DestroyHiddenMarker();
                    break;

                case VisibilityMode.Hidden:
                    m_meshRenderer.enabled = false;
                    SpawnHiddenMarker();
                    break;
            }
        }

        private void SetMaterial(Material mat)
        {
            if (m_meshRenderer != null)
            {
                m_meshRenderer.material = mat;
            }
        }

        private Material CreateBoxMaterial(Color color)
        {
            // Based on shader dump, only Sprites/Default and UI/Default are available in Valheim
            // Try Sprites/Default first - it supports transparency and vertex colors
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("UI/Default");
            }

            // Fallback chain if somehow those aren't available
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogWarning("[EnvironmentBox] Could not find any valid shader for box material!");
                return null;
            }

            var mat = new Material(shader);
            mat.color = color;
            mat.renderQueue = 3000; // Transparent queue

            return mat;
        }

        private Material CreateWireframeMaterial()
        {
            // Use same shader chain as box material - Sprites/Default works well for wireframe too
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("UI/Default");
            }
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
            }

            if (shader == null)
            {
                Debug.LogWarning("[EnvironmentBox] Could not find any valid shader for wireframe material!");
                return null;
            }

            var mat = new Material(shader);
            mat.color = new Color(0.2f, 0.9f, 0.3f, 0.9f);   // thin edge bars need near-opaque to read
            mat.renderQueue = 3000;
            return mat;
        }

        private void SpawnHiddenMarker()
        {
            if (m_hiddenMarker != null) return;

            // Create a simple marker object - don't parent it to avoid scale issues
            m_hiddenMarker = new GameObject("EnvBox_HiddenMarker");

            // Add a visible sphere as marker
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "EnvBox_MarkerOrb";
            marker.transform.SetParent(m_hiddenMarker.transform);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localScale = Vector3.one * 0.5f;

            var markerRenderer = marker.GetComponent<MeshRenderer>();
            markerRenderer.material = CreateBoxMaterial(new Color(1f, 0.5f, 0f, 0.8f));

            // Remove collider from marker sphere
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Position at top of box (accounting for transform scale)
            UpdateHiddenMarkerPosition();
        }

        /// <summary>
        /// Updates the hidden marker position to stay at the top center of the box.
        /// Accounts for both BoxSize and transform.localScale.
        /// </summary>
        private void UpdateHiddenMarkerPosition()
        {
            if (m_hiddenMarker == null) return;

            // Calculate the actual world-space top of the box
            // The box center is at transform.position, half-extents are BoxSize/2 * localScale
            Vector3 scale = transform.localScale;
            float worldHalfHeight = (BoxSize.y / 2f) * scale.y;

            // Position marker at top of box + small offset
            Vector3 topCenter = transform.position + Vector3.up * (worldHalfHeight + 0.5f);
            m_hiddenMarker.transform.position = topCenter;

            // Keep marker oriented upright (don't inherit box rotation for visibility)
            m_hiddenMarker.transform.rotation = Quaternion.identity;
        }

        private void DestroyHiddenMarker()
        {
            if (m_hiddenMarker != null)
            {
                Destroy(m_hiddenMarker);
                m_hiddenMarker = null;
            }
        }

        #endregion

        #region Mesh Generation

        private void UpdateBoxMesh()
        {
            if (IsClientOnly()) return; // dedi: no visible box mesh
            if (m_meshFilter == null) return;

            // EdgesOnly renders a REAL wireframe (12 thin edge bars), not a tinted solid box.
            m_meshFilter.mesh = CurrentVisibility == VisibilityMode.EdgesOnly
                ? BuildEdgeMesh(BoxSize)
                : BuildBoxMesh(BoxSize);
        }

        private static Mesh BuildBoxMesh(Vector3 size)
        {
            Mesh mesh = new Mesh();
            mesh.name = "EnvBoxMesh";

            Vector3 half = size / 2f;

            // Create box vertices
            Vector3[] vertices = new Vector3[]
            {
                // Bottom face
                new Vector3(-half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, -half.z),
                new Vector3(half.x, -half.y, half.z),
                new Vector3(-half.x, -half.y, half.z),
                // Top face
                new Vector3(-half.x, half.y, -half.z),
                new Vector3(half.x, half.y, -half.z),
                new Vector3(half.x, half.y, half.z),
                new Vector3(-half.x, half.y, half.z)
            };

            // Triangles for all 6 faces (inverted for inside view)
            int[] triangles = new int[]
            {
                // Bottom
                0, 2, 1, 0, 3, 2,
                // Top
                4, 5, 6, 4, 6, 7,
                // Front
                0, 1, 5, 0, 5, 4,
                // Back
                2, 3, 7, 2, 7, 6,
                // Left
                3, 0, 4, 3, 4, 7,
                // Right
                1, 2, 6, 1, 6, 5
            };

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // A true wireframe: one mesh of 12 thin bars along the box edges (4 verticals + 4 top + 4 bottom).
        private static Mesh BuildEdgeMesh(Vector3 size)
        {
            Vector3 half = size / 2f;
            float t = Mathf.Clamp(Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.02f, 0.05f, 0.3f);

            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            // Verticals
            AddBar(vertices, triangles, new Vector3(-half.x, 0f, -half.z), new Vector3(t, size.y, t));
            AddBar(vertices, triangles, new Vector3(half.x, 0f, -half.z), new Vector3(t, size.y, t));
            AddBar(vertices, triangles, new Vector3(half.x, 0f, half.z), new Vector3(t, size.y, t));
            AddBar(vertices, triangles, new Vector3(-half.x, 0f, half.z), new Vector3(t, size.y, t));
            // Bottom ring
            AddBar(vertices, triangles, new Vector3(0f, -half.y, -half.z), new Vector3(size.x, t, t));
            AddBar(vertices, triangles, new Vector3(0f, -half.y, half.z), new Vector3(size.x, t, t));
            AddBar(vertices, triangles, new Vector3(-half.x, -half.y, 0f), new Vector3(t, t, size.z));
            AddBar(vertices, triangles, new Vector3(half.x, -half.y, 0f), new Vector3(t, t, size.z));
            // Top ring
            AddBar(vertices, triangles, new Vector3(0f, half.y, -half.z), new Vector3(size.x, t, t));
            AddBar(vertices, triangles, new Vector3(0f, half.y, half.z), new Vector3(size.x, t, t));
            AddBar(vertices, triangles, new Vector3(-half.x, half.y, 0f), new Vector3(t, t, size.z));
            AddBar(vertices, triangles, new Vector3(half.x, half.y, 0f), new Vector3(t, t, size.z));

            Mesh mesh = new Mesh();
            mesh.name = "EnvBoxEdgeMesh";
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddBar(List<Vector3> vertices, List<int> triangles, Vector3 center, Vector3 size)
        {
            Vector3 h = size / 2f;
            int b = vertices.Count;
            vertices.Add(center + new Vector3(-h.x, -h.y, -h.z));
            vertices.Add(center + new Vector3(h.x, -h.y, -h.z));
            vertices.Add(center + new Vector3(h.x, -h.y, h.z));
            vertices.Add(center + new Vector3(-h.x, -h.y, h.z));
            vertices.Add(center + new Vector3(-h.x, h.y, -h.z));
            vertices.Add(center + new Vector3(h.x, h.y, -h.z));
            vertices.Add(center + new Vector3(h.x, h.y, h.z));
            vertices.Add(center + new Vector3(-h.x, h.y, h.z));
            int[] tris =
            {
                0, 2, 1, 0, 3, 2,   // bottom
                4, 5, 6, 4, 6, 7,   // top
                0, 1, 5, 0, 5, 4,   // front
                2, 3, 7, 2, 7, 6,   // back
                3, 0, 4, 3, 4, 7,   // left
                1, 2, 6, 1, 6, 5,   // right
            };
            foreach (int i in tris) triangles.Add(b + i);
        }

        #endregion

        #region Queries

        /// <summary>
        /// Checks if a point is inside this environment box.
        /// </summary>
        public bool ContainsPoint(Vector3 worldPoint)
        {
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
            Vector3 half = BoxSize / 2f;

            return localPoint.x >= -half.x && localPoint.x <= half.x &&
                   localPoint.y >= -half.y && localPoint.y <= half.y &&
                   localPoint.z >= -half.z && localPoint.z <= half.z;
        }

        #endregion

        #region Hoverable / Interactable

        // Minimal hover — the state + all configuration live in the EnvironmentBoxPanel popup (Shift+Alt+RMB).
        // Valheim 1.0 added GetHoverOffset to the Hoverable interface; it is ADDED to
        // Player.m_maxInteractDistance, so 0 preserves the previous interact range exactly.
        public float GetHoverOffset() => 0f;

        public string GetHoverText()
        {
            // Non-admins see nothing — both hover label and full panel are hidden.
            if (!IsLocalPlayerAdmin()) return "";

            if (Locked)
                return "<color=#ff6666><b>Environment Box — LOCKED</b></color>\n[<color=yellow><b>Shift+Alt+RightClick</b></color>] Configure";
            return "<color=orange><b>Environment Box</b></color>\n[<color=yellow><b>Shift+Alt+RightClick</b></color>] Configure";
        }

        // Returning empty hides the cursor label entirely for non-admins, so
        // they never see "Environment Box" floating in their HUD when looking
        // at one. Admins still see the standard label.
        public string GetHoverName() => IsLocalPlayerAdmin() ? "Environment Box" : "";

        // All configuration lives in the EnvironmentBoxPanel popup (Shift+Alt+RMB) — [E] and hover keys do nothing.
        public bool Interact(Humanoid user, bool hold, bool alt) => false;

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;

        /// <summary>
        /// Checks if the local player has admin privileges.
        /// Uses the same admin check pattern as NpcController.
        /// </summary>
        internal static bool IsLocalPlayerAdmin()
        {
            // The EXACT RPGMaker admin gate (the one that locks config pushes to admins): the mod's own ConfigSync
            // IsAdmin flag — lockExempt pushed by the server's AdminSyncing, OR this peer being the source of truth
            // (host/SP). Same pattern as FiresRPGmaker.Instance?.configSync?.IsAdmin. Non-admins get NO hover text
            // and NO config panel; every admin sees both.
            if (ZNet.instance?.IsServer() == true)
                return true;
            if (FiresUnifiedCore.Instance?.configSync?.IsAdmin == true)
                return true;
            // Canonical family client gate: the consumer-wired bridge (FiresNPCs → FiresRPGmaker.configSync.IsAdmin).
            // Catches the admin whose Core lockExempt push hasn't landed. Fails closed when unwired.
            try { return NpcConfigBridge.IsAdmin(); } catch { return false; }
        }

        #endregion

        #region ZDO Persistence

        private void LoadFromZDO()
        {
            if (m_nview == null) return;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            // Load size
            float sx = zdo.GetFloat(HASH_SIZE_X, BoxSize.x);
            float sy = zdo.GetFloat(HASH_SIZE_Y, BoxSize.y);
            float sz = zdo.GetFloat(HASH_SIZE_Z, BoxSize.z);
            BoxSize = new Vector3(sx, sy, sz);

            // Load environment
            EnvironmentName = zdo.GetString(HASH_ENV, EnvironmentName);
            ForceEnvironment = zdo.GetBool(HASH_FORCE_ENV, ForceEnvironment);

            // Load biome
            ForcedBiome = (Heightmap.Biome)zdo.GetInt(HASH_BIOME, (int)ForcedBiome);

            // Load skybox
            CurrentSkyboxMode = (SkyboxMode)zdo.GetInt(HASH_SKYBOX, (int)CurrentSkyboxMode);

            // Load visibility
            CurrentVisibility = (VisibilityMode)zdo.GetInt(HASH_VISIBILITY, (int)CurrentVisibility);

            // Load wind
            CurrentWindIntensity = (WindIntensity)zdo.GetInt(HASH_WIND, (int)CurrentWindIntensity);

            // Load time
            CurrentTimeOfDay = (ForcedTimeOfDay)zdo.GetInt(HASH_TIME, (int)CurrentTimeOfDay);

            // Load AffectsVanillaPaint
            AffectsVanillaPaint = zdo.GetBool(HASH_AFFECTS_VANILLA, AffectsVanillaPaint);

            // Load Locked state
            Locked = zdo.GetBool(HASH_LOCKED, Locked);

            // Load ambient color
            float ar = zdo.GetFloat(HASH_AMB_R, AmbientColorOverride.r);
            float ag = zdo.GetFloat(HASH_AMB_G, AmbientColorOverride.g);
            float ab = zdo.GetFloat(HASH_AMB_B, AmbientColorOverride.b);
            AmbientColorOverride = new Color(ar, ag, ab);

            // Apply loaded state
            if (m_trigger != null)
            {
                m_trigger.size = BoxSize;
            }
        }

        private void SaveToZDO()
        {
            if (m_nview == null || !m_nview.IsOwner()) return;
            var zdo = m_nview.GetZDO();
            if (zdo == null) return;

            // Save size
            zdo.Set(HASH_SIZE_X, BoxSize.x);
            zdo.Set(HASH_SIZE_Y, BoxSize.y);
            zdo.Set(HASH_SIZE_Z, BoxSize.z);

            // Save environment
            zdo.Set(HASH_ENV, EnvironmentName);
            zdo.Set(HASH_FORCE_ENV, ForceEnvironment);

            // Save biome
            zdo.Set(HASH_BIOME, (int)ForcedBiome);

            // Save skybox
            zdo.Set(HASH_SKYBOX, (int)CurrentSkyboxMode);

            // Save visibility
            zdo.Set(HASH_VISIBILITY, (int)CurrentVisibility);

            // Save wind
            zdo.Set(HASH_WIND, (int)CurrentWindIntensity);

            // Save time
            zdo.Set(HASH_TIME, (int)CurrentTimeOfDay);

            // Save AffectsVanillaPaint
            zdo.Set(HASH_AFFECTS_VANILLA, AffectsVanillaPaint);

            // Save Locked state
            zdo.Set(HASH_LOCKED, Locked);

            // Save ambient color
            zdo.Set(HASH_AMB_R, AmbientColorOverride.r);
            zdo.Set(HASH_AMB_G, AmbientColorOverride.g);
            zdo.Set(HASH_AMB_B, AmbientColorOverride.b);
        }

        #endregion
    }
}
