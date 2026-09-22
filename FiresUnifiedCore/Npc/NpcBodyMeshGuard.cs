using System;
using System.Collections.Generic;
using FiresCore.Async;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Catches Unity's render-time body-mesh rejection ("SkinnedMeshRenderer: Rendering stopped because the data
    /// for mesh ... does not match"), which leaves an NPC invisible. The bone-count check cannot predict it (the
    /// live bodyfem matches the baked rig's count but not its vertex layout) and Unity offers no API, so this
    /// listens for the message on the threaded log event (Unity raises it from its skinning work, so the
    /// main-thread event never delivers it), blacklists that mesh and rig pairing for the session, and swaps
    /// affected bodies to their best remaining candidate. Every body-mesh assignment site consults the blacklist.
    /// </summary>
    public static class NpcBodyMeshGuard
    {
        private const string ErrorPrefix = "SkinnedMeshRenderer: Rendering stopped because the data for mesh '";
        private const string GoMarker = "Game Object '";

        private static bool _installed;
        private static bool _healing;
        private static readonly HashSet<string> _rejected = new HashSet<string>();
        private static readonly HashSet<string> _logged = new HashSet<string>();
        private static readonly HashSet<string> _healed = new HashSet<string>();
        private static readonly HashSet<string> _queued = new HashSet<string>();

        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            // Unity reports the rejection from its skinning work, off the main thread, so the main-thread-only event
            // never sees it; healing touches the scene and has to go back. Warm the dispatcher from here, which is the
            // main thread, because it builds its host GameObject on first use.
            MainThreadDispatcher.Enqueue(() => { });
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        public static bool IsRejected(Mesh mesh, SkinnedMeshRenderer smr)
            => mesh != null && smr != null && _rejected.Contains(PairKey(mesh, smr));

        public static void MarkRejected(Mesh mesh, SkinnedMeshRenderer smr)
        {
            if (mesh != null && smr != null) _rejected.Add(PairKey(mesh, smr));
        }

        /// <summary>Bindpose-count compatibility — the cheap pre-assign check. Layout-level rejection
        /// (same count, different vertex data) is only catchable post-hoc via the tripwire.</summary>
        public static bool CanSkin(Mesh mesh, SkinnedMeshRenderer smr)
        {
            if (mesh == null || smr == null) return false;
            int bones = smr.bones != null ? smr.bones.Length : 0;
            var bind = mesh.bindposes;
            int binds = bind != null ? bind.Length : 0;
            return !(bones > 0 && binds > 0 && bones != binds);
        }

        /// <summary>True when this mesh may be assigned to this renderer: the skeleton can skin it and
        /// Unity hasn't already rejected the pairing this session.</summary>
        public static bool IsAssignable(Mesh mesh, SkinnedMeshRenderer smr)
            => CanSkin(mesh, smr) && !IsRejected(mesh, smr);

        internal static void LogOnce(string key, string message)
        {
            if (_logged.Add(key)) Debug.Log(message);
        }

        private static string PairKey(Mesh mesh, SkinnedMeshRenderer skinnedRenderer)
        {
            int bones = skinnedRenderer.bones != null ? skinnedRenderer.bones.Length : 0;
            return mesh.GetInstanceID() + "|" + bones;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error || _healing || condition == null) return;
            if (!condition.StartsWith(ErrorPrefix, StringComparison.Ordinal)) return;

            int meshEnd = condition.IndexOf('\'', ErrorPrefix.Length);
            if (meshEnd < 0) return;
            string meshName = condition.Substring(ErrorPrefix.Length, meshEnd - ErrorPrefix.Length);

            int goStart = condition.IndexOf(GoMarker, meshEnd, StringComparison.Ordinal);
            if (goStart < 0) return;
            goStart += GoMarker.Length;
            int goEnd = condition.IndexOf('\'', goStart);
            if (goEnd < 0) return;
            string goName = condition.Substring(goStart, goEnd - goStart);

            // One heal in flight per pairing: the message repeats every frame until the body renders again, and each
            // heal sweeps every NPC in the scene.
            string queueKey = meshName + "|" + goName;
            lock (_queued)
            {
                if (!_queued.Add(queueKey)) return;
            }

            MainThreadDispatcher.Enqueue(() =>
            {
                lock (_queued) { _queued.Remove(queueKey); }
                if (_healing) return;
                _healing = true;
                try { HealAll(meshName, goName); }
                catch { }
                finally { _healing = false; }
            });
        }

        private static void HealAll(string meshName, string goName)
        {
            string pair = meshName + "|" + goName;
            int healed = 0;
            foreach (var npcVis in UnityEngine.Object.FindObjectsByType<NpcVisEquipment>(FindObjectsSortMode.None))
            {
                if (npcVis != null && npcVis.HealRejectedBodyMesh(meshName, goName)) healed++;
            }
            if (healed > 0)
            {
                _healed.Add(pair);
                return;
            }
            // Unity repeats the message every frame until the body renders again, so the swap that already healed this
            // pairing must not then be reported as a miss.
            if (!_healed.Contains(pair))
                LogOnce("miss|" + meshName + "|" + goName,
                    $"[NpcBodyMeshGuard] '{meshName}' on '{goName}' was rejected by Unity but no NpcVisEquipment body matched — not one of our NPC rigs; leaving it alone.");
        }
    }
}
