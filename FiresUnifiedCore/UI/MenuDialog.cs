using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.Bridge;

namespace FiresCore.UI
{
    using Input = UnityEngine.Input;   // FiresCore.Input (a namespace) otherwise shadows UnityEngine.Input here

    // A modal dialog of labelled fields (text, checkbox, choice) with OK and Cancel, built from UIBuilderHelper's
    // parchment controls. It works on the main menu, where there is no player or HUD, and in game, where it frees the
    // cursor and holds the camera like the other Fires panels. The caller keeps its field objects: once OK passes
    // validation their Value / Index hold what the player chose, and Cancel puts back the values they came in with.
    public static class MenuDialog
    {
        public abstract class Field
        {
            public readonly string Label;

            protected Field(string label) { Label = label; }

            internal abstract void Build(Transform slot);
            internal abstract void Restore();
        }

        public sealed class TextField : Field
        {
            public string Value;
            public readonly string Placeholder;
            private string _initial;
            internal TMP_InputField InputField;

            public TextField(string label, string value = "", string placeholder = "") : base(label)
            {
                Value = value ?? "";
                Placeholder = placeholder ?? "";
            }

            internal override void Build(Transform slot)
            {
                _initial = Value;
                InputField = UIBuilderHelper.CreateInputField(slot, "Input", Placeholder, Vector2.zero, Vector2.one, TextFontSize);
                InputField.text = Value;
                InputField.onValueChanged.AddListener(text => Value = text);
            }

            internal override void Restore() => Value = _initial;
        }

        public sealed class CheckField : Field
        {
            public bool Value;
            private bool _initial;

            public CheckField(string label, bool value = false) : base(label) { Value = value; }

            internal override void Build(Transform slot)
            {
                _initial = Value;
                UIBuilderHelper.CreateToggle(slot, "Check", Value, Vector2.zero, Vector2.one, isOn => Value = isOn);
            }

            internal override void Restore() => Value = _initial;
        }

        public sealed class ChoiceField : Field
        {
            public readonly List<string> Options;
            public int Index;
            private int _initial;
            internal TMP_Dropdown Dropdown;

            public ChoiceField(string label, IEnumerable<string> options, int index = 0) : base(label)
            {
                Options = options != null ? new List<string>(options) : new List<string>();
                Index = index;
            }

            public string Selected => Index >= 0 && Index < Options.Count ? Options[Index] : null;

            internal override void Build(Transform slot)
            {
                _initial = Index;
                Dropdown = UIBuilderHelper.CreateDropdown(slot, "Choice", Options, Index, Vector2.zero, Vector2.one, index => Index = index, TextFontSize);
            }

            internal override void Restore() => Index = _initial;
        }

        private const string CanvasName = "FiresMenuDialog";
        private const string RegistryKey = "FiresMenuDialog";
        private const string DefaultOkLabel = "OK";
        private const string DefaultCancelLabel = "Cancel";
        private const int SortingOrder = 6900;
        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;
        private const float MatchWidthOrHeight = 0.5f;
        private const float PanelWidth = 620f;
        private const float HeaderHeight = 48f;
        private const float RowHeight = 38f;
        private const float RowGap = 8f;
        private const float Padding = 18f;
        private const float ErrorHeight = 22f;
        private const float ButtonHeight = 38f;
        private const float ButtonWidth = 150f;
        private const float ButtonGap = 12f;
        private const float LabelColumnEnd = 0.38f;
        private const float SlotColumnStart = 0.40f;
        private const float TitleFontSize = 22f;
        private const float LabelFontSize = 16f;
        private const float ErrorFontSize = 14f;
        private const int TextFontSize = 15;
        private static readonly Color BlockerColor = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color ErrorInk = new Color(0.55f, 0.1f, 0.06f);
        private static readonly Vector2 OutlineDistance = new Vector2(2f, -2f);
        private static readonly Vector2 Center = new Vector2(0.5f, 0.5f);

        private sealed class State
        {
            public IList<Field> Fields;
            public Action OnOk;
            public Action OnCancel;
            public Func<string> Validate;
        }

        private static readonly List<Graphic> Crosshair = new List<Graphic>();
        private static GameObject _canvas;
        private static State _state;
        private static TextMeshProUGUI _error;
        private static bool _ownsInputBlock;

        public static bool IsOpen => _canvas != null;

