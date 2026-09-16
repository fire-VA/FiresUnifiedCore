using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.NpcMode
{
    /// <summary>
    /// Diagnoses "The Object you want to instantiate is null" from Player.PlacePiece for quest-NPC pieces by logging
    /// every prefab field on the Piece, found by reflection so it works across game versions. The exception is not
    /// swallowed, since placement genuinely fails; the log points at the bundle field to fix.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    internal static class StaticNpcPlacePieceFinalizer
    {
        private const string NullInstantiateMarker = "The Object you want to instantiate is null";

        private static readonly string[] WhitelistedPrefixes = new[]
        {
            "StaticNpc",
            "BaseNpc",
            "CompanionNpc",
        };

        /// <summary>
        /// Candidate <see cref="Piece"/> field names inspected by <see cref="Prefix"/>
        /// for the pre-placement snapshot. The exception-time reflection sweep in
        /// <see cref="Finalizer"/> covers every <see cref="UnityEngine.Object"/> field
        /// automatically and is what actually pinpoints the null.
        /// </summary>

        [HarmonyPrefix]
        private static void Prefix(Piece piece)
        {
            if (piece == null) return;
            string pieceName = piece.name ?? string.Empty;
            bool whitelisted = false;
            for (int i = 0; i < WhitelistedPrefixes.Length; i++)
                if (pieceName.StartsWith(WhitelistedPrefixes[i], StringComparison.OrdinalIgnoreCase))
                { whitelisted = true; break; }
            if (!whitelisted) return;

            Debug.Log($"[FiresRPGmaker] PlacePiece prefix: about to place '{pieceName}'. " +
                      $"gameObject={(piece.gameObject != null ? "ok" : "<NULL>")} " +
                      $"m_placeEffect={(piece.m_placeEffect != null ? "ok" : "<NULL>")} " +
                      $"m_resources.Length={(piece.m_resources != null ? piece.m_resources.Length : -1)}");
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Piece piece, Exception __exception)
        {
            if (__exception == null) return null;
            if (!(__exception is ArgumentException)) return __exception;
            if (string.IsNullOrEmpty(__exception.Message) ||
                __exception.Message.IndexOf(NullInstantiateMarker, StringComparison.Ordinal) < 0)
                return __exception;

            string pieceName = piece != null ? piece.name : null;
            if (string.IsNullOrEmpty(pieceName)) return __exception;

            bool whitelisted = false;
            for (int i = 0; i < WhitelistedPrefixes.Length; i++)
            {
                if (pieceName.StartsWith(WhitelistedPrefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    whitelisted = true;
                    break;
                }
            }

            if (!whitelisted) return __exception; // unrelated mod, let it bubble

            // Build the diagnostic dump - logged ONCE per placement attempt so the log
            // isn't swamped even if someone spam-clicks the hammer.
            var sb = new StringBuilder();
            sb.AppendLine($"[FiresRPGmaker] PlacePiece Instantiate(null) on piece '{pieceName}'. Diagnostic dump:");
            try
            {
                sb.AppendLine($"  piece.gameObject         : {SafeRefDesc(piece != null ? piece.gameObject : null)}");
                sb.AppendLine($"  piece.m_enabled          : {(piece != null ? piece.m_enabled.ToString() : "<piece null>")}");
                sb.AppendLine($"  piece.m_category         : {(piece != null ? piece.m_category.ToString() : "<piece null>")}");

                // Sweep every instance field on Piece whose declared type inherits from
                // UnityEngine.Object -> ANY of them could be the null Instantiate target.
                // This is version-proof: new fields in future Valheim builds show up
                // automatically without us having to update an allowlist.
                var pieceType = typeof(Piece);
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var fieldInfo in pieceType.GetFields(flags))
                {
                    var fieldType = fieldInfo.FieldType;
                    bool isObjectRef = typeof(UnityEngine.Object).IsAssignableFrom(fieldType);
                    bool isObjectArray = fieldType.IsArray && typeof(UnityEngine.Object).IsAssignableFrom(fieldType.GetElementType());
                    if (!isObjectRef && !isObjectArray) continue;

                    object val = piece != null ? fieldInfo.GetValue(piece) : null;
                    if (isObjectArray)
                    {
                        var arr = val as Array;
                        if (arr == null)
                        {
                            sb.AppendLine($"  {fieldInfo.Name,-30} : <null array>");
                            continue;
                        }
                        int nullCount = 0;
                        for (int k = 0; k < arr.Length; k++)
                            if (arr.GetValue(k) == null) nullCount++;
                        sb.AppendLine($"  {fieldInfo.Name,-30} : {arr.Length} entries ({nullCount} null)");
                    }
                    else
                    {
                        sb.AppendLine($"  {fieldInfo.Name,-30} : {SafeRefDesc(val as UnityEngine.Object)}");
                    }
                }

                // Also drill into m_placeEffect's internal prefab array, which has its
                // own EffectData.m_prefab entries that can independently be null.
                var placeEffectField = pieceType.GetField("m_placeEffect",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (placeEffectField != null && piece != null)
                {
                    var placeEffect = placeEffectField.GetValue(piece);
                    if (placeEffect != null)
                    {
                        var effectPrefabsField = placeEffect.GetType().GetField("m_effectPrefabs", flags);
                        if (effectPrefabsField != null)
                        {
                            var arr = effectPrefabsField.GetValue(placeEffect) as System.Collections.IList;
                            if (arr == null)
                            {
                                sb.AppendLine("  m_placeEffect.m_effectPrefabs  : <null> (safe ? EffectList.Create bails)");
                            }
                            else
                            {
                                sb.AppendLine($"  m_placeEffect.m_effectPrefabs  : {arr.Count} entries");
                                for (int j = 0; j < arr.Count; j++)
                                {
                                    var entry = arr[j];
                                    if (entry == null)
                                    {
                                        sb.AppendLine($"    [{j}] <null entry>");
                                        continue;
                                    }
                                    var prefabField = entry.GetType().GetField("m_prefab", flags);
                                    object prefabVal = prefabField?.GetValue(entry);
                                    sb.AppendLine($"    [{j}].m_prefab = {SafeRefDesc(prefabVal as UnityEngine.Object)}");
                                }
                            }
                        }
                    }
                }

                // Resource icons are another recurring null-Instantiate culprit in vanilla
                // - Player.ConsumeResources calls Instantiate on each requirement's
                // m_resItem.m_itemData.m_shared.m_icons[0].
                if (piece != null && piece.m_resources != null)
                {
                    for (int resourceIndex = 0; resourceIndex < piece.m_resources.Length; resourceIndex++)
                    {
                        var req = piece.m_resources[resourceIndex];
                        if (req == null)
                        {
                            sb.AppendLine($"  m_resources[{resourceIndex}] <null requirement>");
                            continue;
                        }
                        var resItem = req.m_resItem;
                        sb.AppendLine($"  m_resources[{resourceIndex}] resItem={SafeRefDesc(resItem)} amount={req.m_amount}");
                    }
                }
            }
            catch (Exception dumpEx)
            {
                sb.AppendLine($"  <diagnostic dump failed: {dumpEx.Message}>");
            }

            Debug.LogWarning(sb.ToString());
            return __exception; // re-throw so the underlying bug stays visible
        }

        private static string SafeRefDesc(UnityEngine.Object obj)
        {
            if (obj == null) return "<null>";
            try { return $"'{obj.name}' (type {obj.GetType().Name})"; }
            catch { return "<unreadable>"; }
        }
    }
}

