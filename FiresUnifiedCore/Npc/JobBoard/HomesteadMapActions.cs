using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using FiresTokens = FiresCore.UI.Shell.Kit.FiresTokens;
using FiresCore.UI.ContextMenu;
using FiresCore.UI.Kit;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.864 ([ghost], Fire 10-27 shot 571: "clicking or right clicking on elements on the map does not do anything and pulls up no context
    /// menue"; his RTS ask: "right click on resources/companions/workbenches/storage/etc and control the homestead like a top down RTS"):
    /// homestead map phase 2d.
    ///  LEFT click a marker: it is selected (gold, larger) and the "Homestead Info" card shows it (companion: name, job, health, carrying;
    ///  station: name, fuel / queue; chest: top contents; tree / ore / pickable / crop: what it is and drops).
    ///  RIGHT click: Core's context menu (FiresContextMenu, marker kind "HomeMap") with the marker's actions:
    ///   companion - Follow me / Stay where they are / Send to... (the next left click on the map) / Assign to a board job / Unassign;
    ///   station   - Assign a worker (a companion) to its board job / Run its job now;
    ///   chest     - Show contents (the card);
    ///   tree / ore / pickable / crop - Harvest now (a one-off "Now" board job of that kind for the companions at base: they pick the
    ///               nearest work themselves; the board has no per-object target).
    /// Board actions (assign / run / harvest) change the player's own Job Board, which the server already checks on every push
    /// (JobBoardServer). Companion orders go through the SERVER: client -> "FiresCore_HomeMapCmd" -> the server checks the package size,
    /// the action name, that the sender's player id is the companion's "companion_owner", and (for Stay / Send) that no ward at the target
    /// refuses them -> "FiresCore_HomeMapApply" to the peer that holds the companion's instance (which accepts it only from the server) ->
    /// CompanionController.CommandFollow / CommandStay (the radial's own path) -> "FiresCore_HomeMapResult" back to the sender.
    /// Lines: "[HomeMap] click <marker> -> <action> (accepted / refused: reason)" on the client, "[HomeMap] server: <player> <action> on
    /// <companion> accepted / refused: reason" on the server, "[HomeMap] apply <action> on <companion>: done / refused: reason" where it runs.
    /// </summary>
    internal static class HomesteadMapActions
    {
        internal const string Kind = "HomeMap";
        private const string CmdRpc = "FiresCore_HomeMapCmd", ApplyRpc = "FiresCore_HomeMapApply", ResultRpc = "FiresCore_HomeMapResult";
        private const int MaxPackage = 256;
        private static readonly string[] Allowed = { "follow", "stay", "send", "area" };
        private static readonly string[] Foci = { "gather", "chop", "mine", "loot", "tame" };   // 0.2.890: an area order's work focus ("area:chop"); hunt is a board job
        private const float AreaMin = 3f, AreaMax = 60f;

        internal sealed class Target { public string Type; public string Name; public Vector3 Pos; public ZDOID Id; public GameObject Go; }

        // ---- click entry (from the marker's MarkerClick) -----------------------------------------------------------------------
        internal sealed class MarkerClick : MonoBehaviour, IPointerClickHandler
        {
            internal Func<Target> Get;
            public void OnPointerClick(PointerEventData e)
            {
                var t = Get?.Invoke();
                if (t == null) return;
                if (e.button == PointerEventData.InputButton.Left && t.Type == "Companion" && t.Go != null && t.Go.GetComponent<CompanionController>() is CompanionController cc)
                {
                    // 0.2.868 (Fire shot 587): a companion's left-click turns its existing intent panel ON (never off from here)
                    ClosePopup(false); Selected = t;
                    if (!FiresCore.Npc.Debugging.CompanionIntentView.IsShown(cc)) FiresCore.Npc.Debugging.CompanionIntentView.Toggle(cc);
                    FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click Companion '{t.Name}' -> intent panel on");
                    return;
                }
                if (e.button == PointerEventData.InputButton.Left) { Select(t, false, e.position); return; }
                if (e.button == PointerEventData.InputButton.Right) Select(t, true, e.position);
            }
        }

        // ---- ONE popup: info (left-click) or info + actions (right-click) -----------------------------------------------------
        // 0.2.868 (Fire 10-27 shots 583/584: "the context ui needs to be fixed too, this is not very clean or professional why are there
        // two ui's"): the big translucent info KitWindow and Core's global context menu (which also pulled other mods' items for the
        // same GameObject, e.g. a sconce's "Edit sound / light") are replaced by one compact opaque popup at the cursor on the map:
        // a header (name + one info line), then rows sized to their text (24 px, no wrap); items sharing a "Prefix: " fold into a
        // "Prefix ▸" submenu. One at a time; closes on a click outside it, Esc, or the map closing. Only our items.
        internal static Target Selected;
        private static RectTransform s_pop, s_sub;
        // 0.2.875: test hooks (homemap_test)
        internal static RectTransform TestPopup => s_pop;
        internal static RectTransform TestSubmenu => s_sub;
        /// <summary>Clicks the popup row whose label starts with the text (as a player would); false when there is none.</summary>
        internal static bool TestClickRow(string startsWith)
        {
            if (s_pop == null) return false;
            foreach (var b in s_pop.GetComponentsInChildren<Button>(true))
            {
                var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null && t.text.StartsWith(startsWith, StringComparison.Ordinal)) { b.onClick.Invoke(); return true; }
            }
            return false;
        }
        /// <summary>0.2.890 (homemap_area_test): clicks the open submenu's row whose label starts with the text.</summary>
        internal static bool TestClickSubRow(string startsWith)
        {
            if (s_sub == null) return false;
            foreach (var b in s_sub.GetComponentsInChildren<Button>(true))
            {
                var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null && t.text.StartsWith(startsWith, StringComparison.Ordinal)) { b.onClick.Invoke(); return true; }
            }
            return false;
        }
        internal static void TestSendTo(Target t, Vector3 pos) => Send(t, "send", pos);
        internal static void TestAreaOrder(string kind, Vector3 c, float r, CompanionController comp) => AreaOrder(kind, c, r, comp);   // 0.2.879: homestead_orders_test

        internal static void Select(Target t, bool actions, Vector2 screen)
        {
            Selected = t;
            List<ContextMenuItem> items = null;
            if (actions)
            {
                EnsureProvider();
                items = s_provider.GetItems(new ContextTarget { GameObject = t.Go, Point = t.Pos, Marker = t, MarkerKind = Kind }).ToList();
            }
            int rows = ShowPopup(t.Name, Describe(t), items, t, screen);
            if (s_pop != null) FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click {t.Type} '{t.Name}' -> {(actions ? $"popup with {rows} action row(s)" : "popup (info)")} ({s_pop.rect.width:0}x{s_pop.rect.height:0} px)");
        }

        // the one popup: a header (title + info, wraps at 320 px) and the item rows, "Prefix: x" items folded into "Prefix ▸" submenus
        // 0.2.890: for HomesteadMapCircles
        /// <summary>0.2.890: starts the chore behaviour for an area focus (its CanStart decides; the companion's home is already the circle),
        /// returns what started or why not.</summary>
        private static string StartFocus(CompanionIdleBehavior idle, string focus)
        {
            idle.StopActiveSubBehavior("area order: " + focus);
            bool ok;
            string name;
            switch (focus)
            {
                case "chop": ok = idle.TryStartSubBehavior<WoodGatheringBehavior>(); name = "WoodGathering"; break;
                case "gather": case "mine": ok = idle.TryStartSubBehavior<ResourceGatheringBehavior>(); name = "ResourceGathering"; break;
                case "loot": ok = idle.TryStartSubBehavior<LootPickupBehaviorV2>(); name = "LootPickup"; break;
                case "tame": ok = idle.TryStartSubBehavior<TamingBehavior>(); name = "Taming"; break;
                default: return "its own chores (unknown focus " + focus + ")";
            }
            return ok ? name : $"nothing yet ({name} can't start now: nothing for it inside the circle, or a missing tool; it keeps trying from its chores)";
        }

        internal static int ShowPopupFor(string title, string infoText, List<ContextMenuItem> items, Target t, Vector2 screen) => ShowPopup(title, infoText, items, t, screen);
        private static Target TargetOf(CompanionController comp)
        {
            var nv = comp.GetComponent<ZNetView>();
            return new Target { Type = "Companion", Name = comp.GetDisplayName(), Pos = comp.transform.position, Go = comp.gameObject, Id = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None };
        }
        internal static void SendArea(CompanionController comp, string focus, Vector3 c, float r) => Send(TargetOf(comp), "area:" + focus, c, r);
        internal static void SendBack(CompanionController comp, Vector3 home) => Send(TargetOf(comp), "send", home);
        /// <summary>A "Now" hunting-trip board job on a circle, assigned to the companion (the map's "Hunt here"); returns the job.</summary>
        internal static JobBoard.Job TestAreaOrderJob(string kind, Vector3 c, float r, CompanionController comp)
        {
            int before = JobBoard.Jobs.Count;
            AreaOrder(kind, c, r, comp);
            return JobBoard.Jobs.Count > before ? JobBoard.Jobs[JobBoard.Jobs.Count - 1] : null;
        }

        private static int ShowPopup(string title, string infoText, List<ContextMenuItem> items, Target t, Vector2 screen)
        {
            ClosePopup(false);
            var mm = Minimap.instance;
            if (mm == null || mm.m_largeRoot == null) return 0;
            var root = (RectTransform)mm.m_largeRoot.transform;
            s_pop = Panel(root, "FiresHomeMapPopup");
            var head = KitUI.Label(s_pop, "Name", title, KitUI.BodySize, FiresTokens.Gold, TextAlignmentOptions.MidlineLeft);
            head.fontStyle = FontStyles.Bold; head.textWrappingMode = TextWrappingModes.NoWrap;
            var info = KitUI.Label(s_pop, "Info", infoText, KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.TopLeft);
            info.textWrappingMode = TextWrappingModes.Normal;   // 0.2.868 (shot 587: the card clipped its own text): wraps at 320 px, height follows
            var ile = info.gameObject.AddComponent<LayoutElement>(); ile.preferredWidth = 320f;
            int rows = 0;
            if (items != null)
            {
                var groups = new Dictionary<string, List<ContextMenuItem>>();
                var order = new List<object>();
                foreach (var it in items)
                {
                    if (it.IsSeparator) continue;
                    int c = it.Label.IndexOf(": ", StringComparison.Ordinal);
                    string prefix = c > 0 ? it.Label.Substring(0, c) : null;
                    if (prefix != null && items.Count(x => !x.IsSeparator && x.Label.StartsWith(prefix + ": ", StringComparison.Ordinal)) > 1)
                    {
                        if (!groups.TryGetValue(prefix, out var g)) { groups[prefix] = g = new List<ContextMenuItem>(); order.Add(prefix); }
                        g.Add(it);
                    }
                    else order.Add(it);
                }
                foreach (var o in order)
                {
                    if (o is string prefix)
                    {
                        var list = groups[prefix];
                        RectTransform rowRt = null;
                        rowRt = Row(s_pop, prefix + "  ▸", true, () => OpenSub(rowRt, prefix, list, t));
                    }
                    else
                    {
                        var it = (ContextMenuItem)o;
                        Row(s_pop, it.Label, it.Enabled, () => { ClosePopup(false); it.OnPick?.Invoke(); });
                    }
                    rows++;
                }
            }
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(s_pop);
            PlaceAt(s_pop, root, screen);
            return rows;
        }

        private static RectTransform Panel(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false); rt.SetAsLastSibling();
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0f, 1f);
            var img = go.AddComponent<Image>(); KitUI.SetPlate(img, KitUI.Plate(FiresTokens.Radius, FiresTokens.PanelRaised, FiresTokens.Hairline));
            var c = img.color; c.a = 1f; img.color = c;   // opaque: nothing on the map shows through
            var vl = go.AddComponent<VerticalLayoutGroup>(); vl.padding = new RectOffset(10, 10, 8, 8); vl.spacing = 3f;
            vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            var fit = go.AddComponent<ContentSizeFitter>(); fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize; fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        private static RectTransform Row(RectTransform parent, string label, bool enabled, Action pick)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var b = go.AddComponent<Button>(); b.targetGraphic = img; b.interactable = enabled; b.onClick.AddListener(() => pick());
            KitUI.StyleButton(b, false);
            var hl = go.AddComponent<HorizontalLayoutGroup>(); hl.padding = new RectOffset(8, 8, 3, 3); hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            var le = go.AddComponent<LayoutElement>(); le.minHeight = 24f; le.flexibleHeight = 0f;
            var lab = KitUI.Label(rt, "Label", label, KitUI.SmallSize, enabled ? FiresTokens.Text : FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft);
            lab.textWrappingMode = TextWrappingModes.NoWrap; lab.raycastTarget = false;
            return rt;
        }

        private static void OpenSub(RectTransform row, string prefix, List<ContextMenuItem> items, Target t)
        {
            if (s_sub != null) UnityEngine.Object.Destroy(s_sub.gameObject);
            var root = (RectTransform)s_pop.parent;
            s_sub = Panel(root, "FiresHomeMapSubmenu");
            foreach (var it in items)
            {
                var item = it;
                Row(s_sub, it.Label.Substring(prefix.Length + 2), it.Enabled, () => { ClosePopup(false); item.OnPick?.Invoke(); });
            }
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(s_sub);
            var c = new Vector3[4]; row.GetWorldCorners(c);   // to the right of the row, top aligned
            PlaceAt(s_sub, root, RectTransformUtility.WorldToScreenPoint(null, c[2]) + new Vector2(4f, 0f));
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click {t.Type} '{t.Name}' -> submenu {prefix} ({items.Count} row(s))");
        }

        // top-left at the screen point, kept inside the map root
        private static void PlaceAt(RectTransform rt, RectTransform root, Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out Vector2 local);
            Rect r = root.rect;
            Vector2 p = local - r.min;   // anchors at the root's bottom-left
            p.x = Mathf.Clamp(p.x + 8f, 0f, Mathf.Max(0f, r.width - rt.rect.width));
            p.y = Mathf.Clamp(p.y - 8f, rt.rect.height, r.height);
            rt.anchoredPosition = p;
        }

        private static bool Over(RectTransform rt) => rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, UnityEngine.Input.mousePosition, null);

        internal static void ClosePopup(bool clearSelection)
        {
            if (s_sub != null) { UnityEngine.Object.Destroy(s_sub.gameObject); s_sub = null; }
            if (s_pop != null) { UnityEngine.Object.Destroy(s_pop.gameObject); s_pop = null; }
            if (clearSelection) Selected = null;
        }

        // a press outside the popup / submenu, or Esc, closes it (a marker's click opens the next one on release)
        private static void PopupTick()
        {
            if (s_pop == null) return;
            bool press = UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonDown(1);
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || (press && !Over(s_pop) && !Over(s_sub))) ClosePopup(true);
        }

        internal static void HideCard() { ClosePopup(true); s_sendFrom = null; }

        private static string Describe(Target t)
        {
            var go = t.Go;
            if (go == null) return $"{t.Type} at ({t.Pos.x:0}, {t.Pos.z:0})\n(not loaded right now)";
            var lines = new List<string>();
            switch (t.Type)
            {
                case "Companion":
                    var c = go.GetComponent<CompanionController>();
                    var ch = go.GetComponent<Character>();
                    if (c != null && ch != null)
                    {
                        lines.Add($"Job: {JobBoard.StateOf(c)}");
                        lines.Add($"Health: {ch.GetHealth():0} / {ch.GetMaxHealth():0}");
                        var inv = c.GetComponent<Humanoid>()?.GetInventory();
                        if (inv != null) lines.Add("Carrying: " + Summ(inv, 5));
                        lines.Add(c.ShouldBeFollowing ? "Following you" : "Staying at base");
                    }
                    break;
                case "Chest": case "Terminal":
                    var ct = go.GetComponent<Container>();
                    if (ct != null && ct.GetInventory() != null) lines.Add($"{ct.GetInventory().NrOfItems()} stacks: " + Summ(ct.GetInventory(), 8));
                    break;
                case "Smelter":
                    var sm = go.GetComponent<Smelter>();
                    var nv = go.GetComponent<ZNetView>();
                    if (sm != null && nv != null && nv.IsValid())
                        lines.Add($"Fuel {nv.GetZDO().GetFloat(ZDOVars.s_fuel, 0f):0} / {sm.m_maxFuel}, queued ore {nv.GetZDO().GetInt(ZDOVars.s_queued, 0)}");
                    break;
                case "Fire":
                    var fp = go.GetComponent<Fireplace>(); var fnv = go.GetComponent<ZNetView>();
                    if (fp != null && fnv != null && fnv.IsValid()) lines.Add($"Fuel {fnv.GetZDO().GetFloat(ZDOVars.s_fuel, 0f):0.0} / {fp.m_maxFuel:0}");
                    break;
                case "Pickable":
                    var pk = go.GetComponent<Pickable>(); if (pk != null && pk.m_itemPrefab != null) lines.Add("Gives " + Localization.instance.Localize(pk.m_itemPrefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_name ?? pk.m_itemPrefab.name));
                    break;
                case "Crop":
                    var pl = go.GetComponent<Plant>(); if (pl != null) lines.Add("Growing: " + Localization.instance.Localize(pl.m_name));
                    break;
            }
            lines.Add($"At ({t.Pos.x:0}, {t.Pos.z:0})");
            return string.Join("\n", lines);
        }

        private static string Summ(Inventory inv, int n)
        {
            var g = inv.GetAllItems().GroupBy(i => i.m_shared.m_name).Select(x => (name: Localization.instance.Localize(x.Key), count: x.Sum(i => i.m_stack))).OrderByDescending(x => x.count).ToList();
            return g.Count == 0 ? "nothing" : string.Join(", ", g.Take(n).Select(x => $"{x.name} {x.count}")) + (g.Count > n ? $", +{g.Count - n} more" : "");
        }

        // ---- context menu ------------------------------------------------------------------------------------------------------
        private sealed class Provider : IContextMenuProvider
        {
            public string TitleFor(ContextTarget target) => target?.MarkerKind == Kind && target.Marker is Target t ? t.Name : null;
            public IEnumerable<ContextMenuItem> GetItems(ContextTarget target)
            {
                if (target?.MarkerKind != Kind || !(target.Marker is Target t)) yield break;
                switch (t.Type)
                {
                    case "Companion":
                        yield return ContextMenuItem.Row("Send to work here... (click the map)", () => ArmSend(t));
                        yield return ContextMenuItem.Sep();
                        var c = t.Go != null ? t.Go.GetComponent<CompanionController>() : null;
                        foreach (var j in JobBoard.Jobs.Where(x => x.Repeat != JobBoard.Repeat.Now).Take(8))
                        {
                            var job = j;
                            yield return ContextMenuItem.Row("Assign: " + JobBoard.Label(job), () => BoardAssign(t, c, job, true), c != null);
                        }
                        yield return ContextMenuItem.Row("Unassign from all board jobs", () => BoardUnassign(t, c), c != null);
                        break;
                    case "Workbench": case "Smelter": case "Fire": case "Cooking":
                        var k = KindFor(t.Type);
                        foreach (var cc in JobBoard.CompanionsAtBase().Take(6))
                        {
                            var comp = cc;
                            yield return ContextMenuItem.Row($"Assign worker: {comp.GetDisplayName()}", () => BoardAssign(t, comp, FindOrAdd(k), false));
                        }
                        yield return ContextMenuItem.Row($"Run its job now ({k})", () => RunNow(t, k));
                        break;
                    case "Animal":   // 0.2.870: hunt this animal (a Now hunting trip on it)
                        foreach (var a in new CompanionController[] { null }.Concat(JobBoard.CompanionsAtBase().Take(8)))
                        {
                            var ac = a;
                            yield return ContextMenuItem.Row($"Hunt this: {(ac == null ? "Nearest free" : ac.GetDisplayName())}", () => AreaOrder("Hunt here", t.Pos, 30f, ac ?? NearestFree(JobBoard.CompanionsAtBase(), t.Pos)));
                        }
                        break;
                    case "Loot":     // 0.2.870: pick up the loot here (an 8 m work area)
                        foreach (var a in new CompanionController[] { null }.Concat(JobBoard.CompanionsAtBase().Take(8)))
                        {
                            var ac = a;
                            yield return ContextMenuItem.Row($"Pick up loot here: {(ac == null ? "Nearest free" : ac.GetDisplayName())}", () => AreaOrder("Work here (loot)", t.Pos, 8f, ac ?? NearestFree(JobBoard.CompanionsAtBase(), t.Pos)));
                        }
                        break;
                    case "Chest": case "Terminal":
                        yield return ContextMenuItem.Row("Show contents", () => Select(t, false, (Vector2)UnityEngine.Input.mousePosition));
                        break;
                    case "Tree": case "Ore": case "Pickable": case "Crop":
                        yield return ContextMenuItem.Row("Harvest now (companions at base)", () => RunNow(t, KindFor(t.Type)));
                        break;
                }
            }
        }
        private static Provider s_provider;
        private static void EnsureProvider() { if (s_provider == null) { s_provider = new Provider(); FiresContextMenu.Register(s_provider); } }

        private static JobBoard.Kind KindFor(string type)
        {
            switch (type)
            {
                case "Smelter": return JobBoard.Kind.Smelt;
                case "Fire": return JobBoard.Kind.Fires;
                case "Cooking": return JobBoard.Kind.Cook;
                case "Tree": return JobBoard.Kind.Wood;
                case "Ore": return JobBoard.Kind.MineHome;
                case "Pickable": case "Crop": return JobBoard.Kind.Farm;
                default: return JobBoard.Kind.Craft;
            }
        }

        private static JobBoard.Job FindOrAdd(JobBoard.Kind k)
            => JobBoard.Jobs.FirstOrDefault(j => j.Kind == k && j.Repeat == JobBoard.Repeat.Daily) ?? JobBoard.Add(k, JobBoard.Repeat.Daily);

        private static void BoardAssign(Target t, CompanionController c, JobBoard.Job j, bool companionClicked)
        {
            if (c == null || j == null) { Log(t, "assign", false, c == null ? "the companion isn't loaded" : "no job"); return; }
            JobBoard.Assign(j, c, true);
            Log(t, $"assign {c.GetDisplayName()} to {JobBoard.Label(j)}", true, null);
        }

        private static void BoardUnassign(Target t, CompanionController c)
        {
            if (c == null) { Log(t, "unassign", false, "the companion isn't loaded"); return; }
            int n = 0;
            foreach (var j in JobBoard.Jobs) if (j.Assigned.Remove(c.companionId)) { n++; JobBoard.Saved(j); }
            Log(t, "unassign", n > 0, n > 0 ? null : "not assigned to any board job");
        }

        private static void RunNow(Target t, JobBoard.Kind k)
        {
            var j = JobBoard.Add(k, JobBoard.Repeat.Now);
            JobBoard.Saved(j);
            Log(t, $"run now: {JobBoard.Label(j)}", true, null);
        }

        private static void Log(Target t, string action, bool ok, string why)
        {
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click {t.Type} '{t.Name}' -> {action} ({(ok ? "accepted" : "refused: " + why)})");
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, ok ? $"{t.Name}: {action}" : $"{t.Name}: {action} refused ({why})");
        }

        // ---- send-to: the next left click on the map -----------------------------------------------------------------------
        private static Target s_sendFrom;
        private static readonly System.Reflection.MethodInfo s_screenToWorld = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
        // ---- 0.2.870: right-click anything on the map / right-drag an area ------------------------------------------------------
        // Fire 10-27: "we should be able to right click all these things, the mushrooms the stumps the trees, animals right click and
        // drag a circle to have a companion go to an area for gathering/hunting etc or to pickup dropped items/loot".
        //  - A short right-click on the map itself (not a marker or the popup) picks the nearest world object within 4 m of that point
        //    (animals, ItemDrop loot, and every type the map scans: pickables, trees / stumps / logs, deposits, crops, stations, chests),
        //    with or without a marker, and opens its popup.
        //  - A right-DRAG draws a gold circle live; on release (radius >= 3 m) the area popup offers, per companion at base or "Nearest
        //    free": Work here (gather / chop / pick up loot: a server-checked area order = stay at the centre with the work radius set to
        //    the circle), Hunt here (a "Now" Hunting-trip board job on the circle) and Mine here (a "Now" Mining-trip board job on it).
        //  Lines: "[HomeMap] right-click at (x, z) -> <type> '<name>' (D m) / nothing within 4 m", "[HomeMap] area drawn at (x, z) r=R m",
        //  "[HomeMap] area order <kind> at (x, z) r=R by <player> -> <companion> (sent / board job #N)".
        private static bool s_rDown; private static Vector2 s_rStart; private static Vector3 s_rWorld; private static RectTransform s_ring;
        private const float DragPx = 8f, PickRadius = 4f;

        private static Vector3? MapWorld(Minimap mm, Vector2 screen)
        {
            if (s_screenToWorld == null) return null;
            var w = (Vector3)s_screenToWorld.Invoke(mm, new object[] { (Vector3)screen });
            if (ZoneSystem.instance != null) w.y = ZoneSystem.instance.GetGroundHeight(w);
            return w;
        }

        private static void AreaTick()
        {
            var mm = Minimap.instance;
            if (mm == null || mm.m_largeRoot == null) return;
            Vector2 mouse = UnityEngine.Input.mousePosition;
            if (!s_rDown && UnityEngine.Input.GetMouseButtonDown(1) && s_sendFrom == null && OnTheMap(mm) && !Over(s_pop) && !Over(s_sub))
            {
                var w = MapWorld(mm, mouse);
                if (w.HasValue) { s_rDown = true; s_rStart = mouse; s_rWorld = w.Value; }
                return;
            }
            if (!s_rDown) return;
            float px = Vector2.Distance(mouse, s_rStart);
            var root = (RectTransform)mm.m_largeRoot.transform;
            if (UnityEngine.Input.GetMouseButton(1))
            {
                if (px < DragPx) return;
                if (s_ring == null)
                {
                    var go = new GameObject("FiresHomeMapAreaRing", typeof(RectTransform));
                    s_ring = (RectTransform)go.transform; s_ring.SetParent(root, false);
                    s_ring.anchorMin = s_ring.anchorMax = Vector2.zero; s_ring.pivot = new Vector2(0.5f, 0.5f);
                    // 0.2.943 (Fire 10-27): the preview is the same thick ring over a shade as the saved circles
                    var shade = go.AddComponent<Image>(); shade.sprite = FiresCore.UI.Shell.Kit.KitSprites.Circle(); shade.color = new Color(1f, 0.84f, 0.3f, HomesteadMapCircles.ShadeAlpha); shade.raycastTarget = false;
                    var ol = new GameObject("Outline", typeof(RectTransform)); var ort = (RectTransform)ol.transform; ort.SetParent(s_ring, false);
                    ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one; ort.offsetMin = ort.offsetMax = Vector2.zero;
                    var img = ol.AddComponent<Image>(); img.sprite = HomesteadMapCircles.RingSprite(); img.color = FiresTokens.Gold; img.raycastTarget = false;
                }
                s_ring.SetAsLastSibling();
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, s_rStart, null, out Vector2 c);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, mouse, null, out Vector2 e);
                // 0.2.943 (Fire 10-27: "rather than the center originating from our click point ... the selection area should click and drag in
                // the direction we move the mouse ... draws a circle between the start point and the cursor"): start and cursor are opposite
                // edges - the centre is their midpoint, the diameter the drag
                Vector2 mid = (c + e) * 0.5f; float dl = Vector2.Distance(c, e);
                s_ring.anchoredPosition = mid - root.rect.min; s_ring.sizeDelta = new Vector2(dl, dl);
                s_ring.gameObject.SetActive(true);
                return;
            }
            // released
            s_rDown = false;
            if (s_ring != null) s_ring.gameObject.SetActive(false);
            if (px < DragPx) { if (HomesteadMapCircles.TryOpenAt(mouse)) return; WorldClick(s_rWorld, mouse); return; }   // 0.2.890: a circle under the click opens its menu (markers already won before this)
            var end = MapWorld(mm, mouse);
            if (!end.HasValue) return;
            // 0.2.943: the circle spans start -> cursor (the centre is the midpoint, the radius half the drag)
            Vector3 centre = (s_rWorld + end.Value) * 0.5f;
            if (ZoneSystem.instance != null) centre.y = ZoneSystem.instance.GetGroundHeight(centre);
            float r = new Vector2(end.Value.x - s_rWorld.x, end.Value.z - s_rWorld.z).magnitude * 0.5f;
            if (r < AreaMin) { WorldClick(s_rWorld, mouse); return; }
            r = Mathf.Min(r, AreaMax);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area drawn from ({s_rWorld.x:0}, {s_rWorld.z:0}) to ({end.Value.x:0}, {end.Value.z:0}): centre ({centre.x:0}, {centre.z:0}) r={r:0} m (start and cursor are opposite edges)");
            HomesteadMapCircles.Create(centre, r);   // 0.2.890: the circle STAYS; right-click it for orders (was: a popup on release)
        }

        private static void WorldClick(Vector3 at, Vector2 screen)
        {
            GameObject best = null; string bestType = null; float bd = float.MaxValue;
            foreach (var col in Physics.OverlapSphere(at, PickRadius + 2f))
            {
                if (col == null) continue;
                GameObject go = null; string type = null;
                var ch = col.GetComponentInParent<Character>();
                if (ch != null)
                {
                    if (ch.IsPlayer() || ch.IsDead() || ch.GetComponent<CompanionController>() != null) continue;
                    go = ch.gameObject; type = "Animal";
                }
                else if (col.GetComponentInParent<ItemDrop>() is ItemDrop drop) { go = drop.gameObject; type = "Loot"; }
                else
                {
                    var nv = col.GetComponentInParent<ZNetView>();
                    go = (col.GetComponentInParent<MineRock5>() as Component ?? col.GetComponentInParent<MineRock>() as Component ?? col.GetComponentInParent<Destructible>() as Component ?? (nv != null ? nv : null))?.gameObject;
                    if (go != null) type = HomesteadMap.TypeOf(go) ?? (go.GetComponent<Destructible>() != null ? "Tree" : null);   // a stump is a Destructible that drops wood
                }
                if (go == null || type == null) continue;
                float d = new Vector2(go.transform.position.x - at.x, go.transform.position.z - at.z).magnitude;
                if (d > PickRadius || d >= bd) continue;
                bd = d; best = go; bestType = type;
            }
            if (best == null)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] right-click at ({at.x:0}, {at.z:0}) -> nothing within {PickRadius:0} m");
                return;
            }
            var bnv = best.GetComponent<ZNetView>();
            var t = new Target { Type = bestType, Name = bestType == "Animal" ? Localization.instance.Localize(best.GetComponent<Character>().m_name) : bestType == "Loot" ? Localization.instance.Localize(best.GetComponent<ItemDrop>().m_itemData.m_shared.m_name) : HomesteadMap.NameOf(best),
                                 Pos = best.transform.position, Go = best, Id = bnv != null && bnv.IsValid() ? bnv.GetZDO().m_uid : ZDOID.None };
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] right-click at ({at.x:0}, {at.z:0}) -> {t.Type} '{t.Name}' ({bd:0.0} m)");
            Select(t, true, screen);
        }

        private static void AreaPopup(Vector3 c, float r, Vector2 screen)
        {
            var items = new List<ContextMenuItem>();
            var comps = JobBoard.CompanionsAtBase();
            foreach (var kind in new[] { "Work here (gather / chop / loot)", "Hunt here", "Mine here", "Tame here" })
            {
                string k = kind;
                items.Add(ContextMenuItem.Row($"{k}: Nearest free", () => AreaOrder(k, c, r, NearestFree(comps, c))));
                foreach (var comp in comps.Take(8)) { var cc = comp; items.Add(ContextMenuItem.Row($"{k}: {cc.GetDisplayName()}", () => AreaOrder(k, c, r, cc))); }
            }
            var t = new Target { Type = "Area", Name = $"Area r={r:0} m", Pos = c };
            int rows = ShowPopup($"Area ({r:0} m)", $"Centre ({c.x:0}, {c.z:0}); {comps.Count} companion(s) at base", items, t, screen);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area popup with {rows} row(s) at ({c.x:0}, {c.z:0}) r={r:0} m");
        }

        private static CompanionController NearestFree(List<CompanionController> comps, Vector3 c)
            => comps.Where(x => x != null && !(x.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior is TripBehavior))
                    .OrderBy(x => Vector3.Distance(x.transform.position, c)).FirstOrDefault();

        private static void AreaOrder(string kind, Vector3 c, float r, CompanionController comp)
        {
            string who = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "?";
            if (comp == null) { FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area order {kind} at ({c.x:0}, {c.z:0}) r={r:0} by {who} -> nobody (refused: no free companion at base)"); Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No free companion at base"); return; }
            var nv = comp.GetComponent<ZNetView>();
            var t = new Target { Type = "Companion", Name = comp.GetDisplayName(), Pos = comp.transform.position, Go = comp.gameObject, Id = nv != null && nv.IsValid() ? nv.GetZDO().m_uid : ZDOID.None };
            if (kind.StartsWith("Work") || kind.StartsWith("Tame"))   // 0.2.873: "Tame here" = the same area order; the taming chore searches around the new home
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area order work at ({c.x:0}, {c.z:0}) r={r:0} by {who} -> {t.Name} (sent)");
                Send(t, "area", c, r);
                return;
            }
            var jk = kind.StartsWith("Hunt") ? JobBoard.Kind.HuntTrip : JobBoard.Kind.MineTrip;
            var j = JobBoard.Add(jk, JobBoard.Repeat.Now);
            j.Center = c; j.Radius = Mathf.Clamp(r, 30f, 100f);
            JobBoard.Assign(j, comp, false);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area order {(jk == JobBoard.Kind.HuntTrip ? "hunt" : "mine")} at ({c.x:0}, {c.z:0}) r={j.Radius:0} by {who} -> {t.Name} (board job #{j.Id}{(r < 30f ? $", radius raised from {r:0} m to the trip minimum 30 m" : "")})");
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, $"{t.Name}: {kind}");
        }

        internal static void Tick()
        {
            PopupTick();
            HomesteadMapCircles.Tick();   // 0.2.890: the work circles (draw, done-scan)
            TickPin();
            AreaTick();
            if (s_sendFrom == null) return;
            // 0.2.868 (Fire 10-27 shot 588: "send to should queue up and wait for the next click somewhere on the map... it does not work";
            // his log shows it armed, sent "(308, 960)", accepted and applied as a stay point there - with no hint, no target mark and no
            // walk line it looked like nothing happened): while armed a hint repeats ("Click a spot for <name> (Esc cancels)"), Esc
            // cancels, the next left click on the map itself (not a marker, not a window) is the target, and a pin marks it until the
            // next send (or 3 min). Lines: "[HomeMap] send-to armed for <name>", "[HomeMap] send-to <name>: click at (x, z) -> sent",
            // then the result line, and "[HomeMap] apply send-to <name> -> (x, z) (D m away)" where it runs.
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] send-to {s_sendFrom.Name}: cancelled (Esc)"); s_sendFrom = null; return; }
            if (Time.unscaledTime >= s_hintAt) { s_hintAt = Time.unscaledTime + 2.5f; Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Click a spot for {s_sendFrom.Name} (Esc cancels)"); }
            if (!UnityEngine.Input.GetMouseButtonDown(0)) return;
            var mm = Minimap.instance;
            if (mm == null || s_screenToWorld == null || !OnTheMap(mm)) return;
            var w = (Vector3)s_screenToWorld.Invoke(mm, new object[] { UnityEngine.Input.mousePosition });
            if (ZoneSystem.instance != null) w.y = ZoneSystem.instance.GetGroundHeight(w);
            var t = s_sendFrom; s_sendFrom = null;
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] send-to {t.Name}: click at ({w.x:0}, {w.z:0}) -> sent");
            try
            {
                if (s_sendPin != null) mm.RemovePin(s_sendPin);
                s_sendPin = mm.AddPin(w, Minimap.PinType.Icon3, t.Name, false, false);
                s_sendPinUntil = Time.unscaledTime + 180f;
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] send-to pin failed: " + e.Message); }
            Send(t, "send", w);
        }
        private static float s_hintAt, s_sendPinUntil;
        private static Minimap.PinData s_sendPin;
        internal static void ArmSend(Target t)
        {
            s_sendFrom = t; s_hintAt = 0f;
            ClosePopup(false);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] send-to armed for {t.Name}");
        }
        internal static void TickPin()
        {
            if (s_sendPin != null && Time.unscaledTime > s_sendPinUntil) { try { Minimap.instance?.RemovePin(s_sendPin); } catch { } s_sendPin = null; }
        }
        // the click landed on the large map itself (its top UI hit is under the map's root and is not a marker), not on a window
        private static bool OnTheMap(Minimap mm)
        {
            if (EventSystem.current == null || mm.m_largeRoot == null) return false;
            var ped = new PointerEventData(EventSystem.current) { position = UnityEngine.Input.mousePosition };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(ped, hits);
            if (hits.Count == 0 || hits[0].gameObject == null) return false;
            var top = hits[0].gameObject;
            return top.transform.IsChildOf(mm.m_largeRoot.transform) && top.GetComponentInParent<MarkerClick>() == null;
        }

        // ---- RPCs ----------------------------------------------------------------------------------------------------------
        private static int s_req;
        private static void Send(Target t, string action, Vector3 pos, float radius = 0f)
        {
            if (ZRoutedRpc.instance == null || t.Id.IsNone()) { Log(t, action, false, "not on the network"); return; }
            var pkg = new ZPackage();
            pkg.Write(++s_req); pkg.Write(action); pkg.Write(t.Id); pkg.Write(pos); pkg.Write(radius);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click Companion '{t.Name}' -> {action}{(action == "send" ? $" to ({pos.x:0}, {pos.z:0})" : "")} (sent to the server, request {s_req})");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), CmdRpc, pkg);
        }

        private static ZRoutedRpc s_registeredOn;
        internal static void Register()
        {
            var r = ZRoutedRpc.instance;
            if (r == null || s_registeredOn == r) return;
            s_registeredOn = r;
            r.Register<ZPackage>(CmdRpc, OnCmd);
            r.Register<ZPackage>(ApplyRpc, OnApply);
            r.Register<ZPackage>(ResultRpc, OnResult);
        }

        private static long PlayerIdOf(long peerUid)
        {
            if (ZNet.instance == null) return 0L;
            if (peerUid == ZNet.GetUID() || peerUid == ZRoutedRpc.instance.m_id) return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;
            var peer = ZNet.instance.GetPeer(peerUid);
            if (peer == null || peer.m_characterID.IsNone()) return 0L;
            var z = ZDOMan.instance?.GetZDO(peer.m_characterID);
            return z != null ? z.GetLong(ZDOVars.s_playerID, 0L) : 0L;
        }

        // server: validate, then forward to the instance owner
        private static void OnCmd(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int req = 0; string action = "?", why = null; ZDOID id = ZDOID.None; Vector3 pos = Vector3.zero; float radius = 0f; string who = "peer " + sender, name = "?";
            try
            {
                if (pkg == null || pkg.Size() > MaxPackage) why = $"package {(pkg == null ? 0 : pkg.Size())} bytes (max {MaxPackage})";
                else { req = pkg.ReadInt(); action = pkg.ReadString(); id = pkg.ReadZDOID(); pos = pkg.ReadVector3(); radius = pkg.ReadSingle(); }
            }
            catch (Exception e) { why = "bad package: " + e.Message; }
            long pid = PlayerIdOf(sender);
            var peer = ZNet.instance.GetPeer(sender); if (peer != null) who = peer.m_playerName;
            ZDO z = null;
            string baseAction = action.Split(':')[0], focus = action.Contains(":") ? action.Substring(action.IndexOf(':') + 1) : "";
            if (why == null && (!Allowed.Contains(baseAction) || (focus.Length > 0 && (baseAction != "area" || !Foci.Contains(focus))))) why = $"unknown action '{action}'";   // 0.2.890: "area:<focus>", focus whitelisted
            if (why == null && pid == 0L) why = "no player for the sender";
            if (why == null && (z = ZDOMan.instance.GetZDO(id)) == null) why = "no such companion";
            if (why == null) { name = z.GetString("companion_name", z.GetString(ZDOVars.s_tamedName, "companion")); if (z.GetLong("companion_owner", 0L) != pid) why = "not your companion"; }
            if (why == null && action != "follow" && float.IsNaN(pos.x + pos.y + pos.z)) why = "bad position";
            if (why == null && baseAction == "area" && (float.IsNaN(radius) || radius < AreaMin || radius > AreaMax)) why = $"area radius {radius:0.0} m (allowed {AreaMin:0}-{AreaMax:0})";
            if (why == null && action != "follow" && WardRefuses(pos, pid, out string ward)) why = ward;
            if (why == null && z.GetOwner() == 0L) why = "the companion isn't loaded anywhere right now";
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] server: {who} {action} on {name} {(why == null ? $"accepted (runs on peer {z.GetOwner()})" : "refused: " + why)}");
            if (why != null) { Reply(sender, req, action, name, false, why); return; }
            var fw = new ZPackage(); fw.Write(req); fw.Write(sender); fw.Write(pid); fw.Write(action); fw.Write(id); fw.Write(pos); fw.Write(radius);
            ZRoutedRpc.instance.InvokeRoutedRPC(z.GetOwner(), ApplyRpc, fw);
        }

        private static bool WardRefuses(Vector3 pos, long pid, out string why)
        {
            why = null;
            var all = Traverse.Create(typeof(PrivateArea)).Field("m_allAreas").GetValue<List<PrivateArea>>();
            if (all == null) return false;
            var permitted = AccessTools.Method(typeof(PrivateArea), "IsPermitted", new[] { typeof(long) });
            foreach (var a in all)
            {
                if (a == null || !a.IsEnabled() || !a.IsInside(pos, 0f)) continue;
                var piece = a.GetComponent<Piece>();
                if (piece != null && piece.GetCreator() == pid) continue;
                if (permitted != null && (bool)permitted.Invoke(a, new object[] { pid })) continue;
                why = "a ward there doesn't allow you";
                return true;
            }
            return false;
        }

        private static void Reply(long to, int req, string action, string name, bool ok, string why)
        {
            var r = new ZPackage(); r.Write(req); r.Write(action); r.Write(name ?? ""); r.Write(ok); r.Write(why ?? "");
            ZRoutedRpc.instance.InvokeRoutedRPC(to, ResultRpc, r);
        }

        // instance owner: only from the server
        private static void OnApply(long sender, ZPackage pkg)
        {
            if (sender != ZRoutedRpc.instance.GetServerPeerID() && !(ZNet.instance != null && ZNet.instance.IsServer() && sender == ZRoutedRpc.instance.m_id))
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] apply refused: not from the server (peer {sender})");
                return;
            }
            if (pkg == null || pkg.Size() > MaxPackage) return;
            int req = pkg.ReadInt(); long requester = pkg.ReadLong(); long pid = pkg.ReadLong(); string action = pkg.ReadString(); var id = pkg.ReadZDOID(); var pos = pkg.ReadVector3(); float radius = pkg.ReadSingle();
            string why = null, name = "?";
            var inst = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
            var c = inst != null ? inst.GetComponent<CompanionController>() : null;
            if (c == null) why = "the companion isn't loaded here";
            else
            {
                name = c.GetDisplayName();
                if (c.ownerPlayerId != pid) why = "not their companion";
                else if (action == "follow")
                {
                    var p = Player.GetAllPlayers().FirstOrDefault(x => x != null && x.GetPlayerID() == pid);
                    if (p == null) why = "you aren't near enough for them to follow (no player body here)";
                    else c.CommandFollow(p);
                }
                else if (action == "area" || action.StartsWith("area:"))
                {
                    // 0.2.870: an area order = stay at the circle's centre with the work radius set to the circle, so its own chores
                    // (gathering, chopping, mining, loot pickup) search only inside it
                    // 0.2.890: with a work FOCUS ("area:chop" ...) the owner also starts THAT chore's own behaviour at once
                    float r = Mathf.Clamp(radius, AreaMin, AreaMax);
                    c.CommandStay(pos);
                    var idle = c.GetComponent<CompanionIdleBehavior>();
                    idle?.SetHomePosition(pos, r);
                    string focus = action.Length > 5 ? action.Substring(5) : "";
                    string started = focus.Length == 0 || idle == null ? "its own chores" : StartFocus(idle, focus);
                    FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] apply area order {(focus.Length > 0 ? focus + " " : "")}on {c.GetDisplayName()} -> ({pos.x:0}, {pos.z:0}) r={r:0} m ({Vector3.Distance(c.transform.position, pos):0} m away) -> started {started}");
                }
                else { if (action == "send") FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] apply send-to {c.GetDisplayName()} -> ({pos.x:0}, {pos.z:0}) ({Vector3.Distance(c.transform.position, pos):0} m away; walks there and works within its stay radius)"); c.CommandStay(action == "stay" ? c.transform.position : pos); }
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] apply {action} on {name}: {(why == null ? "done" : "refused: " + why)}");
            Reply(requester, req, action, name, why == null, why);
        }

        // requester: show the outcome (only for requests this client sent)
        private static void OnResult(long sender, ZPackage pkg)
        {
            if (pkg == null || pkg.Size() > MaxPackage) return;
            int req = pkg.ReadInt(); string action = pkg.ReadString(); string name = pkg.ReadString(); bool ok = pkg.ReadBool(); string why = pkg.ReadString();
            if (req <= 0 || req > s_req) return;
            if (name.Length > 64) name = name.Substring(0, 64); if (why.Length > 160) why = why.Substring(0, 160);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] click Companion '{name}' -> {action} ({(ok ? "accepted" : "refused: " + why)}, request {req})");
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, ok ? $"{name}: {action} done" : $"{name}: {action} refused ({why})");
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_HomeMapRpc { private static void Postfix() => Register(); }
    }
}
