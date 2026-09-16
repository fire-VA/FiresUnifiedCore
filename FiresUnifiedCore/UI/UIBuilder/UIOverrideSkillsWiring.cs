using System;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Wires an override skills panel button/element to the vanilla SkillsDialog.
    /// When attached to a button tagged <see cref="UIOverrideElementTags.SkillsPanel"/>,
    /// clicking it toggles the vanilla skills dialog visibility.
    ///
    /// This is intentionally simple — the skills dialog is already a fully functional
    /// vanilla panel. We just need to provide a way to open/close it from our custom UI.
    /// </summary>
    public class UIOverrideSkillsWiring : MonoBehaviour
    {
        private Button _button;

        private void Start()
        {
            _button = GetComponent<Button>();
            if (_button != null)
            {
                _button.onClick.RemoveAllListeners();
                _button.onClick.AddListener(ToggleSkills);
            }
        }

        private void ToggleSkills()
        {
            if (InventoryGui.instance == null) return;

            var skillsDialog = InventoryGui.instance.m_skillsDialog;
            if (skillsDialog == null) return;

            bool isActive = skillsDialog.gameObject.activeSelf;
            skillsDialog.gameObject.SetActive(!isActive);

            if (!isActive)
            {
                try
                {
                    skillsDialog.Setup(Player.m_localPlayer);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIOverrideSkillsWiring] Setup failed: {ex.Message}");
                }
            }
        }
    }
}
