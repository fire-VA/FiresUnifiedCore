using System;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// World Gen Studio for FAT's "New from Gaea" world button, carried by Core so the public FAT launches nothing and reads
    /// nothing outside the Valheim / BepInEx folders (Fire, 2026-09-29): the private FDT hosts the Studio (finds its executable,
    /// starts it, asks it to import the Gaea maps and bake, stops it) and sets the delegates; FAT calls them and shows the
    /// button only while <see cref="Available"/>. Core does no I/O here: delegates only.
    ///
    /// Threads: FAT calls <see cref="StartBake"/> from the main thread. FDT may call <c>stage</c> and <c>done</c> from any thread;
    /// FAT only stores what they say and reads it back on the main thread.
    /// </summary>
    public static class WorldGenStudioBridge
    {
        /// <summary>
        /// Imports the Gaea maps in <paramref name="gaeaFolder"/> and bakes world <paramref name="worldName"/> into
        /// <paramref name="workFolder"/> (a folder FAT created inside Valheim's save data). <paramref name="stage"/> reports each
        /// step's name. <paramref name="done"/>(ok, result) is called once: ok = true with the path of the baked
        /// <c>firesgen_&lt;name&gt;_baked.bin</c> (its <c>.flow</c> beside it), or false with the reason. Returns a handle for
        /// <see cref="Cancel"/>.
        /// </summary>
        public delegate object BakeHandler(string gaeaFolder, string worldName, float peakY, float worldSize, int resolution,
            string workFolder, Action<string> stage, Action<bool, string> done);

        /// <summary>Set by FDT. Null means no Studio host (FDT absent, or the Studio isn't installed on this machine).</summary>
        public static BakeHandler Bake;

        /// <summary>Set by FDT: stops the Studio for this bake; <c>done</c>(false, "Cancelled.") follows.</summary>
        public static Action<object> Cancel;

        /// <summary>There is a Studio to bake with.</summary>
        public static bool Available => Bake != null;

        /// <summary>
        /// Starts a bake through FDT when it is there; otherwise calls <paramref name="done"/>(false, why) and returns null. A throw
        /// inside FDT's handler is caught and reported the same way.
        /// </summary>
        public static object StartBake(string gaeaFolder, string worldName, float peakY, float worldSize, int resolution,
            string workFolder, Action<string> stage, Action<bool, string> done)
        {
            BakeHandler bake = Bake;
            if (bake == null)
            {
                done?.Invoke(false, "World Gen Studio is not available here (FDT hosts it).");
                return null;
            }
            try
            {
                return bake(gaeaFolder, worldName, peakY, worldSize, resolution, workFolder, stage, done);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldGenStudioBridge] bake of '{worldName}' threw in the Studio host: {ex.Message}");
                done?.Invoke(false, "The Studio host failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Cancels a bake started by <see cref="StartBake"/>; a null handle or no host is a no-op.</summary>
        public static void CancelBake(object job)
        {
            if (job == null) return;
            try { Cancel?.Invoke(job); }
            catch (Exception ex) { Debug.LogWarning($"[WorldGenStudioBridge] cancel threw in the Studio host: {ex.Message}"); }
        }
    }
}
