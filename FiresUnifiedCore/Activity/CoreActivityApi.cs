using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Activity
{
    /// <summary>
    /// Client-reported activity for the server's Studio Link activity log ([visual] 10-29, Fire: "We will add the player hooks into core as
    /// well so that when both mods are installed we are able to receive the info from the players game"). Add-only contract.
    /// Client: hooks call Report(kind, json) - one queue push; ActivityRpc flushes the queue to the server in ONE "FUC_Activity" package
    /// every 5 s or at 32 rows. Inert: Enabled(kind) is false until the server says a receiver is listening (Studio Link on the server),
    /// and per kind until its [Activity] key is on (server-synced, admin-locked; default OFF).
    /// Server: Studio Link calls SetServerReceiver(...) by reflection; Core validates every package (sender, character, size, rate,
    /// kind, json shape) before the receiver sees a row. The player name / uid come from the server's own peer, never from the client.
    /// </summary>
    public static class CoreActivityApi
    {
        public const int ApiVersion = 1;
        public const int MaxJson = 512;

        /// <summary>The client kinds, in wire order. Unknown kinds are dropped on both sides.</summary>
        public static readonly string[] Kinds = { "equip", "consume", "repair", "craft", "pickup", "skill", "command", "hotbar" };

        private static readonly Dictionary<string, ConfigEntry<bool>> s_keys = new Dictionary<string, ConfigEntry<bool>>();
        internal static Action<long, string, string, string> Receiver;   // server: (peer uid, player name, kind, json)

        internal static void Bind(ConfigFile config, ConfigSync configSync)
        {
            Add(config, configSync, "equip", "Report equip", "Equip / unequip of the player's own items.");
            Add(config, configSync, "consume", "Report consume", "Food, meads and other consumed items.");
            Add(config, configSync, "repair", "Report repair", "Items repaired at a station.");
            Add(config, configSync, "craft", "Report craft", "Crafting detail: recipe, quality, station and its level.");
            Add(config, configSync, "pickup", "Report pickup", "Items picked up into the player's inventory.");
            Add(config, configSync, "skill", "Report skills", "Skill raises, summed per skill once a minute.");
            Add(config, configSync, "command", "Report console commands", "Typed console commands (max 200 chars).", true);   // Fire 10-29: "Leave it on console"
            Add(config, configSync, "hotbar", "Report hotbar", "Hotbar slot use.");
        }

        private static void Add(ConfigFile config, ConfigSync configSync, string kind, string key, string what, bool on = false)
        {
            // 0.2.1090 (Fire 11-02: "If core needs to keep those keys in order for God complex to work when it's not installed on the client then we
            // keep them in" Core): bound and server-synced here as before 1086, so a client without God Complex follows the server too. A value
            // God Complex held in 0.8.61 / 0.8.62 copies back once.
            var def = new ConfigDefinition("Activity", key);
            bool hadOwn = HasSaved(config, def);
            var e = config.Bind(def, on, new ConfigDescription(what + " Sent to the server only when it runs VA Studio Link with activity logging on. [Synced with Server]"));
            if (!hadOwn && FiresCore.Config.CoreSettings.TryReadSaved("com.Fire.FiresGodComplex.cfg", "Activity", key, out string old) && bool.TryParse(old, out bool v))
            {
                e.Value = v;
                FiresCore.Logging.FiresLogger.LogInfo($"[Activity] {key} = {v} copied back from God Complex's cfg");
            }
            configSync.AddConfigEntry(e);
            s_keys[kind] = e;
        }

        private static bool HasSaved(ConfigFile config, ConfigDefinition def)
        {
            try
            {
                return HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) is Dictionary<ConfigDefinition, string> o && o.ContainsKey(def);
            }
            catch { return false; }
        }

        /// <summary>True when a row of this kind would be sent (server listening + the kind's key on). Hooks test this first.</summary>
        public static bool Enabled(string kind) => ActivityRpc.ServerListening && s_keys.TryGetValue(kind, out var e) && e.Value;

        /// <summary>Queue one client row (json: one object, max 512 chars). Cheap: a bool test and a queue push.</summary>
        public static void Report(string kind, string json)
        {
            if (!Enabled(kind) || json == null || json.Length > MaxJson) return;
            ActivityRpc.Enqueue(kind, json);
        }

        /// <summary>Server only: the receiver for validated client rows; null stops listening. Announced to every peer.</summary>
        public static void SetServerReceiver(Action<long, string, string, string> receiver)
        {
            Receiver = receiver;
            ActivityRpc.Announce();
        }
    }
}
