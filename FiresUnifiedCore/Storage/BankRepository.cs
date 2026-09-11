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

        // Returns true only if the amount was actually persisted. The caller (ServerDeposit) MUST refund the
        // player when this is false — otherwise a swallowed storage failure (e.g. VaultDatabase never
        // Configure()d because a server-init step threw before it) silently eats the deposited coins.
        public static bool Deposit(string ownerUserId, int prefabHash, int amount)
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
                    return true;
                }

                existing.Amount += amount;
                bank.Update(existing);
                return true;
            }
            catch (Exception ex)
            {
                // LOUD (Error, not Warning) + config state, so a persistence failure is never invisible.
                FiresLogger.LogError($"{LogPrefix} Deposit FAILED owner='{ownerUserId}' amount={amount} " +
                    $"(VaultDatabase.IsConfigured={VaultDatabase.IsConfigured}, path='{VaultDatabase.DatabasePath}'): {ex.Message}");
                return false;
            }
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
                // A GetItems failure reads back as a zero balance in the banker UI, so make it loud + include
                // whether the vault was ever configured (the usual root cause when the whole DB path is dead).
                FiresLogger.LogError($"{LogPrefix} GetItems FAILED owner='{ownerUserId}' " +
                    $"(VaultDatabase.IsConfigured={VaultDatabase.IsConfigured}, path='{VaultDatabase.DatabasePath}'): {ex.Message}");
                return new Dictionary<int, int>();
            }
        }
    }
}
