using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.854 ([ghost], Fire 10-27: "job boards should get a reports section where we can see the last few days worth of jobs, and who has
    /// done what around the base ... hopper chests collecting output near kilns etc should be accounted for in production reports when it
    /// comes to 'stored' items ... if the player didnt store it than a companion had to right? (or a hopper chest)").
    /// Daily aggregates: (in-game day, who, verb, item) -> count, the last KeepDays days, capped at MaxRows, kept on the peer that runs the
    /// companions (their ZDO owner, normally the owner's client) in BepInEx\cache\FiresCore\JobReport\&lt;world&gt;.json. Add-only data.
    /// Verbs: "chore" (a finished chore; item = the chore name), "collected" (picked up into a companion's bag), "stored" (put into a chest
    /// by who = a companion name, "hopper" or "player"). Who stored what:
    ///  - a companion: <see cref="Actor"/> is set to its name around each chore tick (CompanionIdleBehavior), so any item a chore puts into a
    ///    container inventory (Inventory.AddItem / MoveItemToThis / StackAll postfixes) is counted for it;
    ///  - a hopper: the same scope around HopperChest's pulls ("hopper");
    ///  - the player: no scope, and the container is the one the local player has open in InventoryGui.
    /// "[JobReport] recorded &lt;who&gt; &lt;verb&gt; &lt;item&gt; x&lt;n&gt;" (Debug, once per 30 s per who+verb+item).
    /// </summary>
    public static class JobReport
    {
        public const int KeepDays = 7, MaxRows = 2000;

        public sealed class Row { public int Day; public string Who, Verb, Item; public int Count; }

        private static readonly List<Row> s_rows = new List<Row>();
        private static readonly Dictionary<string, Row> s_index = new Dictionary<string, Row>();
        private static string s_world;
        private static bool s_dirty;
        private static float s_saveAt;
        private static readonly Dictionary<string, float> s_said = new Dictionary<string, float>();

        /// <summary>Who is acting right now on this thread (a companion's chore tick, a hopper pull); null = nobody (a player action).</summary>
        [ThreadStatic] public static string Actor;
        private static int s_inMove;   // inside MoveItemToThis: its own AddItem calls are not counted twice

        /// <summary>Raised (main thread) after a new count is recorded - the Reports tab refreshes on it.</summary>
        public static event Action Changed;

        public static int Today => EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;

        public static IReadOnlyList<Row> Rows { get { EnsureLoaded(); return s_rows; } }

        public static void Record(string who, string verb, string item, int n)
        {
            if (n <= 0 || string.IsNullOrEmpty(who) || string.IsNullOrEmpty(verb) || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            EnsureLoaded();
            int day = Today;
            item = item ?? "";
            string key = day + "|" + who + "|" + verb + "|" + item;
            if (!s_index.TryGetValue(key, out Row r))
            {
                r = new Row { Day = day, Who = Cap(who), Verb = Cap(verb), Item = Cap(item) };
                s_index[key] = r; s_rows.Add(r);
                Trim();
            }
            r.Count += n;
            s_dirty = true;
            string sk = who + "|" + verb + "|" + item;
            if (!s_said.TryGetValue(sk, out float at) || Time.time - at > 30f)
            {
                if (s_said.Count > 256) s_said.Clear();
                s_said[sk] = Time.time;
                FiresCore.Logging.FiresLogger.LogDebug($"[JobReport] recorded {who} {verb} {(item.Length > 0 ? item + " " : "")}x{n} (day {day}, today's total {r.Count})");
            }
            Changed?.Invoke();
        }

        private static string Cap(string s) => s.Length > 64 ? s.Substring(0, 64) : s;

        private static void Trim()
        {
            int oldest = Today - KeepDays + 1;
            if (s_rows.Count <= MaxRows && s_rows.All(x => x.Day >= oldest)) return;
            s_rows.RemoveAll(x => x.Day < oldest);
            if (s_rows.Count > MaxRows) s_rows.RemoveRange(0, s_rows.Count - MaxRows);
            s_index.Clear();
            foreach (var x in s_rows) s_index[x.Day + "|" + x.Who + "|" + x.Verb + "|" + x.Item] = x;
        }

        // ---- persistence ---------------------------------------------------------------------------------------------
        private static string PathFor(string world) => System.IO.Path.Combine(FiresCore.Storage.FiresConfigPaths.CacheRoot, "JobReport", world + ".json");

        private static void EnsureLoaded()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world) || world == s_world) return;
            Save();
            s_world = world;
            s_rows.Clear(); s_index.Clear();
            try
            {
                string p = PathFor(world);
                if (File.Exists(p))
                {
                    var list = JsonConvert.DeserializeObject<List<Row>>(File.ReadAllText(p)) ?? new List<Row>();
                    foreach (var r in list)
                        if (r != null && !string.IsNullOrEmpty(r.Who) && !string.IsNullOrEmpty(r.Verb) && r.Count > 0)
                        { r.Item = r.Item ?? ""; s_rows.Add(r); }
                    Trim();
                    foreach (var x in s_rows) s_index[x.Day + "|" + x.Who + "|" + x.Verb + "|" + x.Item] = x;
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[JobReport] loaded {s_rows.Count} row(s) for world '{world}'");
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[JobReport] load failed: {ex.Message}"); }
        }

        internal static void Save()
        {
            if (!s_dirty || string.IsNullOrEmpty(s_world)) return;
            s_dirty = false;
            try
            {
                string p = PathFor(s_world);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(s_rows));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[JobReport] save failed: {ex.Message}"); }
        }

        // saved every 60 s while dirty, and on logout / quit
        [HarmonyPatch(typeof(EnvMan), "Update")]
        private static class SaveTick
        {
            private static void Postfix()
            {
                if (!s_dirty || Time.time < s_saveAt) return;
                s_saveAt = Time.time + 60f;
                Save();
            }
        }

        [HarmonyPatch(typeof(Game), "Shutdown")]
        private static class SaveOnShutdown { private static void Prefix() => Save(); }

        // ---- who stored it --------------------------------------------------------------------------------------------
        private static bool IsChestInventory(Inventory inv)
        {
            if (inv == null) return false;
            var me = Player.m_localPlayer;
            if (me != null && ReferenceEquals(inv, me.GetInventory())) return false;
            foreach (var c in CompanionController.AllCompanions)
            {
                var ci = c != null ? c.GetInventory() : null;
                if (ci != null && ReferenceEquals(ci.GetStorageInventory(), inv)) return false;
            }
            return true;
        }

        private static string WhoStores(Inventory into)
        {
            if (Actor != null) return Actor;
            var gui = InventoryGui.instance;
            var open = gui != null ? Traverse.Create(gui).Field("m_currentContainer").GetValue() as Container : null;
            return open != null && ReferenceEquals(open.GetInventory(), into) ? "player" : null;
        }

        private static void NoteStored(Inventory into, ItemDrop.ItemData item, int n)
        {
            if (item == null || n <= 0 || !IsChestInventory(into)) return;
            string who = WhoStores(into);
            if (who == null) return;
            Record(who, "stored", item.m_shared.m_name, n);
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
        private static class Stored_AddItem
        {
            private static void Prefix(ItemDrop.ItemData item, out int __state) => __state = item != null ? item.m_stack : 0;
            private static void Postfix(Inventory __instance, ItemDrop.ItemData item, bool __result, int __state)
            {
                if (__result && Actor != null && s_inMove == 0) NoteStored(__instance, item, __state);   // AddItem from code paths: only with an actor
            }
        }

        [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
        private static class Stored_MoveItemToThis
        {
            private static void Prefix() => s_inMove++;
            private static void Finalizer() => s_inMove--;
            private static void Postfix(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount)
            {
                if (s_inMove == 1 && fromInventory != null && !IsChestInventory(fromInventory)) NoteStored(__instance, item, amount);   // 1 = not nested in the other overload
            }
        }

        // the 2-argument overload (whole stack, first free slot) - vanilla's shift-click moves use it
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
        private static class Stored_MoveItemToThisWhole
        {
            private static void Prefix(ItemDrop.ItemData item, out int __state) { s_inMove++; __state = item != null ? item.m_stack : 0; }
            private static void Finalizer() => s_inMove--;
            private static void Postfix(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item, int __state)
            {
                if (s_inMove == 1 && fromInventory != null && !IsChestInventory(fromInventory) && __instance.ContainsItem(item)) NoteStored(__instance, item, __state);
            }
        }
    }
}
