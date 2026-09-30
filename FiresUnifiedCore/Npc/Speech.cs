using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Speech bubbles every client near the speaker sees (Fire 2026-09-29: "give the bots chat bubbles like the companions have
    /// that show their intent … maybe some idle chatter"). Vanilla Chat.SetNpcText is local to one screen, so until 0.2.184 a
    /// companion's bubble reached only its owner and the stream never saw it. Here the speaker's peer routes the line to everybody
    /// and each client puts the bubble up on its own copy of the speaker. <see cref="Bubble"/> is for any body (the FDT bot's
    /// player too), rate-limited per speaker (the same line not again within 10 s, at most 5 a minute); the companions'
    /// CompanionChatHelper keeps its own limits and sends through <see cref="Send"/>. Lines: <see cref="IdleLine"/>,
    /// <see cref="CompanionChatHelper.ChatterFor"/>, <see cref="CompanionChatHelper.PickLine"/>, so the bot and the companions talk alike.
    /// </summary>
    public static class Speech
    {
        private const string Rpc = "FiresCore_Bubble";
        private const float SameLineSeconds = 10f, WindowSeconds = 60f, BubbleCull = 30f, BubbleHeight = 2f;
        private const int LinesPerWindow = 5;
        private const int MaxLength = 160;

        private static readonly Dictionary<ZDOID, (string line, float at)> s_lastLine = new Dictionary<ZDOID, (string, float)>();
        private static readonly Dictionary<ZDOID, Queue<float>> s_window = new Dictionary<ZDOID, Queue<float>>();
        private static bool s_registered;

        /// <summary>
        /// Puts <paramref name="text"/> in a bubble over <paramref name="body"/> for <paramref name="ttl"/> s on every client within
        /// 30 m of it. Call it on the peer that drives the body. False when rate-limited, empty, teleporting, or not in a world.
        /// </summary>
        public static bool Bubble(Character body, string text, float ttl = 5f, bool large = false)
        {
            if (body == null || string.IsNullOrEmpty(text)) return false;
            ZDOID id = body.GetZDOID();
            if (id.IsNone()) return false;
            float now = Time.time;
            if (s_lastLine.TryGetValue(id, out var last) && last.line == text && now - last.at < SameLineSeconds) return false;
            if (!s_window.TryGetValue(id, out Queue<float> times)) s_window[id] = times = new Queue<float>();
            while (times.Count > 0 && now - times.Peek() > WindowSeconds) times.Dequeue();
            if (times.Count >= LinesPerWindow) return false;
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);
            int size = large ? CompanionChatHelper.CHAT_BUBBLE_FONT_SIZE + 4 : CompanionChatHelper.CHAT_BUBBLE_FONT_SIZE;
            if (!Send(body.gameObject, $"<size={size}>{text}</size>", Mathf.Clamp(ttl, 1f, 20f), BubbleCull, BubbleHeight)) return false;
            times.Enqueue(now);
            s_lastLine[id] = (text, now);
            // A player's m_name is its prefab's ("Human", R70): name it as others see it.
            string who = body is Player player ? player.GetPlayerName() : body.GetHoverName();
            Debug.Log($"[Speech] {who}: \"{text}\" (to everyone within {BubbleCull:0} m, {ttl:0} s)");
            return true;
        }

        /// <summary>One idle-chatter line from the companions' pool (no immediate repeats).</summary>
        public static string IdleLine() => CompanionChatHelper.IdleLine();

        /// <summary>
        /// Routes an already formatted bubble (TMP tags included) over <paramref name="talker"/> to every client, this one included;
        /// empty text clears it. No rate limit here: callers keep their own. Falls back to this screen only when there is no route
        /// (not in a world yet). False when nothing was shown or sent.
        /// </summary>
        internal static bool Send(GameObject talker, string formatted, float ttl, float cull, float height)
        {
            if (talker == null) return false;
            // RPCs while the local player teleports / respawns stall the zone stream.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return false;
            ZNetView view = talker.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null || ZRoutedRpc.instance == null)
            {
                ShowHere(talker, formatted ?? "", ttl, cull, height);
                return true;
            }
            Register();
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, zdo.m_uid, formatted ?? "", ttl, cull, height);
            return true;
        }

        private static void Register()
        {
            if (s_registered || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZDOID, string, float, float, float>(Rpc, RPC_Bubble);
            s_registered = true;
        }

        private static void RPC_Bubble(long sender, ZDOID id, string formatted, float ttl, float cull, float height)
        {
            if (ZNetScene.instance == null) return;
            GameObject talker = ZNetScene.instance.FindInstance(id);
            if (talker == null) return;
            Player me = Player.m_localPlayer;
            if (!string.IsNullOrEmpty(formatted) && me != null && Vector3.Distance(me.transform.position, talker.transform.position) > cull) return;
            ShowHere(talker, formatted, ttl, cull, height);
        }

        private static void ShowHere(GameObject talker, string formatted, float ttl, float cull, float height)
        {
            if (Chat.instance == null || Hud.instance == null) return;
            try
            {
                Chat.instance.SetNpcText(talker, Vector3.up * height, cull, ttl, "", formatted, false);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Speech] bubble failed: {ex.Message}");
            }
        }

        // Every peer registers the route on world load, so a line from any peer lands everywhere.
        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Register
        {
            private static void Postfix()
            {
                s_registered = false;
                s_lastLine.Clear();
                s_window.Clear();
                try { Register(); } catch { }
            }
        }
    }
}
