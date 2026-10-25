// Fires UI Shell (the plate's pills are configurable): the minimap plate's
// pill row is filled from a registry. Core ships weather now, forecast, weather warning, rested / comfort and FPS; any mod adds
// more through FiresShell.RegisterPlatePill (Tools\CORE_PLATE_PILLS_API.md, add-only). At most 3 show; each player picks which
// (right-click the plate), saved per character at <BepInEx>\cache\FiresUnifiedCore\platepills_<playerId>.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FiresCore.UI.Shell
{
    /// <summary>What a plate pill shows right now. Text null or empty = the pill hides this tick. Add-only.</summary>
    public struct PillContent
    {
        public string Text;
        public string Tooltip;
        public StatState State;
        public PillContent(string text, string tooltip = null, StatState state = StatState.Normal) { Text = text; Tooltip = tooltip; State = state; }
    }

    internal static class PlatePills
    {
        internal const int MaxShown = 3;
        internal sealed class Entry { public string Id, Label; public int Order; public Func<PillContent> Content; public float Refresh, NextAt; public PillContent Last; public bool Failed; }
        private static readonly Dictionary<string, Entry> s_byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        internal static int Version { get; private set; }
        private static readonly string[] DefaultPick = { "core.weather", "core.forecast", "core.rested" };

        internal static void Register(string id, string label, int order, Func<PillContent> content, float refreshSeconds)
        {
            if (string.IsNullOrEmpty(id) || content == null) { ShellLog.Warn("plate pills: RegisterPlatePill ignored (empty id or null content)"); return; }
            s_byId[id] = new Entry { Id = id, Label = string.IsNullOrEmpty(label) ? id : label, Order = order, Content = content, Refresh = Mathf.Clamp(refreshSeconds, 0.25f, 60f) };
            Version++;
            ShellLog.Info($"plate pills: '{id}' registered (\"{label}\", order {order}, every {Mathf.Clamp(refreshSeconds, 0.25f, 60f):0.##} s)");
        }

        internal static void Unregister(string id) { if (id != null && s_byId.Remove(id)) { Version++; ShellLog.Info("plate pills: '" + id + "' unregistered"); } }

        internal static List<Entry> All()
        {
            EnsureCore();
            var l = new List<Entry>(s_byId.Values);
            l.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            return l;
        }

        internal static Entry Find(string id) { EnsureCore(); return id != null && s_byId.TryGetValue(id, out var e) ? e : null; }

        // ---- the player's pick ----------------------------------------------------------------------------
        private static List<string> s_pick;
        private static long s_pickFor = long.MinValue;

        private static long PlayerId()
        {
            try { return Game.instance != null && Game.instance.GetPlayerProfile() != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L; } catch { return 0L; }
        }

        private static string PickPath(long id) => Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "platepills_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt");

        internal static List<string> Pick()
        {
            long id = PlayerId();
            if (s_pick != null && s_pickFor == id) return s_pick;
            s_pickFor = id;
            s_pick = new List<string>();
            string path = PickPath(id);
            try
            {
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path)) { var t = line.Trim(); if (t.Length > 0 && !s_pick.Contains(t)) s_pick.Add(t); }
                    ShellLog.Info($"plate pills: pick loaded {path} ({string.Join(", ", s_pick.ToArray())})");
                }
                else { s_pick.AddRange(DefaultPick); ShellLog.Info("plate pills: default pick (" + string.Join(", ", DefaultPick) + ") for player " + id); }
            }
            catch (Exception e) { s_pick.Clear(); s_pick.AddRange(DefaultPick); ShellLog.Warn("plate pills: pick file unreadable (" + e.Message + "), using the default"); }
            return s_pick;
        }

        internal static void Toggle(string id)
        {
            var pick = Pick();
            if (pick.Contains(id)) pick.Remove(id);
            else { if (pick.Count >= MaxShown) pick.RemoveAt(0); pick.Add(id); }
            try
            {
                string path = PickPath(PlayerId());
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, pick.ToArray());
                ShellLog.Info($"plate pills: pick saved ({string.Join(", ", pick.ToArray())}) -> {path}");
            }
            catch (Exception e) { ShellLog.Warn("plate pills: pick not saved (" + e.Message + ")"); }
            Version++;
        }

        // ---- Core's own pills ----------------------------------------------------------------------------
        private static bool s_core;
        private static void EnsureCore()
        {
            if (s_core) return;
            s_core = true;
            Register("core.weather", "Weather now", 0, WeatherNow, 1f);
            Register("core.forecast", "Forecast", 10, Forecast, 2f);
            Register("core.warning", "Weather warning", 20, Warning, 2f);
            Register("core.rested", "Rested / comfort", 30, Rested, 1f);
            Register("core.fps", "FPS", 90, Fps, 0.5f);
            Register("core.instances", "Instance count", 95, Instances, 2f);   // the "inst" number in FIRES PERF
        }

        private static FieldInfo s_zdoById;
        private static PillContent Instances()
        {
            var zs = ZNetScene.instance;
            if (zs == null) return default;
            int inst = zs.NrOfInstances();
            string zdos = "?";
            try
            {
                if (s_zdoById == null) s_zdoById = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dict = ZDOMan.instance != null ? s_zdoById?.GetValue(ZDOMan.instance) as System.Collections.ICollection : null;
                if (dict != null) zdos = dict.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return new PillContent(inst + " inst", "Loaded instances (ZNetScene) of " + zdos + " ZDOs known");
        }

        internal static string Pretty(string env)
        {
            if (string.IsNullOrEmpty(env)) return "";
            switch (env)
            {
                case "ThunderStorm": return "Thunderstorm";
                case "LightRain": return "Light rain";
                case "SnowStorm": return "Snowstorm";
                case "Twilight_Clear": return "Clear";
                case "DeepForest_Mist": return "Forest mist";
                case "Ashrain": return "Ash rain";
                case "Darklands_dark": return "Mistlands gloom";
            }
            return env.Replace('_', ' ');
        }

        private static bool Wet(string env) => env != null && (env.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0);
        private static bool Danger(string env) => env != null && (env.IndexOf("ThunderStorm", StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("SnowStorm", StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("Ashrain", StringComparison.OrdinalIgnoreCase) >= 0);

        private static PillContent WeatherNow()
        {
            var env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
            return env == null ? default : new PillContent(Pretty(env.m_name), "Weather now (" + env.m_name + ")");
        }

        // Vanilla EnvMan.UpdateEnvironment: period = (long)m_totalSeconds / m_environmentDuration, Random.InitState((int)period), a weighted
        // pick from GetAvailableEnvironments(current biome sector), ashlands / deep north overrides. The same pick for period + 1 is the
        // forecast. Overrides (events, forced zones, debug env) cannot be forecast: the pill says so.
        private static FieldInfo s_totalSec, s_curBiome;
        private static long s_predPeriod = long.MinValue;
        private static string s_predEnv;
        private static long s_lastCheckedPeriod = long.MinValue;

        private static PillContent Forecast()
        {
            var em = EnvMan.instance;
            if (em == null) return default;
            try
            {
                if (s_totalSec == null) s_totalSec = typeof(EnvMan).GetField("m_totalSeconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (s_curBiome == null) s_curBiome = typeof(EnvMan).GetField("m_currentBiome", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                long total = (long)Convert.ToDouble(s_totalSec.GetValue(em));
                long dur = Math.Max(1L, em.m_environmentDuration);
                long period = total / dur;
                var cur = em.GetCurrentEnvironment();
                string curName = cur != null ? cur.m_name : "";

                // check the last prediction once its period arrives
                if (s_predPeriod == period && s_lastCheckedPeriod != period)
                {
                    s_lastCheckedPeriod = period;
                    ShellLog.Info($"plate pills: forecast check for period {period}: predicted '{s_predEnv}', actual '{curName}' ({(s_predEnv == curName ? "MATCH" : "differs - an override, a biome change or a wrong prediction")})");
                }

                var biome = s_curBiome.GetValue(em) as BiomeSector;
                if (biome == null) return default;
                string next = Predict(em, biome, period + 1);
                if (next == null) return new PillContent("Forecast n/a", "No environment list for this biome", StatState.Muted);
                if (s_predPeriod != period + 1 || s_predEnv != next)
                {
                    s_predPeriod = period + 1; s_predEnv = next;
                    ShellLog.Info($"plate pills: forecast '{next}' for period {period + 1} (now '{curName}' in period {period}, {(period + 1) * dur - total} game s left, biome {biome.Biome})");
                }
                bool overridden = !string.IsNullOrEmpty(em.m_debugEnv) || (RandEventSystem.instance != null && !string.IsNullOrEmpty(RandEventSystem.instance.GetEnvOverride()));
                long left = (period + 1) * dur - total;
                // A flavour line from the forecast table, per context, the same all period long
                string ctx = ForecastContext();
                string text = ForecastLines.Pick(ctx, curName, next, period) ?? (Wet(curName) && !Wet(next) ? "Clearing up" : "Hard to say");
                string lineSig = text + "/" + period;
                if (lineSig != s_lineSig) { s_lineSig = lineSig; ShellLog.Info($"plate pills: forecast line '{text}' (env {curName} -> {next}, context {ctx})"); }
                string tip = $"Next: {Pretty(next)} in about {Mathf.CeilToInt(left / 60f)} min (game time), if you stay in {biome.Biome}." + (overridden ? " An event or override is active now: it wins while it lasts." : "");
                return new PillContent(text, tip, Danger(next) ? StatState.Warn : StatState.Normal);
            }
            catch (Exception e) { return new PillContent("Forecast n/a", e.Message, StatState.Muted); }
        }

        private static string s_lineSig;

        // Where the player is, for the forecast lines: a boss near, an event on, inside a dungeon / cave, else normal.
        private static string ForecastContext()
        {
            var p = Player.m_localPlayer;
            if (p == null) return "normal";
            try
            {
                foreach (var c in Character.GetAllCharacters())
                    if (c != null && c.IsBoss() && !c.IsDead() && Vector3.Distance(c.transform.position, p.transform.position) < 80f) return "boss";
                if (RandEventSystem.instance != null && RandEventSystem.instance.GetActiveEvent() != null) return "raid";
                if (p.InInterior()) return "cave";
            }
            catch { }
            return "normal";
        }

        private static string Predict(EnvMan em, BiomeSector biome, long period)
        {
            var state = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState((int)period);
                var list = em.GetAvailableEnvironments(biome);
                if (list == null || list.Count == 0) return null;
                float max = 0f;
                foreach (var e in list) if (!e.m_ashlandsOverride && !e.m_deepnorthOverride) max += e.m_weight;
                float roll = UnityEngine.Random.Range(0f, max), acc = 0f;
                EnvSetup pick = null;
                foreach (var e in list)
                {
                    if (e.m_ashlandsOverride || e.m_deepnorthOverride) continue;
                    acc += e.m_weight;
                    if (acc >= roll) { pick = e.m_env; break; }
                }
                if (pick == null) { var last = list[list.Count - 1]; pick = last.m_ashlandsOverride || last.m_deepnorthOverride ? null : last.m_env; }
                var cam = Utils.GetMainCamera();
                if (cam != null)
                {
                    Vector3 p = cam.transform.position;
                    bool ash = WorldGenerator.IsAshlands(p.x, p.z), deep = WorldGenerator.IsDeepnorth(p.x, p.y);
                    foreach (var e in list) { if (e.m_ashlandsOverride && ash) pick = e.m_env; if (e.m_deepnorthOverride && deep) pick = e.m_env; }
                }
                return pick != null ? pick.m_name : null;
            }
            finally { UnityEngine.Random.state = state; }
        }

        private static PillContent Warning()
        {
            try
            {
                var rs = RandEventSystem.instance;
                var ev = rs != null ? rs.GetActiveEvent() : null;
                if (ev != null && !string.IsNullOrEmpty(ev.m_name))
                {
                    string msg = Localization.instance != null && !string.IsNullOrEmpty(ev.m_startMessage) ? Localization.instance.Localize(ev.m_startMessage) : ev.m_name;
                    return new PillContent("Event: " + msg, "A raid or event is active near you (" + ev.m_name + ")", StatState.Bad);
                }
                var cur = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
                if (cur != null && Danger(cur.m_name)) return new PillContent(Pretty(cur.m_name) + "!", "Dangerous weather now", StatState.Warn);
                if (s_predEnv != null && Danger(s_predEnv)) return new PillContent(Pretty(s_predEnv) + " coming", "Forecast for the next weather period", StatState.Warn);
            }
            catch { }
            return default;   // nothing dangerous: the pill hides
        }

        private static PillContent Rested()
        {
            var p = Player.m_localPlayer;
            if (p == null || p.GetSEMan() == null) return default;
            var se = p.GetSEMan().GetStatusEffect("Rested".GetStableHashCode());
            int comfort = 0;
            try { comfort = SE_Rested.CalculateComfortLevel(p); } catch { }
            if (se != null)
            {
                int left = Mathf.Max(0, Mathf.FloorToInt(se.GetRemaningTime()));
                return new PillContent("Rested " + (left / 60) + ":" + (left % 60).ToString("00"), "Rested time left; comfort here " + comfort, StatState.Good);
            }
            return comfort > 1 ? new PillContent("Comfort " + comfort, "Comfort level here (rest near a fire to get Rested)") : default;
        }

        private static float s_fps = -1f;
        private static PillContent Fps()
        {
            float fps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            s_fps = s_fps < 0f ? fps : Mathf.Lerp(s_fps, fps, 0.3f);
            return new PillContent(Mathf.RoundToInt(s_fps) + " FPS", null, s_fps < 30f ? StatState.Bad : s_fps < 50f ? StatState.Warn : StatState.Normal);
        }
    }

    internal static partial class ShellMinimap
    {
        private static readonly List<(RectTransform rt, KitLabel label, PlatePills.Entry entry)> s_platePills = new List<(RectTransform, KitLabel, PlatePills.Entry)>();
        private static int s_platePillsVersion = -1;
        private static RectTransform s_picker;

        // Rebuilds the pill nodes when the registry or the pick changes; refreshes their text at each pill's rate.
        // Returns the visible pills for LayoutPlate.
        private static List<(RectTransform rt, float w)> PlatePillRow()
        {
            if (s_plate == null) return new List<(RectTransform, float)>();
            if (PlatePills.Version != s_platePillsVersion)
            {
                s_platePillsVersion = PlatePills.Version;
                foreach (var pp in s_platePills) if (pp.rt != null) UnityEngine.Object.Destroy(pp.rt.gameObject);
                s_platePills.Clear();
                int n = 0;
                foreach (var id in PlatePills.Pick())
                {
                    var e = PlatePills.Find(id);
                    if (e == null || n >= PlatePills.MaxShown) continue;   // a missing mod's pill stays in the pick, not drawn
                    var rt = PillNode(s_plate, "Pill_" + n, out var label, FiresTokens.Text);
                    s_platePills.Add((rt, label, e));
                    e.NextAt = 0f; n++;
                }
                ShellLog.Info("plate pills: showing " + s_platePills.Count + " (" + string.Join(", ", PlatePills.Pick().ToArray()) + ")");
            }
            var row = new List<(RectTransform, float)>();
            float now = Time.unscaledTime;
            foreach (var pp in s_platePills)
            {
                var e = pp.entry;
                if (now >= e.NextAt)
                {
                    e.NextAt = now + e.Refresh;
                    try { e.Last = e.Content(); }
                    catch (Exception ex) { if (!e.Failed) { e.Failed = true; ShellLog.Warn($"plate pills: '{e.Id}' threw {ex.GetType().Name}: {ex.Message}"); } e.Last = default; }
                    pp.label.Value = e.Last.Text ?? "";
                    pp.label.Color = e.Last.State == StatState.Good ? FiresTokens.Ok : e.Last.State == StatState.Warn ? FiresTokens.Warn
                        : e.Last.State == StatState.Bad ? FiresTokens.Danger : e.Last.State == StatState.Muted ? FiresTokens.TextFaint : FiresTokens.Text;
                }
                bool show = !string.IsNullOrEmpty(e.Last.Text);
                if (pp.rt.gameObject.activeSelf != show) pp.rt.gameObject.SetActive(show);
                if (show) row.Add((pp.rt, pp.label.PreferredWidth + 28f));
            }
            return row;
        }

        // right-click the plate: the picker (every registered pill, ticked = shown; at most 3)
        internal static void PlateClicked(PointerEventData e)
        {
            if (!Cursor.visible) return;
            if (e.button != PointerEventData.InputButton.Right) return;
            if (s_picker != null) { ClosePicker(); return; }
            OpenPicker();
        }

        private static void OpenPicker()
        {
            var all = PlatePills.All();
            var pick = PlatePills.Pick();
            const float W = 260f, RowH = 28f;
            float H = 40f + all.Count * RowH + 12f;
            s_picker = Widgets.Node(s_rect, "FiresPlatePillPicker");
            s_picker.SetAsLastSibling();
            s_picker.anchorMin = s_picker.anchorMax = new Vector2(0.5f, 0.5f); s_picker.pivot = new Vector2(1f, 1f);
            s_picker.sizeDelta = new Vector2(W, H);
            s_picker.position = s_plate.TransformPoint(new Vector3(s_plate.rect.xMin - 8f, s_plate.rect.yMax, 0f));
            var fill = Widgets.Box(s_picker, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), new Color(0.086f, 0.067f, 0.047f, 0.97f), true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = true;
            var edge = Widgets.Box(s_picker, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            var title = KitText.Create(s_picker, "Title", "Plate pills (max " + PlatePills.MaxShown + ")", FontRole.SansBold, 13f, FiresTokens.Gold, KitAlign.Left, 0f, false, false);
            Widgets.Place(title.Rect, 12f, 8f, W - 24f, 24f);
            float y = 40f;
            foreach (var e in all)
            {
                bool on = pick.Contains(e.Id);
                var t = Widgets.DiamondToggle(s_picker, e.Id.Replace('.', '_'), e.Label, on, 12f, y, RowH - 2f);
                string id = e.Id;
                ShellWidgets.Clickable(t, () => { PlatePills.Toggle(id); ClosePicker(); OpenPicker(); RefreshPlate(); });
                y += RowH;
            }
            ShellLog.Info("plate pills: picker open (" + all.Count + " pills)");
        }

        private static void ClosePicker()
        {
            if (s_picker != null) UnityEngine.Object.Destroy(s_picker.gameObject);
            s_picker = null;
        }

        private static void UpdatePicker() { if (s_picker != null && !Cursor.visible && !s_testPicker) ClosePicker(); }

        // ---- test hooks (no OS input in tests). Read by reflection on FiresCore.UI.Shell.ShellMinimap. ----------
        private static bool s_testPicker;

        /// <summary>Opens the plate pill picker as a right-click would (root "FiresPlatePillPicker", one "Toggle/&lt;id with . as _&gt;" per
        /// pill). Stays open without a free cursor until TestClosePlatePicker. False when the minimap shell is not built.</summary>
        public static bool TestOpenPlatePicker()
        {
            if (s_plate == null || s_rect == null) { ShellLog.Info("plate pills: test open picker - no plate (override OFF or no minimap)"); return false; }
            ClosePicker(); s_testPicker = true; OpenPicker();
            ShellLog.Info("plate pills: test open picker");
            return s_picker != null;
        }

        /// <summary>Toggles one pill in the pick exactly as clicking its row does (saves the pick file, redraws). False for an unknown id.</summary>
        public static bool TestTogglePlatePill(string id)
        {
            if (PlatePills.Find(id) == null) { ShellLog.Info("plate pills: test toggle '" + id + "' - unknown id"); return false; }
            PlatePills.Toggle(id);
            if (s_picker != null) { ClosePicker(); OpenPicker(); }
            RefreshPlate();
            ShellLog.Info("plate pills: test toggle '" + id + "' -> pick (" + string.Join(", ", PlatePills.Pick().ToArray()) + ")");
            return true;
        }

        /// <summary>The current pick, in order (what the plate shows, max 3).</summary>
        public static string[] TestPlatePick() => PlatePills.Pick().ToArray();

        /// <summary>Closes the picker (test or right-click opened).</summary>
        public static void TestClosePlatePicker()
        {
            s_testPicker = false; ClosePicker();
            ShellLog.Info("plate pills: test close picker");
        }

        private static void RestorePlatePills()
        {
            ClosePicker();
            s_platePills.Clear(); s_platePillsVersion = -1;   // the nodes go with the plate
        }
    }

    internal sealed class PlateClick : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData e) => ShellMinimap.PlateClicked(e);
    }
}
