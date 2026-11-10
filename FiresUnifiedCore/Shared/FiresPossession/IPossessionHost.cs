using System;
using UnityEngine;

// Shared\FiresPossession: one source, compiled into Core (FIRES_POSSESSION_PUBLIC defined: public, the API other mods call) and
// into Valcast (internal: Valcast stays standalone). The names below are an agreed API - additive changes
// only; Core depends on them.
namespace FiresPossession
{
    /// <summary>
    /// What the mod that hosts the engine provides: its camera, its settings, its messages and its log. Core and Valcast each have
    /// one. Settings the engine reads (each with the default used when the host doesn't know the key):
    /// "MouseSensitivity" (2, degrees of heading per unit of mouse), "CycleModifier" (KeyCode.LeftAlt as a number: hold it and turn
    /// the wheel to pick the special attack), "FlightArcade" (false: Flight controls; true: Space up / Ctrl down).
    /// </summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    interface IPossessionHost
    {
        /// <summary>A Frozen-body possession began: film the creature (Valcast: the NPC mount camera). Hidden bodies keep the player camera.</summary>
        void CameraOn(Character creature);
        /// <summary>The possession ended: the host's camera goes back to normal.</summary>
        void CameraOff();
        float Config(string key, float def);
        bool Config(string key, bool def);
        /// <summary>A short message to the local player (e.g. "Special: Club (2/3)").</summary>
        void Say(string message);
        /// <summary>One engine log line (the host decides the level; lines starting "WARN: " are warnings).</summary>
        void Log(string line);
    }
}
