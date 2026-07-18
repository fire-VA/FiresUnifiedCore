using FiresCore.Storage;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod surface over Core's authoritative <see cref="PlayerAppearanceRepository"/> (the shared
    /// LiteDB vault), mirroring <see cref="LeaderboardBridge"/>. The leaderboard frontend saves an owner's
    /// captured appearance and reads a remote player's appearance for the mannequin preview through here, so
    /// the data lives in Core, not the frontend. Owner key is the trimmed player name
    /// (<c>LeaderboardServerFeed.OwnerKey</c>) — the SAME key the board uses.
    ///
    /// Writes are SERVER-AUTHORITATIVE — the save RPC handler is the only caller of <see cref="Save"/>.
    /// </summary>
    public static class PlayerAppearanceBridge
    {
        public static void Save(PlayerAppearance appearance)
            => PlayerAppearanceRepository.Upsert(appearance);

        public static PlayerAppearance Get(string owner)
            => PlayerAppearanceRepository.Get(owner);
    }
}
