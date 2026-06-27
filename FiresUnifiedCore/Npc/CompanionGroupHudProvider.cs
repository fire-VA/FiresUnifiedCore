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

        private static IEnumerable<GroupHudMember> Collect()
        {
            var local = Player.m_localPlayer;
            if (local == null) yield break;

            long playerId = local.GetPlayerID();
            Vector3 playerPos = local.transform.position;

            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed || companion.ownerPlayerId != playerId) continue;
                if (!companion.ShouldBeFollowing) continue;

                yield return ToMember(companion, playerPos);
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
                if (respawnTime > 0f)
                {
                    float remaining = respawnTime - Time.time;
                    member.StatusText = remaining > 0f ? $"({remaining:F0}s)" : "(Respawning…)";
                }
                else
                {
                    member.StatusText = "(Dead)";
                }
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
