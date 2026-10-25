using System;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Movement
{
    /// <summary>
    /// Extra movements can be unlocked through playing. PUBLIC, add-only contract.
    /// Core never references RPGClasses: classes and races register delegates at load, keyed by stable string ids.
    ///
    /// MovementGates: who has a move unlocked. Core's own gates read vanilla skills (double jump = Jump, run slide = Run); a mod adds
    ///   its own (a class level, a race stage) with Register. A move is unlocked when ANY gate for it says yes, or "[Movement] Unlock all"
    ///   is on (server-synced, for tests). The first time a gate opens for the local player Core shows a centre message once and logs it.
    /// MovementStyles: how the move looks. For a move ("second jump", "dodge") the highest-priority registered style whose Applies(player)
    ///   is true runs; none = Core's generic (a plain second jump; the vanilla roll). A style's Perform returns false to fall back.
    /// </summary>
    public static class MovementGates
    {
        public const string DoubleJump = "double_jump", RunSlide = "run_slide", SecondJumpStyle = "second_jump_style", DodgeStyle = "dodge_style";

        private sealed class Gate { public string Move, Id, Label, Owner; public Func<Player, bool> Open; }
        private static readonly List<Gate> s_gates = new List<Gate>();
        private static readonly HashSet<string> s_announced = new HashSet<string>();

        /// <summary>Adds a gate: <paramref name="move"/> is unlocked for a player when <paramref name="open"/> says so. Same move+id replaces.</summary>
        public static void Register(string move, string id, Func<Player, bool> open, string label, string owner)
        {
            if (string.IsNullOrEmpty(move) || string.IsNullOrEmpty(id) || open == null) return;
            s_gates.RemoveAll(g => g.Move == move && g.Id == id);
            s_gates.Add(new Gate { Move = move, Id = id, Open = open, Label = label ?? id, Owner = owner ?? "?" });
            if (owner != "Core") CoreMovement.FeatureFor(move)?.MarkConsumer(owner);   // Core's own gates don't make Auto active
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] gate registered: {move} <- {id} ('{label}', by {owner})");
        }

        /// <summary>True when the move is unlocked for the player; <paramref name="by"/> names the gate (or "unlock all").</summary>
        public static bool Unlocked(Player p, string move, out string by)
        {
            by = null;
            if (p == null) return false;
            if (CoreMovement.UnlockAll) { by = "unlock all"; return true; }
            foreach (var g in s_gates)
            {
                if (g.Move != move) continue;
                bool open; try { open = g.Open(p); } catch { open = false; }
                if (!open) continue;
                by = g.Id;
                if (p == Player.m_localPlayer && s_announced.Add(move + "|" + g.Id))
                {
                    p.Message(MessageHud.MessageType.Center, $"Unlocked: {g.Label}");
                    FiresCore.Logging.FiresLogger.LogDebug($"[Movement] unlocked for {p.GetPlayerName()}: {move} by gate {g.Id} ('{g.Label}')");
                }
                return true;
            }
            return false;
        }
    }

    public static class MovementStyles
    {
        private sealed class Style { public string Move, Id, Owner; public int Priority; public Func<Player, bool> Applies; public Func<Player, bool> Perform; }
        private static readonly List<Style> s_styles = new List<Style>();

        /// <summary>Adds a style for a move ("second_jump_style" / "dodge_style"); higher priority wins among the ones that apply.</summary>
        public static void Register(string move, string id, int priority, Func<Player, bool> applies, Func<Player, bool> perform, string owner)
        {
            if (string.IsNullOrEmpty(move) || string.IsNullOrEmpty(id) || applies == null || perform == null) return;
            s_styles.RemoveAll(s => s.Move == move && s.Id == id);
            s_styles.Add(new Style { Move = move, Id = id, Priority = priority, Applies = applies, Perform = perform, Owner = owner ?? "?" });
            CoreMovement.Styles.MarkConsumer(owner);
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] style registered: {move} <- {id} (priority {priority}, by {owner})");
        }

        /// <summary>Runs the best style for the move; false = none applied or it declined (the caller does the generic move).</summary>
        public static bool TryPerform(Player p, string move, out string id)
        {
            id = null;
            if (p == null || !CoreMovement.Styles.IsActive) return false;
            foreach (var s in s_styles.Where(x => x.Move == move).OrderByDescending(x => x.Priority))
            {
                bool applies; try { applies = s.Applies(p); } catch { applies = false; }
                if (!applies) continue;
                bool done; try { done = s.Perform(p); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[Movement] style {s.Id} threw {e.GetType().Name}: {e.Message}"); done = false; }
                if (done) { id = s.Id; return true; }
            }
            return false;
        }
    }
}
