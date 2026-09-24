using System;
using UnityEngine;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// The one place an ability heal is applied, so every heal has a known healer and can be observed.
    /// Vanilla Character.Heal routes to the healed character's owner and caps at max health, so the
    /// award is computed and reported on that owner. Plan: Docs/PLAN_AbilityHeals.md
    /// </summary>
    public static class AbilityHeals
    {
        internal const string RpcApplyHeal = "RPC_CompanionAbilityHeal";

        /// <summary>
        /// Raised on the peer that owns the healed character, after the heal has landed, with the
        /// amount ACTUALLY healed after max-health capping. Never raised for a heal that changed
        /// nothing, so a pure overheal produces no event. The healer may be null only when a heal is
        /// applied with no attribution.
        /// </summary>
        public static event Action<Character, Character, float> OnHealApplied;

        public static bool VerboseLogging;

        /// <summary>
        /// Heals a character and reports it. Safe to call from any peer: when the caller does not own
        /// the target, the intent is routed so the owner applies it and raises the event once.
        /// </summary>
        public static void Apply(Character healer, Character target, float amount, bool showText = true)
        {
            if (target == null || amount <= 0f) return;

            var targetView = target.GetComponent<ZNetView>();
            if (targetView == null || !targetView.IsValid())
            {
                ApplyLocally(healer, target, amount, showText);
                return;
            }

            if (targetView.IsOwner())
            {
                ApplyLocally(healer, target, amount, showText);
                return;
            }

            var routed = ZRoutedRpc.instance;
            if (routed == null)
            {
                ApplyLocally(healer, target, amount, showText);
                return;
            }

            routed.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcApplyHeal,
                HealerId(healer), targetView.GetZDO().m_uid, amount, showText);
        }

        /// <summary>Reports a heal this class did not apply, so callers into vanilla can still attribute one.</summary>
        public static void Report(Character healer, Character target, float amountHealed)
        {
            if (target == null || amountHealed <= 0f) return;
            Raise(healer, target, amountHealed);
        }

        internal static void RegisterRpc(ZRoutedRpc routed)
        {
            routed.Register<ZDOID, ZDOID, float, bool>(RpcApplyHeal, RPC_HandleHeal);
        }

        private static void RPC_HandleHeal(long sender, ZDOID healerId, ZDOID targetId, float amount, bool showText)
        {
            var target = AbilityRPCManager.FindCharacterByZDOID(targetId);
            if (target == null) return;

            var targetView = target.GetComponent<ZNetView>();
            if (targetView == null || !targetView.IsValid() || !targetView.IsOwner()) return;

            ApplyLocally(AbilityRPCManager.FindCharacterByZDOID(healerId), target, amount, showText);
        }

        private static void ApplyLocally(Character healer, Character target, float amount, bool showText)
        {
            float before = target.GetHealth();
            target.Heal(amount, showText);
            float healed = target.GetHealth() - before;

            if (healed <= 0f) return;
            Raise(healer, target, healed);
        }

        private static void Raise(Character healer, Character target, float healed)
        {
            if (VerboseLogging)
                Debug.Log($"[AbilityHeals] {healer?.m_name ?? "unattributed"} healed {target.m_name} for {healed:0.#}");

            var handlers = OnHealApplied;
            if (handlers == null) return;

            foreach (Action<Character, Character, float> handler in handlers.GetInvocationList())
            {
                try { handler(healer, target, healed); }
                catch (Exception ex)
                {
                    Debug.LogError($"[AbilityHeals] OnHealApplied subscriber {handler.Method?.DeclaringType?.Name}."
                        + $"{handler.Method?.Name} threw: {ex.Message}");
                }
            }
        }

        private static ZDOID HealerId(Character healer)
        {
            var view = healer?.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view.GetZDO().m_uid : ZDOID.None;
        }
    }
}
