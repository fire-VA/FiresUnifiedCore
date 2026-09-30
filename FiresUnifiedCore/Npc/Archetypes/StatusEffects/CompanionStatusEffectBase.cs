using UnityEngine;
using System;

namespace FiresCore.Npc.Archetypes.StatusEffects
{
    /// <summary>
    /// Base class for companion status effects, providing the HUD icon (a colored square when no sprite is set),
    /// duration countdown, tooltip and logging. Concrete effects live in folders by archetype.
    /// </summary>
    public abstract class CompanionStatusEffectBase : StatusEffect
    {
        private const int CompanionPrefixLength = 9;
        private const float InnerBorderBrightenAmount = 0.3f;

        /// <summary>Duration of the effect in seconds.</summary>
        public float Duration { get; set; } = 10f;
        
        /// <summary>Icon to display in HUD.</summary>
        public Sprite EffectIcon { get; set; }
        
        /// <summary>Verbose logging for debugging.</summary>
        public static bool VerboseLogging = false;
        
        /// <summary>The character who applied this effect (if applicable).</summary>
        public Character SourceCharacter { get; set; }

        /// <summary>Whose ability this is: the one who applied it, else the character wearing it.</summary>
        protected Character Caster => SourceCharacter ?? m_character;

        /// <summary>
        /// True on the one peer that owns the character. AbilityRPCManager only adds class effects there, so this is a belt
        /// for work done when the effect goes on: a heal or hit run on every peer landed once per peer.
        /// </summary>
        protected bool Authoritative => m_character != null && m_character.IsOwner();

        /// <summary>How often a running aura re-spawns its FX, per second.</summary>
        protected const float AuraSpawnsPerSecond = 1f;

        /// <summary>
        /// Whether a looping aura FX is due this frame, at about <paramref name="spawnsPerSecond"/> regardless of frame rate. The
        /// auras rolled a fixed chance EVERY frame (Divine Shield 0.3: ~18 spawns a second at 60 fps), and the pile of pooled FX
        /// washed the screen pink/magenta (Fire's Divine Shield screenshot, 2026-09-29; [wishbone]).
        /// </summary>
        protected static bool AuraDue(float dt, float spawnsPerSecond) => UnityEngine.Random.value < spawnsPerSecond * dt;

        /// <summary>Fire: heals and buffs only the caster's party (ClassTargeting).</summary>
        protected bool IsParty(Character character) => ClassTargeting.IsPartyMember(Caster, character);

        /// <summary>Fire: damage and debuffs only on enemies; a player only when both have PvP on (ClassTargeting).</summary>
        protected bool IsFoe(Character character) => ClassTargeting.IsEnemyTarget(Caster, character);

        // ---- Placed areas (Rain of Arrows, Meteor): where the caster aimed, handed over by the class mod just before the cast ----

        private static Vector3 s_placement;
        private static float s_placementAt = -1f;
        private const float PlacementFresh = 1f, AutoPlaceRange = 30f;

        /// <summary>
        /// The class mod calls this right before casting a placed area skill as a self effect (e.g. RPGClasses' ranger_rain_arrows,
        /// aimed up to 40 m): the next placed effect lands there instead of 8 m in front of the caster.
        /// </summary>
        public static void SetNextPlacement(Vector3 point)
        {
            s_placement = point;
            s_placementAt = Time.time;
        }

