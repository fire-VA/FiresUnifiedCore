using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// The possessor's body for a creature the humanoid
    /// Hidden body can't drive. While it runs the body is hidden (renderers off here; other clients hide it from the same ZDO flag the
    /// Hidden body uses, which also keeps AI and damage off it), its colliders are off, its rigidbody is kinematic, and it sits inside
    /// the creature every frame, so the local player (the network reference point) keeps the world loaded around the creature. At the
    /// end it is set down on the ground or water under the creature's last position, with the fall tracker reset (no fall damage).
    /// </summary>
    internal sealed class BodyCarrier : MonoBehaviour
    {
        private const string ProxyKey = "valcast_proxy";   // FiresPossession.PossessionProxy.ProxyKey: remote clients hide this body
        private static BodyCarrier s_active;

        /// <summary>0.2.817: true while this body is the one a form carries (GravityUp.BodyOverride: no field gravity on it).</summary>
        internal static bool IsCarrying(GameObject body) => s_active != null && body != null && s_active.gameObject == body;
        private static readonly AccessTools.FieldRef<Character, float> s_maxAirAltitude = Field("m_maxAirAltitude");

        private Player m_player;
        private Character m_creature;
        private Rigidbody m_body;
        private bool m_wasKinematic, m_hadGravity;
        private readonly List<Collider> m_colliders = new List<Collider>();
        private Vector3 m_last, m_startPos;
        private Quaternion m_startRot;
        private string m_tag, m_fx;
        private bool m_endAtCreature;

        // A local effect (each client that runs the form sees its own; it is the possessor's own view that matters).
        private static void Puff(string fx, Vector3 at, Quaternion rot)
        {
            if (string.IsNullOrEmpty(fx) || ZNetScene.instance == null) return;
            var prefab = ZNetScene.instance.GetPrefab(fx);
            if (prefab == null) { FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: vanish effect '{fx}' not found"); return; }
            try { UnityEngine.Object.Instantiate(prefab, at, rot); }
            catch (Exception ex) { FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: vanish effect '{fx}' failed: {ex.Message}"); }
        }

        private static AccessTools.FieldRef<Character, float> Field(string name)
        {
            try { return AccessTools.FieldRefAccess<Character, float>(name); }
            catch (Exception ex) { FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: Character.{name} not found ({ex.Message}); fall tracking not reset"); return null; }
        }

        internal static bool Active => s_active != null;

        internal static void Start(Player player, Character creature, string tag, string fx, bool endAtCreature) =>
            Start(player, creature, tag, fx, endAtCreature, 0f, 0f);

        // Rise > 0 = the body floats up first (visible, no collisions, eased like the Necromancer's hover
        // rise), and only at the top does the puff play and the body vanish into the creature.
        internal static void Start(Player player, Character creature, string tag, string fx, bool endAtCreature, float rise, float riseSeconds)
        {
            if (player == null || creature == null) return;
            if (s_active != null) Stop("restart");
            var c = player.gameObject.AddComponent<BodyCarrier>();
            c.m_player = player; c.m_creature = creature; c.m_tag = tag; c.m_last = creature.transform.position; c.m_fx = fx;
            c.m_startPos = player.transform.position; c.m_startRot = player.transform.rotation; c.m_endAtCreature = endAtCreature;
            c.m_body = player.GetComponent<Rigidbody>();
            if (c.m_body != null) { c.m_wasKinematic = c.m_body.isKinematic; c.m_hadGravity = c.m_body.useGravity; FiresCore.Npc.KinematicSync.Set(c.m_body, true, "BodyCarrier pick up"); c.m_body.useGravity = false; }
            foreach (var col in player.GetComponentsInChildren<Collider>(true)) if (col != null && col.enabled) { col.enabled = false; c.m_colliders.Add(col); }
            s_active = c;
            if (rise > 0f)
            {
                c.m_rise = rise; c.m_riseSeconds = riseSeconds > 0f ? riseSeconds : 0.8f; c.m_riseStart = Time.time; c.m_rising = true;
                FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: rising {rise:0.0} m over {c.m_riseSeconds:0.0} s before vanishing into {creature.gameObject.name} ({tag})");
                return;
            }
            c.Vanish();
        }

        private float m_rise, m_riseSeconds, m_riseStart;
        private bool m_rising;

        // The puff, then hidden on every client and carried inside the creature.
        private void Vanish()
        {
            m_rising = false;
            Puff(m_fx, m_player.transform.position, m_player.transform.rotation);
            var view = m_player.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) view.GetZDO().Set(ProxyKey, true);
            if (m_rise > 0f) FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: rose {m_rise:0.0} m in {Time.time - m_riseStart:0.00} s, vanished");
            Carry();
            FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: {(m_creature != null ? m_creature.gameObject.name : "?")} carries the hidden body ({m_tag}; {m_colliders.Count} collider(s) off)");
        }

        private void Rise()
        {
            if (m_player == null) { Stop("player gone"); return; }
            float k = Mathf.Clamp01((Time.time - m_riseStart) / m_riseSeconds);
            Vector3 at = m_startPos + FiresCore.World.GravityUp.At(m_startPos) * (m_rise * Mathf.SmoothStep(0f, 1f, k));   // halo ring: rise along the local up
            m_player.transform.position = at;
            if (m_body != null) { m_body.position = at; m_body.linearVelocity = Vector3.zero; }
            if (k >= 1f) Vanish();
        }

        internal static void Stop(string reason)
        {
            var c = s_active;
            s_active = null;
            if (c == null) return;
            try { c.SetDown(reason); }
            catch (Exception ex) { FiresLogger.LogWarning(PossessionRules.Tag + "carried body: setting down failed: " + ex.Message); }
            Destroy(c);
        }

        private void LateUpdate() { if (s_active == this) { if (m_rising) Rise(); else Carry(); } }
        private void FixedUpdate() { if (s_active == this) { if (m_rising) Rise(); else Carry(); } }

        private void Carry()
        {
            if (m_player == null) { Stop("player gone"); return; }
            if (m_creature != null) m_last = m_creature.transform.position;
            m_player.transform.position = m_last;
            if (m_body != null) { m_body.position = m_last; m_body.linearVelocity = Vector3.zero; }
            foreach (var r in m_player.GetComponentsInChildren<Renderer>(true)) if (r != null) r.forceRenderingOff = true;
        }

        // The player goes back to where the body stood. Near = put straight back; far = vanilla's TeleportTo, which waits
        // for that area to load before it lets go. Either way physics, colliders and visibility come back and nothing counts as a fall.
        private const float NearMetres = 64f;

        private void SetDown(string reason)
        {
            if (m_player == null) return;
            // "death" is the engine's reason for a dead creature (the carried body can't die); a consumed form is destroyed before
            // this runs, so a missing creature alone is not a death.
            bool creatureDied = reason == "death" || (m_creature != null && m_creature.IsDead());
            bool atCreature = m_endAtCreature && !creatureDied;
            Vector3 at = atCreature ? Ground(m_last) : m_startPos;
            float dist = Vector3.Distance(m_player.transform.position, at);
            if (m_body != null) { m_body.linearVelocity = Vector3.zero; FiresCore.Npc.KinematicSync.Set(m_body, m_wasKinematic, "BodyCarrier drop"); m_body.useGravity = m_hadGravity; }
            foreach (var col in m_colliders) if (col != null) col.enabled = true;
            foreach (var r in m_player.GetComponentsInChildren<Renderer>(true)) if (r != null) r.forceRenderingOff = false;
            if (dist <= NearMetres)
            {
                m_player.transform.SetPositionAndRotation(at, m_startRot);
                if (m_body != null) m_body.position = at;
            }
            else m_player.TeleportTo(at, m_startRot, true);
            if (s_maxAirAltitude != null) s_maxAirAltitude(m_player) = at.y;   // back from the creature's height is not a fall
            var view = m_player.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) view.GetZDO().Set(ProxyKey, false);
            Puff(m_fx, at, m_startRot);
            FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: {(atCreature ? "set down under the creature" : "back at its start")} {at.x:0.0},{at.y:0.0},{at.z:0.0} from {dist:0} m ({(dist <= NearMetres ? "placed" : "TeleportTo, area loads first")}; {reason}{(creatureDied ? ", creature died" : "")}; {m_tag})");
        }

        // The highest solid surface under the point (terrain or a piece), or the water surface when that is higher.
        private static Vector3 Ground(Vector3 p)
        {
            int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            if (FiresCore.World.GravityUp.InField(p, out Vector3 up))
            {
                // halo ring: the floor along the local up (no flat heightmap, no sea level on the wall); nothing solid = where the creature was
                Vector3 floor = p;
                if (FiresCore.World.Surface.Point(p, out Vector3 sp, out _)) floor = sp;
                else if (Physics.Raycast(p + up * 2f, -up, out RaycastHit ringHit, 60f, mask, QueryTriggerInteraction.Ignore)) floor = ringHit.point;
                FiresLogger.LogDebug(PossessionRules.Tag + $"carried body: halo ring set-down along up {up:F2} at {floor:F1}");
                return floor + up * 0.1f;
            }
            float y = p.y;
            if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 2000f, mask, QueryTriggerInteraction.Ignore)) y = hit.point.y;
            else if (ZoneSystem.instance != null) y = ZoneSystem.instance.GetGroundHeight(p);
            if (ZoneSystem.instance != null) y = Mathf.Max(y, ZoneSystem.instance.m_waterLevel);
            return new Vector3(p.x, y + 0.1f, p.z);
        }
    }
}
