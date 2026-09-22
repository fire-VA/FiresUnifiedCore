using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresCore.Logging;
using FiresCore.Net;
using UnityEngine;
using FiresCoreRoot = FiresCore.FiresUnifiedCore;

namespace FiresCore.Sync
{
    /// <summary>
    /// Plumbing shared by the family's server-authoritative override services (recipes, creatures). The server alone
    /// owns a JSON file and hot-reloads outside edits to it; clients change entries through admin-checked upsert and
    /// remove RPCs; every change broadcasts the full set; each client pulls the set once per session when its player
    /// spawns. The owning service supplies the entry key, sanitizer, document format and the local apply step.
    /// </summary>
    public sealed class ServerOverrideStore<T> where T : class
    {
        public delegate bool Sanitizer(T entry, out string error);

        private const float FilePollSeconds = 0.5f;
        private const double FileQuietSeconds = 0.3;
        private const double SelfWriteWindowSeconds = 2.0;

        private readonly string _label;
        private readonly string _fileName;
        private readonly Func<string> _directory;
        private readonly Func<T, string> _keyOf;
        private readonly Sanitizer _sanitize;
        private readonly Func<List<T>, string> _serialize;
        private readonly Func<string, List<T>> _deserialize;
        private readonly Func<T, T> _clone;
        private readonly Func<T, string> _describeRemoval;
        private readonly Action _applyLocally;
        private readonly Dictionary<string, T> _entries = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly object _fileEventLock = new object();
        private FileSystemWatcher _watcher;
        private DateTime _fileChangedUtc = DateTime.MinValue;
        private DateTime _lastSelfWriteUtc = DateTime.MinValue;
        private bool _requestedThisSession;

        public event Action Changed;
        public event Action<bool, string> SubmitResult;

        public ServerOverrideStore(string label, string fileName, Func<string> directory, Func<T, string> keyOf,
            Sanitizer sanitize, Func<List<T>, string> serialize, Func<string, List<T>> deserialize, Func<T, T> clone,
            Func<T, string> describeRemoval, Action applyLocally)
        {
            _label = label;
            _fileName = fileName;
            _directory = directory;
            _keyOf = keyOf;
            _sanitize = sanitize;
            _serialize = serialize;
            _deserialize = deserialize;
            _clone = clone;
            _describeRemoval = describeRemoval;
            _applyLocally = applyLocally;
        }

        public IReadOnlyList<T> All => _entries.Values.ToList();

        public IEnumerable<T> Entries => _entries.Values;

        public bool TryGet(string key, out T entry) => _entries.TryGetValue(key ?? "", out entry);

        public bool Contains(string key) => _entries.ContainsKey(key ?? "");

        private string RequestRpc => $"{FiresCoreRoot.PluginName}_{_label}_Request";
        private string SetRpc => $"{FiresCoreRoot.PluginName}_{_label}_Set";
        private string UpsertRpc => $"{FiresCoreRoot.PluginName}_{_label}_Upsert";
        private string RemoveRpc => $"{FiresCoreRoot.PluginName}_{_label}_Remove";
        private string ResultRpc => $"{FiresCoreRoot.PluginName}_{_label}_Result";
        private string FilePath => Path.Combine(_directory(), _fileName);

        /// <summary>Call from a ZNet.Awake postfix: forgets the previous world's set, registers RPCs, loads on a server.</summary>
        public void OnSessionStart(ZNet znet)
        {
            _entries.Clear();
            _requestedThisSession = false;

            ZRoutedRpc.instance.Register<ZPackage>(RequestRpc, RPC_Request);
            ZRoutedRpc.instance.Register<ZPackage>(SetRpc, RPC_Set);
            ZRoutedRpc.instance.Register<ZPackage>(UpsertRpc, RPC_Upsert);
            ZRoutedRpc.instance.Register<ZPackage>(RemoveRpc, RPC_Remove);
            ZRoutedRpc.instance.Register<ZPackage>(ResultRpc, RPC_Result);

            if (!znet.IsServer())
                return;
            try
            {
                LoadFromDisk();
                WatchFile();
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[{_label}] could not load {_fileName}: {ex.Message}");
            }
            _applyLocally();
            znet.StartCoroutine(ReloadOnFileEdits());
        }

