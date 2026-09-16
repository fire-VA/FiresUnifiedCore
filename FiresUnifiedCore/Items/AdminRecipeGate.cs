using System;
using UnityEngine;

namespace FiresCore.Items
{
    // Shared admin gate for buildable content across the Fires family.
    //
    // The gate is an INGREDIENT, not a permission check: a gated recipe requires
    // a cheat-sword, which only an admin can produce (`genitem SwordCheat`). A
    // non-admin therefore cannot satisfy the recipe no matter what their build
    // menu happens to show, so the gate still holds when a UI-level admin check
    // is bypassed, arrives late, or fails open. Same approach already proven by
    // FiresWayshrines' Town Portal Scroll recipe.
    //
    // NOTE the ingredient IS consumed on build (vanilla has no require-without-
    // consume flag for piece requirements — m_amount 0 passes trivially and
    // gates nothing). Recover defaults to true so deconstructing hands it back.
    public static class AdminRecipeGate
    {
        // Vanilla cheat-sword prefab. Casing varies by lookup path, so try every
        // known form rather than assuming one.
        private static readonly string[] CheatSwordCandidates =
            { "SwordCheat", "Cheatsword", "cheatsword" };

        public const string GateItemName = "SwordCheat";

        // Resolves the cheat-sword ItemDrop. False means the databases aren't up
        // yet — it is a timing result, not proof the item is absent, since the
        // sword is vanilla content and always exists once ObjectDB is built.
        public static bool TryResolveGateItem(out ItemDrop gateItem)
        {
            gateItem = null;
            foreach (var candidate in CheatSwordCandidates)
            {
                GameObject go = null;
                try { go = ObjectDB.instance?.GetItemPrefab(candidate); } catch { }
                if (go == null)
                {
                    try { go = ZNetScene.instance?.GetPrefab(candidate); } catch { }
                }
                if (go == null) continue;

                var drop = go.GetComponent<ItemDrop>();
                if (drop != null)
                {
                    gateItem = drop;
                    return true;
                }
            }
            return false;
        }

        // Resolved gate item, cached so the hot path is a reference compare
        // rather than a string compare. IsGated runs across every piece in the
        // table on each PieceTable.UpdateAvailable, so it has to stay cheap.
        private static ItemDrop _cachedGateItem;

        // True when the requirement list already carries the gate item — used
        // both to keep EnsureGated idempotent and, on the FAP side, to decide
        // whether a piece is admin-only content.
        public static bool IsGated(Piece.Requirement[] requirements)
        {
            if (requirements == null || requirements.Length == 0) return false;

            if (_cachedGateItem == null) TryResolveGateItem(out _cachedGateItem);

            for (int i = 0; i < requirements.Length; i++)
            {
                var req = requirements[i];
                if (req == null || req.m_resItem == null) continue;

                // Fast path: same ItemDrop instance as the resolved gate item.
                if (_cachedGateItem != null && ReferenceEquals(req.m_resItem, _cachedGateItem))
                    return true;

                // Fallback by name — covers a rebuilt ObjectDB handing out a
                // fresh ItemDrop instance that the cache hasn't caught up to.
                string name = req.m_resItem.gameObject.name;
                for (int candidateIndex = 0; candidateIndex < CheatSwordCandidates.Length; candidateIndex++)
                {
                    if (string.Equals(name, CheatSwordCandidates[candidateIndex], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        // Drop the cached ItemDrop — call when ObjectDB is rebuilt so a stale
        // instance from the previous world can't linger.
        public static void InvalidateCache() => _cachedGateItem = null;

        // Returns the requirement list with the gate item guaranteed present.
        //
        // Returns FALSE when the gate item can't be resolved yet. Callers must
        // then SKIP applying and retry on a later pass rather than writing an
        // ungated recipe — failing closed on an admin gate is the whole point,
        // and silently shipping a free recipe is the failure mode worth avoiding.
        public static bool TryEnsureGated(Piece.Requirement[] requirements,
                                          out Piece.Requirement[] gated,
                                          int amount = 1,
                                          bool recover = true)
        {
            gated = requirements;

            if (IsGated(requirements)) return true;
            if (!TryResolveGateItem(out var gateItem)) return false;

            var source = requirements ?? new Piece.Requirement[0];
            var result = new Piece.Requirement[source.Length + 1];

            // Gate first so it reads as the headline cost in the build tooltip.
            result[0] = new Piece.Requirement
            {
                m_resItem = gateItem,
                m_amount = Mathf.Max(1, amount),
                m_amountPerLevel = 0,
                m_recover = recover
            };
            Array.Copy(source, 0, result, 1, source.Length);

            gated = result;
            return true;
        }

        // Convenience for the common case: gate a Piece in place. False means
        // nothing was written (gate item unresolved) so the caller can retry.
        public static bool TryGatePiece(Piece piece, int amount = 1, bool recover = true)
        {
            if (piece == null) return false;
            if (!TryEnsureGated(piece.m_resources, out var gated, amount, recover)) return false;
            piece.m_resources = gated;
            return true;
        }
    }
}
