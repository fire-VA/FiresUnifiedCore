using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// The cold survival rule for every body (Core 0.2.250; overnight 06:57 Coop1 died "to Freezing (damage) … health before it 1" at
    /// (590, 245) on a walk for LeatherScraps). Vanilla's own rule (Player.UpdateEnvStatusEffects): Freezing while the environment
    /// freezes (EnvMan.IsFreezing: the Mountains, the Deep North) and the body is neither near a fire's heat nor in shelter; Cold
    /// while it is cold (night, rain) away from a fire, or freezing by a fire with no shelter; frost resistance (even slight) or a
    /// warm cozy area cancels both. Freezing eats health. So: warm up at a lit fire in reach, else get back toward home out of the
    /// cold; and a planner shouldn't send an unresisting body into a freezing biome at all (<see cref="FreezesThere"/>).
    /// </summary>
    public static class ColdRisk
    {
        public enum Move { None, WarmUp, LeaveCold, Shelter }

        public struct Advice
        {
            public Move Move;
            /// <summary>Where to go: the fire to stand by (WarmUp), or home (LeaveCold, Shelter).</summary>
            public Vector3 Point;
            public string Why;
        }

        /// <summary>A lit fire this close (m) is walked to, to warm up.</summary>
        public const float FireReach = 25f;

        /// <summary>Frost resistance from the body's gear and effects (slightly resistant or better), which cancels Freezing and Cold.</summary>
        public static bool FrostResistant(Character body)
        {
            if (body == null) return false;
            HitData.DamageModifier frost = body.GetDamageModifiers().GetModifier(HitData.DamageType.Frost);
            return frost == HitData.DamageModifier.SlightlyResistant || frost == HitData.DamageModifier.Resistant || frost == HitData.DamageModifier.VeryResistant;
        }

        /// <summary>
        /// Whether going to <paramref name="p"/> would freeze <paramref name="body"/>: a biome whose weather freezes (Mountain, Deep North)
        /// and no frost resistance. A planner skips such a goal (or fetches resistance first).
        /// </summary>
        public static bool FreezesThere(Character body, Vector3 p)
        {
            if (FrostResistant(body) || WorldGenerator.instance == null) return false;
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(p);
            return (biome & (Heightmap.Biome.Mountain | Heightmap.Biome.DeepNorth)) != 0;
        }

        /// <summary>
        /// What <paramref name="body"/> does about the cold now (<paramref name="home"/>: where it lives): Freezing -> WarmUp at a lit fire
        /// within <see cref="FireReach"/>, else LeaveCold toward home; Cold (and not by a fire) -> WarmUp at a lit fire in reach, else
        /// Shelter toward home; None when neither applies. Why says which status, where and how far.
        /// </summary>
        public static Advice Advise(Character body, Vector3 home)
        {
            var advice = new Advice { Move = Move.None, Why = "" };
            if (body == null || body.GetSEMan() == null) return advice;
            SEMan se = body.GetSEMan();
            bool freezing = se.HaveStatusEffect(SEMan.s_statusEffectFreezing), cold = se.HaveStatusEffect(SEMan.s_statusEffectCold);
            if (!freezing && !cold) { advice.Why = "not cold"; return advice; }
            bool wet = se.HaveStatusEffect(SEMan.s_statusEffectWet);
            Vector3 at = body.transform.position;
            Fireplace fire = NearestLitFire(at, FireReach);
            string state = freezing ? $"freezing (health {body.GetHealth():0}/{body.GetMaxHealth():0}, no frost resistance)" : $"cold{(wet ? " and wet" : "")}";
            if (fire != null)
            {
                advice.Move = Move.WarmUp;
                advice.Point = fire.transform.position;
                advice.Why = $"{state}: warming up at {Utils.GetPrefabName(fire.gameObject)} ({advice.Point.x:0}, {advice.Point.z:0}), {Vector3.Distance(at, advice.Point):0} m";
                return advice;
            }
            advice.Point = home;
            float d = Vector3.Distance(at, home);
            if (freezing)
            {
                advice.Move = Move.LeaveCold;
                advice.Why = $"{state} and no lit fire within {FireReach:0} m: back toward home ({home.x:0}, {home.z:0}), {d:0} m, out of the cold";
            }
            else
            {
                advice.Move = Move.Shelter;
                advice.Why = $"{state} with no lit fire within {FireReach:0} m: home to shelter and a fire ({home.x:0}, {home.z:0}), {d:0} m";
            }
            return advice;
        }

        // The nearest burning fire within reach.
        private static Fireplace NearestLitFire(Vector3 at, float reach)
        {
            Fireplace best = null;
            float bestD = reach * reach;
            foreach (Fireplace f in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (f == null || !f.IsBurning()) continue;
                float d = (f.transform.position - at).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = f; }
            }
            return best;
        }
    }
}
