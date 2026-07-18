using LiteDB;

namespace FiresCore.Storage
{
    /// <summary>
    /// Owner-side snapshot of a player's resolved <c>VisEquipment</c> state, mirrored for the remote
    /// leaderboard mannequin preview. One row per player in the shared LiteDB vault, keyed by
    /// <see cref="Owner"/> (the trimmed player name — the SAME key the leaderboard board uses, via
    /// <c>LeaderboardServerFeed.OwnerKey</c>). Colors travel as 8-hex <c>RRGGBBAA</c> strings
    /// (<see cref="UnityEngine.ColorUtility"/> round-trips them losslessly); item fields are prefab-name
    /// tokens (or, on a hashed build, the stable-hash token — the dress side resolves either form).
    ///
    /// Wire + disk format is a single versioned <see cref="Pack"/> / <see cref="Unpack"/> pair. The
    /// leading schema-version int lets the field set grow without breaking older readers, and
    /// <see cref="Unpack"/> tolerates a truncated package (returns the partial record rather than throwing).
    /// </summary>
    public class PlayerAppearance
    {
        // ── identity / freshness (travels with appearance, keyed like the board) ──
        [BsonId]
        public string Owner;            // = PlayerName.Trim() — SAME key as LeaderboardEntry.Owner
        public string PlayerName;
        public long UpdatedAtUtcTicks;

        // ── model + colors ──
        public int ModelIndex;          // m_modelIndex (0 = male, 1 = female)
        public string SkinColorRgba;    // m_skinColor -> "RRGGBBAA"
        public string HairColorRgba;    // m_hairColor -> "RRGGBBAA" (also tints beard)

        // ── hair / beard ──
        public string HairItem;         // m_hairItem
        public string BeardItem;        // m_beardItem

        // ── equip slots (+ variants) ──
        public string RightItem;        // m_rightItem        -> RightHand
        public string LeftItem;         // m_leftItem(+variant) -> LeftHand
        public int LeftItemVariant;
        public string ChestItem;        // m_chestItem        -> Chest
        public string LegItem;          // m_legItem          -> Legs
        public string HelmetItem;       // m_helmetItem       -> Helmet
        public string ShoulderItem;     // m_shoulderItem(+variant) -> Shoulder
        public int ShoulderItemVariant;
        public string UtilityItem;      // m_utilityItem      -> Utility
        public string LeftBackItem;     // m_leftBackItem(+variant) -> LeftBack
        public int LeftBackItemVariant;
        public string RightBackItem;    // m_rightBackItem    -> RightBack
        public string TrinketItem;      // m_trinketItem — captured for forward-compat; no Fires slot today

        private const int SchemaVersion = 1;

        public void Pack(ZPackage pkg)
        {
            if (pkg == null) return;
            pkg.Write(SchemaVersion);
            pkg.Write(Owner ?? "");
            pkg.Write(PlayerName ?? "");
            pkg.Write(UpdatedAtUtcTicks);
            pkg.Write(ModelIndex);
            pkg.Write(SkinColorRgba ?? "");
            pkg.Write(HairColorRgba ?? "");
            pkg.Write(HairItem ?? "");
            pkg.Write(BeardItem ?? "");
            pkg.Write(RightItem ?? "");
            pkg.Write(LeftItem ?? "");
            pkg.Write(LeftItemVariant);
            pkg.Write(ChestItem ?? "");
            pkg.Write(LegItem ?? "");
            pkg.Write(HelmetItem ?? "");
            pkg.Write(ShoulderItem ?? "");
            pkg.Write(ShoulderItemVariant);
            pkg.Write(UtilityItem ?? "");
            pkg.Write(TrinketItem ?? "");
            pkg.Write(LeftBackItem ?? "");
            pkg.Write(LeftBackItemVariant);
            pkg.Write(RightBackItem ?? "");
        }

        /// <summary>
        /// Reads a packed record back. Each read is wrapped so a truncated/short package yields a partial
        /// record (whatever was read before the package ran dry) instead of throwing. The leading version
        /// int is read and ignored for now — it exists so future field growth stays backward-compatible.
        /// </summary>
        public static PlayerAppearance Unpack(ZPackage pkg)
        {
            var rec = new PlayerAppearance();
            if (pkg == null) return rec;

            try { pkg.ReadInt(); /* schema version — reserved */ } catch { return rec; }

            try { rec.Owner = pkg.ReadString(); } catch { return rec; }
            try { rec.PlayerName = pkg.ReadString(); } catch { return rec; }
            try { rec.UpdatedAtUtcTicks = pkg.ReadLong(); } catch { return rec; }
            try { rec.ModelIndex = pkg.ReadInt(); } catch { return rec; }
            try { rec.SkinColorRgba = pkg.ReadString(); } catch { return rec; }
            try { rec.HairColorRgba = pkg.ReadString(); } catch { return rec; }
            try { rec.HairItem = pkg.ReadString(); } catch { return rec; }
            try { rec.BeardItem = pkg.ReadString(); } catch { return rec; }
            try { rec.RightItem = pkg.ReadString(); } catch { return rec; }
            try { rec.LeftItem = pkg.ReadString(); } catch { return rec; }
            try { rec.LeftItemVariant = pkg.ReadInt(); } catch { return rec; }
            try { rec.ChestItem = pkg.ReadString(); } catch { return rec; }
            try { rec.LegItem = pkg.ReadString(); } catch { return rec; }
            try { rec.HelmetItem = pkg.ReadString(); } catch { return rec; }
            try { rec.ShoulderItem = pkg.ReadString(); } catch { return rec; }
            try { rec.ShoulderItemVariant = pkg.ReadInt(); } catch { return rec; }
            try { rec.UtilityItem = pkg.ReadString(); } catch { return rec; }
            try { rec.TrinketItem = pkg.ReadString(); } catch { return rec; }
            try { rec.LeftBackItem = pkg.ReadString(); } catch { return rec; }
            try { rec.LeftBackItemVariant = pkg.ReadInt(); } catch { return rec; }
            try { rec.RightBackItem = pkg.ReadString(); } catch { return rec; }

            return rec;
        }

        /// <summary>
        /// Stable hash over only the appearance-bearing fields (excludes <see cref="PlayerName"/> and
        /// <see cref="UpdatedAtUtcTicks"/>) for capture dedup: the owner only re-sends when this changes.
        /// </summary>
        public int GetSignature() => GetSignatureString().GetStableHashCode();

        /// <summary>String form of the appearance fields — also usable as a compact vault token if needed.</summary>
        public string GetSignatureString()
        {
            return string.Join("|", new[]
            {
                ModelIndex.ToString(),
                SkinColorRgba ?? "",
                HairColorRgba ?? "",
                HairItem ?? "",
                BeardItem ?? "",
                RightItem ?? "",
                LeftItem ?? "",
                LeftItemVariant.ToString(),
                ChestItem ?? "",
                LegItem ?? "",
                HelmetItem ?? "",
                ShoulderItem ?? "",
                ShoulderItemVariant.ToString(),
                UtilityItem ?? "",
                TrinketItem ?? "",
                LeftBackItem ?? "",
                LeftBackItemVariant.ToString(),
                RightBackItem ?? "",
            });
        }
    }
}
