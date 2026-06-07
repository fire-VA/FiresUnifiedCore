using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    // Player-to-player auction storage over the "Marketplace" collection. Operations mirror Marketplace's
    // DB.cs. Listings are indexed by ItemPrefab for category/search queries.
    public static class MarketplaceRepository
    {
        private const string LogPrefix = "[VaultMarket]";

        public static int AddListing(MarketListing listing)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var market = db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId);
                BsonValue id = market.Insert(listing);
                market.EnsureIndex(x => x.ItemPrefab);
                return id.AsInt32;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} AddListing failed: {ex.Message}"); return 0; }
        }

        public static void RemoveListing(int listingId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId).Delete(listingId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} RemoveListing failed: {ex.Message}"); }
        }

        public static List<MarketListing> GetListings()
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId).FindAll().ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetListings failed: {ex.Message}"); return new List<MarketListing>(); }
        }

        public static MarketListing GetListing(int listingId)
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId).FindById(listingId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetListing failed: {ex.Message}"); return null; }
        }

        public static void UpdateListing(MarketListing listing)
        {
            try
            {
                using var db = VaultDatabase.Open();
                db.GetCollection<MarketListing>(VaultDatabase.MarketplaceCollection, BsonAutoId.ObjectId).Update(listing);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} UpdateListing failed: {ex.Message}"); }
        }
    }
}
