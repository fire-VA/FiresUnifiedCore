using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Preview cameras must update when a model is swapped (and swapped back) or equipment changes, or they show the old model.
    /// THE one "character visual changed" signal for the
    /// family. Raised, at most once per character per frame and reason, when:
    ///   "model"     - Core BodyModelSwap applied or cleared a model (companions / NPCs), or FCC swapped a player's model (FccApi.ModelChanged);
    ///   "equipment" - a VisEquipment's shown gear, hair, beard or base model index changed (any path: equip, unequip, barber, swap back);
    ///   "dye"       - FiresDyes recoloured a renderer of the character (FiresDyesApi.Changed).
    /// Previews subscribe to <see cref="Changed"/> and rebuild / re-target their clone for that character; no per-preview polling.
    /// Add-only contract.
    /// </summary>
    [HarmonyPatch]
    public static class CharacterVisuals
    {
        /// <summary>(character root GameObject, reason "model" / "equipment" / "dye"). Main thread. Handlers must not throw (they are caught).</summary>
        public static event Action<GameObject, string> Changed;

        /// <summary>For a mod that changes a character's look in a way Core can't see (e.g. a custom attach): raise the signal yourself.</summary>
        public static void Raise(GameObject character, string reason)
        {
            if (character == null) return;
            int frame = Time.frameCount;
            var key = (character.GetInstanceID(), reason ?? "");
            if (s_raised.TryGetValue(key, out int at) && at == frame) return;
            s_raised[key] = frame;
            if (s_raised.Count > 4096) s_raised.Clear();
            var h = Changed;
            if (h == null) return;
            foreach (Action<GameObject, string> d in h.GetInvocationList())
            {
                try { d(character, reason); }
                catch (Exception ex) { Debug.LogWarning($"[CharacterVisuals] {reason} handler {d.Method.DeclaringType?.Name}.{d.Method.Name} threw: {ex.Message}"); }
            }
            if (s_logLeft > 0 && !Application.isBatchMode) { s_logLeft--; Debug.Log($"[CharacterVisuals] {character.name}: {reason} changed ({h.GetInvocationList().Length} listener(s))"); }
        }
        private static readonly Dictionary<(int, string), int> s_raised = new Dictionary<(int, string), int>();
        private static int s_logLeft = 40;   // a few proof lines per session, then quiet

        // ---- sources -------------------------------------------------------------------------------------------------------------
        private static bool s_hooked;
        private static void HookSources()
        {
            if (s_hooked) return;
            s_hooked = true;
            try { FiresCore.Npc.BodyModelSwap.Applied += (body, model) => Raise(Root(body), "model"); } catch (Exception ex) { Debug.LogWarning("[CharacterVisuals] BodyModelSwap hook: " + ex.Message); }
            Subscribe("FiresCharacterCreator.FccApi, FiresCharacterCreator", "ModelChanged", nameof(OnFccModel));
            Subscribe("FiresDyes.Api.FiresDyesApi, FiresDyes", "Changed", nameof(OnDye));
        }

        private static void Subscribe(string typeName, string evName, string handler)
        {
            try
            {
                var t = Type.GetType(typeName, false);
                var ev = t?.GetEvent(evName, BindingFlags.Public | BindingFlags.Static);
                if (ev == null) return;
                var m = typeof(CharacterVisuals).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static);
                ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, m));
                Debug.Log($"[CharacterVisuals] listening to {t.Name}.{evName}");
            }
            catch (Exception ex) { Debug.LogWarning($"[CharacterVisuals] {typeName}.{evName}: {ex.Message}"); }
        }
        private static void OnFccModel(Player p, string model) { if (p != null) Raise(p.gameObject, "model"); }
        private static void OnDye(Renderer r, int slot) { if (r != null) { var c = r.GetComponentInParent<Character>(); if (c != null) Raise(c.gameObject, "dye"); } }

        private static GameObject Root(GameObject body)
        {
            if (body == null) return null;
            var c = body.GetComponentInParent<Character>();
            return c != null ? c.gameObject : body;
        }

        // VisEquipment: the shown items / hair / beard / model index, hashed after each update; a change raises "equipment"
        private static readonly Dictionary<int, int> s_hash = new Dictionary<int, int>();
        private static readonly string[] HashFields = { "m_currentRightItemHash", "m_currentLeftItemHash", "m_currentChestItemHash", "m_currentLegItemHash",
            "m_currentHelmetItemHash", "m_currentShoulderItemHash", "m_currentUtilityItemHash", "m_currentBeardItemHash", "m_currentHairItemHash",
            "m_currentRightBackItemHash", "m_currentLeftBackItemHash", "m_currentModelIndex" };
        private static AccessTools.FieldRef<VisEquipment, int>[] s_refs;

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        [HarmonyPostfix]
        private static void AfterEquipmentVisuals(VisEquipment __instance)
        {
            if (!s_hooked) HookSources();
            if (__instance == null || Changed == null) return;
            if (s_refs == null)
            {
                var list = new List<AccessTools.FieldRef<VisEquipment, int>>();
                foreach (var n in HashFields)
                {
                    var fi = AccessTools.Field(typeof(VisEquipment), n);
                    if (fi != null && fi.FieldType == typeof(int)) list.Add(AccessTools.FieldRefAccess<VisEquipment, int>(fi));
                }
                s_refs = list.ToArray();
            }
            int h = 17;
            for (int i = 0; i < s_refs.Length; i++) h = h * 31 + s_refs[i](__instance);
            int id = __instance.GetInstanceID();
            if (s_hash.TryGetValue(id, out int was))
            {
                if (was == h) return;
                s_hash[id] = h;
                Raise(Root(__instance.gameObject), "equipment");
            }
            else
            {
                if (s_hash.Count > 8192) s_hash.Clear();
                s_hash[id] = h;   // first sight: the baseline, not a change
            }
        }
    }
}