        // Shows the dialog, replacing any open one (which closes as if cancelled, without its callback). validate runs
        // on OK and returns an error to show while the dialog stays open, or null to accept.
        public static void Show(string title, IList<Field> fields, Action onOk, Action onCancel = null,
            Func<string> validate = null, string okLabel = DefaultOkLabel, string cancelLabel = DefaultCancelLabel)
        {
            Close(restoreFields: true);
            _state = new State { Fields = fields ?? Array.Empty<Field>(), OnOk = onOk, OnCancel = onCancel, Validate = validate };
            Build(title, okLabel, cancelLabel);
            Engage();
        }

        // ── Hosting the same field rows inside SOMEONE ELSE'S frame ─────────
        // A caller that already has a panel — one of vanilla's own, cloned — wants these rows and nothing else: no
        // canvas, no parchment, no buttons, no input capture. It lays the rows into `host` top-down and returns the
        // height they used, so the caller can size its frame. The caller owns the frame, the buttons, validation and
        // the lifetime; on cancel it calls RestoreFields to put the values back the way Show does.
        public static float RowsHeightFor(int fieldCount)
            => fieldCount <= 0 ? 0f : fieldCount * RowHeight + (fieldCount - 1) * RowGap;

        public static float BuildRowsIn(RectTransform host, IList<Field> fields)
        {
            if (host == null || fields == null || fields.Count == 0) return 0f;
            TMP_InputField firstInput = null;
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field == null) continue;
                var row = NewRect($"Row{i}", host);
                PlaceTop(row, -(i * (RowHeight + RowGap)), RowHeight, 0f);
                UIBuilderHelper.CreateLabel(row, "Label", field.Label ?? "", LabelFontSize, UIFontConfig.Colors.ParchmentInk,
                    Vector2.zero, new Vector2(LabelColumnEnd, 1f), TextAlignmentOptions.MidlineLeft);
                var slot = NewRect("Slot", row);
                slot.anchorMin = new Vector2(SlotColumnStart, 0f);
                slot.anchorMax = Vector2.one;
                slot.offsetMin = Vector2.zero;
                slot.offsetMax = Vector2.zero;
                field.Build(slot);
                if (firstInput == null && field is TextField text) firstInput = text.InputField;
            }
            if (firstInput != null) firstInput.ActivateInputField();
            return RowsHeightFor(fields.Count);
        }

        public static void RestoreFields(IList<Field> fields)
        {
            if (fields == null) return;
            foreach (var field in fields) field?.Restore();
        }

        private static void Build(string title, string okLabel, string cancelLabel)
        {
            _canvas = new GameObject(CanvasName);
            _canvas.SetActive(false);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.matchWidthOrHeight = MatchWidthOrHeight;
            _canvas.AddComponent<GraphicRaycaster>();
            _canvas.AddComponent<Pump>();
            _canvas.SetActive(true);

            UIBuilderHelper.CreateImage(_canvas.transform, "Blocker", Vector2.zero, Vector2.one, BlockerColor);

            var fields = _state.Fields;
            float rowsHeight = fields.Count * RowHeight + Math.Max(0, fields.Count - 1) * RowGap;
            float panelHeight = HeaderHeight + Padding + rowsHeight + Padding + ErrorHeight + ButtonHeight + Padding;
            var panel = UIBuilderHelper.CreatePanel(_canvas.transform, "Panel", Center, Center, UIFontConfig.Colors.ParchmentPanel);
            var panelRect = (RectTransform)panel.transform;
            panelRect.sizeDelta = new Vector2(PanelWidth, panelHeight);
            var outline = panel.AddComponent<Outline>();
            outline.effectColor = UIFontConfig.Colors.ParchmentOutline;
            outline.effectDistance = OutlineDistance;

            var header = UIBuilderHelper.CreatePanel(panel.transform, "Header", new Vector2(0f, 1f), new Vector2(1f, 1f), UIFontConfig.Colors.ParchmentHeader);
            PlaceTop((RectTransform)header.transform, 0f, HeaderHeight, 0f);
            var titleText = UIBuilderHelper.CreateLabel(header.transform, "Title", title ?? "", TitleFontSize,
                UIFontConfig.Colors.ParchmentButtonInk, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
            UIBuilderHelper.ApplyDecorativeFont(titleText);

            TMP_InputField firstInput = null;
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var row = NewRect($"Row{i}", panelRect);
                PlaceTop(row, -(HeaderHeight + Padding + i * (RowHeight + RowGap)), RowHeight, Padding);
                UIBuilderHelper.CreateLabel(row, "Label", field.Label ?? "", LabelFontSize, UIFontConfig.Colors.ParchmentInk,
                    Vector2.zero, new Vector2(LabelColumnEnd, 1f), TextAlignmentOptions.MidlineLeft);
                var slot = NewRect("Slot", row);
                slot.anchorMin = new Vector2(SlotColumnStart, 0f);
                slot.anchorMax = Vector2.one;
                slot.offsetMin = Vector2.zero;
                slot.offsetMax = Vector2.zero;
                field.Build(slot);
                if (firstInput == null && field is TextField text) firstInput = text.InputField;
            }

            _error = UIBuilderHelper.CreateLabel(panel.transform, "Error", "", ErrorFontSize, ErrorInk,
                Vector2.zero, Vector2.one, TextAlignmentOptions.MidlineLeft);
            PlaceTop(_error.rectTransform, -(HeaderHeight + Padding + rowsHeight + Padding), ErrorHeight, Padding);

            PlaceButton(UIBuilderHelper.CreateParchmentButton(panel.transform, okLabel, Vector2.zero, Vector2.zero, Confirm), 0);
            PlaceButton(UIBuilderHelper.CreateParchmentButton(panel.transform, cancelLabel, Vector2.zero, Vector2.zero, Cancel), 1);

            if (firstInput != null) firstInput.ActivateInputField();
        }

        // Buttons sit bottom-right, counted from the right edge: slot 0 is OK, slot 1 is Cancel to its left.
        private static void PlaceButton(Button button, int slotFromRight)
        {
            var rect = (RectTransform)button.transform;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.anchoredPosition = new Vector2(-(Padding + slotFromRight * (ButtonWidth + ButtonGap)), Padding);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // Stretches a child across the panel at a slot measured down from the top edge (0 at the top, negative below).
        private static void PlaceTop(RectTransform rect, float top, float height, float sidePadding)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(sidePadding, top - height);
            rect.offsetMax = new Vector2(-sidePadding, top);
        }

        private static void Confirm()
        {
            var state = _state;
            if (state == null) return;
            string error = null;
            if (state.Validate != null)
            {
                try { error = state.Validate(); }
                catch (Exception ex) { error = ex.Message; }
            }
            if (!string.IsNullOrEmpty(error))
            {
                if (_error != null) _error.text = error;
                return;
            }
            Close(restoreFields: false);
            try { state.OnOk?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning($"[MenuDialog] OK handler threw: {ex.Message}"); }
        }

        private static void Cancel()
        {
            var state = _state;
            if (state == null) return;
            Close(restoreFields: true);
            try { state.OnCancel?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning($"[MenuDialog] Cancel handler threw: {ex.Message}"); }
        }

        private static void Close(bool restoreFields)
        {
            var state = _state;
            _state = null;
            if (state != null && restoreFields)
                foreach (var field in state.Fields) field?.Restore();

            var canvas = _canvas;
            _canvas = null;
            _error = null;
            if (canvas != null) UnityEngine.Object.Destroy(canvas);
            if (state != null) Disengage();
        }

        // A scene change destroys the canvas without Close: treat it as a cancel with no callback.
        private static void OnCanvasDestroyed(GameObject canvas)
        {
            if (_canvas != canvas) return;
            _canvas = null;
            Close(restoreFields: true);
        }

        private static void Engage()
        {
            ModUiRegistry.Register(RegistryKey, () => IsOpen, Cancel);
            if (Player.m_localPlayer == null) return;
            _ownsInputBlock = !InputBlock.IsBlocked;
            if (_ownsInputBlock) InputBlock.Block(true);
            CrosshairRaycast.Suppress(Crosshair);
        }

        private static void Disengage()
        {
            ModUiRegistry.Unregister(RegistryKey);
            if (_ownsInputBlock) InputBlock.Block(false);
            _ownsInputBlock = false;
            CrosshairRaycast.Restore(Crosshair);
        }

        private static bool AnyChoiceExpanded()
        {
            var state = _state;
            if (state == null) return false;
            foreach (var field in state.Fields)
                if (field is ChoiceField choice && choice.Dropdown != null && choice.Dropdown.IsExpanded) return true;
            return false;
        }

        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                if (Player.m_localPlayer != null) CrosshairRaycast.Reassert(Crosshair);
                if (AnyChoiceExpanded()) return;
                if (Input.GetKeyDown(KeyCode.Escape)) Cancel();
                else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Confirm();
            }

            private void OnDestroy() => OnCanvasDestroyed(gameObject);
        }
    }
}
