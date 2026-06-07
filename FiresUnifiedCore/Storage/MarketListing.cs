namespace FiresCore.Storage
{
    // One player-to-player marketplace (auction) listing in the "Marketplace" collection. Field names and
    // defaults match Marketplace's DB.MarketSlot exactly. Price is per-unit (total = Count * Price).
    public class MarketListing
    {
        public int _id { get; set; }
        public string ItemPrefab { get; set; }
        public int Count { get; set; }
        public int Price { get; set; }
        public string SellerName { get; set; }
        public string SellerUserID { get; set; }
        public ItemCategory ItemCategory { get; set; }
        public int Quality { get; set; }
        public int Variant { get; set; }
        public string CrafterName { get; set; } = "";
        public long CrafterID { get; set; }
        public string CUSTOMdata { get; set; } = "{}";
        public byte DurabilityPercent { get; set; }
        public uint TimeStamp { get; set; }
        public string Currency { get; set; } = "Coins";
    }
}
