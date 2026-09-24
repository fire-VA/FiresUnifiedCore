using UnityEngine;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// The one place a telegraph is asked for. Defaults to the ground rings; a different drawer can replace it.
    /// </summary>
    public static class CombatTelegraphs
    {
        private static ICombatTelegraph _drawer = new GroundRingTelegraph();

        public static ICombatTelegraph Drawer => _drawer;

        public static void Register(ICombatTelegraph drawer)
        {
            if (drawer == null)
            {
                Debug.LogError("[CombatTelegraphs] Register was given no drawer.");
                return;
            }
            _drawer = drawer;
        }

        public static void ResetToGroundRings() => _drawer = new GroundRingTelegraph();

        public static bool Show(TelegraphRequest request) => _drawer.Show(request);

        /// <summary>
        /// A damage area committed to the ground. Drawn for everyone it can hurt, PvP opponents included.
        /// </summary>
        public static bool ShowPlacedDamage(Character source, Vector3 center, float radius, float duration)
        {
            return Show(new TelegraphRequest
            {
                Source = source,
                Center = center,
                Direction = source != null ? source.transform.forward : Vector3.forward,
                Shape = TelegraphShape.Circle,
                Intent = TelegraphIntent.Damage,
                Audience = TelegraphAudience.Placed,
                Radius = radius,
                Duration = duration
            });
        }

        /// <summary>A caster's own preview of where a cast will land.</summary>
        public static bool ShowAiming(Character source, Vector3 center, float radius, float duration, TelegraphIntent intent)
        {
            return Show(new TelegraphRequest
            {
                Source = source,
                Center = center,
                Direction = source != null ? source.transform.forward : Vector3.forward,
                Shape = TelegraphShape.Circle,
                Intent = intent,
                Audience = TelegraphAudience.Aiming,
                Radius = radius,
                Duration = duration
            });
        }

        /// <summary>A charge lane. Degrades to a circle covering the lane until the drawer grows a line shape.</summary>
        public static bool ShowPlacedLine(Character source, Vector3 origin, Vector3 direction,
            float length, float width, float duration)
        {
            return Show(new TelegraphRequest
            {
                Source = source,
                Center = origin,
                Direction = direction,
                Shape = TelegraphShape.Line,
                Intent = TelegraphIntent.Damage,
                Audience = TelegraphAudience.Placed,
                Length = length,
                Width = width,
                Duration = duration
            });
        }
    }
}
