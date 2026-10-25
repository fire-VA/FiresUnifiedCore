using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.Archetypes.Effects;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Commands
{
    /// <summary>
    /// Picks a trip's area before it starts (Fire 10-24: "why is it a fixed 60m area?"). A ground ring (GroundRingManager, aiming
    /// preview) at the trip spot; mouse wheel = radius (team rule, Input.RadiusWheel; no height for a trip), 30-100 m in 5 m steps;
    /// left-click sends the trip, right-click or Esc cancels; 30 s without a choice cancels. The wheel is swallowed from vanilla
    /// (camera zoom) while picking.
    /// </summary>
    internal sealed class TripAreaPicker : MonoBehaviour
    {
        private const float MinArea = 30f, MaxArea = 100f, Step = 5f, IdleCancel = 30f;
        private static TripAreaPicker s_live;
        internal static bool WheelOwned => s_live != null;

        private CompanionController _companion;
        private TripBehavior.TripKind _kind;
        private Vector3 _spot;
        private string _prey, _label;
        private bool _everyMorning;
        private float _area = TripBehavior.DefaultArea, _started;
        private GroundRing _ring;

        internal static void Begin(CompanionController c, TripBehavior.TripKind kind, Vector3 spot, string prey, string label, bool everyMorning)
        {
            if (s_live != null) s_live.End("replaced");
            var go = new GameObject("FiresTripAreaPicker");
            s_live = go.AddComponent<TripAreaPicker>();
            s_live._companion = c; s_live._kind = kind; s_live._spot = spot; s_live._prey = prey ?? ""; s_live._label = label; s_live._everyMorning = everyMorning;
            s_live._started = Time.unscaledTime;
            s_live.Redraw();
        }

        private void Redraw()
        {
            if (_ring == null) _ring = GroundRingManager.Show(GroundRingKind.Buff, _spot, _area, 9999f, Player.m_localPlayer, true);
            else { GroundRingManager.Release(_ring); _ring = GroundRingManager.Show(GroundRingKind.Buff, _spot, _area, 9999f, Player.m_localPlayer, true); }
            Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft,
                $"{_companion?.GetDisplayName()}: {_label}{(_everyMorning ? ", every morning" : "")} - area {_area:0} m (wheel to resize, left-click to send, right-click to cancel)");
        }

        private void Update()
        {
            if (_companion == null || Player.m_localPlayer == null) { End("gone"); return; }
            if (FiresCore.Input.FiresInputBlock.IsTyping()) return;
            if (FiresCore.Input.RadiusWheel.Read(out float dRadius, out _) && dRadius != 0f)
            {
                float a = Mathf.Clamp(_area + dRadius * Step, MinArea, MaxArea);
                if (!Mathf.Approximately(a, _area)) { _area = a; Redraw(); }
                _started = Time.unscaledTime;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Mouse0))
            {
                FiresCore.Input.FiresInputBlock.ConsumeThisFrame(KeyCode.Mouse0);   // the click sends the trip, it doesn't swing
                FiresCore.Input.FiresInputBlock.ConsumeThisFrame("Attack");
                Send();
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Mouse1) || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                FiresCore.Input.FiresInputBlock.ConsumeThisFrame(KeyCode.Mouse1);
                End("cancelled");
                Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "Trip cancelled");
                return;
            }
            if (Time.unscaledTime - _started > IdleCancel) End("timed out");
        }

        private void Send()
        {
            var c = _companion; var kind = _kind; var spot = _spot; var prey = _prey; var area = _area; bool morning = _everyMorning;
            End("sent");
            FiresCore.Logging.FiresLogger.LogDebug($"[Trip] {c.companionName}: area picked {area:0} m at ({spot.x:0},{spot.z:0}){(morning ? ", every morning" : "")}");
            CompanionContextProvider.StartTrip(c, kind, spot, prey, area, morning);
        }

        private void End(string why)
        {
            if (_ring != null) { GroundRingManager.Release(_ring); _ring = null; }
            if (s_live == this) s_live = null;
            Destroy(gameObject);
        }

        private void OnDestroy() { if (_ring != null) GroundRingManager.Release(_ring); if (s_live == this) s_live = null; }
    }

    // Vanilla's wheel (camera zoom) reads 0 while the trip area picker owns it.
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class TripAreaPickerWheelSwallow
    {
        private static void Postfix(ref float __result)
        {
            if (TripAreaPicker.WheelOwned) __result = 0f;
        }
    }
}
