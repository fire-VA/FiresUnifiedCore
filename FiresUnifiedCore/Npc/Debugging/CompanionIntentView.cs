using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.UI.ContextMenu;
using FiresCore.UI.Kit;
using FiresCore.UI.Shell.Kit;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Debugging
{
    /// <summary>
    /// Companion intent view (Fire 10-24: "we should have a way to view a companions intent and debug them"). Read-only: a small
    /// kit-style panel over the companion's head with what it is doing (behaviour, phase, command or idle), its target (name, position,
    /// distance), its move goal (who asked, distance, path points complete/partial, how long without progress, how far short), the
    /// last decision line the brain logged for it and the last "can't / skipped / timeout" line, plus a gold ground line to the goal.
    /// Toggle one: Alt+Shift right-click it ▸ "Show intent". All companions: Ctrl+F7 (config). No new AI; the lines are what the
    /// brains already log, captured by name from the log stream.
    /// </summary>
    internal static class CompanionIntentView
    {
        private static readonly HashSet<CompanionController> s_shown = new HashSet<CompanionController>();
        private static bool s_all;
        private static BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> s_key;

        internal static void BindConfig(BepInEx.Configuration.ConfigFile cfg)
        {
            s_key = cfg.Bind("Companions", "Intent view key", new BepInEx.Configuration.KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
                "Shows / hides the intent panel over every companion (what it is doing, its target and move goal, its last decision). One companion: Alt+Shift right-click it > Show intent.");
        }

        internal static bool IsShown(CompanionController c) => c != null && (s_all || s_shown.Contains(c));

        internal static void Toggle(CompanionController c)
        {
            if (c == null) return;
            if (!s_shown.Remove(c)) s_shown.Add(c);
            FiresCore.Logging.FiresLogger.LogDebug($"[Intent] {c.GetDisplayName()}: panel {(s_shown.Contains(c) ? "on" : "off")}");
        }

        // ---- the last decision lines, by companion name ---------------------------------------------------------------------
        private sealed class Capture : ILogListener
        {
            public LogLevel LogLevelFilter => LogLevel.All;
            public void Dispose() { }
            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (s_names.Count == 0 || e?.Data == null) return;
                    string line = e.Data.ToString();
                    if (line.Length < 8 || line.StartsWith("[Intent]")) return;
                    foreach (var n in s_names)
                    {
                        if (line.IndexOf(n, StringComparison.Ordinal) < 0) continue;
                        string clean = line.Replace("\n", " ").Replace("  ", " ");
                        if (clean.Length > 160) clean = clean.Substring(0, 157) + "...";
                        lock (s_last) { s_last[n] = (clean, Time.realtimeSinceStartup); if (IsProblem(clean)) s_lastWhy[n] = (clean, Time.realtimeSinceStartup); }
                        break;
                    }
                }
                catch { }
            }
        }

        private static bool IsProblem(string l)
        {
            string s = l.ToLowerInvariant();
            return s.Contains("can't") || s.Contains("cannot") || s.Contains("timeout") || s.Contains("skipp") || s.Contains("refus") || s.Contains("blocked") || s.Contains("short of") || s.Contains("no ") && s.Contains("found");
        }

        private static readonly List<string> s_names = new List<string>();
        private static readonly Dictionary<string, (string line, float at)> s_last = new Dictionary<string, (string, float)>();
        private static readonly Dictionary<string, (string line, float at)> s_lastWhy = new Dictionary<string, (string, float)>();
        private static Capture s_capture;

        // ---- panels -------------------------------------------------------------------------------------------------------
        private static Canvas s_canvas;
        private static readonly Dictionary<CompanionController, (RectTransform box, TextMeshProUGUI text, LineRenderer line)> s_panels = new Dictionary<CompanionController, (RectTransform, TextMeshProUGUI, LineRenderer)>();

        private static Canvas CanvasOf()
        {
            if (s_canvas != null) return s_canvas;
            var go = new GameObject("FiresIntentView");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_canvas = go.AddComponent<Canvas>(); s_canvas.renderMode = RenderMode.ScreenSpaceOverlay; s_canvas.sortingOrder = 40;
            var scaler = go.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920f, 1080f); scaler.matchWidthOrHeight = 1f;
            return s_canvas;
        }

        private static (RectTransform, TextMeshProUGUI, LineRenderer) PanelFor(CompanionController c)
        {
            if (s_panels.TryGetValue(c, out var p) && p.box != null) return p;
            var box = KitUI.NewRect(CanvasOf().transform, "Intent_" + c.GetDisplayName());
            box.pivot = new Vector2(0.5f, 0f); box.sizeDelta = new Vector2(420f, 150f);
            var img = box.gameObject.AddComponent<Image>(); img.raycastTarget = false;
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.RadiusSmall, new Color(FiresTokens.Panel.r, FiresTokens.Panel.g, FiresTokens.Panel.b, 0.92f), FiresTokens.Edge));
            var t = KitUI.Label(box, "Text", "", KitUI.SmallSize, FiresTokens.Text, TextAlignmentOptions.TopLeft);
            t.textWrappingMode = TextWrappingModes.Normal; t.raycastTarget = false; t.richText = true;
            var tr = t.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(10f, 8f); tr.offsetMax = new Vector2(-10f, -8f);
            var lgo = new GameObject("IntentLine"); UnityEngine.Object.DontDestroyOnLoad(lgo);
            var lr = lgo.AddComponent<LineRenderer>(); lr.positionCount = 2; lr.widthMultiplier = 0.06f; lr.useWorldSpace = true;
            lr.material = new Material(Shader.Find("Sprites/Default")); lr.startColor = lr.endColor = FiresTokens.Gold;
            p = (box, t, lr);
            s_panels[c] = p;
            return p;
        }

        private static void Drop(CompanionController c)
        {
            if (!s_panels.TryGetValue(c, out var p)) return;
            if (p.box != null) UnityEngine.Object.Destroy(p.box.gameObject);
            if (p.line != null) UnityEngine.Object.Destroy(p.line.gameObject);
            s_panels.Remove(c);
        }

        private static string Describe(CompanionController c)
        {
            var sb = new System.Text.StringBuilder();
            var idle = c.GetComponent<CompanionIdleBehavior>();
            var sub = idle != null ? idle.ActiveSubBehavior : null;
            var ai = c.GetComponent<FiresCore.Npc.AI.CompanionAI>();
            Vector3 me = c.transform.position;
            sb.Append($"<b><color=#F0B44C>{c.GetDisplayName()}</color></b>  <color=#B3A181>{(ai != null ? ai.CurrentState.ToString() : "-")}</color>\n");
            var kaView = c.GetComponent<ZNetView>();
            if (kaView != null && kaView.IsValid()) { string ka = FiresCore.Npc.Core.CompanionKeepAlive.KeptAliveText(kaView.GetZDO().m_uid); if (ka.Length > 0) sb.Append($"<color=#B3A181>Kept alive:</color> {ka}\n"); }
            sb.Append(sub != null ? $"Doing: {sub.BehaviorName} ({(sub.IsCommandInitiated ? "command" : "idle")}) - {sub.GetStatusDescription()}\n" : "Doing: nothing (idle)\n");
            var target = ai != null ? ai.GetTargetCreature() : null;
            if (target != null) sb.Append($"Target: {target.GetHoverName()} ({target.transform.position.x:0},{target.transform.position.z:0}) {Vector3.Distance(me, target.transform.position):0.0} m\n");
            if (ai != null && ai.IntentGoalAt >= 0f && Time.time - ai.IntentGoalAt < 2f)
            {
                int pts = ai.IntentPathPoints;
                string state = pts == 0 ? "no path" : Utils.DistanceXZ(ai.IntentPathEnd, ai.IntentGoal) > 1.5f ? $"{pts} pts PARTIAL" : $"{pts} pts";
                float stuck = ai.IntentStuckSeconds;
                sb.Append($"Moving to: ({ai.IntentGoal.x:0},{ai.IntentGoal.y:0},{ai.IntentGoal.z:0}) {Vector3.Distance(me, ai.IntentGoal):0.0} m for {ai.IntentGoalOwner}, {state}");
                if (stuck > 2f) sb.Append($", <color=#E0A040>no progress {stuck:0} s</color>");
                if (ai.PathShortBy > 0f) sb.Append($", <color=#E0A040>path ends {ai.PathShortBy:0.0} m short</color>");
                sb.Append('\n');
            }
            string n = c.companionName;
            float now = Time.realtimeSinceStartup;
            lock (s_last)
            {
                if (s_last.TryGetValue(n, out var l)) sb.Append($"<color=#B3A181>Last ({now - l.at:0}s):</color> {l.line}\n");
                if (s_lastWhy.TryGetValue(n, out var w) && now - w.at < 120f) sb.Append($"<color=#D2563F>Why ({now - w.at:0}s):</color> {w.line}");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private sealed class Pump : MonoBehaviour
        {
            private float _nextNames;
            private void Update()
            {
                try
                {
                    if (s_key != null && Player.m_localPlayer != null && !FiresCore.Input.FiresInputBlock.IsTyping() && FiresCore.Input.Chord.IsDown(s_key.Value))
                    {
                        s_all = !s_all;
                        FiresCore.Logging.FiresLogger.LogDebug($"[Intent] all companions: panel {(s_all ? "on" : "off")}");
                    }
                    if (Time.unscaledTime >= _nextNames)
                    {
                        _nextNames = Time.unscaledTime + 2f;
                        s_names.Clear();
                        foreach (var c in CompanionController.AllCompanions) if (c != null && IsShown(c) && !string.IsNullOrEmpty(c.companionName)) s_names.Add(c.companionName);
                    }
                }
                catch { }
            }

            private void LateUpdate()
            {
                var cam = Utils.GetMainCamera();
                foreach (var c in s_panels.Keys.ToList()) if (c == null || !IsShown(c)) Drop(c);
                if (cam == null) return;
                foreach (var c in CompanionController.AllCompanions)
                {
                    if (c == null || !IsShown(c)) continue;
                    var (box, text, line) = PanelFor(c);
                    Vector3 head = c.transform.position + Vector3.up * 2.4f;
                    Vector3 sp = cam.WorldToScreenPoint(head);
                    bool vis = sp.z > 0f && Vector3.Distance(cam.transform.position, head) < 60f;
                    box.gameObject.SetActive(vis);
                    var ai = c.GetComponent<FiresCore.Npc.AI.CompanionAI>();
                    bool walking = ai != null && ai.IntentGoalAt >= 0f && Time.time - ai.IntentGoalAt < 2f;
                    line.enabled = walking;
                    if (walking) { line.SetPosition(0, c.transform.position + Vector3.up * 0.15f); line.SetPosition(1, ai.IntentGoal + Vector3.up * 0.15f); }
                    if (!vis) continue;
                    text.text = Describe(c);
                    float h = Mathf.Clamp(text.preferredHeight + 18f, 50f, 260f);
                    box.sizeDelta = new Vector2(420f, h);
                    float scale = s_canvas.transform.lossyScale.x > 0f ? s_canvas.transform.lossyScale.x : 1f;
                    box.position = new Vector3(sp.x, sp.y, 0f);
                    box.anchoredPosition = new Vector2(box.anchoredPosition.x, box.anchoredPosition.y) ;
                    _ = scale;
                }
            }
        }

        private sealed class Provider : IContextMenuProvider
        {
            public string TitleFor(ContextTarget target) => null;
            public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
            {
                var c = target?.GameObject != null ? target.GameObject.GetComponentInParent<CompanionController>() : null;
                if (c == null || Player.m_localPlayer == null) return null;
                if (!c.IsOwner(Player.m_localPlayer) && !FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return null;
                var comp = c;
                return new[] { ContextMenuItem.Row($"{c.GetDisplayName()}: {(s_shown.Contains(c) ? "Hide intent" : "Show intent")}", () => Toggle(comp)) };
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_IntentView
        {
            private static bool s_registered;
            private static void Postfix()
            {
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresIntentViewPump"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
                if (s_registered) return;
                s_registered = true;
                FiresContextMenu.Register(new Provider());
                if (s_capture == null) { s_capture = new Capture(); BepInEx.Logging.Logger.Listeners.Add(s_capture); }
            }
        }
    }
}
