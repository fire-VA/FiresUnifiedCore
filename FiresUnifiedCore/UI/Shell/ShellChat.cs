// Fires UI Shell (the chat in the unified look; while typing, other HUDs must not sit on top of it and the
// chat tabs must be clickable). Driven from ShellRuntime.LateUpdate, so only while [UI] Fires UI override is ON:
//  - FiresShell.ChatOpenChanged(bool) when the chat's typing focus opens / closes (not on a message fade-in), so each owner hides its
//    own UI (RPGClasses bars, FDT's FIRES PERF overlay);
//  - the cursor is free while typing (Core's modal InputBlock, only if nobody else holds it), so the tabs take clicks;
//  - the chat window gets the rounded kit background (its own sprite comes back on OFF).
using System;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static class ShellChat
    {
        private const float CloseGrace = 0.35f;   // a click on a chat tab blurs the input for a moment; don't flap the event

        private static bool s_open, s_setBlock;
        private static float s_lostFocusAt = -1f;
        private static Image s_bg;
        private static Sprite s_bgSprite;
        private static Image.Type s_bgType;
        private static Color s_bgColor;
        private static bool s_skinned;
        private static Chat s_skinnedFor;

        internal static event Action<bool> Changed;

        internal static bool IsOpen => s_open;

        internal static void Tick()
        {
            var chat = Chat.instance;
            if (chat == null) { if (s_open) SetOpen(false, "chat gone"); return; }
            Skin(chat);

            bool focus = false;
            try { focus = chat.HasFocus(); } catch { }
            if (focus) { s_lostFocusAt = -1f; if (!s_open) SetOpen(true, "typing focus"); return; }
            if (!s_open) return;
            if (s_lostFocusAt < 0f) { s_lostFocusAt = Time.unscaledTime; return; }
            bool windowUp = chat.m_chatWindow != null && chat.m_chatWindow.gameObject.activeInHierarchy;
            if (Time.unscaledTime - s_lostFocusAt >= CloseGrace || !windowUp) SetOpen(false, windowUp ? "typing focus left" : "chat window hidden");
        }

        private static void SetOpen(bool open, string why)
        {
            s_open = open;
            s_lostFocusAt = -1f;
            if (open)
            {
                if (!FiresCore.UI.InputBlock.IsBlocked) { FiresCore.UI.InputBlock.Block(true); s_setBlock = true; }
            }
            else if (s_setBlock) { s_setBlock = false; FiresCore.UI.InputBlock.Block(false); }
            ShellLog.Info("chat " + (open ? "opened" : "closed") + " (" + why + "); cursor " + (open ? (s_setBlock ? "freed (InputBlock set)" : "already free (InputBlock held by another UI)") : "back to the game") + "; ChatOpenChanged(" + open + ")");
            try { Changed?.Invoke(open); }
            catch (Exception e) { ShellLog.Warn("ChatOpenChanged handler threw " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Skin(Chat chat)
        {
            if (s_skinned && s_skinnedFor != chat) { s_skinned = false; s_bg = null; }   // direct hop: a NEW Chat is skinned again
            if (s_skinned || chat.m_chatWindow == null) return;
            s_skinned = true; s_skinnedFor = chat;
            s_bg = chat.m_chatWindow.GetComponent<Image>();
            if (s_bg == null) { ShellLog.Info("chat skin: the chat window has no background Image; left as is"); return; }
            s_bgSprite = s_bg.sprite; s_bgType = s_bg.type; s_bgColor = s_bg.color;
            s_bg.sprite = KitSprites.RoundedFill(FiresTokens.Radius);
            s_bg.type = Image.Type.Sliced;
            s_bg.color = new Color(0.086f, 0.067f, 0.047f, Mathf.Max(0.82f, s_bgColor.a));
            ShellLog.Info("chat skin: rounded kit background on the chat window (was '" + (s_bgSprite != null ? s_bgSprite.name : "none") + "')");
        }

        /// <summary>Master OFF: the chat's own background back, the event closed, the input block released if this set it.</summary>
        internal static void Restore()
        {
            if (s_open) SetOpen(false, "Fires UI override OFF");
            if (s_bg != null) { s_bg.sprite = s_bgSprite; s_bg.type = s_bgType; s_bg.color = s_bgColor; ShellLog.Info("chat skin: restored to vanilla"); }
            s_bg = null; s_bgSprite = null; s_skinned = false;
        }
    }
}
