using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.UI.Shell.Kit;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// Jobs on the map (Fire 10-24: "select the job board and any active trip and it can open the map and show where they are and what
    /// stage of progress... traveling to the site, hunting/fishing/mining / traveling home"). "Show on map" on a running job opens the
    /// large map on it: a vanilla pin per companion named with its stage ("Vali: Hunting (3 kills so far) · 12 min"), a translucent
    /// disc for a trip's area and a line from the area home, placed with vanilla's own pin maths (Minimap.WorldToMapPoint /
    /// MapPointToLocalGuiPos) under the large map's pin root. Refreshed every 2 s from the companions' ZDO positions while the map is
    /// open; everything is removed when the map closes or the job ends.
    /// </summary>
    internal static class TripMapView
    {
        private static JobBoard.Job s_job;
        private static readonly List<Minimap.PinData> s_pins = new List<Minimap.PinData>();
        private static RectTransform s_disc, s_line;
        private static float s_next;

        internal static void Show(JobBoard.Job j)
        {
            var mm = Minimap.instance;
            if (mm == null || j == null) return;
            Clear();
            s_job = j;
            Vector3 focus = j.IsTrip ? j.Center : (Companions(j).FirstOrDefault()?.transform.position ?? JobBoard.BaseCenter());
            mm.ShowPointOnMap(focus);
            s_next = 0f;
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] map: showing {JobBoard.Label(j)} ({Companions(j).Count} companion(s))");
        }

        private static List<CompanionController> Companions(JobBoard.Job j) =>
            j.Running.Select(id => CompanionController.AllCompanions.FirstOrDefault(c => c != null && c.companionId == id)).Where(c => c != null).ToList();

        private static void Clear()
        {
            var mm = Minimap.instance;
            if (mm != null) foreach (var p in s_pins) mm.RemovePin(p);
            s_pins.Clear();
            if (s_disc != null) UnityEngine.Object.Destroy(s_disc.gameObject);
            if (s_line != null) UnityEngine.Object.Destroy(s_line.gameObject);
            s_disc = null; s_line = null; s_job = null;
        }

        private static Vector3 PosOf(CompanionController c)
        {
            var zdo = c.GetComponent<ZNetView>()?.GetZDO();
            return zdo != null ? zdo.GetPosition() : c.transform.position;   // the synced (server) position
        }

        private static string StageOf(CompanionController c)
        {
            var trip = c.GetComponent<CompanionIdleBehavior>()?.GetSubBehavior<TripBehavior>();
            if (trip != null && trip.OnTrip) return $"{trip.TripStage} · {trip.TripMinutes:0} min{trip.StuckNote}";
            return JobBoard.StateOf(c);
        }

        private static bool Running(JobBoard.Job j) => j != null && JobBoard.Jobs.Contains(j) && j.Running.Count > 0 && JobBoard.LastNext(j).StartsWith("●");

        // ---- vanilla's own map maths ---------------------------------------------------------------------------------------
        private static readonly System.Reflection.MethodInfo s_w2m = AccessTools.Method(typeof(Minimap), "WorldToMapPoint");
        private static readonly System.Reflection.MethodInfo s_m2g = AccessTools.Method(typeof(Minimap), "MapPointToLocalGuiPos", new[] { typeof(float), typeof(float), typeof(RawImage) });
        private static Vector2 Gui(Minimap mm, Vector3 world)
        {
            var args = new object[] { world, 0f, 0f };
            s_w2m.Invoke(mm, args);
            return (Vector2)s_m2g.Invoke(mm, new object[] { (float)args[1], (float)args[2], mm.m_mapImageLarge });
        }

        private static RectTransform Overlay(Minimap mm, string name, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(mm.m_pinRootLarge, false); rt.SetAsFirstSibling();   // under the pins
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>(); img.sprite = sprite; img.color = color; img.raycastTarget = false;
            return rt;
        }

        private static void Refresh()
        {
            var mm = Minimap.instance;
            var j = s_job;
            if (mm == null || j == null) return;
            var comps = Companions(j);
            // pins: one per companion, named with its stage
            while (s_pins.Count < comps.Count) s_pins.Add(mm.AddPin(Vector3.zero, Minimap.PinType.Icon3, "", false, false));
            while (s_pins.Count > comps.Count) { mm.RemovePin(s_pins[s_pins.Count - 1]); s_pins.RemoveAt(s_pins.Count - 1); }
            for (int i = 0; i < comps.Count; i++) { s_pins[i].m_pos = PosOf(comps[i]); s_pins[i].m_name = $"{comps[i].GetDisplayName()}: {StageOf(comps[i])}"; }
            if (!j.IsTrip) return;
            // the trip's area and its way home
            if (s_disc == null) s_disc = Overlay(mm, "FiresTripArea", KitSprites.Circle(), new Color(FiresTokens.Gold.r, FiresTokens.Gold.g, FiresTokens.Gold.b, 0.18f));
            if (s_line == null) s_line = Overlay(mm, "FiresTripHomeLine", KitSprites.White(), new Color(FiresTokens.Gold.r, FiresTokens.Gold.g, FiresTokens.Gold.b, 0.7f));
        }

        private static bool s_ringLogged;

        private static void Place()
        {
            var mm = Minimap.instance;
            var j = s_job;
            if (mm == null || j == null || !j.IsTrip || s_disc == null || s_line == null) return;
            Vector2 c = Gui(mm, j.Center);
            if (FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(j.Center, out Vector3 up))
            {
                // Halo ring ([classes-ui] for [generator], lead 10-24): the trip area measured ON the ring floor - two surface directions
                // through FAT's map maths - drawn as the ellipse they span (a circle on FAT's equal-scale strip). World +x leaves the floor here.
                Vector3 a = Vector3.ProjectOnPlane(Vector3.up, up);
                if (a.sqrMagnitude < 1e-4f) a = Vector3.ProjectOnPlane(Vector3.forward, up);
                a.Normalize();
                Vector3 b = Vector3.Cross(up.normalized, a);
                Vector2 ea = Gui(mm, j.Center + a * j.Radius) - c, eb = Gui(mm, j.Center + b * j.Radius) - c;
                float rx = Mathf.Max(4f, Mathf.Max(Mathf.Abs(ea.x), Mathf.Abs(eb.x))), ry = Mathf.Max(4f, Mathf.Max(Mathf.Abs(ea.y), Mathf.Abs(eb.y)));
                s_disc.anchoredPosition = c; s_disc.sizeDelta = new Vector2(rx * 2f, ry * 2f);
                if (!s_ringLogged) { s_ringLogged = true; FiresCore.Logging.FiresLogger.LogDebug($"[Halo] trip map: area drawn on the ring floor, {rx * 2f:0}x{ry * 2f:0} px for {j.Radius:0} m (up {up})"); }
            }
            else
            {
            Vector2 edge = Gui(mm, j.Center + Vector3.right * j.Radius);
            float r = Mathf.Max(4f, Vector2.Distance(c, edge));
            s_disc.anchoredPosition = c; s_disc.sizeDelta = new Vector2(r * 2f, r * 2f);
            }
            Vector2 home = Gui(mm, JobBoard.BaseCenter());
            Vector2 d = home - c;
            s_line.anchoredPosition = (c + home) * 0.5f; s_line.sizeDelta = new Vector2(d.magnitude, 2f);
            s_line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }

        private sealed class Pump : MonoBehaviour
        {
            private void LateUpdate()
            {
                try
                {
                    if (s_job == null) return;
                    var mm = Minimap.instance;
                    if (mm == null || mm.m_mode != Minimap.MapMode.Large || !Running(s_job))
                    {
                        FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] map: cleared ({(mm == null || mm.m_mode != Minimap.MapMode.Large ? "map closed" : "the job ended")})");
                        Clear();
                        return;
                    }
                    if (Time.unscaledTime >= s_next) { s_next = Time.unscaledTime + 2f; Refresh(); }
                    Place();
                }
                catch (Exception e) { Debug.LogWarning("[JobBoard] map: " + e.Message); Clear(); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_TripMapView
        {
            private static void Postfix()
            {
                s_pins.Clear(); s_job = null;
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresTripMapView"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }
    }
}
