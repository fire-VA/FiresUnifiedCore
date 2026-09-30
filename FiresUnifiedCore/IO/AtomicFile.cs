using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace FiresCore.IO
{
    // Crash-safe file writes for any mod's store (Tools\SAVE_SAFETY_AUDIT.md, fix 2). The new content goes to a temp file beside
    // the target, is flushed to disk, and then replaces the target in one step, keeping the previous version as <file>.bak, so
    // a crash leaves the old file or the new one, never a torn one. File.WriteAllText(path, text) becomes
    // AtomicFile.WriteAllText(path, text). Writers of the same path are serialized; different paths don't wait on each other.
    public static class AtomicFile
    {
        public const string TempSuffix = ".writing";
        public const string BackupSuffix = ".bak";
        private const int BufferBytes = 64 * 1024;

        private static readonly ConcurrentDictionary<string, object> s_pathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // UTF-8 without a byte-order mark, as File.WriteAllText writes.
        public static void WriteAllText(string path, string text) => WriteAllText(path, text, new UTF8Encoding(false));

        public static void WriteAllText(string path, string text, Encoding encoding) =>
            WriteAllBytes(path, encoding.GetBytes(text ?? string.Empty));

        public static void WriteAllBytes(string path, byte[] bytes) =>
            Write(path, stream => stream.Write(bytes, 0, bytes.Length));

        // The content comes from a writer (a serializer, a zip archive in Create mode) into a fresh file.
        public static void Write(string path, Action<Stream> write) => Commit(path, false, stream => write(stream));

        // The writer edits a copy of the current file (a zip archive in Update mode), then the copy replaces it. A writer that
        // wraps the stream must leave it open (ZipArchive's leaveOpen), so the edit can be flushed to disk before the replace.
        public static void Update(string path, Action<FileStream> edit) => Commit(path, true, edit);

        private static void Commit(string path, bool startFromCurrent, Action<FileStream> write)
        {
            string full = Path.GetFullPath(path);
            string temp = full + TempSuffix;
            lock (s_pathLocks.GetOrAdd(full, _ => new object()))
            {
                string folder = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                bool exists = File.Exists(full);
                if (startFromCurrent && exists) File.Copy(full, temp, true);
                try
                {
                    var mode = startFromCurrent && exists ? FileMode.Open : FileMode.Create;
                    using (var stream = new FileStream(temp, mode, FileAccess.ReadWrite, FileShare.None, BufferBytes, FileOptions.WriteThrough))
                    {
                        write(stream);
                        if (stream.CanWrite) stream.Flush(true);
                    }
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
                if (exists) File.Replace(temp, full, full + BackupSuffix, true);
                else File.Move(temp, full);
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
