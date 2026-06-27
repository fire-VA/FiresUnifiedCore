using UnityEngine;
using UnityEngine.EventSystems;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// Forwards pointer drags on the HUD panel to the controller so it can be repositioned. Drags
    /// only take effect in reposition mode (Esc menu open) — the controller enforces that and the
    /// background image's raycastTarget is only enabled then, so normal play never snags the panel.
    /// </summary>
    internal sealed class GroupHudDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private GroupHudController _controller;

        public void Initialize(GroupHudController controller) => _controller = controller;

        public void OnBeginDrag(PointerEventData eventData) => _controller?.OnDragStart(eventData.position);

        public void OnDrag(PointerEventData eventData) => _controller?.OnDrag(eventData.position);

        public void OnEndDrag(PointerEventData eventData) => _controller?.OnDragEnd();
    }
}
