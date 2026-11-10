// Fires UI Shell: the hub
// window's header drag (move) and bottom-right grip (resize). Pointer deltas are divided by the canvas scale so the window follows
// the mouse 1:1 at any resolution. A double-click on the grip resets. Exists only inside the hub canvas (override ON).
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    // ONE Core move / resize path (Tools\UI_HANDLING_AUDIT.md): the dock's grips use this too (Mode.Scale: the element is
    // scaled through OnScale, so another owner's rect keeps its size), with the full pointer chain (press logged by the owner, no drag
    // threshold on a grip).
    internal sealed class ShellWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IInitializePotentialDragHandler
    {
        internal enum Mode { Move, Resize, Scale }
        internal Action Began;                 // drag start (the dock logs / snapshots)
        internal Action Pressed;               // pointer down (proof the grip is the pointer target)
        internal Func<Vector2, float> OnScale; // Mode.Scale: called with the SCREEN-pixel delta; returns nothing useful (0)
        internal float Sign = 1f;
        internal float SignY = 1f;            // -1 on top corners (drag up = taller); bottom corners grow downwards
        internal string Key;                   // the dock element id a Mode.Scale grip acts on              // Mode.Scale: +1 right-hand corners (drag right = bigger), -1 left-hand ones

        internal Mode DragMode;
        internal RectTransform Target;
        internal Func<Vector2> MinSize;        // UI units
        internal Func<Vector2> Area;           // UI units the window must stay inside (centre-anchored)
        internal Action Changed;               // while dragging (relayout)
        internal Action Ended;                 // drag end (save)
        internal Action DoubleClicked;         // grip: reset
        internal Graphic Tint;                 // grip art: Edge at rest, GoldBright on hover
        internal Color Rest = Color.white, Hover = Color.white;

        private Canvas _canvas;

        private float Scale
        {
            get
            {
                if (_canvas == null) _canvas = GetComponentInParent<Canvas>();
                return _canvas != null && _canvas.scaleFactor > 0.0001f ? _canvas.scaleFactor : 1f;
            }
        }

        public void OnBeginDrag(PointerEventData e) { Began?.Invoke(); }
        public void OnPointerDown(PointerEventData e) { Pressed?.Invoke(); }
        public void OnInitializePotentialDrag(PointerEventData e) { if (DragMode != Mode.Move) e.useDragThreshold = false; }

        public void OnDrag(PointerEventData e)
        {
            if (DragMode == Mode.Scale) { OnScale?.Invoke(new Vector2(e.delta.x * Sign, e.delta.y * SignY)); Changed?.Invoke(); return; }
            if (Target == null) return;
            Vector2 d = e.delta / Scale;
            Vector2 area = Area != null ? Area() : new Vector2(99999f, 99999f);
            if (DragMode == Mode.Move)
            {
                Vector2 p = Target.anchoredPosition + d;
                Target.anchoredPosition = Clamp(p, Target.sizeDelta, area);
            }
            else
            {
                // bottom-right grip: the top-left corner stays put (centre-anchored, so the centre moves by half the growth)
                Vector2 min = MinSize != null ? MinSize() : Vector2.zero;
                Vector2 size = Target.sizeDelta;
                Vector2 want = new Vector2(size.x + d.x, size.y - d.y);
                want = Vector2.Max(want, min);
                want = Vector2.Min(want, Vector2.Max(area, min));
                Vector2 grow = want - size;
                Target.sizeDelta = want;
                Target.anchoredPosition = Clamp(Target.anchoredPosition + new Vector2(grow.x * 0.5f, -grow.y * 0.5f), want, area);
            }
            Changed?.Invoke();
        }

        public void OnEndDrag(PointerEventData e) { Ended?.Invoke(); }

        public void OnPointerClick(PointerEventData e)
        {
            if (DragMode == Mode.Resize && e.clickCount >= 2) DoubleClicked?.Invoke();
        }

        public void OnPointerEnter(PointerEventData e) { if (Tint != null) Tint.color = Hover; }
        public void OnPointerExit(PointerEventData e) { if (Tint != null) Tint.color = Rest; }

        internal static Vector2 Clamp(Vector2 pos, Vector2 size, Vector2 area)
        {
            float mx = Mathf.Max(0f, (area.x - size.x) * 0.5f), my = Mathf.Max(0f, (area.y - size.y) * 0.5f);
            return new Vector2(Mathf.Clamp(pos.x, -mx, mx), Mathf.Clamp(pos.y, -my, my));
        }
    }
}
