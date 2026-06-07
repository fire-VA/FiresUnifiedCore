using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    // Mail storage over the "Users" and "Mails" collections. Operations mirror Marketplace's DB.cs:
    // users are upserted by UserID, mail is owned by the recipient's platform id. There is no server-side
    // expiry purge (Marketplace expires client-side only) — mail lives until explicitly removed.
    public static class MailRepository
    {
        private const string LogPrefix = "[VaultMail]";

        public static List<MailUser> GetAllUsers()
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<MailUser>(VaultDatabase.MailUsersCollection, BsonAutoId.ObjectId).FindAll().ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetAllUsers failed: {ex.Message}"); return new List<MailUser>(); }
        }

        public static void UpsertUser(MailUser user)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var users = db.GetCollection<MailUser>(VaultDatabase.MailUsersCollection, BsonAutoId.ObjectId);
                users.EnsureIndex(x => x.UserID);

                var existing = users.FindOne(u => u.UserID == user.UserID);
                if (existing != null)
                {
                    user._id = existing._id;
                    users.Update(user);
                    return;
                }
                users.Insert(user);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} UpsertUser failed: {ex.Message}"); }
        }

        public static void AddMail(MailEntry mail)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var mails = db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId);
                mails.EnsureIndex(x => x.Owner);
                mails.Insert(mail);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} AddMail failed: {ex.Message}"); }
        }

        public static List<MailEntry> GetUserMails(string ownerUserId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId)
                    .Find(e => e.Owner == ownerUserId).ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetUserMails failed: {ex.Message}"); return new List<MailEntry>(); }
        }

        public static Dictionary<string, List<MailEntry>> GetAllMailsByOwner()
        {
            var byOwner = new Dictionary<string, List<MailEntry>>();
            try
            {
                using var db = VaultDatabase.Open();
                foreach (var mail in db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId).FindAll())
                {
                    if (!byOwner.TryGetValue(mail.Owner, out var list))
                        byOwner[mail.Owner] = list = new List<MailEntry>();
                    list.Add(mail);
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetAllMailsByOwner failed: {ex.Message}"); }
            return byOwner;
        }

        public static int GetTotalMailCount(string ownerUserId) => CountMail(ownerUserId, unreadOnly: false);

        public static int GetUnreadMailCount(string ownerUserId) => CountMail(ownerUserId, unreadOnly: true);

        public static MailEntry GetMail(string ownerUserId, int mailId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId)
                    .FindOne(e => e.Owner == ownerUserId && e._id == mailId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetMail failed: {ex.Message}"); return null; }
        }

        public static void MarkRead(string ownerUserId, int mailId) =>
            MutateMail(ownerUserId, mailId, m => m.WasRead = true, nameof(MarkRead));

        public static void MarkAttachmentsTaken(string ownerUserId, int mailId) =>
            MutateMail(ownerUserId, mailId, m => m.AttachmentsTaken = true, nameof(MarkAttachmentsTaken));

        public static void RemoveMail(string ownerUserId, int mailId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var mails = db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId);
                var existing = mails.FindOne(e => e.Owner == ownerUserId && e._id == mailId);
                if (existing != null) mails.Delete(existing._id);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} RemoveMail failed: {ex.Message}"); }
        }

        private static int CountMail(string ownerUserId, bool unreadOnly)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var mails = db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId);
                return unreadOnly
                    ? mails.Count(e => e.Owner == ownerUserId && !e.WasRead)
                    : mails.Count(e => e.Owner == ownerUserId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} CountMail failed: {ex.Message}"); return 0; }
        }

        private static void MutateMail(string ownerUserId, int mailId, Action<MailEntry> mutate, string opName)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var mails = db.GetCollection<MailEntry>(VaultDatabase.MailEntriesCollection, BsonAutoId.ObjectId);
                var existing = mails.FindOne(e => e.Owner == ownerUserId && e._id == mailId);
                if (existing == null) return;
                mutate(existing);
                mails.Update(existing);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} {opName} failed: {ex.Message}"); }
        }
    }
}
