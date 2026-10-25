using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.817 (Fire 10-24, halo ring: "some things that need gravity and 'up' are still not re targeted"; [visual]'s plan A1-A3):
    /// gravity for the loose physics bodies of the world inside a GravityUp field, on EVERY peer (client, bot, dedicated server).
    /// Item drops, chopped logs (TreeLog), ragdoll bones and any other networked rigidbody that is not a Character: inside the field
    /// world gravity is switched off for it and -up x |Physics.gravity| is added every physics step (acceleration, mass-independent,
    /// the same strength as vanilla). Characters are not touched (FDT's HaloFrame owns them), nor any body GravityUp.BodyOverride names.
    /// Leaving the field, or the field going away, gives the body its own useGravity back. Outside every field nothing is changed:
    /// no field active = the driver does nothing at all (off the ring, byte-identical behaviour).
    /// ItemDrop.TerrainCheck (vanilla's "below the heightmap: lift it up") is skipped inside a field: the heightmap is the flat world's.
    /// </summary>
    [HarmonyPatch]
    public static class GravityBody
    {
        private static readonly List<Rigidbody> s_bodies = new List<Rigidbody>();
        private static readonly HashSet<int> s_known = new HashSet<int>();
        private static readonly Dictionary<Rigidbody, bool> s_ours = new Dictionary<Rigidbody, bool>();   // bodies we switched: their own useGravity
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static GameObject s_driver;

        /// <summary>Add a rigidbody to field gravity (add-only; Core registers drops, logs, ragdolls and networked bodies itself).</summary>
        public static void Track(Rigidbody rb)
        {
            if (rb == null || !s_known.Add(rb.GetInstanceID())) return;
            if (rb.GetComponentInParent<Character>() != null) { s_known.Remove(rb.GetInstanceID()); return; }   // HaloFrame's
            if (rb.GetComponent("HaloGravityBody") != null) { s_known.Remove(rb.GetInstanceID()); return; }   // 0.2.818: FDT's spike already pulls it: never two gravities
            s_bodies.Add(rb);
            if (s_driver == null)
            {
                s_driver = new GameObject("FiresGravityBody");
                Object.DontDestroyOnLoad(s_driver);
                s_driver.AddComponent<Driver>();
            }
        }

        private sealed class Driver : MonoBehaviour
        {
            private void FixedUpdate()
            {
                if (s_bodies.Count == 0) return;
                bool active = GravityUp.Active;
                if (!active && s_ours.Count == 0) return;   // no field and nothing of ours to give back: off the ring, do nothing
                float g = Physics.gravity.magnitude;
                for (int i = s_bodies.Count - 1; i >= 0; i--)
                {
                    Rigidbody rb = s_bodies[i];
                    if (rb == null) { s_bodies.RemoveAt(i); continue; }
                    if (rb.GetComponent("HaloGravityBody") != null)   // 0.2.818: FDT's spike added its own pull after we registered
                    {
                        if (s_ours.TryGetValue(rb, out bool own)) { rb.useGravity = own; s_ours.Remove(rb); }
                        continue;
                    }
                    bool inField = active && !rb.isKinematic && !GravityUp.BodyOverride(rb.gameObject) && GravityUp.InField(rb.position, out Vector3 up)
                                   && Apply(rb, up, g);
                    if (!inField && s_ours.TryGetValue(rb, out bool had)) { rb.useGravity = had; s_ours.Remove(rb); }
                }
                if (s_ours.Count > 0)   // destroyed bodies leave the map
                {
                    List<Rigidbody> dead = null;
                    foreach (var kv in s_ours) if (kv.Key == null) (dead ?? (dead = new List<Rigidbody>())).Add(kv.Key);
                    if (dead != null) foreach (var d in dead) s_ours.Remove(d);
                }
            }

            private static bool Apply(Rigidbody rb, Vector3 up, float g)
            {
                if (!s_ours.ContainsKey(rb))
                {
                    if (!rb.useGravity) return false;   // a body with no gravity of its own (floating, scripted) stays as it is
                    s_ours[rb] = true;
                    rb.useGravity = false;
                    string kind = rb.GetComponent<ItemDrop>() != null ? "item drop" : rb.GetComponent<TreeLog>() != null ? "tree log"
                                : rb.GetComponentInParent<Ragdoll>() != null ? "ragdoll" : "body";
                    if (s_logged.Count < 40 && s_logged.Add(kind + ":" + Utils.GetPrefabName(rb.gameObject)))
                        Debug.Log($"[GravityBody] {kind} {Utils.GetPrefabName(rb.gameObject)}: field gravity along -up ({up.x:0.00}, {up.y:0.00}, {up.z:0.00}) at ({rb.position.x:0}, {rb.position.y:0}, {rb.position.z:0}); world gravity off");
                }
                rb.AddForce(-up * g, ForceMode.Acceleration);
                return true;
            }
        }

        // ---- registration ----------------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        [HarmonyPostfix]
        private static void ItemDropAwake(ItemDrop __instance) { if (__instance != null) Track(__instance.GetComponent<Rigidbody>()); }

        [HarmonyPatch(typeof(TreeLog), "Awake")]
        [HarmonyPostfix]
        private static void TreeLogAwake(TreeLog __instance) { if (__instance != null) Track(__instance.GetComponent<Rigidbody>()); }

        [HarmonyPatch(typeof(Ragdoll), "Awake")]
        [HarmonyPostfix]
        private static void RagdollAwake(Ragdoll __instance)
        {
            if (__instance == null) return;
            foreach (var rb in __instance.GetComponentsInChildren<Rigidbody>(true)) Track(rb);
        }

        // any other networked loose body (rock pieces, carts, dropped debris): not a Character
        [HarmonyPatch(typeof(ZNetView), "Awake")]
        [HarmonyPostfix]
        private static void ZNetViewAwake(ZNetView __instance)
        {
            if (__instance == null) return;
            var rb = __instance.GetComponent<Rigidbody>();
            if (rb != null) Track(rb);
        }

        // vanilla lifts a drop that is below the heightmap; on a ring that heightmap is the flat world's, so it would fling drops away
        [HarmonyPatch(typeof(ItemDrop), "TerrainCheck")]
        [HarmonyPrefix]
        private static bool ItemDropTerrainCheck(ItemDrop __instance)
        {
            if (__instance == null || !GravityUp.Active || !GravityUp.InField(__instance.transform.position)) return true;
            if (s_logged.Count < 40 && s_logged.Add("terraincheck"))
                Debug.Log("[GravityBody] ItemDrop.TerrainCheck skipped in the field (the heightmap is the flat world's)");
            return false;
        }
    }
}
