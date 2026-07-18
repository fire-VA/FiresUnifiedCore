using LiteDB;

namespace FiresCore.Storage
{
    /// <summary>
    /// One persisted guild in the shared per-world vault (the <see cref="VaultDatabase.GuildCollection"/>
    /// collection). Core stores the stable id plus a few queryable top-level fields; the full guild domain
    /// model (members, ranks, applications, permission rules) lives in the guild mod and is serialized into
    /// <see cref="Payload"/>, so Core carries no guild domain logic (extraction plan section 6). Keyed by
    /// <see cref="GuildId"/> as the LiteDB document id, so a guild rename never re-keys the row.
    /// </summary>
    public class GuildRecord
    {
        [BsonId] public string GuildId { get; set; }
        public string Name { get; set; }
        public long LeaderPlayerId { get; set; }

        /// <summary>Mod-serialized full guild document (JSON). Opaque to Core.</summary>
        public string Payload { get; set; }

        public long UpdatedAtUtcTicks { get; set; }
    }
}
