using UnityEngine;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// Finds or creates the ICombatAgent for a body. A companion gets a CompanionAgent; anything else has to
    /// bring its own agent component, as FiresMonsters' MonsterAgent does.
    /// </summary>
    public static class CombatAgents
    {
        public static ICombatAgent Find(GameObject body)
        {
            if (body == null) return null;
            return body.GetComponent<ICombatAgent>();
        }

        public static ICombatAgent Find(Character body) => body == null ? null : Find(body.gameObject);

        /// <summary>
        /// Returns the body's agent, adding a CompanionAgent when the body is a companion and has none.
        /// Never call from a field initializer - AddComponent is an engine call.
        /// </summary>
        public static ICombatAgent Ensure(GameObject body)
        {
            if (body == null) return null;

            var existing = body.GetComponent<ICombatAgent>();
            if (existing != null) return existing;

            if (body.GetComponent<CompanionController>() == null) return null;

            return body.AddComponent<CompanionAgent>();
        }

        public static bool IsCompanion(ICombatAgent agent) => agent is CompanionAgent;
    }
}
