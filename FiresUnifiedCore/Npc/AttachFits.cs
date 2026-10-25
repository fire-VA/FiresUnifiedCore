using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// The per-model
    /// correction table for hair / beard / helmet on swapped bodies: model prefab -> part -> local position / rotation / scale of the
    /// attached item under its head joint (the computed fit). Server-authoritative: the server keeps
    /// BepInEx\cache\FiresUnifiedCore\attach_fits.txt (one line per entry), admins save through an RPC, every client gets the whole
    /// table on connect and after each change. Bodies with no entry keep the computed fit. Add-only API.
    /// </summary>
    [HarmonyPatch]   // required so the RPC / OnSpawned / UpdateVisuals postfixes are applied
    public static class AttachFits
    {
        public enum Part { Hair, Beard, Helmet }
        public struct Fit { public Vector3 Pos; public Vector3 Euler; public Vector3 Scale; }

        private const string SaveRpc = "FiresCore_AttachFitSave", TableRpc = "FiresCore_AttachFitTable", AskRpc = "FiresCore_AttachFitAsk";
        private static readonly Dictionary<string, Fit> s_table = new Dictionary<string, Fit>(StringComparer.OrdinalIgnoreCase);
        private static ZRoutedRpc s_rpcOn;
        private static bool s_loaded;

        private static string Key(string model, Part part) => model + "|" + part;
        private static string FilePath => Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "attach_fits.txt");

        /// <summary>The VisEquipment the fit tool is editing (its saved fit is not re-applied over the gizmo).</summary>
        public static VisEquipment EditingVis;

        public static bool TryGet(string model, Part part, out Fit fit) => s_table.TryGetValue(Key(model ?? "", part), out fit);

        /// <summary>Saves (or with reset = true removes) one entry: on the server directly, from a client through the server (admins only).</summary>
        public static void Save(string model, Part part, Fit fit, bool reset)
        {
            if (string.IsNullOrEmpty(model)) return;
            var pkg = new ZPackage();
            pkg.Write(model); pkg.Write((int)part); pkg.Write(reset);
            WriteFit(pkg, fit);
            if (ZNet.instance == null || ZNet.instance.IsServer()) { ApplySave(pkg, null); return; }
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), SaveRpc, pkg);
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        private static void RegisterRpcs()
        {
            var cur = ZRoutedRpc.instance;
            if (cur == null || ReferenceEquals(s_rpcOn, cur)) return;
            try
            {
                cur.Register<ZPackage>(SaveRpc, RPC_Save);
                cur.Register<ZPackage>(TableRpc, RPC_Table);
                cur.Register(AskRpc, RPC_Ask);
                s_rpcOn = cur;
            }
            catch (Exception ex) { Debug.LogWarning("[AttachFits] RPC register failed: " + ex.Message); }
        }

        // client: ask the server for the table once in a world
        [HarmonyPatch(typeof(Player), "OnSpawned")]
        [HarmonyPostfix]
        private static void AskOnSpawn(Player __instance)
        {
            if (__instance != Player.m_localPlayer || ZNet.instance == null) return;
            if (ZNet.instance.IsServer()) { LoadIfNeeded(); return; }
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc);
        }

        private static void RPC_Ask(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            LoadIfNeeded();
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, TableRpc, TablePackage());
        }

        private static void RPC_Save(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var peer = ZNet.instance.GetPeer(sender);
            bool admin = peer == null || ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
            if (!admin) { Debug.LogWarning("[AttachFits] save refused: the sender is not an admin"); return; }
            ApplySave(pkg, sender);
        }

        private static void ApplySave(ZPackage pkg, long? sender)
        {
            LoadIfNeeded();
            pkg.SetPos(0);
            string model = pkg.ReadString(); var part = (Part)pkg.ReadInt(); bool reset = pkg.ReadBool(); var fit = ReadFit(pkg);
            // The table is a line file keyed "model|part": a model name with '|' or a line break would corrupt it
            if (string.IsNullOrEmpty(model) || model.Length > MaxModelNameLength || model.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0 || !Enum.IsDefined(typeof(Part), part))
            { Debug.LogWarning($"[AttachFits] save refused: bad model '{model}' or part {(int)part}"); return; }
            if (reset) s_table.Remove(Key(model, part)); else s_table[Key(model, part)] = fit;
            WriteFile();
            Debug.Log($"[AttachFits] {(reset ? "reset" : "saved")} {part} fit for '{model}'");
            if (ZNet.instance != null && ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, TableRpc, TablePackage());
            ReapplyAll();
        }

        private const int MaxModelNameLength = 128;
        private const int MaxTableEntries = 4096;
        private static void RPC_Table(long sender, ZPackage pkg)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) { ReapplyAll(); return; }   // the server's own table is the source
            if (!RpcGuard.SenderIsServer(sender)) { RpcGuard.Refuse("AttachFits_Table", sender, "only the server sends the fit table"); return; }
            int n = pkg.ReadInt();
            if (n < 0 || n > MaxTableEntries) { RpcGuard.Refuse("AttachFits_Table", sender, $"{n} entries (max {MaxTableEntries})"); return; }
            s_table.Clear();
            for (int i = 0; i < n; i++) { string k = pkg.ReadString(); s_table[k] = ReadFit(pkg); }
            s_loaded = true;
            ReapplyAll();
        }

        private static ZPackage TablePackage()
        {
            var pkg = new ZPackage();
            pkg.Write(s_table.Count);
            foreach (var kv in s_table) { pkg.Write(kv.Key); WriteFit(pkg, kv.Value); }
            return pkg;
        }

        private static void WriteFit(ZPackage p, Fit f) { p.Write(f.Pos); p.Write(f.Euler); p.Write(f.Scale); }
        private static Fit ReadFit(ZPackage p) => new Fit { Pos = p.ReadVector3(), Euler = p.ReadVector3(), Scale = p.ReadVector3() };

        private static void LoadIfNeeded()
        {
            if (s_loaded) return;
            s_loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('|');
                    if (p.Length != 11 || !Enum.TryParse(p[1], true, out Part part)) continue;
                    float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                    s_table[Key(p[0], part)] = new Fit { Pos = new Vector3(F(2), F(3), F(4)), Euler = new Vector3(F(5), F(6), F(7)), Scale = new Vector3(F(8), F(9), F(10)) };
                }
            }
            catch (Exception ex) { Debug.LogWarning("[AttachFits] table not read: " + ex.Message); }
        }

        private static void WriteFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var kv in s_table)
                {
                    var f = kv.Value; string S(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
                    lines.Add($"{kv.Key}|{S(f.Pos.x)}|{S(f.Pos.y)}|{S(f.Pos.z)}|{S(f.Euler.x)}|{S(f.Euler.y)}|{S(f.Euler.z)}|{S(f.Scale.x)}|{S(f.Scale.y)}|{S(f.Scale.z)}");
                }
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex) { Debug.LogWarning("[AttachFits] table not saved: " + ex.Message); }
        }

        // ---- applying -------------------------------------------------------------------------------------------------------------
        private static readonly System.Reflection.FieldInfo s_hairInst = AccessTools.Field(typeof(VisEquipment), "m_hairItemInstance");
        private static readonly System.Reflection.FieldInfo s_beardInst = AccessTools.Field(typeof(VisEquipment), "m_beardItemInstance");
        private static readonly System.Reflection.FieldInfo s_helmetInst = AccessTools.Field(typeof(VisEquipment), "m_helmetItemInstance");

        /// <summary>The attached hair / beard / helmet object on this VisEquipment (null when none).</summary>
        public static GameObject Instance(VisEquipment vis, Part part)
        {
            if (vis == null) return null;
            var fi = part == Part.Hair ? s_hairInst : part == Part.Beard ? s_beardInst : s_helmetInst;
            return fi?.GetValue(vis) as GameObject;
        }

        /// <summary>Applies the saved fits to one body's attached items (swapped bodies only).</summary>
        public static void ApplyTo(VisEquipment vis)
        {
            var st = vis != null ? vis.GetComponent<BodyModelState>() : null;
            if (st == null || string.IsNullOrEmpty(st.Applied)) return;
            foreach (Part part in Enum.GetValues(typeof(Part)))
            {
                if (!TryGet(st.Applied, part, out var f)) continue;
                var go = Instance(vis, part);
                if (go == null) continue;
                var t = go.transform;
                t.localPosition = f.Pos; t.localRotation = Quaternion.Euler(f.Euler); t.localScale = f.Scale;
            }
        }

        private static void ReapplyAll()
        {
            foreach (var st in UnityEngine.Object.FindObjectsOfType<BodyModelState>())
                if (st != null) ApplyTo(st.GetComponent<VisEquipment>());
        }

        // vanilla re-creates the hair / beard / helmet object when it changes: put the saved fit on the new one
        [HarmonyPatch(typeof(VisEquipment), "UpdateVisuals")]
        [HarmonyPostfix]
        private static void AfterVisuals(VisEquipment __instance)
        {
            if (s_table.Count == 0 || ReferenceEquals(__instance, EditingVis)) return;   // the fit tool owns the one being edited
            var st = __instance.GetComponent<BodyModelState>();
            if (st == null || string.IsNullOrEmpty(st.Applied)) return;
            ApplyTo(__instance);
        }
    }
}
