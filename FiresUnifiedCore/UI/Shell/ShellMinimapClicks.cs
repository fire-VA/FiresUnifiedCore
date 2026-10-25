// Fires UI Shell: the minimap column's clicks, while the cursor is free (Esc menu / hub).
// - The data pill: each LINE is its own target. Coordinates line: left = "x,z,y", right = goto "x z". Zone line: admin + FAT voxel world
//   = a "Save chunk blueprint" popup (FatApi.RequestChunkBlueprint), else copies "x,z". Biome line: SHIFT+click = FAT's
//   biome paint brush for the spot under the player (FatApi.QueueBiomePaintAt, when FAT has it).
// - Registered column boxes (territory, quest tracker, ...) stay visible above the Esc menu, and a mod can take clicks on its box
//   through FiresShell.SetMinimapColumnClick(id, handler) (e.g. a territory quick edit).
using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI.Shell.Kit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static partial class ShellMinimap
    {
        // ---- data pill lines ----------------------------------------------------------------------------------
        private static string[] s_dataLines = new string[0];
        private const float DataLineH = 14f, DataPadY = 6f;

        internal static void DataPillClick(PointerEventData e)
        {
            if (s_dataPill == null || !Cursor.visible) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(s_dataPill, e.position, e.pressEventCamera, out Vector2 local)) return;
            float fromTop = s_dataPill.rect.yMax - local.y - DataPadY - (s_dataPill.rect.height - 2f * DataPadY - s_dataLines.Length * DataLineH) * 0.5f;
            int idx = Mathf.FloorToInt(fromTop / DataLineH);
            if (idx < 0 || idx >= s_dataLines.Length) return;
            string line = s_dataLines[idx];
            bool shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool right = e.button == PointerEventData.InputButton.Right;
            if (line == s_noticeLine) return;
            if (line.IndexOf("x:", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("z:", StringComparison.OrdinalIgnoreCase) >= 0) { CopyCoords(right); return; }
            if (line.StartsWith("zone:", StringComparison.OrdinalIgnoreCase)) { ZoneClick(line); return; }
            if (idx == FirstRealLine())
            {
                if (shift) BiomePaint();
                else ShellLog.Info("minimap: biome line clicked (no action; SHIFT+click = FAT biome paint brush)");
            }
        }

        private static int FirstRealLine()
        {
            for (int i = 0; i < s_dataLines.Length; i++) if (s_dataLines[i] != s_noticeLine) return i;
            return -1;
        }

        private static void Copied(string text, string what)
        {
            s_copied = text; s_toastNoPrefix = false;
            GUIUtility.systemCopyBuffer = text;
            s_copiedUntil = Time.unscaledTime + 1.5f;
            ShellLog.Info("minimap: copied " + what + " '" + text + "'");
            LayoutDataPill();
        }

        // "zone: 5/-60" (FAT's line) -> 5, -60
        private static bool ParseZone(string line, out int zx, out int zz)
        {
            zx = zz = 0;
            string s = line.Substring(5).Trim();
            int sp = s.IndexOf(' '); if (sp > 0) s = s.Substring(0, sp);
            var parts = s.Split('/');
            return parts.Length == 2 && int.TryParse(parts[0], out zx) && int.TryParse(parts[1], out zz);
        }

        private static void ZoneClick(string line)
        {
            if (!ParseZone(line, out int zx, out int zz)) { ShellLog.Info("minimap: zone line '" + line + "' not understood"); return; }
            if (FatCall("CanCaptureChunkBlueprint") is bool can && can) { OpenBlueprintPopup(zx, zz); return; }
            Copied(zx + "," + zz, "zone");
        }

        // ---- coords copy icon ----------------------------------------------------
        // Two small overlapping rounded squares right after the text on the coordinates line; left = "x,z,y", right = goto "x z".
        internal const float CopyIconW = 18f;
        private static bool s_copyHover;
        internal static void SetCopyIconHover(bool on) { s_copyHover = on; if (s_copyIcon != null && s_copyIcon.gameObject.activeSelf != on && (on == false || s_copyIcon.anchoredPosition != Vector2.zero)) s_copyIcon.gameObject.SetActive(on); }
        private static RectTransform s_copyIcon;
        private static void PlaceCopyIcon(float x, float pillH)
        {
            int idx = -1;
            for (int i = 0; i < s_dataLines.Length; i++)
                if (s_dataLines[i] != s_noticeLine && s_dataLines[i].IndexOf("x:", StringComparison.OrdinalIgnoreCase) >= 0 && s_dataLines[i].IndexOf("z:", StringComparison.OrdinalIgnoreCase) >= 0) { idx = i; break; }
            if (idx < 0) { if (s_copyIcon != null) s_copyIcon.gameObject.SetActive(false); return; }
            if (s_copyIcon == null)
            {
                s_copyIcon = Widgets.Node(s_dataPill, "CopyIcon");
                var hit = s_copyIcon.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
                var back = Widgets.Box(s_copyIcon, "Back", KitSprites.RoundedOutline(2f), FiresTokens.TextMuted, true);
                Widgets.Place(back.rectTransform, 2f, 1f, 8f, 9f); back.raycastTarget = false;
                var front = Widgets.Box(s_copyIcon, "Front", KitSprites.RoundedOutline(2f), FiresTokens.Gold, true);
                Widgets.Place(front.rectTransform, 5f, 4f, 8f, 9f); front.raycastTarget = false;
                s_copyIcon.gameObject.AddComponent<CopyIconClick>();
            }
            s_copyIcon.gameObject.SetActive(s_copyHover);
            s_copyIcon.anchorMin = s_copyIcon.anchorMax = new Vector2(0f, 1f); s_copyIcon.pivot = new Vector2(0f, 1f);
            float top = DataPadY + (pillH - 2f * DataPadY - s_dataLines.Length * DataLineH) * 0.5f + idx * DataLineH;
            s_copyIcon.anchoredPosition = new Vector2(x, -top); s_copyIcon.sizeDelta = new Vector2(16f, DataLineH);
        }

        // ---- FAT (by name; Core never references FAT) --------------------------------------------------------
        private static Type s_fatApi;
        private static bool s_fatLooked;
        private static Type FatApi()
        {
            if (!s_fatLooked) { s_fatLooked = true; s_fatApi = Type.GetType("VerdantsAscent.FatApi, FiresAdminTerrain"); }
            return s_fatApi;
        }

        private static object FatCall(string name)
        {
            var m = FatApi()?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null) return null;
            try { return m.Invoke(null, null); } catch (Exception ex) { ShellLog.Warn("minimap: FatApi." + name + " threw " + (ex.InnerException ?? ex).Message); return null; }
        }

        private static void BiomePaint()
        {
            var p = Player.m_localPlayer;
            var m = FatApi()?.GetMethod("QueueBiomePaintAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float), typeof(string).MakeByRefType() }, null);
            if (p == null || m == null) { ShellLog.Info("minimap: SHIFT+click biome: no FAT biome paint API (FatApi.QueueBiomePaintAt)"); return; }
            // Halo ring ([classes-ui] for [generator], lead 10-24; [fgn]: zone-mode paint keys vanilla x,z zones and never runs on HaloFlat,
            // whose biomes are FatHaloBiome's procedural patches with no paint store): refused on the ring until FAT adds a ring paint layer.
            if (FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(p.transform.position))
            {
                ShellLog.Info("[Halo] minimap: SHIFT+click biome paint refused on the ring (no ring biome paint layer in FAT yet)");
                Toast("Biome paint isn't available on the ring yet");
                return;
            }
            if (FatCall("CanQueueBiomePaint") is bool can && !can) { ShellLog.Info("minimap: SHIFT+click biome: FAT says not allowed (admin + Chaos Hammer needed)"); Toast("Biome paint: admin + Chaos Hammer needed"); return; }
            var args = new object[] { p.transform.position.x, p.transform.position.z, null };
            bool ok = false;
            try { ok = (bool)m.Invoke(null, args); } catch (Exception ex) { args[2] = (ex.InnerException ?? ex).Message; }
            string report = args[2] as string ?? "";
            ShellLog.Info($"minimap: biome paint at {p.transform.position.x:0},{p.transform.position.z:0} -> {(ok ? "ready" : "not ready")}: {report}");
            Toast(report.Length > 0 ? report : ok ? "Paint brush ready" : "No paint brush");
        }

        // ---- chunk blueprint popup -------------------------------------------------------------------------
        private static RectTransform s_popup;
        private static TMP_InputField s_popupName;
        private static KitLabel s_popupNote;
        private static int s_popupZx, s_popupZz;
        private static bool s_popupReplace, s_popupWaiting;
        private static float s_popupDeadline;

        private static void OpenBlueprintPopup(int zx, int zz)
        {
            CloseBlueprintPopup();
            s_popupZx = zx; s_popupZz = zz; s_popupReplace = false; s_popupWaiting = false;
            const float W = 360f, H = 168f;
            s_popup = Widgets.Node(s_rect, "FiresChunkBlueprintPopup");
            s_popup.SetAsLastSibling();
            s_popup.anchorMin = s_popup.anchorMax = s_popup.pivot = new Vector2(0.5f, 0.5f);
            s_popup.sizeDelta = new Vector2(W, H);
            s_popup.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
            var fill = Widgets.Box(s_popup, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), new Color(0.086f, 0.067f, 0.047f, 0.97f), true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = true;
            var edge = Widgets.Box(s_popup, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            var title = KitText.Create(s_popup, "Title", "Save chunk blueprint  (zone " + zx + "/" + zz + ")", FontRole.SerifBold, 16f, FiresTokens.Gold, KitAlign.Left, 0f, false, false);
            Widgets.Place(title.Rect, 16f, 12f, W - 32f, 24f);
            s_popupName = ShellConfigModule.MakeInput(s_popup, "name", null);
            var ir = (RectTransform)s_popupName.transform; Widgets.Place(ir, 16f, 46f, W - 32f, 32f);
            s_popupName.text = "chunk_" + zx + "_" + zz;
            s_popupName.onValueChanged.AddListener(_ => { if (s_popupReplace) { s_popupReplace = false; Note(""); } });
            s_popupNote = KitText.Create(s_popup, "Note", "", FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextMuted, KitAlign.Left, 0f, false, true);
            Widgets.Place(s_popupNote.Rect, 16f, 84f, W - 32f, 34f);
            var save = Widgets.Button(s_popup, "save", "Save", ButtonKind.Primary, W - 16f - 96f - 8f - 96f, H - 42f, 96f);
            ShellWidgets.Clickable(save, BlueprintSave);
            var cancel = Widgets.Button(s_popup, "cancel", "Cancel", ButtonKind.Primary,   /* Disabled turns raycasts off */ W - 16f - 96f, H - 42f, 96f);
            ShellWidgets.Clickable(cancel, () => { ShellLog.Info("minimap: chunk blueprint popup cancelled"); CloseBlueprintPopup(); });
            ShellLog.Info($"minimap: chunk blueprint popup for zone {zx},{zz}");
        }

        private static void Note(string s) { if (s_popupNote != null) s_popupNote.Value = s; }

        private static void BlueprintSave()
        {
            if (s_popup == null || s_popupWaiting) return;
            string name = (s_popupName != null ? s_popupName.text : "").Trim();
            var m = FatApi()?.GetMethod("RequestChunkBlueprint", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int), typeof(int), typeof(string), typeof(bool), typeof(Action<bool, string>) }, null);
            if (m == null) { Note("This FAT has no chunk blueprint API (needs FAT 0.2.949+)."); return; }
            ShellLog.Info($"minimap: chunk blueprint '{name}' requested for zone {s_popupZx},{s_popupZz}{(s_popupReplace ? " (replace)" : "")}");
            s_popupWaiting = true; s_popupDeadline = Time.unscaledTime + 30f;
            Note("Saving...");
            Action<bool, string> done = BlueprintDone;
            bool sent;
            try { sent = (bool)m.Invoke(null, new object[] { s_popupZx, s_popupZz, name, s_popupReplace, done }); }
            catch (Exception ex) { sent = false; Note((ex.InnerException ?? ex).Message); }
            if (!sent && s_popupWaiting) { s_popupWaiting = false; Note("Not sent (no world, empty name, or not allowed)."); ShellLog.Info("minimap: chunk blueprint not sent"); }
        }

        private static void BlueprintDone(bool ok, string report)
        {
            s_popupWaiting = false;
            ShellLog.Info($"minimap: chunk blueprint answer: {(ok ? "saved" : "not saved")} - {report}");
            if (ok) { Toast(report); CloseBlueprintPopup(); return; }
            if (report != null && report.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                s_popupReplace = true;
                Note("That name exists. Save again to REPLACE it, or change the name.");
                return;
            }
            Note(report ?? "not saved");
        }

        private static void UpdateBlueprintPopup()
        {
            if (s_popup == null) return;
            if (s_popupWaiting && Time.unscaledTime > s_popupDeadline)
            {
                s_popupWaiting = false;
                Note("No answer from the server (is it on FAT 0.2.949+?).");
                ShellLog.Info("minimap: chunk blueprint: no answer from the server in 30 s");
            }
            if (!Cursor.visible) CloseBlueprintPopup();
        }

        private static void CloseBlueprintPopup()
        {
            if (s_popup != null) UnityEngine.Object.Destroy(s_popup.gameObject);
            s_popup = null; s_popupName = null; s_popupNote = null; s_popupWaiting = false;
        }

        // A short toast over the data pill (re-uses the "Copied" line).
        private static void Toast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            s_copied = text.Length > 60 ? text.Substring(0, 57) + "..." : text;
            s_copiedUntil = Time.unscaledTime + 3f;
            s_toastNoPrefix = true;
            LayoutDataPill();
        }
        private static bool s_toastNoPrefix;
        private static string s_noticeLine;

        // ---- column boxes: above Esc + click hooks -----------------------------------------------------------
        private static readonly Dictionary<string, Action<PointerEventData>> s_columnClicks = new Dictionary<string, Action<PointerEventData>>();
        private sealed class Raised { public Canvas Canvas; public bool Added, WasOverride; public int WasOrder; public GraphicRaycaster Ray; }
        private static readonly Dictionary<RectTransform, Raised> s_raised = new Dictionary<RectTransform, Raised>();

        internal static void SetColumnClick(string id, Action<PointerEventData> onClick)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (onClick == null) { s_columnClicks.Remove(id); return; }
            s_columnClicks[id] = onClick;
            ShellLog.Info("minimap column: click handler set for '" + id + "'");
        }

        internal static void ClearColumnClick(string id) { if (id != null && s_columnClicks.Remove(id)) ShellLog.Info("minimap column: click handler cleared for '" + id + "'"); }

        internal static void ColumnBoxClicked(string id, PointerEventData e)
        {
            if (!Cursor.visible || !s_columnClicks.TryGetValue(id, out var h)) return;
            try { h(e); }
            catch (Exception ex) { ShellLog.Warn("minimap column: click handler for '" + id + "' threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // every half second: each registered box raised above the Esc menu while it shows, with a click forwarder
        private static void SyncColumnBoxes(bool menu)
        {
            foreach (var b in s_column)
            {
                if (b.Rt == null) continue;
                var fw = b.Rt.GetComponent<ColumnClickForwarder>();
                if (fw == null) { fw = b.Rt.gameObject.AddComponent<ColumnClickForwarder>(); }
                fw.Id = b.Id;
                bool raised = s_raised.ContainsKey(b.Rt);
                if (menu && !raised) Raise(b.Rt, b.Id);
                else if (!menu && raised) Lower(b.Rt);
            }
        }

        private static void Raise(RectTransform rt, string id)
        {
            var r = new Raised();
            r.Canvas = rt.GetComponent<Canvas>();
            if (r.Canvas == null) { r.Canvas = rt.gameObject.AddComponent<Canvas>(); r.Added = true; }
            else { r.WasOverride = r.Canvas.overrideSorting; r.WasOrder = r.Canvas.sortingOrder; }
            r.Canvas.overrideSorting = true; r.Canvas.sortingOrder = 30000;
            if (rt.GetComponent<GraphicRaycaster>() == null) r.Ray = rt.gameObject.AddComponent<GraphicRaycaster>();
            s_raised[rt] = r;
            ShellLog.Info("minimap column: '" + id + "' above the Esc menu");
        }

        private static void Lower(RectTransform rt)
        {
            if (!s_raised.TryGetValue(rt, out var r)) return;
            s_raised.Remove(rt);
            if (rt == null) return;
            // The raycaster goes at once (a deferred Destroy left it alive, and Unity refused the Canvas right after)
            if (r.Ray != null) UnityEngine.Object.DestroyImmediate(r.Ray);
            if (r.Added) { if (r.Canvas != null && r.Canvas.GetComponent<GraphicRaycaster>() == null) UnityEngine.Object.Destroy(r.Canvas); }
            else if (r.Canvas != null) { r.Canvas.overrideSorting = r.WasOverride; r.Canvas.sortingOrder = r.WasOrder; }
        }

        private static void RestoreClicks()
        {
            foreach (var rt in new List<RectTransform>(s_raised.Keys)) Lower(rt);
            s_raised.Clear();
            foreach (var b in s_column) { var fw = b.Rt != null ? b.Rt.GetComponent<ColumnClickForwarder>() : null; if (fw != null) UnityEngine.Object.Destroy(fw); }
            CloseBlueprintPopup();
        }
    }

    internal sealed class CopyIconClick : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData e) { if (Cursor.visible) ShellMinimap.CopyCoords(e.button == PointerEventData.InputButton.Right); }
    }

    internal sealed class ColumnClickForwarder : MonoBehaviour, IPointerClickHandler
    {
        internal string Id;
        public void OnPointerClick(PointerEventData e) { if (Id != null) ShellMinimap.ColumnBoxClicked(Id, e); }
    }
}
