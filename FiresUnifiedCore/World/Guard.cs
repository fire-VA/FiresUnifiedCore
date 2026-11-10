using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.1026 (Fire 10-30 via [lead]: the Studio's walk mode is a "God client" that takes damage but never dies; [visual] found
    /// FDT's test Guard already does exactly that, so it MOVES here - Core is the one implementation; FDT's tests and FiresGodComplex's
    /// god.set both call this). Behaviour kept from FDT's AutoGodModeOnSpawn Guard (Fire's 10-05 design, "Real health + refill"):
    /// real hits at normal max health (stagger, effects, numbers all happen), nobody dies, god / ghost kept off so nothing is flagged
    /// cheated. A hit that could kill refills to full FIRST and then lands in full; anyone under 20% is refilled; a hit past the
    /// refill is held at 1 health. Covers the local player and the Core companions it owns here (ZDO "companion_owner" = this player,
    /// owned on this peer). Per-companion WOULD-DIE ledger ([seasons]' boss drill): the health the guard gave is a debt; a hit that
    /// would have killed without it is counted and the debt cleared.
    /// Who may turn it on (test launch / cfg / admin pairing) stays with the caller; Core only does what <see cref="On"/> says.
    /// </summary>
    [HarmonyPatch]
    public static class Guard
    {
        public const float FloorShare = 0.2f;
        // Vanilla still scales a hit after the prefix (difficulty / damage-taken rates), so "could kill" has a margin.
        private const float LethalMargin = 1.5f;
        private const float CompanionScanSeconds = 0.25f;
        private const float HeldAtHealth = 1f;
        private const string CompanionOwnerKey = "companion_owner";
        private const string Tag = "[Guard] ";

        /// <summary>The switch. Callers: FDT's test protection (Guard mode), FiresGodComplex's god.set.</summary>
        public static bool On;
        /// <summary>false = the companions stay mortal (FDT's 'bot group guard off'); the local player keeps the guard.</summary>
        public static bool GuardCompanions = true;
        public static int Refills { get; private set; }
        public static readonly Dictionary<Character, int> CompanionWouldDie = new Dictionary<Character, int>();
        private static readonly Dictionary<Character, float> s_companionDebt = new Dictionary<Character, float>();
        private static float s_nextCompanionScan;
        private static bool s_infoLogged;

        public static void ResetCompanionWouldDie() { s_companionDebt.Clear(); CompanionWouldDie.Clear(); }

        /// <summary>0.2.1030 ([ghost], the server-side Studio god clone: a dedi has no local player): a character whose ZDO carries this
        /// bool is guarded on the peer that OWNS it, with or without <see cref="On"/> - the key is the switch for that one body.</summary>
        public const string ZdoKey = "fires_guard";
        private static readonly int s_zdoKey = ZdoKey.GetStableHashCode();
        private static readonly List<Character> s_keyed = new List<Character>();
        private static float s_nextKeyedScan;

        // 1074 ([generator] event-driven/cache): ZNetView is fixed on a Character, so it is looked up once per body (weak table, dies
        // with the body). A missing or destroyed cached view is looked up again, so a late-added view is still found as before.
        // (Guard reads no VisEquipment; CompanionController is not cached.)
        private static readonly ConditionalWeakTable<Character, ZNetView> s_views = new ConditionalWeakTable<Character, ZNetView>();
        private static ZNetView ViewOf(Character character)
        {
            if (s_views.TryGetValue(character, out var cached) && cached != null) return cached;
            var view = character.GetComponent<ZNetView>();
            s_views.Remove(character);
            if (view != null) s_views.Add(character, view);
            return view;
        }

        /// <summary>Marks (or clears) one character as guarded through its ZDO; the owner must call it. False = no valid owned ZDO.</summary>
        public static bool Protect(Character character, bool on)
        {
            var view = character != null ? ViewOf(character) : null;
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            view.GetZDO().Set(s_zdoKey, on);
            return true;
        }

        private static bool IsKeyed(Character character)
        {
            if (character == null) return false;
            var view = ViewOf(character);
            return view != null && view.IsValid() && view.IsOwner() && view.GetZDO().GetBool(s_zdoKey);
        }

        public static bool IsGuarded(Character character) =>
            character != null && ((On && (character == Player.m_localPlayer || IsGuardedCompanion(character))) || IsKeyed(character));

        // the keyed bodies (no local player needed - the dedi runs this too): held above the floor a few times a second
        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix]
        private static void ZNet_Update_Postfix()
        {
            if (Time.unscaledTime < s_nextKeyedScan) return;
            s_nextKeyedScan = Time.unscaledTime + CompanionScanSeconds;
            s_keyed.Clear();
            foreach (Character c in Character.GetAllCharacters()) if (IsKeyed(c)) s_keyed.Add(c);
            foreach (Character c in s_keyed) KeepAboveFloor(c);
        }

        // Every local-player frame while on: god and ghost kept off, the player held above the floor, its companions a few times a second
        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update_Postfix(Player __instance)
        {
            if (!On || __instance != Player.m_localPlayer) return;
            if (__instance.InGodMode()) __instance.SetGodMode(false);
            if (__instance.InGhostMode()) __instance.SetGhostMode(false);
            KeepAboveFloor(__instance);
            if (Time.unscaledTime < s_nextCompanionScan) return;
            s_nextCompanionScan = Time.unscaledTime + CompanionScanSeconds;
            foreach (Character character in Character.GetAllCharacters())
                if (IsGuardedCompanion(character)) KeepAboveFloor(character);
        }

        private static void KeepAboveFloor(Character character)
        {
            if (character.IsDead() || character.GetHealth() >= character.GetMaxHealth() * FloorShare) return;
            Refill(character, $"under {FloorShare:P0}");
        }

        /// <summary>Full health now; the local player through vanilla's own heal (what the 'heal' command does), companions by SetHealth.</summary>
        public static void Refill(Character character, string why)
        {
            float before = character.GetHealth(), max = character.GetMaxHealth();
            if (character == Player.m_localPlayer) ((Player)character).Heal(max, true);
            else character.SetHealth(max);
            Refills++;
            if (!(character is Player)) s_companionDebt[character] = (s_companionDebt.TryGetValue(character, out float d) ? d : 0f) + (max - before);
            string line = Tag + $"refilled {Name(character)} from {before:0} to {max:0} (hit {Refills}: {why})";
            if (!s_infoLogged) { s_infoLogged = true; FiresLogger.LogInfo(line + " - first refill this session; later ones are Debug"); }
            else FiresLogger.LogDebug(line);
        }

        // A Core companion whose owner is the local player and whose ZDO this peer owns
        private static bool IsGuardedCompanion(Character character)
        {
            if (character == null || character is Player || Player.m_localPlayer == null || !GuardCompanions) return false;
            var view = ViewOf(character);
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            long owner = view.GetZDO().GetLong(CompanionOwnerKey);
            return owner != 0L && owner == Player.m_localPlayer.GetPlayerID();
        }

        private static string Name(Character character) =>
            character is Player player ? player.GetPlayerName() : Localization.instance.Localize(character.m_name);

        // Before vanilla applies a hit: if it could kill, full health first, so the hit lands in full and is seen
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Character_ApplyDamage_Prefix(Character __instance, HitData hit)
        {
            if (hit == null || !IsGuarded(__instance) || __instance.IsDead()) return;
            float damage = hit.GetTotalDamage();
            if (!(__instance is Player) && s_companionDebt.TryGetValue(__instance, out float debt) && debt > 0f
                && __instance.GetHealth() - damage - debt <= 0f)
            {
                s_companionDebt[__instance] = 0f;
                int n = CompanionWouldDie.TryGetValue(__instance, out int c) ? c + 1 : 1;
                CompanionWouldDie[__instance] = n;
                FiresCore.Logging.FiresLogger.LogDebug(Tag + $"WOULD-DIE {Name(__instance)} #{n} (a {damage:0} hit on {__instance.GetHealth():0} health with {debt:0} of it from the guard)");
            }
            if (__instance.GetHealth() - damage * LethalMargin > 0f) return;
            Refill(__instance, $"a {damage:0} hit could have killed");
        }

        // The last line: a hit bigger than the margin allowed for is held at 1 health instead of a death
        [HarmonyPatch(typeof(Character), nameof(Character.SetHealth)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Character_SetHealth_Prefix(Character __instance, ref float health)
        {
            if (health > 0f || !IsGuarded(__instance) || __instance.IsDead()) return;
            health = HeldAtHealth;
            FiresCore.Logging.FiresLogger.LogDebug(Tag + $"held {Name(__instance)} at {HeldAtHealth:0} (a hit went past the refill)");
        }
    }
}