        /// <summary>Call when the local player spawns: a host applies its own set, a client asks the server once.</summary>
        public void OnLocalPlayerSpawned()
        {
            if (ZNet.instance == null)
                return;
            if (ZNet.instance.IsServer())
            {
                _applyLocally();
                return;
            }
            if (_requestedThisSession)
                return;
            _requestedThisSession = true;
            SendToServer(RequestRpc, "");
        }

        public void Submit(T entry)
        {
            if (ZNet.instance == null || entry == null)
                return;
            if (ZNet.instance.IsServer())
                StoreAsServer(_clone(entry), replyTo: null);
            else
                SendToServer(UpsertRpc, _serialize(new List<T> { entry }));
        }

        public void Remove(string key)
        {
            if (ZNet.instance == null || string.IsNullOrEmpty(key))
                return;
            if (ZNet.instance.IsServer())
                RemoveAsServer(key, replyTo: null);
            else
                SendToServer(RemoveRpc, key);
        }

        private void RPC_Request(long sender, ZPackage package)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
                SafeRoutedRpc.InvokeSafe(sender, SetRpc, PackText(_serialize(_entries.Values.ToList())));
        }

        private void RPC_Set(long sender, ZPackage package)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
                return;
            if (!TryDeserialize(package.ReadString(), out List<T> entries))
                return;
            ReplaceAll(entries);
            _applyLocally();
            Changed?.Invoke();
        }

        private void RPC_Upsert(long sender, ZPackage package)
        {
            if (!IsServerAndSenderIsAdmin(sender))
                return;
            if (!TryDeserialize(package.ReadString(), out List<T> entries) || entries.Count != 1)
            {
                Reply(sender, false, "The edit was unreadable.");
                return;
            }
            StoreAsServer(entries[0], sender);
        }

        private void RPC_Remove(long sender, ZPackage package)
        {
            if (IsServerAndSenderIsAdmin(sender))
                RemoveAsServer(package.ReadString(), sender);
        }

        private void RPC_Result(long sender, ZPackage package)
        {
            bool ok = package.ReadBool();
            SubmitResult?.Invoke(ok, package.ReadString());
        }

        private bool IsServerAndSenderIsAdmin(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return false;
            if (AdminSyncing.IsAdmin(sender))
                return true;
            Reply(sender, false, "Edits are admin-only on this server.");
            return false;
        }

        private void StoreAsServer(T entry, long? replyTo)
        {
            if (!_sanitize(entry, out string error))
            {
                Answer(replyTo, false, error);
                return;
            }
            string key = _keyOf(entry);
            _entries[key] = entry;
            CommitAsServer();
            Answer(replyTo, true, $"Saved '{key}' for everyone.");
        }

        private void RemoveAsServer(string key, long? replyTo)
        {
            if (!_entries.TryGetValue(key ?? "", out T entry))
            {
                Answer(replyTo, false, $"'{key}' has no saved edit to remove.");
                return;
            }
            _entries.Remove(key);
            CommitAsServer();
            Answer(replyTo, true, _describeRemoval(entry));
        }

        private void CommitAsServer()
        {
            try { SaveToDisk(); }
            catch (Exception ex) { FiresLogger.LogError($"[{_label}] could not save {_fileName}, the change is live but not persisted: {ex.Message}"); }
            _applyLocally();
            SafeRoutedRpc.InvokeSafe(SetRpc, PackText(_serialize(_entries.Values.ToList())));
            Changed?.Invoke();
        }

        private void Answer(long? replyTo, bool ok, string message)
        {
            if (replyTo.HasValue)
                Reply(replyTo.Value, ok, message);
            else
                SubmitResult?.Invoke(ok, message);
            if (!ok)
                FiresLogger.LogWarning($"[{_label}] {message}");
        }

        private void Reply(long target, bool ok, string message)
        {
            var package = new ZPackage();
            package.Write(ok);
            package.Write(message ?? "");
            SafeRoutedRpc.InvokeSafe(target, ResultRpc, package);
        }

        private void SendToServer(string rpc, string text) =>
            SafeRoutedRpc.InvokeSafe(ZRoutedRpc.instance.GetServerPeerID(), rpc, PackText(text));

        private static ZPackage PackText(string text)
        {
            var package = new ZPackage();
            package.Write(text ?? "");
            return package;
        }

        private void ReplaceAll(IEnumerable<T> entries)
        {
            _entries.Clear();
            foreach (T entry in entries)
            {
                string key = entry != null ? _keyOf(entry) : null;
                if (!string.IsNullOrWhiteSpace(key))
                    _entries[key] = entry;
            }
        }

        private bool TryDeserialize(string json, out List<T> entries)
        {
            entries = null;
            try
            {
                entries = string.IsNullOrWhiteSpace(json) ? new List<T>() : _deserialize(json);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[{_label}] data unreadable, keeping the previous set: {ex.Message}");
            }
            return entries != null;
        }

        private void LoadFromDisk()
        {
            string path = FilePath;
            string json = File.Exists(path) ? File.ReadAllText(path) : "";
            if (!TryDeserialize(json, out List<T> loaded))
                return;
            var accepted = new List<T>();
            foreach (T entry in loaded)
            {
                string error = "empty entry";
                if (entry != null && _sanitize(entry, out error))
                    accepted.Add(entry);
                else
                    FiresLogger.LogWarning($"[{_label}] {_fileName}: skipped an entry: {error}");
            }
            ReplaceAll(accepted);
            FiresLogger.LogInfo($"[{_label}] loaded {_entries.Count} override(s) from {path}");
        }

        private void SaveToDisk()
        {
            string path = FilePath;
            string temp = path + ".tmp";
            lock (_fileEventLock)
                _lastSelfWriteUtc = DateTime.UtcNow;
            File.WriteAllText(temp, _serialize(_entries.Values.ToList()));
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        private void WatchFile()
        {
            if (_watcher != null)
                return;
            _watcher = new FileSystemWatcher(_directory(), _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }

        private void OnFileEvent(object source, FileSystemEventArgs change)
        {
            lock (_fileEventLock)
                _fileChangedUtc = DateTime.UtcNow;
        }

        /// <summary>Server loop: reloads the file after an outside edit settles, ignoring the server's own saves.</summary>
        private IEnumerator ReloadOnFileEdits()
        {
            var wait = new WaitForSeconds(FilePollSeconds);
            while (ZNet.instance != null)
            {
                yield return wait;
                if (!TakeSettledOutsideEdit())
                    continue;
                try { LoadFromDisk(); }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"[{_label}] could not reload {_fileName}: {ex.Message}");
                    continue;
                }
                _applyLocally();
                SafeRoutedRpc.InvokeSafe(SetRpc, PackText(_serialize(_entries.Values.ToList())));
                Changed?.Invoke();
            }
        }

        private bool TakeSettledOutsideEdit()
        {
            lock (_fileEventLock)
            {
                if (_fileChangedUtc == DateTime.MinValue || (DateTime.UtcNow - _fileChangedUtc).TotalSeconds < FileQuietSeconds)
                    return false;
                bool outsideEdit = (_fileChangedUtc - _lastSelfWriteUtc).TotalSeconds > SelfWriteWindowSeconds;
                _fileChangedUtc = DateTime.MinValue;
                return outsideEdit;
            }
        }
    }
}
