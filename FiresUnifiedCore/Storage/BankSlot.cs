namespace FiresCore.Storage
{
    // One banked stack for a player. Field names match Marketplace's DB.BankSlot exactly so the "Bank"
    // collection in an existing DB.db reads unchanged. Prefab is the item's stable hash code, not its name.
    public class BankSlot
    {
        public int _id { get; set; }
        public string Owner { get; set; }
        public int Prefab { get; set; }
        public int Amount { get; set; }
    }
}
