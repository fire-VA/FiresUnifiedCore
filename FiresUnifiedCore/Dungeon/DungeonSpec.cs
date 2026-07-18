using System;
using FiresCore.Utilities;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// The complete parameterization of one custom modular dungeon, consumed by the generic
    /// <see cref="FiresDungeonCore"/> engine. Each owning mod (FiresMausoleum today, FiresDungeonMaster later)
    /// builds one or more of these and registers them via
    /// <see cref="FiresCore.Bridge.FiresDungeonBridge.RegisterDungeon"/>. The engine dispatches every per-DG
    /// Harmony body by <see cref="DgNamePrefix"/> so multiple dungeon mods never fight over the same static state.
    ///
    /// Everything that varies between dungeon mods lives here as DATA; the engine in FiresDungeonCore is pure
    /// mechanism. The owning mod still ships + registers its own bundle prefabs into ZNetScene — the spec only
    /// carries the NAMES, which the engine resolves through the supplied <see cref="Assets"/> registry.
    /// </summary>
    public sealed class DungeonSpec
    {
        /// <summary>
        /// The GameObject-name PREFIX of this dungeon's DungeonGenerator anchor (e.g. "FiresMausoleumDG"). The
        /// SetupAvailableRooms / Awake patches gate on <c>dg.gameObject.name.StartsWith(DgNamePrefix)</c>. Must be
        /// unique per registered spec so dispatch is unambiguous.
        /// </summary>
        public string DgNamePrefix;

        /// <summary>The surface Location prefab name placed by ZoneSystem world-gen (e.g. "FiresMausoleumCrypt").</summary>
        public string CryptLocationPrefabName;

        /// <summary>The interior prefab name (instantiated at y+5000 by the Location). Informational / future use.</summary>
        public string InteriorPrefabName;

        /// <summary>
        /// A NAMED vanilla Room.Theme (e.g. Room.Theme.ForestCrypt). MUST be a named enum value: Expand World Data
        /// patches Enum.TryParse&lt;Room.Theme&gt; and drops unnamed bits. Isolation comes from the per-DG
        /// SetupAvailableRooms injection, not the theme.
        /// </summary>
        public Room.Theme NamedTheme;

        /// <summary>
        /// The room prefab NAMES the DungeonGenerator stitches together inside this dungeon (resolved from
        /// <see cref="Assets"/>). At least one MUST carry m_entrance=true on its baked Room so FindStartRoom never
        /// crashes (the engine does not set m_entrance; it is baked on the prefab).
        /// </summary>
        public string[] RoomPrefabNames;

        // ── env-box (atmosphere wrap) ────────────────────────────────────────────

        /// <summary>The baked+registered empty env-box prefab name (e.g. "FiresEnvBox"). Stays mod-baked.</summary>
        public string EnvBoxPrefabName;

        /// <summary>Environment name forced inside the box (e.g. "Crypt").</summary>
        public string EnvName;

        /// <summary>Skybox enclosure mode (e.g. DungeonBlack).</summary>
        public EnvironmentBoxController.SkyboxMode SkyboxMode = EnvironmentBoxController.SkyboxMode.DungeonBlack;

        /// <summary>Forced biome inside the box (None for a dungeon — no terrain override).</summary>
        public Heightmap.Biome EnvBoxBiome = Heightmap.Biome.None;

        /// <summary>Cubic env-box size (encloses the dungeon zone + margin). Default 72.</summary>
        public float EnvBoxSize = 72f;

        /// <summary>Dedupe radius — skip spawning a second box if one already sits within this distance. Default 40.</summary>
        public float EnvBoxDedupeRadius = 40f;

        // ── doorway blockers ─────────────────────────────────────────────────────

        /// <summary>
        /// When true, the owning mod attaches <see cref="DungeonDoorBlockerController"/> to each room prefab at
        /// ZNetScene registration so every unused (non-entrance, unpaired) doorway is walled off solid per-peer,
        /// preventing a fall into the y+5000 void. The blocker walls are baked CHILDREN of each RoomConnection in
        /// the room prefab (see FiresMausoleumBuilder.BuildRoom). Pure per-client mechanism — no RPC, no ZNetView,
        /// no engine patch. Default false (opt-in).
        /// </summary>
        public bool BakeDoorBlockers = false;

        // ── optional near-spawn ZoneLocation ─────────────────────────────────────

        /// <summary>
        /// When non-null, the engine appends one ZoneLocation built from this descriptor in the
        /// ZoneSystem.SetupLocations postfix (the surface placement near spawn). Null = no world-gen placement
        /// (placed only via the spawn command / blueprint).
        /// </summary>
        public ZoneLocationDescriptor NearSpawnLocation;

        // ── portal link ──────────────────────────────────────────────────────────

        /// <summary>Name prefix of the surface gateway teleport (re-paired locally on every peer).</summary>
        public string GatewayNamePrefix;

        /// <summary>Name prefix of the interior exit teleport.</summary>
        public string ExitNamePrefix;

        /// <summary>
        /// When true, the interior EXIT teleport does NOT fire on proximity — the player must press [Use] to leave.
        /// Vanilla <c>Teleport.OnTriggerEnter</c> auto-teleports anyone who walks into the exit's trigger volume, which
        /// ejects players who just want to inspect something near the door (e.g. a headstone by the mausoleum exit).
        /// The [Use]/Interact path is untouched. Default false (keep vanilla step-on-to-exit).
        /// </summary>
        public bool RequireInteractToExit = false;

        /// <summary>
        /// Prefab names of this dungeon's SURFACE structures (location building, portal root, …) that
        /// <see cref="FiresDungeonTeardown.DestroyDungeonAt"/> removes alongside the interior. Matched by ZDO prefab
        /// hash within <see cref="SurfaceCleanupRadius"/> (XZ) of the entrance, so neighbouring player builds are never
        /// touched. Null/empty defaults to just <see cref="CryptLocationPrefabName"/>.
        /// </summary>
        public string[] SurfaceCleanupPrefabNames;

        /// <summary>XZ radius around the entrance for the surface-structure sweep. Default 32.</summary>
        public float SurfaceCleanupRadius = 32f;

        /// <summary>Interior Y offset (the exit sits gateway + this in Y). Default 5000.</summary>
        public float InteriorYOffset = 5000f;

        /// <summary>
        /// When true, <see cref="FiresDungeonCore.SpawnDungeonAt"/> snaps the spawn XZ to the ZONE CENTRE before
        /// placing the Location, so the DungeonGenerator lands at the zone centre EXACTLY like a vanilla interior
        /// dungeon. Vanilla <c>Location.Awake</c> hardcodes the interior env box to the zone centre and
        /// <c>DungeonGenerator.Generate</c> centres its placement <c>Bounds(m_zoneCenter, m_zoneSize)</c> on the zone
        /// centre — so a DG anchored at an arbitrary door XZ is OFF-centre and its rooms clip on one side unless the
        /// zone volume is ballooned (which then over-generates). Anchoring to the zone centre lets the REAL
        /// (unballooned) m_zoneSize fill the zone symmetrically. One dungeon per XZ zone (vanilla's own constraint).
        /// The owning mod's teleport/cleanup must compute the SAME zone centre from the door position. Default false.
        /// </summary>
        public bool AnchorInteriorToZoneCenter = false;

        /// <summary>
        /// When true, the DungeonGenerator's room generation is SKIPPED entirely (<see cref="FiresDungeonCore"/> prefixes
        /// <c>DungeonGenerator.Generate</c> and returns without running it). The Location's own baked children — the
        /// entry/exit portal, the arrival landing pad, the visible exit door — and the env box (created on the DG's
        /// Awake) are still spawned, so the result is a single ENCLOSED EMPTY ROOM you teleport into and out of, with a
        /// floor to stand on but no procedural rooms. Use with a theme no room carries + empty RoomPrefabNames so nothing
        /// would generate anyway; this flag additionally avoids vanilla's <c>PlaceStartRoom</c> NRE on an empty room set.
        /// Default false.
        /// </summary>
        public bool EmptyInterior = false;

        /// <summary>
        /// Visibility of the spawned env box itself (the placeable box's own shell/marker): Hidden for normal
        /// dungeon interiors (the rooms are the visuals), Full for an EMPTY room the admin builds inside of —
        /// the semi-transparent shell shows the volume from inside and out. Default Hidden.
        /// </summary>
        public EnvironmentBoxController.VisibilityMode EnvBoxVisibility = EnvironmentBoxController.VisibilityMode.Hidden;

        // ── hooks / gates ────────────────────────────────────────────────────────

        /// <summary>Master enable predicate (the engine no-ops every body when this returns false). Null = always on.</summary>
        public Func<bool> EnabledGate;

        /// <summary>Log tag prefix for engine diagnostics, e.g. "[FiresMausoleum]".</summary>
        public string LogTag = "[FiresDungeon]";

        /// <summary>
        /// Post-spawn / post-regenerate content reconcile hook (e.g. Mausoleum's memorial niche drop). The engine
        /// calls this after a successful manual spawn and after a stale-heal regenerate. Null = no content pass.
        /// </summary>
        public Action OnDungeonGenerated;

        /// <summary>The per-spec SoftReference asset registry (the owning mod hands its loaded bundle prefabs here).</summary>
        public FiresDungeonAssets Assets;

        public bool IsEnabled() => EnabledGate == null || EnabledGate();

        public bool MatchesDg(string dgGameObjectName) =>
            !string.IsNullOrEmpty(dgGameObjectName) &&
            !string.IsNullOrEmpty(DgNamePrefix) &&
            dgGameObjectName.StartsWith(DgNamePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pure-data description of the near-spawn ZoneLocation for a dungeon's surface placement. The engine builds a
    /// vanilla ZoneSystem.ZoneLocation from these values (the SoftReference m_prefab is resolved by the engine from
    /// the spec's <see cref="DungeonSpec.CryptLocationPrefabName"/>).
    /// </summary>
    public sealed class ZoneLocationDescriptor
    {
        public Heightmap.Biome Biome = Heightmap.Biome.All;
        public Heightmap.BiomeArea BiomeArea = Heightmap.BiomeArea.Everything;
        public int Quantity = 1;
        public bool Unique = true;
        public bool Prioritized = true;
        public bool CenterFirst = true;
        public float MinDistanceFromCenter = 0f;
        public float MaxDistanceFromCenter = 300f;
        public float ExteriorRadius = 16f;
        public float InteriorRadius = 32f;
        public bool ClearArea = true;
        public bool RandomRotation = true;
        public float MinAltitude = 1f;
    }
}
