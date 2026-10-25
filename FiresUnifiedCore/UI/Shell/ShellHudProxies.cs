// Every HUD element movable: vanilla HUD pieces that are SPAWNED per event (a boss bar per boss, unlock toasts,
// the biome-found banner) cannot be registered once. Each gets an invisible SLOT rect under the same parent with the prefab's own anchors
// and size, registered as a dock element (outline + name in layout mode, drag / dock, saved). Every LateUpdate after the dock's reflow
// each live instance is put at its own vanilla spot plus the slot's move (screen px), so vanilla's per-slot offsets (unlock toasts stack
// by m_maxUnlockMsgSpace) are kept. Boss bars also stack downward (16 px gap at 1080p): vanilla draws two bosses on top of each other.
// Nothing moves while the override is OFF or the HUD is hidden (Hud.SetVisible moves the whole root offscreen).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI.Dock;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    internal static class ShellHudProxies
    {
        private sealed class Proxy
        {
            public string Id, Label; public bool Stack;
            public Func<Transform> Parent; public Func<GameObject> Prefab; public Func<IEnumerable<GameObject>> Instances;
            public RectTransform Slot; public Vector2 SlotOrigin;
            public readonly Dictionary<int, Vector2> Origin = new Dictionary<int, Vector2>();
        }

        private static readonly FieldInfo F_unlock = AccessTools.Field(typeof(MessageHud), "m_unlockMessages");
        private static readonly FieldInfo F_biome = AccessTools.Field(typeof(MessageHud), "m_biomeMsgInstance");
        private static readonly FieldInfo F_huds = AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static FieldInfo s_fGui, s_fChar;

        private static readonly Proxy[] Proxies =
        {
            new Proxy { Id = "hud.boss", Label = "Boss bar", Stack = true,
                Parent = () => EnemyHud.instance != null && EnemyHud.instance.m_hudRoot != null ? EnemyHud.instance.m_hudRoot.transform : null,
                Prefab = () => EnemyHud.instance?.m_baseHudBoss, Instances = BossBars },
            new Proxy { Id = "hud.msg.unlock", Label = "Unlock messages",
                Parent = () => MessageHud.instance != null ? MessageHud.instance.transform : null,
                Prefab = () => MessageHud.instance?.m_unlockMsgPrefab, Instances = UnlockToasts },
            new Proxy { Id = "hud.msg.biome", Label = "Biome banner",
                Parent = () => MessageHud.instance != null ? MessageHud.instance.transform : null,
                Prefab = () => MessageHud.instance?.m_biomeFoundPrefab, Instances = BiomeBanner },
        };

        private static bool s_hooked;

        /// <summary>Creates + registers each slot once its parent exists (polled with the other HUD elements).</summary>
        internal static void Ensure()
        {
            if (!s_hooked) { DockService.AfterReflow += Tick; s_hooked = true; }
            foreach (var p in Proxies)
            {
                if (p.Slot != null) continue;
                Transform parent; GameObject prefab;
                try { parent = p.Parent(); prefab = p.Prefab(); } catch { continue; }
                var src = prefab != null ? prefab.transform as RectTransform : null;
                if (parent == null || src == null) continue;
                var go = new GameObject("FiresDockSlot_" + p.Id, typeof(RectTransform));
                go.layer = parent.gameObject.layer;
                var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
                rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot;
                rt.anchoredPosition = src.anchoredPosition; rt.sizeDelta = src.sizeDelta; rt.localScale = Vector3.one;
                p.Slot = rt;
                p.SlotOrigin = DockService.TopLeft(rt);   // the vanilla spot, before Register puts the slot at its saved spot
                DockService.Register(new DockElementOptions { Id = p.Id, Owner = "vanilla", Layer = "hud", Rect = rt, Label = p.Label, ShowGrip = false });
            }
        }

        /// <summary>Override OFF: slots removed, live instances back at their vanilla spots.</summary>
        internal static void Restore()
        {
            foreach (var p in Proxies)
            {
                if (p.Slot == null) continue;
                foreach (var go in SafeInstances(p))
                    if (go != null && p.Origin.TryGetValue(go.GetInstanceID(), out var o)) DockService.Place(go.transform as RectTransform, go.transform.localScale.x, o);
                DockService.Unregister(p.Id);
                UnityEngine.Object.Destroy(p.Slot.gameObject);
                p.Slot = null; p.Origin.Clear();
            }
        }

        private static void Tick()
        {
            if (DockService.IsHudHidden) return;
            foreach (var p in Proxies)
            {
                if (p.Slot == null) continue;
                Vector2 delta = DockService.TopLeft(p.Slot) - p.SlotOrigin;
                float stackY = 0f, gap = 16f * Screen.height / 1080f;
                var live = new HashSet<int>();
                foreach (var go in SafeInstances(p))
                {
                    if (go == null || !go.activeInHierarchy) continue;
                    var rt = go.transform as RectTransform;
                    if (rt == null) continue;
                    int id = go.GetInstanceID(); live.Add(id);
                    if (!p.Origin.TryGetValue(id, out var origin)) { origin = DockService.TopLeft(rt); p.Origin[id] = origin; }
                    var want = origin + delta + new Vector2(0f, p.Stack ? stackY : 0f);
                    if ((DockService.TopLeft(rt) - want).sqrMagnitude > 0.25f) DockService.MoveBy(rt, want - DockService.TopLeft(rt));
                    if (p.Stack) stackY += DockService.ScreenRect(rt).height + gap;
                }
                if (p.Origin.Count > live.Count) { var dead = new List<int>(); foreach (var k in p.Origin.Keys) if (!live.Contains(k)) dead.Add(k); foreach (var k in dead) p.Origin.Remove(k); }
            }
        }

        private static IEnumerable<GameObject> SafeInstances(Proxy p)
        {
            try { return p.Instances() ?? Array.Empty<GameObject>(); } catch { return Array.Empty<GameObject>(); }
        }

        private static IEnumerable<GameObject> UnlockToasts()
        {
            var arr = MessageHud.instance != null ? F_unlock?.GetValue(MessageHud.instance) as GameObject[] : null;
            return arr ?? Array.Empty<GameObject>();
        }

        private static IEnumerable<GameObject> BiomeBanner()
        {
            var go = MessageHud.instance != null ? F_biome?.GetValue(MessageHud.instance) as GameObject : null;
            return go != null ? new[] { go } : Array.Empty<GameObject>();
        }

        private static IEnumerable<GameObject> BossBars()
        {
            var list = new List<GameObject>();
            var dict = EnemyHud.instance != null ? F_huds?.GetValue(EnemyHud.instance) as IDictionary : null;
            if (dict == null) return list;
            foreach (var v in dict.Values)
            {
                if (v == null) continue;
                if (s_fGui == null) { s_fGui = AccessTools.Field(v.GetType(), "m_gui"); s_fChar = AccessTools.Field(v.GetType(), "m_character"); }
                var ch = s_fChar?.GetValue(v) as Character;
                if (ch == null || !ch.IsBoss()) continue;
                if (s_fGui?.GetValue(v) is GameObject gui && gui != null) list.Add(gui);
            }
            return list;
        }
    }
}
