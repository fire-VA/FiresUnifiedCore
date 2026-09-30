using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The one place the Fires family reads Norger's Vedr (seasons). No reference to Norger.Vedr.dll and no hard
    /// dependency: Vedr is found through BepInEx at runtime and its public Vedr.VedrApi is bound once into delegates,
    /// so each query is a plain delegate call. With Vedr absent every read answers "not ready / summer / no snow",
    /// events never fire, and registrations are kept and handed over if Vedr appears later.
    /// </summary>
    public static class SeasonBridge
    {
        private const string VedrGuid = "Norger.Vedr";
        private const string ApiTypeName = "Vedr.VedrApi";
        private const int MinApiVersion = 3;
        private const int FreezesLakesApiVersion = 4;
        private const int ScarDrawerApiVersion = 5;
        private const int CrackDrawerApiVersion = 6;

        private delegate bool CrackQuery(float x, float z, out float signedDistance, out float halfWidth, out float depth);
        private const float RetrySeconds = 5f;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        private static bool _bound;
        private static bool _gaveUp;
        private static float _nextProbe;

        private static Func<bool> _isReady, _isWinter, _isThawing, _freezesLakes;
        private static Func<int> _seasonIndex, _stageIndex, _stateVersion, _snowTerrainMask, _snowBuildingMask, _winterBiomeMask;
        private static Func<float> _snowAmount, _pieceSnowBuildup, _seasonProgressSmooth;
        private static Func<Vector3, bool> _isLakeIceAt, _isGroundScarredAt;
        private static Func<float, float, float, float, bool> _isGroundScarredIn;
        private static Action<Func<Vector3, bool>> _registerGroundOwner, _registerWaterOwner;
        private static Action<Func<Vector3, bool>, Func<Vector3, bool>> _registerGroundScarDrawer;
        private static Action<Func<Vector3, bool>> _registerGroundCrackDrawer;
        private static CrackQuery _groundCrackAt;
        private static Action<Func<IEnumerable<Material>>> _registerSeasonalMaterials;

        private static readonly List<Func<Vector3, bool>> PendingGroundOwners = new List<Func<Vector3, bool>>();
        private static readonly List<Func<Vector3, bool>> PendingWaterOwners = new List<Func<Vector3, bool>>();
        private static readonly List<Func<Vector3, bool>> PendingCrackDrawers = new List<Func<Vector3, bool>>();
        private static readonly List<(Func<Vector3, bool> draws, Func<Vector3, bool> settled)> PendingScarDrawers =
            new List<(Func<Vector3, bool> draws, Func<Vector3, bool> settled)>();
        private static readonly List<Func<IEnumerable<Material>>> PendingMaterials = new List<Func<IEnumerable<Material>>>();

        /// <summary>
        /// Season or stage changed, the snow biome masks changed, or the spring thaw started or ended. Never on a plain
        /// day tick. Args: season index (0 Spring .. 3 Winter, -1 not ready), stage index.
        /// </summary>
        public static event Action<int, int> VisualsChanged;

        /// <summary>Temporary ground damage formed, changed or healed inside an XZ rectangle (minX, minZ, maxX, maxZ).</summary>
        public static event Action<float, float, float, float> GroundScarsChanged;

        public static bool IsAvailable
        {
            get
            {
                EnsureBound();
                return _bound;
            }
        }

        public static bool IsReady => IsAvailable && _isReady();
        public static bool IsWinter => IsAvailable && _isWinter();
        public static bool IsThawing => IsAvailable && _isThawing();
        public static int SeasonIndex => IsAvailable ? _seasonIndex() : -1;
        public static int StageIndex => IsAvailable ? _stageIndex() : -1;
        public static int StateVersion => IsAvailable ? _stateVersion() : 0;
        public static float SeasonProgressSmooth => IsAvailable ? _seasonProgressSmooth() : 0f;

        /// <summary>Heightmap.Biome flags whose ground Vedr draws as snow right now (0 outside winter).</summary>
        public static int SnowTerrainBiomeMask => IsAvailable ? _snowTerrainMask() : 0;

        /// <summary>Heightmap.Biome flags whose pieces collect vanilla Deep North snow right now.</summary>
        public static int SnowBuildingBiomeMask => IsAvailable ? _snowBuildingMask() : 0;

        /// <summary>Heightmap.Biome flags that are wintry for gameplay whenever it is winter.</summary>
        public static int WinterBiomeMask => IsAvailable ? _winterBiomeMask() : 0;

        /// <summary>0 outside winter, ramping to 1 over the first day of winter and back to 0 over the thaw.</summary>
        public static float SnowAmount01 => IsAvailable ? _snowAmount() : 0f;

        /// <summary>WearNTear.m_snowBuildup an exposed, unheated piece in a snow building biome converges to right now.</summary>
        public static float PieceSnowBuildup => IsAvailable ? _pieceSnowBuildup() : 0f;

        public static bool IsSnowTerrainBiome(Heightmap.Biome biome) => (SnowTerrainBiomeMask & (int)biome) != 0;

        public static bool IsSnowBuildingBiome(Heightmap.Biome biome) => (SnowBuildingBiomeMask & (int)biome) != 0;

        public static bool IsLakeIceAt(Vector3 point) => IsAvailable && _isLakeIceAt(point);

        public static bool IsGroundScarredAt(Vector3 point) => IsAvailable && _isGroundScarredAt(point);

        /// <summary>Ground damage reaches into this XZ rectangle: forming, standing, healing or still being redrawn (main thread). Vedr older than API v5 answers false.</summary>
        public static bool IsGroundScarredIn(float minX, float minZ, float maxX, float maxZ) =>
            IsAvailable && _isGroundScarredIn(minX, minZ, maxX, maxZ);

        /// <summary>
        /// The open quake fissure nearest a world XZ point (main thread): signed metres from its centre line (positive on
        /// its left), its half-width and depth there, scaled by how far it has formed. False with no fissure there, or
        /// on Vedr older than API v6.
        /// </summary>
        public static bool GroundCrackAt(float x, float z, out float signedDistance, out float halfWidth, out float depth)
        {
            if (IsAvailable) return _groundCrackAt(x, z, out signedDistance, out halfWidth, out depth);
            signedDistance = halfWidth = depth = 0f;
            return false;
        }

        /// <summary>Vedr's "Freeze lakes and rivers" setting (the server's): a water owner freezes its water only while true. Vedr older than API v4 always answers true.</summary>
        public static bool FreezesLakes => IsAvailable && _freezesLakes();

        /// <summary>The caller owns the ground where this returns true: Vedr places no quake scar, crater, rockfall or lake ice there.</summary>
        public static void RegisterGroundOwner(Func<Vector3, bool> ownsGround) => Register(ownsGround, PendingGroundOwners, () => _registerGroundOwner);

        /// <summary>
        /// The caller builds its owned ground from the vanilla heightmap, which carries Vedr's scars: Vedr places quake
        /// scars, rockfalls and meteor craters where drawsScars is true and waits for groundSettled before lifting
        /// anything the ground rose over. Vedr older than API v5 keeps scars off owned ground, as before.
        /// </summary>
        public static void RegisterGroundScarDrawer(Func<Vector3, bool> drawsScars, Func<Vector3, bool> groundSettled) =>
            Register((drawsScars, groundSettled), PendingScarDrawers, () => ForwardScarDrawer);

        /// <summary>
        /// A scar drawer that cuts quake fissures in 3D itself from <see cref="GroundCrackAt"/>: Vedr leaves fissures out
        /// of the heightmap where drawsCracks is true (their dirt stays). Vedr older than API v6 keeps them in the heightmap.
        /// </summary>
        public static void RegisterGroundCrackDrawer(Func<Vector3, bool> drawsCracks) =>
            Register(drawsCracks, PendingCrackDrawers, () => _registerGroundCrackDrawer);

        /// <summary>The caller owns the water where this returns true and freezes it itself off <see cref="IsWinter"/>, only while <see cref="FreezesLakes"/>.</summary>
        public static void RegisterWaterOwner(Func<Vector3, bool> ownsWater) => Register(ownsWater, PendingWaterOwners, () => _registerWaterOwner);

        /// <summary>Extra foliage materials Vedr recolours with the seasons (read at each world load).</summary>
        public static void RegisterSeasonalMaterials(Func<IEnumerable<Material>> materials) => Register(materials, PendingMaterials, () => _registerSeasonalMaterials);

        /// <summary>Called by Core at load; every read also retries until Vedr is found or proves incompatible.</summary>
        public static void Bind() => EnsureBound(true);

        private static void Register<T>(T provider, List<T> pending, Func<Action<T>> forward)
        {
            EnsureBound();
            if (_bound) forward()(provider);
            else pending.Add(provider);
        }

        private static void EnsureBound(bool force = false)
        {
            if (_bound || _gaveUp) return;
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextProbe) return;
            _nextProbe = now + RetrySeconds;

            if (!Chainloader.PluginInfos.TryGetValue(VedrGuid, out var info) || info.Instance == null) return;
            Type api = info.Instance.GetType().Assembly.GetType(ApiTypeName, false);
            if (api == null) return;

            int apiVersion = api.GetField("ApiVersion", PublicStatic)?.GetRawConstantValue() is int v ? v : 0;
            if (apiVersion < MinApiVersion)
            {
                _gaveUp = true;
                FiresUnifiedCore.Log?.LogWarning($"[SeasonBridge] Vedr API v{apiVersion} is older than v{MinApiVersion}; seasons stay invisible to the Fires mods.");
                return;
            }

            try
            {
                BindMembers(api, apiVersion);
            }
            catch (Exception e)
            {
                _gaveUp = true;
                FiresUnifiedCore.Log?.LogWarning($"[SeasonBridge] Vedr found but its API could not be bound: {e.Message}");
                return;
            }

            _bound = true;
            HandOverPending();
            FiresUnifiedCore.Log?.LogInfo($"[SeasonBridge] Vedr API v{apiVersion} bound: seasons, snow masks and providers are live for the Fires mods.");
        }

        private static void BindMembers(Type api, int apiVersion)
        {
            _isReady = Getter<bool>(api, "IsReady");
            _isWinter = Getter<bool>(api, "IsWinter");
            _isThawing = Getter<bool>(api, "IsThawing");
            _seasonIndex = Getter<int>(api, "SeasonIndex");
            _stageIndex = Getter<int>(api, "StageIndex");
            _stateVersion = Getter<int>(api, "StateVersion");
            _snowTerrainMask = Getter<int>(api, "SnowTerrainBiomeMask");
            _snowBuildingMask = Getter<int>(api, "SnowBuildingBiomeMask");
            _winterBiomeMask = Getter<int>(api, "WinterBiomeMask");
            _snowAmount = Getter<float>(api, "SnowAmount01");
            _pieceSnowBuildup = Getter<float>(api, "PieceSnowBuildup");
            _seasonProgressSmooth = Getter<float>(api, "SeasonProgressSmooth");
            _freezesLakes = apiVersion >= FreezesLakesApiVersion ? Getter<bool>(api, "FreezesLakes") : () => true;
            _isLakeIceAt = Method<Func<Vector3, bool>>(api, "IsLakeIceAt");
            _isGroundScarredAt = Method<Func<Vector3, bool>>(api, "IsGroundScarredAt");
            bool scarDrawers = apiVersion >= ScarDrawerApiVersion;
            _isGroundScarredIn = scarDrawers ? Method<Func<float, float, float, float, bool>>(api, "IsGroundScarredIn") : (minX, minZ, maxX, maxZ) => false;
            _registerGroundScarDrawer = scarDrawers ? Method<Action<Func<Vector3, bool>, Func<Vector3, bool>>>(api, "RegisterGroundScarDrawer") : (draws, settled) => { };
            bool crackDrawers = apiVersion >= CrackDrawerApiVersion;
            _groundCrackAt = crackDrawers ? Method<CrackQuery>(api, "GroundCrackAt") : NoCrack;
            _registerGroundCrackDrawer = crackDrawers ? Method<Action<Func<Vector3, bool>>>(api, "RegisterGroundCrackDrawer") : draws => { };
            _registerGroundOwner = Method<Action<Func<Vector3, bool>>>(api, "RegisterGroundOwner");
            _registerWaterOwner = Method<Action<Func<Vector3, bool>>>(api, "RegisterWaterOwner");
            _registerSeasonalMaterials = Method<Action<Func<IEnumerable<Material>>>>(api, "RegisterSeasonalMaterials");
            api.GetEvent("VisualsChanged", PublicStatic).AddEventHandler(null, new Action<int, int>(OnVisualsChanged));
            api.GetEvent("GroundScarsChanged", PublicStatic).AddEventHandler(null, new Action<float, float, float, float>(OnGroundScarsChanged));
        }

        private static void HandOverPending()
        {
            foreach (Func<Vector3, bool> owner in PendingGroundOwners) _registerGroundOwner(owner);
            foreach (Func<Vector3, bool> owner in PendingWaterOwners) _registerWaterOwner(owner);
            foreach ((Func<Vector3, bool> draws, Func<Vector3, bool> settled) drawer in PendingScarDrawers) ForwardScarDrawer(drawer);
            foreach (Func<Vector3, bool> drawer in PendingCrackDrawers) _registerGroundCrackDrawer(drawer);
            foreach (Func<IEnumerable<Material>> source in PendingMaterials) _registerSeasonalMaterials(source);
            PendingGroundOwners.Clear();
            PendingWaterOwners.Clear();
            PendingScarDrawers.Clear();
            PendingCrackDrawers.Clear();
            PendingMaterials.Clear();
        }

        private static void ForwardScarDrawer((Func<Vector3, bool> draws, Func<Vector3, bool> settled) drawer) =>
            _registerGroundScarDrawer(drawer.draws, drawer.settled);

        private static bool NoCrack(float x, float z, out float signedDistance, out float halfWidth, out float depth)
        {
            signedDistance = halfWidth = depth = 0f;
            return false;
        }

        private static Func<T> Getter<T>(Type api, string property) =>
            (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), api.GetProperty(property, PublicStatic).GetGetMethod());

        private static T Method<T>(Type api, string name) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), api.GetMethod(name, PublicStatic));

        private static void OnVisualsChanged(int season, int stage) => RaiseEach(VisualsChanged, handler => handler(season, stage));

        private static void OnGroundScarsChanged(float minX, float minZ, float maxX, float maxZ) =>
            RaiseEach(GroundScarsChanged, handler => handler(minX, minZ, maxX, maxZ));

        /// <summary>A throwing Fires subscriber must not stop the others or reach back into Vedr.</summary>
        private static void RaiseEach<T>(T handlers, Action<T> invoke) where T : Delegate
        {
            if (handlers == null) return;
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                try
                {
                    invoke((T)subscriber);
                }
                catch (Exception e)
                {
                    FiresUnifiedCore.Log?.LogError($"[SeasonBridge] {subscriber.Method.DeclaringType?.FullName}.{subscriber.Method.Name} threw: {e}");
                }
            }
        }
    }
}
