namespace FiresCore.Help
{
    // One compendium entry. Mods register these; discovered entries are injected into the player's vanilla
    // known-texts so they show in the inventory Texts tab. The display Topic is clean and groups a mod's
    // entries together — no sort-order prefix bleeds into the visible title (the bug in the pre-Core version,
    // where the dictionary key was "! [VA] 000 - Title" and vanilla TextsDialog shows the key verbatim).
    public sealed class CompendiumEntry
    {
        // Short mod label used to group + cluster this mod's entries in the alphabetically-sorted Texts list
        // (e.g. "Verdant"). Stamped by the registering mod's facade, not by content authors.
        public string ModLabel;

        public string Key;
        public string Category;
        public string Title;
        public string Content;
        public string DiscoveryKey;

        // Retained for API completeness and any future custom renderer. NOT honored by the vanilla Texts tab,
        // which sorts strictly alphabetically by topic.
        public int SortOrder;

        // The string shown as the entry's heading in the vanilla Texts tab. Vanilla uses the known-texts key
        // as both identity and visible topic and sorts alphabetically, so the label/category lead keeps a
        // mod's entries clustered and readable.
        public string Topic => $"{ModLabel} · {Category} — {Title}";

        // The body text shown when the entry is selected.
        public string Body => $"<color=orange>{Category}</color>\n\n{Content}";
    }
}
