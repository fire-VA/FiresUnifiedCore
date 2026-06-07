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
    }
}
