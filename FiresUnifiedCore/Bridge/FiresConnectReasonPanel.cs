using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.UI;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Client-only: replaces the vanilla connection-failed dialog with our own Core-UI panel when the server
    /// pushed a rejection reason via <see cref="FiresConnectReason"/>. A plain reason renders as title +
    /// scrollable text; an anti-cheat mod-mismatch reason (whose detail lines are tab-delimited
    /// "status|guid|clientVer|serverVer" records) renders as a Jotunn-style split TABLE: Mod / You / Server /
    /// Status, color-coded, scrollable. Skipped on a dedicated server (FejdStartup patch native-crashes a
    /// headless build; see FiresUnifiedCore DedicatedServerSkipPatchTypes).
    /// </summary>
    [HarmonyPatch(typeof(FejdStartup), "ShowConnectError", new[] { typeof(ZNet.ConnectionStatus) })]
    internal static class FiresConnectReasonPanel
    {
        private const char Sep = '\t';
        private static GameObject _root;
        private static RectTransform _cardRt;
        private static TextMeshProUGUI _title;
        private static TextMeshProUGUI _subheader;
        private static GameObject _colHeader;
        private static RectTransform _tableContent;

        [HarmonyPostfix]
        private static void Postfix(FejdStartup __instance)
        {
            if (__instance == null) return;
            if (!FiresConnectReason.TryConsumeReason(out var text)) return;
            if (__instance.m_connectionFailedPanel != null) __instance.m_connectionFailedPanel.SetActive(false);
            Show(text);
        }

        private static void Show(string text)
        {
            try
            {
                EnsureBuilt();
                if (_root == null) return;

                var lines = text.Split('\n');
                _title.text = lines.Length > 0 ? lines[0] : "Connection refused";

                var sub = new System.Text.StringBuilder();
                var rows = new List<string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    if (lines[i].IndexOf(Sep) >= 0) rows.Add(lines[i]);
                    else { if (sub.Length > 0) sub.Append('\n'); sub.Append(lines[i]); }
                }
                _subheader.text = sub.ToString();

                bool table = rows.Count > 0;
                _colHeader.SetActive(table);

                for (int i = _tableContent.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(_tableContent.GetChild(i).gameObject);

                if (table)
                {
                    foreach (var row in rows)
                    {
                        var f = row.Split(Sep);
                        if (f.Length < 4) continue;
                        var (label, color) = StatusStyle(f[0]);
                        AddRow(_tableContent, f[1],
                            string.IsNullOrEmpty(f[2]) ? "-" : f[2],
                            string.IsNullOrEmpty(f[3]) ? "-" : f[3],
                            label, color);
                    }
                }
                else
                {
                    // Plain reason (no comparison rows): show the body text in the scroll area.
                    var body = UIBuilderHelper.CreateLabel(_tableContent, "Body", sub.ToString(), 20f,
                        new Color(0.90f, 0.88f, 0.84f), Vector2.zero, Vector2.one, TextAlignmentOptions.TopLeft);
                    body.enableWordWrapping = true;
                    _subheader.text = string.Empty;
                }

                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
                if (_cardRt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_cardRt);
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresConnectReason] panel show failed: {ex.Message}"); }
        }

        private static (string, Color) StatusStyle(string status)
        {
            switch (status)
            {
                case "OK":         return ("OK",          new Color(0.55f, 0.75f, 0.55f));
                case "ALLOWED":    return ("allowed",     new Color(0.60f, 0.72f, 0.85f));
                case "SERVERONLY": return ("server-only", new Color(0.60f, 0.60f, 0.62f));
                case "ADMINOK":    return ("admin-only",  new Color(0.72f, 0.60f, 0.88f));
                case "UPDATE":     return ("UPDATE",      new Color(0.96f, 0.72f, 0.32f));
                case "MISSING":    return ("MISSING",     new Color(0.92f, 0.45f, 0.40f));
                case "NOTALLOWED": return ("NOT ALLOWED", new Color(0.92f, 0.45f, 0.40f));
                case "ADMINONLY":  return ("ADMIN ONLY",  new Color(0.92f, 0.45f, 0.40f));
                case "BANNED":     return ("BANNED",      new Color(1.00f, 0.32f, 0.32f));
                default:           return (status,        Color.white);
            }
        }

        private static void Hide() { if (_root != null) _root.SetActive(false); }

        private static void EnsureBuilt()
        {
            if (_root != null) return;

            _root = new GameObject("FiresConnectReasonCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            UIBuilderHelper.CreateImage(_root.transform, "Dimmer", Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.78f));

            var card = UIBuilderHelper.CreatePanel(_root.transform, "Card",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.10f, 0.09f, 0.08f, 0.98f));
            RoundedChrome(card, FiresRoundedSkin.RoundedSprite(10, FiresPopupTheme.PanelBg, FiresPopupTheme.PanelEdge, 1));
            _cardRt = card.GetComponent<RectTransform>();
            _cardRt.sizeDelta = new Vector2(980f, 720f);
            UIBuilderHelper.AddVerticalLayout(card, new RectOffset(24, 24, 20, 20), 10f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: true, childForceExpandHeight: false);

            _title = UIBuilderHelper.CreateLabel(card.transform, "Title", "", 28f, Color.white,
                Vector2.zero, Vector2.one, TextAlignmentOptions.Top);
            _title.enableWordWrapping = true;
            UIBuilderHelper.AddLayoutElement(_title.gameObject, preferredHeight: 46f);

            _subheader = UIBuilderHelper.CreateLabel(card.transform, "Subheader", "", 19f, new Color(0.86f, 0.84f, 0.80f),
                Vector2.zero, Vector2.one, TextAlignmentOptions.Top);
            _subheader.enableWordWrapping = true;
            UIBuilderHelper.AddLayoutElement(_subheader.gameObject, preferredHeight: 30f);

            // Fixed column header (stays put above the scrolling rows).
            _colHeader = new GameObject("ColHeader", typeof(RectTransform));
            _colHeader.transform.SetParent(card.transform, false);
            UIBuilderHelper.AddHorizontalLayout(_colHeader, new RectOffset(14, 14, 4, 4), 10f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: false, childForceExpandHeight: false);
            UIBuilderHelper.AddLayoutElement(_colHeader, minHeight: 30f);
            var hcol = new Color(0.72f, 0.70f, 0.66f);
            // "Mod" header indented by the icon slot so it lines up with the row names, not the icons.
            var modHdr = new GameObject("ModHdr", typeof(RectTransform));
            modHdr.transform.SetParent(_colHeader.transform, false);
            var modHdrLayout = UIBuilderHelper.AddHorizontalLayout(modHdr, new RectOffset(0, 0, 0, 0), 8f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: false, childForceExpandHeight: false);
            modHdrLayout.childAlignment = TextAnchor.MiddleLeft;
            UIBuilderHelper.AddLayoutElement(modHdr, minWidth: 260f, flexibleWidth: 1f);
            var modSpacer = UIBuilderHelper.CreateImage(modHdr.transform, "Sp", Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0f));
            UIBuilderHelper.AddLayoutElement(modSpacer.gameObject, minWidth: 22f, preferredWidth: 22f, flexibleWidth: 0f);
            var modHdrLbl = UIBuilderHelper.CreateLabel(modHdr.transform, "Mod", "Mod", 18f, hcol, Vector2.zero, Vector2.one, TextAlignmentOptions.Left);
            UIBuilderHelper.AddLayoutElement(modHdrLbl.gameObject, flexibleWidth: 1f);
            MakeCell(_colHeader.transform, "You", hcol, width: 130f);
            MakeCell(_colHeader.transform, "Server", hcol, width: 130f);
            MakeCell(_colHeader.transform, "Status", hcol, width: 140f);

            var scroll = UIBuilderHelper.CreateScrollArea(card.transform, "Table", Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.30f));
            RoundedChrome(scroll.Root, FiresRoundedSkin.RoundedSprite(8, FiresPopupTheme.ListBg, FiresPopupTheme.PanelEdge, 1));
            UIBuilderHelper.AddLayoutElement(scroll.Root, flexibleHeight: 1f);
            UIBuilderHelper.AddVerticalLayout(scroll.Content.gameObject, new RectOffset(4, 4, 4, 4), 2f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: true, childForceExpandHeight: false);
            UIBuilderHelper.AddContentSizeFitter(scroll.Content.gameObject,
                ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize);
            _tableContent = scroll.Content;

            // Footer spans the card width but centers a small fixed-width OK button inside it.
            var footer = new GameObject("Footer", typeof(RectTransform));
            footer.transform.SetParent(card.transform, false);
            var footerLayout = UIBuilderHelper.AddHorizontalLayout(footer, new RectOffset(0, 0, 4, 0), 0f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: false, childForceExpandHeight: false);
            footerLayout.childAlignment = TextAnchor.MiddleCenter;
            UIBuilderHelper.AddLayoutElement(footer, minHeight: 44f, preferredHeight: 48f);

            var okBtn = UIBuilderHelper.CreateButton(footer.transform, "OK", Vector2.zero, Vector2.one, Hide);
            // White rounded sprite + palette tint states, the same way FiresPopupTheme styles a button
            // (the Button's ColorTint multiplies the image, so the sprite itself must stay white).
            RoundedChrome(okBtn.gameObject, FiresRoundedSkin.RoundedSprite(7, Color.white));
            var okColors = okBtn.colors;
            okColors.normalColor = FiresPopupTheme.BtnNormal;
            okColors.highlightedColor = FiresPopupTheme.BtnHover;
            okColors.pressedColor = FiresPopupTheme.BtnPressed;
            okColors.selectedColor = FiresPopupTheme.BtnNormal;
            okColors.fadeDuration = 0.08f;
            okBtn.colors = okColors;
            foreach (var okLabel in okBtn.GetComponentsInChildren<TMP_Text>(true))
                okLabel.color = FiresPopupTheme.TextLight;
            UIBuilderHelper.AddLayoutElement(okBtn.gameObject, minWidth: 150f, preferredWidth: 160f,
                minHeight: 38f, preferredHeight: 40f, flexibleWidth: 0f, flexibleHeight: 0f);

            _root.SetActive(false);
        }

        // Fires rounded chrome (FiresRoundedSkin + the FiresPopupTheme palette) so the panel reads as part
        // of the same popup family instead of hard-edged boxes. Applied to CHROME ONLY - deliberately not
        // FiresPopupTheme.Reskin(), whose text pass forces every label >=18f to gold bold, which would erase
        // the status colour coding (BANNED red / OK green / admin-only purple) and the mod-name tint, and
        // those colours carry the meaning of the table. Sliced so the corner radius survives any size.
        private static void RoundedChrome(GameObject go, Sprite sprite)
        {
            var img = go != null ? go.GetComponent<Image>() : null;
            if (img == null) return;
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.color = Color.white;
        }

        private static void AddRow(Transform parent, string mod, string you, string server, string statusText, Color statusColor)
        {
            var row = new GameObject("Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            UIBuilderHelper.AddHorizontalLayout(row, new RectOffset(14, 14, 2, 2), 10f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: false, childForceExpandHeight: false);
            UIBuilderHelper.AddLayoutElement(row, minHeight: 26f);

            var neutral = new Color(0.88f, 0.86f, 0.82f);
            MakeModCell(row.transform, mod, Color.Lerp(neutral, statusColor, 0.55f));
            MakeCell(row.transform, you, neutral, width: 130f);
            MakeCell(row.transform, server, neutral, width: 130f);
            MakeCell(row.transform, statusText, statusColor, width: 140f);
        }

        // The Mod column: the mod's Thunderstore icon (or a faint placeholder box) + its GUID.
        private static void MakeModCell(Transform row, string guid, Color nameColor)
        {
            var cell = new GameObject("ModCell", typeof(RectTransform));
            cell.transform.SetParent(row, false);
            var layout = UIBuilderHelper.AddHorizontalLayout(cell, new RectOffset(0, 0, 0, 0), 8f,
                childControlWidth: true, childControlHeight: true, childForceExpandWidth: false, childForceExpandHeight: false);
            layout.childAlignment = TextAnchor.MiddleLeft;
            UIBuilderHelper.AddLayoutElement(cell, minWidth: 260f, flexibleWidth: 1f);

            var sprite = FiresModIcons.Get(guid);
            var icon = UIBuilderHelper.CreateImage(cell.transform, "Icon", Vector2.zero, Vector2.one,
                sprite != null ? Color.white : new Color(1f, 1f, 1f, 0.05f));
            icon.preserveAspect = true;
            if (sprite != null) icon.sprite = sprite;
            UIBuilderHelper.AddLayoutElement(icon.gameObject, minWidth: 22f, minHeight: 22f,
                preferredWidth: 22f, preferredHeight: 22f, flexibleWidth: 0f, flexibleHeight: 0f);

            var name = UIBuilderHelper.CreateLabel(cell.transform, "Name", guid, 18f, nameColor,
                Vector2.zero, Vector2.one, TextAlignmentOptions.Left);
            name.enableWordWrapping = false;
            name.overflowMode = TextOverflowModes.Ellipsis;
            UIBuilderHelper.AddLayoutElement(name.gameObject, minWidth: 150f, flexibleWidth: 1f);
        }

        private static void MakeCell(Transform row, string text, Color color, float width = -1f, bool flexible = false)
        {
            var lbl = UIBuilderHelper.CreateLabel(row, "cell", text, 18f, color, Vector2.zero, Vector2.one, TextAlignmentOptions.Left);
            lbl.enableWordWrapping = false;
            lbl.overflowMode = TextOverflowModes.Ellipsis;
            if (flexible) UIBuilderHelper.AddLayoutElement(lbl.gameObject, minWidth: 260f, flexibleWidth: 1f);
            else UIBuilderHelper.AddLayoutElement(lbl.gameObject, minWidth: width, preferredWidth: width, flexibleWidth: 0f);
        }
    }
}
