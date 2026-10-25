// Every HUD element, vanilla and modded, is movable in layout mode:
// the vanilla HUD pieces as FiresDock HUD elements, stage 1. Each is registered once its object exists (polled from
// ShellMinimapWatch.EnsureHudElements) and put back exactly (position + scale) on override OFF. Pieces that are hidden most of
// the time are shown while layout mode is on (ShowInLayout) so they can be placed. Element list + stages:
// Tools\golden\ui_overhaul\hud_layout\INVENTORY.md. Mod elements register through FiresShell.RegisterHudElement.
// Stage 2: bars + food, stagger, stealth, piece author, crosshair (locked); spawned pieces (boss bars, unlock toasts, biome
// banner) dock through slots in ShellHudProxies; the plates in ShellMinimapWatch. Not dockable: world-projected pieces (ship rudder
// controls, speech / NPC bubbles, nameplates, saddle bar).
using System;
using System.Collections.Generic;
using FiresCore.UI.Dock;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    internal static class ShellVanillaHud
    {
        private sealed class Spec
        {
            public string Id, Label; public Func<RectTransform> Get; public bool ShowInLayout, Resizable = true, Locked, AlsoInInventoryLayout, InventorySpot;
        }

        private static RectTransform Rt(GameObject go) => go != null ? go.transform as RectTransform : null;
        private static RectTransform Rt(Component c) => c != null ? c.transform as RectTransform : null;

        private static readonly Spec[] Specs =
        {
            new Spec { Id = "hud.chat",          Label = "Chat",                 Get = () => Chat.instance != null ? Chat.instance.m_chatWindow : null, ShowInLayout = true },
            new Spec { Id = "hud.build.info",    Label = "Build piece info",     Get = () => Rt(Hud.instance?.m_buildHud), ShowInLayout = true },
            // Two build menus: hud.build.menu (Hud.m_pieceSelectionWindow) is gone. That window is dead in Valheim 1.0
            // (only Hud.Awake hides it; the real menu is BuildUi, FAP's fap.buildmenu), and the editor's force-show left it on for good.
            new Spec { Id = "hud.msg.topleft",   Label = "Messages",             Get = () => Rt(MessageHud.instance?.m_messageText), ShowInLayout = true },
            new Spec { Id = "hud.msg.center",    Label = "Centre message",       Get = () => Rt(MessageHud.instance?.m_messageCenterText), ShowInLayout = true },
            new Spec { Id = "hud.hover",         Label = "Hover text",           Get = () => Rt(Hud.instance?.m_hoverName), ShowInLayout = true },
            new Spec { Id = "hud.piecehealth",   Label = "Piece health",         Get = () => Hud.instance?.m_pieceHealthRoot, ShowInLayout = true },
            new Spec { Id = "hud.power",         Label = "Forsaken power",       Get = () => Hud.instance?.m_gpRoot, ShowInLayout = true },
            new Spec { Id = "hud.action",        Label = "Action progress",      Get = () => Rt(Hud.instance?.m_actionBarRoot), ShowInLayout = true },
            new Spec { Id = "hud.mount",         Label = "Mount",                Get = () => Rt(Hud.instance?.m_mountPanel), ShowInLayout = true },
            new Spec { Id = "hud.ship",          Label = "Ship (wind)",          Get = () => Rt(Hud.instance?.m_shipHudRoot), ShowInLayout = true },   // the rudder controls inside stay world-projected (Hud.UpdateShipHud)
            new Spec { Id = "hud.event",         Label = "Raid event",           Get = () => Rt(Hud.instance?.m_eventBar), ShowInLayout = true },
            new Spec { Id = "hud.keyhints",      Label = "Key hints",            Get = () => Rt(KeyHints.instance), ShowInLayout = true, AlsoInInventoryLayout = true, InventorySpot = true },   // its own spot while the inventory is open (was behind the character panel)
            new Spec { Id = "hud.saveicon",      Label = "Save icon",            Get = () => Rt(Hud.instance?.m_saveIcon), ShowInLayout = true, Resizable = false },
            new Spec { Id = "hud.connection",    Label = "Bad connection",       Get = () => Rt(Hud.instance?.m_badConnectionIcon), ShowInLayout = true, Resizable = false },
            // Stage 2. The globes keep their look; only their position docks. Vanilla rewrites the
            // stamina / adrenaline / eitr anchoredPosition every Update (y 130, or 320 / 285 with the build or ship HUD up); the dock
            // re-applies the committed spot every LateUpdate, after that write, so the player's spot wins.
            new Spec { Id = "hud.health",        Label = "Health",               Get = () => Hud.instance?.m_healthPanel, AlsoInInventoryLayout = true },   // also movable from the inventory layout
            new Spec { Id = "hud.stamina",       Label = "Stamina",              Get = () => Hud.instance?.m_staminaBar2Root, ShowInLayout = true },
            new Spec { Id = "hud.eitr",          Label = "Eitr",                 Get = () => Hud.instance?.m_eitrBarRoot, ShowInLayout = true },
            new Spec { Id = "hud.adrenaline",    Label = "Adrenaline",           Get = () => Hud.instance?.m_adrenalineBarRoot, ShowInLayout = true },
            new Spec { Id = "hud.food",          Label = "Food",                 Get = () => Hud.instance?.m_foodBarRoot },
            new Spec { Id = "hud.stagger",       Label = "Stagger",              Get = () => Rt(Hud.instance?.m_staggerProgress), ShowInLayout = true },
            new Spec { Id = "hud.stealth",       Label = "Stealth",              Get = StealthBlock, ShowInLayout = true },
            new Spec { Id = "hud.build.author",  Label = "Piece author",         Get = () => Rt(Hud.instance?.m_hoveredPieceAuthorWindow), ShowInLayout = true, Resizable = false },
            new Spec { Id = "hud.crosshair",     Label = "Crosshair (locked)",   Get = () => Rt(Hud.instance?.m_crosshair), Locked = true, Resizable = false },
        };

        // The stealth bar and its eyes (hidden / targeted / alert) move as one block: their nearest common parent, unless that is the
        // whole HUD (then the bar alone).
        private static RectTransform StealthBlock()
        {
            var h = Hud.instance;
            if (h == null || h.m_stealthBar == null) return null;
            var parts = new List<Transform> { h.m_stealthBar.transform };
            if (h.m_hidden != null) parts.Add(h.m_hidden.transform);
            if (h.m_targeted != null) parts.Add(h.m_targeted.transform);
            if (h.m_targetedAlert != null) parts.Add(h.m_targetedAlert.transform);
            Transform common = parts[0].parent;
            while (common != null && !parts.TrueForAll(p => p.IsChildOf(common))) common = common.parent;
            bool tooHigh = common == null || common == h.transform || (h.m_rootObject != null && common == h.m_rootObject.transform);
            return tooHigh ? h.m_stealthBar.transform as RectTransform : common as RectTransform;
        }

        private static readonly Dictionary<string, (RectTransform rt, Vector3 pos, Vector3 scale, bool active)> s_orig =
            new Dictionary<string, (RectTransform, Vector3, Vector3, bool)>();

        /// <summary>Registers every vanilla piece whose object exists and is not registered yet. Cheap when all are in.</summary>
        internal static void Ensure()
        {
            // Direct hop: entries whose UI was destroyed are forgotten so the new world's pieces register again
            if (s_orig.Count > 0) { List<string> dead = null; foreach (var kv in s_orig) if (kv.Value.rt == null) (dead ?? (dead = new List<string>())).Add(kv.Key); if (dead != null) foreach (var k in dead) s_orig.Remove(k); }
            if (s_orig.Count == Specs.Length) return;
            foreach (var s in Specs)
            {
                if (s_orig.ContainsKey(s.Id)) continue;
                RectTransform rt;
                try { rt = s.Get(); } catch { rt = null; }
                if (rt == null) continue;
                s_orig[s.Id] = (rt, rt.localPosition, rt.localScale, rt.gameObject.activeSelf);
                DockService.Register(new DockElementOptions
                {
                    Id = s.Id, Owner = "vanilla", Layer = "hud", Rect = rt, Label = s.Label, ShowGrip = s.Resizable && !s.Locked, ShowInLayout = s.ShowInLayout, AlsoInInventoryLayout = s.AlsoInInventoryLayout, InventorySpot = s.InventorySpot,
                    Passive = s.Locked, OutlineOnly = s.Locked,
                });
            }
        }

        /// <summary>Override OFF: every piece unregistered and put back exactly where vanilla had it.</summary>
        internal static void Restore()
        {
            foreach (var kv in s_orig)
            {
                DockService.Unregister(kv.Key);
                var o = kv.Value;
                if (o.rt == null) continue;
                o.rt.localScale = o.scale; o.rt.localPosition = o.pos;
            }
            s_orig.Clear();
        }

        /// <summary>Test / log hook: the ids registered now.</summary>
        internal static IEnumerable<string> Registered => s_orig.Keys;
    }
}
