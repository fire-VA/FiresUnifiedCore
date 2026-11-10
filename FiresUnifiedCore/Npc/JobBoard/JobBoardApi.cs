namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// Public, ADD-ONLY entry to the Homestead Job Board window, for pieces in other mods (the physical Job Board piece: its E
    /// interact opens this). Callers bind by reflection ("FiresCore.Npc.JobBoard.JobBoardApi, FiresUnifiedCore") so an older Core
    /// without it still loads; Ctrl+J keeps working unchanged.
    /// </summary>
    public static class JobBoardApi
    {
        /// <summary>Opens the job board (no-op when it is already showing).</summary>
        public static void Open() { if (!IsOpen) JobBoardWindow.Toggle(); }

        /// <summary>Closes the job board when it is showing.</summary>
        public static void Close() { if (IsOpen) JobBoardWindow.Toggle(); }

        /// <summary>True while the job board window is showing.</summary>
        public static bool IsOpen => JobBoardWindow.IsShowing;
    }
}
