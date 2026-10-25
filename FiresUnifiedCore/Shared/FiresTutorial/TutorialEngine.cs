using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace FiresTutorial
{
    // FiresTutorial: a small game-style tutorial manager, SHARED SOURCE compiled into each host (Valcast, Core) as
    // linked files. Every type is internal, so the copies never clash; hosts talk across mods only through plain System types
    // (TutorialJson + callbacks). A host registers a TutorialBook of short chapters; each step introduces at most one key and
    // advances only when its Done() check sees the action happened, never on a timer. Skip / Back on every step, resume where
    // you left off (per character), Exit restores. Proof: one Debug line per step and a results file in BepInEx/ (inside the
    // game folder). No mod-specific types here. EDITING THIS FOLDER CHANGES EVERY HOST'S NEXT BUILD: post a coordination line first.
    internal sealed class TutorialStep
    {
        public string Id = "", Title = "", Text = "", Watch = "";
        public Func<string> Key;          // the one key this step teaches (live binding text), or null
        public Func<bool> Done;           // true once the action has happened
        public Action Enter;              // optional: called when the step starts (snapshot positions, counters)
        public Func<string> Hint;         // optional live line (e.g. "no creature close: Skip is fine here")
    }

    internal sealed class TutorialChapter
    {
        public string Id = "", Title = "", Intro = "";
        public readonly List<TutorialStep> Steps = new List<TutorialStep>();
        public TutorialChapter Add(TutorialStep s) { Steps.Add(s); return this; }
    }

    internal sealed class TutorialBook
    {
        public string Id = "", Title = "", LogTag = "Tutorial", ResultsFile = "Tutorial_results.txt", Version = "";
        public readonly List<TutorialChapter> Chapters = new List<TutorialChapter>();
        public Action<string> LogDebug, LogInfo;
        public Func<bool> SkipPressed, BackPressed, ExitPressed, InputBlocked;
        public Func<string> ControlsLine;   // "Skip: … · Back: … · Exit: …"
        public Action OnStart, OnStop;       // e.g. turn needed modules on / restore them and the camera
        public TutorialBook Add(TutorialChapter c) { Chapters.Add(c); return this; }
        internal IEnumerable<(TutorialChapter ch, TutorialStep st)> AllSteps() => Chapters.SelectMany(c => c.Steps.Select(s => (c, s)));
        internal int Total => Chapters.Sum(c => c.Steps.Count);
    }

    internal static class TutorialRegistry
    {
        private static readonly Dictionary<string, TutorialBook> s_books = new Dictionary<string, TutorialBook>(StringComparer.OrdinalIgnoreCase);
        public static void Register(TutorialBook book) { if (book != null) s_books[book.Id] = book; }
        public static TutorialBook Get(string id) => id != null && s_books.TryGetValue(id, out var b) ? b : null;
        public static IEnumerable<TutorialBook> All => s_books.Values;
    }

    internal sealed class TutorialRunner : MonoBehaviour
    {
        public static TutorialRunner Instance { get; private set; }
        public static bool IsRunning => Instance != null && Instance._book != null;
        public static string RunningBookId => IsRunning ? Instance._book.Id : null;
        // The card follows the player's hide-HUD (Hud.IsUserHidden); a test that shows the tutorial on purpose sets this.
        public static bool ForceVisible;
        public static string CurrentStepId => IsRunning ? Instance.Step?.Id : null;
        public static int CurrentStepNumber => IsRunning ? Instance.GlobalIndex() + 1 : 0;

        private const float CelebrateSeconds = 1.3f, ChapterCardSeconds = 3.5f;
        private static readonly string[] Cheers = { "Nice!", "Got it!", "Lovely shot!", "That's a wrap!", "Smooth!", "Perfect!" };

        private TutorialBook _book;
        private int _ch, _st;
        private float _stepStart, _celebrateUntil, _cardUntil;
        private string _cheer = "", _card = "";
        private bool _advancePending, _entered;
        private int _startFrame;
        // "chapter/step" -> (done|skipped, seconds)
        private readonly Dictionary<string, (string status, float seconds)> _results = new Dictionary<string, (string, float)>();
        private Component _audio; private UnityEngine.Object _chime, _fanfare;
        private GUIStyle _box, _title, _body, _key, _small, _cheerStyle, _cardStyle, _button;
        private Texture2D _bg, _barBg, _barFg, _keyBg;

        private TutorialChapter Chapter => _book != null && _ch < _book.Chapters.Count ? _book.Chapters[_ch] : null;
        private TutorialStep Step => Chapter != null && _st < Chapter.Steps.Count ? Chapter.Steps[_st] : null;
        private static string Key(TutorialChapter c, TutorialStep s) => c.Id + "/" + s.Id;

        private void Awake() => Instance = this;

        // ── public control ──────────────────────────────────────────────────────────────────────────────────────────
        public static bool Begin(string bookId, string chapterId = null)
        {
            var book = TutorialRegistry.Get(bookId);
            if (Instance == null || book == null || book.Total == 0) return false;
            Instance.StartBook(book, chapterId);
            return true;
        }

        public static void End(string why) { if (Instance != null && Instance._book != null) Instance.StopBook(why); }
        public static void SkipStep() { if (IsRunning) Instance.Finish("skipped"); }
        public static void BackStep() { if (IsRunning) Instance.Back(); }

        private void StartBook(TutorialBook book, string chapterId)
        {
            if (_book != null) StopBook("restarted");
            _book = book;
            _startFrame = Time.frameCount;   // the key that opened it is often the exit key: ignore keys on this frame
            _results.Clear();
            LoadProgress(out int ch, out int st);
            _ch = ch; _st = st;
            if (!string.IsNullOrEmpty(chapterId))
            {
                int i = _book.Chapters.FindIndex(c => c.Id.Equals(chapterId, StringComparison.OrdinalIgnoreCase)
                    || c.Title.IndexOf(chapterId, StringComparison.OrdinalIgnoreCase) >= 0);
                if (i >= 0) { _ch = i; _st = 0; }
            }
            if (Step == null) { _ch = 0; _st = 0; }   // finished before: start over
            try { _book.OnStart?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[{_book.LogTag}] start: {e.Message}"); }
            _card = $"{_book.Title}\n<size=22>{Chapter.Title}</size>"; _cardUntil = Time.unscaledTime + 2.5f;
            BeginStep();
            _book.LogInfo?.Invoke($"[{_book.LogTag}] started at step {GlobalIndex() + 1} ({Chapter.Id}/{Step.Id})");
            WriteResults(false);
        }

        private void StopBook(string why)
        {
            var book = _book;
            SaveProgress();
            WriteResults(Step == null);
            _book = null;
            try { book.OnStop?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[{book.LogTag}] stop: {e.Message}"); }
            book.LogInfo?.Invoke($"[{book.LogTag}] {(why == "complete" ? "complete" : "paused (" + why + ")")}: {_results.Values.Count(r => r.status == "done")} done, "
                + $"{_results.Values.Count(r => r.status == "skipped")} skipped of {book.Total}; results in {ResultsPath(book)}");
        }

        // ── per frame ───────────────────────────────────────────────────────────────────────────────────────────────
        private void Update()
        {
            if (_book == null) return;
            bool blocked = Time.frameCount <= _startFrame + 1 || SafeBool(_book.InputBlocked);
            if (!blocked)
            {
                if (SafeBool(_book.ExitPressed)) { StopBook("the exit key"); return; }
                if (SafeBool(_book.BackPressed)) { Back(); return; }
                if (SafeBool(_book.SkipPressed)) { Finish("skipped"); return; }
            }
            if (_advancePending)
            {
                if (Time.unscaledTime >= _celebrateUntil) { _advancePending = false; Advance(); }
                return;
            }
            var step = Step;
            if (step == null) return;
            if (!_entered) BeginStep();
            if (SafeBool(step.Done)) Finish("done");
        }

        private void BeginStep()
        {
            _entered = true;
            _stepStart = Time.unscaledTime;
            try { Step?.Enter?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[{_book.LogTag}] {Step?.Id} enter: {e.Message}"); }
        }

        private void Finish(string status)
        {
            var c = Chapter; var s = Step;
            if (c == null || s == null || _advancePending) return;
            float secs = Time.unscaledTime - _stepStart;
            _results[Key(c, s)] = (status, secs);
            _book.LogDebug?.Invoke($"[{_book.LogTag}] step {GlobalIndex() + 1} {s.Id}: {status} ({secs:0.0} s)");
            if (status == "done")
            {
                _cheer = Cheers[UnityEngine.Random.Range(0, Cheers.Length)];
                _celebrateUntil = Time.unscaledTime + CelebrateSeconds;
                Play(false);
            }
            else { _cheer = ""; _celebrateUntil = Time.unscaledTime + 0.2f; }
            _advancePending = true;
            WriteResults(false);
        }

        private void Advance()
        {
            _st++;
            if (_st >= Chapter.Steps.Count)
            {
                var done = Chapter;
                int ok = done.Steps.Count(s => _results.TryGetValue(Key(done, s), out var r) && r.status == "done");
                _card = $"Chapter complete!\n<size=22>{done.Title}: {ok} of {done.Steps.Count} done</size>";
                _cardUntil = Time.unscaledTime + ChapterCardSeconds;
                Play(true);
                _ch++; _st = 0;
                if (Chapter == null)
                {
                    _card = $"Tutorial complete!\n<size=22>{_results.Values.Count(r => r.status == "done")} of {_book.Total} steps done. Results saved.</size>";
                    _cardUntil = Time.unscaledTime + 6f;
                    StopBook("complete");
                    return;
                }
            }
            SaveProgress();
            _entered = false;
            BeginStep();
        }

        private void Back()
        {
            _advancePending = false;
            if (_st > 0) _st--;
            else if (_ch > 0) { _ch--; _st = Chapter.Steps.Count - 1; }
            if (Chapter != null && Step != null) _results.Remove(Key(Chapter, Step));
            SaveProgress();
            _entered = false;
            BeginStep();
        }

        private int GlobalIndex()
        {
            if (_book == null) return 0;
            int n = 0;
            for (int i = 0; i < _ch && i < _book.Chapters.Count; i++) n += _book.Chapters[i].Steps.Count;
            return n + _st;
        }

        private static bool SafeBool(Func<bool> f) { try { return f != null && f(); } catch { return false; } }

        // ── progress + results (inside the game folder) ─────────────────────────────────────────────────────────────
        // Per character, per book: every host copy uses the same path, so progress made with Valcast alone carries over to Core's menu.
        private static string CharId() => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID().ToString() : "nochar";
        private static string ProgressPath(TutorialBook b) => Path.Combine(Paths.ConfigPath, "FiresTutorials", CharId(), b.Id + ".progress");
        private static string ResultsPath(TutorialBook b) => Path.Combine(Paths.BepInExRootPath, b.ResultsFile);

        private void SaveProgress()
        {
            if (_book == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("chapter\t").Append(Chapter?.Id ?? "").Append('\n').Append("step\t").Append(Step?.Id ?? "").Append('\n');
                foreach (var kv in _results) sb.Append(kv.Key).Append('\t').Append(kv.Value.status).Append('\t').Append(kv.Value.seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath(_book)));
                File.WriteAllText(ProgressPath(_book), sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning($"[{_book.LogTag}] progress not saved: {e.Message}"); }
        }

        private void LoadProgress(out int ch, out int st)
        {
            ch = 0; st = 0;
            try
            {
                string p = ProgressPath(_book);
                if (!File.Exists(p)) return;
                string chId = "", stId = "";
                foreach (var line in File.ReadAllLines(p))
                {
                    var f = line.Split('\t');
                    if (f.Length == 2 && f[0] == "chapter") chId = f[1];
                    else if (f.Length == 2 && f[0] == "step") stId = f[1];
                    else if (f.Length == 3 && float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                        _results[f[0]] = (f[1], s);
                }
                int c = _book.Chapters.FindIndex(x => x.Id == chId);
                if (c < 0) return;
                int s2 = _book.Chapters[c].Steps.FindIndex(x => x.Id == stId);
                ch = c; st = Math.Max(0, s2);
            }
            catch (Exception e) { Debug.LogWarning($"[{_book.LogTag}] progress not read: {e.Message}"); }
        }

        private void WriteResults(bool complete)
        {
            if (_book == null) return;
            try
            {
                string who = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "(no character)";
                var sb = new StringBuilder();
                sb.Append(_book.Title).Append(" results").Append('\n');
                sb.Append("player: ").Append(who).Append('\n');
                sb.Append("mod version: ").Append(_book.Version).Append('\n');
                sb.Append("written: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
                int done = _results.Values.Count(r => r.status == "done"), skipped = _results.Values.Count(r => r.status == "skipped");
                sb.Append("status: ").Append(complete ? "complete" : "in progress").Append(" - ").Append(done).Append(" done, ").Append(skipped)
                  .Append(" skipped, ").Append(_book.Total - done - skipped).Append(" not reached, of ").Append(_book.Total).Append("\n\n");
                int n = 0;
                foreach (var c in _book.Chapters)
                {
                    sb.Append("== ").Append(c.Title).Append('\n');
                    foreach (var s in c.Steps)
                    {
                        n++;
                        string line = _results.TryGetValue(Key(c, s), out var r) ? $"{r.status} ({r.seconds:0.0} s)" : "not reached";
                        sb.Append(string.Format("  {0,2}. {1,-22} {2}", n, s.Id, line)).Append('\n');
                    }
                }
                File.WriteAllText(ResultsPath(_book), sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning($"[{_book.LogTag}] results not written: {e.Message}"); }
        }

        // ── sound ───────────────────────────────────────────────────────────────────────────────────────────────────
        // Unity's audio types are reached by reflection: their assembly targets netstandard 2.1, which hosts built against
        // netstandard 2.0 can't reference at compile time (CS1705). The tones are made in code, no asset needed.
        private static readonly Type AudioSourceType = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
        private static readonly Type AudioClipType = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule");

        private void Play(bool fanfare)
        {
            try
            {
                if (AudioSourceType == null || AudioClipType == null) return;
                if (_audio == null)
                {
                    _audio = gameObject.AddComponent(AudioSourceType);
                    AudioSourceType.GetProperty("playOnAwake")?.SetValue(_audio, false);
                    AudioSourceType.GetProperty("spatialBlend")?.SetValue(_audio, 0f);
                    AudioSourceType.GetProperty("volume")?.SetValue(_audio, 0.35f);
                }
                if (_chime == null) _chime = Tones("tutorial_chime", new[] { 880f, 1320f }, 0.12f);
                if (_fanfare == null) _fanfare = Tones("tutorial_fanfare", new[] { 660f, 880f, 1100f, 1320f }, 0.13f);
                var clip = fanfare ? _fanfare : _chime;
                if (clip != null) AudioSourceType.GetMethod("PlayOneShot", new[] { AudioClipType })?.Invoke(_audio, new object[] { clip });
            }
            catch { }
        }

        private static UnityEngine.Object Tones(string name, float[] freqs, float each)
        {
            const int rate = 44100;
            int per = (int)(rate * each), n = per * freqs.Length;
            var data = new float[n];
            for (int t = 0; t < freqs.Length; t++)
                for (int i = 0; i < per; i++)
                {
                    float env = Mathf.Min(1f, i / (rate * 0.01f)) * Mathf.Exp(-4f * i / per);
                    data[t * per + i] = 0.5f * env * Mathf.Sin(2f * Mathf.PI * freqs[t] * i / rate);
                }
            var create = AudioClipType.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
            var clip = create?.Invoke(null, new object[] { name, n, 1, rate, false }) as UnityEngine.Object;
            if (clip != null) AudioClipType.GetMethod("SetData", new[] { typeof(float[]), typeof(int) })?.Invoke(clip, new object[] { data, 0 });
            return clip;
        }

        // ── drawing: a compact card bottom-right, the camera stays the star ─────────────────────────────────────────
        private static Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

        private void Styles()
        {
            if (_box != null) return;
            _bg = Tex(new Color(0.06f, 0.07f, 0.09f, 0.82f)); _barBg = Tex(new Color(1f, 1f, 1f, 0.12f));
            _barFg = Tex(new Color(1f, 0.72f, 0.25f, 0.95f)); _keyBg = Tex(new Color(1f, 0.72f, 0.25f, 0.18f));
            _box = new GUIStyle { normal = { background = _bg }, padding = new RectOffset(16, 16, 12, 12) };
            _title = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true, richText = true, normal = { textColor = Color.white } };
            _body = new GUIStyle { fontSize = 16, wordWrap = true, richText = true, normal = { textColor = new Color(0.9f, 0.9f, 0.9f) } };
            _small = new GUIStyle { fontSize = 13, wordWrap = true, richText = true, normal = { textColor = new Color(0.7f, 0.72f, 0.75f) } };
            _key = new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.8f, 0.4f), background = _keyBg }, padding = new RectOffset(8, 8, 6, 6) };
            _cheerStyle = new GUIStyle { fontSize = 56, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };
            _cardStyle = new GUIStyle { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true, normal = { textColor = Color.white, background = _bg }, padding = new RectOffset(24, 24, 18, 18) };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        private void OnGUI()
        {
            bool card = Time.unscaledTime < _cardUntil && !string.IsNullOrEmpty(_card);
            if (_book == null && !card) return;
            if (!ForceVisible) { try { if (Hud.IsUserHidden()) return; } catch { } }
            Styles();
            if (card)
            {
                var r = new Rect(Screen.width / 2f - 320f, Screen.height * 0.18f, 640f, 140f);
                GUI.Label(r, _card, _cardStyle);
            }
            if (_book == null) return;
            if (_advancePending && _cheer.Length > 0)
            {
                float a = Mathf.Clamp01((_celebrateUntil - Time.unscaledTime) / CelebrateSeconds);
                var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, a);
                GUI.Label(new Rect(Screen.width / 2f - 300f, Screen.height * 0.3f - 40f * (1f - a), 600f, 90f), _cheer, _cheerStyle);
                GUI.color = old;
            }
            var ch = Chapter; var st = Step;
            if (ch == null || st == null) return;

            float w = Mathf.Min(440f, Screen.width - 40f);
            float x = Screen.width - w - 24f, y = Screen.height - 330f;
            GUILayout.BeginArea(new Rect(x, y, w, 310f));
            GUILayout.BeginVertical(_box);
            GUILayout.Label($"<b>{_book.Title}</b> · {ch.Title} · step {_st + 1}/{ch.Steps.Count}", _small);
            // progress bar across the whole book
            Rect bar = GUILayoutUtility.GetRect(w - 32f, 6f);
            GUI.DrawTexture(bar, _barBg);
            float frac = (float)GlobalIndex() / Math.Max(1, _book.Total);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * frac, bar.height), _barFg);
            GUILayout.Space(6f);
            GUILayout.Label(st.Title, _title);
            if (!string.IsNullOrEmpty(st.Text)) GUILayout.Label(st.Text, _body);
            string key = null; try { key = st.Key?.Invoke(); } catch { }
            if (!string.IsNullOrEmpty(key)) { GUILayout.Space(4f); GUILayout.Label(key, _key); }
            if (!string.IsNullOrEmpty(st.Watch)) GUILayout.Label("<i>Watch: " + st.Watch + "</i>", _small);
            string hint = null; try { hint = st.Hint?.Invoke(); } catch { }
            if (!string.IsNullOrEmpty(hint)) GUILayout.Label(hint, _small);
            GUILayout.Space(4f);
            string controls = null; try { controls = _book.ControlsLine?.Invoke(); } catch { }
            if (!string.IsNullOrEmpty(controls)) GUILayout.Label(controls, _small);
            if (Cursor.visible)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("◀ Back", _button)) Back();
                if (GUILayout.Button("Skip ▶", _button)) Finish("skipped");
                if (GUILayout.Button("Exit", _button)) StopBook("the Exit button");
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void OnDestroy() { if (_book != null) StopBook("game closing"); }
    }
}
