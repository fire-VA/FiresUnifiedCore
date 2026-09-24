using System.Collections;
using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// The Healer's Sanctuary cast: the ground gives, a column of water draws up into a drop, the drop falls back
    /// into itself and the splash becomes the heal ring. Built entirely from vanilla prefabs - the droplet mesh is
    /// the later art upgrade, and this is the fallback that plays without it.
    /// </summary>
    public class SanctuaryWaterDrop : MonoBehaviour
    {
        private const float CastGlowScale = 0.5f;
        private const float GroundGiveAtSeconds = 0.10f;
        private const float RippleScale = 1.5f;
        private const float RiseStartSeconds = 0.15f;
        private const float RiseEndSeconds = 0.55f;
        private const float HangEndSeconds = 0.70f;
        private const float FallEndSeconds = 0.90f;
        private const float ColumnHeightMeters = 2.5f;
        private const float ColumnStartSpeed = 3.5f;
        private const float ColumnGravityModifier = -0.6f;
        private const float HangWobbleAmplitude = 0.12f;
        private const float HangWobbleCycles = 2f;
        private const float WaterImpactScale = 0.6f;
        private const float NovaRingNativeRadiusMeters = 4f;
        private const float NovaRingExpandSeconds = 0.35f;
        private const float MoteIntervalSeconds = 1.2f;
        private const float MoteScale = 0.5f;
        private const float MoteHeightMeters = 0.4f;

        private static readonly Color WaterAqua = new Color(0.435f, 0.827f, 0.878f, 1f);
        private static readonly Color HealGreen = new Color(0.247f, 0.839f, 0.498f, 1f);

        private Vector3 _casterPosition;
        private Vector3 _target;
        private float _radius;
        private float _ringDuration;
        private long _casterPlayerId;

        /// <summary>
        /// Plays the whole sequence on this client. Call it from the ability's per-client path (each peer runs the
        /// cast RPC), not once on the owner.
        /// </summary>
        public static void Play(Vector3 casterPosition, Vector3 target, float radius, float ringDuration,
            long casterPlayerId)
        {
            if (!NpcFxRange.NearLocalPlayer(target)) return;

            var host = new GameObject("SanctuaryWaterDrop");
            host.transform.position = target;
            var drop = host.AddComponent<SanctuaryWaterDrop>();
            drop._casterPosition = casterPosition;
            drop._target = target;
            drop._radius = Mathf.Max(radius, Mathf.Epsilon);
            drop._ringDuration = ringDuration;
            drop._casterPlayerId = casterPlayerId;
            drop.StartCoroutine(drop.RunSequence());
        }

        private IEnumerator RunSequence()
        {
            Vector3 groundedTarget = _target;
            groundedTarget.y = GroundHeightSampler.Sample(_target, _target.y);

            AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_SANCTUARY, _casterPosition, Quaternion.identity, CastGlowScale);
            AbilityFXManager.SpawnLocalSound(AbilityFXManager.SFX_HEAL_START, _casterPosition);

            yield return new WaitForSeconds(GroundGiveAtSeconds);

            AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_WATER_RIPPLE, groundedTarget, Quaternion.identity, RippleScale);
            Tinted(AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_GROUND_GIVE, groundedTarget), WaterAqua);

            yield return new WaitForSeconds(RiseStartSeconds - GroundGiveAtSeconds);

            var column = AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_WATER_COLUMN, groundedTarget);
            Tinted(column, WaterAqua);
            GiveUpwardVelocity(column);

            yield return RunColumn(column, groundedTarget);

            AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_WATER_IMPACT, groundedTarget, Quaternion.identity, WaterImpactScale);
            AbilityFXManager.SpawnLocalSound(AbilityFXManager.SFX_LAND_WATER, groundedTarget);
            AbilityFXManager.SpawnLocalSound(AbilityFXManager.SFX_SHAMAN_HEAL, groundedTarget);

            yield return new WaitForSeconds(FallEndSeconds - HangEndSeconds);

            var nova = AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_NOVA_RING, groundedTarget);
            Tinted(nova, HealGreen);
            GroundRingManager.Show(GroundRingKind.Heal, groundedTarget, _radius, _ringDuration, _casterPlayerId);

            yield return ExpandNovaRing(nova);
            yield return EmitHealingMotes(groundedTarget);

            AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_WATER_RIPPLE, groundedTarget, Quaternion.identity, RippleScale);
            Destroy(gameObject);
        }

        /// <summary>Draws the column up, holds it with a wobble, then drops it back into the ground.</summary>
        private IEnumerator RunColumn(GameObject column, Vector3 groundedTarget)
        {
            float riseSeconds = RiseEndSeconds - RiseStartSeconds;
            for (float elapsed = 0f; elapsed < riseSeconds; elapsed += Time.deltaTime)
            {
                MoveColumn(column, groundedTarget, Mathf.SmoothStep(0f, 1f, elapsed / riseSeconds), 1f);
                yield return null;
            }

            float hangSeconds = HangEndSeconds - RiseEndSeconds;
            for (float elapsed = 0f; elapsed < hangSeconds; elapsed += Time.deltaTime)
            {
                float wobble = 1f + HangWobbleAmplitude *
                    Mathf.Sin(elapsed / hangSeconds * HangWobbleCycles * Mathf.PI * 2f);
                MoveColumn(column, groundedTarget, 1f, wobble);
                yield return null;
            }

            float fallSeconds = FallEndSeconds - HangEndSeconds;
            for (float elapsed = 0f; elapsed < fallSeconds; elapsed += Time.deltaTime)
            {
                MoveColumn(column, groundedTarget, 1f - Mathf.SmoothStep(0f, 1f, elapsed / fallSeconds), 1f);
                yield return null;
            }
        }

        private static void MoveColumn(GameObject column, Vector3 groundedTarget, float heightFraction, float scale)
        {
            if (column == null) return;
            column.transform.position = groundedTarget + Vector3.up * ColumnHeightMeters * heightFraction;
            column.transform.localScale = Vector3.one * scale;
        }

        private IEnumerator ExpandNovaRing(GameObject nova)
        {
            float targetScale = _radius / NovaRingNativeRadiusMeters;
            for (float elapsed = 0f; elapsed < NovaRingExpandSeconds; elapsed += Time.deltaTime)
            {
                if (nova == null) yield break;
                nova.transform.localScale = Vector3.one *
                    Mathf.Lerp(0f, targetScale, elapsed / NovaRingExpandSeconds);
                yield return null;
            }
            if (nova != null) nova.transform.localScale = Vector3.one * targetScale;
        }

        private IEnumerator EmitHealingMotes(Vector3 groundedTarget)
        {
            float remaining = _ringDuration - FallEndSeconds - NovaRingExpandSeconds;
            while (remaining > 0f)
            {
                Vector2 offset = Random.insideUnitCircle * _radius;
                Vector3 motePosition = groundedTarget + new Vector3(offset.x, MoteHeightMeters, offset.y);
                motePosition.y = GroundHeightSampler.Sample(motePosition, groundedTarget.y) + MoteHeightMeters;
                Tinted(AbilityFXManager.SpawnLocalEffect(AbilityFXManager.FX_HEALING_MOTES, motePosition, Quaternion.identity, MoteScale),
                    HealGreen);

                yield return new WaitForSeconds(MoteIntervalSeconds);
                remaining -= MoteIntervalSeconds;
            }
        }

        private static void Tinted(GameObject instance, Color color)
        {
            if (instance == null) return;
            FxTint.Apply(instance, color);
        }

        private static void GiveUpwardVelocity(GameObject instance)
        {
            if (instance == null) return;
            var systems = instance.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var system = systems[i];
                if (system == null) continue;
                var main = system.main;
                main.startSpeed = ColumnStartSpeed;
                main.gravityModifier = ColumnGravityModifier;
            }
        }
    }
}