        /// <summary>
        /// Where a placed area effect lands: the class mod's aim if it was just handed over, else the nearest foe in front of the
        /// caster within <see cref="AutoPlaceRange"/> m, else <paramref name="fallbackDistance"/> m ahead (R75: Rain of Arrows always
        /// fell 8 m ahead, so it hit nothing unless a foe happened to stand there).
        /// </summary>
        protected Vector3 PlacedTarget(float fallbackDistance, out string how)
        {
            Character caster = m_character;
            if (s_placementAt >= 0f && Time.time - s_placementAt < PlacementFresh)
            {
                s_placementAt = -1f;
                how = "aimed";
                return s_placement;
            }
            how = "ahead";
            if (caster == null) return Vector3.zero;
            Vector3 at = caster.transform.position, forward = caster.transform.forward;
            Character best = null;
            float bestDistance = float.MaxValue;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == caster || other.IsDead() || !IsFoe(other)) continue;
                Vector3 to = other.transform.position - at;
                float distance = to.magnitude;
                if (distance > AutoPlaceRange || Vector3.Dot(forward, to / Mathf.Max(0.01f, distance)) < 0.3f) continue;
                if (distance < bestDistance) { bestDistance = distance; best = other; }
            }
            if (best != null)
            {
                how = $"on the nearest foe in front, {best.m_name}";
                return best.transform.position;
            }
            return at + forward * fallbackDistance;
        }

        // Each hit's result, read back a moment later on this peer (the health change comes back from the target's owner).
        private sealed class PendingHit
        {
            public Character Target;
            public string Effect;
            public float Sent;
            public float HpBefore;
            public float At;
        }

        private static readonly System.Collections.Generic.List<PendingHit> s_pendingHits = new System.Collections.Generic.List<PendingHit>();
        private const float HitReadBack = 0.6f;

        /// <summary>Counts a hit or heal this effect landed (ClassEffectLedger, read by class_test) and logs it verbosely.</summary>
        protected void NoteHit(Character target, float amount, bool heal)
        {
            // Always on (Fire, R75: "the skills don't seem to actually do any damage"): what was sent, and a moment later what the
            // target's health did, "[ClassSkill] <effect> hit <foe>: dealt N (hp a -> b)".
            if (!heal && target != null && s_pendingHits.Count < 64)
            {
                s_pendingHits.Add(new PendingHit { Target = target, Effect = name, Sent = amount, HpBefore = target.GetHealth(), At = Time.time });
                HitReader.Ensure();
            }
            ClassEffectLedger.Record(name, heal);
            if (VerboseLogging)
            {
                Debug.Log($"[{GetType().Name}] {(heal ? "healed" : "hit")} {target?.m_name} for {amount:0.#}");
            }
        }

        private sealed class HitReader : MonoBehaviour
        {
            private static HitReader s_instance;

            internal static void Ensure()
            {
                if (s_instance != null) return;
                var go = new GameObject("FiresClassSkillHits");
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<HitReader>();
            }

            private void Update()
            {
                float now = Time.time;
                for (int i = s_pendingHits.Count - 1; i >= 0; i--)
                {
                    PendingHit h = s_pendingHits[i];
                    if (now - h.At < HitReadBack) continue;
                    s_pendingHits.RemoveAt(i);
                    if (h.Target == null) continue;
                    float after = h.Target.GetHealth();
                    string effect = h.Effect != null && h.Effect.StartsWith("Companion_") ? h.Effect.Substring(10) : h.Effect;
                    Debug.Log($"[ClassSkill] {effect} hit {h.Target.m_name}: dealt {h.Sent:0.#} (hp {h.HpBefore:0} -> {after:0}" +
                              $"{(h.Target.IsDead() ? ", dead" : "")}{(Mathf.Approximately(after, h.HpBefore) && !h.Target.IsDead() ? ", NO CHANGE" : "")})");
                }
            }
        }

        /// <summary>
        /// Friendly display name for the tooltip. Override in derived classes.
        /// </summary>
        public virtual string DisplayName => GetDisplayNameFromEffectName(name);
        
        /// <summary>
        /// Description shown in tooltip. Override in derived classes.
        /// </summary>
        public virtual string Description => "Companion ability effect.";
        
        public override void Setup(Character character)
        {
            base.Setup(character);
            m_ttl = Duration;
            m_icon = EffectIcon;
            
            // Set tooltip info for HUD display
            m_name = DisplayName;
            m_tooltip = Description;
            
            OnEffectApplied();
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[{GetType().Name}] Applied to {m_character.m_name}, Duration: {Duration}s");
            }
        }
        
        public override void Stop()
        {
            base.Stop();
            
            OnEffectRemoved();
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[{GetType().Name}] Removed from {m_character.m_name}");
            }
        }
        
        /// <summary>
        /// Called when the effect is first applied.
        /// Override in derived classes for custom setup logic.
        /// </summary>
        protected virtual void OnEffectApplied() { }
        
        /// <summary>
        /// Called when the effect is removed.
        /// Override in derived classes for custom cleanup logic.
        /// </summary>
        protected virtual void OnEffectRemoved() { }
        
        /// <summary>
        /// Creates a simple colored icon texture as a fallback.
        /// Creates a rounded rectangle with a border for better visibility.
        /// </summary>
        public static Sprite CreateFallbackIcon(Color fillColor)
        {
            int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color borderColor = new Color(0.1f, 0.1f, 0.1f, 1f); // Dark border
            Color innerBorderColor = new Color(
                Mathf.Min(fillColor.r + InnerBorderBrightenAmount, 1f),
                Mathf.Min(fillColor.g + InnerBorderBrightenAmount, 1f),
                Mathf.Min(fillColor.b + InnerBorderBrightenAmount, 1f),
                1f
            ); // Lighter inner border
            
            int borderWidth = 3;
            int innerBorderWidth = 2;
            int cornerRadius = 8;
            
            Color[] pixels = new Color[size * size];
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x;
                    
                    // Calculate distance from corners for rounded effect
                    float distFromEdge = GetDistanceFromRoundedEdge(x, y, size, cornerRadius);
                    
                    if (distFromEdge < 0)
                    {
                        // Outside rounded corners - transparent
                        pixels[idx] = Color.clear;
                    }
                    else if (distFromEdge < borderWidth)
                    {
                        // Outer border
                        pixels[idx] = borderColor;
                    }
                    else if (distFromEdge < borderWidth + innerBorderWidth)
                    {
                        // Inner border (highlight)
                        pixels[idx] = innerBorderColor;
                    }
                    else
                    {
                        // Fill
                        pixels[idx] = fillColor;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
        
        /// <summary>
        /// Helper for rounded rectangle calculation.
        /// </summary>
        private static float GetDistanceFromRoundedEdge(int x, int y, int size, int cornerRadius)
        {
            int right = size - 1 - x;
            int top = size - 1 - y;
            
            // Check if in corner region
            bool inLeftCorner = x < cornerRadius;
            bool inRightCorner = right < cornerRadius;
            bool inBottomCorner = y < cornerRadius;
            bool inTopCorner = top < cornerRadius;
            
            // Calculate distance from edge
            float distFromEdge;
            
            if ((inLeftCorner && inBottomCorner))
            {
                // Bottom-left corner
                float dx = cornerRadius - x;
                float dy = cornerRadius - y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inRightCorner && inBottomCorner))
            {
                // Bottom-right corner
                float dx = cornerRadius - right;
                float dy = cornerRadius - y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inLeftCorner && inTopCorner))
            {
                // Top-left corner
                float dx = cornerRadius - x;
                float dy = cornerRadius - top;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inRightCorner && inTopCorner))
            {
                // Top-right corner
                float dx = cornerRadius - right;
                float dy = cornerRadius - top;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else
            {
                // Not in corner - use minimum distance from any edge
                distFromEdge = Mathf.Min(Mathf.Min(x, right), Mathf.Min(y, top));
            }
            
            return distFromEdge;
        }
        
        /// <summary>
        /// Converts an effect name like "CompanionBerserkRage" to "Berserk Rage".
        /// </summary>
        protected static string GetDisplayNameFromEffectName(string effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return "Unknown Effect";
            
            // Remove "Companion" prefix
            if (effectName.StartsWith("Companion"))
            {
                effectName = effectName.Substring(CompanionPrefixLength);
            }
            
            // Add spaces before capital letters
            var result = new System.Text.StringBuilder();
            foreach (char c in effectName)
            {
                if (char.IsUpper(c) && result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(c);
            }
            
            return result.ToString();
        }
    }
}
