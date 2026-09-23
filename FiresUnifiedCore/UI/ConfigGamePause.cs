namespace FiresCore.UI
{
    /// <summary>
    /// Pauses single-player while the config window is open, and tells the pause menu that a settings screen
    /// is up so its own close handling (gamepad B / Esc) doesn't shut the menu out from under the window.
    /// Both are client-only concerns; the window never opens headless.
    /// </summary>
    internal static class ConfigGamePause
    {
        public static void Apply(bool windowOpen)
        {
            HoldPauseMenuOpen(windowOpen);

            if (!FiresConfigUI.PauseGame || Game.instance == null) return;

            if (windowOpen)
            {
                if (!Game.IsPaused() && Game.CanPause()) Game.Pause();
            }
            else if (Game.IsPaused() && !Menu.IsActive()) Game.Unpause();
        }

        private static void HoldPauseMenuOpen(bool windowOpen)
        {
            if (Menu.instance == null) return;
            Menu.instance.m_closeMenuState = windowOpen ? Menu.CloseMenuState.SettingsOpen : Menu.CloseMenuState.CanBeClosed;
            Menu.instance.m_rebuildLayout = true;
        }
    }
}
