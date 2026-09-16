using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Every distance, timing and predicate for the companion leash, shared by the client run-back
    /// (<see cref="CompanionController.CheckFollowTeleport"/>) and the server safety net
    /// (<see cref="CompanionTeleportService"/>) so they never disagree. Within <see cref="LeashDistance"/> the
    /// companion follows normally; past it, it drops everything and runs back; past <see cref="SnapDistance"/>, or
    /// when it can't close the gap, the server teleports it to the owner.
    /// </summary>
    public static class CompanionLeash
    {
        /// <summary>Beyond this the companion drops everything and runs back. Within it, normal AI-driven follow.</summary>
        public const float LeashDistance = 50f;

        /// <summary>Hard ceiling: at/after this the companion snaps (teleports) to the owner. Also the server
        /// heartbeat's reel-in distance — a follower this far out is stranded, not catching up.</summary>
        public const float SnapDistance = 80f;

        /// <summary>Post-arrival (portal / login / respawn) reel-in distance: bring followers this close when they
        /// materialise somewhere new. Tighter than the leash because it's an instant relocation, not a drift.</summary>
        public const float ArrivalReelInDistance = 30f;

        /// <summary>Seconds of committed run-back before we judge "they can't close it" (blocked path / outpaced).</summary>
        public const float RunBackWindow = 6f;

        /// <summary>Metres of net inward progress within <see cref="RunBackWindow"/> that count as "they're making it".</summary>
        public const float ProgressRequired = 3f;

        /// <summary>Minimum seconds between snaps for a single companion (network settle).</summary>
        public const float SnapThrottle = 5f;

        /// <summary>Owner speed (m/s) above which we assume portal-jump / admin-fly and skip snapping — chasing a
        /// teleporting owner just loops.</summary>
        public const float OwnerTooFastToSnap = 15f;

        /// <summary>
        /// Shared snap decision. Given the current owner distance, the distance when the leash first engaged
        /// (<paramref name="startDistance"/>), the closest the companion has gotten since (<paramref name="bestDistance"/>),
        /// and how long it's been leashed (<paramref name="secondsLeashed"/>) — should it snap now? True when it hit
        /// the hard ceiling, OR it's had the full run-back window and still hasn't closed <see cref="ProgressRequired"/>
        /// metres (stuck / outpaced). Otherwise it should keep running back on foot.
        /// </summary>
        public static bool ShouldSnap(float distance, float startDistance, float bestDistance, float secondsLeashed)
        {
            if (distance >= SnapDistance) return true;
            bool windowElapsed = secondsLeashed >= RunBackWindow;
            float closed = startDistance - bestDistance;   // positive = making progress inward
            return windowElapsed && closed < ProgressRequired;
        }

        /// <summary>
        /// Is this owner in a state where snapping / reeling followers to them is safe? Excludes dead, mid-teleport,
        /// in-bed, sleeping — the "don't yank companions onto a loading screen or a corpse" guard. Server-side safe:
        /// works off the owner Player object with no <c>Player.m_localPlayer</c> dependency.
        /// </summary>
        public static bool IsOwnerReelTarget(Player owner)
        {
            if (owner == null) return false;
            try
            {
                if (owner.IsDead() || owner.IsTeleporting()) return false;
                if (owner.GetHealth() <= 0f) return false;
                if (owner.InBed() || owner.IsSleeping()) return false;
            }
            catch { return false; }
            return true;
        }
    }
}
