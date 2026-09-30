using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using FiresCore.IO;
using FiresCore.Logging;
using HarmonyLib;
using LiteDB;

namespace FiresCore.Storage
{
    // Fire's "save with the world" (Tools\SAVE_SAFETY_AUDIT.md, fix 3). The vault (bank, mail, marketplace, guilds) commits each
    // operation at once, but a crash puts the world back to its last save, so a deposit around a crash could be duplicated or
    // lost. At every world save the server copies the vault as it stands when vanilla captures the world; once that save is
    // written, the copy is tagged with the save's number and its .fwl2 write time (the number alone repeats after a crash
    // mid-save, [worldgen]). A clean shutdown refreshes the final save's copy once the players are gone. At the next start, a
    // vault that differs from the copy of the save the world actually loaded changed after that save: the copy is put back, and
    // the vault as it was is kept beside it. Vanilla's save numbering only; nothing on a client or for a cloud-stored world.
    internal static class VaultWorldSnapshots
    {
        private const string Tag = "[VaultSnapshots] ";
        private const string FolderSuffix = ".world";
        private const string PendingPrefix = "pending-";
        private const string PreRestorePrefix = "pre-restore-";
        private const string CleanShutdownFile = "clean-shutdown.txt";
        private const string StampFormat = "yyyyMMdd-HHmmss";
        private const string LogFileInfix = "-log";
        private const char TagSeparator = '-';
        private const int SnapshotsKept = 3;
        private const int CopyBufferBytes = 64 * 1024;
        private const double BytesPerKb = 1024.0;

        private static readonly AccessTools.FieldRef<ZNet, Thread> s_saveThread = AccessTools.FieldRefAccess<ZNet, Thread>("m_saveThread");
        private static readonly object s_lock = new object();
        private static ConfigEntry<bool> s_enabled;
        private static uint s_pendingSave;
        private static bool s_pendingValid;
        private static string s_lastCompletedTag;
        private static bool s_shuttingDown;
        private static bool s_completedWhileShuttingDown;

        internal static void Register(Harmony harmony, ConfigFile config)
        {
            s_enabled = config.Bind("Vault", "Roll back with the world", true,
                "SERVER. The vault (bank, mail, marketplace, guilds) keeps a copy per world save and, after a crash, goes back to the copy " +
                "of the save the world loaded, so items can't be duplicated or lost around a crash. Off: the vault keeps every change.");
            var self = typeof(VaultWorldSnapshots);
            harmony.Patch(AccessTools.Method(typeof(ZNet), "SaveWorld"), prefix: new HarmonyMethod(self, nameof(SaveWorld_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(SaveSystem), nameof(SaveSystem.EndSave)), postfix: new HarmonyMethod(self, nameof(EndSave_Postfix)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Shutdown)),
                prefix: new HarmonyMethod(self, nameof(Shutdown_Prefix)), postfix: new HarmonyMethod(self, nameof(Shutdown_Postfix)));
        }

        private static bool Enabled => s_enabled != null && s_enabled.Value;

        private static bool OnLocalWorldServer =>
            ZNet.instance != null && ZNet.instance.IsServer() && ZNet.World != null && ZNet.World.m_fileSource == FileHelpers.FileSource.Local;

        private static bool Active => Enabled && VaultDatabase.IsConfigured && OnLocalWorldServer;

        // ------------------------------------------------------------ the start check

        // From VaultDatabase.Configure, before the path is set, so nothing can open the vault meanwhile. The world has loaded by then.
        internal static void BeforeVaultConfigured(string path)
        {
            if (!Enabled || !OnLocalWorldServer) return;
            try { RestoreIfChangedSinceSave(path); }
            catch (Exception ex) { FiresLogger.LogError($"{Tag}start check failed ({ex.Message}); the vault is left as it is."); }
        }

