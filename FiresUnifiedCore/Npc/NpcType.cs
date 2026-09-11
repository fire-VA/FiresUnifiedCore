namespace FiresCore.Npc
{
    // The kind of server NPC, selecting which interaction it opens. Each value maps to its own config
    // folder and UI tab in the consuming mod. Explicit values are persisted (stored as the integer), so
    // they must not be reordered.
    public enum NpcType
    {
        QuestNpc = 0,
        InfoNpc = 1,
        DialogueNpc = 2,
        Trader = 3,
        Banker = 4,
        Gambler = 5,
        Marketplace = 6,
        Achievement = 7,
        Leaderboard = 8,
        Mail = 9,
        Transmog = 10,
        Teleporter = 11,
        Buffer = 12,
        // No role: decorative NPC (dress-up / hammer templates). Placed NPCs start here until an
        // admin assigns a role in the book. Appended value — stored ints must never be reordered.
        None = 13,
        // kg Feedback NPC: [E] opens a text prompt that posts to the server's feedback webhook
        // (same sink as the /feedback command). Appended value — never reorder.
        Feedback = 14,
    }
}
