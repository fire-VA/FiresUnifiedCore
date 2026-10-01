using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// The torch tactic (Core 0.2.224; Fire 20:4x: "the bots should know that greydwarfs and other meadows creatures are afraid of
    /// fire, they can pull out their torch to run them off if they are getting swarmed before they have the food and gear to fight
    /// them all ... picking them off one by one as they get the opportunity"; 0.2.230, Fire: "the torch only fears mobs if it is
    /// equipped, they have to hold the torch in their off hand and fight with a weapon in the main hand"). Vanilla's fire-shy monsters
    /// (BaseAI.m_afraidOfFire / m_avoidFire) keep off any EffectArea of type Fire within 3 m of themselves, and circle instead of
    /// closing on a target that stands inside one (BaseAI.AvoidFire). A lit torch carries one ("FireWarmth", r 2) under its
    /// attach/equiped child, live in either hand (VisEquipment attaches both hands through AttachItem). Vanilla puts a torch in the
    /// OFF hand only beside a one-handed weapon in the main hand with the off hand empty (Humanoid.EquipItem); otherwise the torch
    /// takes the main hand. So the loadout is: one-handed weapon in the main hand, torch in the off hand (<see cref="EquipTorchOffHand"/>),
    /// backing off facing the pack and striking the one foe that closes alone without putting the torch away. The advice checks that a
    /// Fire area really is round the body once the torch is out and flees when it isn't. One brain: the FDT bot asks it from its
    /// survival rule; a companion can later.
    /// </summary>
    public static class FireScare
    {
        public enum Move { None, TorchOut, PickOff, Flee }

        /// <summary>What to do this tick.</summary>
        public struct Advice
        {
            public Move Move;
            /// <summary><see cref="Move.PickOff"/>: the straggler.</summary>
            public Character Target;
            /// <summary>The torch, for the off hand (from the bag or in hand).</summary>
            public ItemDrop.ItemData Torch;
            /// <summary>The one-handed weapon for the main hand beside the torch, or null when none is carried (then the torch takes the main hand and there is no PickOff).</summary>
            public ItemDrop.ItemData Weapon;
            public string Why;
        }

        /// <summary>A foe this close (m) is the straggler to pick off (the held torch keeps fire-shy foes about 5 m out: its 2 m area + AvoidFire's 3 m)…</summary>
        public const float StragglerReach = 6f;
        /// <summary>…when every other foe after the body is at least this far (m).</summary>
        public const float OthersAway = 10f;
        /// <summary>Held this long (s) without a Fire area round the body: the held torch doesn't scare here.</summary>
        public const float VerifySeconds = 1.5f;

        /// <summary>A foe after the body that doesn't fear fire within this (m) turns the torch tactic into a flee (0.2.251: it closes on a torch).</summary>
        public const float FearlessReach = 12f;

        /// <summary>A hit this recent (s) while the torch is held means it isn't keeping them off (avoid-fire Greydwarfs circle the fire at its radius + 4 m and throw rocks).</summary>
        public const float HitWindow = 5f;
        /// <summary>After such a hit the torch tactic stays off this long (s) for the body, so it doesn't flip back between hits.</summary>
        public const float TorchFailedSeconds = 20f;

        private static readonly Dictionary<Humanoid, float> s_heldSince = new Dictionary<Humanoid, float>();
        private static readonly HashSet<Humanoid> s_noFireArea = new HashSet<Humanoid>();
        private static readonly Dictionary<Character, (Character by, float at)> s_lastHit = new Dictionary<Character, (Character, float)>();
        private static readonly Dictionary<Humanoid, (float until, string why)> s_torchFailed = new Dictionary<Humanoid, (float, string)>();

        /// <summary>Records that <paramref name="body"/> was hit by <paramref name="attacker"/> now (Character.ApplyDamage, on the body's owner: DeathRecorder's prefix).</summary>
        public static void NoteHit(Character body, Character attacker)
        {
            if (body == null || attacker == null || attacker == body) return;
            if (s_lastHit.Count > 128) s_lastHit.Clear();
            s_lastHit[body] = (attacker, Time.time);
        }

        /// <summary>Whether <paramref name="body"/> was hit by a foe in the last <paramref name="window"/> seconds; who, how long ago.</summary>
        public static bool HitLately(Character body, float window, out Character by, out float ago)
        {
            by = null;
            ago = float.MaxValue;
            if (body == null || !s_lastHit.TryGetValue(body, out var last)) return false;
            ago = Time.time - last.at;
            by = last.by;
            return ago <= window;
        }

        /// <summary>
        /// Whether <paramref name="foe"/> keeps off fire: vanilla runs BaseAI.AvoidFire for m_afraidOfFire (flees a Fire area within 3 m
        /// for 6 s) OR m_avoidFire (circles it, and while its target stands inside one, circles that at the area's radius + 4 m instead
        /// of closing in). 0.2.225, R90 run 8: the Greydwarf is avoid-fire, and the check read only m_afraidOfFire ("0 of 1 foes fear fire").
        /// </summary>
        public static bool AfraidOfFire(Character foe)
        {
            BaseAI ai = foe != null ? foe.GetBaseAI() : null;
            return ai != null && (ai.m_afraidOfFire || ai.m_avoidFire);
        }

        /// <summary>The torch the body could hold: the one in hand, else the best one in its bag (most durability); null without one.</summary>
        public static ItemDrop.ItemData TorchCarried(Humanoid body)
        {
            if (body == null) return null;
            if (IsTorch(body.GetLeftItem())) return body.GetLeftItem();
            if (IsTorch(body.GetRightItem())) return body.GetRightItem();
            Inventory bag = body.GetInventory();
            return bag?.GetAllItems().Where(IsTorch).OrderByDescending(i => i.m_durability).FirstOrDefault();
        }

        /// <summary>
        /// The one-handed weapon for the main hand beside the torch: the one in the main hand, else the hardest-hitting usable one in
        /// the bag; null without one.
        /// </summary>
        public static ItemDrop.ItemData OneHandedCarried(Humanoid body)
        {
            if (body == null) return null;
            if (IsOneHanded(body.GetRightItem())) return body.GetRightItem();
            Inventory bag = body.GetInventory();
            return bag?.GetAllItems().Where(IsOneHanded).OrderByDescending(i => i.GetDamage().GetTotalDamage()).FirstOrDefault();
        }

        /// <summary>A lit torch is in either hand.</summary>
        public static bool HoldsTorch(Humanoid body) => body != null && (IsTorch(body.GetRightItem()) || IsTorch(body.GetLeftItem()));

        /// <summary>The fighting loadout: a one-handed weapon in the main hand and a lit torch in the off hand.</summary>
        public static bool HoldsTorchOffHand(Humanoid body) => body != null && IsOneHanded(body.GetRightItem()) && IsTorch(body.GetLeftItem());

        /// <summary>
        /// Put on the torch loadout, in the order vanilla's Humanoid.EquipItem needs: a shield (or anything else) off the off hand, the
        /// one-handed weapon into the main hand (a torch already there moves to the off hand), then the torch (beside a one-hander with
        /// the off hand empty it goes to the off hand). Without a one-handed weapon the torch takes the main hand. True when the torch
        /// is held (in the off hand when a one-hander was carried); <paramref name="why"/> says what was done or refused.
        /// </summary>
        public static bool EquipTorchOffHand(Humanoid body, out string why)
        {
            why = "";
            if (body == null) { why = "no body"; return false; }
            ItemDrop.ItemData torch = TorchCarried(body);
            if (torch == null) { why = "no torch carried"; return false; }
            ItemDrop.ItemData weapon = OneHandedCarried(body);
            if (weapon == null)
            {
                if (!body.IsItemEquiped(torch)) body.EquipItem(torch);
                why = "no one-handed weapon: the torch in the main hand";
                return HoldsTorch(body);
            }
            if (body.GetRightItem() == weapon && body.GetLeftItem() == torch) { why = "ready"; return true; }
            ItemDrop.ItemData left = body.GetLeftItem();
            if (left != null && left != torch) body.UnequipItem(left, false);
            if (body.GetRightItem() != weapon) body.EquipItem(weapon);
            if (body.GetLeftItem() != torch) body.EquipItem(torch);
            bool ok = body.GetRightItem() == weapon && body.GetLeftItem() == torch;
            why = ok ? $"torch in the off hand, {ItemName(weapon)} in the main hand"
                : $"equip refused: main hand {ItemName(body.GetRightItem())}, off hand {ItemName(body.GetLeftItem())}";
            return ok;
        }

        /// <summary>The body's feet stand inside a Fire EffectArea (a campfire's, a torch's): vanilla's "target inside fire" test.</summary>
        public static bool InFireArea(Humanoid body) =>
            body != null && EffectArea.IsPointInsideArea(body.transform.position, EffectArea.Type.Fire) != null;

        /// <summary>
        /// A Fire EffectArea is round the body: its feet inside one, or one within 1.5 m of its centre (0.2.227: a held torch's r 2 area
        /// is centred on the flame in the hand, 1.5-2 m above the feet, so the feet alone can miss it; R90 run 9's "torch held 1.5 s
        /// and no Fire area round the body" read only the feet).
        /// </summary>
        public static bool FireAreaRound(Humanoid body) =>
            body != null && (InFireArea(body) || EffectArea.IsPointInsideArea(body.GetCenterPoint(), EffectArea.Type.Fire, 1.5f) != null);

        private static bool IsTorch(ItemDrop.ItemData item) =>
            item?.m_shared != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch && (!item.m_shared.m_useDurability || item.m_durability > 0f);

        private static bool IsOneHanded(ItemDrop.ItemData item) =>
            item?.m_shared != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon && (!item.m_shared.m_useDurability || item.m_durability > 0f);

        /// <summary>
        /// The torch tactic for <paramref name="body"/> given its threat <paramref name="reading"/>: only when it is backing off (retreat
        /// or space), at least half of the foes after it fear fire, and it carries a torch. <see cref="Move.TorchOut"/>: the loadout on
        /// (<see cref="EquipTorchOffHand"/>), back off facing the pack. <see cref="Move.PickOff"/> (only with a one-handed weapon, so the
        /// torch stays in the off hand) when exactly one foe is within <see cref="StragglerReach"/> and the weapon's
        /// <see cref="MeleeReach"/> (0.2.249) and every other is <see cref="OthersAway"/> or further. <see cref="Move.Flee"/> when the held torch turned out to carry no Fire area here, or
        /// when the body was hit within <see cref="HitWindow"/> while holding it (then for <see cref="TorchFailedSeconds"/>).
        /// None when the tactic doesn't apply.
        /// </summary>
        public static Advice Advise(Humanoid body, ThreatLevel.Reading reading)
        {
            var advice = new Advice { Move = Move.None, Why = "" };
            if (body == null) return advice;
            if (reading.Level != ThreatLevel.Level.Retreat && reading.Level != ThreatLevel.Level.Space) { advice.Why = "not backing off"; return advice; }

            Count(body, out int foes, out int afraid, out Character nearest, out float nearestD, out float secondD, out string kind,
                out Character fearless, out float fearlessD);
            if (foes == 0 || afraid * 2 < foes) { advice.Why = foes == 0 ? "no foe after us" : $"{afraid} of {foes} foes fear fire"; return advice; }
            // 0.2.251 ([visual], Coop1 overnight: "flee because threat retreat: one hit can kill (Bjorn ★0: 130 after armour vs hp 39 of
            // 39)", then the torch tactic took over for 3 fire-shy Greylings and the Bjorn walked up and hit 63 on 39 hp): the torch keeps
            // off only the fire-shy. With a foe after us that doesn't fear fire this close, it is no protection at all: flee.
            if (fearless != null && fearlessD <= FearlessReach)
            {
                advice.Move = Move.Flee;
                advice.Why = $"{ThreatLevel.FoeName(fearless)} doesn't fear fire ({fearlessD:0.0} m): the torch keeps off only the {afraid} fire-shy; fleeing";
                return advice;
            }
            advice.Torch = TorchCarried(body);
            if (advice.Torch == null) { advice.Why = "no torch carried"; return advice; }
            advice.Weapon = OneHandedCarried(body);
            string pack = $"{afraid} {Plural(kind, afraid)} {(afraid == 1 ? "is" : "are")} keeping off fire";

            // The prefab read is logged as evidence only; what decides is the live check below (the read infers vanilla's attach rules).
            TorchAreas areas = AreasOf(advice.Torch);
            if (s_noFireArea.Contains(body))
            {
                advice.Move = Move.Flee;
                advice.Why = "the held torch carried no Fire area here; fleeing instead";
                return advice;
            }
            // 0.2.247 ([visual], Coop2 04:26: stoned by three avoid-fire Greydwarfs from 5.8-10.4 m, 46 -> 0 hp, TorchOut all along):
            // vanilla's avoid-fire foes don't close on a body inside fire, they circle it at the area's radius + 4 m and keep throwing.
            // A hit while the torch is held means it isn't keeping them off: flee, and keep the tactic off for a while.
            if (s_torchFailed.TryGetValue(body, out var failed) && Time.time < failed.until)
            {
                advice.Move = Move.Flee;
                advice.Why = failed.why;
                return advice;
            }
            if (HoldsTorch(body) && HitLately(body, HitWindow, out Character hitter, out float hitAgo))
            {
                float from = hitter != null ? Vector3.Distance(body.transform.position, hitter.transform.position) : 0f;
                string why = $"the torch isn't keeping them off: {(hitter != null ? ThreatLevel.FoeName(hitter) : "a foe")} hit us {hitAgo:0.0} s ago " +
                             $"from {from:0.0} m{(AfraidOfFire(hitter) ? " (fire-shy foes circle the fire and throw)" : "")}; fleeing";
                s_torchFailed[body] = (Time.time + TorchFailedSeconds, why);
                Debug.Log($"[FireScare] {Name(body)}: {why}; the torch tactic is off for {TorchFailedSeconds:0} s");
                advice.Move = Move.Flee;
                advice.Why = why;
                return advice;
            }
            if (HoldsTorch(body))
            {
                if (!s_heldSince.TryGetValue(body, out float since)) s_heldSince[body] = since = Time.time;
                if (Time.time - since >= VerifySeconds && !FireAreaRound(body))
                {
                    s_noFireArea.Add(body);
                    Debug.Log($"[FireScare] {Name(body)}: torch held {VerifySeconds:0.0} s ({(HoldsTorchOffHand(body) ? "off hand" : "main hand")}) and no Fire area round the body; " +
                              $"the torch tactic is off for it. Areas on the body: {AreasOnBody(body)}; the {PrefabName(advice.Torch)} prefab: {areas}");
                    advice.Move = Move.Flee;
                    advice.Why = "the held torch carries no Fire area here; fleeing instead";
                    return advice;
                }
            }
            else s_heldSince.Remove(body);

            // 0.2.249 ([visual], Coop2 05:58: "picking off Greydwarf ★0 … (6.0 m)" alternating with TorchOut, stuck at 5.9-6.0 m, then
            // dead): a held torch keeps every fire-shy foe at its area + AvoidFire's distance (avoid-fire foes ring it at radius + 4 m,
            // afraid ones flee it), so a straggler at that ring never comes into a swing. PickOff only once it is within the weapon's
            // reach ("picking them off one by one as they get the opportunity"); until then the torch holds them off (and a hit while
            // holding it flees, above).
            float reach = MeleeReach(advice.Weapon);
            if (advice.Weapon != null && nearest != null && nearestD <= StragglerReach && secondD >= OthersAway && nearestD > reach)
            {
                advice.Move = Move.TorchOut;
                advice.Why = $"torch out in the off hand, {ItemName(advice.Weapon)} in the main hand: {pack}; {ThreatLevel.FoeName(nearest)} keeps {nearestD:0.0} m off the torch, " +
                             $"out of the {ItemName(advice.Weapon)}'s {reach:0.0} m reach (fire-shy foes keep off a held torch); picking it off if it comes in";
                return advice;
            }
            if (advice.Weapon != null && nearest != null && nearestD <= StragglerReach && secondD >= OthersAway)
            {
                advice.Move = Move.PickOff;
                advice.Target = nearest;
                advice.Why = $"picking off {ThreatLevel.FoeName(nearest)} with the {ItemName(advice.Weapon)}, torch kept in the off hand ({nearestD:0.0} m; " +
                             $"the others {(secondD < float.MaxValue ? $"{secondD:0} m" : "gone")} away)";
                return advice;
            }
            advice.Move = Move.TorchOut;
            advice.Why = advice.Weapon != null
                ? $"torch out in the off hand, {ItemName(advice.Weapon)} in the main hand: {pack}; picking off the nearest when it's alone"
                : $"torch out in the main hand (no one-handed weapon carried, so no picking off): {pack}";
            return advice;
        }

        /// <summary>How close (m, body to body) a foe must be for <paramref name="weapon"/>'s swing to land: its attack range plus a body's width.</summary>
        public static float MeleeReach(ItemDrop.ItemData weapon)
        {
            float range = weapon?.m_shared?.m_attack != null ? weapon.m_shared.m_attack.m_attackRange : 1.5f;
            return Mathf.Max(1.5f, range) + 1f;
        }

        private static void Count(Humanoid body, out int foes, out int afraid, out Character nearest, out float nearestD, out float secondD, out string kind,
            out Character fearless, out float fearlessD)
        {
            Vector3 at = body.transform.position;
            foes = afraid = 0;
            nearest = fearless = null;
            nearestD = secondD = fearlessD = float.MaxValue;
            kind = null;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == body || other.IsDead() || !BaseAI.IsEnemy(body, other)) continue;
                float d = Vector3.Distance(at, other.transform.position);
                if (d > ThreatLevel.Radius || !ThreatLevel.IsAfter(other, body, out _)) continue;
                foes++;
                if (AfraidOfFire(other)) { afraid++; kind = kind ?? Utils.GetPrefabName(other.gameObject); }
                else if (d < fearlessD) { fearlessD = d; fearless = other; }
                if (d < nearestD) { secondD = nearestD; nearestD = d; nearest = other; }
                else if (d < secondD) secondD = d;
            }
        }

        // ---- Whether a held torch carries a live Fire area (0.2.227), read off its item prefab once per prefab ----

        private struct TorchAreas
        {
            /// <summary>The prefab was found and read.</summary>
            public bool Known;
            public string Held;
            public bool HeldLive;
            public override string ToString() => !Known ? "not read" : $"held {(Held ?? "none")}";
        }

        private static readonly Dictionary<string, TorchAreas> s_torchAreas = new Dictionary<string, TorchAreas>();

        // A Fire EffectArea is live (vanilla's IsPointInsideArea finds it) when its GameObject is active, its collider enabled and it sits
        // on the character_trigger layer. Held (either hand), VisEquipment.AttachItem instantiates the "attach" child, disables every
        // collider in its ACTIVE children (CleanupInstance), then activates "equiped": an area live in the hand sits under attach/equiped
        // with equiped inactive in the prefab and every node below it active.
        private static TorchAreas AreasOf(ItemDrop.ItemData torch)
        {
            string prefabName = PrefabName(torch);
            if (s_torchAreas.TryGetValue(prefabName, out TorchAreas known)) return known;
            var r = new TorchAreas();
            GameObject prefab = torch?.m_dropPrefab != null ? torch.m_dropPrefab : ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            if (prefab == null) return r;   // not cached: ObjectDB may not be up yet
            r.Known = true;
            int trigger = LayerMask.NameToLayer("character_trigger");
            Transform attach = prefab.transform.Find("attach");
            Transform equiped = attach != null ? attach.Find("equiped") : null;
            foreach (EffectArea area in prefab.GetComponentsInChildren<EffectArea>(true))
            {
                if ((area.m_type & EffectArea.Type.Fire) == 0) continue;
                if (equiped == null || equiped.gameObject.activeSelf || !area.transform.IsChildOf(equiped) || !ActiveUpTo(area.transform, equiped)) continue;
                Collider collider = area.GetComponent<Collider>();
                bool layer = area.gameObject.layer == trigger;
                r.Held = r.Held ?? $"{RelativePath(area.transform, prefab.transform)} r {Radius(collider):0.0}{(collider == null || !collider.enabled ? " (collider off)" : "")}" +
                                   $"{(layer ? "" : $" (layer {LayerMask.LayerToName(area.gameObject.layer)})")}";
                r.HeldLive |= collider != null && collider.enabled && layer;
            }
            s_torchAreas[prefabName] = r;
            Debug.Log($"[FireScare] the {prefabName} prefab's Fire area in the hand: {r.Held ?? "none"} (live: {r.HeldLive})");
            return r;
        }

        // Every node from t up to (not including) stop is activeSelf.
        private static bool ActiveUpTo(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private static float Radius(Collider collider)
        {
            if (collider is SphereCollider sphere) return sphere.radius * Mathf.Max(sphere.transform.lossyScale.x, sphere.transform.lossyScale.z);
            if (collider is CapsuleCollider capsule) return capsule.radius * Mathf.Max(capsule.transform.lossyScale.x, capsule.transform.lossyScale.z);
            return collider != null ? collider.bounds.extents.magnitude : 0f;
        }

        private static string RelativePath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && t != root && i < 8; i++, t = t.parent) parts.Insert(0, t.name);
            return parts.Count > 0 ? string.Join("/", parts) : root.name;
        }

        private static string PrefabName(ItemDrop.ItemData item) => item?.m_dropPrefab != null ? item.m_dropPrefab.name : "Torch";
        private static string ItemName(ItemDrop.ItemData item) => item == null ? "nothing" : item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;

        // The EffectAreas in the body's own hierarchy (a held torch's), any type, for the "no Fire area" evidence line.
        private static string AreasOnBody(Humanoid body)
        {
            var parts = new List<string>();
            foreach (EffectArea area in body.GetComponentsInChildren<EffectArea>(true))
            {
                Collider collider = area.GetComponent<Collider>();
                parts.Add($"{area.name} {area.m_type} r {Radius(collider):0.0}{(area.gameObject.activeInHierarchy ? "" : " (inactive)")}" +
                          $"{(collider != null && collider.enabled ? "" : " (collider off)")} layer {LayerMask.LayerToName(area.gameObject.layer)}");
            }
            return parts.Count > 0 ? string.Join("; ", parts) : "none";
        }

        private static string Plural(string kind, int n) => string.IsNullOrEmpty(kind) ? (n == 1 ? "foe" : "foes") : n == 1 ? kind : kind + "s";
        private static string Name(Character c) => c is Player p ? p.GetPlayerName() : c.m_name;

        // ---- fires_fireareas: the Fire / Heat / Burning areas round the local player (does the held torch carry one?) ----

        private const string CommandName = "fires_fireareas";
        private static bool s_registered;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => Register();
        }

        private static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(CommandName,
                "[FiresUnifiedCore] The effect areas (fire, heat, burning…) within 8 m of you, and whether you stand in a Fire one (the torch tactic).",
                args =>
                {
                    Player me = Player.m_localPlayer;
                    if (me == null) { args.Context?.AddString($"{CommandName}: no local player"); return; }
                    Vector3 at = me.transform.position;
                    int n = 0;
                    foreach (EffectArea area in EffectArea.GetAllAreas())
                    {
                        if (area == null || !area.isActiveAndEnabled) continue;
                        float d = Vector3.Distance(at, area.transform.position);
                        if (d > 8f) continue;
                        n++;
                        Collider collider = area.GetComponent<Collider>();
                        args.Context?.AddString($"{CommandName}: {area.m_type} r {Radius(collider):0.0} at {d:0.0} m, layer {LayerMask.LayerToName(area.gameObject.layer)}" +
                                                $"{(collider != null && collider.enabled ? "" : ", collider off")}: {PathOf(area.transform)}");
                    }
                    args.Context?.AddString($"{CommandName}: {n} area(s); torch in hand: {HoldsTorch(me)} (off hand beside a one-hander: {HoldsTorchOffHand(me)}); " +
                                            $"feet in a Fire area: {InFireArea(me)}; a Fire area round you: {FireAreaRound(me)}; on your body: {AreasOnBody(me)}");
                    ItemDrop.ItemData torch = TorchCarried(me);
                    if (torch != null) args.Context?.AddString($"{CommandName}: the {PrefabName(torch)} prefab: {AreasOf(torch)}");
                });
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
