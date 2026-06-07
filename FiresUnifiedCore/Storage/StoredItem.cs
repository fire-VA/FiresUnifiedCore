namespace FiresCore.Storage
{
    // A serialized item payload — used as the element type of MailEntry.Attachments/Links, and as the
    // shape exchanged for marketplace transactions. Field names match Marketplace's ClientMarketSendData
    // exactly so nested arrays inside an existing DB.db deserialize unchanged.
    public class StoredItem
    {
        public string ItemPrefab;
        public int Count;
        public int Price;
        public string SellerName;
        public ItemCategory ItemCategory;
        public int Quality;
        public int Variant;
        public string CUSTOMdata = "{}";
        public string CrafterName = "";
        public long CrafterID;
        public byte DurabilityPercent;
        public string Currency;
    }
}
