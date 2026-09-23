using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Puts a "Mod Settings" entry under Settings in both the main menu and the in-game pause menu, cloned from
    /// the vanilla Settings button so it inherits the game's own styling and gamepad navigation. Without it the
    /// config window is hotkey-only, which is not where players look for settings.
    /// </summary>
    public static class ConfigMenuButton
    {
        private const string ButtonName = "FiresModSettings";
        private const string ButtonCaption = "Mod Settings";
        private const string MenuEntriesPath = "MenuEntries";
        private const string SettingsEntryName = "Settings";

        public static void Refresh()
        {
            var mainMenuList = FejdStartup.instance != null ? FejdStartup.instance.m_menuList : null;
            if (mainMenuList != null)
            {
                AddEntry(mainMenuList.transform.Find(MenuEntriesPath));
                FejdStartup.instance.m_menuButtons = mainMenuList.GetComponentsInChildren<Button>();
            }
            if (Menu.instance != null && Menu.instance.m_menuDialog != null)
                AddEntry(Menu.instance.m_menuDialog.Find(MenuEntriesPath));
        }

        private static void AddEntry(Transform menuEntries)
        {
            if (menuEntries == null) return;

            var existing = menuEntries.Find(ButtonName);
            if (!FiresConfigUI.ShowMenuButton)
            {
                if (existing != null) Object.Destroy(existing.gameObject);
                return;
            }

            var settingsEntry = menuEntries.Find(SettingsEntryName);
            if (settingsEntry == null) return;

            var entry = existing != null ? existing.gameObject : CreateEntry(settingsEntry, menuEntries);
            var caption = entry.GetComponentInChildren<TMP_Text>();
            if (caption != null && caption.text != ButtonCaption) caption.text = ButtonCaption;

            // Menu.UpdateNavigation rebuilds explicit navigation from its own hardcoded button list every
            // call, which drops the cloned entry, so the links are re-applied on every refresh.
            LinkNavigation(settingsEntry.GetComponent<Button>(), entry.GetComponent<Button>());
        }

        private static GameObject CreateEntry(Transform settingsEntry, Transform menuEntries)
        {
            var entry = Object.Instantiate(settingsEntry.gameObject, menuEntries);
            entry.name = ButtonName;
            entry.transform.SetSiblingIndex(settingsEntry.GetSiblingIndex() + 1);

            var button = entry.GetComponent<Button>();
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
                button.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener((UnityAction)ConfigPanel.Open);
            return entry;
        }

        private static void LinkNavigation(Button settingsButton, Button modSettingsButton)
        {
            if (settingsButton == null) return;

            var below = modSettingsButton.navigation.selectOnDown;

            var settingsNavigation = settingsButton.navigation;
            settingsNavigation.selectOnDown = modSettingsButton;
            settingsButton.navigation = settingsNavigation;

            var modSettingsNavigation = modSettingsButton.navigation;
            modSettingsNavigation.selectOnUp = settingsButton;
            modSettingsButton.navigation = modSettingsNavigation;

            if (below == null) return;
            var belowNavigation = below.navigation;
            belowNavigation.selectOnUp = modSettingsButton;
            below.navigation = belowNavigation;
        }

        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class FejdStartup_Start_AddEntry
        {
            private static void Postfix() => Refresh();
        }

        [HarmonyPatch(typeof(Menu), "Start")]
        private static class Menu_Start_AddEntry
        {
            private static void Postfix() => Refresh();
        }

        [HarmonyPatch(typeof(Menu), "UpdateNavigation")]
        private static class Menu_UpdateNavigation_AddEntry
        {
            private static void Postfix() => Refresh();
        }
    }
}
