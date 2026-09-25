using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Cloning a vanilla UI object inherits its BEHAVIOUR, not just its look, and the obvious way of clearing that
    /// behaviour does not work. Use this instead of Instantiate.
    ///
    /// <para><b>Why this exists.</b> <c>UnityEvent.RemoveAllListeners()</c> removes RUNTIME listeners only. A
    /// listener wired in the scene is a PERSISTENT call and survives it, so a cloned control keeps doing vanilla's
    /// job in addition to whatever is added. A convert dialog cloned from Valheim's RemoveWorldDialog kept
    /// <c>FejdStartup.OnButtonRemoveWorldYes</c> on its OK button, so every click both converted a world AND
    /// deleted the selected one - it ate a 486 MB world three times before the cause was found. The same clone's
    /// checkboxes, taken from the Start Server toggle, were still firing that toggle's handler.</para>
    ///
    /// <para>Persistent calls can only be switched off one at a time by index, or dropped wholesale by replacing
    /// the event object. Both are done here so no caller has to remember either.</para>
    /// </summary>
    public static class VanillaClone
    {
        private const string GamepadHintName = "gamepad_hint";

        /// <summary>Clones a vanilla object and strips every behaviour it carried. The result draws like vanilla
        /// and does nothing until something is wired to it.</summary>
        public static GameObject Of(GameObject source, Transform parent, string name = null)
        {
            if (source == null) return null;
            GameObject clone = Object.Instantiate(source, parent);
            if (!string.IsNullOrEmpty(name)) clone.name = name;
            Sanitize(clone);
            return clone;
        }

        /// <summary>Strips inherited handling from an already-cloned object: every button, toggle, input field,
        /// slider and dropdown under it loses its listeners, and gamepad hints - which would promise a button that
        /// now does something else - are removed.</summary>
        public static void Sanitize(GameObject clone)
        {
            if (clone == null) return;

            foreach (Button button in clone.GetComponentsInChildren<Button>(true))
                if (button != null) button.onClick = new Button.ButtonClickedEvent();

            foreach (Toggle toggle in clone.GetComponentsInChildren<Toggle>(true))
                if (toggle != null) toggle.onValueChanged = new Toggle.ToggleEvent();

            foreach (Slider slider in clone.GetComponentsInChildren<Slider>(true))
                if (slider != null) slider.onValueChanged = new Slider.SliderEvent();

            foreach (TMP_Dropdown dropdown in clone.GetComponentsInChildren<TMP_Dropdown>(true))
                if (dropdown != null) dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();

            foreach (TMP_InputField field in clone.GetComponentsInChildren<TMP_InputField>(true))
            {
                if (field == null) continue;
                field.onValueChanged = new TMP_InputField.OnChangeEvent();
                field.onEndEdit = new TMP_InputField.SubmitEvent();
                field.onSubmit = new TMP_InputField.SubmitEvent();
                field.onSelect = new TMP_InputField.SelectionEvent();
                field.onDeselect = new TMP_InputField.SelectionEvent();
                field.onValidateInput = null;
            }

            foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
                if (child != null && child.name == GamepadHintName) Object.Destroy(child.gameObject);
        }

        /// <summary>Switches off an event's persistent calls in place, for a control that must KEEP its runtime
        /// listeners. <see cref="Sanitize"/> is the usual choice; this is for the case where replacing the event
        /// object would drop something already attached.</summary>
        public static void Silence(UnityEventBase raised)
        {
            if (raised == null) return;
            for (int index = 0; index < raised.GetPersistentEventCount(); index++)
                raised.SetPersistentListenerState(index, UnityEventCallState.Off);
        }

        /// <summary>Gives a cloned button one job and one caption.</summary>
        public static void Rewire(Button button, string caption, UnityAction onClick)
        {
            if (button == null) return;
            button.onClick = new Button.ButtonClickedEvent();
            if (onClick != null) button.onClick.AddListener(onClick);
            RemoveHint(button.transform);
            if (caption == null) return;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = caption;
        }

        /// <summary>Gives a cloned toggle one job, one caption and a known state.</summary>
        public static void Rewire(Toggle toggle, string caption, bool isOn, UnityAction<bool> onChanged)
        {
            if (toggle == null) return;
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.SetIsOnWithoutNotify(isOn);
            toggle.interactable = true;
            if (onChanged != null) toggle.onValueChanged.AddListener(onChanged);
            RemoveHint(toggle.transform);
            if (caption == null) return;
            TMP_Text label = toggle.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = caption;
        }

        /// <summary>Frees a cloned input field of the source's validation. Vanilla's New World name field is
        /// serialised Alphanumeric with a 20 character limit, so a cloned one silently swallows the spaces and
        /// apostrophes a world name is allowed to have.</summary>
        public static void Free(TMP_InputField field, int characterLimit = 0)
        {
            if (field == null) return;
            field.onValueChanged = new TMP_InputField.OnChangeEvent();
            field.onEndEdit = new TMP_InputField.SubmitEvent();
            field.onSubmit = new TMP_InputField.SubmitEvent();
            field.onValidateInput = null;
            field.contentType = TMP_InputField.ContentType.Standard;
            field.characterValidation = TMP_InputField.CharacterValidation.None;
            field.inputValidator = null;
            field.characterLimit = characterLimit;
        }

        /// <summary>Repaints text under <paramref name="root"/> with the colour and font a vanilla text is already
        /// using on the same screen. Codegen palettes are built for a light parchment field and are unreadable on
        /// a cloned wooden panel; taking the style from the game means it follows whatever the game does.</summary>
        public static void Readable(GameObject root, TMP_Text template)
        {
            if (root == null || template == null) return;
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text == null) continue;
                text.color = template.color;
                if (template.font != null) text.font = template.font;
            }
        }

        /// <summary>The children a clone should not keep: gamepad hints that name a button doing something else.</summary>
        private static void RemoveHint(Transform node)
        {
            if (node == null) return;
            Transform hint = node.Find(GamepadHintName);
            if (hint != null) Object.Destroy(hint.gameObject);
        }

        /// <summary>Every persistent call still live under an object, for checking a clone was actually cleaned.
        /// Empty is the expected answer after <see cref="Sanitize"/>.</summary>
        public static List<string> RemainingPersistentCalls(GameObject clone)
        {
            var found = new List<string>();
            if (clone == null) return found;

            foreach (Button button in clone.GetComponentsInChildren<Button>(true))
                Note(found, button, button == null ? null : button.onClick, "Button.onClick");
            foreach (Toggle toggle in clone.GetComponentsInChildren<Toggle>(true))
                Note(found, toggle, toggle == null ? null : toggle.onValueChanged, "Toggle.onValueChanged");
            foreach (TMP_InputField field in clone.GetComponentsInChildren<TMP_InputField>(true))
                Note(found, field, field == null ? null : field.onValueChanged, "InputField.onValueChanged");
            return found;
        }

        private static void Note(List<string> found, Component owner, UnityEventBase raised, string what)
        {
            if (owner == null || raised == null) return;
            int count = raised.GetPersistentEventCount();
            if (count > 0) found.Add($"{owner.gameObject.name}.{what} x{count}");
        }
    }
}