        private static void RestoreIfChangedSinceSave(string path)
        {
            string root = RootOf(path);
            string cleanShutdownOf = TakeCleanShutdownMark(root);
            uint number = SaveSystem.GetSaveNumber();
            string tag = SaveTag(number);
            if (tag == null)
            {
                FiresLogger.LogInfo($"{Tag}the world has no save yet; nothing to compare the vault with.");
                return;
            }
            string snapshot = Path.Combine(root, tag);
            // The save's files were whole but the server stopped before EndSave ([worldgen]'s A3b): the world kept that save, and the
            // copy taken at its capture is still pending under its number. Every save replaces the pending copy, so it is this one's.
            string pending = Path.Combine(root, PendingPrefix + number);
            if (!Directory.Exists(snapshot) && Directory.Exists(pending))
            {
                Directory.Move(pending, snapshot);
                FiresLogger.LogInfo($"{Tag}world save {number} loaded before its vault copy was tagged; its pending copy is used.");
            }
            if (!Directory.Exists(snapshot))
            {
                if (Directory.Exists(root) && Directory.GetDirectories(root).Any(IsSnapshotFolder))
                    FiresLogger.LogWarning($"{Tag}NO vault copy for the save the world loaded (save {number}, {tag}): the vault is kept as it " +
                                           "is and may hold changes the world lost.");
                else
                    FiresLogger.LogInfo($"{Tag}no vault copies yet; the first world save makes one.");
                return;
            }
            if (SameFiles(path, snapshot))
            {
                FiresLogger.LogInfo($"{Tag}the vault matches world save {number}; nothing to restore.");
                return;
            }
            // The server left cleanly on this very save, so no crash can explain the difference: someone changed the vault while it
            // was down (an admin tool, a manual fix). That change is kept ([perf]).
            if (cleanShutdownOf == tag)
            {
                FiresLogger.LogWarning($"{Tag}the vault differs from world save {number}'s copy although the server shut down cleanly on " +
                                       "that save: it was changed OUTSIDE the game and is kept as it is. The copy stays in " + snapshot + ".");
                return;
            }
            string restoredRows = RowCounts(snapshot, path);
            if (restoredRows == null)
            {
                FiresLogger.LogWarning($"{Tag}the vault copy of world save {number} can't be read: the vault is kept as it is and may hold " +
                                       "changes the world lost.");
                return;
            }
            string kept = Path.Combine(root, PreRestorePrefix + DateTime.Now.ToString(StampFormat));
            MoveVaultInto(path, kept);
            string keptRows = RowCounts(kept, path) ?? "unreadable";
            CopyIntoVault(snapshot, path);
            FiresLogger.LogWarning($"{Tag}the vault changed after world save {number}, the save the world loaded (a crash?): restored its copy " +
                                   $"({snapshot}). The vault as it was is kept in {kept}. Rows before: {keptRows}. After: {restoredRows}.");
        }

        // ------------------------------------------------------------ at each save

        // The moment vanilla captures the world. A save still being written finishes first (vanilla joins it right after this).
        private static void SaveWorld_Prefix(ZNet __instance)
        {
            if (!Active) return;
            try
            {
                Thread previous = s_saveThread(__instance);
                if (previous != null && previous.IsAlive) previous.Join();
                var timer = Stopwatch.StartNew();
                long bytes;
                lock (s_lock)
                {
                    string root = RootOf(VaultDatabase.DatabasePath);
                    if (Directory.Exists(root))
                        foreach (string stale in Directory.GetDirectories(root, PendingPrefix + "*")) DeleteFolder(stale);
                    s_pendingSave = SaveSystem.GetSaveNumber() + 1;
                    bytes = CopyVaultInto(Path.Combine(root, PendingPrefix + s_pendingSave));
                    s_pendingValid = true;
                }
                FiresLogger.LogInfo($"{Tag}vault copied for world save {s_pendingSave} ({bytes / BytesPerKb:0} KB) in {timer.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                lock (s_lock) s_pendingValid = false;
                FiresLogger.LogWarning($"{Tag}vault copy for this world save failed ({ex.Message}); that save won't have one.");
            }
        }

        // On the save thread, once the save is fully written (writeOK) or has been deleted again.
        private static void EndSave_Postfix(bool writeOK)
        {
            lock (s_lock)
            {
                if (!s_pendingValid) return;
                s_pendingValid = false;
                try
                {
                    string root = RootOf(VaultDatabase.DatabasePath);
                    string pending = Path.Combine(root, PendingPrefix + s_pendingSave);
                    uint number = SaveSystem.GetSaveNumber();
                    string tag = writeOK && number == s_pendingSave ? SaveTag(number) : null;
                    if (tag == null)
                    {
                        DeleteFolder(pending);
                        FiresLogger.LogWarning($"{Tag}world save {s_pendingSave} was not written; its vault copy is dropped.");
                        return;
                    }
                    string target = Path.Combine(root, tag);
                    DeleteFolder(target);
                    Directory.Move(pending, target);
                    s_lastCompletedTag = tag;
                    if (s_shuttingDown) s_completedWhileShuttingDown = true;
                    Prune(root);
                }
                catch (Exception ex) { FiresLogger.LogWarning($"{Tag}tagging the vault copy of world save {s_pendingSave} failed ({ex.Message})."); }
            }
        }

        private static void Shutdown_Prefix()
        {
            if (Active) s_shuttingDown = true;
        }

        // After the final save and the players' disconnects: the final save's copy becomes the vault as the server leaves it, so a
        // clean restart finds them equal and changes nothing.
        private static void Shutdown_Postfix()
        {
            if (!Active || !s_completedWhileShuttingDown || s_lastCompletedTag == null) return;
            try
            {
                VaultWriter.Drain();
                string root = RootOf(VaultDatabase.DatabasePath);
                lock (s_lock) CopyVaultInto(Path.Combine(root, s_lastCompletedTag));
                AtomicFile.WriteAllText(Path.Combine(root, CleanShutdownFile), s_lastCompletedTag);
                FiresLogger.LogInfo($"{Tag}vault copy of the final world save ({s_lastCompletedTag}) refreshed at shutdown.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag}refreshing the final vault copy failed ({ex.Message}); the next start restores the save's copy."); }
        }

        // ------------------------------------------------------------ files

        private static string RootOf(string path) => path + FolderSuffix;

        // The save the last clean shutdown left on, read once and removed, so a crash in this session can't be taken for a clean exit.
        private static string TakeCleanShutdownMark(string root)
        {
            string file = Path.Combine(root, CleanShutdownFile);
            if (!File.Exists(file)) return null;
            string tag = File.ReadAllText(file).Trim();
            File.Delete(file);
            return tag;
        }

        private static string LogFileOf(string path) =>
            Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + LogFileInfix + Path.GetExtension(path));

