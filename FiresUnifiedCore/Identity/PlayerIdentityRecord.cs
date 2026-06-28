using System;
using System.Collections.Generic;
using LiteDB;

namespace FiresCore.Identity
{
    // One captured connection identity. Persisted in the shared VaultDatabase under the
    // PlayerIdentity collection; ported from VikingLands.Core's PlayerIdentityRecord.
    public sealed class PlayerIdentityRecord
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string SteamId { get; set; } = string.Empty;
        public long ConnectionUid { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public DateTime LastConnectionUtc { get; set; }
        public string LastIp { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class PlayerIdentityGroupedDto
    {
        public string SteamId { get; set; } = string.Empty;
        public List<PlayerIdentityProfileDto> Profiles { get; set; } = new List<PlayerIdentityProfileDto>();
    }

    public sealed class PlayerIdentityProfileDto
    {
        public long ConnectionUid { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public DateTime LastConnectionUtc { get; set; }
        public string LastIp { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
    }

    public sealed class PlayerIdentityStatsDto
    {
        public int TotalRecords { get; set; }
        public int DistinctSteamIds { get; set; }
        public int AdminProfiles { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
