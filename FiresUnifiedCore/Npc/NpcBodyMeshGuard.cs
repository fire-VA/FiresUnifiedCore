using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Runtime tripwire for Unity's skin-time body-mesh rejection: "SkinnedMeshRenderer: Rendering
    /// stopped because the data for mesh 'X' on Game Object 'Y' does not match the expected mesh data
    /// size and vertex stride."
    ///
    /// The bindpose-count guard cannot catch this class of failure. The live game's 'bodyfem' carries
    /// the same bindpose COUNT as the baked NPC rig (53), yet its vertex layout has drifted from what
    /// the ripped rig's renderer can skin, so Unity rejects the pairing at render time, stops rendering
    /// the body (invisible NPC) and logs the error once per render attempt. Unity exposes no API to
    /// query that verdict up front — the only reliable detector is the error message itself.
    ///
    /// So: listen for the message, blacklist the exact (mesh instance, rig bone count) pairing that was
    /// rejected, and swap every affected NPC body back to its best non-rejected candidate. Assignment
    /// sites (NpcVisEquipment.TryAssignBodyMesh, the VisEquipment.UpdateBaseModel prefix,
    /// NpcTemplateAppearance.DressFromLook) consult the blacklist so a rejected pairing is never
    /// assigned again within the session.
    /// </summary>
    public static class NpcBodyMeshGuard
    {
        private const string ErrorPrefix = "SkinnedMeshRenderer: Rendering stopped because the data for mesh '";
        private const string GoMarker = "Game Object '";

        private static bool _installed;
        private static bool _healing;
        private static readonly HashSet<string> _rejected = new HashSet<string>();
        private static readonly HashSet<string> _logged = new HashSet<string>();

        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            Application.logMessageReceived += OnLogMessage;
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

        private static string PairKey(Mesh mesh, SkinnedMeshRenderer smr)
        {
            int bones = smr.bones != null ? smr.bones.Length : 0;
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

            _healing = true;
            try { HealAll(meshName, goName); }
            catch { }
            finally { _healing = false; }
        }

        private static void HealAll(string meshName, string goName)
        {
            int healed = 0;
            foreach (var nve in UnityEngine.Object.FindObjectsOfType<NpcVisEquipment>())
            {
                if (nve != null && nve.HealRejectedBodyMesh(meshName, goName)) healed++;
            }
            if (healed == 0)
                LogOnce("miss|" + meshName + "|" + goName,
                    $"[NpcBodyMeshGuard] '{meshName}' on '{goName}' was rejected by Unity but no NpcVisEquipment body matched — not one of our NPC rigs; leaving it alone.");
        }
    }
}
