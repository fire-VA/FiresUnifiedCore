using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The raw config-file editor tab: browse everything under <c>BepInEx/config</c>, edit a file as text and
    /// save it. Saving a file that belongs to a loaded plugin also calls that plugin's
    /// <see cref="ConfigFile.Reload"/>, so the edit takes effect without a restart.
    /// Writes are confined to the config tree and to an extension allowlist, the same rule the admin config
    /// push uses.
    /// </summary>
    internal sealed class ConfigFilesEditor
    {
        private const long MaxEditableBytes = 512 * 1024;
        private const float ListWidthBase = 300f;
        private const float ToolbarHeightBase = 24f;

        private static readonly HashSet<string> EditableExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cfg", ".yaml", ".yml", ".json", ".txt", ".xml", ".ini" };

        private static float Px(float designUnits) => ConfigWindowScale.Px(designUnits);
        private static float ListWidth => Px(ListWidthBase);
        private static float ToolbarHeight => Px(ToolbarHeightBase);

        private readonly List<string> _relativePaths = new List<string>();
        private Vector2 _listScroll;
        private Vector2 _textScroll;
        private string _filter = "";
        private string _selected;
        private string _text = "";
        private string _loadedText = "";
        private string _status = "";
        private bool _tooLarge;

        public void Refresh()
        {
            _relativePaths.Clear();
            string root = Paths.ConfigPath;
            int skipped = Collect(root, root);
            _relativePaths.Sort(StringComparer.OrdinalIgnoreCase);
            _status = skipped == 0
                ? $"{_relativePaths.Count} file(s)."
                : $"{_relativePaths.Count} file(s); {skipped} folder(s) skipped (unreadable or path too long).";
        }

        // Walked folder by folder rather than with SearchOption.AllDirectories: one over-long path anywhere
        // under BepInEx/config (the dump folders do this) makes the recursive call throw and lose the whole
        // listing, where this only loses the offending folder.
        private int Collect(string root, string directory)
        {
            int skipped = 0;
            try
            {
                foreach (string path in Directory.GetFiles(directory))
                {
                    if (!EditableExtensions.Contains(Path.GetExtension(path))) continue;
                    _relativePaths.Add(MakeRelative(root, path));
                }
                foreach (string child in Directory.GetDirectories(directory))
                    skipped += Collect(root, child);
            }
            catch (Exception)
            {
                skipped++;
            }
            return skipped;
        }

        public void Draw(float height)
        {
            GUILayout.BeginHorizontal();
            DrawFileList(height);
            GUILayout.Space(6f);
            DrawEditor(height);
            GUILayout.EndHorizontal();
        }

        private void DrawFileList(float height)
        {
            GUILayout.BeginVertical(GUILayout.Width(ListWidth));
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("fileFilter");
            _filter = GUILayout.TextField(_filter ?? "", ConfigSkin.TextInput, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Rescan", ConfigSkin.ButtonSmall, GUILayout.Width(Px(60f)))) Refresh();
            GUILayout.EndHorizontal();

            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(height - ToolbarHeight));
            foreach (string relativePath in _relativePaths)
            {
                if (!MatchesFilter(relativePath)) continue;
                bool selected = relativePath == _selected;
                if (GUILayout.Button(relativePath, selected ? ConfigSkin.NavSel : ConfigSkin.NavItem))
                    Load(relativePath);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private bool MatchesFilter(string relativePath)
            => string.IsNullOrEmpty(_filter)
            || relativePath.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawEditor(float height)
        {
            GUILayout.BeginVertical();
            if (_selected == null)
            {
                GUILayout.Label("Pick a file on the left to edit it.", ConfigSkin.Label);
                GUILayout.EndVertical();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>" + _selected + "</b>", ConfigSkin.Title, GUILayout.ExpandWidth(true));
            bool changed = _text != _loadedText;
            GUI.enabled = changed && !_tooLarge;
            if (GUILayout.Button("Save", ConfigSkin.ButtonSmall, GUILayout.Width(Px(56f)))) Save();
            if (GUILayout.Button("Revert", ConfigSkin.ButtonSmall, GUILayout.Width(Px(60f)))) _text = _loadedText;
            GUI.enabled = true;
            if (GUILayout.Button("Reload", ConfigSkin.ButtonSmall, GUILayout.Width(Px(60f)))) Load(_selected);
            GUILayout.EndHorizontal();

            if (_tooLarge)
            {
                GUILayout.Label(_status, ConfigSkin.Desc);
                GUILayout.EndVertical();
                return;
            }

            _textScroll = GUILayout.BeginScrollView(_textScroll, GUILayout.Height(height - ToolbarHeight * 2f));
            GUI.SetNextControlName("fileText");
            _text = GUILayout.TextArea(_text, ConfigSkin.TextInput, GUILayout.ExpandWidth(true));
            GUILayout.EndScrollView();

            GUILayout.Label(changed ? "<color=#FFD980>unsaved changes</color>" : _status, ConfigSkin.Desc);
            GUILayout.EndVertical();
        }

        private void Load(string relativePath)
        {
            _selected = relativePath;
            _tooLarge = false;
            _status = "";
            _textScroll = Vector2.zero;
            try
            {
                string path = ResolveInsideConfig(relativePath);
                var info = new FileInfo(path);
                if (info.Length > MaxEditableBytes)
                {
                    _tooLarge = true;
                    _status = $"{info.Length / 1024} KB is too large to edit here — open it in a text editor.";
                    _text = _loadedText = "";
                    return;
                }
                _text = _loadedText = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                _tooLarge = true;
                _status = "Cannot read: " + ex.Message;
                _text = _loadedText = "";
            }
        }

        private void Save()
        {
            try
            {
                string path = ResolveInsideConfig(_selected);
                File.WriteAllText(path, _text);
                _loadedText = _text;
                _status = ReloadOwningPlugin(path)
                    ? "Saved and reloaded into the running mod."
                    : "Saved. No loaded plugin owns this file, so nothing was reloaded.";
                CfgDiscovery.Rebuild();
            }
            catch (Exception ex)
            {
                _status = "Save failed: " + ex.Message;
                FiresConfigUI.Log.LogWarning($"config file save failed for '{_selected}': {ex.Message}");
            }
        }

        private static bool ReloadOwningPlugin(string path)
        {
            foreach (var kvp in Chainloader.PluginInfos)
            {
                var config = kvp.Value?.Instance?.Config;
                if (config == null) continue;
                if (!PathsEqual(config.ConfigFilePath, path)) continue;
                config.Reload();
                return true;
            }
            return false;
        }

        private static bool PathsEqual(string left, string right)
            => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

        private static string ResolveInsideConfig(string relativePath)
        {
            string root = Path.GetFullPath(Paths.ConfigPath);
            string full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("path escapes the config folder");
            if (!EditableExtensions.Contains(Path.GetExtension(full)))
                throw new InvalidOperationException("file type is not editable here");
            return full;
        }

        private static string MakeRelative(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full.Substring(rootFull.Length) : full;
        }
    }
}