        private static string[] VaultFiles(string path) => new[] { path, LogFileOf(path) };

        // "<save number>-<.fwl2 write ticks>", or null when that save's .fwl2 isn't there.
        private static string SaveTag(uint number)
        {
            string fwl2 = ZNet.World.GetSaveDirectory(ZNet.World.m_fileSource) + SaveSystem.MainFwl2FileName();
            return File.Exists(fwl2) ? $"{number}{TagSeparator}{File.GetLastWriteTimeUtc(fwl2).Ticks}" : null;
        }

        private static bool IsSnapshotFolder(string folder)
        {
            string[] parts = Path.GetFileName(folder).Split(TagSeparator);
            return parts.Length == 2 && uint.TryParse(parts[0], out _) && long.TryParse(parts[1], out _);
        }

        private static void Prune(string root)
        {
            var stale = Directory.GetDirectories(root).Where(IsSnapshotFolder)
                .OrderByDescending(folder => long.Parse(Path.GetFileName(folder).Split(TagSeparator)[1])).Skip(SnapshotsKept);
            foreach (string folder in stale) DeleteFolder(folder);
        }

        // Inside a read transaction: LiteDB's shared engine holds its cross-process mutex from BeginTrans to Rollback, so no other
        // operation can touch the files while they are copied. Returns the bytes copied.
        private static long CopyVaultInto(string folder)
        {
            long bytes = 0;
            using (var db = VaultDatabase.Open())
            {
                db.BeginTrans();
                try
                {
                    DeleteFolder(folder);
                    Directory.CreateDirectory(folder);
                    foreach (string file in VaultFiles(VaultDatabase.DatabasePath))
                        if (File.Exists(file)) bytes += CopyShared(file, Path.Combine(folder, Path.GetFileName(file)));
                }
                finally { db.Rollback(); }
            }
            return bytes;
        }

        // The engine keeps the files open for writing, so they are read with sharing that allows it.
        private static long CopyShared(string source, string target)
        {
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.WriteThrough))
            {
                from.CopyTo(to, CopyBufferBytes);
                to.Flush(true);
                return to.Length;
            }
        }

        private static bool SameFiles(string path, string folder) =>
            VaultFiles(path).All(file => ReadOrEmpty(file).SequenceEqual(ReadOrEmpty(Path.Combine(folder, Path.GetFileName(file)))));

        private static byte[] ReadOrEmpty(string file)
        {
            if (!File.Exists(file)) return Array.Empty<byte>();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        private static void MoveVaultInto(string path, string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (string file in VaultFiles(path))
                if (File.Exists(file)) File.Move(file, Path.Combine(folder, Path.GetFileName(file)));
        }

        private static void CopyIntoVault(string folder, string path)
        {
            foreach (string file in VaultFiles(path))
            {
                string saved = Path.Combine(folder, Path.GetFileName(file));
                if (File.Exists(saved)) File.Copy(saved, file, true);
            }
        }

        // "Bank 12, Guild 3, …" from a throwaway copy of a folder's vault, so opening it can't change the files kept; null if it
        // won't open.
        private static string RowCounts(string folder, string path)
        {
            // Inside the game's BepInEx folder, not the OS temp folder (0.2.206, Fire: public mods keep their files inside the Valheim
            // folder); what a crash mid-read left behind is swept on the next call.
            string root = Path.Combine(BepInEx.Paths.CachePath, "fires-vault");
            SweepStale(root);
            string scratch = Path.Combine(root, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                foreach (string file in VaultFiles(path))
                {
                    string saved = Path.Combine(folder, Path.GetFileName(file));
                    if (File.Exists(saved)) File.Copy(saved, Path.Combine(scratch, Path.GetFileName(file)));
                }
                using (var db = new LiteDatabase(new ConnectionString { Filename = Path.Combine(scratch, Path.GetFileName(path)), Connection = ConnectionType.Direct }))
                    return string.Join(", ", db.GetCollectionNames().OrderBy(name => name, StringComparer.Ordinal)
                        .Select(name => $"{name} {db.GetCollection(name).Count()}"));
            }
            catch { return null; }
            finally { DeleteFolder(scratch); }
        }

        // Scratch folders older than an hour (a read that crashed before its finally ran).
        private static void SweepStale(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1)) DeleteFolder(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void DeleteFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
