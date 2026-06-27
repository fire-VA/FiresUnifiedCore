using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// One row in the group HUD. A mod-agnostic snapshot: any mod can describe its tracked
    /// entities (companions, party players, summons, …) as a list of these and feed them to the
    /// shared HUD via <see cref="FiresCore.Bridge.GroupHudBridge"/>. The HUD never reaches back into
    /// the source — it only renders what the provider hands it each refresh.
    /// </summary>
    public sealed class GroupHudMember
    {
        /// <summary>Stable unique key, used to diff rows across refreshes (e.g. companionId, "party_&lt;uid&gt;").</summary>
        public string Id;

        public string Name = "";

        /// <summary>Name tint. Leave default — the HUD substitutes its gold/dead colors when unset (a == 0).</summary>
        public Color NameColor = default;

        /// <summary>True = render greyed-out (dead/offline). Bars zero out and the dead palette is used.</summary>
        public bool IsDead;

        /// <summary>Metres from the local player. Negative = don't show a distance readout.</summary>
        public float Distance = -1f;

        /// <summary>
        /// Optional right-aligned status text that REPLACES the distance when non-empty
        /// (e.g. "(12s)", "(Respawning…)", "(Dead)", "(offline)").
        /// </summary>
        public string StatusText;

        public float Health;
        public float MaxHealth;
        public float Stamina;
        public float MaxStamina;

        /// <summary>Eitr. <see cref="MaxEitr"/> &lt;= 0 hides the eitr bar for this row.</summary>
        public float Eitr;
        public float MaxEitr;

        /// <summary>Active status-effect icons to show under the bars. Null/empty = none.</summary>
        public List<GroupHudStatusIcon> StatusIcons;

        /// <summary>Ascending sort hint. Ties break on <see cref="Name"/>. Lets providers group their own rows.</summary>
        public int SortKey;
    }

    /// <summary>A single status-effect pip under a member row (vanilla SE icon + optional countdown).</summary>
    public sealed class GroupHudStatusIcon
    {
        /// <summary>The effect sprite; null falls back to a solid colored square.</summary>
        public Sprite Icon;

        public Color Color = Color.white;

        /// <summary>Remaining seconds; &lt;= 0 renders no timer text (permanent / unknown ttl).</summary>
        public float RemainingSeconds;
    }
}
