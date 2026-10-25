using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell.Kit
{
    /// <summary>
    /// One style everywhere: THE interactive toggle and
    /// slider, exactly the config window's (Widgets.DiamondToggle pill switch, Widgets.Slider slim track + round knob + gold fill + value
    /// at right, ShellSliderDrag click/drag on the track). Every mod's uGUI toggles and sliders use these. Add-only API.
    /// </summary>
    public static class KitControls
    {
        /// <summary>[Toggle/&lt;id&gt;] the config pill switch + ON/OFF label. rightAlign: x is measured from the parent's right edge.
        /// Clicking flips it, redraws it and calls onChanged(newValue). height: the row band it is centred in (26 in the config window).</summary>
        public static KitToggleHandle Toggle(RectTransform parent, string id, bool on, float x, float y, Action<bool> onChanged,
            float height = 26f, bool rightAlign = false, string onLabel = "ON", string offLabel = "OFF")
        {
            var h = new KitToggleHandle(parent, id, x, y, height, rightAlign, onLabel, offLabel, onChanged);
            h.Build(on);
            return h;
        }

        /// <summary>[Slider/&lt;id&gt;] the config slider: Track (Hairline pill), Gold Fill, round Text knob, value right (52 wide).
        /// Click or drag on the track sets the value (clamped to min..max, rounded to step when step &gt; 0) and calls onChanged
        /// every move; onEnd after the release. format: ToString format for the value ("0" for whole numbers, default "0.##").</summary>
        public static KitSliderHandle Slider(RectTransform parent, string id, float min, float max, float value, float x, float y,
            float width, Action<float> onChanged, Action onEnd = null, float step = 0f, string format = "0.##", float height = 26f)
        {
            var h = new KitSliderHandle(min, max, step, format, onChanged, onEnd);
            h.Build(parent, id, value, x, y, width, height);
            return h;
        }
    }

    public sealed class KitToggleHandle
    {
        private readonly RectTransform _parent;
        private readonly string _id, _onLabel, _offLabel;
        private readonly float _x, _y, _h;
        private readonly bool _right;
        private readonly Action<bool> _onChanged;
        public RectTransform Root { get; private set; }
        public bool On { get; private set; }

        internal KitToggleHandle(RectTransform parent, string id, float x, float y, float h, bool right, string onLabel, string offLabel, Action<bool> onChanged)
        { _parent = parent; _id = id; _x = x; _y = y; _h = h; _right = right; _onLabel = onLabel; _offLabel = offLabel; _onChanged = onChanged; }

        /// <summary>Sets the value without calling onChanged (e.g. when the model changed elsewhere).</summary>
        public void Set(bool on) { if (on != On || Root == null) Build(on); }

        /// <summary>Sets the value and calls onChanged, like a click (for tests).</summary>
        public void SetAndNotify(bool on)
        {
            Build(on);
            try { _onChanged?.Invoke(On); } catch (Exception ex) { Debug.LogWarning("[FiresCore] KitControls.Toggle '" + _id + "' onChanged threw: " + ex.Message); }
        }

        internal void Build(bool on)
        {
            if (Root != null) { Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(Root.gameObject); }
            On = on;
            Root = Widgets.DiamondToggle(_parent, _id, on ? _onLabel : _offLabel, on, _x, _y, _h, _right);
            ShellWidgets.Clickable(Root, () =>
            {
                Build(!On);
                try { _onChanged?.Invoke(On); } catch (Exception ex) { Debug.LogWarning("[FiresCore] KitControls.Toggle '" + _id + "' onChanged threw: " + ex.Message); }
            });
        }
    }

    public sealed class KitSliderHandle
    {
        private const float ValueW = 52f;   // Widgets.Slider's value column
        private float _min, _max;           // SetRange
        private readonly float _step;
        private ShellSliderDrag _drag;
        private readonly string _format;
        private readonly Action<float> _onChanged;
        private readonly Action _onEnd;
        private float _trackW, _th, _k;
        private RectTransform _fill, _knob;
        private KitLabel _valueLabel;
        public RectTransform Root { get; private set; }
        public float Value { get; private set; }

        internal KitSliderHandle(float min, float max, float step, string format, Action<float> onChanged, Action onEnd)
        { _min = min; _max = Mathf.Max(min, max); _step = step; _format = string.IsNullOrEmpty(format) ? "0.##" : format; _onChanged = onChanged; _onEnd = onEnd; }

        private float To01(float v) => _max > _min ? Mathf.Clamp01((v - _min) / (_max - _min)) : 0f;
        private string Text(float v) => v.ToString(_format, CultureInfo.InvariantCulture);

        internal void Build(RectTransform parent, string id, float value, float x, float y, float width, float height)
        {
            Value = Mathf.Clamp(value, _min, _max);
            Root = Widgets.Slider(parent, id, To01(Value), Text(Value), x, y, width, height);
            _trackW = width - ValueW - FiresTokens.S12;
            _th = FiresTokens.SliderTrack;
            _k = FiresTokens.SliderKnob;
            _fill = Root.Find("Fill") as RectTransform;
            _knob = Root.Find("Knob") as RectTransform;
            var vt = Root.Find("Value");
            if (vt != null)
            {
                _valueLabel = new KitLabel { Tmp = vt.GetComponent<TMPro.TextMeshProUGUI>(), Legacy = vt.GetComponent<Text>() };
                if (_valueLabel.Graphic == null) _valueLabel = null;
            }
            var hit = Root.gameObject.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
            var drag = _drag = Root.gameObject.AddComponent<ShellSliderDrag>();
            drag.TrackWidth = _trackW;
            drag.OnValue = f01 =>
            {
                float nv = _min + (_max - _min) * f01;
                if (_step > 0f) nv = Mathf.Round(nv / _step) * _step;
                nv = Mathf.Clamp(nv, _min, _max);
                if (Mathf.Approximately(nv, Value)) return;
                Redraw(nv);
                try { _onChanged?.Invoke(nv); } catch (Exception ex) { Debug.LogWarning("[FiresCore] KitControls.Slider '" + id + "' onChanged threw: " + ex.Message); }
            };
            drag.OnEnd = () => { try { _onEnd?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[FiresCore] KitControls.Slider '" + id + "' onEnd threw: " + ex.Message); } };
            // FDT drives editor rows through a uGUI Slider's .value: a non-interactable, graphic-less Slider on
            // Root mirrors the value; setting .value from code redraws and calls onChanged. The pointer stays with ShellSliderDrag.
            UiSlider = Root.gameObject.AddComponent<UnityEngine.UI.Slider>();
            UiSlider.transition = Selectable.Transition.None;
            UiSlider.interactable = false;
            UiSlider.minValue = _min; UiSlider.maxValue = _max;
            UiSlider.SetValueWithoutNotify(Value);
            UiSlider.onValueChanged.AddListener(nv => SetAndNotify(nv));
        }

        /// <summary>The mirror uGUI Slider (tests set .value; onValueChanged → SetAndNotify).</summary>
        public UnityEngine.UI.Slider UiSlider { get; private set; }

        /// <summary>Sets the value (clamped, stepped) and calls onChanged + onEnd, like a click.</summary>
        public void SetAndNotify(float value)
        {
            float nv = value;
            if (_step > 0f) nv = Mathf.Round(nv / _step) * _step;
            nv = Mathf.Clamp(nv, _min, _max);
            Redraw(nv);
            try { _onChanged?.Invoke(nv); _onEnd?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[FiresCore] KitControls.Slider SetAndNotify threw: " + ex.Message); }
        }

        /// <summary>Sets the value without calling onChanged.</summary>
        public void Set(float value) => Redraw(Mathf.Clamp(value, _min, _max));

        /// <summary>New range (e.g. a bet range that changes in play); the value is re-clamped and redrawn (no callback).</summary>
        public void SetRange(float min, float max)
        {
            _min = min; _max = Mathf.Max(min, max);
            if (UiSlider != null) { UiSlider.minValue = _min; UiSlider.maxValue = _max; }
            Redraw(Mathf.Clamp(Value, _min, _max));
        }

        public bool IsInteractable { get; private set; } = true;

        /// <summary>False = clicks/drags ignored and the slider drawn like a kit Disabled control (TextFaint fill, knob and
        /// value); true restores the normal colours.</summary>
        public void Interactable(bool on)
        {
            IsInteractable = on;
            if (_drag != null) _drag.enabled = on;
            var fi = _fill != null ? _fill.GetComponent<Image>() : null; if (fi != null) fi.color = on ? FiresTokens.Gold : FiresTokens.TextFaint;
            var ki = _knob != null ? _knob.GetComponent<Image>() : null; if (ki != null) ki.color = on ? FiresTokens.Text : FiresTokens.TextFaint;
            if (_valueLabel != null) _valueLabel.Color = on ? FiresTokens.Text : FiresTokens.TextFaint;
        }

        private void Redraw(float v)
        {
            Value = v;
            float t = To01(v);
            if (_fill != null) _fill.sizeDelta = new Vector2(Mathf.Max(_th, _trackW * t), _fill.sizeDelta.y);
            if (_knob != null) _knob.anchoredPosition = new Vector2(Mathf.Clamp(_trackW * t - _k * 0.5f, 0f, _trackW - _k), _knob.anchoredPosition.y);
            if (_valueLabel != null) _valueLabel.Value = Text(v);
            if (UiSlider != null) UiSlider.SetValueWithoutNotify(v);
        }
    }
}
