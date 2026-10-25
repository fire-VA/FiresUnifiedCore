using System;
using System.Collections.Generic;
using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// vanilla InventoryGui's crafting panel in the
    /// Fires UI kit look when <see cref="FiresShell.OverrideOn"/>. IN PLACE: the panel's own objects are restyled, nothing is rebuilt or
    /// replaced, so every mod's crafting patch keeps working on the same objects. Every sprite / Image.Type / colour / material / font is
    /// recorded the first time it changes and written back when the override goes off: OFF = today, exactly.
    /// (VAInventory InventoryTabs.LayoutRow re-lays the tab row EVERY FRAME: rect, fontSize, text): only Image sprite / type /
    /// colour / material and TMP colour / fontMaterial are touched here, never a rect, fontSize or text; the tab row gets no font swap.
    /// Placement (the crafting root as the right panel) is VAInventory's dock, not this skin. World backdrop: no sky behind it.
    /// <see cref="ApplyTo"/> is public for mods that build their own crafting-tab content (FATI) and want the same look.
    /// One Debug line per apply ("[CraftSkin] ...").
    /// </summary>
    public static class CraftingSkin
    {
        private enum Role { Keep, Plate, Well, Raised, Selected, Slot, Heading, Body, Muted, Value, TabPill, TabPillOn, TabText, TabTextOn }

        // the tab row as the golden's
        // full kit pills (PillRadius 15), the selected one Selected + a Gold outline, labels Text / Gold. Shape and colour only; the
        // tabs' rects, fontSize and text stay (VAInventory InventoryTabs.LayoutRow re-lays them every frame).
        private const string PillOutlineName = "KitPillOutline";
        private const string SlotOutlineName = "KitSlotOutline";
        private static readonly List<GameObject> s_pillOutlines = new List<GameObject>();

        private sealed class Orig { public Sprite Sprite; public Image.Type Type; public Color Color; public Material Material; public TMP_FontAsset Font; public Material FontMaterial; }
        private static readonly Dictionary<Graphic, Orig> s_orig = new Dictionary<Graphic, Orig>();
        private static readonly HashSet<RectTransform> s_roots = new HashSet<RectTransform>();
        private static bool s_hooked;
        private static readonly Dictionary<RectTransform, bool> s_rootState = new Dictionary<RectTransform, bool>();

        private static bool On { get { try { return FiresShell.OverrideOn; } catch { return false; } } }

        /// <summary>Restyle (override ON) or restore (OFF) any crafting-tab content root with the shared classifier (FATI opts in here).</summary>
        /// <returns>How many graphics were restyled (override ON) or restored (OFF). Idempotent: call it after every rebuild.</returns>
        public static int ApplyTo(RectTransform root)
        {
            if (root == null) return 0;
            Hook();
            s_roots.Add(root);
            s_slotCount = 0;
            int n = Walk(root, null);
            bool on = On;
            if (!s_rootState.TryGetValue(root, out bool was) || was != on)   // once per root per state: callers rebuild often (FATI pages)
            {
                s_rootState[root] = on;
                Debug.Log($"[FiresUnifiedCore] [CraftSkin] {root.name}: {(on ? "kit look" : "classic look")} ({n} graphic(s))");
                if (on) Debug.Log($"[FiresUnifiedCore] [CraftSkin] {root.name}: {s_slotCount} slot(s) styled as Slot, {s_origTint.Count} slot tint(s) neutralised");
            }
            return n;
        }

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            try { FiresShell.OverrideChanged += _ => RefreshAll(); } catch { }
        }

        private static void RefreshAll()
        {
            s_roots.RemoveWhere(r => r == null);
            foreach (var r in new List<RectTransform>(s_roots)) Walk(r, InventoryGui.instance);
            // the added kit pieces (wells, title rule, craft outline) follow the override
            bool onNow = On;
            s_extras.RemoveAll(o => o == null);
            foreach (var o in s_extras) if (o.activeSelf != onNow) o.SetActive(onNow);
            if (s_recipeWell != null && s_recipeWell.activeSelf != onNow) s_recipeWell.SetActive(onNow);
            Debug.Log($"[FiresUnifiedCore] [CraftSkin] override {(On ? "ON" : "OFF")}: {s_roots.Count} root(s) {(On ? "restyled" : "restored")}");
        }

        // The whole crafting root, classified with the vanilla field references where we have them.
        private static int Walk(RectTransform root, InventoryGui gui)
        {
            bool on = On; int n = 0;
            if (!on) { HidePillOutlines(); RestoreTints(); }   // OFF = today: the pill outlines go, slot tints come back (faces restored below)
            Transform tabRow = gui != null && gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent : null;
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                if (OptedOut(g)) continue;   // kit widgets (KitLeaf) and anything under a "NoKitSkin" object stay as built
                if (!on) { Restore(g); continue; }
                Role r = Classify(g, gui, tabRow);
                if (r == Role.Keep) continue;
                Record(g);
                bool inTabs = tabRow != null && g.transform.IsChildOf(tabRow);
                if (Style(g, r, allowFont: !inTabs)) n++;
            }
            return n;
        }

        /// <summary>Per-element opt-out: a KitLeaf (already a kit widget), or the graphic or any parent named with "NoKitSkin".</summary>
        private static bool OptedOut(Graphic g)
        {
            if (g.GetComponent<KitLeaf>() != null) return true;
            for (var t = g.transform; t != null; t = t.parent) if (t.name.IndexOf("NoKitSkin", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static Role Classify(Graphic g, InventoryGui gui, Transform tabRow)
        {
            string n = g.name;
            if (g is TMP_Text t)
            {
                if (gui != null && (t == gui.m_recipeName || t == gui.m_craftingStationName)) return Role.Heading;
                if (gui != null && (t == gui.m_recipeDecription)) return Role.Body;
                if (gui != null && (t == gui.m_itemCraftType || t == gui.m_minStationLevelText)) return Role.Keep;   // vanilla recolours these per frame
                if (n.IndexOf("res_amount", StringComparison.Ordinal) >= 0) return Role.Keep;                           // vanilla: red when missing; CraftPull's "(+N)"
                if (tabRow != null && g.transform.IsChildOf(tabRow)) { var tb = g.GetComponentInParent<Button>(); return tb != null && !tb.interactable ? Role.TabTextOn : Role.TabText; }
                if (n.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Header", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Heading;
                if (n.IndexOf("quality", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Value;
                return Role.Body;
            }
            if (!(g is Image img) || g.color.a < 0.01f) return Role.Keep;
            if (gui != null && (img == gui.m_recipeIcon || img == gui.m_craftingStationIcon || img == gui.m_upgradeItemIcon || img == gui.m_minStationLevelIcon || img == gui.m_repairButtonGlow)) return Role.Keep;
            if (n.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Icon", StringComparison.Ordinal) >= 0) return Role.Keep;        // real item icons stay
            // Panels mirrors (VAInventory 1.0.161): the repair "Glow" (mirrors m_repairButtonGlow) and the PvP toggle's "Check" stay vanilla
            if (n.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) >= 0 || n == "Check" || n.IndexOf("Checkmark", StringComparison.OrdinalIgnoreCase) >= 0) return Role.Keep;
            if (n.IndexOf("bar", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Scrollbar", StringComparison.Ordinal) >= 0 || n.IndexOf("Handle", StringComparison.Ordinal) >= 0) return Role.Keep;
            // tabs: the active one is non-interactable in vanilla
            if (tabRow != null && g.transform.IsChildOf(tabRow))
            {
                var b = g.GetComponentInParent<Button>();
                if (b == null || b.targetGraphic != g) return Role.Keep;   // only the tab's own face becomes the pill
                return !b.interactable ? Role.TabPillOn : Role.TabPill;
            }
            if (gui != null && gui.m_recipeListRoot != null && g.transform.IsChildOf(gui.m_recipeListRoot))
                return n.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0 ? Role.Selected : Role.Keep;   // mock: rows flat on the list well, only the selected row highlighted
            // an InventoryGrid element's own background (or VAInventory's "Cell_" square)
            // is a Slot: a lighter face + 1 px Hairline outline; everything under it (icon, amount, durability, equiped, queued) stays vanilla
            int slot = SlotKind(img);
            if (slot > 0) return Role.Slot;
            if (slot < 0) return Role.Keep;
            if (gui != null && gui.m_craftButton != null && g.transform.IsChildOf(gui.m_craftButton.transform)) return Role.Raised;
            if (g.GetComponentInParent<Button>() != null || g.GetComponentInParent<Toggle>() != null) return Role.Raised;   // incl. the Panels info-row PvP toggle
            var r = img.rectTransform.rect;
            if (r.width * r.height > 300f * 300f) return Role.Plate;
            return 0.3f * g.color.r + 0.59f * g.color.g + 0.11f * g.color.b > 0.5f ? Role.Well : Role.Raised;
        }

        private static bool Style(Graphic g, Role role, bool allowFont)
        {
            switch (role)
            {
                case Role.Plate:    return Fill(g, FiresTokens.Panel, 10f);
                case Role.Well:     return Fill(g, FiresTokens.Well, 6f);
                case Role.Raised:   return Fill(g, FiresTokens.PanelRaised, 6f);
                case Role.Selected: return Fill(g, FiresTokens.Selected, 6f);
                case Role.Slot:     Fill(g, FiresTokens.SlotFace, FiresTokens.RadiusSmall); Outline(g, FiresTokens.Hairline, FiresTokens.RadiusSmall, SlotOutlineName); NeutraliseTint(g); s_slotCount++; return true;
                case Role.Heading:  return Text(g, FiresTokens.Gold, allowFont ? FontRole.SerifBold : (FontRole?)null);
                case Role.Body:     return Text(g, FiresTokens.Text, allowFont ? FontRole.SerifRegular : (FontRole?)null);
                case Role.Muted:    return Text(g, FiresTokens.TextMuted, allowFont ? FontRole.SansRegular : (FontRole?)null);
                case Role.Value:    return Text(g, FiresTokens.GoldBright, allowFont ? FontRole.SansBold : (FontRole?)null);
                case Role.TabPill:   Fill(g, FiresTokens.PanelRaised, FiresTokens.PillRadius); PillOutline(g, FiresTokens.Hairline); return true;
                case Role.TabPillOn: Fill(g, FiresTokens.Selected, FiresTokens.PillRadius); PillOutline(g, FiresTokens.Gold); return true;
                case Role.TabText:   return Text(g, FiresTokens.Text, null);   // no font swap on the tab row (LayoutRow measures it)
                case Role.TabTextOn: return Text(g, FiresTokens.Gold, null);
            }
            return false;
        }

        private static bool Fill(Graphic g, Color c, float radius)
        {
            if (g is Image img)
            {
                img.sprite = radius > 0f ? KitSprites.RoundPill(radius, false) : null;
                img.type = radius > 0f ? Image.Type.Sliced : Image.Type.Simple;
                img.material = null;   // the VA panel materials (rune art) go; the kit plates are flat
            }
            g.color = c;
            return true;
        }

        /// <summary>The pill's 1 px outline: one KitLeaf child per tab face (the skin's own walk skips KitLeaf), stretched to the face;
        /// recoloured on every apply, hidden when the override goes off.</summary>
        private static void PillOutline(Graphic face, Color c) => Outline(face, c, FiresTokens.PillRadius, PillOutlineName);

        private static void Outline(Graphic face, Color c, float radius, string name)
        {
            Transform t = face.transform.Find(name);
            Image line;
            if (t == null)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(KitLeaf));
                var rt = (RectTransform)go.transform;
                rt.SetParent(face.transform, false);
                rt.SetSiblingIndex(0);   // under the label
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                line = go.GetComponent<Image>();
                line.sprite = KitSprites.RoundPill(radius, true); line.type = Image.Type.Sliced; line.raycastTarget = false;
                s_pillOutlines.Add(go);
            }
            else line = t.GetComponent<Image>();
            if (line == null) return;
            line.gameObject.SetActive(true);
            line.color = c;
        }

        private static void HidePillOutlines()
        {
            s_pillOutlines.RemoveAll(o => o == null);
            foreach (var o in s_pillOutlines) o.SetActive(false);
        }

        private static bool Text(Graphic g, Color c, FontRole? font)
        {
            if (!(g is TMP_Text t)) return false;
            if (font.HasValue) { var f = KitText.TmpFont(font.Value); if (f != null) t.font = f; }   // a font ASSET swap only off the tab row; never fontSize
            t.color = c;
            return true;
        }

        private static int s_slotCount;

        // an InventoryElement is a Selectable whose ColorTint normalColor is ~0.13 grey @0.5;
        // Unity multiplies the face by it, so SlotFace rendered near black. The face's own Selectable gets white tints (the face shows
        // exactly SlotFace); its ColorBlock is recorded once and written back on override OFF.
        private static readonly Dictionary<Selectable, ColorBlock> s_origTint = new Dictionary<Selectable, ColorBlock>();
        private static void NeutraliseTint(Graphic g)
        {
            var sel = g.GetComponent<Selectable>();
            if (sel == null || sel.targetGraphic != g || sel.transition != Selectable.Transition.ColorTint) return;
            if (!s_origTint.ContainsKey(sel)) s_origTint[sel] = sel.colors;
            var cb = sel.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = Color.white;
            cb.selectedColor = Color.white;
            cb.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            cb.colorMultiplier = 1f;
            sel.colors = cb;
            g.canvasRenderer.SetColor(Color.white);   // the running tint keeps the old dark value until the next state change otherwise
        }

        private static void RestoreTints()
        {
            int n = 0;
            foreach (var kv in s_origTint) if (kv.Key != null)
            {
                kv.Key.colors = kv.Value; n++;
                // NeutraliseTint had set the running tint to white on the canvasRenderer; Unity only re-applies the ColorBlock on the next
                // state change (hover / re-enable), so put the normal tint back on the renderer now
                var tg = kv.Key.targetGraphic;
                if (tg != null) tg.canvasRenderer.SetColor(kv.Value.normalColor * kv.Value.colorMultiplier);
            }
            s_origTint.Clear();
            if (n > 0) Debug.Log($"[FiresUnifiedCore] [CraftSkin] {n} slot tint(s) restored");
        }

        /// <summary>1 = a slot face (an InventoryElement, an InventoryGrid element's root Image, or an empty "Cell_" square); -1 = inside a slot (icon, amount, durability,
        /// equiped, queued: stays vanilla); 0 = not a slot.</summary>
        private static int SlotKind(Image img)
        {
            var tr = img.transform;
            // the equipment rail / pouch slots are element-prefab clones outside any grid root: the InventoryElement
            // component marks every slot face (grids, rails, pouches, backpacks); a "Cell_" holding one is only its frame
            if (tr.GetComponent<InventoryElement>() != null) return 1;
            for (var p = tr.parent; p != null; p = p.parent)
                if (p.GetComponent<InventoryElement>() != null) return -1;
            if (tr.name.StartsWith("Cell_", StringComparison.Ordinal))
                return tr.GetComponentInChildren<InventoryElement>(true) != null ? -1 : 1;
            var grid = img.GetComponentInParent<InventoryGrid>();
            if (grid == null || grid.m_gridRoot == null) return 0;
            if (tr.parent == grid.m_gridRoot) return 1;
            for (var p = tr.parent; p != null && p != grid.transform; p = p.parent)
                if (p.parent == grid.m_gridRoot || p.name.StartsWith("Cell_", StringComparison.Ordinal)) return -1;
            return 0;
        }

        private static void Record(Graphic g)
        {
            if (s_orig.ContainsKey(g)) return;
            var o = new Orig { Color = g.color, Material = g.material != g.defaultMaterial ? g.material : null };
            if (g is Image img) { o.Sprite = img.sprite; o.Type = img.type; }
            if (g is TMP_Text t) { o.Font = t.font; o.FontMaterial = t.fontSharedMaterial; }
            s_orig[g] = o;
        }

        private static void Restore(Graphic g)
        {
            if (!s_orig.TryGetValue(g, out var o)) return;
            g.color = o.Color;
            if (g is Image img) { img.sprite = o.Sprite; img.type = o.Type; img.material = o.Material; }
            if (g is TMP_Text t) { if (o.Font != null) t.font = o.Font; if (o.FontMaterial != null) t.fontSharedMaterial = o.FontMaterial; }
            s_orig.Remove(g);
        }

        // ---- hooks: after the panel is (re)built; never per frame -------------------------------------------------------------------
        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        private static class AfterCraftingPanel
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter("com.Fire.VAInventory")]
            private static void Postfix(InventoryGui __instance) => Apply(__instance, "UpdateCraftingPanel");
        }

        // a recipe row instantiated after the panel pass (list rebuilds on search / tab change) gets the same look
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipeList")]
        private static class AfterRecipeList
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter("com.Fire.VAInventory")]
            private static void Postfix(InventoryGui __instance)
            {
                if (!On || __instance == null || __instance.m_recipeListRoot == null) return;
                try { Hook(); s_roots.Add(__instance.m_crafting); Walk(__instance.m_recipeListRoot, __instance); }
                catch (Exception ex) { Debug.LogWarning("[FiresUnifiedCore] [CraftSkin] recipe rows: " + ex.Message); }
            }
        }

        // ---- Crafting panel kit pieces -----------------------------
        // (a) the recipe list sits in a kit WELL (Widgets.Well look: Well fill + Hairline frame) behind its viewport, so it reads as an inset
        // list instead of blending into the plate; (b) vanilla's station-level cell left of the requirements shows the required STATION
        // (its icon + "Name level", e.g. "Workbench 2"), and is hidden (with the empty square behind it) when no station is needed.
        private const string RecipeWellName = "KitRecipeWell_NoKitSkin";
        private static GameObject s_recipeWell;

        private static void RecipeWell(InventoryGui gui, bool on)
        {
            var viewport = gui != null && gui.m_recipeListRoot != null ? gui.m_recipeListRoot.parent as RectTransform : null;
            if (viewport == null) return;
            if (s_recipeWell == null)
            {
                if (!on) return;
                var n = Widgets.Well(viewport, RecipeWellName, 0f, 0f, 10f, 10f);
                n.anchorMin = Vector2.zero; n.anchorMax = Vector2.one; n.pivot = new Vector2(0.5f, 0.5f);
                n.offsetMin = new Vector2(-4f, -4f); n.offsetMax = new Vector2(4f, 4f);   // a 4 px frame around the rows
                n.SetSiblingIndex(0);   // under the rows
                foreach (var g in n.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                n.gameObject.AddComponent<KitLeaf>();
                s_recipeWell = n.gameObject;
            }
            if (s_recipeWell.activeSelf != on) s_recipeWell.SetActive(on);
        }

        // Split: layout / sections are VAInventory's, the look is Core's. A RectTransform under the crafting root named
        // "KitWell_<x>" gets an inset gold-edged WELL behind its content; a TMP text named "KitCaps_<x>" is a section label (gold caps).
        private const string WellPrefix = "KitWell_", CapsPrefix = "KitCaps_", WellChild = "KitSectionWell_NoKitSkin";
        private const string TitleRuleName = "KitTitleRule_NoKitSkin", CraftOutlineName = "KitCraftOutline";
        private static readonly List<GameObject> s_extras = new List<GameObject>();

        private static void SectionWells(RectTransform root, bool on)
        {
            foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rt == null || !rt.name.StartsWith(WellPrefix, StringComparison.Ordinal)) continue;
                var w = rt.Find(WellChild);
                if (w == null)
                {
                    if (!on) continue;
                    var n = Widgets.Well(rt, WellChild, 0f, 0f, 10f, 10f, FiresTokens.GoldDim, FiresTokens.Radius);
                    n.anchorMin = Vector2.zero; n.anchorMax = Vector2.one; n.pivot = new Vector2(0.5f, 0.5f);
                    n.offsetMin = Vector2.zero; n.offsetMax = Vector2.zero;
                    n.SetSiblingIndex(0);
                    foreach (var g in n.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                    n.gameObject.AddComponent<KitLeaf>();
                    s_extras.Add(n.gameObject);
                    w = n;
                }
                if (w.gameObject.activeSelf != on) w.gameObject.SetActive(on);
            }
            if (!on) return;
            foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == null || !t.name.StartsWith(CapsPrefix, StringComparison.Ordinal)) continue;
                Record(t);
                var f = KitText.TmpFont(FontRole.SansBold); if (f != null) t.font = f;
                t.color = FiresTokens.Gold;
                t.fontStyle |= FontStyles.UpperCase;
            }
        }

        // the panel title ("CRAFTING") gets the hub pages' gold rule with the diamond under it
        private static void TitleRule(RectTransform root, bool on)
        {
            TMP_Text title = null;
            foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
                if (t != null && t.isActiveAndEnabled && string.Equals(t.text?.Trim(), "Crafting", StringComparison.OrdinalIgnoreCase)) { title = t; break; }
            if (title == null) return;
            var trt = title.rectTransform;
            var rule = trt.Find(TitleRuleName);
            if (rule == null)
            {
                if (!on) return;
                float w = Mathf.Max(200f, trt.rect.width * 1.6f);
                var d = Widgets.Divider(trt, 0f, w);
                d.name = TitleRuleName;
                d.anchorMin = d.anchorMax = new Vector2(0.5f, 0f); d.pivot = new Vector2(0.5f, 1f);
                d.anchoredPosition = new Vector2(0f, -2f);
                foreach (var g in d.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                if (d.GetComponent<KitLeaf>() == null) d.gameObject.AddComponent<KitLeaf>();
                s_extras.Add(d.gameObject);
                rule = d;
            }
            if (rule.gameObject.activeSelf != on) rule.gameObject.SetActive(on);
        }

        // EVERY active Button in that row is a kit pill (PanelRaised + Hairline; the selected one, non-interactable in vanilla,
        // Selected + Gold), its label Text / Gold, and the pill is re-asserted per frame (cheap: only faces that drifted are touched).
        private static Sprite s_pillSprite;
        private static void TabPills(InventoryGui gui)
        {
            var row = gui != null && gui.m_tabCraft != null ? gui.m_tabCraft.transform.parent : null;
            if (row == null) return;
            if (s_pillSprite == null) s_pillSprite = KitSprites.RoundPill(FiresTokens.PillRadius, false);
            foreach (Transform c in row)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                var b = c.GetComponent<Button>(); if (b == null) continue;
                ButtonPill(b, !b.interactable);
            }
        }

        // tab pill, shared with the chest header buttons (Take all / Place stacks / Tags)
        private static void ButtonPill(Button b, bool sel)
        {
            if (s_pillSprite == null) s_pillSprite = KitSprites.RoundPill(FiresTokens.PillRadius, false);
            var c = b.transform;
            {
                var face = (b.targetGraphic as Image) ?? c.GetComponent<Image>(); if (face == null) return;
                Color want = sel ? FiresTokens.Selected : FiresTokens.PanelRaised;
                if (face.sprite != s_pillSprite || face.color != want)
                {
                    Record(face);
                    face.sprite = s_pillSprite; face.type = Image.Type.Sliced; face.material = null; face.color = want;
                    Outline(face, sel ? FiresTokens.Gold : FiresTokens.Hairline, FiresTokens.PillRadius, PillOutlineName);
                }
                // the Button's ColorTint multiplies the face on the
                // canvasRenderer (the selected tab is non-interactable = disabledColor), which Image.color never shows. White tints.
                if (b.transition == Selectable.Transition.ColorTint && b.colors.disabledColor != Color.white)
                {
                    NeutraliseTint(face);
                    var cb = b.colors; cb.disabledColor = Color.white; b.colors = cb;
                    face.canvasRenderer.SetColor(Color.white);
                }
                foreach (var t in c.GetComponentsInChildren<TMP_Text>(true))
                {
                    Color tc = sel ? FiresTokens.Gold : FiresTokens.Text;
                    if (t.color != tc) { Record(t); t.color = tc; }
                }
            }
        }

        // the container panel (m_container) gets the crafting
        // panel's kit pieces: the chest name with the gold rule + diamond, Take all / Place stacks / Core's Tags as kit pills, and any
        // "KitWell_<x>" under it (VAInventory's KitWell_ChestGrid) as an inset well. Look only: VAInventory owns the layout.
        private const string ChestRuleName = "KitChestRule_NoKitSkin";
        private static void ChestPieces(InventoryGui gui)
        {
            var root = gui != null ? gui.m_container : null;
            if (root == null || !root.gameObject.activeInHierarchy) return;
            if (gui.m_takeAllButton != null && gui.m_takeAllButton.gameObject.activeInHierarchy) ButtonPill(gui.m_takeAllButton, false);
            if (gui.m_stackAllButton != null && gui.m_stackAllButton.gameObject.activeInHierarchy) ButtonPill(gui.m_stackAllButton, false);
            var tags = FiresCore.Storage.Patches.ChestTagsPanel.OpenButton;
            if (tags != null && tags.gameObject.activeInHierarchy) ButtonPill(tags, false);
            SectionWells(root, true);
            var title = gui.m_containerName;
            if (title != null && title.rectTransform.Find(ChestRuleName) == null)
            {
                var trt = title.rectTransform;
                var d = Widgets.Divider(trt, 0f, Mathf.Max(200f, trt.rect.width));
                d.name = ChestRuleName;
                d.anchorMin = d.anchorMax = new Vector2(0.5f, 0f); d.pivot = new Vector2(0.5f, 1f);
                d.anchoredPosition = new Vector2(0f, -2f);
                foreach (var g in d.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                if (d.GetComponent<KitLeaf>() == null) d.gameObject.AddComponent<KitLeaf>();
                s_extras.Add(d.gameObject);
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static class KeepTabPills
        {
            private static void Postfix(InventoryGui __instance)
            {
                if (!On || __instance == null || !InventoryGui.IsVisible()) return;
                try { TabPills(__instance); ChestPieces(__instance); } catch { }
            }
        }

        // the Craft button: kit raised face (the classifier) + a gold 1 px outline + gold label, like the hub's primary buttons
        private static void CraftButton(InventoryGui gui, bool on)
        {
            var b = gui != null ? gui.m_craftButton : null; if (b == null || b.targetGraphic == null) return;
            var face = b.targetGraphic;
            var o = face.transform.Find(CraftOutlineName);
            if (on) Outline(face, FiresTokens.Gold, 6f, CraftOutlineName);
            else if (o != null) o.gameObject.SetActive(false);
            if (!on) return;
            foreach (var t in b.GetComponentsInChildren<TMP_Text>(true)) { Record(t); t.color = b.interactable ? FiresTokens.GoldBright : FiresTokens.TextMuted; }
        }

        private static int s_lastCount = -1;
        private static bool s_lastOn;
        private static void Apply(InventoryGui gui, string from)
        {
            if (gui == null || gui.m_crafting == null) return;
            try
            {
                Hook();
                bool on = On;
                if (!on && s_orig.Count == 0) return;   // never styled: nothing to restore, vanilla untouched
                s_roots.Add(gui.m_crafting);
                int n = Walk(gui.m_crafting, gui);
                try { RecipeWell(gui, on); SectionWells(gui.m_crafting, on); TitleRule(gui.m_crafting, on); CraftButton(gui, on); if (on) TabPills(gui); }   // pills
                catch (Exception ex2) { Debug.LogWarning("[FiresUnifiedCore] [CraftSkin] kit pieces: " + ex2.Message); }
                if (n != s_lastCount || on != s_lastOn)   // logged on change, not every panel refresh
                {
                    s_lastCount = n; s_lastOn = on;
                    Debug.Log($"[FiresUnifiedCore] [CraftSkin] {from}: {(on ? $"kit look applied to {n} graphic(s) (plate {ColorUtility.ToHtmlStringRGBA(FiresTokens.Panel)}, World backdrop: no sky)" : "classic look restored")}");
                }
            }
            catch (Exception ex) { Debug.LogWarning("[FiresUnifiedCore] [CraftSkin] apply failed (panel stays as it is): " + ex.Message); }
        }
    }
}
