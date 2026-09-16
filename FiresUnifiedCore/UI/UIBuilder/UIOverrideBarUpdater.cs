using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Per-frame updater that reads Valheim's Player stats and applies fill amounts
    /// to injected bar elements (health, stamina, eitr, food, guardian power).
    /// Attached by the override wiring system to injected mod bar elements.
    /// </summary>
    public class UIOverrideBarUpdater : MonoBehaviour
    {
        public enum BarType
        {
            Health,
            Stamina,
            Eitr,
            Food,
            GuardianPower
        }

        public BarType Type;
        public Image FillImage;
        public TMP_Text ValueText;

        /// <summary>
        /// Optional slow-bar Image that lerps behind the main fill for damage feedback.
        /// </summary>
        public Image SlowFillImage;

        private float _smoothFill;
        private float _targetFill;
        private const float SlowFillSpeed = 0.5f;

        private static FieldInfo _foodsField;
        private static FieldInfo _guardianCooldownField;
        private static bool _reflectionLookedUp;

        // Throttle updates to reduce canvas rebuilds — stats don't need 60fps precision
        private float _updateTimer;
        private const float UpdateInterval = 0.05f; // ~20Hz is visually smooth for bars

        private void Update()
        {
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;

            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!_reflectionLookedUp)
            {
                _reflectionLookedUp = true;
                _foodsField = typeof(Player).GetField("m_foods",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _guardianCooldownField = typeof(Player).GetField("m_guardianPowerCooldown",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            }

            float current = 0f;
            float max = 0f;

            switch (Type)
            {
                case BarType.Health:
                    current = player.GetHealth();
                    max = player.GetMaxHealth();
                    break;
                case BarType.Stamina:
                    current = player.GetStamina();
                    max = player.GetMaxStamina();
                    break;
                case BarType.Eitr:
                    current = player.GetEitr();
                    max = player.GetMaxEitr();
                    break;
                case BarType.Food:
                    if (_foodsField != null)
                    {
                        try
                        {
                            var foods = _foodsField.GetValue(player) as System.Collections.IList;
                            if (foods != null && foods.Count > 0)
                            {
                                // Show aggregate food time remaining as a fraction
                                max = foods.Count;
                                current = foods.Count; // Just show as full for now; per-food bars need individual wiring
                            }
                        }
                        catch { }
                    }
                    break;
                case BarType.GuardianPower:
                    if (_guardianCooldownField != null)
                    {
                        try
                        {
                            float cooldown = (float)_guardianCooldownField.GetValue(player);
                            max = cooldown;
                            // Guardian power: full bar = ready, empty = on cooldown
                            current = Mathf.Max(0f, cooldown);
                        }
                        catch { }
                    }
                    break;
            }

            _targetFill = max > 0f ? Mathf.Clamp01(current / max) : 0f;

            // Only write to Image/Text when values actually changed — avoids dirtying canvas
            if (FillImage != null && !Mathf.Approximately(FillImage.fillAmount, _targetFill))
                FillImage.fillAmount = _targetFill;

            if (SlowFillImage != null)
            {
                _smoothFill = Mathf.MoveTowards(_smoothFill, _targetFill, SlowFillSpeed * Time.deltaTime);
                if (!Mathf.Approximately(SlowFillImage.fillAmount, _smoothFill))
                    SlowFillImage.fillAmount = _smoothFill;
            }

            if (ValueText != null)
            {
                string newText;
                if (max > 0f)
                    newText = $"{Mathf.CeilToInt(current)}/{Mathf.CeilToInt(max)}";
                else
                    newText = "";
                if (ValueText.text != newText)
                    ValueText.text = newText;
            }
        }
    }
}
