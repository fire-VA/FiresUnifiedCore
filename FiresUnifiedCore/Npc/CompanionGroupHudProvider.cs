using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using FiresCore.Bridge;
using FiresCore.UI.GroupHud;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Maps the local player's following companions onto <see cref="GroupHudMember"/> rows for the
    /// shared <see cref="GroupHudController"/>.
    /// </summary>
    public static class CompanionGroupHudProvider
    {
        private const string ProviderKey = "fires.companions";
        private const int MaxStatusIcons = 10;

        private static readonly string[] CompanionHostPluginGuids = { "com.Fire.FiresCompanions", "com.Fire.FiresRPGmaker" };

        public static void Register() => GroupHudBridge.RegisterProvider(ProviderKey, Collect);

        public static void Unregister() => GroupHudBridge.UnregisterProvider(ProviderKey);

        /// <summary>Feeds the HUD only when a mod that actually spawns companions is loaded.</summary>
        internal static void RegisterIfCompanionHostLoaded()
        {
            if (CompanionHostPluginGuids.Any(Chainloader.PluginInfos.ContainsKey)) Register();
        }

        // ── Dead-pending roster (keep dead companions on the HUD) ────────────────────────────────────
        // A dead companion's live GameObject is destroyed ~1s after death, which would blink its HUD
        // row out of existence. Instead we keep the row shown — grayed, with the respawn countdown —
        // until it respawns (reappears live) or the timer lapses. Populated by RecordDeath from the
        // owner-side death-notice RPC (the authoritative source of the respawn delay on a dedi, where
        // the death itself runs on whatever peer owns the ZDO). Client-side, owner-only display.
        private sealed class DeadPending
        {
            public string Name;
            public float RespawnDeadline; // Time.time-based
            public float GraceExpiry;     // drop the ghost row this long after the deadline if it never came back
            public long OwnerPlayerId;
        }
        private static readonly Dictionary<string, DeadPending> _deadPending = new Dictionary<string, DeadPending>();

        /// <summary>
        /// Record a companion as dead-and-pending-respawn so the Group HUD keeps its row (grayed, with a
        /// countdown) instead of dropping it when the corpse is destroyed. Called on the OWNER client
        /// from the death-notice RPC. Re-recording refreshes the deadline.
        /// </summary>
        public static void RecordDeath(string companionId, string name, float respawnSeconds, long ownerPlayerId)
        {
            if (string.IsNullOrEmpty(companionId)) return;
            if (_deadPending.ContainsKey(companionId)) return; // already tracked — keep the first deadline stable
            float now = Time.time;
            float delay = Mathf.Max(0f, respawnSeconds);
            _deadPending[companionId] = new DeadPending
            {
                Name = string.IsNullOrEmpty(name) ? "Companion" : name,
                RespawnDeadline = now + delay,
                GraceExpiry = now + delay + 60f,
                OwnerPlayerId = ownerPlayerId,
            };
        }

        /// <summary>Forget a dead-pending row (e.g. the companion was recalled / permanently dismissed).</summary>
        public static void ClearDeath(string companionId)
        {
            if (!string.IsNullOrEmpty(companionId)) _deadPending.Remove(companionId);
        }

        private static IEnumerable<GroupHudMember> Collect()
        {
            var local = Player.m_localPlayer;
            if (local == null) yield break;

            long playerId = local.GetPlayerID();
            Vector3 playerPos = local.transform.position;

            var liveIds = new HashSet<string>();
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed || companion.ownerPlayerId != playerId) continue;
                if (!companion.ShouldBeFollowing) continue;

                if (!string.IsNullOrEmpty(companion.companionId)) liveIds.Add(companion.companionId);
                yield return ToMember(companion, playerPos);
            }

            // Dead-pending rows: shown after the live ones, grayed with a respawn countdown. Drop any
            // that have respawned (now live again, same companionId) or whose grace window elapsed.
            if (_deadPending.Count > 0)
            {
                float now = Time.time;
                List<string> stale = null;
                foreach (var kv in _deadPending)
                {
                    var pending = kv.Value;
                    if (pending.OwnerPlayerId != playerId || liveIds.Contains(kv.Key) || now > pending.GraceExpiry)
                    {
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                        continue;
                    }
                    float remaining = pending.RespawnDeadline - now;
                    yield return new GroupHudMember
                    {
                        Id = "companion_" + kv.Key,
                        Name = pending.Name,
                        IsDead = true,
                        StatusText = remaining > 0f ? $"({remaining:F0}s)" : "(Respawning…)",
                    };
                }
                if (stale != null) foreach (var staleId in stale) _deadPending.Remove(staleId);
            }
        }

        private static GroupHudMember ToMember(CompanionController companion, Vector3 playerPos)
        {
            var character = companion.GetCharacter();
            bool dead = companion.isDefeated || (character != null && character.IsDead());

            var member = new GroupHudMember
            {
                Id = "companion_" + (string.IsNullOrEmpty(companion.companionId) ? companion.GetInstanceID().ToString() : companion.companionId),
                Name = companion.GetDisplayName(),
                IsDead = dead
            };

            if (dead)
            {
                float respawnTime = CompanionDeathHandler.GetRespawnTime(companion.companionId);
                float remaining;
                if (respawnTime > 0f)
                {
                    remaining = respawnTime - Time.time;
                }
                else
                {
                    // Dedi: the respawn timer was scheduled on whatever peer owns the ZDO, so this
                    // client may lack GetRespawnTime. Approximate from the configured delay (the death
                    // just happened) so the countdown still shows.
                    var deathHandler = companion.GetComponent<CompanionDeathHandler>();
                    remaining = deathHandler != null ? deathHandler.respawnDelay : 120f;
                }
                member.StatusText = remaining > 0f ? $"({remaining:F0}s)" : "(Respawning…)";

                // Persist the row so it survives the corpse's imminent destruction. This provider only
                // iterates the local player's own companions, so recording here is inherently owner-side.
                RecordDeath(companion.companionId, member.Name, Mathf.Max(0f, remaining), companion.ownerPlayerId);
                return member;
            }

            member.Distance = Vector3.Distance(playerPos, companion.transform.position);

            var stats = companion.GetStats();
            if (stats != null)
            {
                member.Health = stats.CurrentHealth;
                member.MaxHealth = stats.MaxHealth;
                member.Stamina = stats.CurrentStamina;
                member.MaxStamina = stats.MaxStamina;
                member.Eitr = stats.CurrentEitr;
                member.MaxEitr = stats.MaxEitr;
            }

            member.StatusIcons = CollectStatusIcons(character);
            return member;
        }

        private static List<GroupHudStatusIcon> CollectStatusIcons(Character character)
        {
            var seman = character != null ? character.GetSEMan() : null;
            var effects = seman != null ? seman.GetStatusEffects() : null;
            if (effects == null || effects.Count == 0) return null;

            List<GroupHudStatusIcon> icons = null;
            foreach (var effect in effects)
            {
                if (effect == null || effect.m_icon == null) continue;
                (icons ?? (icons = new List<GroupHudStatusIcon>())).Add(new GroupHudStatusIcon
                {
                    Icon = effect.m_icon,
                    RemainingSeconds = effect.m_ttl > 0f ? Mathf.Max(0f, effect.m_ttl - effect.m_time) : 0f
                });
                if (icons.Count >= MaxStatusIcons) break;
            }
            return icons;
        }
    }
}
