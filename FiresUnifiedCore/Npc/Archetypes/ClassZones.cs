using System;
using UnityEngine;

namespace FiresCore.Npc.Archetypes
{
    public enum ZoneKind
    {
        /// <summary>Damage and debuffs: only enemies of the caster (ClassTargeting.IsEnemyTarget).</summary>
        Harmful,

        /// <summary>Heals and buffs: only the caster's party (ClassTargeting.IsPartyMember).</summary>
        Helpful,
    }

    /// <summary>
    /// Lingering areas where a class skill was aimed (Fire, popup 2026-09-28 21:3x: "real lingering zones"): banner, beacon,
    /// smoke, clouds, storms, wells, totems, traps. A zone runs on the CASTER's peer and, every tick, hands each character
    /// inside it that ClassTargeting allows to the skill's callback. The callback does its work through the owner-safe
    /// paths (Character.Damage / AbilityHeals.Apply / AbilityRPCManager.ApplySingleEffect), so each target is changed on its
    /// owner. The look is a vanilla FX name repeated over the zone (networked, AbilityRPCManager.SpawnFX); no new models.
    /// </summary>
    public static class ClassZones
    {
        public static ClassZone ZoneAt(Character caster, Vector3 point, float radius, float duration, ZoneKind kind,
            Action<Character> onTick = null, float tickSeconds = 1f, string fx = null, float fxSeconds = 2f)
        {
            if (caster == null || radius <= 0f || duration <= 0f) return null;
            var host = new GameObject("FiresClassZone");
            UnityEngine.Object.DontDestroyOnLoad(host);
            ClassZone zone = host.AddComponent<ClassZone>();
            zone.Begin(caster, point, radius, duration, kind, onTick, Mathf.Max(0.1f, tickSeconds), fx, Mathf.Max(0.5f, fxSeconds));
            return zone;
        }
    }

    public sealed class ClassZone : MonoBehaviour
    {
        private Character _caster;
        private ZoneKind _kind;
        private Action<Character> _onTick;
        private float _tickSeconds;
        private float _until;
        private float _nextTick;
        private string _fx;
        private float _fxSeconds;
        private float _nextFx;

        public Vector3 Point { get; private set; }
        public float Radius { get; private set; }
        public bool Running => this != null && enabled;

        internal void Begin(Character caster, Vector3 point, float radius, float duration, ZoneKind kind,
            Action<Character> onTick, float tickSeconds, string fx, float fxSeconds)
        {
            _caster = caster;
            Point = point;
            Radius = radius;
            _kind = kind;
            _onTick = onTick;
            _tickSeconds = tickSeconds;
            _until = Time.time + duration;
            _fx = fx;
            _fxSeconds = fxSeconds;
            _nextTick = Time.time;
            _nextFx = Time.time;
        }

        /// <summary>Ends the zone now.</summary>
        public void Stop()
        {
            enabled = false;
            Destroy(gameObject);
        }

        private void Update()
        {
            if (_caster == null || _caster.IsDead() || Time.time >= _until) { Stop(); return; }

            if (!string.IsNullOrEmpty(_fx) && Time.time >= _nextFx)
            {
                _nextFx = Time.time + _fxSeconds;
                AbilityRPCManager.SpawnFX(Point, _fx);
            }

            if (Time.time < _nextTick) return;
            _nextTick = Time.time + _tickSeconds;
            if (_onTick == null) return;
            foreach (Character character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (Vector3.Distance(character.transform.position, Point) > Radius) continue;
                bool allowed = _kind == ZoneKind.Harmful
                    ? ClassTargeting.IsEnemyTarget(_caster, character)
                    : ClassTargeting.IsPartyMember(_caster, character);
                if (!allowed) continue;
                try { _onTick(character); }
                catch (Exception ex) { Debug.LogWarning($"[ClassZones] a zone's tick threw {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }
}
