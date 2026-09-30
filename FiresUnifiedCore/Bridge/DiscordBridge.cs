using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Raw Discord channel access for private tools, carried by Core so a public mod never reads folders outside the Valheim install
    /// (Fire, 2026-09-29): the Discord integration (FDI, public) sets the delegates; a private tool (FDT: the stream's clip / Live-now
    /// outbox, the test-channel commands) calls them. Core does no I/O here: delegates and one event only. Beside
    /// <see cref="DiscordSink"/>, which carries the game's domain events.
    ///
    /// Threads: callers call from the main thread; FDI may run the post off it and must call <c>done</c> and
    /// <see cref="RaiseChannelMessage"/> back on the main thread.
    /// </summary>
    public static class DiscordBridge
    {
        /// <summary>
        /// Posts to a channel as the bot, with mentions off. <paramref name="replyToMessageId"/> and the attachment
        /// (<paramref name="fileName"/>, <paramref name="file"/>, <paramref name="contentType"/>) may be null.
        /// <paramref name="done"/>(ok, messageIdOrError) is called once.
        /// </summary>
        public delegate void PostHandler(string channelId, string text, string replyToMessageId, string fileName, byte[] file, string contentType,
            Action<bool, string> done);

        /// <summary>Set by FDI. Null means FDI is absent (or not connected).</summary>
        public static PostHandler PostAsBot;

        /// <summary>Set by FDI: poll this channel (every ~2 s) and raise <see cref="ChannelMessage"/> for each new message.</summary>
        public static Action<string> Watch;

        /// <summary>Set by FDI: stop polling this channel.</summary>
        public static Action<string> Unwatch;

        /// <summary>A new message in a watched channel: channelId, messageId, authorId, authorIsBot, content.</summary>
        public static event Action<string, string, string, bool, string> ChannelMessage;

        /// <summary>FDI is there to post.</summary>
        public static bool Available => PostAsBot != null;

        /// <summary>
        /// Posts through FDI when it is there; otherwise calls <paramref name="done"/>(false, "FDI not installed") and returns false.
        /// A throw inside FDI's handler is caught and reported the same way.
        /// </summary>
        public static bool Post(string channelId, string text, string replyToMessageId = null, string fileName = null, byte[] file = null,
            string contentType = null, Action<bool, string> done = null)
        {
            PostHandler post = PostAsBot;
            if (post == null)
            {
                done?.Invoke(false, "FDI not installed");
                return false;
            }
            try
            {
                post(channelId, text, replyToMessageId, fileName, file, contentType, done);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DiscordBridge] post to {channelId} threw in the Discord integration: {ex.Message}");
                done?.Invoke(false, ex.Message);
                return false;
            }
        }

        /// <summary>Asks FDI to watch a channel; false when FDI is absent.</summary>
        public static bool WatchChannel(string channelId)
        {
            Action<string> watch = Watch;
            if (watch == null) return false;
            try { watch(channelId); return true; }
            catch (Exception ex) { Debug.LogWarning($"[DiscordBridge] watch {channelId} threw in the Discord integration: {ex.Message}"); return false; }
        }

        /// <summary>FDI calls this (on the main thread) for each new message in a watched channel. One failing subscriber doesn't stop the others.</summary>
        public static void RaiseChannelMessage(string channelId, string messageId, string authorId, bool authorIsBot, string content)
        {
            Action<string, string, string, bool, string> handlers = ChannelMessage;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<string, string, string, bool, string>)d)(channelId, messageId, authorId, authorIsBot, content); }
                catch (Exception ex) { Debug.LogWarning($"[DiscordBridge] a ChannelMessage handler ({d.Method.DeclaringType?.Name}) threw: {ex.Message}"); }
            }
        }
    }
}
