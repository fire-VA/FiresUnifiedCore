using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Companion-progression events an optional consumer (e.g. the Marketplace Compendium /
    /// discovery tracker, a Discord relay) subscribes to. While no consumer is present every
    /// delegate is null and raising is a no-op, so the NPC engine fires events freely with no
    /// hard reference to a tracker and runs standalone. Archetypes are passed as ints so
    /// consumers need not reference the engine's ArchetypeClass enum.
    /// </summary>
    public static class CompanionEventBridge
    {
        public static Action<Player> CompanionRecruited;
        public static Action<Player, int> ArchetypeSeen;
        public static Action<Player, int, int> HybridUnlocked;

        public static void RaiseCompanionRecruited(Player owner)
        {
            try { CompanionRecruited?.Invoke(owner); } catch { }
        }

        public static void RaiseArchetypeSeen(Player owner, int archetype)
        {
            try { ArchetypeSeen?.Invoke(owner, archetype); } catch { }
        }

        public static void RaiseHybridUnlocked(Player owner, int main, int sub)
        {
            try { HybridUnlocked?.Invoke(owner, main, sub); } catch { }
        }
    }
}
