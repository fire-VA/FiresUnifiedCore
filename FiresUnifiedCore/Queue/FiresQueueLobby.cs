using UnityEngine;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;

namespace FiresCore.Queue
{
    /// <summary>
    /// The shared lobby window for any <see cref="FiresQueueDef"/>. Open it for a queue id and it draws that queue's
    /// title/labels in the family window look (<see cref="FiresRoundedSkin"/>), shows the live roster with ready
    /// markers, and wires Join / Ready / Leave to <see cref="FiresQueueSystem"/>. One window instance serves every
    /// mod's queue — call <see cref="Toggle"/> / <see cref="Show"/> with the id. Modal while open, Esc closes.
    /// Client-only.
    /// </summary>
    public sealed class FiresQueueLobby : MonoBehaviour
    {
        private static FiresQueueLobby _instance;
        private static string _queueId;
        private static bool _open, _blocked;
        private static readonly object Token = new object();
        private Rect _rect = new Rect(120f, 170f, 330f, 320f);
        private const int WinId = 0x46_51_4C_31;   // "FQL1"

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        public static bool IsOpen => _open;
        public static string OpenQueueId => _queueId;

        /// <summary>Fires when the lobby window closes (Esc/Close/Toggle) with the queue id it was showing — mods
        /// park their queued-status dock chip here.</summary>
        public static event System.Action<string> Closed;

        public static void Toggle(string queueId)
        {
            if (IsClientOnly() || string.IsNullOrEmpty(queueId)) return;
            Ensure();
            if (_open && _queueId == queueId) SetOpen(false);
            else { _queueId = queueId; SetOpen(true); }
        }

        public static void Show(string queueId)
        {
            if (IsClientOnly() || string.IsNullOrEmpty(queueId)) return;
            Ensure();
            _queueId = queueId;
            SetOpen(true);
        }

        private static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresQueueLobby");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FiresQueueLobby>();
        }

        private static void SetOpen(bool open)
        {
            bool was = _open;
            _open = open;
            if (open)
            {
                FiresContextMenu.SuppressDriver = true;
                if (!InputBlock.IsBlocked) { InputBlock.Block(true); _blocked = true; }
                FiresCore.Input.FiresInputBlock.Acquire(Token);
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            else
            {
                FiresContextMenu.SuppressDriver = false;
                FiresCore.Input.FiresInputBlock.Release(Token);
                if (_blocked) { InputBlock.Block(false); _blocked = false; }
                if (was) { try { Closed?.Invoke(_queueId); } catch { } }
            }
        }

        private void Update()
        {
            if (!_open) return;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { SetOpen(false); return; }
            if (!Cursor.visible || Cursor.lockState != CursorLockMode.None) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        }

        private void OnGUI()
        {
            if (!_open) return;
            var def = FiresQueueSystem.GetDef(_queueId);
            string title = def != null ? def.Title : "Queue";
            FiresRoundedSkin.Ensure(0.97f);
            _rect = GUILayout.Window(WinId, _rect, DrawWindow, title, FiresRoundedSkin.Window, GUILayout.Width(330f));
            FiresRoundedSkin.DrawPendingTooltip();
        }

        private void DrawWindow(int id)
        {
            FiresRoundedSkin.ResetTooltip();
            var def = FiresQueueSystem.GetDef(_queueId);
            if (def == null)
            {
                GUILayout.Label("<i>Queue not registered.</i>", FiresRoundedSkin.Desc);
                if (GUILayout.Button("Close", FiresRoundedSkin.ButtonSmall)) SetOpen(false);
                GUI.DragWindow(new Rect(0f, 0f, 100000f, 22f));
                return;
            }

            if (!string.IsNullOrEmpty(def.Description)) { GUILayout.Label(def.Description, FiresRoundedSkin.Desc); GUILayout.Space(4f); }

            var roster = FiresQueueSystem.Roster(_queueId);
            int readyCount = 0;
            if (roster.Count == 0) GUILayout.Label("<i>No one queued yet.</i>", FiresRoundedSkin.Desc);
            else
                foreach (var member in roster)
                {
                    if (member.Ready) readyCount++;
                    GUILayout.Label((member.Ready ? "<color=#9CC24A>●</color> " : "<color=#7A6F58>○</color> ") + member.Name, FiresRoundedSkin.Label);
                }

            GUILayout.Space(6f);
            GUILayout.Label($"{roster.Count}/{def.MaxPlayers} seated  ·  {readyCount} ready  ·  need {def.MinPlayers}", FiresRoundedSkin.Hint);
            GUILayout.Space(8f);

            if (!FiresQueueSystem.LocalInQueue(_queueId))
            {
                if (GUILayout.Button(def.JoinLabel, FiresRoundedSkin.Button)) FiresQueueSystem.Join(_queueId);
            }
            else
            {
                bool ready = FiresQueueSystem.LocalReady(_queueId);
                if (GUILayout.Button(ready ? def.CancelReadyLabel : def.ReadyLabel, FiresRoundedSkin.Button)) FiresQueueSystem.SetReady(_queueId, !ready);
                if (GUILayout.Button(def.LeaveLabel, FiresRoundedSkin.Button)) FiresQueueSystem.Leave(_queueId);
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Close", FiresRoundedSkin.ButtonSmall)) SetOpen(false);

            GUI.DragWindow(new Rect(0f, 0f, 100000f, 22f));
        }
    }
}
