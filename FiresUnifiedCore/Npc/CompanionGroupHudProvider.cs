using System.Collections.Generic;
using FiresCore.Bridge;
using FiresCore.UI.GroupHud;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Feeds the local player's following companions into the shared <see cref="GroupHudController"/>
    /// via <see cref="GroupHudBridge"/>. This is the companion-side adapter that replaces the old
    /// monolithic companion-scanning HUD — the HUD itself is now generic and Core-owned, and this
    /// just maps live <see cref="CompanionController"/> state onto <see cref="GroupHudMember"/> rows.
    /// </summary>
    public static class CompanionGroupHudProvider
    {
        private const string ProviderKey = "fires.companions";
        private const int MaxStatusIcons = 10;

        public static void Register() => GroupHudBridge.RegisterProvider(ProviderKey, Collect);

        public static void Unregister() => GroupHudBridge.UnregisterProvider(ProviderKey);

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
                    var d = kv.Value;
                    if (d.OwnerPlayerId != playerId || liveIds.Contains(kv.Key) || now > d.GraceExpiry)
                    {
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                        continue;
                    }
                    float remaining = d.RespawnDeadline - now;
                    yield return new GroupHudMember
                    {
                        Id = "companion_" + kv.Key,
                        Name = d.Name,
                        IsDead = true,
                        StatusText = remaining > 0f ? $"({remaining:F0}s)" : "(Respawning…)",
                    };
                }
                if (stale != null) foreach (var k in stale) _deadPending.Remove(k);
            }
        }

        private static GroupHudMember ToMember(CompanionController c, Vector3 playerPos)
        {
            var character = c.GetCharacter();
            bool dead = c.isDefeated || (character != null && character.IsDead());

            var member = new GroupHudMember
            {
                Id = "companion_" + (string.IsNullOrEmpty(c.companionId) ? c.GetInstanceID().ToString() : c.companionId),
                Name = c.GetDisplayName(),
                IsDead = dead
            };

            if (dead)
            {
                float respawnTime = CompanionDeathHandler.GetRespawnTime(c.companionId);
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
                    var dh = c.GetComponent<CompanionDeathHandler>();
                    remaining = dh != null ? dh.respawnDelay : 120f;
                }
                member.StatusText = remaining > 0f ? $"({remaining:F0}s)" : "(Respawning…)";

                // Persist the row so it survives the corpse's imminent destruction. This provider only
                // iterates the local player's own companions, so recording here is inherently owner-side.
                RecordDeath(c.companionId, member.Name, Mathf.Max(0f, remaining), c.ownerPlayerId);
                return member;
            }

            member.Distance = Vector3.Distance(playerPos, c.transform.position);

            var stats = c.GetStats();
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
            foreach (var se in effects)
            {
                if (se == null || se.m_icon == null) continue;
                (icons ?? (icons = new List<GroupHudStatusIcon>())).Add(new GroupHudStatusIcon
                {
                    Icon = se.m_icon,
                    RemainingSeconds = se.m_ttl > 0f ? Mathf.Max(0f, se.m_ttl - se.m_time) : 0f
                });
                if (icons.Count >= MaxStatusIcons) break;
            }
            return icons;
        }
    }
}
