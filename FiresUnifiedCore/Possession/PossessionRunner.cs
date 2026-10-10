using System;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>Registers the possession and summon RPCs at ZNet.Start and drives their per-frame and server ticks.</summary>
    internal sealed class PossessionRunner : MonoBehaviour
    {
        private static PossessionRunner s_instance;
        private float _nextServerTick;

        private void Awake() { s_instance = this; DontDestroyOnLoad(gameObject); }
        private void OnDestroy() { if (s_instance == this) s_instance = null; }

        private void Update()
        {
            try
            {
                PossessionClient.Update();
                FiresCore.Summon.SummonClient.Update();
                if (Time.time >= _nextServerTick && ZNet.instance != null && ZNet.instance.IsServer())
                {
                    _nextServerTick = Time.time + 1f;
                    PossessionServer.Tick();
                    FiresCore.Summon.SummonServer.Tick();
                }
            }
            catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "tick failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_Possession
        {
            private static void Postfix()
            {
                if (ZRoutedRpc.instance == null) return;
                PossessionServer.RegisterRpcs();
                PossessionClient.RegisterRpcs();
                FiresCore.Summon.SummonServer.RegisterRpcs();
                PossessionEngineBridge.InstallEarly();
                if (s_instance == null) new GameObject("FiresPossession").AddComponent<PossessionRunner>();
            }
        }
    }
}
