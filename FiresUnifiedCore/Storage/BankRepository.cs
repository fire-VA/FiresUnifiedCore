using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    // Banker storage over the "Bank" collection. Operations mirror Marketplace's DB.cs: one row per
    // (owner, prefab) merged on deposit, partial withdraw that deletes the row when it hits zero.
    // prefabHash is the item's stable hash code (NOT the prefab name) — match Marketplace's keying.
    public static class BankRepository
    {
        private const string LogPrefix = "[VaultBank]";

        public static void Deposit(string ownerUserId, int prefabHash, int amount)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var bank = db.GetCollection<BankSlot>(VaultDatabase.BankCollection, BsonAutoId.ObjectId);
                bank.EnsureIndex(x => x.Owner);

                var existing = bank.FindOne(s => s.Owner == ownerUserId && s.Prefab == prefabHash);
                if (existing == null)
                {
                    bank.Insert(new BankSlot { Owner = ownerUserId, Prefab = prefabHash, Amount = amount });
                    return;
                }

                existing.Amount += amount;
                bank.Update(existing);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Deposit failed: {ex.Message}"); }
        }

        public static int Withdraw(string ownerUserId, int prefabHash, int amount)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var bank = db.GetCollection<BankSlot>(VaultDatabase.BankCollection, BsonAutoId.ObjectId);
                bank.EnsureIndex(x => x.Owner);

                var existing = bank.FindOne(s => s.Owner == ownerUserId && s.Prefab == prefabHash);
                if (existing == null) return 0;

                int withdrawn = Math.Min(existing.Amount, amount);
                existing.Amount -= withdrawn;
                if (existing.Amount <= 0) bank.Delete(existing._id);
                else bank.Update(existing);
                return withdrawn;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Withdraw failed: {ex.Message}"); return 0; }
        }

        public static Dictionary<int, int> GetItems(string ownerUserId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<BankSlot>(VaultDatabase.BankCollection, BsonAutoId.ObjectId)
                    .Find(s => s.Owner == ownerUserId)
                    .ToDictionary(s => s.Prefab, s => s.Amount);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} GetItems failed: {ex.Message}");
                return new Dictionary<int, int>();
            }
        }
    }
}
