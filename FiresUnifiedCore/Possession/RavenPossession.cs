using System;
using System.Collections.Generic;
using FiresCore.Logging;
using FiresCore.Ravens;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// Admin possession of vanilla's tutorial ravens (Hugin / Munin). Those are local, client-side Raven objects, not
    /// Characters, so the admin tap that finds no creature looks for a Raven, spawns the matching networked
    /// <see cref="RavenPrefabs"/> flyer at the raven's perch, owned by the player, and possesses that through the normal
    /// flyer path. The vanilla raven is hidden (renderers, colliders, Raven behaviour off) on this client only and restored
    /// at its exact perch when the possession ends for any reason (release, death, logout, refusal, world unload).
    /// </summary>
    internal static class RavenPossession
    {
        private sealed class State
        {
            public Raven Raven;
            public Transform Parent;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
            public bool RavenWasEnabled;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Collider> Colliders = new List<Collider>();
            public GameObject Body;
            public PossessHandle Handle;
            // The request waits until the body's ZDO has reached the server (see TryPossess).
            public float RequestAt;
            public PossessOptions Options;
            public Character BodyCharacter;
        }

        private static State s_state;
        private const float RequestDelay = 0.75f;
        private static bool s_subscribed;

        internal static bool Allowed => PossessionRules.AllowRavens == null || PossessionRules.AllowRavens.Value;

        /// <summary>The visible vanilla raven under the crosshair (or nearest to it, within 8 degrees) within range.</summary>
        internal static Raven PickRaven(float range, out string why)
        {
            why = null;
            Player local = Player.m_localPlayer;
            if (GameCamera.instance == null || local == null) { why = "no camera or player"; return null; }
            Transform cam = GameCamera.instance.transform;
            Vector3 eye = local.m_eye != null ? local.m_eye.position : local.transform.position + Vector3.up * 1.6f;
            float behind = Mathf.Max(0f, Vector3.Dot(eye - cam.position, cam.forward));

            var hits = Physics.SphereCastAll(cam.position, 0.6f, cam.forward, behind + range, ~0, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                Raven r = hit.collider != null ? hit.collider.GetComponentInParent<Raven>() : null;
                if (r == null || !Visible(r) || hit.distance < behind - 0.5f) continue;
                if (Vector3.Distance(local.transform.position, r.transform.position) > range) continue;
                why = "raven ray";
                return r;
            }

            Raven best = null;
            float bestAngle = 8f;
            foreach (Raven r in UnityEngine.Object.FindObjectsOfType<Raven>())
            {
                if (r == null || !Visible(r)) continue;
                if (Vector3.Distance(local.transform.position, r.transform.position) > range) continue;
                float a = Vector3.Angle(cam.forward, r.transform.position + Vector3.up * 0.3f - cam.position);
                if (a < bestAngle) { bestAngle = a; best = r; }
            }
            why = best != null ? $"raven {bestAngle:0} deg off the crosshair" : "no raven under the crosshair";
            return best;
        }

        private static bool Visible(Raven r)
        {
            if (!r.isActiveAndEnabled) return false;
            foreach (Renderer rend in r.GetComponentsInChildren<Renderer>(false)) if (rend.enabled) return true;
            return false;
        }

        /// <summary>Admin tap on a vanilla raven. Returns false (with a logged reason) when refused.</summary>
        internal static bool TryPossess(Raven raven, PossessOptions options)
        {
            string why = Refusal(raven);
            if (why != null)
            {
                FiresLogger.LogDebug(PossessionRules.Tag + "raven refused: " + why);
                if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Can't possess: " + why);
                return false;
            }
            bool munin = raven.m_isMunin || raven.gameObject.name.IndexOf("Munin", StringComparison.OrdinalIgnoreCase) >= 0;
            string prefabName = munin ? RavenPrefabs.Munin : RavenPrefabs.Hugin;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                FiresLogger.LogDebug(PossessionRules.Tag + "raven refused: " + prefabName + " is not built");
                return false;
            }
            EnsureSubscribed();

            Player local = Player.m_localPlayer;
            Transform t = raven.transform;
            var s = new State
            {
                Raven = raven,
                Parent = t.parent,
                LocalPos = t.localPosition,
                LocalRot = t.localRotation,
                RavenWasEnabled = raven.enabled,
            };
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;

            GameObject body = UnityEngine.Object.Instantiate(prefab, pos, rot);
            s.Body = body;
            ZNetView nv = body.GetComponent<ZNetView>();
            Character c = body.GetComponent<Character>();
            if (nv == null || !nv.IsValid() || c == null)
            {
                FiresLogger.LogDebug(PossessionRules.Tag + "raven refused: the " + prefabName + " body did not spawn networked");
                if (nv != null && nv.IsValid()) ZNetScene.instance.Destroy(body); else UnityEngine.Object.Destroy(body);
                return false;
            }
            ZDO zdo = nv.GetZDO();
            zdo.Set(FiresCore.Summon.SummonKeys.Owner, local.GetPlayerID());
            zdo.Set(FiresCore.Summon.SummonKeys.Source, PossessionRules.AdminTag);
            zdo.Set(PossessionRules.PossessedKey, local.GetPlayerID());   // never a wild bat, not even before the grant

            Hide(s);
            s_state = s;
            FiresLogger.LogDebug(PossessionRules.Tag + $"raven {(munin ? "Munin" : "Hugin")} possessed at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}); vanilla raven hidden");
            // Possessing Hugin/Munin on a dedicated server could report the creature as gone. The body is spawned here
            // and its ZDO only reaches the server with the next ZDO send, while the request RPC goes at once, so the server
            // found no ZDO. Push the ZDO now and send the request from Tick a moment later (at once when this peer is the server).
            s.Options = options;
            s.BodyCharacter = c;
            bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
            if (!isServer) { try { ZDOMan.instance.ForceSendZDO(zdo.m_uid); } catch (Exception e) { FiresLogger.LogDebug(PossessionRules.Tag + "raven body ZDO push failed: " + e.Message); } }
            s.RequestAt = Time.time + (isServer ? 0f : RequestDelay);
            FiresLogger.LogDebug(PossessionRules.Tag + $"raven body {zdo.m_uid} spawned; possession request in {(isServer ? 0f : RequestDelay):0.00} s (server {isServer})");
            return true;
        }

        private static string Refusal(Raven raven)
        {
            if (raven == null) return "nothing there";
            if (PossessionRules.AdminEnabled == null || !PossessionRules.AdminEnabled.Value) return "admin possession is off";
            if (!Allowed) return "raven possession is off ([Possession] Allow raven possession)";
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return "admins only";
            if (PossessionClient.Busy || s_state != null) return "already possessing";
            if (ZNetScene.instance == null || Player.m_localPlayer == null) return "no world";
            return null;
        }

        private static void Hide(State s)
        {
            foreach (Renderer r in s.Raven.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; s.Renderers.Add(r); }
            foreach (Collider c in s.Raven.GetComponentsInChildren<Collider>(true)) if (c.enabled) { c.enabled = false; s.Colliders.Add(c); }
            s.Raven.enabled = false;
        }

        private static void EnsureSubscribed()
        {
            if (s_subscribed) return;
            s_subscribed = true;
            PossessionApi.Ended += OnEnded;
        }

        private static void OnEnded(PossessHandle h, PossessEndReason reason)
        {
            var s = s_state;
            if (s == null || h == null) return;
            if (h == s.Handle || (s.Body != null && h.Creature != null && h.Creature.gameObject == s.Body)) Finish("possession ended: " + reason);
        }

        /// <summary>Every frame: a safety net so a raven is never left hidden (the handle ended, or the body went away).</summary>
        internal static void Tick()
        {
            var s = s_state;
            if (s == null) return;
            if (s.Body == null) { Finish("the raven body is gone"); return; }
            if (s.Handle == null && s.RequestAt > 0f)
            {
                if (Time.time < s.RequestAt) return;
                s.RequestAt = 0f;
                // A refusal inside Request raises Ended at once, which restores the raven through OnEnded.
                PossessHandle h = PossessionClient.Request(s.BodyCharacter, s.Options);
                if (s_state == s) s.Handle = h;
                return;
            }
            if (s.Handle != null && s.Handle.Ended) Finish("the possession ended");
        }

        /// <summary>Despawns the body and restores the vanilla raven at its perch. Safe to call any number of times.</summary>
        internal static void Finish(string why)
        {
            var s = s_state;
            if (s == null) return;
            s_state = null;
            try
            {
                if (s.Body != null)
                {
                    ZNetView nv = s.Body.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid() && ZNetScene.instance != null)
                    {
                        if (!nv.IsOwner()) nv.ClaimOwnership();
                        ZNetScene.instance.Destroy(s.Body);
                    }
                    else UnityEngine.Object.Destroy(s.Body);
                }
            }
            catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "raven body despawn failed: " + e.Message); }

            if (s.Raven == null) { FiresLogger.LogDebug(PossessionRules.Tag + "raven released (" + why + "); the vanilla raven is gone, nothing to restore"); return; }
            try
            {
                Transform t = s.Raven.transform;
                if (s.Parent != null && t.parent != s.Parent) t.SetParent(s.Parent, false);
                t.localPosition = s.LocalPos;
                t.localRotation = s.LocalRot;
                foreach (Renderer r in s.Renderers) if (r != null) r.enabled = true;
                foreach (Collider c in s.Colliders) if (c != null) c.enabled = true;
                s.Raven.enabled = s.RavenWasEnabled;
                FiresLogger.LogDebug(PossessionRules.Tag + "raven released; vanilla raven restored at its perch (" + why + ")");
            }
            catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "vanilla raven restore failed: " + e.Message); }
        }

        // World unload: restore before the scene's objects go (the body's ZDO is destroyed while ZNetScene still exists).
        [HarmonyPatch(typeof(ZNetScene), "Shutdown")]
        private static class ZNetScene_Shutdown_RavenRestore
        {
            private static void Prefix()
            {
                try
                {
                    var h = PossessionApi.Current;
                    if (s_state != null && h != null && !h.Ended) PossessionClient.EndLocal(h, PossessEndReason.Gone);
                    Finish("world unload");
                }
                catch (Exception e) { FiresLogger.LogWarning(PossessionRules.Tag + "raven unload restore failed: " + e.Message); }
            }
        }
    }
}
