using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresPossession
{
    // A possessed creature's attacks (LMB = main, MMB = special, Alt + the wheel cycles): its own weapons
    // (Humanoid inventory items with a primary attack), the ones its AI uses on ENEMIES first (m_aiTargetType Enemy), then the rest
    // (heals, buffs, summons). MAIN = the first; SPECIAL = the next enemy attack (else the next one at all), changed with the cycle.
    // Aim: vanilla aims a non-player's attack at its AI's target creature, else along its facing; the AI is off while possessed, so
    // the target is set to the creature under the crosshair for the swing (null = straight ahead).
    internal static class PossessionAttacks
    {
        internal const int Main = -1, Special = -2, None = -3;
        private static int s_for;
        private static readonly List<ItemDrop.ItemData> s_list = new List<ItemDrop.ItemData>();
        private static readonly List<PossessAttack> s_public = new List<PossessAttack>();
        private static int s_special;
        private static readonly System.Reflection.FieldInfo s_target = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");

        internal static int SpecialIndex => s_special;
        internal static IReadOnlyList<PossessAttack> Public => s_public;

        internal static void Reset() { s_for = 0; s_list.Clear(); s_public.Clear(); s_special = 0; }

        internal static IReadOnlyList<ItemDrop.ItemData> List(Humanoid h)
        {
            if (h == null || h.GetInventory() == null) { s_list.Clear(); s_public.Clear(); s_for = 0; return s_list; }
            if (s_for == h.GetInstanceID() && s_list.Count > 0) return s_list;
            s_for = h.GetInstanceID();
            s_list.Clear();
            var all = h.GetInventory().GetAllItems().Where(i => i != null && i.IsWeapon() && i.HavePrimaryAttack()).ToList();
            s_list.AddRange(all.Where(IsEnemyAttack));
            s_list.AddRange(all.Where(i => !IsEnemyAttack(i)));
            s_special = s_list.Count > 1 ? 1 : 0;
            if (s_list.Count > 1 && !IsEnemyAttack(s_list[1]) && s_list.Skip(1).Any(IsEnemyAttack)) s_special = s_list.IndexOf(s_list.Skip(1).First(IsEnemyAttack));
            Publish();
            return s_list;
        }

        private static void Publish()
        {
            s_public.Clear();
            for (int i = 0; i < s_list.Count; i++)
            {
                var a = s_list[i].m_shared.m_attack;
                bool ranged = a != null && (a.m_attackType == Attack.AttackType.Projectile || a.m_attackProjectile != null);
                s_public.Add(new PossessAttack { Name = Name(s_list[i]), Kind = i == 0 ? PossessAttackKind.Main : i == s_special ? PossessAttackKind.Special : PossessAttackKind.Other, Ranged = ranged });
            }
        }

        private static bool IsEnemyAttack(ItemDrop.ItemData i) => i.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy;

        internal static ItemDrop.ItemData Pick(Humanoid h, int which)
        {
            var list = List(h);
            if (list.Count == 0) return null;
            int idx = which == Main ? 0 : which == Special ? s_special : which;
            return list[Mathf.Clamp(idx, 0, list.Count - 1)];
        }

        internal static string Name(ItemDrop.ItemData i) => i?.m_dropPrefab != null ? i.m_dropPrefab.name : i?.m_shared?.m_name ?? "none";

        // RMB = a third attack: the attack after the Special in the list (skipping Main), so it moves when the
        // Special is cycled; None when the creature has fewer than three attacks.
        internal static int ThirdIndex(Humanoid h)
        {
            var list = List(h);
            if (list.Count < 3) return None;
            for (int k = 1; k < list.Count; k++)
            {
                int i = (s_special + k) % list.Count;
                if (i != 0 && i != s_special) return i;
            }
            return None;
        }

        // The cycle (the host's modifier + the wheel): which attack MMB fires.
        internal static void Cycle(Humanoid h, int dir, IPossessionHost host)
        {
            var list = List(h);
            if (list.Count == 0) return;
            s_special = ((s_special + dir) % list.Count + list.Count) % list.Count;
            Publish();
            var pick = list[s_special];
            host?.Say($"Special: {Localization.instance?.Localize(pick.m_shared.m_name) ?? Name(pick)} ({s_special + 1}/{list.Count})");
        }

        // The creature swings attack 'which' itself (a Frozen body: the creature's own body): its weapon equipped, aimed, its own
        // StartAttack. An attack whose own data is broken on this creature (a null prefab or projectile: "NullReferenceException at
        // Attack.Start") fails as that attack, named, never as an exception out of the possession.
        internal static bool Fire(Humanoid h, int which, string label, IPossessionHost host)
        {
            if (h == null || h.IsDead()) { host?.Log($"{label}: the creature is gone"); return false; }
            var item = Pick(h, which);
            string who = Utils.GetPrefabName(h.gameObject);
            if (item == null) { host?.Log($"{who} {label}: no attacks (no weapon with a primary attack)"); return false; }
            if (item.m_shared?.m_attack == null) { host?.Log($"{who} {label}: {Name(item)} has no attack"); return false; }
            if (h.GetCurrentWeapon() != item) h.EquipItem(item, false);
            Aim(h);
            bool ok;
            try { ok = h.StartAttack(null, false); }
            catch (System.Exception e)
            {
                host?.Log($"WARN: {who} {label} attack {Name(item)} ({item.m_shared.m_attack.m_attackType}, {item.m_shared.m_attack.m_attackAnimation}) threw in vanilla's Attack.Start: {e.GetType().Name}: {e.Message}");
                return false;
            }
            host?.Log($"{who} {label} attack {Name(item)} ({item.m_shared.m_attack.m_attackType}, {item.m_shared.m_attack.m_attackAnimation}): {(ok ? "started" : "did not start: " + WhyNot(h, item))}");
            return ok;
        }

        // Which of vanilla's StartAttack / Attack.Start gates said no.
        internal static string WhyNot(Humanoid h, ItemDrop.ItemData item)
        {
            var why = new List<string>();
            var cur = h.GetCurrentWeapon();
            if (cur != item) why.Add($"weapon in hand is {Name(cur)} (equip refused)");
            if (h.InAttack()) why.Add("still in an attack");
            if (h.InDodge()) why.Add("dodging");
            if (!h.CanMove()) why.Add("CanMove false (an animation holds it: spawn/alert/sleep)");
            if (h.IsKnockedBack()) why.Add("knocked back");
            if (h.IsStaggering()) why.Add("staggering");
            if (h.InMinorAction()) why.Add("in a minor action");
            var atk = item.m_shared.m_attack;
            if (atk != null)
            {
                if (string.IsNullOrEmpty(atk.m_attackAnimation)) why.Add("no attack animation");
                if (atk.m_requiresReload && !h.IsWeaponLoaded()) why.Add("needs a reload");
                if (!h.HaveStamina(atk.m_attackStamina)) why.Add($"not enough stamina ({atk.m_attackStamina:0})");
                if (atk.m_attackEitr > 0f && !h.HaveEitr(atk.m_attackEitr)) why.Add($"not enough eitr ({atk.m_attackEitr:0})");
            }
            return why.Count > 0 ? string.Join(", ", why) : "no gate found (Attack.Start refused: warded area or a weapon rule)";
        }

        // The AI's target = PossessionEngine.AimOverride (a test's dummy), else the creature under the crosshair, else none.
        internal static void Aim(Character attacker)
        {
            var ai = attacker != null ? attacker.GetComponent<MonsterAI>() : null;
            if (ai == null || s_target == null) return;
            Character t = PossessionEngine.AimOverride != null ? PossessionEngine.AimOverride : UnderCrosshair(attacker);
            if (t == null && PossessionEngine.AutoTarget != null)
            {
                try { t = PossessionEngine.AutoTarget(attacker); } catch { t = null; }
            }
            s_target.SetValue(ai, t);
        }

        private static readonly int s_charMask = LayerMask.GetMask("character", "character_net", "character_noenv", "character_ghost");

        internal static Character UnderCrosshair(Character exclude)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            Character best = null; float bestDist = float.MaxValue;
            foreach (var hit in Physics.SphereCastAll(ray, 0.4f, 80f, s_charMask))
            {
                var c = hit.collider != null ? hit.collider.GetComponentInParent<Character>() : null;
                if (c == null || c == exclude || c == Player.m_localPlayer || c.IsDead()) continue;
                if (hit.distance < bestDist) { bestDist = hit.distance; best = c; }
            }
            return best;
        }
    }

    /// <summary>
    /// A ridden mount attacks on LMB / MMB like a possession: the possession engine's attack path for any caller
    /// that drives a creature itself (FiresMounts' mount), so it is reused, not forked. The same picks (Main = the creature's first
    /// enemy attack, Special = the next, cycled), the same aim (the creature under the crosshair), the same per-attack refusal reasons.
    /// One creature's list is cached at a time (shared with a possession; switching creature rebuilds it).
    /// </summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    static class CreatureAttacks
    {
        /// <summary>The creature's attacks in pick order; Kind says which is Main / Special / Other.</summary>
        public static IReadOnlyList<PossessAttack> List(Humanoid creature)
        {
            PossessionAttacks.List(creature);
            return PossessionAttacks.Public;
        }

        /// <summary>
        /// The creature swings Main (LMB) or Special (MMB; Other counts as Special) itself: its weapon equipped, aimed at the creature
        /// under the crosshair, its own StartAttack. False = it didn't start; the reason goes to host.Log (null = silent).
        /// </summary>
        public static bool Fire(Humanoid creature, PossessAttackKind kind, IPossessionHost host = null)
        {
            bool main = kind == PossessAttackKind.Main;
            return PossessionAttacks.Fire(creature, main ? PossessionAttacks.Main : PossessionAttacks.Special, main ? "main" : "special", host);
        }

        /// <summary>
        /// RMB is a third attack: fires the attack at 'index' in List(creature) (clamped), the same path as
        /// Main / Special. False = it didn't start; the reason goes to host.Log.
        /// </summary>
        public static bool Fire(Humanoid creature, int index, IPossessionHost host = null) =>
            PossessionAttacks.Fire(creature, Mathf.Max(0, index), "attack " + Mathf.Max(0, index), host);

        /// <summary>The third attack's index in List(creature) (the one after the Special), or -1 when it has fewer than three.</summary>
        public static int ThirdIndex(Humanoid creature)
        {
            int i = PossessionAttacks.ThirdIndex(creature);
            return i >= 0 ? i : -1;
        }

        /// <summary>RMB's third attack (the one after the Special), the same path as Main / Special; false if it has none or it didn't start.</summary>
        public static bool FireThird(Humanoid creature, IPossessionHost host = null)
        {
            int i = PossessionAttacks.ThirdIndex(creature);
            if (i < 0) { host?.Log($"{(creature != null ? Utils.GetPrefabName(creature.gameObject) : "?")} has no third attack"); return false; }
            return PossessionAttacks.Fire(creature, i, "third", host);
        }

        /// <summary>Steps which attack Special fires (+1 / -1) and says the pick through host.Say.</summary>
        public static void CycleSpecial(Humanoid creature, int dir, IPossessionHost host = null) => PossessionAttacks.Cycle(creature, dir, host);
    }
}
