using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Npc
{
    // Companions should never be able to move a player; a player running into a companion moves it instead, passing through
    // their own or other friendly companions.
    // Mass stays as it is (a lighter body went flying when hit). Instead, per pair: Physics.IgnoreCollision
    // between a player's solid colliders and a friendly companion's solid colliders.
    //   friendly = ClassTargeting.IsPartyMember (own companion, or an allied owner's) AND not ClassTargeting.IsEnemyTarget AND the
    //              companion's AI is not targeting that player; dead companions (ragdolls) are left alone.
    //   EVERY peer runs it (client, host, dedicated server): a body is simulated on its ZDO owner, so the owner needs the pair too.
    //   Only non-trigger colliders, never layers or masses. Raycasts (hover / interact), Physics.Overlap* (melee hits) and triggers
    //   ignore IgnoreCollision, so attacking and interacting keep working.
    //   Unity forgets a pair when a collider is disabled / re-created (re-dress, model swap, respawn, mount), so every pair in range
    //   is re-applied every 2 s; state changes are evaluated every 0.5 s.
    // [Companions] "Pass through friendly companions" (default ON). Info line per pair change; Debug line when the pair count moves.
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class CompanionPassThrough
    {
        private const float Range = 30f;
        private const float EvaluateSeconds = 0.5f;
        private const float ReapplySeconds = 2f;

        private static readonly FiresCore.Config.OwnedSetting<bool> s_on = new FiresCore.Config.OwnedSetting<bool>("Companions", "Pass through friendly companions", true);   // 1086 ([generator], Fire 11-01: configs live in their own mods): bound and synced by FiresCompanions
        private sealed class Pair { public Character Player, Body; public bool Ignored; }
        private static readonly Dictionary<long, Pair> s_pairs = new Dictionary<long, Pair>();   // (player, companion) instance ids
        private static readonly List<Collider> s_a = new List<Collider>(), s_b = new List<Collider>();
        private static readonly HashSet<long> s_seen = new HashSet<long>();
        private static readonly List<long> s_gone = new List<long>();
        private static int s_lastCount = -1;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            // 1086 ([generator], Fire 11-01: configs live in their own mods): no Core bind; the owner's change event replaces SettingChanged
            s_on.Changed += () => { if (!s_on.Value) ReleaseAll("cfg off"); };
            var go = new GameObject("FiresCompanionPassThrough");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private float _nextEval, _nextReapply;

            private void Update()
            {
                if (!s_on.Value || ZNetScene.instance == null) return;
                float now = Time.time;
                if (now < _nextEval) return;
                _nextEval = now + EvaluateSeconds;
                bool reapply = now >= _nextReapply;
                if (reapply) _nextReapply = now + ReapplySeconds;
                Sweep(reapply);
            }
        }

        private static void Sweep(bool reapply)
        {
            s_seen.Clear();
            var players = Player.GetAllPlayers();
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                var body = companion.GetComponent<Character>();
                if (body == null || body.IsDead()) continue;
                foreach (var player in players)
                {
                    if (player == null || player.IsDead()) continue;
                    if (Vector3.Distance(player.transform.position, body.transform.position) > Range) continue;
                    long key = Key(player, body);
                    s_seen.Add(key);
                    string reason;
                    bool friendly = Friendly(body, player, out reason);
                    bool had = s_pairs.TryGetValue(key, out Pair was) && was.Ignored;
                    if (friendly != had || (friendly && reapply))
                    {
                        Apply(player, body, friendly);
                        if (friendly != had)
                        {
                            s_pairs[key] = new Pair { Player = player, Body = body, Ignored = friendly };
                            FiresCore.Logging.FiresLogger.LogDebug($"[PassThrough] ignore {(friendly ? "ON" : "OFF")} {player.GetPlayerName()} <-> {body.m_name} ({companion.name}) (reason: {reason})");
                        }
                    }
                }
            }
            // Pairs that left range or despawned: put the contact back if both still exist, then forget them.
            s_gone.Clear();
            foreach (var kv in s_pairs) if (!s_seen.Contains(kv.Key)) s_gone.Add(kv.Key);
            foreach (var key in s_gone)
            {
                // left range, died or despawned: give the bodies their contact back if both still exist, so a pair that turns
                // hostile while apart never comes back still ignored
                var p = s_pairs[key];
                if (p.Ignored && p.Player != null && p.Body != null) Apply(p.Player, p.Body, false);
                s_pairs.Remove(key);
            }
            SweepCompanionPairs(reapply);
            int count = 0;
            foreach (var kv in s_pairs) if (kv.Value.Ignored) count++;
            if (count != s_lastCount)
            {
                s_lastCount = count;
                FiresUnifiedCore.Log?.LogDebug($"[PassThrough] {count} friendly pair(s) passing through");
            }
        }

        // 0.2.848 ([ghost], Fire 10-26: "companions get stuck on eachother and push eachother away from benches, companions should have
        // the same collision logic with eachother as they do with players"; shot: Wulf and Knut jammed on one spot): companions of the same
        // owner pass through each other exactly as a player and his companions do (allied owners' companions still block each other) - IgnoreCollision between their solid colliders, re-applied every 2 s, given back when they part or turn hostile.
        private static readonly Dictionary<long, Pair> s_cpairs = new Dictionary<long, Pair>();
        private static readonly HashSet<long> s_cseen = new HashSet<long>();
        private static readonly List<CompanionController> s_live = new List<CompanionController>();
        private static int s_lastCompanionCount = -1;

        private static void SweepCompanionPairs(bool reapply)
        {
            s_cseen.Clear();
            s_live.Clear();
            foreach (var c in CompanionController.AllCompanions)
            {
                var ch = c != null ? c.GetComponent<Character>() : null;
                if (ch != null && !ch.IsDead() && c.isTamed && c.ownerPlayerId != 0L) s_live.Add(c);
            }
            for (int i = 0; i < s_live.Count; i++)
                for (int j = i + 1; j < s_live.Count; j++)
                {
                    var a = s_live[i]; var b = s_live[j];
                    var ca = a.GetComponent<Character>(); var cb = b.GetComponent<Character>();
                    if (Vector3.Distance(ca.transform.position, cb.transform.position) > Range) continue;
                    bool same = a.ownerPlayerId == b.ownerPlayerId;
                    if (!same) continue;   // allied owners: their companions still block (party check needs a player on each side)
                    long key = Key(ca, cb);
                    s_cseen.Add(key);
                    bool had = s_cpairs.TryGetValue(key, out Pair was) && was.Ignored;
                    if (!had || reapply)
                    {
                        Apply(ca, cb, true);
                        if (!had) s_cpairs[key] = new Pair { Player = ca, Body = cb, Ignored = true };
                    }
                }
            s_gone.Clear();
            foreach (var kv in s_cpairs) if (!s_cseen.Contains(kv.Key)) s_gone.Add(kv.Key);
            foreach (var key in s_gone)
            {
                var p = s_cpairs[key];
                if (p.Ignored && p.Player != null && p.Body != null) Apply(p.Player, p.Body, false);
                s_cpairs.Remove(key);
            }
            if (s_cpairs.Count != s_lastCompanionCount)
            {
                s_lastCompanionCount = s_cpairs.Count;
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionCollision] ignoring {s_cpairs.Count} companion pair(s) (same owner, within {Range:0} m: they pass through each other like a player and his companions)");
            }
        }

        private static bool Friendly(Character companion, Player player, out string reason)
        {
            if (!FiresCore.Npc.Archetypes.ClassTargeting.IsPartyMember(companion, player)) { reason = "not the party"; return false; }
            if (FiresCore.Npc.Archetypes.ClassTargeting.IsEnemyTarget(companion, player)) { reason = "hostile"; return false; }
            var ai = companion.GetBaseAI() as MonsterAI;
            if (ai != null && ai.GetTargetCreature() == player) { reason = "aggro"; return false; }
            reason = ClassTargeting_PartyOwner(companion) == player.GetPlayerID() ? "own" : "allied";
            return true;
        }

        private static long ClassTargeting_PartyOwner(Character companion)
        {
            var c = companion.GetComponent<CompanionController>();
            var zdo = companion.GetComponent<ZNetView>()?.GetZDO();
            long owner = zdo != null ? zdo.GetLong("companion_owner", 0L)   /* ClassTargeting.CompanionOwnerKey (private) */ : 0L;
            return owner != 0L ? owner : (c != null ? c.ownerPlayerId : 0L);
        }

        private static void Apply(Character a, Character b, bool ignore)
        {
            Solid(a, s_a); Solid(b, s_b);
            foreach (var x in s_a) foreach (var y in s_b) Physics.IgnoreCollision(x, y, ignore);
        }

        private static void Solid(Character c, List<Collider> into)
        {
            into.Clear();
            c.GetComponentsInChildren(true, into);
            into.RemoveAll(col => col == null || col.isTrigger);
        }

        private static void ReleaseAll(string why)
        {
            int n = 0;
            foreach (var kv in s_pairs) if (kv.Value.Ignored) n++;
            s_pairs.Clear();
            s_lastCount = -1;
            // 0.2.848: companion <-> companion pairs get their contact back too
            foreach (var kv in s_cpairs) if (kv.Value.Player != null && kv.Value.Body != null) Apply(kv.Value.Player, kv.Value.Body, false);
            s_cpairs.Clear();
            s_lastCompanionCount = -1;
            // Restoring contact needs both bodies; re-scan the live ones.
            foreach (var companion in CompanionController.AllCompanions)
            {
                var body = companion != null ? companion.GetComponent<Character>() : null;
                if (body == null) continue;
                foreach (var player in Player.GetAllPlayers()) if (player != null) Apply(player, body, false);
            }
            FiresUnifiedCore.Log?.LogInfo($"[PassThrough] {n} pair(s) released ({why}); bodies collide again");
        }

        // For CompanionPersonalSpaceEnforcer: true while this player <-> companion pair passes through each
        // other, so the enforcer leaves that pair alone. Cheap (one dictionary lookup); false when the feature is off.
        internal static bool IsIgnored(Character player, Character companion)
        {
            if (player == null || companion == null || !s_on.Value) return false;
            return s_pairs.TryGetValue(Key(player, companion), out var p) && p.Ignored;
        }

        private static long Key(Character player, Character companion) => ((long)player.GetInstanceID() << 32) ^ (uint)companion.GetInstanceID();
    }
}
