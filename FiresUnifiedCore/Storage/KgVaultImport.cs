using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;

namespace FiresCore.Storage
{
    // One-shot importer for kg.Marketplace's SavedData/DB.db — the player economy a live kg server
    // accumulated (bank items, mail inboxes, market listings, the user registry, leaderboard stats).
    // Collections, record shapes, and owner keys are kg-identical by design (VaultDatabase is a true
    // drop-in; the economy networks key by platform user id exactly like kg; LeaderboardEntry keys by
    // "{hostName}_{playerName}" like kg), so records copy verbatim into the live vault — only fresh
    // _ids are assigned. Idempotent: an import-marker row keyed by the source file's fingerprint
    // refuses accidental double-imports (which would double every bank amount).
    public static class KgVaultImport
    {
        private const string MarkerCollection = "KgImportMeta";

        public static List<string> Run(string kgDbPath, bool confirm)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(kgDbPath) || !File.Exists(kgDbPath))
            {
                lines.Add("kg DB not found: " + kgDbPath);
                return lines;
            }
            if (!VaultDatabase.IsConfigured)
            {
                lines.Add("our vault is not configured yet (no world loaded?)");
                return lines;
            }

            List<MailUser> users; List<BankSlot> bank; List<MailEntry> mails; List<MarketListing> market; List<LeaderboardEntry> board;
            using (var kgDatabase = new LiteDatabase(new ConnectionString { Filename = kgDbPath, ReadOnly = true }))
            {
                users  = SafeAll<MailUser>(kgDatabase, VaultDatabase.MailUsersCollection, lines);
                bank   = SafeAll<BankSlot>(kgDatabase, VaultDatabase.BankCollection, lines);
                mails  = SafeAll<MailEntry>(kgDatabase, VaultDatabase.MailEntriesCollection, lines);
                market = SafeAll<MarketListing>(kgDatabase, VaultDatabase.MarketplaceCollection, lines);
                board  = SafeAll<LeaderboardEntry>(kgDatabase, VaultDatabase.LeaderboardCollection, lines);
            }

            lines.Add("source: " + users.Count + " user(s), " + bank.Count + " bank slot(s) across "
                      + bank.Select(b => b.Owner ?? "").Distinct().Count() + " owner(s), "
                      + mails.Count + " mail(s), " + market.Count + " market listing(s), " + board.Count + " leaderboard row(s)");
            if (!confirm)
            {
                lines.Add("dry run only - 'fires_migrate_kg economy confirm' imports into the live vault");
                return lines;
            }

            var fileInfo = new FileInfo(kgDbPath);
            string marker = fileInfo.Length + "_" + fileInfo.LastWriteTimeUtc.Ticks;

            using (var db = VaultDatabase.Open())
            {
                var meta = db.GetCollection(MarkerCollection);
                if (meta.FindById(marker) != null)
                {
                    lines.Add("SKIPPED: this DB.db (same size+timestamp) was already imported - re-running would double bank amounts. Delete its '" + MarkerCollection + "' row in vault.db to force.");
                    return lines;
                }

                // Users: insert-if-absent by UserID (platform id).
                var usersCol = db.GetCollection<MailUser>(VaultDatabase.MailUsersCollection, BsonAutoId.ObjectId);
                var knownUsers = new HashSet<string>(usersCol.FindAll().Select(x => x.UserID ?? ""), StringComparer.Ordinal);
                int nextUserId = NextId(usersCol.FindAll().Select(x => x._id));
                int usersInserted = 0, usersSkipped = 0;
                foreach (var user in users)
                {
                    if (knownUsers.Contains(user.UserID ?? "")) { usersSkipped++; continue; }
                    user._id = nextUserId++;
                    usersCol.Insert(user);
                    usersInserted++;
                }

                // Bank: merge by (Owner, Prefab) — amounts SUM so nothing a player stored is lost.
                var bankCol = db.GetCollection<BankSlot>(VaultDatabase.BankCollection, BsonAutoId.ObjectId);
                var existingBank = bankCol.FindAll().ToList();
                var bankByKey = new Dictionary<string, BankSlot>(StringComparer.Ordinal);
                foreach (var slot in existingBank) bankByKey[(slot.Owner ?? "") + "\u0001" + slot.Prefab] = slot;
                int nextBankId = NextId(existingBank.Select(x => x._id));
                int bankInserted = 0, bankMerged = 0;
                foreach (var slot in bank)
                {
                    string key = (slot.Owner ?? "") + "\u0001" + slot.Prefab;
                    if (bankByKey.TryGetValue(key, out var have))
                    {
                        have.Amount += slot.Amount;
                        bankCol.Update(have);
                        bankMerged++;
                    }
                    else
                    {
                        slot._id = nextBankId++;
                        bankCol.Insert(slot);
                        bankByKey[key] = slot;
                        bankInserted++;
                    }
                }

                // Mails + market listings: plain inserts with fresh sequential ids.
                var mailCol = db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId);
                int nextMailId = NextId(mailCol.FindAll().Select(x => x._id));
                foreach (var mail in mails) { mail._id = nextMailId++; mailCol.Insert(mail); }

                var marketCol = db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId);
                int nextListingId = NextId(marketCol.FindAll().Select(x => x._id));
                foreach (var listing in market) { listing._id = nextListingId++; marketCol.Insert(listing); }

                // Leaderboard: insert-if-absent by Owner — rows already earned on OUR side stay authoritative.
                var boardCol = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                var knownOwners = new HashSet<string>(boardCol.FindAll().Select(x => x.Owner ?? ""), StringComparer.Ordinal);
                int nextBoardId = NextId(boardCol.FindAll().Select(x => x._id));
                int boardInserted = 0, boardSkipped = 0;
                foreach (var entry in board)
                {
                    if (knownOwners.Contains(entry.Owner ?? "")) { boardSkipped++; continue; }
                    entry._id = nextBoardId++;
                    entry.Season = 0; // live row of the current season
                    boardCol.Insert(entry);
                    boardInserted++;
                }

                var markerDoc = new BsonDocument();
                markerDoc["_id"] = marker;
                markerDoc["when"] = DateTime.UtcNow.ToString("o");
                markerDoc["users"] = usersInserted;
                markerDoc["bank"] = bankInserted + bankMerged;
                markerDoc["mails"] = mails.Count;
                markerDoc["market"] = market.Count;
                markerDoc["leaderboard"] = boardInserted;
                meta.Insert(markerDoc);

                lines.Add("imported: users +" + usersInserted + " (" + usersSkipped + " already known), bank +" + bankInserted + " new slot(s), " + bankMerged + " merged, "
                          + "mails +" + mails.Count + ", market +" + market.Count + ", leaderboard +" + boardInserted + " (" + boardSkipped + " kept ours)");
                lines.Add("owner keys are platform ids on both sides - players keep their bank/mail/listings across the swap with no relinking.");
            }
            return lines;
        }

        private static List<T> SafeAll<T>(LiteDatabase db, string collectionName, List<string> lines)
        {
            try { return db.GetCollection<T>(collectionName, BsonAutoId.ObjectId).FindAll().ToList(); }
            catch (Exception ex)
            {
                lines.Add("read '" + collectionName + "' failed: " + ex.Message);
                return new List<T>();
            }
        }

        private static int NextId(IEnumerable<int> ids)
        {
            int max = 0;
            foreach (var i in ids) if (i > max) max = i;
            return max + 1;
        }
    }
}
