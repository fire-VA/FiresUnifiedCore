using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Syncs the player's active status effects into an override UI container.
    /// Attach to a panel tagged <see cref="UIOverrideElementTags.StatusEffectContainer"/>.
    ///
    /// Each active status effect gets a child GameObject with an icon Image and
    /// optional timer/name text. Effects are added/removed dynamically as the
    /// player gains or loses status effects.
    ///
    /// This reads from <c>Player.m_localPlayer.GetSEMan().GetStatusEffects()</c>
    /// every frame and reconciles the visual list.
    /// </summary>
    public class UIOverrideStatusEffectSync : MonoBehaviour
    {
        public float IconSize = 48f;
        public float Spacing = 4f;

        /// <summary>If true, shows remaining time text on timed effects.</summary>
        public bool ShowTimer = true;

        /// <summary>If true, shows the effect name below the icon.</summary>
        public bool ShowName = false;

        private readonly List<StatusEffectEntry> _entries = new List<StatusEffectEntry>();
        private HorizontalLayoutGroup _hlg;

        private class StatusEffectEntry
        {
            public int NameHash;
            public GameObject GO;
            public Image Icon;
            public TMP_Text TimerText;
        }

        private void Start()
        {
            _hlg = GetComponent<HorizontalLayoutGroup>();
            if (_hlg == null) _hlg = gameObject.AddComponent<HorizontalLayoutGroup>();
            _hlg.spacing = Spacing;
            _hlg.childAlignment = TextAnchor.MiddleLeft;
            _hlg.childControlWidth = false;
            _hlg.childControlHeight = false;
            _hlg.childForceExpandWidth = false;
            _hlg.childForceExpandHeight = false;
        }

        private void LateUpdate()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                ClearAll();
                return;
            }

            var seMan = player.GetSEMan();
            if (seMan == null)
            {
                ClearAll();
                return;
            }

            var effects = seMan.GetStatusEffects();
            ReconcileEffects(effects);
            UpdateTimers(effects);
        }

        private void ReconcileEffects(List<StatusEffect> effects)
        {
            // Build a set of active effect name hashes
            var activeHashes = new HashSet<int>();
            foreach (var se in effects)
            {
                if (se == null) continue;
                activeHashes.Add(se.NameHash());
            }

            // Remove entries that are no longer active
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!activeHashes.Contains(_entries[i].NameHash))
                {
                    if (_entries[i].GO != null) Destroy(_entries[i].GO);
                    _entries.RemoveAt(i);
                }
            }

            // Add entries for new effects
            var existingHashes = new HashSet<int>();
            foreach (var e in _entries) existingHashes.Add(e.NameHash);

            foreach (var se in effects)
            {
                if (se == null) continue;
                int hash = se.NameHash();
                if (existingHashes.Contains(hash)) continue;

                var entry = CreateEffectEntry(se);
                _entries.Add(entry);
                existingHashes.Add(hash);
            }
        }

        private void UpdateTimers(List<StatusEffect> effects)
        {
            if (!ShowTimer) return;

            foreach (var se in effects)
            {
                if (se == null) continue;
                int hash = se.NameHash();

                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].NameHash != hash) continue;

                    // Update icon in case it changes (e.g., some effects cycle)
                    if (_entries[i].Icon != null && se.m_icon != null)
                        _entries[i].Icon.sprite = se.m_icon;

                    // Update timer text
                    if (_entries[i].TimerText != null)
                    {
                        float ttl = se.GetRemaningTime();
                        if (ttl > 0f)
                        {
                            _entries[i].TimerText.text = FormatTime(ttl);
                            _entries[i].TimerText.enabled = true;
                        }
                        else
                        {
                            _entries[i].TimerText.text = "";
                            _entries[i].TimerText.enabled = false;
                        }
                    }
                    break;
                }
            }
        }

        private StatusEffectEntry CreateEffectEntry(StatusEffect se)
        {
            var go = new GameObject(se.m_name ?? "SE", typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(IconSize, IconSize);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = IconSize;
            le.preferredHeight = IconSize;

            // Icon
            var iconImg = go.AddComponent<Image>();
            iconImg.sprite = se.m_icon;
            iconImg.color = se.m_icon != null ? Color.white : Color.clear;
            iconImg.raycastTarget = false;

            TMP_Text timerText = null;
            if (ShowTimer)
            {
                var timerGO = new GameObject("timer", typeof(RectTransform));
                timerGO.transform.SetParent(go.transform, false);
                var timerRect = timerGO.GetComponent<RectTransform>();
                timerRect.anchorMin = new Vector2(0f, 0f);
                timerRect.anchorMax = new Vector2(1f, 0.35f);
                timerRect.offsetMin = Vector2.zero;
                timerRect.offsetMax = Vector2.zero;
                timerText = timerGO.AddComponent<TextMeshProUGUI>();
                timerText.fontSize = 10;
                timerText.alignment = TextAlignmentOptions.Bottom;
                timerText.color = Color.white;
                timerText.raycastTarget = false;
                timerText.text = "";
            }

            return new StatusEffectEntry
            {
                NameHash = se.NameHash(),
                GO = go,
                Icon = iconImg,
                TimerText = timerText
            };
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 60f)
                return $"{Mathf.CeilToInt(seconds)}s";
            int m = Mathf.FloorToInt(seconds / 60f);
            int s = Mathf.CeilToInt(seconds % 60f);
            return $"{m}:{s:D2}";
        }

        private void ClearAll()
        {
            foreach (var e in _entries)
            {
                if (e.GO != null) Destroy(e.GO);
            }
            _entries.Clear();
        }

        private void OnDestroy()
        {
            ClearAll();
        }
    }
}
