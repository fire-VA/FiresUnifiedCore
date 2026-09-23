using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Logging;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// The custom-dungeon engine, lifted from FiresMausoleum and parameterized by <see cref="DungeonSpec"/>. Its
    /// patches live once here and dispatch by generator-name prefix through <see cref="FiresDungeonRegistry"/>, so
    /// several dungeon mods coexist, and they hold up against Expand World Data rebuilding DungeonDB. DungeonDB.Start
    /// registers each spec's rooms and indexes them by hash for saved dungeons; SetupAvailableRooms swaps in exactly
    /// the matching spec's rooms; DungeonGenerator.Awake ensures the environment box and stale-dungeon heal; and
    /// SetupLocations adds each spec's optional near-spawn location.
    /// </summary>
    public static class FiresDungeonCore
    {
        private static FieldInfo _roomByHashField;
        private static FieldInfo _locationsByHashField;
        private static MethodInfo _spawnLocationMethod;
        private static int _manualSpawnDepth;

        // The held RoomData per spec (keyed by DG-name prefix), so SetupAvailableRooms can re-inject even after EWD
        // rebuilds DungeonDB.m_rooms. Order irrelevant; one room carries m_entrance=true.
        private static readonly Dictionary<string, List<DungeonDB.RoomData>> _heldRooms =
            new Dictionary<string, List<DungeonDB.RoomData>>(StringComparer.Ordinal);

        // The dungeon env box MINIMUM = vanilla's interior EnvZone scale (Location.Awake: new Vector3(64, 500, 64) at
        // the zone centre): 64 wide (X) x 64 long (Z) x 500 tall (Y). This is placed the instant a dungeon spawns and is
        // the floor the box never goes below. For dungeons whose rooms sprawl PAST the single 64x64 zone (e.g. a large
        // mausoleum) the SAME box is scaled UP per-axis to cover them — one box, never smaller than this.
        private static readonly Vector3 VanillaEnvBoxSize = new Vector3(64f, 500f, 64f);

        private static List<DungeonDB.RoomData> HeldFor(DungeonSpec spec)
        {
            if (!_heldRooms.TryGetValue(spec.DgNamePrefix, out var list))
            {
                list = new List<DungeonDB.RoomData>();
                _heldRooms[spec.DgNamePrefix] = list;
            }
            return list;
        }

        // ── DungeonDB: register every spec's rooms ───────────────────────────────

        [HarmonyPatch(typeof(DungeonDB), "Start")]
        internal static class DungeonDB_Start_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(DungeonDB __instance)
            {
                foreach (var spec in FiresDungeonRegistry.All)
                {
                    if (spec == null || !spec.IsEnabled()) continue;
                    try { RegisterRooms(__instance, spec); }
                    catch (Exception ex) { Debug.LogError($"{spec.LogTag} room register failed: {ex}"); }
                }
            }
        }

        private static void RegisterRooms(DungeonDB dungeonDb, DungeonSpec spec)
        {
            var rooms = DungeonDB.GetRooms();
            if (rooms == null) return;

            var roomByHash = GetRoomByHash(dungeonDb);
            var held = HeldFor(spec);
            held.Clear();

            // Override the DG's baked theme to the same NAMED value so any tool reading it (EWD, debug dumps) can
            // name it. Generation isolation is enforced by SetupAvailableRooms, not m_themes.
            GameObject crypt = spec.Assets?.Get(spec.CryptLocationPrefabName);
            var dgComp = crypt != null ? crypt.GetComponentInChildren<DungeonGenerator>(true) : null;
            if (dgComp != null) dgComp.m_themes = spec.NamedTheme;

            foreach (string prefabName in spec.RoomPrefabNames)
            {
                GameObject prefab = spec.Assets?.Get(prefabName);
                if (prefab == null)
                {
                    Debug.LogWarning($"{spec.LogTag} room prefab '{prefabName}' not loaded; skipping registration.");
                    continue;
                }

                var roomComp = prefab.GetComponent<Room>();
                if (roomComp != null) roomComp.m_theme = spec.NamedTheme;

                int hash = prefabName.GetStableHashCode();
                SoftReference<GameObject> softRef = spec.Assets.SoftRefFor(prefab);
                var data = new DungeonDB.RoomData
                {
                    m_prefab = softRef,
                    m_enabled = true,
                    m_theme = spec.NamedTheme,
                };
                held.Add(data);

                if (roomByHash == null || !roomByHash.ContainsKey(hash))
                {
                    rooms.Add(data);
                    if (roomByHash != null) roomByHash[hash] = data;
                    Debug.Log($"{spec.LogTag} registered room '{prefabName}' (hash {hash}) theme={spec.NamedTheme}.");
                }
            }
        }

        private static Dictionary<int, DungeonDB.RoomData> GetRoomByHash(DungeonDB dungeonDb)
        {
            if (_roomByHashField == null)
                _roomByHashField = typeof(DungeonDB).GetField("m_roomByHash", BindingFlags.NonPublic | BindingFlags.Instance);
            return _roomByHashField?.GetValue(dungeonDb) as Dictionary<int, DungeonDB.RoomData>;
        }

        // ── DungeonGenerator.SetupAvailableRooms: inject ONLY the matching spec's rooms ──

        [HarmonyPatch(typeof(DungeonGenerator), "SetupAvailableRooms")]
        internal static class DungeonGenerator_SetupAvailableRooms_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(DungeonGenerator __instance)
            {
                if (__instance == null) return;
                var spec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (spec == null)
                {
                    // NOT one of our dungeons. Our rooms deliberately carry a NAMED vanilla theme (EWD drops rooms
                    // whose theme value has no name), so vanilla's theme filter pulls them into ANY same-theme
                    // dungeon — e.g. mausoleum rooms stitched into a real ForestCrypt. InjectOurRooms only guards the
                    // other direction (vanilla rooms out of OUR dungeon); this is the missing half.
                    try { StripOurRoomsFromForeignDg(__instance); }
                    catch (Exception ex) { Debug.LogError($"[FiresCore] foreign-DG room strip failed: {ex}"); }
                    return;
                }
                try { InjectOurRooms(__instance, spec); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} room injection failed: {ex}"); }
            }
        }

        // ── DungeonGenerator.Awake: env box + stale-heal for the matching spec's DG ──

        [HarmonyPatch(typeof(DungeonGenerator), "Awake")]
        internal static class DungeonGenerator_Awake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(DungeonGenerator __instance)
            {
                if (__instance == null || __instance.gameObject == null) return;
                var spec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (spec == null) return;
                try { MaybeRegenerateStale(__instance, spec); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} stale-crypt check failed: {ex}"); }
            }
        }

        // ── DungeonGenerator.Generate: fire the spec's content hook (e.g. the memorial reconcile) on EVERY
        // generation path — world-gen, the manual spawn command, and stale-heal regen. SpawnDungeonAt also fires
        // it explicitly, but a WORLD-GEN dungeon never goes through SpawnDungeonAt (it runs the vanilla pipeline
        // directly), so without this its content (tombstones) never gets placed. The hook is idempotent (the
        // reconcile dedupes), so the redundant fire on the manual path is harmless. Server-gated (Generate is
        // server-authoritative).
        [HarmonyPatch(typeof(DungeonGenerator), "Generate", new[] { typeof(ZoneSystem.SpawnMode) })]
        internal static class DungeonGenerator_Generate_Patch
        {
            // EMPTY-ROOM mode: for a spec flagged EmptyInterior, skip vanilla Generate entirely. Vanilla's
            // PlaceStartRoom dereferences FindStartRoom() with no null-check, so a DG with zero available rooms would
            // NRE — and we WANT zero rooms. Skipping leaves the Location's baked portal/landing-pad/exit-door + the
            // env box (created on this DG's Awake postfix), i.e. a single enclosed empty room. The Postfix still runs
            // (ScaleEnvBoxToRooms bails at 0 rooms; OnDungeonGenerated is null for an empty room).
            [HarmonyPrefix]
            private static bool Prefix(DungeonGenerator __instance)
            {
                if (__instance == null || __instance.gameObject == null) return true;
                var spec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (spec != null && spec.EmptyInterior)
                {
                    Debug.Log($"{spec.LogTag} EmptyInterior — skipping room generation (portal + landing pad + exit + env box only).");
                    return false;   // do not run vanilla Generate
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(DungeonGenerator __instance)
            {
                if (__instance == null || __instance.gameObject == null) return;
                var dressSpec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (dressSpec == null) return;

                // Visual prop markers dress on EVERY peer (each peer instantiates room roots locally); the
                // dresser itself no-ops headless and on already-dressed markers.
                try { FiresDungeonPropDresser.Dress(__instance, dressSpec); }
                catch (Exception ex) { Debug.LogError($"{dressSpec.LogTag} prop dresser (Generate) threw: {ex}"); }

                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                var spec = dressSpec;

                // All rooms are now placed as children of the DG (synchronous on the server Generate path). Grow +
                // re-center the env box to fully encompass them — THE fix for the fixed-size box leaking sky.
                try { ScaleEnvBoxToRooms(__instance, spec); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} env-box auto-scale (Generate) threw: {ex}"); }

                try { spec.OnDungeonGenerated?.Invoke(); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} Generate content hook threw: {ex}"); }
            }
        }

        // ── DungeonGenerator.PlaceStartRoom: guard against 0 available rooms ──
        // Vanilla PlaceStartRoom → FindStartRoom indexes m_availableRooms with no empty-check, so a dungeon whose theme
        // resolves to ZERO stock/injected rooms (e.g. a label mapped to an unused vanilla theme, or EWD having dropped a
        // theme's rooms) throws IndexOutOfRange and the whole spawn fails. For OUR dungeons only (MatchForDg), skip start-
        // room placement when there's nothing to place — the portal + env box still spawn, so you get an empty room
        // instead of a crash. Other mods' / vanilla world-gen dungeons are untouched.
        [HarmonyPatch(typeof(DungeonGenerator), "PlaceStartRoom")]
        internal static class DungeonGenerator_PlaceStartRoom_Guard
        {
            [HarmonyPrefix]
            private static bool Prefix(DungeonGenerator __instance)
            {
                if (__instance == null || __instance.gameObject == null) return true;
                var spec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (spec == null) return true;
                var avail = DungeonGenerator.m_availableRooms;
                if (avail == null || avail.Count == 0)
                {
                    Debug.LogWarning($"{spec.LogTag} 0 available rooms for theme {spec.NamedTheme} — skipping room placement to avoid vanilla's empty-list crash (dungeon will be empty).");
                    return false;
                }
                return true;
            }
        }

        // ── DungeonGenerator.Load: re-scale the env box after a saved dungeon's rooms re-spawn as DG children.
        // Mirrors ExpandWorldData.Dungeon.EnvironmentBox which also hooks Load. On the server, Load re-instantiates
        // the saved rooms synchronously (Spawn); the env box (ZDO-persisted) already carries the grown size from
        // the original Generate, but re-running here heals any box that predates this fix or was placed at the
        // fixed 72m default. Server-gated. ──
        [HarmonyPatch(typeof(DungeonGenerator), "Load")]
        internal static class DungeonGenerator_Load_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(DungeonGenerator __instance)
            {
                if (__instance == null || __instance.gameObject == null) return;
                var spec = FiresDungeonRegistry.MatchForDg(__instance.gameObject.name);
                if (spec == null) return;

                // Load is the CLIENT path for room roots (a remote peer rebuilds a saved dungeon's rooms from
                // s_roomData here) — dress the visual prop markers on every peer before the server-only work.
                try { FiresDungeonPropDresser.Dress(__instance, spec); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} prop dresser (Load) threw: {ex}"); }

                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                try { ScaleEnvBoxToRooms(__instance, spec); }
                catch (Exception ex) { Debug.LogError($"{spec.LogTag} env-box auto-scale (Load) threw: {ex}"); }
            }
        }

        private static void MaybeRegenerateStale(DungeonGenerator generator, DungeonSpec spec)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // only the authority regenerates

            // Wrap the interior rooms in the env box (server-authoritative, ZDO-persisted). Ensured BEFORE the
            // ZDO-owner early-return so the env box is created on every server even when regeneration is skipped.
            EnsureEnvBox(generator, spec);

            // Inside our own SpawnDungeonAt the pipeline calls Generate right after this Awake — not stale.
            if (_manualSpawnDepth > 0) return;

            var netView = generator.GetComponent<ZNetView>();
            ZDO zdo = (netView != null && netView.IsValid()) ? netView.GetZDO() : null;
            if (zdo == null || !zdo.IsOwner()) return;

            // already generated? (Save always writes s_roomData). Then leave it alone.
            if (zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data) && data != null && data.Length >= 4) return;

            Debug.Log($"{spec.LogTag} stale dungeon DG at {generator.transform.position} has no saved rooms — scheduling a regenerate.");
            FiresDungeonService.RegenerateDungeonSoon(generator, spec);
        }

        /// <summary>
        /// Ensure exactly ONE env box wraps this dungeon's interior (the DG/rooms area at y+5000). The box forces
        /// the spec's environment + a fully-enclosed skybox so the interior reads as a proper dungeon void instead
        /// of a bright open sky. ForcedBiome from the spec (None for a dungeon). Deduped by scanning for an existing
        /// box near the DG; the box is ZDO-backed so it persists. Server only (caller is IsServer-gated).
        /// </summary>
        private static void EnsureEnvBox(DungeonGenerator generator, DungeonSpec spec)
        {
            if (string.IsNullOrEmpty(spec.EnvBoxPrefabName)) return;
            Vector3 center = generator.transform.position; // already at surface pos + (0,5000,0), i.e. the zone centre
            Vector3 boxSize = VanillaEnvBoxSize;    // 64 x 64 x 500 immediately — vanilla parity, no math

            if (FiresCore.Logging.FiresLogger.VerboseEnabled) Debug.Log($"[ENVBOX-DBG] EnsureEnvBox: {spec.LogTag} DG='{generator.gameObject.name}' dgPos={center} " +
                      $"boxSize={boxSize} (vanilla 64x64x500 minimum) env='{spec.EnvName}' skybox={spec.SkyboxMode}. " +
                      $"ScaleEnvBoxToRooms grows this SAME box up if rooms sprawl past the zone.");

            float dedupeSqr = spec.EnvBoxDedupeRadius * spec.EnvBoxDedupeRadius;
            foreach (var existing in UnityEngine.Object.FindObjectsByType<FiresCore.Utilities.EnvironmentBoxController>(FindObjectsSortMode.None))
            {
                if (existing == null) continue;
                if ((existing.transform.position - center).sqrMagnitude <= dedupeSqr)
                {
                    // Already present — leave its SIZE alone (ScaleEnvBoxToRooms owns that; re-asserting the min here
                    // could shrink a box already scaled up), but assert the spec's ATMOSPHERE config once per revision
                    // so boxes persisted under older builds get corrected (Hidden shell + DungeonBlack interior).
                    AssertSpecConfigOnce(existing, spec);
                    if (FiresCore.Logging.FiresLogger.VerboseEnabled) Debug.Log($"[ENVBOX-DBG] EnsureEnvBox: {spec.LogTag} DEDUPED — existing box '{existing.gameObject.name}' " +
                              $"at {existing.transform.position} size={existing.BoxSize} within {spec.EnvBoxDedupeRadius}m of dgPos={center}.");
                    return;
                }
            }

            var box = FiresCore.Bridge.EnvironmentBoxBridge.SpawnBox(
                spec.EnvBoxPrefabName,
                center,
                boxSize,
                environment: spec.EnvName,
                skybox: spec.SkyboxMode,
                force: true,
                biome: spec.EnvBoxBiome,
                visibility: spec.EnvBoxVisibility);

            if (box != null)
            {
                StampSpecConfigRev(box); // fresh spawn is spec-correct by construction — stamp so the assert never re-runs
                if (FiresCore.Logging.FiresLogger.VerboseEnabled) Debug.Log($"[ENVBOX-DBG] {spec.LogTag} spawned env box '{box.gameObject.name}' at {box.transform.position} " +
                          $"BoxSize={box.BoxSize} ({spec.EnvName} env, {spec.SkyboxMode} skybox, {spec.EnvBoxVisibility} shell).");
            }
            else
                Debug.LogWarning($"{spec.LogTag} failed to spawn dungeon env box (bridge/ZNetScene not ready?).");
        }

        // ── one-shot spec-config assertion on existing boxes ────────────────────────────────────────────────────
        // Dungeon boxes persist their atmosphere config in the ZDO, so a box spawned under an OLDER build keeps its
        // stale settings forever (the dedupe path never touched it). This stamps a config revision into the ZDO and,
        // for any box stamped BELOW the current revision, re-asserts the spec's skybox/visibility/env/biome exactly
        // once — then never again, so admin customization via the dungeon-edit panel is preserved afterward.
        // Bump the revision to force one refresh pass across every existing dungeon box.
        private const int EnvBoxSpecConfigRev = 2;
        private static readonly int HashEnvBoxSpecRev = "fires_envbox_specrev".GetStableHashCode();

        private static void AssertSpecConfigOnce(FiresCore.Utilities.EnvironmentBoxController box, DungeonSpec spec)
        {
            var netView = box.GetComponent<ZNetView>();
            ZDO zdo = (netView != null && netView.IsValid()) ? netView.GetZDO() : null;
            if (zdo == null) return;
            if (zdo.GetInt(HashEnvBoxSpecRev, 0) >= EnvBoxSpecConfigRev) return; // already asserted this revision

            if (!netView.IsOwner()) netView.ClaimOwnership(); // SaveToZDO is owner-gated; the server must own to persist

            box.EnvironmentName = spec.EnvName ?? string.Empty;
            box.CurrentSkyboxMode = spec.SkyboxMode;
            box.CurrentVisibility = spec.EnvBoxVisibility;
            box.ForceEnvironment = true;
            box.ForcedBiome = spec.EnvBoxBiome;
            box.ApplyConfiguredState();
            zdo.Set(HashEnvBoxSpecRev, EnvBoxSpecConfigRev);

            Debug.Log($"{spec.LogTag} asserted spec config on existing env box at {box.transform.position}: " +
                      $"env='{spec.EnvName}' skybox={spec.SkyboxMode} shell={spec.EnvBoxVisibility} (rev {EnvBoxSpecConfigRev}).");
        }

        private static void StampSpecConfigRev(FiresCore.Utilities.EnvironmentBoxController box)
        {
            var netView = box != null ? box.GetComponent<ZNetView>() : null;
            ZDO zdo = (netView != null && netView.IsValid()) ? netView.GetZDO() : null;
            if (zdo != null) zdo.Set(HashEnvBoxSpecRev, EnvBoxSpecConfigRev);
        }

        /// <summary>
        /// Scale the SAME env box up to cover the placed rooms, exactly like the vanilla interior box would if a dungeon
        /// sprawled past its single 64x64 zone. Seeds the AABB at the vanilla box (64x64x500 centred on the DG/zone
        /// centre) then encapsulates every room collider, so: a dungeon that fits one zone stays exactly the vanilla box,
        /// and a larger one grows the box per-axis to wrap it. NEVER shrinks below the vanilla minimum (the 500 height
        /// also keeps the arrival pad above the rooms enclosed). One box, scaled — no buffer math, no second box.
        /// Server-authoritative + ZDO-persisted.
        /// </summary>
        private static void ScaleEnvBoxToRooms(DungeonGenerator generator, DungeonSpec spec)
        {
            if (string.IsNullOrEmpty(spec.EnvBoxPrefabName)) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return; // server computes; clients read ZDO

            var box = FindWrappingBox(generator, spec);
            if (box == null)
            {
                if (FiresCore.Logging.FiresLogger.VerboseEnabled) Debug.Log($"[ENVBOX-DBG] ScaleEnvBoxToRooms: {spec.LogTag} DG='{generator.gameObject.name}' — no env box found " +
                          $"near {generator.transform.position} within {spec.EnvBoxDedupeRadius}m; skipping.");
                return;
            }

            // Seed the AABB as the vanilla box centred on the DG (zone centre). Rooms that fit inside it leave the box at
            // exactly 64x64x500; rooms past the zone extend it.
            Bounds bounds = new Bounds(generator.transform.position, VanillaEnvBoxSize);
            Room[] rooms = generator.GetComponentsInChildren<Room>();
            int colliderCount = 0;
            foreach (Room room in rooms)
            {
                if (room == null) continue;
                foreach (BoxCollider boxCollider in room.GetComponentsInChildren<BoxCollider>())
                {
                    if (boxCollider == null) continue;
                    bounds.Encapsulate(boxCollider.bounds);
                    colliderCount++;
                }
            }

            Vector3 newCenter = bounds.center;
            Vector3 newSize = Vector3.Max(bounds.size, VanillaEnvBoxSize); // never below the vanilla minimum

            if (FiresCore.Logging.FiresLogger.VerboseEnabled) Debug.Log($"[ENVBOX-DBG] ScaleEnvBoxToRooms: {spec.LogTag} DG='{generator.gameObject.name}' rooms={rooms.Length} " +
                      $"colliders={colliderCount} roomAABB[center={bounds.center} size={bounds.size}] -> box center={newCenter} " +
                      $"size={newSize} (min 64x64x500; was center={box.transform.position} size={box.BoxSize}).");

            FiresCore.Bridge.EnvironmentBoxBridge.ReconfigureBoxToBounds(box, newCenter, newSize);
        }

        /// <summary>
        /// Find the single env box wrapping this DG — the nearest <see cref="FiresCore.Utilities.EnvironmentBoxController"/>
        /// within the spec's dedupe radius of the DG anchor (the same criterion EnsureEnvBox uses to dedupe).
        /// </summary>
        private static FiresCore.Utilities.EnvironmentBoxController FindWrappingBox(DungeonGenerator generator, DungeonSpec spec)
        {
            Vector3 center = generator.transform.position;
            float bestSqr = spec.EnvBoxDedupeRadius * spec.EnvBoxDedupeRadius;
            FiresCore.Utilities.EnvironmentBoxController best = null;
            foreach (var box in UnityEngine.Object.FindObjectsByType<FiresCore.Utilities.EnvironmentBoxController>(FindObjectsSortMode.None))
            {
                if (box == null) continue;
                float sqr = (box.transform.position - center).sqrMagnitude;
                if (sqr <= bestSqr) { bestSqr = sqr; best = box; }
            }
            return best;
        }

        /// <summary>
        /// Remove EVERY registered Fires dungeon room from a FOREIGN generator's available list (vanilla or another
        /// mod's dungeon). Our rooms share a named vanilla theme for EWD compatibility, so vanilla's theme filter
        /// includes them in same-theme dungeons — that's how mausoleum rooms end up welded into real forest crypts.
        /// Ours must ONLY ever generate inside their own dungeon, so we strip them back out here. Idempotent and
        /// cheap (a name-set pass over the already-built list); logs only when it actually removed something.
        /// </summary>
        private static void StripOurRoomsFromForeignDg(DungeonGenerator generator)
        {
            var available = DungeonGenerator.m_availableRooms;
            if (available == null || available.Count == 0) return;

            var ours = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in FiresDungeonRegistry.All)
            {
                if (spec?.RoomPrefabNames == null) continue;
                foreach (string roomName in spec.RoomPrefabNames)
                    if (!string.IsNullOrEmpty(roomName)) ours.Add(roomName);
            }
            if (ours.Count == 0) return;

            int before = available.Count;
            available.RemoveAll(rd => rd != null && rd.m_prefab.IsValid && ours.Contains(rd.m_prefab.Name));
            int removed = before - available.Count;
            if (removed > 0)
                Debug.Log($"[FiresCore] stripped {removed} Fires room(s) from foreign dungeon '{generator.gameObject.name}' " +
                          $"(available {before} -> {available.Count}) — our rooms only generate in our own dungeons.");
        }

        private static void InjectOurRooms(DungeonGenerator generator, DungeonSpec spec)
        {
            var held = HeldFor(spec);

            // Last-chance rebuild of the held RoomData if registration never ran (e.g. DungeonDB.Start fired before
            // the bundle loaded). Without this, clearing the list below would leave 0 rooms -> crash.
            if (!held.Exists(rd => rd != null && rd.m_prefab.IsValid))
                RebuildOurRooms(spec, held);

            // Re-ensure our rooms exist in DungeonDB for the save/reload hash lookup (EWD may have rebuilt m_rooms
            // from YAML and dropped ours). Cheap + idempotent.
            EnsureRoomsInDungeonDB(spec, held);

            var available = DungeonGenerator.m_availableRooms; // private static; reachable via the publicized asm.
            if (available == null) return;
            int hadBefore = available.Count;

            int validOurs = held.FindAll(rd => rd != null && rd.m_prefab.IsValid).Count;
            if (validOurs == 0)
            {
                // Never make it worse: if we somehow have no rooms to inject, leave whatever the original found.
                Debug.LogWarning($"{spec.LogTag} SetupAvailableRooms[{generator.gameObject.name}]: no valid rooms to inject; leaving available={hadBefore}.");
                return;
            }

            available.Clear();
            foreach (var roomData in held)
                if (roomData != null && roomData.m_prefab.IsValid) available.Add(roomData);

            int dbCount = DungeonDB.GetRooms()?.Count ?? -1;
            int present = CountOursInDb(held);
            Debug.Log($"{spec.LogTag} SetupAvailableRooms[{generator.gameObject.name}]: DungeonDB rooms={dbCount}, ours-in-DB={present}/{held.Count}, " +
                      $"availableBefore={hadBefore} -> availableAfter={available.Count} (theme={spec.NamedTheme}).");
        }

        private static void RebuildOurRooms(DungeonSpec spec, List<DungeonDB.RoomData> held)
        {
            held.Clear();
            foreach (string prefabName in spec.RoomPrefabNames)
            {
                GameObject prefab = spec.Assets?.Get(prefabName);
                if (prefab == null) continue;
                var roomComp = prefab.GetComponent<Room>();
                if (roomComp != null) roomComp.m_theme = spec.NamedTheme;
                held.Add(new DungeonDB.RoomData
                {
                    m_prefab = spec.Assets.SoftRefFor(prefab),
                    m_enabled = true,
                    m_theme = spec.NamedTheme,
                });
            }
            Debug.Log($"{spec.LogTag} rebuilt held room set ({held.Count}) from bundle prefabs.");
        }

        private static void EnsureRoomsInDungeonDB(DungeonSpec spec, List<DungeonDB.RoomData> held)
        {
            var dungeonDb = DungeonDB.instance;
            if (dungeonDb == null) return;
            var rooms = DungeonDB.GetRooms();
            if (rooms == null) return;
            var roomByHash = GetRoomByHash(dungeonDb);

            foreach (var roomData in held)
            {
                if (roomData == null || !roomData.m_prefab.IsValid) continue;
                int hash = roomData.m_prefab.Name.GetStableHashCode();
                if (roomByHash != null && roomByHash.ContainsKey(hash)) continue;
                rooms.Add(roomData);
                if (roomByHash != null) roomByHash[hash] = roomData;
            }
        }

        private static int CountOursInDb(List<DungeonDB.RoomData> held)
        {
            var rooms = DungeonDB.GetRooms();
            if (rooms == null) return -1;
            int count = 0;
            foreach (var roomData in held)
            {
                if (roomData == null || !roomData.m_prefab.IsValid) continue;
                if (rooms.Contains(roomData)) count++;
            }
            return count;
        }

        // ── ZoneSystem: place each spec's surface dungeon near spawn ──────────────

        [HarmonyPatch(typeof(ZoneSystem), "SetupLocations")]
        internal static class ZoneSystem_SetupLocations_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ZoneSystem __instance)
            {
                // Register EVERY enabled spec's location — not just world-gen (NearSpawnLocation) ones. A
                // door-/command-spawned dungeon still has its Location re-spawned on remote clients through the
                // replicated LocationProxy, and that resolves the Location by hash in m_locationsByHash (below).
                // Per-location detail is verbose-gated; the newly-registered set is summarized into ONE banner
                // below instead of one console line each (which, on Debug.Log, doubled via the stdout echo).
                List<string> registered = null;
                foreach (var spec in FiresDungeonRegistry.All)
                {
                    if (spec == null || !spec.IsEnabled()) continue;
                    try
                    {
                        string name = RegisterDungeonLocation(__instance, spec);
                        if (name != null)
                        {
                            if (registered == null) registered = new List<string>();
                            registered.Add(name);
                        }
                    }
                    catch (Exception ex) { Debug.LogError($"{spec.LogTag} location setup failed: {ex}"); }
                }
                EmitLocationSummary(registered);
            }
        }

        /// <summary>
        /// Registers a spec's dungeon location the way vanilla SetupLocations does, including the private
        /// m_locationsByHash index that vanilla only builds for built-in locations. Clients re-spawn a location
        /// proxy by hash; without the index they log "Missing location", never run the DungeonGenerator, and see
        /// the networked props without the static walls and floors. Idempotent on server and client. Returns the
        /// spec's name when a new hash was indexed, otherwise null.
        /// </summary>
        private static string RegisterDungeonLocation(ZoneSystem zoneSystem, DungeonSpec spec)
        {
            if (zoneSystem == null || zoneSystem.m_locations == null) return null;

            ZoneSystem.ZoneLocation loc = zoneSystem.m_locations.Find(l => l != null && l.m_prefabName == spec.CryptLocationPrefabName);
            bool isNew = loc == null;
            if (isNew)
            {
                // World-gen specs get the full placement descriptor; door-/command-spawned specs get the manual record.
                loc = spec.NearSpawnLocation != null ? BuildZoneLocation(spec) : BuildManualZoneLocation(spec);
                if (loc == null) return null;
            }
            else
            {
                // A rebuilt spec (section edit / re-register) swaps in a NEW Location clone and tears the old one down,
                // so an existing ZoneLocation may hold a now-dead SoftReference. Re-point it at the current clone.
                GameObject crypt = spec.Assets?.Get(spec.CryptLocationPrefabName);
                if (crypt != null) loc.m_prefab = spec.Assets.SoftRefFor(crypt);
            }

            if (!loc.m_prefab.IsValid)
            {
                // Never leave an enabled-but-invalid location in m_locations: 1.0's GetLocation(string) THROWS on
                // exactly that shape, taking down any vanilla name lookup that walks the list.
                if (!isNew) loc.m_enable = false;
                Debug.LogWarning($"{spec.LogTag} dungeon location prefab not valid at SetupLocations; " +
                                 "client proxy resolution will fail until the bundle loads + a reload.");
                return null;
            }
            loc.m_enable = true; // re-arm a location the guard above disabled on an earlier (invalid) pass
            if (isNew) zoneSystem.m_locations.Add(loc);

            // Vanilla forces m_prefabName = m_prefab.Name, then keys by Hash. CreateLocationProxy stamps the SAME
            // m_prefab.Name onto the proxy ZDO, so the client resolves by exactly this hash. Mirror it verbatim.
            loc.m_prefabName = loc.m_prefab.Name;
            int hash = loc.Hash;
            var byHash = GetLocationsByHash(zoneSystem);
            if (byHash == null)
            {
                Debug.LogError($"{spec.LogTag} could not access ZoneSystem.m_locationsByHash; " +
                               "client dungeon walls will not rebuild.");
                return null;
            }
            if (byHash.ContainsKey(hash)) return null;

            byHash[hash] = loc;
            if (FiresLogger.VerboseEnabled)
            {
                bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
                FiresLogger.LogInfo($"{spec.LogTag} registered dungeon location '{loc.m_prefabName}' (hash {hash}) into " +
                                    $"m_locationsByHash (server={isServer}); client LocationProxy can now rebuild room roots.");
            }
            return FriendlyName(spec);
        }

        // Extracts the readable name from a spec's LogTag: "[FiresDungeonMaster:Forest Crypt(vanilla)]" → "Forest
        // Crypt(vanilla)". Falls back to the crypt prefab name, then the bracket-stripped tag.
        private static string FriendlyName(DungeonSpec spec)
        {
            string tag = spec.LogTag ?? "";
            int colon = tag.IndexOf(':');
            int close = tag.IndexOf(']');
            if (colon >= 0 && close > colon) return tag.Substring(colon + 1, close - colon - 1);
            if (!string.IsNullOrEmpty(spec.CryptLocationPrefabName)) return spec.CryptLocationPrefabName;
            return tag.Trim('[', ']');
        }

        // One neat box summarizing the dungeon locations indexed this pass, instead of a console line each. Routes
        // through FiresUnifiedCore.Log (via LoadSummary) so it prints once — no stdout-echo duplicate — and the 🏰
        // title colors it through FiresLogColorPatch. Nothing new registered → nothing emitted (idempotent reruns).
        private static void EmitLocationSummary(List<string> names)
        {
            if (names == null || names.Count == 0) return;
            const int maxRows = 16;
            var lines = new List<string> { $"{names.Count} location(s) indexed" };
            int show = Math.Min(names.Count, maxRows);
            for (int i = 0; i < show; i++) lines.Add("• " + names[i]);
            if (names.Count > show) lines.Add($"…+{names.Count - show} more");
            LoadSummary.EmitMiniBox("🏰 DUNGEONS", lines.ToArray());
        }

        private static Dictionary<int, ZoneSystem.ZoneLocation> GetLocationsByHash(ZoneSystem zoneSystem)
        {
            if (_locationsByHashField == null)
                _locationsByHashField = typeof(ZoneSystem).GetField("m_locationsByHash", BindingFlags.NonPublic | BindingFlags.Instance);
            return _locationsByHashField?.GetValue(zoneSystem) as Dictionary<int, ZoneSystem.ZoneLocation>;
        }

        /// <summary>
        /// Register a single spec's dungeon Location into ZoneSystem NOW, if ZoneSystem is already live. Called from
        /// <see cref="FiresDungeonRegistry.Register"/> so a spec built AFTER world load (WorldStart on any peer, or an
        /// on-demand server generate) still gets its Location indexed in m_locationsByHash — the SetupLocations postfix
        /// only covers specs that existed at world load, and WorldStart fires after SetupLocations. No-op before
        /// ZoneSystem exists (the postfix covers that path). Idempotent.
        /// </summary>
        public static void EnsureLocationRegistered(DungeonSpec spec)
        {
            if (spec == null || !spec.IsEnabled()) return;
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return; // ZoneSystem not up yet; SetupLocations postfix will register it at world load.
            try { RegisterDungeonLocation(zoneSystem, spec); }
            catch (Exception ex) { Debug.LogError($"{spec.LogTag} EnsureLocationRegistered failed: {ex}"); }
        }

        /// <summary>Build the dungeon's ZoneLocation (same record world-gen and the manual spawn command use).</summary>
        public static ZoneSystem.ZoneLocation BuildZoneLocation(DungeonSpec spec)
        {
            GameObject crypt = spec.Assets?.Get(spec.CryptLocationPrefabName);
            if (crypt == null)
            {
                Debug.LogWarning($"{spec.LogTag} location prefab not loaded; cannot build ZoneLocation.");
                return null;
            }
            var nearSpawn = spec.NearSpawnLocation;
            if (nearSpawn == null) return null;

            return new ZoneSystem.ZoneLocation
            {
                m_prefabName = spec.CryptLocationPrefabName,
                m_prefab = spec.Assets.SoftRefFor(crypt),
                m_enable = true,
                m_biome = nearSpawn.Biome,
                m_biomeArea = nearSpawn.BiomeArea,
                m_quantity = nearSpawn.Quantity,
                m_unique = nearSpawn.Unique,
                m_prioritized = nearSpawn.Prioritized,
                m_centerFirst = nearSpawn.CenterFirst,
                m_minDistanceFromCenter = nearSpawn.MinDistanceFromCenter,
                m_maxDistanceFromCenter = nearSpawn.MaxDistanceFromCenter,
                m_exteriorRadius = nearSpawn.ExteriorRadius,
                m_interiorRadius = nearSpawn.InteriorRadius,
                m_clearArea = nearSpawn.ClearArea,
                m_randomRotation = nearSpawn.RandomRotation,
                m_minAltitude = nearSpawn.MinAltitude,
            };
        }

        /// <summary>
        /// Build a ZoneLocation for a MANUAL spawn at an explicit pos/rot (an entry door or a console command). Unlike
        /// <see cref="BuildZoneLocation"/> — which requires a NearSpawnLocation descriptor and is what gates world-gen —
        /// this ALWAYS builds from the loaded prefab: the manual SpawnLocation pipeline only needs m_prefab plus the
        /// terrain clear radii, not the world-gen placement rules (biome / quantity / distance). A spec that opts OUT of
        /// world-gen (NearSpawnLocation == null, e.g. FiresDungeonMaster's door-spawned types) can therefore still be
        /// placed on demand. Uses the spec's descriptor radii when present, else sane crypt defaults. Returns null only
        /// when the location prefab isn't loaded.
        /// </summary>
        public static ZoneSystem.ZoneLocation BuildManualZoneLocation(DungeonSpec spec)
        {
            GameObject crypt = spec.Assets?.Get(spec.CryptLocationPrefabName);
            if (crypt == null)
            {
                Debug.LogWarning($"{spec.LogTag} location prefab not loaded; cannot build ZoneLocation.");
                return null;
            }
            var nearSpawn = spec.NearSpawnLocation;
            return new ZoneSystem.ZoneLocation
            {
                m_prefabName = spec.CryptLocationPrefabName,
                m_prefab = spec.Assets.SoftRefFor(crypt),
                m_enable = true,
                m_biome = nearSpawn?.Biome ?? Heightmap.Biome.All,
                m_biomeArea = nearSpawn?.BiomeArea ?? Heightmap.BiomeArea.Everything,
                m_quantity = nearSpawn?.Quantity ?? 0,
                m_unique = nearSpawn?.Unique ?? false,
                m_prioritized = nearSpawn?.Prioritized ?? false,
                m_centerFirst = nearSpawn?.CenterFirst ?? false,
                m_minDistanceFromCenter = nearSpawn?.MinDistanceFromCenter ?? 0f,
                m_maxDistanceFromCenter = nearSpawn?.MaxDistanceFromCenter ?? 0f,
                m_exteriorRadius = nearSpawn?.ExteriorRadius ?? 16f,
                m_interiorRadius = nearSpawn?.InteriorRadius ?? 32f,
                m_clearArea = nearSpawn?.ClearArea ?? true,
                m_randomRotation = nearSpawn?.RandomRotation ?? false,
                m_minAltitude = nearSpawn?.MinAltitude ?? 1f,
            };
        }

        // ── manual spawn ─────────────────────────────────────────────────────────

        /// <summary>
        /// Place a fully working dungeon at <paramref name="pos"/> by running the SAME vanilla pipeline world-gen
        /// uses: ZoneSystem.SpawnLocation(loc, seed, pos, rot, Full, ...) via reflection. That instantiates the
        /// ZNetView pieces (Portal teleports, DG) as ZDOs, runs Location.Awake (interior env box) and
        /// DungeonGenerator.Generate (rooms). Falls back to a direct prefab Instantiate + DG.Generate if the
        /// reflected method can't be found. Fires <see cref="DungeonSpec.OnDungeonGenerated"/> after a successful
        /// spawn. Returns a short status string. Server-side only.
        /// </summary>
        public static string SpawnDungeonAt(DungeonSpec spec, Vector3 pos, Quaternion rot)
        {
            if (spec == null) return "no dungeon spec.";
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return "ZoneSystem not ready.";

            ZoneSystem.ZoneLocation loc =
                zoneSystem.m_locations?.Find(l => l != null && l.m_prefabName == spec.CryptLocationPrefabName)
                ?? BuildManualZoneLocation(spec);
            if (loc == null || loc.m_prefab.IsValid == false) return "dungeon ZoneLocation unavailable (bundle not loaded?).";

            // Anchor the interior at the ZONE CENTRE exactly like vanilla when the spec opts in. Vanilla Location.Awake
            // instantiates the interior env box at (zoneCentre.xz, y+5000) and DungeonGenerator.Generate centres its
            // placement Bounds(m_zoneCenter, m_zoneSize) on the zone centre — so the DG must land there for the REAL
            // (unballooned) m_zoneSize to fill the zone symmetrically. Spawning at the door's arbitrary XZ left the DG
            // off-centre → rooms clipped on one side (which the old 256 m_zoneSize balloon "fixed" by over-generating).
            if (spec.AnchorInteriorToZoneCenter)
            {
                Vector3 requestedPosition = pos;
                Vector3 zoneCentre = ZoneSystem.GetZonePos(ZoneSystem.GetZone(pos));
                pos = new Vector3(zoneCentre.x, pos.y, zoneCentre.z);
                Debug.Log($"[FDM-PLACE] {spec.LogTag} SpawnDungeonAt zone-centre snap: inPos={requestedPosition} " +
                          $"zone={ZoneSystem.GetZone(requestedPosition)} zoneCentre={zoneCentre} -> spawnPos={pos} " +
                          $"(shifted XZ by {new Vector2(pos.x - requestedPosition.x, pos.z - requestedPosition.z).magnitude:F2}m). " +
                          $"DG should land at spawnPos+DGlocal; expect DG world XZ == zoneCentre XZ if DG local XZ~0.");
            }

            int seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);

            // Primary path: the vanilla private SpawnLocation(loc, seed, pos, rot, Full, List<GameObject>).
            // Resolved by NAME and invoked with an arg array sized to the live parameter list: 1.0 appended a
            // trailing 'bool cheated = false', and an exact 6-type lookup returned null — which silently sent every
            // manual spawn down the degraded direct-instantiate fallback below since the 1.0 update.
            try
            {
                if (_spawnLocationMethod == null)
                    _spawnLocationMethod = AccessTools.Method(typeof(ZoneSystem), "SpawnLocation");
                if (_spawnLocationMethod != null)
                {
                    var pars = _spawnLocationMethod.GetParameters();
                    var args = new object[pars.Length];
                    args[0] = loc; args[1] = seed; args[2] = pos; args[3] = rot;
                    args[4] = ZoneSystem.SpawnMode.Full; args[5] = new List<GameObject>();
                    for (int i = 6; i < pars.Length; i++)
                        args[i] = pars[i].HasDefaultValue ? pars[i].DefaultValue : null;
                    _manualSpawnDepth++;
                    try { _spawnLocationMethod.Invoke(zoneSystem, args); }
                    finally { _manualSpawnDepth--; }
                    int rooms = LogGeneratedRooms(spec, pos);
                    Debug.Log($"{spec.LogTag} spawn: generated, placed {rooms} rooms (full pipeline).");
                    spec.OnDungeonGenerated?.Invoke();
                    return $"dungeon spawned at {pos}; placed {rooms} rooms.";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{spec.LogTag} reflected SpawnLocation failed ({ex.Message}); falling back to direct instantiate.");
            }

            // Fallback: instantiate the location prefab directly (runs Location.Awake) + drive the DG ourselves.
            try
            {
                GameObject prefab = spec.Assets?.Get(spec.CryptLocationPrefabName);
                if (prefab == null) return "dungeon prefab not loaded.";
                _manualSpawnDepth++;
                try
                {
                    GameObject go = UnityEngine.Object.Instantiate(prefab, pos, rot);
                    var generator = go.GetComponentInChildren<DungeonGenerator>();
                    if (generator != null) generator.Generate(ZoneSystem.SpawnMode.Full);
                }
                finally { _manualSpawnDepth--; }
                int rooms = LogGeneratedRooms(spec, pos);
                Debug.Log($"{spec.LogTag} spawn: generated, placed {rooms} rooms (fallback instantiate).");
                spec.OnDungeonGenerated?.Invoke();
                return $"dungeon spawned (fallback) at {pos}; placed {rooms} rooms.";
            }
            catch (Exception ex)
            {
                return "spawn failed: " + ex.Message;
            }
        }

        // After a spawn, find the freshly-placed DG (at pos + ~(0,5000,0)) and report how many rooms it saved.
        private static int LogGeneratedRooms(DungeonSpec spec, Vector3 spawnPos)
        {
            try
            {
                Vector3 generatorPosition = spawnPos + new Vector3(0f, 5000f, 0f);
                DungeonGenerator best = null;
                float bestSqr = 64f * 64f; // within one zone of where the DG should be
                foreach (var generator in UnityEngine.Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None))
                {
                    if (generator == null) continue;
                    float sqr = (generator.transform.position - generatorPosition).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; best = generator; }
                }
                if (best == null) { Debug.Log($"{spec.LogTag} spawn: no DG found near spawn to count rooms."); return -1; }

                int rooms = -1;
                var netView = best.GetComponent<ZNetView>();
                ZDO zdo = (netView != null && netView.IsValid()) ? netView.GetZDO() : null;
                if (zdo != null && zdo.GetByteArray(ZDOVars.s_roomData, out byte[] data) && data != null && data.Length >= 4)
                    rooms = BitConverter.ToInt32(data, 0); // DungeonGenerator.Save writes the room count as the first int

                // DECISIVE placement confirmation: is the DG actually AT the zone centre, and do the placed rooms sit
                // SYMMETRICALLY around it (vanilla) rather than clipped to one side (off-centre)? offCentreXZ ~0 proves
                // the zone-centre anchor worked; a room AABB centre far from the DG in XZ proves it did not.
                Vector3 dgWorld = best.transform.position;
                Vector3 zoneCenter = ZoneSystem.GetZonePos(ZoneSystem.GetZone(dgWorld));
                float offCentreXZ = new Vector2(dgWorld.x - zoneCenter.x, dgWorld.z - zoneCenter.z).magnitude;

                var roomComps = best.GetComponentsInChildren<Room>();
                if (roomComps.Length > 0)
                {
                    var bounds = new Bounds(dgWorld, Vector3.zero);
                    int colliders = 0;
                    foreach (var room in roomComps)
                    {
                        if (room == null) continue;
                        foreach (var boxCollider in room.GetComponentsInChildren<BoxCollider>())
                        {
                            if (boxCollider == null) continue;
                            bounds.Encapsulate(boxCollider.bounds); colliders++;
                        }
                    }
                    Vector2 aabbOff = new Vector2(bounds.center.x - dgWorld.x, bounds.center.z - dgWorld.z);
                    Debug.Log($"[FDM-PLACE] {spec.LogTag} PLACEMENT CHECK: DG world={dgWorld} zoneCentre={zoneCenter} " +
                              $"DGoffZoneCentreXZ={offCentreXZ:F2}m | m_zoneCenter={best.m_zoneCenter} m_zoneSize={best.m_zoneSize} | " +
                              $"rooms={roomComps.Length} colliders={colliders} roomAABBcentre={bounds.center} size={bounds.size} " +
                              $"roomAABBoffDGxz={aabbOff.magnitude:F2}m. WANT: DGoffZoneCentreXZ~0 AND roomAABBoffDGxz small/symmetric.");
                }

                Debug.Log($"{spec.LogTag} spawn: DG at {dgWorld} saved {rooms} room(s) (theme {spec.NamedTheme}).");
                return rooms;
            }
            catch (Exception ex) { Debug.LogWarning($"{spec.LogTag} room-count log failed: {ex.Message}"); return -1; }
        }
    }
}
