using System;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Pieces
{
    /// <summary>
    /// 0.2.1077 (add-only, [saddles] for FAP 1.1.352 placed spawners; Fire's GO): an invisible, hammer-removable box on a piece, MOVED
    /// out of FiresWayshrines' WayshrineTriggerWire (1.2.12) so there is one implementation (same unit line mesh + material).
    ///  - <see cref="Attach"/> on a prefab template OR a live instance adds a child "HammerBox" with a trigger BoxCollider on
    ///    piece_nonsolid (in vanilla's m_removeRayMask / m_interactMask, so it hovers and hammer-removes through the root Piece +
    ///    ZNetView) and a line-mesh wire child (no shadows). Idempotent: a second Attach returns the existing component.
    ///  - Drawing: a ghost (no ZDO) always draws; a placed copy draws only when showPlacedWhileHammer is set AND the local player holds
    ///    the Hammer. Batch mode (dedicated server) = collider only, no visuals.
    ///  - Hammer state is event-driven: Humanoid.EquipItem / UnequipItem postfixes on the local player raise
    ///    <see cref="HammerInHandChanged"/> and keep <see cref="HammerInHand"/>; each box toggles its wire on the event.
    /// Debug: one line per Attach; Info: one line the first time the hammer event fires.
    /// </summary>
    public sealed class HammerBox : MonoBehaviour
    {
        private const string ChildName = "HammerBox", WireName = "HammerBoxWire";

        public static event Action<bool> HammerInHandChanged;
        public static bool HammerInHand { get; private set; }

        [SerializeField] private Vector3 m_size = Vector3.one, m_center = new Vector3(0f, 0.5f, 0f);
        [SerializeField] private Color m_color = new Color(1f, 0.78f, 0.30f, 1f);
        [SerializeField] private bool m_showPlacedWhileHammer;
        private MeshRenderer m_wire;
        private bool m_ghost, m_subscribed;

        public Vector3 Size => m_size;
        public Vector3 Center => m_center;

        /// <summary>Adds the box (or returns the one already there). Works on a prefab template or a live instance.</summary>
        public static Component Attach(GameObject go, Vector3 size, Vector3 center, Color color, bool showPlacedWhileHammer)
        {
            if (go == null) return null;
            var had = go.GetComponent<HammerBox>();
            if (had != null) return had;
            var box = go.AddComponent<HammerBox>();
            box.m_size = size; box.m_center = center; box.m_color = color; box.m_showPlacedWhileHammer = showPlacedWhileHammer;

            var col = go.transform.Find(ChildName);
            if (col == null)
            {
                var child = new GameObject(ChildName);
                child.transform.SetParent(go.transform, false);
                int layer = LayerMask.NameToLayer("piece_nonsolid");
                if (layer >= 0) child.layer = layer;
                var bc = child.AddComponent<BoxCollider>();
                bc.isTrigger = true; bc.size = size; bc.center = center;
            }
            bool live = go.scene.IsValid();
            FiresLogger.LogDebug($"[HammerBox] attached to {(live ? "instance" : "prefab")} '{go.name}': size {size}, center {center}, {(showPlacedWhileHammer ? "placed copy drawn while the hammer is held" : "placed copy never drawn")}{(Application.isBatchMode ? " (batch mode: collider only)" : "")}");
            return box;
        }

        private void Start()
        {
            if (Application.isBatchMode) { enabled = false; return; }   // headless: no visuals
            var nv = GetComponent<ZNetView>();
            m_ghost = nv == null || nv.GetZDO() == null;
            if (!m_ghost && !m_showPlacedWhileHammer) { enabled = false; return; }   // placed, never drawn (Wayshrines' look)
            BuildWire();
            if (!m_ghost) { HammerInHandChanged += OnHammer; m_subscribed = true; m_wire.enabled = HammerInHand; }
        }

        private void OnDestroy() { if (m_subscribed) HammerInHandChanged -= OnHammer; }

        private void OnHammer(bool held) { if (m_wire != null) m_wire.enabled = held; }

        private void BuildWire()
        {
            var t = transform.Find(WireName);
            GameObject child = t != null ? t.gameObject : new GameObject(WireName);   // NOT "_GhostOnly" (vanilla's ghost channel)
            child.transform.SetParent(transform, false);
            child.transform.localPosition = m_center - new Vector3(0f, m_size.y * 0.5f, 0f);   // the unit mesh spans y 0..1
            child.transform.localScale = m_size;
            var mf = child.GetComponent<MeshFilter>() ?? child.AddComponent<MeshFilter>();
            mf.sharedMesh = UnitBoxLineMesh();
            m_wire = child.GetComponent<MeshRenderer>() ?? child.AddComponent<MeshRenderer>();
            m_wire.sharedMaterial = WireMaterial(m_color);
            m_wire.shadowCastingMode = ShadowCastingMode.Off;
            m_wire.receiveShadows = false;
            m_wire.lightProbeUsage = LightProbeUsage.Off;
            m_wire.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        // ---- shared mesh + material (from WayshrineTriggerWire, unchanged; one material per colour) ----

        private static readonly System.Collections.Generic.Dictionary<Color, Material> s_mats = new System.Collections.Generic.Dictionary<Color, Material>();
        private static Material WireMaterial(Color c)
        {
            if (s_mats.TryGetValue(c, out var m) && m != null) return m;
            Shader sh = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            m = new Material(sh) { color = c, hideFlags = HideFlags.HideAndDontSave };
            s_mats[c] = m;
            return m;
        }

        private static Mesh s_lineMesh;
        private static Mesh UnitBoxLineMesh()
        {
            if (s_lineMesh != null) return s_lineMesh;
            var v = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(-0.5f, 1f, -0.5f), new Vector3(0.5f, 1f, -0.5f), new Vector3(0.5f, 1f, 0.5f), new Vector3(-0.5f, 1f, 0.5f),
            };
            var idx = new[] { 0, 1, 1, 2, 2, 3, 3, 0,  4, 5, 5, 6, 6, 7, 7, 4,  0, 4, 1, 5, 2, 6, 3, 7 };
            s_lineMesh = new Mesh { name = "HammerBoxWire", hideFlags = HideFlags.HideAndDontSave };
            s_lineMesh.vertices = v;
            s_lineMesh.SetIndices(idx, MeshTopology.Lines, 0);
            s_lineMesh.RecalculateBounds();
            return s_lineMesh;
        }

        // ---- the hammer state, event-driven ----

        private static bool s_infoSaid;
        private static PieceTable s_hammerTable;

        private static bool IsHammer(ItemDrop.ItemData item)
        {
            if (item?.m_shared?.m_buildPieces == null) return false;
            if (s_hammerTable == null && ObjectDB.instance != null)
            {
                var prefab = ObjectDB.instance.GetItemPrefab("Hammer");
                s_hammerTable = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            }
            return s_hammerTable != null ? item.m_shared.m_buildPieces == s_hammerTable : item.m_dropPrefab != null && item.m_dropPrefab.name == "Hammer";
        }

        private static void Recheck(Humanoid h)
        {
            if (h == null || h != Player.m_localPlayer) return;
            bool held = IsHammer(h.GetRightItem());
            if (held == HammerInHand) return;
            HammerInHand = held;
            if (!s_infoSaid) { s_infoSaid = true; FiresLogger.LogInfo($"[HammerBox] hammer-in-hand event live (first: {(held ? "equipped" : "unequipped")})"); }
            var ev = HammerInHandChanged;
            if (ev == null) return;
            foreach (Action<bool> d in ev.GetInvocationList())
            {
                try { d(held); } catch (Exception e) { FiresLogger.LogDebug($"[HammerBox] a HammerInHandChanged listener threw: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class Humanoid_EquipItem_HammerBox { private static void Postfix(Humanoid __instance) => Recheck(__instance); }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
        private static class Humanoid_UnequipItem_HammerBox { private static void Postfix(Humanoid __instance) => Recheck(__instance); }

        [HarmonyPatch(typeof(Player), "OnDestroy")]
        private static class Player_OnDestroy_HammerBox
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == null || __instance != Player.m_localPlayer || !HammerInHand) return;
                HammerInHand = false;
                try { HammerInHandChanged?.Invoke(false); } catch { }
            }
        }
    }
}
