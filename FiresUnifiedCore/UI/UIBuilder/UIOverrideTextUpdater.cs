using System.Reflection;
using TMPro;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Per-frame text updater that displays live game data on injected text elements.
    /// Attached by the override wiring system to text elements detected by name patterns.
    /// </summary>
    public class UIOverrideTextUpdater : MonoBehaviour
    {
        public enum TextType
        {
            HealthValue,
            StaminaValue,
            EitrValue,
            Weight,
            DayTime,
            Biome,
            PlayerName,
            Comfort,
            GuardianPower
        }

        public TextType Type;
        public TMP_Text Text;

        /// <summary>Fallback for legacy UnityEngine.UI.Text components.</summary>
        public UnityEngine.UI.Text LegacyText;

        private float _updateInterval = 0.1f;
        private float _updateTimer;

        private static FieldInfo _guardianCooldownField;
        private static bool _reflectionLookedUp;

        private void Update()
        {
            _updateTimer += Time.unscaledDeltaTime;
            if (_updateTimer < _updateInterval) return;
            _updateTimer = 0f;

            var player = Player.m_localPlayer;
            if (player == null) return;

            if (!_reflectionLookedUp)
            {
                _reflectionLookedUp = true;
                _guardianCooldownField = typeof(Player).GetField("m_guardianPowerCooldown",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            }

            string value = "";

            switch (Type)
            {
                case TextType.HealthValue:
                    value = $"{Mathf.CeilToInt(player.GetHealth())}/{Mathf.CeilToInt(player.GetMaxHealth())}";
                    break;
                case TextType.StaminaValue:
                    value = $"{Mathf.CeilToInt(player.GetStamina())}/{Mathf.CeilToInt(player.GetMaxStamina())}";
                    break;
                case TextType.EitrValue:
                    float maxEitr = player.GetMaxEitr();
                    value = maxEitr > 0
                        ? $"{Mathf.CeilToInt(player.GetEitr())}/{Mathf.CeilToInt(maxEitr)}"
                        : "";
                    break;
                case TextType.Weight:
                    var inv = player.GetInventory();
                    if (inv != null)
                        value = $"{inv.GetTotalWeight():F1}/{player.GetMaxCarryWeight():F0}";
                    break;
                case TextType.DayTime:
                    if (EnvMan.instance != null)
                    {
                        int day = EnvMan.instance.GetDay(ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0);
                        value = $"Day {day}";
                    }
                    break;
                case TextType.Biome:
                    if (EnvMan.instance != null)
                    {
                        var biome = player.GetCurrentBiome();
                        value = Localization.instance != null
                            ? Localization.instance.Localize("$biome_" + biome.ToString().ToLowerInvariant())
                            : biome.ToString();
                    }
                    break;
                case TextType.PlayerName:
                    value = player.GetPlayerName();
                    break;
                case TextType.Comfort:
                    value = player.GetComfortLevel().ToString();
                    break;
                case TextType.GuardianPower:
                    if (_guardianCooldownField != null)
                    {
                        try
                        {
                            float cd = (float)_guardianCooldownField.GetValue(player);
                            if (cd > 0)
                            {
                                int mins = (int)(cd / 60f);
                                int secs = (int)(cd % 60f);
                                value = $"{mins}:{secs:D2}";
                            }
                            else
                            {
                                value = "Ready";
                            }
                        }
                        catch { value = ""; }
                    }
                    break;
            }

            // Only write when value changed — avoids dirtying canvas with identical text
            if (Text != null)
            {
                if (Text.text != value)
                    Text.text = value;
            }
            else if (LegacyText != null)
            {
                if (LegacyText.text != value)
                    LegacyText.text = value;
            }
        }
    }
}
