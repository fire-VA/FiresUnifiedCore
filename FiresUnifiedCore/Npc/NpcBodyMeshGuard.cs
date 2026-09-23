using System;
using System.Collections.Generic;
using FiresCore.Async;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps NPC bodies off meshes Unity refuses to skin ("SkinnedMeshRenderer: Rendering stopped because the data
    /// for mesh ... does not match"), which leaves an NPC invisible. The bone-count check cannot predict it: the
    /// baked bodyfem has the rig's bone and vertex counts, yet Unity rejects it on the rig and accepts the vanilla
    /// Player's bodyfem. So a mesh whose vertex layout differs from its vanilla namesake's is refused up front.
    /// Anything that still slips through is caught on the threaded log event (Unity raises the message from its
    /// skinning work, so the main-thread event never delivers it): the pairing is blacklisted for the session and
    /// affected bodies swap to their best remaining candidate. Every body-mesh assignment site consults
    /// <see cref="IsAssignable"/>.
    /// </summary>
    public static class NpcBodyMeshGuard
    {
        private const string ErrorPrefix = "SkinnedMeshRenderer: Rendering stopped because the data for mesh '";
        private const string GoMarker = "Game Object '";
        private const string PlayerPrefabName = "Player";

        private static bool _installed;
        private static bool _healing;
        private static readonly HashSet<(int Mesh, int Bones)> _rejected = new HashSet<(int, int)>();
        private static readonly Dictionary<int, bool> _matchesVanillaLayout = new Dictionary<int, bool>();
        private static readonly Dictionary<int, string> _layoutWhenJudged = new Dictionary<int, string>();
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

        /// <summary>True when this mesh may be assigned to this renderer: the skeleton can skin it, its vertex layout
        /// matches the vanilla Player mesh of the same name, and Unity hasn't already rejected the pairing this
        /// session.</summary>
        public static bool IsAssignable(Mesh mesh, SkinnedMeshRenderer smr)
            => CanSkin(mesh, smr) && !IsRejected(mesh, smr) && MatchesVanillaLayout(mesh);

        internal static void LogOnce(string key, string message)
        {
            if (_logged.Add(key)) Debug.Log(message);
        }

        private static (int, int) PairKey(Mesh mesh, SkinnedMeshRenderer skinnedRenderer)
        {
            int bones = skinnedRenderer.bones != null ? skinnedRenderer.bones.Length : 0;
            return (mesh.GetInstanceID(), bones);
        }

        /// <summary>
        /// False when the vanilla Player has a body mesh of the same name whose vertex layout differs. That mesh skins
        /// on the Player rig, so the difference is what gets ours rejected. Judged once per mesh after ZNetScene loads.
        /// </summary>
        private static bool MatchesVanillaLayout(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (_matchesVanillaLayout.TryGetValue(id, out bool matches)) return matches;

            var player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(PlayerPrefabName) : null;
            if (player == null) return true;
            var vanilla = FindModelMesh(player.GetComponent<VisEquipment>(), mesh.name);

            string ours = LayoutSignature(mesh);
            _layoutWhenJudged[id] = ours;
            matches = vanilla == null || ReferenceEquals(vanilla, mesh) || LayoutSignature(vanilla) == ours;
            _matchesVanillaLayout[id] = matches;
            if (!matches)
                Debug.Log($"[NpcBodyMeshGuard] '{mesh.name}' has a different vertex layout from the vanilla Player's " +
                          $"(ours: {ours} | vanilla: {LayoutSignature(vanilla)}); NPC bodies use the vanilla mesh.");
            return matches;
        }

        /// <summary>A rejected mesh's layout now, against the layout it had when first judged. A difference means
        /// something rewrote the shared mesh after it was assigned.</summary>
        internal static string DescribeRejectedLayout(Mesh mesh)
        {
            string now = LayoutSignature(mesh) + ", blendshapes " + mesh.blendShapeCount + ", readable " + mesh.isReadable;
            if (!_layoutWhenJudged.TryGetValue(mesh.GetInstanceID(), out string judged)) return $"layout {now} (never judged)";
            return now.StartsWith(judged, StringComparison.Ordinal)
                ? $"layout {now} (unchanged since judged)"
                : $"layout CHANGED since judged: was {judged} | now {now}";
        }

        private static Mesh FindModelMesh(VisEquipment playerVis, string meshName)
        {
            if (playerVis == null || playerVis.m_models == null) return null;
            foreach (var model in playerVis.m_models)
            {
                if (model != null && model.m_mesh != null && model.m_mesh.name == meshName) return model.m_mesh;
            }
            return null;
        }

        private static string LayoutSignature(Mesh mesh)
        {
            var signature = new System.Text.StringBuilder();
            signature.Append("verts ").Append(mesh.vertexCount).Append(", skin ").Append(mesh.skinWeightBufferLayout);
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++)
                signature.Append(", stream").Append(stream).Append(' ').Append(mesh.GetVertexBufferStride(stream)).Append('B');
            foreach (var attribute in mesh.GetVertexAttributes())
                signature.Append(", ").Append(attribute.attribute).Append(' ').Append(attribute.format).Append('x')
                    .Append(attribute.dimension).Append('@').Append(attribute.stream);
            return signature.ToString();
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
