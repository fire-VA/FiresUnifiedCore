using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using FiresCore.UI.Kit;
using KitWindow = FiresCore.UI.Kit.KitWindow;
using KitWindowStyle = FiresCore.UI.Kit.KitWindowStyle;
using FiresCore.UI.Shell.Kit;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// The Homestead Job Board window (Fire 10-24, approved mockup Tools\mockups\jobboard_20261024): a kit window with three columns -
    /// DAILY WORK (recurring jobs, then the add/edit panel), CURRENT NEEDED JOBS (one-off / now jobs) and COMPANIONS AT BASE (state and
    /// job skills). A job is assigned with its "Assigned" button (Anyone or a named companion) or by dragging a companion card onto it.
    /// A job the base can't run right now is greyed with a tooltip listing what is missing. Open: [Job Board] key (default Ctrl+J) or
    /// the console command jobboard. Logic lives in <see cref="JobBoard"/>.
    /// </summary>
    internal static class JobBoardWindow
    {
        private const string Id = "FiresCore.JobBoard";
        private static KitWindow s_win;
        private static RectTransform s_daily, s_needed, s_comps, s_editor;
        private static JobBoard.Job s_sel;
        private static bool s_dirty, s_dragging;
        private static float s_nextRefresh;
        private static ConfigEntry<KeyboardShortcut> s_key;
        private static readonly Color Grey = new Color(1f, 1f, 1f, 0.45f);
        private static readonly string[] Prey = { "Deer", "Boar", "Neck", "Hare", "Lox", "Chicken", "Asksvin", "Volture" };

        internal static void BindConfig(ConfigFile cfg)
        {
            s_key = cfg.Bind("Job Board", "Open key", new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl), "Opens the Homestead Job Board (daily work, needed jobs and the companions at base). Ctrl+J: J alone is FiresNPCs' quest journal.");
        }

        // ---- open / close --------------------------------------------------------------------------------------------
        internal static void Toggle()
        {
            if (s_win != null && s_win.IsShowing) { s_win.Hide(); return; }
            if (s_win == null || s_win.Root == null)
            {
                s_win = KitWindow.Create(Id, "Homestead Job Board", "", new Vector2(1500f, 780f), new Vector2(1100f, 560f));
                BuildFrame(s_win.Body);
                JobBoard.Changed += () => s_dirty = true;
            }
            JobBoard.Load();
            s_win.Show();
            Refresh();
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] opened: {JobBoard.Jobs.Count} job(s), {JobBoard.CompanionsAtBase().Count} companion(s) at base");
        }

        private static void BuildFrame(RectTransform body)
        {
            var row = KitUI.NewRect(body, "Columns");
            row.anchorMin = Vector2.zero; row.anchorMax = Vector2.one; row.offsetMin = new Vector2(12f, 12f); row.offsetMax = new Vector2(-12f, -8f);
            var hl = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 14f; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
            s_daily = Column(row, "DAILY WORK", 1f, "+ Add daily job", () => AddMenu(JobBoard.Repeat.Daily), out _);
            s_needed = Column(row, "CURRENT NEEDED JOBS", 1f, "+ Add one-off job", () => AddMenu(JobBoard.Repeat.OneOff), out _);
            s_comps = Column(row, "COMPANIONS AT BASE", 0.62f, null, null, out _);
        }

        private static RectTransform Column(RectTransform parent, string title, float weight, string addText, Action add, out RectTransform root)
        {
            root = KitUI.NewRect(parent, "Col_" + title);
            var img = root.gameObject.AddComponent<Image>();
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.Radius, FiresTokens.PanelRaised, FiresTokens.Hairline));
            var le = root.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = weight; le.minWidth = 300f;
            var vl = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(10, 10, 8, 10); vl.spacing = 6f;
            vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            var head = KitUI.NewRect(root, "Head");
            var hh = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hh.childAlignment = TextAnchor.MiddleCenter; hh.childControlWidth = true; hh.childControlHeight = true; hh.childForceExpandWidth = false;
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
            var t = KitUI.Label(head, "Title", title, KitUI.HeaderSize + 2f, FiresTokens.Gold, TextAlignmentOptions.Center);
            t.fontStyle = FontStyles.Bold; t.characterSpacing = FiresTokens.SectionTracking;
            t.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            if (addText != null) KitUI.Button(head, addText, false, add);
            return KitUI.ScrollView(root, 120f);
        }

        // ---- refresh -------------------------------------------------------------------------------------------------
        private static void Refresh()
        {
            if (s_win == null || !s_win.IsShowing) return;
            s_dirty = false;
            Clear(s_daily); Clear(s_needed); Clear(s_comps);
            var daily = JobBoard.Jobs.Where(j => j.Repeat == JobBoard.Repeat.Daily).ToList();
            var needed = JobBoard.Jobs.Where(j => j.Repeat != JobBoard.Repeat.Daily).ToList();
            HeadRow(s_daily, "Last / next");
            foreach (var j in daily) JobRow(s_daily, j);
            if (daily.Count == 0) KitUI.Note(s_daily, "No daily work yet. \"+ Add daily job\" picks a chore or a trip and the time it runs each day.");
            s_editor = KitUI.Section(s_daily, s_sel != null ? "EDIT JOB · " + JobBoard.Label(s_sel).ToUpperInvariant() : "EDIT JOB");
            if (s_sel != null && JobBoard.Jobs.Contains(s_sel)) Editor(s_editor, s_sel);
            else KitUI.Note(s_editor, "Click a job to edit its schedule, companions and (for trips) its center and radius.");
            HeadRow(s_needed, "Status");
            foreach (var j in needed) JobRow(s_needed, j);
            if (needed.Count == 0) KitUI.Note(s_needed, "Nothing queued. One-off jobs run once at their time and drop off the board; a right-click quick trip lands here.");
            var comps = JobBoard.CompanionsAtBase();
            foreach (var c in comps) CompanionCard(s_comps, c);
            if (comps.Count == 0) KitUI.Note(s_comps, "No companions at base (within 60 m of home).");
            KitUI.Note(s_comps, "Drag a companion onto a job to assign it, or use the job's Assigned button (Anyone / a name).");
            s_win.SetSubtitle($"{JobBoard.Jobs.Count} job(s) · {comps.Count} companion(s) at base · " + (EnvMan.instance != null ? $"day {EnvMan.instance.GetDay()}, {JobBoard.Clock(Mathf.FloorToInt(EnvMan.instance.GetDayFraction() * 1440f))}" : ""));
        }

        private static void Clear(RectTransform rt) { if (rt == null) return; for (int i = rt.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(rt.GetChild(i).gameObject); }

        private static RectTransform Cells(Transform parent, string name, float height)
        {
            var r = KitUI.NewRect(parent, name);
            var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8f; h.padding = new RectOffset(10, 10, 4, 4); h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            r.gameObject.AddComponent<LayoutElement>().minHeight = height;
            return r;
        }

        private static TextMeshProUGUI Cell(Transform parent, string text, float flex, Color color, float size, TextAlignmentOptions align)
        {
            var t = KitUI.Label(parent, "Cell", text, size, color, align);
            t.textWrappingMode = TextWrappingModes.Normal;
            var le = t.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = flex; le.minWidth = 40f; le.preferredWidth = 10f;
            return t;
        }

        private static void HeadRow(RectTransform col, string last)
        {
            var r = Cells(col, "HeadRow", 22f);
            Cell(r, "JOB", 1.6f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.MidlineLeft);
            Cell(r, "SCHEDULE", 1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
            Cell(r, "ASSIGNED", 1.1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
            Cell(r, last.ToUpperInvariant(), 1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
        }

        private static void JobRow(RectTransform col, JobBoard.Job j)
        {
            var miss = JobBoard.Missing(j);
            var r = Cells(col, "Job_" + j.Id, 52f);
            var img = r.gameObject.AddComponent<Image>();
            bool sel = s_sel == j;
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.RadiusSmall, sel ? FiresTokens.Selected : FiresTokens.Well, sel ? FiresTokens.GoldBright : FiresTokens.Hairline));
            var name = KitUI.NewRect(r, "Name");
            var nv = name.gameObject.AddComponent<VerticalLayoutGroup>(); nv.childControlWidth = true; nv.childControlHeight = true; nv.childForceExpandHeight = false;
            var nle = name.gameObject.AddComponent<LayoutElement>(); nle.flexibleWidth = 1.6f; nle.minWidth = 120f; nle.preferredWidth = 10f;
            var t1 = KitUI.Label(name, "Label", JobBoard.Label(j), KitUI.BodySize + 1f, FiresTokens.Text, TextAlignmentOptions.MidlineLeft); t1.fontStyle = FontStyles.Bold;
            var t2 = KitUI.Label(name, "Detail", JobBoard.Detail(j), KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft); t2.textWrappingMode = TextWrappingModes.Normal;
            Cell(r, JobBoard.Schedule(j), 1f, j.Repeat == JobBoard.Repeat.Daily ? FiresTokens.GoldBright : j.Repeat == JobBoard.Repeat.Now ? FiresTokens.Ok : FiresTokens.Text, KitUI.SmallSize, TextAlignmentOptions.Center);
            var assign = KitUI.Button(r, JobBoard.AssignedText(j), false, () => AssignMenu(j));
            var ale = assign.Rect.GetComponent<LayoutElement>(); if (ale != null) { ale.flexibleWidth = 1.1f; ale.minWidth = 80f; ale.preferredWidth = 10f; }
            string status = miss.Count > 0 ? "unavailable · hover for why" : JobBoard.LastNext(j);
            Cell(r, status, 1f, status.StartsWith("●") ? FiresTokens.Warn : status.StartsWith("✔") ? FiresTokens.Ok : FiresTokens.TextMuted, KitUI.SmallSize, TextAlignmentOptions.Center);
            if (status.StartsWith("●")) { var job = j; KitUI.Button(r, "Show on map", false, () => { s_win?.Hide(); TripMapView.Show(job); }); }
            if (miss.Count > 0)
            {
                var cg = r.gameObject.AddComponent<CanvasGroup>(); cg.alpha = 0.45f;
                KitUI.AttachTooltip(r.gameObject, $"{JobBoard.Label(j)}: can't run yet\n" + string.Join("\n", miss.Select(m => "✖ Needs " + m)) + "\nRechecked from the stations and chests around base");
            }
            var click = r.gameObject.AddComponent<JobDrop>(); click.Job = j;
        }

        // ---- the add/edit panel ---------------------------------------------------------------------------------------
        private static void Editor(RectTransform sec, JobBoard.Job j)
        {
            var rep = KitUI.Row(sec, "Repeat");
            KitUI.Segmented(rep.Control, new[] { "Daily", "One-off", "Now" }, (int)j.Repeat, i => { j.Repeat = (JobBoard.Repeat)i; JobBoard.ArmDue(j); JobBoard.Saved(j); });
            if (j.Repeat != JobBoard.Repeat.Now)
            {
                var tod = KitUI.Row(sec, "Time of day", "In-game clock; daily jobs run each day at this time, one-off jobs once.");
                KitStepper st = null;
                st = KitUI.Stepper(tod.Control, JobBoard.Clock(j.Minute), () => { j.Minute = (j.Minute + 1410) % 1440; st.SetValue(JobBoard.Clock(j.Minute)); JobBoard.ArmDue(j); JobBoard.Saved(j); },
                                                                       () => { j.Minute = (j.Minute + 30) % 1440; st.SetValue(JobBoard.Clock(j.Minute)); JobBoard.ArmDue(j); JobBoard.Saved(j); });
            }
            if (j.IsTrip)
            {
                var type = KitUI.Row(sec, "Trip type");
                int ti = j.Kind == JobBoard.Kind.FishTrip ? 2 : j.Kind == JobBoard.Kind.MineTrip ? 3 : string.IsNullOrEmpty(j.Prey) ? 0 : 1;
                KitUI.Segmented(type.Control, new[] { "Hunt any game", "Hunt species", "Fish", "Mine" }, ti, i =>
                {
                    j.Kind = i == 2 ? JobBoard.Kind.FishTrip : i == 3 ? JobBoard.Kind.MineTrip : JobBoard.Kind.HuntTrip;
                    j.Prey = i == 1 ? (string.IsNullOrEmpty(j.Prey) ? FirstPrey() : j.Prey) : "";
                    JobBoard.Saved(j);
                });
                if (j.Kind == JobBoard.Kind.HuntTrip && !string.IsNullOrEmpty(j.Prey))
                {
                    var sp = KitUI.Row(sec, "Species");
                    KitStepper ss = null;
                    ss = KitUI.Stepper(sp.Control, JobBoard.PreyName(j.Prey), () => { j.Prey = NextPrey(j.Prey, -1); JobBoard.Saved(j); }, () => { j.Prey = NextPrey(j.Prey, 1); JobBoard.Saved(j); });
                }
                var cnt = KitUI.Row(sec, "Companions", "How many go (a party is up to 3). Assigned names go first, else the best free companions at base.");
                KitUI.Segmented(cnt.Control, new[] { "1", "2", "3" }, Mathf.Clamp(j.Count, 1, 3) - 1, i => { j.Count = i + 1; JobBoard.Saved(j); });
                KitUI.Button(cnt.Control, JobBoard.AssignedText(j), false, () => AssignMenu(j));
                var ctr = KitUI.Row(sec, "Center", "X / Y / Z of the trip's center. Enter applies a typed value.");
                Coord(ctr.Control, "X", j.Center.x, v => { j.Center.x = v; JobBoard.Saved(j); });
                Coord(ctr.Control, "Y", j.Center.y, v => { j.Center.y = v; JobBoard.Saved(j); });
                Coord(ctr.Control, "Z", j.Center.z, v => { j.Center.z = v; JobBoard.Saved(j); });
                var ctr2 = KitUI.Row(sec, "");
                KitUI.Button(ctr2.Control, "Use my position", false, () => { if (Player.m_localPlayer != null) { j.Center = Player.m_localPlayer.transform.position; JobBoard.Saved(j); } });
                KitUI.Button(ctr2.Control, "Map pin…", false, () => PinMenu(j));
                var rad = KitUI.Row(sec, "Radius", "The area the companions work: 30-100 m around the center.");
                KitUI.Slider(rad.Control, 30f, 100f, 5f, Mathf.Clamp(j.Radius, 30f, 100f), "m", v => { j.Radius = v; JobBoard.Saved(j); });
            }
            else
            {
                var who = KitUI.Row(sec, "Companion");
                KitUI.Button(who.Control, JobBoard.AssignedText(j), false, () => AssignMenu(j));
            }
            var miss = JobBoard.Missing(j);
            KitUI.Note(sec, miss.Count == 0 ? "✔ Ready" + (j.IsTrip ? $" · {Vector3.Distance(Flat(j.Center), Flat(JobBoard.BaseCenter())):0} m from home (limit {TripBehavior.MaxRange:0} m) · stops on bag full, nightfall, low health, 5 min with nothing to do" : "")
                                         : "✖ Needs " + string.Join(", ", miss), miss.Count > 0);
            var btn = KitUI.Strip(sec);
            KitUI.Button(btn, "Run now", true, () => { int n = JobBoard.Run(j); FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {JobBoard.Label(j)}: run now, {n} started"); if (n > 0) { j.Started = true; j.LastRunDay = EnvMan.instance != null ? EnvMan.instance.GetDay() : j.LastRunDay; } JobBoard.Saved(j); });
            KitUI.Button(btn, "Remove", false, () => { s_sel = null; JobBoard.Remove(j); });
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static void Coord(RectTransform parent, string axis, float value, Action<float> set)
        {
            KitUI.Text(parent, axis, FiresTokens.TextMuted);
            var f = KitUI.TextField(parent, axis, s => { if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) set(v); });
            f.SetText(value.ToString("0", CultureInfo.InvariantCulture));
            var le = f.Input.GetComponent<LayoutElement>() ?? f.Input.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 70f; le.minWidth = 60f;
        }

        private static string FirstPrey() => Prey.FirstOrDefault(p => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(p) != null) ?? "Deer";
        private static string NextPrey(string cur, int dir)
        {
            var list = Prey.Where(p => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(p) != null).ToList();
            if (list.Count == 0) return cur;
            int i = Mathf.Max(0, list.IndexOf(cur));
            return list[(i + dir + list.Count) % list.Count];
        }

        // ---- popover menus --------------------------------------------------------------------------------------------
        private static KitWindow s_menu;
        private static void Menu(string title, List<(string label, Action act)> items)
        {
            if (s_menu != null) s_menu.Destroy();
            s_menu = KitWindow.Create(Id + ".Menu", title, "", new Vector2(320f, Mathf.Min(520f, 70f + items.Count * 36f)), new Vector2(240f, 120f), KitWindowStyle.Popover);
            s_menu.RememberRect = false;
            Vector2 m = UnityEngine.Input.mousePosition;
            s_menu.DefaultPlacement = px => new Rect(Mathf.Clamp(m.x, 0f, Screen.width - px.x), Mathf.Clamp(m.y - px.y, 0f, Screen.height - px.y), px.x, px.y);
            var scroll = KitUI.ScrollView(s_menu.Body, 60f);
            foreach (var it in items)
            {
                var a = it.act;
                var b = KitUI.Button(scroll, it.label, false, () => { s_menu?.Hide(); a(); });
                var le = b.Rect.GetComponent<LayoutElement>(); if (le != null) le.flexibleWidth = 1f;
            }
            s_menu.Show();
        }

        private static void AddMenu(JobBoard.Repeat repeat)
        {
            var items = new List<(string, Action)>();
            foreach (JobBoard.Kind k in Enum.GetValues(typeof(JobBoard.Kind)))
            {
                var kind = k;
                var probe = new JobBoard.Job { Kind = kind, Center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero };
                var miss = JobBoard.Missing(probe);
                items.Add((JobBoard.Label(probe) + (miss.Count > 0 ? "  (needs " + string.Join(", ", miss) + ")" : ""), () => { s_sel = JobBoard.Add(kind, repeat); s_dirty = true; }));
            }
            Menu(repeat == JobBoard.Repeat.Daily ? "Add daily job" : "Add one-off job", items);
        }

        private static void AssignMenu(JobBoard.Job j)
        {
            var items = new List<(string, Action)> { ("Anyone (first free companion)", () => JobBoard.Assign(j, null)) };
            var skill = JobBoard.SkillOf(j.Kind);
            foreach (var c in JobBoard.CompanionsAtBase())
            {
                var comp = c;
                string s = skill == Skills.SkillType.None ? "" : $" · {skill} {c.GetSkills()?.GetSkillLevel(skill) ?? 0f:0}";
                items.Add(($"{c.GetDisplayName()} ({JobBoard.StateOf(c)}{s})", () => JobBoard.Assign(j, comp, j.IsTrip && j.Assigned.Count < j.Count)));
            }
            Menu("Assign: " + JobBoard.Label(j), items);
        }

        private static void PinMenu(JobBoard.Job j)
        {
            var items = new List<(string, Action)>();
            var pins = Minimap.instance != null ? Traverse.Create(Minimap.instance).Field("m_pins").GetValue<List<Minimap.PinData>>() : null;
            if (pins != null)
                foreach (var p in pins.Where(p => p != null && p.m_save && p.m_ownerID == 0L))
                {
                    var pos = p.m_pos;
                    string n = string.IsNullOrEmpty(p.m_name) ? p.m_type.ToString() : p.m_name;
                    items.Add(($"{n} ({pos.x:0}, {pos.z:0})", () => { j.Center = new Vector3(pos.x, ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float h) ? h : pos.y, pos.z); JobBoard.Saved(j); }));
                }
            if (items.Count == 0) items.Add(("No map pins of yours yet", () => { }));
            Menu("Center on a map pin", items);
        }

        // ---- companions -----------------------------------------------------------------------------------------------
        private static readonly (string label, Skills.SkillType type)[] CardSkills =
        {
            ("Farming", Skills.SkillType.Farming), ("Hunting", Skills.SkillType.Bows), ("Fishing", Skills.SkillType.Fishing),
            ("Woodcutting", Skills.SkillType.WoodCutting), ("Mining", Skills.SkillType.Pickaxes),
        };

        private static void CompanionCard(RectTransform col, CompanionController c)
        {
            var card = KitUI.NewRect(col, "Card_" + c.companionId);
            var img = card.gameObject.AddComponent<Image>();
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.RadiusSmall, FiresTokens.Well, FiresTokens.Hairline));
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>(); v.padding = new RectOffset(10, 10, 8, 8); v.spacing = 4f;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            var top = Cells(card, "Top", 22f); top.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(0, 0, 0, 0);
            var n = Cell(top, c.GetDisplayName(), 1f, FiresTokens.Text, KitUI.BodySize + 1f, TextAlignmentOptions.MidlineLeft); n.fontStyle = FontStyles.Bold;
            string state = JobBoard.StateOf(c);
            Cell(top, state, 1f, state == "Idle" ? FiresTokens.Ok : FiresTokens.Warn, KitUI.SmallSize, TextAlignmentOptions.MidlineRight);
            var grid = KitUI.NewRect(card, "Skills");
            var g = grid.gameObject.AddComponent<GridLayoutGroup>(); g.cellSize = new Vector2(118f, 26f); g.spacing = new Vector2(10f, 2f);
            var skills = c.GetSkills();
            var shown = CardSkills.ToList();
            var best = skills?.GetAllSkills()?.Where(kv => !CardSkills.Any(s => s.type == kv.Key) && kv.Key != Skills.SkillType.Run && kv.Key != Skills.SkillType.Jump && kv.Key != Skills.SkillType.Swim && kv.Key != Skills.SkillType.Sneak)
                .OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best != null && best.Value.Value > 0f) shown.Add((best.Value.Key.ToString(), best.Value.Key));
            foreach (var s in shown)
            {
                float lv = skills?.GetSkillLevel(s.type) ?? 0f;
                var cell = KitUI.NewRect(grid, s.label);
                var cl = cell.gameObject.AddComponent<VerticalLayoutGroup>(); cl.childControlWidth = true; cl.childControlHeight = true; cl.childForceExpandHeight = false; cl.spacing = 2f;
                var lab = KitUI.Label(cell, "L", $"{s.label}  <color=#EFE2C4>{lv:0}</color>", KitUI.NoteSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft);
                lab.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;
                var bar = KitUI.NewRect(cell, "Bar"); bar.gameObject.AddComponent<LayoutElement>().preferredHeight = 6f;
                var bi = bar.gameObject.AddComponent<Image>(); bi.color = new Color(0.07f, 0.05f, 0.03f, 1f);
                var fill = KitUI.NewRect(bar, "Fill"); fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(Mathf.Clamp01(lv / 100f), 1f); fill.offsetMin = fill.offsetMax = Vector2.zero;
                fill.gameObject.AddComponent<Image>().color = FiresTokens.Gold;
            }
            int rows = Mathf.CeilToInt(shown.Count / 2f);
            grid.gameObject.AddComponent<LayoutElement>().preferredHeight = rows * 28f;
            var drag = card.gameObject.AddComponent<CompanionDrag>(); drag.Companion = c;
        }

        // ---- drag a companion onto a job --------------------------------------------------------------------------------
        private sealed class CompanionDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public CompanionController Companion;
            private RectTransform _ghost;
            public void OnBeginDrag(PointerEventData e)
            {
                s_dragging = true;
                _ghost = (RectTransform)Instantiate(gameObject, s_win.Root, true).transform;
                Destroy(_ghost.GetComponent<CompanionDrag>());
                foreach (var g in _ghost.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
                var cg = _ghost.gameObject.AddComponent<CanvasGroup>(); cg.alpha = 0.85f; cg.blocksRaycasts = false;
                _ghost.localRotation = Quaternion.Euler(0f, 0f, -1.2f);
            }
            public void OnDrag(PointerEventData e) { if (_ghost != null) _ghost.position = e.position; }
            public void OnEndDrag(PointerEventData e)
            {
                if (_ghost != null) Destroy(_ghost.gameObject);
                s_dragging = false;
                var hit = e.pointerCurrentRaycast.gameObject;
                var drop = hit != null ? hit.GetComponentInParent<JobDrop>() : null;
                if (drop != null && drop.Job != null) JobBoard.Assign(drop.Job, Companion, drop.Job.IsTrip && drop.Job.Assigned.Count < drop.Job.Count);
            }
        }

        /// <summary>A job row: click selects it for the editor; it glows while a companion is dragged over it.</summary>
        private sealed class JobDrop : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
        {
            public JobBoard.Job Job;
            private Outline _glow;
            public void OnPointerClick(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) { s_sel = Job; s_dirty = true; } }
            public void OnPointerEnter(PointerEventData e) { if (!s_dragging) return; _glow = _glow ?? gameObject.AddComponent<Outline>(); _glow.effectColor = FiresTokens.GoldBright; _glow.effectDistance = new Vector2(2f, -2f); _glow.enabled = true; }
            public void OnPointerExit(PointerEventData e) { if (_glow != null) _glow.enabled = false; }
        }

        // ---- key, console, periodic refresh -----------------------------------------------------------------------------
        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                try
                {
                    if (s_key != null && Player.m_localPlayer != null && !FiresCore.Input.FiresInputBlock.IsTyping() && FiresCore.Input.Chord.IsDown(s_key.Value)
                        && (s_win == null || !s_win.IsShowing ? !Console.IsVisible() && !global::Menu.IsVisible() && !InventoryGui.IsVisible() : true))
                        Toggle();
                    if (s_win != null && s_win.IsShowing && !s_dragging && (s_dirty || Time.unscaledTime >= s_nextRefresh))
                    {
                        s_nextRefresh = Time.unscaledTime + 3f;
                        Refresh();
                    }
                }
                catch (Exception e) { Debug.LogWarning("[JobBoard] window: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_JobBoardWindow
        {
            private static void Postfix()
            {
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresJobBoardWindow"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class JobBoard_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("jobboard", "[FiresUnifiedCore] jobboard - opens the Homestead Job Board.", args => Toggle());
            }
        }
    }
}
