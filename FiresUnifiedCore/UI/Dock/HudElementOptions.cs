// Core: options for FiresShell.RegisterHudElement (add-only contract; Tools\CORE_HUD_LAYOUT_PLAN.md).
using System;
using FiresCore.UI.Dock;

namespace FiresCore.UI.Shell
{
    public sealed class HudElementOptions
    {
        /// <summary>Shows a corner grip that scales the element (0.5..1.5; a docked one scales its group).</summary>
        public bool Resizable = true;
        /// <summary>The name shown in layout mode (default = the id).</summary>
        public string Label;
        /// <summary>The owning mod, for logs.</summary>
        public string Owner;
        /// <summary>Optional: dock to this element id by default, on <see cref="DefaultEdge"/>.</summary>
        public string DefaultDockTo;
        public DockEdge DefaultEdge;
        /// <summary>Optional: false = hidden (it leaves its group's flow and the gap closes until it shows again).</summary>
        public Func<bool> Visible;
        /// <summary>shown (forced active) while the HUD layout editor is open, so an element that is usually
        /// hidden (e.g. flight hints before the first ride) can be placed; its owner hides it again as usual afterwards.</summary>
        public bool ShowInLayout;
        /// <summary>also movable from the INVENTORY layout session (the hotbars, the health HUD). Its spot stays
        /// the HUD layout's (one source of truth); it snaps / docks only with HUD elements.</summary>
        public bool AlsoInInventoryLayout;
        /// <summary>(add-only): size resize instead of scale; called with the new size (canvas units). See DockElementOptions.SizeChanged.</summary>
        public Action<UnityEngine.Vector2> SizeChanged;
        /// <summary>(add-only): called on Reset (editor "Reset layout" or a per-element reset) so the owner clears its saved size
        /// and re-lays at its default. Unset = Core calls SizeChanged with the size the rect had when it registered.</summary>
        public Action SizeReset;
        /// <summary>(add-only): the smallest size for a SizeChanged resize.</summary>
        public UnityEngine.Vector2 MinSize = new UnityEngine.Vector2(40f, 40f);
        /// <summary>true = movable /
        /// resizable at ANY time, no editor needed (header drag via <see cref="DragHandle"/>, the bottom-right handle always shown), like
        /// FiresShell.RegisterLivePanel. Its contents stay clickable.</summary>
        public bool LiveEdit;
        /// <summary>(add-only): the part that drags the element (a header / title strip). Null = the whole rect (only when not
        /// LiveEdit; a LiveEdit element without a handle drags from its whole rect, which blocks nothing because no grab area is laid on top).</summary>
        public UnityEngine.RectTransform DragHandle;
        /// <summary>(add-only): with the cursor free, a double-click on the element reveals its grab area + resize handle until a
        /// click elsewhere or Esc (the old globe behaviour). No per-frame cost while hidden.</summary>
        public bool RevealOnDoubleClick;
        /// <summary>(add-only; the old globe resize): the grip scales PER AXIS. Mostly sideways = localScale.x only, mostly up/down
        /// = localScale.y only, diagonal = both; 0.3..2.5 per axis; saved as x + y; Reset = 1,1.</summary>
        public bool ScalePerAxis;
        /// <summary>(add-only): the rect that IS the element's art; hit area, outline and grips use exactly it (when the child
        /// graphics include hidden / transparent placeholders far from the art). Null = Core measures the drawn children.</summary>
        public UnityEngine.RectTransform HitBounds;
    }
}