using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FiresCore.Pieces
{
    // Vanilla adds every WearNTear to its zone heightmap's m_clearConnectedWearNTearCache in Start and removes it in
    // OnDestroy. Each += or -= copies the zone's whole invocation list, so loading or unloading n pieces in one zone costs
    // n² in time and allocations: in a heavily built world it was 27% of all allocations and the core of the
    // object-creation stalls, on every machine that loads the pieces. Here the pieces sit in one list per heightmap, and
    // each heightmap gets a single subscriber that clears them all, so Heightmap.Regenerate fires the event as before.
    public static class ConnectedPieces
    {
        private sealed class Zone
        {
            public readonly Heightmap Heightmap;
            public readonly int HeightmapId;
            public readonly Dictionary<int, WearNTear> Pieces = new Dictionary<int, WearNTear>();
            public readonly Action ClearAll;

            public Zone(Heightmap heightmap, int heightmapId)
            {
                Heightmap = heightmap;
                HeightmapId = heightmapId;
                ClearAll = ClearPieces;
            }

            private void ClearPieces()
            {
                s_dispatch.Clear();
                foreach (WearNTear piece in Pieces.Values) s_dispatch.Add(piece);
                foreach (WearNTear piece in s_dispatch)
                    if (piece != null) ClearCachedSupport(piece);
                s_dispatch.Clear();
            }
        }

        private static readonly Dictionary<int, Zone> s_zonesByHeightmap = new Dictionary<int, Zone>();
        private static readonly Dictionary<int, Zone> s_zonesByPiece = new Dictionary<int, Zone>();
        private static readonly List<WearNTear> s_dispatch = new List<WearNTear>();
        private static readonly Action<WearNTear> ClearCachedSupport =
            AccessTools.MethodDelegate<Action<WearNTear>>(AccessTools.Method(typeof(WearNTear), "ClearCachedSupport"));

        // The event's own accessors: the publicized game DLL also exposes its backing field under the same name.
        private static readonly Action<Heightmap, Action> Subscribe =
            AccessTools.MethodDelegate<Action<Heightmap, Action>>(AccessTools.Method(typeof(Heightmap), "add_m_clearConnectedWearNTearCache"));
        private static readonly Action<Heightmap, Action> Unsubscribe =
            AccessTools.MethodDelegate<Action<Heightmap, Action>>(AccessTools.Method(typeof(Heightmap), "remove_m_clearConnectedWearNTearCache"));

        // Stands in for vanilla's `heightmap.m_clearConnectedWearNTearCache += ClearCachedSupport` in WearNTear.Start.
        public static void Add(Heightmap heightmap, WearNTear piece)
        {
            int pieceId = piece.GetInstanceID();
            if (s_zonesByPiece.ContainsKey(pieceId)) return;
            int heightmapId = heightmap.GetInstanceID();
            if (!s_zonesByHeightmap.TryGetValue(heightmapId, out Zone zone))
            {
                zone = new Zone(heightmap, heightmapId);
                s_zonesByHeightmap[heightmapId] = zone;
                Subscribe(heightmap, zone.ClearAll);
            }
            zone.Pieces[pieceId] = piece;
            s_zonesByPiece[pieceId] = zone;
        }

        // Runs after every WearNTear.OnDestroy, keyed by the piece: vanilla skips its -= once the heightmap is gone, and a
        // zone unloaded before its pieces must still let them go. Vanilla's own -= stays; with one subscriber per heightmap
        // it finds nothing to remove and costs nothing.
        public static void Remove(WearNTear piece)
        {
            int pieceId = piece.GetInstanceID();
            if (!s_zonesByPiece.TryGetValue(pieceId, out Zone zone)) return;
            s_zonesByPiece.Remove(pieceId);
            zone.Pieces.Remove(pieceId);
            if (zone.Pieces.Count > 0) return;
            Unsubscribe(zone.Heightmap, zone.ClearAll);
            s_zonesByHeightmap.Remove(zone.HeightmapId);
        }

        // Rewrites Start's `heightmap.m_clearConnectedWearNTearCache += new Action(this.ClearCachedSupport)` as
        // `ConnectedPieces.Add(heightmap, this)`. Any other shape leaves Start as vanilla wrote it, and then no piece is
        // ever registered here, so Remove finds nothing and vanilla's += and -= keep working together.
        [HarmonyPatch(typeof(WearNTear), "Start")]
        internal static class WearNTear_Start_ConnectedPieces
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);
                for (int i = 3; i < code.Count; i++)
                {
                    if (!(code[i].operand is MethodInfo called) || called.Name != "add_m_clearConnectedWearNTearCache"
                        || called.DeclaringType != typeof(Heightmap))
                        continue;
                    bool expected = code[i - 1].opcode == OpCodes.Newobj
                        && code[i - 2].opcode == OpCodes.Ldftn && code[i - 2].operand is MethodInfo target && target.Name == "ClearCachedSupport"
                        && code[i - 3].opcode == OpCodes.Ldarg_0;
                    if (!expected) break;
                    var call = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ConnectedPieces), nameof(Add))).MoveLabelsFrom(code[i]);
                    call.labels.AddRange(code[i - 1].ExtractLabels());
                    call.labels.AddRange(code[i - 2].ExtractLabels());
                    code[i] = call;
                    code.RemoveRange(i - 2, 2);
                    return code;
                }
                FiresCore.Logging.FiresLogger.LogWarning(
                    "[ConnectedPieces] WearNTear.Start is not the shape this expects; pieces keep vanilla's per-piece subscription.");
                return code;
            }
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        internal static class WearNTear_OnDestroy_ConnectedPieces
        {
            private static void Postfix(WearNTear __instance) => Remove(__instance);
        }
    }
}
