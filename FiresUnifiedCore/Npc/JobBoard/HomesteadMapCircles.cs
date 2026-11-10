using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.UI.ContextMenu;
using FiresCore.UI.Kit;
using FiresTokens = FiresCore.UI.Shell.Kit.FiresTokens;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.890 ([ghost], Fire 10-27): homestead-map WORK ZONES.
    ///  - A right-drag on the map makes a circle (3-60 m) that stays until "Remove circle": a ring on the marker layer (pans / zooms with the
    ///    map) coloured by its designation, and a label like "Logging · r 18 m · Knut, Gymir". Several can coexist. Saved per world.
    ///  - Right-click ON a circle: "Designate as ▸ Gathering (pickables) / Logging / Mining / Hunting / Taming / Loot pickup",
    ///    "Assign ▸ Anyone / Nearest free / companions", "Remove circle".
    ///  - Fire: "It would just be added to the daily jobs and the rotation of work ... They would still need to come back to the homestead
    ///    and deposit and do other tasks." So a designated circle is a DAILY Job Board job ("Logging zone #3"); when the board starts it, the
    ///    companion gets the zone for ONE work run (CompanionIdleBehavior.SetWorkZone: the chore searches inside the circle), deposits at
    ///    base as usual and goes back to the full rotation. No CommandStay; the stay point is untouched. Hunting = a daily hunting trip on
    ///    the circle. Removing the circle removes its job (and ends a zone run in progress).
    ///  - Every 5 s a designated circle checks its work; with none left the label says "nothing left: <zone>" and the circle stays.
    /// Lines: "[HomeMap] circle #N drawn ...", "... right-click -> popup", "... designated <zone> -> board job ...", "... assign ...",
    /// "... removed ...", "[HomeMap] zone run: <name> works / finished <zone>".
    /// </summary>
    internal static class HomesteadMapCircles
    {
        internal sealed class Circle
        {
            public int Id; public Vector3 Center; public float Radius;
            public string Designation;   // focus: gather / chop / mine / hunt / tame / loot; null = not designated yet
            public int JobId;            // its Job Board job (0 = none)
            public RectTransform Ring; public TextMeshProUGUI Label;
            public float NextScan; public string Status;   // null = work left, else "nothing left: ..." (shown, the circle stays)
        }
        internal static readonly List<Circle> Circles = new List<Circle>();
        private static int s_nextId = 1;
        private const float ScanEvery = 5f;

        internal static readonly string[] Foci = { "gather", "chop", "mine", "hunt", "tame", "loot" };

        internal static Circle Create(Vector3 center, float radius)
        {
            var c = new Circle { Id = s_nextId++, Center = center, Radius = radius, NextScan = Time.unscaledTime + ScanEvery };
            Circles.Add(c);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} drawn at ({center.x:0}, {center.z:0}) r={radius:0} m (right-click it to designate)");
            Save();
            return c;
        }

        // 0.2.943: a true ring, 256 px with a 14 px band (~5 % of the diameter: thick at every zoom), white (tinted per zone); and the shade alpha
        internal const float ShadeAlpha = 0.22f;
        private static Sprite s_ring;
        internal static Sprite RingSprite()
        {
            if (s_ring != null) return s_ring;
            const int size = 256; const float band = 14f; float h = size * 0.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "FiresHomeMapRing" };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - h) * (x + 0.5f - h) + (y + 0.5f - h) * (y + 0.5f - h));
                    float a = Mathf.Clamp01(h - 1f - d) * Mathf.Clamp01(d - (h - 1f - band));   // 1 inside the band, soft 1 px edges
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            s_ring = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return s_ring;
        }

        internal static JobBoard.Job JobOf(Circle c) => c.JobId == 0 ? null : JobBoard.Jobs.FirstOrDefault(j => j.Id == c.JobId);


        private static void EnsureVisual(Circle c)
        {
            var layer = HomesteadMap.MarkerLayer;
            if (layer == null) return;
            if (c.Ring == null)
            {
                var go = new GameObject($"FiresHomeMapCircle_{c.Id}", typeof(RectTransform));
                c.Ring = (RectTransform)go.transform; c.Ring.SetParent(layer, false);
                c.Ring.anchorMin = c.Ring.anchorMax = Vector2.zero; c.Ring.pivot = new Vector2(0.5f, 0.5f);
                // 0.2.943 (Fire 10-27: "the circles we draw should be colored and thicker lines ... a thicker outline and a shade to the circle";
                // shots 606-608: thin, faint rounded squares): a translucent filled disc (the shade) under a true ring with a thick outline,
                // both in the zone's colour
                var simg = go.AddComponent<Image>(); simg.sprite = FiresCore.UI.Shell.Kit.KitSprites.Circle(); simg.raycastTarget = false; simg.color = new Color(1f, 0.84f, 0.3f, ShadeAlpha);
                var outline = new GameObject("Outline", typeof(RectTransform)); var ort = (RectTransform)outline.transform; ort.SetParent(c.Ring, false);
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one; ort.offsetMin = ort.offsetMax = Vector2.zero;
                var img = outline.AddComponent<Image>(); img.sprite = RingSprite(); img.raycastTarget = false;
                img.color = FiresTokens.Gold;
                c.Label = KitUI.Label(c.Ring, "Label", "", KitUI.SmallSize, FiresTokens.Gold, TextAlignmentOptions.Top);
                c.Label.textWrappingMode = TextWrappingModes.NoWrap; c.Label.raycastTarget = false; c.Label.outlineWidth = 0.25f; c.Label.outlineColor = Color.black;
                var lrt = c.Label.rectTransform; lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0f); lrt.pivot = new Vector2(0.5f, 1f);
                lrt.anchoredPosition = new Vector2(0f, -4f); lrt.sizeDelta = new Vector2(420f, 40f);
            }
            if (c.Ring.parent != layer) c.Ring.SetParent(layer, false);
        }

        /// <summary>The circle's centre and radius in the marker layer's GUI space (the same maths as the markers).</summary>
        internal static bool ScreenOf(Circle c, out Vector2 gui, out float rPx)
        {
            gui = Vector2.zero; rPx = 0f;
            try
            {
                gui = HomesteadMap.WorldToGui(c.Center);
                Vector2 edge = HomesteadMap.WorldToGui(c.Center + new Vector3(c.Radius, 0f, 0f));
                rPx = Mathf.Abs(edge.x - gui.x);
                return rPx > 0.5f;
            }
            catch { return false; }
        }

        private static readonly Dictionary<string, int> s_areaCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, bool> s_shownWas = new Dictionary<string, bool>();
        internal static void Tick()
        {
            LoadOnce();
            // 0.2.944 (Fire: "show/hide the rings"): the Ledger's Areas rows (per type) decide which rings draw; counts go on those rows
            s_areaCounts.Clear();
            foreach (var cc in Circles) { string k = "Area." + (cc.Designation ?? "none"); s_areaCounts[k] = (s_areaCounts.TryGetValue(k, out int n0) ? n0 : 0) + 1; }
            foreach (string f in Foci) HomesteadMap.SetCount("Area." + f, s_areaCounts.TryGetValue("Area." + f, out int n1) ? n1 : 0);
            HomesteadMap.SetCount("Area.none", s_areaCounts.TryGetValue("Area.none", out int n2) ? n2 : 0);
            for (int i = Circles.Count - 1; i >= 0; i--)
            {
                var c = Circles[i];
                EnsureVisual(c);
                var job = JobOf(c);
                string areaKey = "Area." + (c.Designation ?? "none");
                bool shown = HomesteadMap.Shows(areaKey);
                if (!s_shownWas.TryGetValue(areaKey, out bool shownBefore) || shownBefore != shown) { s_shownWas[areaKey] = shown; FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] area rings {areaKey.Substring(5)} {(shown ? "shown" : "hidden")} (Ledger)"); }
                if (c.Ring != null && !shown) c.Ring.gameObject.SetActive(false);
                if (c.Ring != null && shown && ScreenOf(c, out Vector2 gui, out float rPx))
                {
                    c.Ring.gameObject.SetActive(true);
                    c.Ring.anchoredPosition = gui; c.Ring.sizeDelta = new Vector2(rPx * 2f, rPx * 2f);
                    // Fire 10-27: the ring's colour + label show the zone's designation, e.g. "Logging · r 18 m · Knut, Gymir"
                    var col = ColourOf(c.Designation);
                    c.Ring.GetComponent<Image>().color = new Color(col.r, col.g, col.b, ShadeAlpha); c.Label.color = col;   // the shade
                    var ol = c.Ring.Find("Outline")?.GetComponent<Image>(); if (ol != null) ol.color = col;   // the thick ring
                    c.Label.text = $"{(c.Designation != null ? ZoneName(c.Designation) : "#" + c.Id)} · r {c.Radius:0} m"
                                   + (job != null ? " · " + JobBoard.AssignedText(job) : c.Designation == null ? " · right-click to designate" : "");
                    if (c.Status != null) c.Label.text += "\n" + c.Status.Split(new[] { " - " }, StringSplitOptions.None)[0];
                }
                if (c.Designation != null && Time.unscaledTime >= c.NextScan)
                {
                    c.NextScan = Time.unscaledTime + ScanEvery;
                    string was = c.Status; c.Status = WorkLeft(c);   // Fire: "The circle should stay until we right click and clear it"
                    if (c.Status != was) FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} {(c.Status == null ? "has work again" : c.Status)} (it stays until removed)");
                }
            }
        }

        internal static string ZoneName(string focus)
        {
            switch (focus) { case "gather": return "Gathering (pickables)"; case "chop": return "Logging"; case "mine": return "Mining"; case "hunt": return "Hunting"; case "tame": return "Taming"; case "loot": return "Loot pickup"; default: return focus; }
        }

        private static Color ColourOf(string focus)
        {
            switch (focus)
            {
                case "gather": return new Color(0.55f, 0.85f, 0.35f); case "chop": return new Color(0.80f, 0.55f, 0.30f);
                case "mine": return new Color(0.70f, 0.75f, 0.85f); case "hunt": return new Color(0.90f, 0.35f, 0.30f);
                case "tame": return new Color(0.85f, 0.50f, 0.85f); case "loot": return new Color(0.40f, 0.80f, 0.90f);
                default: return FiresTokens.Gold;
            }
        }

        private static JobBoard.Kind KindOf(string focus)
        {
            switch (focus)
            {
                case "chop": return JobBoard.Kind.Wood; case "hunt": return JobBoard.Kind.HuntTrip; case "tame": return JobBoard.Kind.Tame;
                case "loot": return JobBoard.Kind.Loot; default: return JobBoard.Kind.MineHome;   // mine + gather: ResourceGathering
            }
        }

        // ---- hit + menu -----------------------------------------------------------------------------------------------------
        /// <summary>The circle under a screen point (the ring's inside, + 6 px), or null.</summary>
        internal static Circle HitAt(Vector2 screen)
        {
            var layer = HomesteadMap.MarkerLayer;
            if (layer == null) return null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, screen, null, out Vector2 local)) return null;
            Vector2 p = local - layer.rect.min;   // the markers' anchoring is the layer's bottom-left
            Circle best = null; float bd = float.MaxValue;
            foreach (var c in Circles)
            {
                if (!ScreenOf(c, out Vector2 gui, out float rPx)) continue;
                float d = Vector2.Distance(p, gui);
                if (d <= rPx + 6f && d < bd) { bd = d; best = c; }
            }
            return best;
        }

        internal static bool TryOpenAt(Vector2 screen)
        {
            var c = HitAt(screen);
            if (c == null) return false;
            OpenMenu(c, screen);
            return true;
        }

        internal static void OpenMenu(Circle c, Vector2 screen)
        {
            // Fire 10-27: "Designate as ▸ <zone>", "Assign ▸ Anyone / Nearest free / companions", "Remove circle"
            var comps = JobBoard.CompanionsAtBase();
            var items = new List<ContextMenuItem>();
            foreach (var f in Foci) { string ff = f; items.Add(ContextMenuItem.Row($"Designate as: {ZoneName(ff)}", () => Designate(c, ff))); }
            if (c.Designation != null)
            {
                items.Add(ContextMenuItem.Row("Assign: Anyone", () => Assign(c, null, "Anyone")));
                items.Add(ContextMenuItem.Row("Assign: Nearest free", () => Assign(c, NearestFree(comps, c.Center), "Nearest free")));
                foreach (var comp in comps.Take(8)) { var cc = comp; items.Add(ContextMenuItem.Row($"Assign: {cc.GetDisplayName()}", () => Assign(c, cc, cc.GetDisplayName()))); }
            }
            items.Add(ContextMenuItem.Row("Remove circle", () => Remove(c, "removed")));
            var t = new HomesteadMapActions.Target { Type = "Area", Name = $"Circle #{c.Id}", Pos = c.Center };
            var job = JobOf(c);
            string zone = c.Designation != null ? ZoneName(c.Designation) : "not designated yet";
            string sub = job != null ? $"{JobBoard.Label(job)} · {JobBoard.Schedule(job)} · {JobBoard.AssignedText(job)}" : $"Centre ({c.Center.x:0}, {c.Center.z:0})";
            int rows = HomesteadMapActions.ShowPopupFor($"{zone} #{c.Id} ({c.Radius:0} m)", sub, items, t, screen);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} right-click -> popup with {rows} row(s) ({zone})");
        }

        private static CompanionController NearestFree(List<CompanionController> comps, Vector3 c)
            => comps.Where(x => x != null && !(x.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior is TripBehavior))
                    .OrderBy(x => Vector3.Distance(x.transform.position, c)).FirstOrDefault();

        // ---- designate / assign / remove ---------------------------------------------------------------------------------
        private static int MinuteNow() => EnvMan.instance != null ? Mathf.FloorToInt(EnvMan.instance.GetDayFraction() * 24f * 60f) % (24 * 60) : 0;

        /// <summary>Sets the zone's work type: its daily board job is (re)made for that kind; assignments carry over.</summary>
        internal static void Designate(Circle c, string focus)
        {
            string was = c.Designation;
            var old = JobOf(c);
            var keep = old != null ? old.Assigned.ToList() : new List<string>();
            if (old != null) JobBoard.Remove(old);
            // 0.2.902 (round #25: "took the server's last-run days: #12 'Logging zone #1' day 21" right after Designate): JobBoard.Add saved and
            // pushed the job armed at its default 07:00 = "ran today", and the server's answer to THAT push arrived after the reset below.
            // The zone's fields and minute are now set before Add's first save, so no push ever says "ran today".
            var j = JobBoard.Add(KindOf(focus), JobBoard.Repeat.Daily, nj =>
            {
                nj.Zone = $"{focus}:{c.Id}:{DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 100000000}";   // 0.2.898: + a creation stamp, so a re-made zone is never "the same job" as a removed one
                nj.Center = c.Center; nj.Radius = c.Radius; nj.Count = 1;
                // 0.2.893 (round #18: never due - fires_test_all pins the clock at 12:00 and the job sat at 12:01): due NOW (ArmDue only moves
                // a daily job to tomorrow when now > Minute), then daily at this time like the other daily jobs
                nj.Minute = MinuteNow();
                nj.Assigned.AddRange(keep);
            });
            // 0.2.897 (round #20: Mining zone #2 never due): JobBoard.Add armed the job at the default 07:00, and with now later it marked it
            // "ran today" (LastRunDay); ArmDue never clears that, so the zone's first run must reset it
            j.LastRunDay = -1;
            JobBoard.ArmDue(j);
            JobBoard.Saved(j);
            c.Designation = focus; c.JobId = j.Id; c.Status = null; c.NextScan = Time.unscaledTime + ScanEvery;
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} designated {ZoneName(focus)} (was {(was != null ? ZoneName(was) : "none")}) -> board job #{j.Id} '{JobBoard.Label(j)}' {JobBoard.Schedule(j)}, {JobBoard.AssignedText(j)}");
            Save();
        }

        internal static void Assign(Circle c, CompanionController comp, string how)
        {
            var j = JobOf(c);
            if (j == null) { FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} assign {how}: refused (not designated)"); return; }
            if (how == "Nearest free" && comp == null) { FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} assign Nearest free: refused (no free companion at base)"); Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No free companion at base"); return; }
            JobBoard.Assign(j, comp, add: comp != null);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} assign {how} -> {JobBoard.Label(j)}: {JobBoard.AssignedText(j)}");
        }

        /// <summary>Null while there is work of the zone's kind inside the circle, else why there's none.</summary>
        private static string WorkLeft(Circle c)
        {
            int trees = 0, ore = 0, pick = 0, loot = 0, game = 0, tameable = 0;
            foreach (var col in Physics.OverlapSphere(c.Center, c.Radius))
            {
                if (col == null) continue;
                if (col.GetComponentInParent<TreeBase>() != null || col.GetComponentInParent<TreeLog>() != null) trees++;
                else if (col.GetComponentInParent<MineRock5>() != null || col.GetComponentInParent<MineRock>() != null) ore++;
                else if (col.GetComponentInParent<Pickable>() is Pickable p && p.CanBePicked()) pick++;
                else if (col.GetComponentInParent<ItemDrop>() != null) loot++;
            }
            foreach (var ch in Character.GetAllCharacters())
            {
                if (ch == null || ch.IsDead() || ch.IsPlayer() || ch.IsTamed() || ch.GetComponent<CompanionController>() != null) continue;
                Vector3 d = ch.transform.position - c.Center; d.y = 0f;
                if (d.magnitude > c.Radius) continue;
                if (ch.m_faction == Character.Faction.AnimalsVeg) game++;
                if (ch.GetComponent<Tameable>() != null) tameable++;
            }
            int n;
            switch (c.Designation)
            {
                case "chop": n = trees; break; case "mine": n = ore; break; case "gather": n = pick; break;
                case "loot": n = loot; break; case "hunt": n = game; break; case "tame": n = tameable; break; default: n = 1; break;
            }
            return n > 0 ? null : $"nothing left: {ZoneName(c.Designation)} - (trees {trees}, ore {ore}, pickables {pick}, loot {loot}, game {game}, tameable {tameable})";
        }

        internal static void Remove(Circle c, string why)
        {
            var j = JobOf(c);
            int stopped = 0;
            if (j != null)
            {
                string label = JobBoard.Label(j);
                foreach (var comp in CompanionController.AllCompanions)
                {
                    var idle = comp != null ? comp.GetComponent<CompanionIdleBehavior>() : null;
                    if (idle == null || !idle.HasWorkZone || idle.WorkZoneName != label) continue;
                    idle.StopActiveSubBehavior("zone removed: " + label); idle.ClearWorkZone("zone removed"); stopped++;
                }
                JobBoard.Remove(j);
            }
            if (c.Ring != null) UnityEngine.Object.Destroy(c.Ring.gameObject);
            Circles.Remove(c);
            FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circle #{c.Id} {why}: its board job {(j != null ? "removed" : "none")}, {stopped} zone run(s) in progress ended");
            Save();
        }

        // ---- persistence (Fire: circles stay until removed - across map close / open and relogs) -------------------------------
        // BepInEx\cache\FiresUnifiedCore\homestead_circles\<world>.json. The board job itself is saved by the Job Board.
        private sealed class SavedCircle { public int Id; public string Designation; public int JobId; public float X, Y, Z, R; }
        private static string s_loadedWorld;

        private static string FileFor(string world)
        {
            foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) world = world.Replace(ch, '_');
            return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "homestead_circles", world + ".json");
        }

        internal static void Save()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return;
            try
            {
                var all = Circles.Select(c => new SavedCircle { Id = c.Id, Designation = c.Designation, JobId = c.JobId, X = c.Center.x, Y = c.Center.y, Z = c.Center.z, R = c.Radius }).ToList();
                string path = FileFor(world);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(all));
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] circles save failed: " + e.Message); }
        }

        private static void LoadOnce()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world) || world == s_loadedWorld) return;
            s_loadedWorld = world;
            foreach (var c in Circles) if (c.Ring != null) UnityEngine.Object.Destroy(c.Ring.gameObject);
            Circles.Clear();
            try
            {
                string path = FileFor(world);
                if (!System.IO.File.Exists(path)) return;
                var all = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SavedCircle>>(System.IO.File.ReadAllText(path)) ?? new List<SavedCircle>();
                foreach (var sc in all)
                {
                    Circles.Add(new Circle { Id = sc.Id, Designation = sc.Designation, JobId = sc.JobId, Center = new Vector3(sc.X, sc.Y, sc.Z), Radius = sc.R, NextScan = Time.unscaledTime + ScanEvery });
                    s_nextId = Mathf.Max(s_nextId, sc.Id + 1);
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] circles loaded for '{world}': {Circles.Count} circle(s) ({string.Join(", ", Circles.Select(c => $"#{c.Id} {(c.Designation != null ? ZoneName(c.Designation) : "undesignated")}"))})");
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[HomeMap] circles load failed: " + e.Message); }
        }
    }
}
