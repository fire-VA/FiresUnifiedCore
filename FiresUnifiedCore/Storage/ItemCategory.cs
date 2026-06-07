namespace FiresCore.Storage
{
    // Item category for marketplace listings. Member NAMES must match Marketplace's
    // ItemData_ItemCategory exactly — LiteDB serializes enums by name (EnumAsInteger is left at its
    // default of false), so matching names is what keeps an existing DB.db drop-in readable.
    public enum ItemCategory
    {
        ALL,
        WEAPONS,
        ARMOR,
        CONSUMABLE,
        TOOLS,
        RESOURCES,
    }
}
