// Fires UI Shell, Core step 0/1: what a module gets for building (IShellContext) and the widget facade (ShellWidgets).
// Both exist only while [UI] Fires UI override is ON (the hub creates one context per module on first show).
using System;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// The shared widget kit for modules: thin wrappers over Kit.Widgets that add click handling. Positions are UI units
    /// from the parent's top-left, y down (Kit README). Every widget gets a stable name from its id.
    /// </summary>
    public sealed class ShellWidgets
    {
        internal ShellWidgets() { }

        public KitSection Section(RectTransform parent, string id, string title, float x, float y, float width, string note = null)
            => Widgets.Section(parent, id, title, x, y, width, note);

        public KitRow Row(KitSection section, string id, string label, string value, RowState state = RowState.Normal)
            => Widgets.Row(section, id, label, value, state);

        public RectTransform Toggle(RectTransform parent, string id, string label, bool on, float x, float y, float height,
            Action onClick = null, bool rightAlign = false)
        {
            var rt = Widgets.DiamondToggle(parent, id, label, on, x, y, height, rightAlign);
            if (onClick != null) Clickable(rt, onClick);
            return rt;
        }

        public RectTransform Pill(RectTransform parent, string id, string label, bool on, float x, float y, Action onClick = null)
        {
            var rt = Widgets.Pill(parent, id, label, on, x, y);
            if (onClick != null) Clickable(rt, onClick);
            return rt;
        }

        public RectTransform Slider(RectTransform parent, string id, float value01, string text, float x, float y, float width, float height)
            => Widgets.Slider(parent, id, value01, text, x, y, width, height);

        public RectTransform Button(RectTransform parent, string id, string label, ButtonKind kind, float x, float y, Action onClick,
            float minWidth = 0f, float height = 30f)
        {
            var rt = Widgets.Button(parent, id, label, kind, x, y, minWidth, height);
            if (onClick != null) Clickable(rt, onClick);
            return rt;
        }

        public RectTransform Tooltip(RectTransform parent, string title, string body, string meta, float x, float y, float width = 300f)
            => Widgets.Tooltip(parent, title, body, meta, x, y, width);

        public KitLabel Text(RectTransform parent, string name, string text, FontRole role, float size, Color color, KitAlign align,
            float x, float y, float w, float h, bool wrap = false)
        {
            var l = KitText.Create(parent, name, text, role, size, color, align, 0f, true, wrap);
            Widgets.Place(l.Rect, x, y, w, h);
            return l;
        }

        /// <summary>Makes a kit node clickable: its own Graphic (or a transparent Image) becomes the raycast target.</summary>
        public static UnityEngine.UI.Button Clickable(RectTransform rt, Action onClick)
        {
            if (rt == null) return null;
            var g = rt.GetComponent<Graphic>();
            if (g == null)
            {
                var img = rt.gameObject.AddComponent<Image>();
                img.color = new Color(0f, 0f, 0f, 0f);
                g = img;
            }
            g.raycastTarget = true;
            var b = rt.gameObject.GetComponent<UnityEngine.UI.Button>() ?? rt.gameObject.AddComponent<UnityEngine.UI.Button>();
            b.transition = Selectable.Transition.None;
            b.targetGraphic = g;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(() =>
            {
                try { onClick(); }
                catch (Exception e) { ShellLog.Warn("click on " + rt.name + " threw " + e.GetType().Name + ": " + e.Message); }
            });
            return b;
        }
    }

    internal sealed class ShellContext : IShellContext
    {
        private readonly ShellHub _hub;
        private readonly FiresShell.Entry _entry;
        private static readonly ShellWidgets s_ui = new ShellWidgets();

        internal (string key, string action)[] FooterHints;
        internal ShellPill[] Pills;
        internal ShellSubTab[] SubTabs;
        internal Action<string> OnSubTab;
        internal string ActiveSubTab;

        internal ShellContext(ShellHub hub, FiresShell.Entry entry) { _hub = hub; _entry = entry; }

        public ShellWidgets Ui => s_ui;

        public void Confirm(string text, Action yes, Action no = null)
        {
            // The existing Core modal (MenuDialog) is the shell's confirm until the kit has its own (step 1 follow-up).
            try { FiresCore.UI.MenuDialog.Show(text, null, yes, no); }
            catch (Exception e) { ShellLog.Warn("Confirm failed (" + e.GetType().Name + ": " + e.Message + ")"); }
        }

        public void SetFooterHints(params (string key, string action)[] hints)
        {
            FooterHints = hints;
            if (_hub.IsActive(_entry)) _hub.RefreshChrome();
        }

        public void SetContextPills(params ShellPill[] pills)
        {
            Pills = pills;
            if (_hub.IsActive(_entry)) _hub.RefreshChrome();
        }

        public void SetSubTabs((string id, string title, bool badge)[] tabs, Action<string> onSelect)
        {
            ShellSubTab[] conv = null;
            if (tabs != null)
            {
                conv = new ShellSubTab[tabs.Length];
                for (int i = 0; i < tabs.Length; i++) conv[i] = new ShellSubTab(tabs[i].id, tabs[i].title, true, tabs[i].badge);
            }
            SetSubTabs(conv, onSelect);
        }

        public void SetSubTabs(ShellSubTab[] tabs, Action<string> onSelect)
        {
            SubTabs = tabs;
            OnSubTab = onSelect;
            if (tabs != null && tabs.Length > 0 && !IsVisibleSubTab(ActiveSubTab))
            {
                ActiveSubTab = null;
                foreach (var t in tabs) if (t.Visible) { ActiveSubTab = t.Id; break; }
            }
            if (_hub.IsActive(_entry)) _hub.RefreshChrome();
        }

        private bool IsVisibleSubTab(string id) => id != null && SubTabs != null && Array.FindIndex(SubTabs, t => t.Id == id && t.Visible) >= 0;

        public void SelectSubTab(string id)
        {
            if (!IsVisibleSubTab(id)) return;
            ActiveSubTab = id;
            if (_hub.IsActive(_entry)) _hub.RefreshChrome();
            try { OnSubTab?.Invoke(id); }
            catch (Exception e) { ShellLog.Warn("sub-tab handler of " + _entry.Module.Id + " threw " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
