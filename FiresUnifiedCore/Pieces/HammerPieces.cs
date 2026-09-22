using UnityEngine;

namespace FiresCore.Pieces
{
    // Hammer registration shared by the Fires mods that add their own build pieces from ObjectDB.Awake.
    public static class HammerPieces
    {
        private const string HammerItemName = "Hammer";

        // ObjectDB.Awake also runs for the main menu's ObjectDB, which sits on FejdStartup; pieces belong on the
        // in-game one.
        public static bool IsMainMenuObjectDB(ObjectDB objectDB) => objectDB != null && objectDB.GetComponent<FejdStartup>() != null;

        // The Hammer's piece table with destroyed entries removed, or null while ObjectDB has no Hammer yet.
        public static PieceTable HammerTable()
        {
            var hammer = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(HammerItemName) : null;
            var table = hammer != null ? hammer.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces : null;
            if (table == null) return null;
            table.m_pieces.RemoveAll(piece => piece == null);
            return table;
        }

        // Adds the prefab to the table unless it is already listed. True when it was added.
        public static bool AddToTable(PieceTable table, GameObject prefab)
        {
            if (table == null || prefab == null || table.m_pieces.Contains(prefab)) return false;
            table.m_pieces.Add(prefab);
            return true;
        }

        public static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null || layer < 0) return;
            root.layer = layer;
            foreach (Transform child in root.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
