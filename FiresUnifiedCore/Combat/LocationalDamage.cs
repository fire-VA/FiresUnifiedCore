using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.Logging;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Combat
{
    /// <summary>
    /// Locational damage: players' hits (on players and creatures) scaled by the body part they land on, in the attacker's view.
    /// Dormant: Core ships it off; a mod turns it on with Enable (FiresRPGClasses, the tests). The server's state decides for
    /// everyone (a synced value), and a server admin can force it on or off. Patches attach the first time it turns on and stay.
    ///
    /// Fire's rule (2026-09-28): a HEAD hit is scaled before armour (the attacker scales the raw hit); every other part after
    /// armour (the victim's owner scales vanilla's armoured damage). The part travels as FC_HitPart, sent to the victim's owner
    /// ahead of vanilla's RPC_Damage on the same route, matched to the hit by victim, attacker and point, and applied only to that
    /// same HitData object. Design and review: Tools\LOCATIONAL_HITS.md.
    /// Intended by that rule: vanilla armour is quadratic for hits under twice the armour (dmg²/4ac), so a head hit's x1.5 before
    /// armour lands as more than x1.5 (x2.25 in that band: headshots punch through armour), while the other parts land exactly.
    /// </summary>
    public static class LocationalDamage
    {
        public enum Override { Auto, ForceOn, ForceOff }

        private const string SyncId = "FiresCore.LocationalDamage";
        private const string HitPartRpc = "FC_HitPart";
        private const float PendingSeconds = 2f;
        private const float PointMatchSquared = 1e-6f;
        private const char OwnerSeparator = '|';
        private const char EntrySeparator = ';';
        private const char ValueSeparator = '=';
        private const string ForcedOwner = "server admin (ForceOn)";

        // Fire's "Moderate" table (2026-09-28): head 1.5, neck/chest 1.1, torso 1.0, arms 0.8, legs 0.7.
        public static readonly IReadOnlyDictionary<BodyPart, float> DefaultMultipliers = new Dictionary<BodyPart, float>
        {
            [BodyPart.Head] = 1.5f,
            [BodyPart.Neck] = 1.1f,
            [BodyPart.Chest] = 1.1f,
            [BodyPart.Torso] = 1.0f,
            [BodyPart.UpperArm] = 0.8f,
            [BodyPart.Forearm] = 0.8f,
            [BodyPart.Hand] = 0.8f,
            [BodyPart.Thigh] = 0.7f,
            [BodyPart.Shin] = 0.7f,
            [BodyPart.Foot] = 0.7f,
        };

        private sealed class Pending
        {
            public ZDOID Victim;
            public ZDOID Attacker;
            public BodyPart Part;
            public float Multiplier;
            public bool HeadApplied;
            public Vector3 Point;
            public float At;
            public HitData Hit;   // the very HitData vanilla's RPC_Damage received; only that object is scaled in ApplyDamage
        }

        private static ConfigEntry<Override> s_override;
        private static ConfigEntry<bool> s_verbose;
        private static CustomSyncedValue<string> s_state;
        private static Harmony s_harmony;
        private static bool s_attached;
        private static ZRoutedRpc s_rpcRegisteredOn;
        private static Dictionary<BodyPart, float> s_table = CopyOfDefaults();
        // In the order they enabled it: the first owner's table is the one in effect.
        private static readonly List<(string Owner, IReadOnlyDictionary<BodyPart, float> Table)> s_enablers =
            new List<(string, IReadOnlyDictionary<BodyPart, float>)>();
        private static readonly List<(string Owner, Func<Character, Character, HitLocation, float, float> Modify)> s_modifiers =
            new List<(string, Func<Character, Character, HitLocation, float, float>)>();
        private static readonly List<Pending> s_pending = new List<Pending>();
        private static Pending s_current;

        /// <summary>On for this server (the synced state, after the admin override).</summary>
        public static bool Active { get; private set; }

        /// <summary>Who turned it on ("" when off).</summary>
        public static string EnabledBy { get; private set; } = string.Empty;

        /// <summary>Raised on the attacker's client for every located hit: attacker, victim, hit, where, the multiplier.</summary>
        public static event Action<Character, Character, HitData, HitLocation, float> Applied;

        /// <summary>
        /// Raised on the VICTIM's owner when the shooter's part for a hit arrives and is matched to that hit (RPC_Damage): victim,
        /// attacker (null if not loaded here), part, point, multiplier. The one truth for victim-side consumers (feedback, armour wear
        /// by part, UI, tests); never re-locate a hit on the victim ([fgn], R71).
        /// </summary>
        public static event Action<Character, Character, BodyPart, Vector3, float> Received;

        // For the log only: never GetHoverName (0.2.215, R86 dedi: a tameable's GetHoverName -> Tameable.GetName threw a NullReference
        // inside RPC_Damage and the hit was lost). The prefab's name token, or the player's name.
        private static string NameOf(Character c)
        {
            if (c == null) return "?";
            try { return c is Player p ? p.GetPlayerName() : c.m_name; }
            catch { return "?"; }
        }

        public static void Register(Harmony harmony, ConfigFile config)
        {
            s_harmony = harmony;
            s_override = config.Bind("Combat", "Locational Damage", Override.Auto,
                "SERVER. Auto: on when a mod turns it on (e.g. FiresRPGClasses); ForceOn / ForceOff: decided here regardless. Players' hits "
                + "then scale by body part: head x1.5 before armour; neck/chest x1.1, torso x1, arms x0.8, legs x0.7 after armour. "
                + "Because vanilla armour cuts small hits harder, a head hit's x1.5 before armour lands as MORE than x1.5 through armour "
                + "(about x2.25 when the hit and the armour are close); the other parts land exactly as listed.");
            s_verbose = config.Bind("Combat", "Locational Damage Log Every Hit", false, "One log line per located hit (testing).");
            s_override.SettingChanged += (_, __) => Recompute();
            HitLocator.RegisterCommand();
        }

        public static void BindToSync(ConfigSync configSync)
        {
            s_state = new CustomSyncedValue<string>(configSync, SyncId, string.Empty);
            s_state.ValueChanged += ApplyState;
            Recompute();   // a mod may have enabled it before Core bound its sync
        }

        /// <summary>From Core's ZNet.Start postfix: this session's RPC registration (patches stay attached across sessions).</summary>
        internal static void OnSessionStart() => EnsureRpc();

        /// <summary>Turns it on for this server (a mod on the server, or single player); idempotent. A table overrides the defaults;
        /// with several mods, the first to enable decides the table.</summary>
        public static void Enable(string owner, IReadOnlyDictionary<BodyPart, float> multipliers = null)
        {
            string name = OwnerName(owner);
            int index = s_enablers.FindIndex(e => e.Owner == name);
            if (index >= 0) s_enablers[index] = (name, multipliers);
            else s_enablers.Add((name, multipliers));
            Recompute();
        }

        public static void Disable(string owner)
        {
            if (s_enablers.RemoveAll(e => e.Owner == OwnerName(owner)) > 0) Recompute();
        }

        private static string OwnerName(string owner) =>
            string.IsNullOrEmpty(owner) ? "?" : owner.Replace(OwnerSeparator, '/').Replace(EntrySeparator, '/');

        // .NET Framework's Dictionary has no constructor taking an IReadOnlyDictionary.
        private static Dictionary<BodyPart, float> CopyOfDefaults() => DefaultMultipliers.ToDictionary(kv => kv.Key, kv => kv.Value);

        public static float Multiplier(BodyPart part) => s_table.TryGetValue(part, out float value) ? value : 1f;

        /// <summary>Lets a mod adjust one hit's multiplier (attacker, victim, where, multiplier so far) → new multiplier.</summary>
        public static void AddModifier(string owner, Func<Character, Character, HitLocation, float, float> modify)
        {
            if (modify != null) s_modifiers.Add((owner ?? "?", modify));
        }

        // ------------------------------------------------------------ state

        /// <summary>Only a server (or single player, before any world) decides; a client follows the synced value.</summary>
        private static void Recompute()
        {
            if (s_state == null) return;
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;
            Override mode = s_override?.Value ?? Override.Auto;
            bool on = mode == Override.ForceOn || (mode == Override.Auto && s_enablers.Count > 0);
            string owners = mode == Override.ForceOn ? ForcedOwner : string.Join(", ", s_enablers.Select(e => e.Owner));
            IReadOnlyDictionary<BodyPart, float> table = s_enablers.Count > 0 ? s_enablers[0].Table : null;
            s_state.AssignLocalValue(on ? Encode(owners, table ?? DefaultMultipliers) : string.Empty);
        }

        private static void ApplyState()
        {
            string value = s_state?.Value ?? string.Empty;
            bool on = Decode(value, out string owner, out Dictionary<BodyPart, float> table);
            bool changed = on != Active || owner != EnabledBy;
            Active = on;
            EnabledBy = on ? owner : string.Empty;
            s_table = table ?? CopyOfDefaults();
            if (on) Attach();
            if (changed)
                FiresLogger.LogInfo(on ? $"[LocationalDamage] ON, enabled by {EnabledBy}: {Describe(s_table)}" : "[LocationalDamage] off");
        }

        /// <summary>The one hook of its own, attached the first time it turns on and kept (Core's PatchAll must not see it, so it has
        /// no attribute): after armour. The attacker's and the victim's steps ride Core's existing Character.Damage and
        /// RPC_Damage prefixes (VikHavnBridge's kick patch, CompanionPatches), which call OnAttackerHit / OnVictimHit.</summary>
        private static void Attach()
        {
            EnsureRpc();
            if (s_attached || s_harmony == null) return;
            s_attached = true;
            try
            {
                s_harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ApplyDamage)),
                    prefix: new HarmonyMethod(typeof(LocationalDamage), nameof(AfterArmour)));
            }
            catch (Exception ex) { FiresLogger.LogError($"[LocationalDamage] couldn't attach ({ex.GetBaseException().Message}); hits stay vanilla."); }
        }

        private static void EnsureRpc()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_rpcRegisteredOn)) return;
            rpc.Register<ZPackage>(HitPartRpc, RPC_HitPart);
            rpc.Register<ZPackage>(HitFeedbackRpc, RPC_HitFeedback);
            s_rpcRegisteredOn = rpc;
        }

        private static string Encode(string owner, IReadOnlyDictionary<BodyPart, float> table) =>
            owner + OwnerSeparator + string.Join(EntrySeparator.ToString(),
                table.Select(kv => kv.Key + ValueSeparator.ToString() + kv.Value.ToString("0.###", CultureInfo.InvariantCulture)));

        private static bool Decode(string value, out string owner, out Dictionary<BodyPart, float> table)
        {
            owner = string.Empty;
            table = null;
            if (string.IsNullOrEmpty(value)) return false;
            int split = value.IndexOf(OwnerSeparator);
            owner = split >= 0 ? value.Substring(0, split) : value;
            table = CopyOfDefaults();
            if (split < 0) return true;
            foreach (string entry in value.Substring(split + 1).Split(EntrySeparator))
            {
                string[] pair = entry.Split(ValueSeparator);
                if (pair.Length == 2 && Enum.TryParse(pair[0], out BodyPart part)
                    && float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier))
                    table[part] = multiplier;
            }
            return true;
        }

        private static string Describe(Dictionary<BodyPart, float> table) =>
            string.Join(", ", table.Select(kv => $"{kv.Key} x{kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));

        // ------------------------------------------------------------ the hit

        private static void RPC_HitPart(long sender, ZPackage pkg)
        {
            float now = Time.time;
            s_pending.RemoveAll(p => now - p.At > PendingSeconds);
            var pending = new Pending
            {
                Victim = pkg.ReadZDOID(),
                Attacker = pkg.ReadZDOID(),
                Part = (BodyPart)pkg.ReadInt(),
                Multiplier = pkg.ReadSingle(),
                HeadApplied = pkg.ReadBool(),
                Point = pkg.ReadVector3(),
                At = now,
            };
            s_pending.Add(pending);
        }

        // Fire (popups 2026-09-28): every hit plays one of the archery target's bullseyes by body part (tiers below), with
        // its hit sound, at the hit point, only while locational damage is on, for everyone near the hit. The shooter plays it at
        // once and sends one small routed RPC; every other peer within FeedbackRange of the hit plays it too. Found through the
        // target piece itself ([fgn], the 1.0 rip): effect prefabs aren't networked prefabs, so ZNetScene can't find them by name.
        /// <summary>This peer played a hit's feedback: the part, the point, and true when this peer is the shooter (else a nearby
        /// player). Raised only while locational damage is active and never on a headless peer.</summary>
        public static event Action<BodyPart, Vector3, bool> HitFeedbackPlayed;

        // FC_HitFeedback2 (0.2.201): a package (part, point, shooter) instead of 0.2.200's (int, Vector3), under a new name so a
        // peer still on 0.2.200 never misreads it.
        private const string HitFeedbackRpc = "FC_HitFeedback2";
        private const float FeedbackRange = 50f;
        // Fire (2026-09-29 21:0x, message + popup): the tier reads at a glance - HEAD plays the target's FULL bullseye, CHEST and
        // NECK the DOUBLE one (the multicoloured burst), every other part the plain bullseye. Each one faces the shooter.
        private enum Tier { Plain, Double, Full }
        private static readonly string[] TierNames = { "plain bullseye", "double bullseye", "full bullseye" };
        private static readonly EffectList[] s_tier = new EffectList[3];
        // Hit sparks per tier, only for a tier whose list draws nothing (never on top of a visible burst).
        private static readonly EffectList[] s_tierSparks = new EffectList[3];
        private static EffectList s_hitSound, s_headSound;
        private const string HeadSparks = "vfx_HitSparks", HeadSound = "sfx_perfectblock";
        private static bool s_feedbackResolved;

        private static Tier TierOf(BodyPart part) =>
            part == BodyPart.Head ? Tier.Full : part == BodyPart.Chest || part == BodyPart.Neck ? Tier.Double : Tier.Plain;

        private static void ShareHitFeedback(BodyPart part, Vector3 point, Vector3 shooter)
        {
            PlayHitFeedback(part, point, shooter, "shooter");
            if (!Active || ZRoutedRpc.instance == null) return;
            EnsureRpc();
            var pkg = new ZPackage();
            pkg.Write((int)part);
            pkg.Write(point);
            pkg.Write(shooter);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, HitFeedbackRpc, pkg);
        }

        private static void RPC_HitFeedback(long sender, ZPackage pkg)
        {
            if (sender == ZDOMan.GetSessionID()) return;
            var part = (BodyPart)pkg.ReadInt();
            Vector3 point = pkg.ReadVector3(), shooter = pkg.ReadVector3();
            Player me = Player.m_localPlayer;
            if (me == null || Vector3.Distance(me.transform.position, point) > FeedbackRange) return;
            PlayHitFeedback(part, point, shooter, "nearby");
        }

        private static void PlayHitFeedback(BodyPart part, Vector3 point, Vector3 shooter, string seenAs)
        {
            if (!Active || part == BodyPart.Unknown || FiresCore.Lifecycle.FiresMod.IsHeadless) return;
            ResolveFeedback();
            try
            {
                // Facing the shooter, as the target board faces the archer (vanilla creates its bullseyes with the board's rotation;
                // identity pointed a directional burst up or sideways).
                Vector3 toShooter = shooter - point;
                Quaternion facing = toShooter.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toShooter.normalized) : Quaternion.identity;
                Tier tier = TierOf(part);
                s_hitSound?.Create(point, facing);
                s_tier[(int)tier]?.Create(point, facing);
                s_tierSparks[(int)tier]?.Create(point, facing);
                if (part == BodyPart.Head) s_headSound?.Create(point, facing);
                // What a test asserts on each peer (pvp_test): the kind of feedback, where, and whether this peer shot or saw it.
                HitFeedbackPlayed?.Invoke(part, point, seenAs == "shooter");
                if (s_verbose.Value)
                {
                    string shown = $"{TierNames[(int)tier]} {Count(s_tier[(int)tier])}" +
                                   (s_tierSparks[(int)tier] != null ? $" + sparks {Count(s_tierSparks[(int)tier])}" : "") +
                                   (part == BodyPart.Head && s_headSound != null ? $" + sound {Count(s_headSound)}" : "");
                    FiresLogger.LogInfo($"[LocationalDamage] feedback {(part == BodyPart.Head ? "headshot" : "bodyshot")} ({part}) " +
                                        $"at {point.x:0.0},{point.y:0.0},{point.z:0.0}, as the {seenAs}: {shown} prefab(s)");
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[LocationalDamage] hit feedback failed ({ex.Message})."); }
        }

        private static void ResolveFeedback()
        {
            if (s_feedbackResolved || ZNetScene.instance == null) return;
            s_feedbackResolved = true;
            GameObject target = ZNetScene.instance.GetPrefab("piece_ArcheryTarget");
            if (target == null) { FiresLogger.LogWarning("[LocationalDamage] no piece_ArcheryTarget prefab; hits play no feedback."); return; }
            ArcheryTarget archery = target.GetComponentInChildren<ArcheryTarget>(true);
            if (archery != null)
            {
                s_tier[(int)Tier.Plain] = archery.m_bullsEyeEffect;
                s_tier[(int)Tier.Double] = archery.m_doubleBullsEyeEffect;
                s_tier[(int)Tier.Full] = archery.m_fullBullsEyeEffect;
                if (archery.m_projectileHitEffects != null && archery.m_projectileHitEffects.Count > 0)
                    s_hitSound = archery.m_projectileHitEffects[0].m_effect;
            }
            // Fire's popups (2026-09-29 18:5x, 20:4x, 21:0x): hit sparks only for a tier whose list has no renderer (an empty or
            // audio-only list would otherwise show nothing); the perfect-block ring on head hits stays unless the head burst
            // carries its own sound.
            var sparksFor = new List<string>();
            for (int i = 0; i < s_tier.Length; i++)
            {
                s_tierSparks[i] = HasRenderer(s_tier[i]) ? null : Fallback(HeadSparks);
                if (s_tierSparks[i] != null) sparksFor.Add(TierNames[i]);
            }
            bool headHasSound = HasAudio(s_tier[(int)Tier.Full]);
            s_headSound = headHasSound ? null : Fallback(HeadSound);
            FiresLogger.LogInfo($"[LocationalDamage] feedback effects: head -> full bullseye {Count(s_tier[(int)Tier.Full])}, chest + neck -> double bullseye {Count(s_tier[(int)Tier.Double])}, " +
                                $"other parts -> plain bullseye {Count(s_tier[(int)Tier.Plain])}; sparks ({HeadSparks}) {(sparksFor.Count == 0 ? "off (every tier is visible)" : "for " + string.Join(", ", sparksFor) + " (no renderer)")}; " +
                                $"head ring ({HeadSound}) {(headHasSound ? "off (the full burst has its own sound)" : $"on {Count(s_headSound)}")}; hit sound {Count(s_hitSound)}");
            for (int i = 0; i < s_tier.Length; i++) DescribeEffects(TierNames[i], s_tier[i]);
        }

        // Once per list: each entry's prefab and EffectData flags, and whether it draws anything or is audio only (pvp round proof).
        private static void DescribeEffects(string label, EffectList effects)
        {
            if (effects?.m_effectPrefabs == null || effects.m_effectPrefabs.Length == 0)
            {
                FiresLogger.LogInfo($"[LocationalDamage] {label}: no entries");
                return;
            }
            foreach (var e in effects.m_effectPrefabs)
            {
                if (e == null) continue;
                string prefab = e.m_prefab != null ? e.m_prefab.name : "(null)";
                FiresLogger.LogInfo($"[LocationalDamage] {label}: {prefab} enabled {e.m_enabled}, attach {e.m_attach}, inheritParentRotation {e.m_inheritParentRotation}, " +
                                    $"inheritParentScale {e.m_inheritParentScale}, multiplyParentVisualScale {e.m_multiplyParentVisualScale}, variant {e.m_variant}, " +
                                    $"scale {e.m_scale}, randomRotation {e.m_randomRotation} -> {Kind(e.m_prefab)}");
            }
        }

        private static string Kind(GameObject prefab)
        {
            if (prefab == null) return "nothing";
            bool visual = prefab.GetComponentsInChildren<Renderer>(true).Length > 0;
            bool audio = prefab.GetComponentsInChildren<AudioSource>(true).Length > 0 || prefab.GetComponentsInChildren<ZSFX>(true).Length > 0;
            return visual && audio ? "renderer + audio" : visual ? "renderer" : audio ? "audio only" : "no renderer, no audio";
        }

        private static bool HasRenderer(EffectList effects) => Any(effects, p => p.GetComponentsInChildren<Renderer>(true).Length > 0);

        private static bool HasAudio(EffectList effects) =>
            Any(effects, p => p.GetComponentsInChildren<AudioSource>(true).Length > 0 || p.GetComponentsInChildren<ZSFX>(true).Length > 0);

        private static bool Any(EffectList effects, Func<GameObject, bool> test)
        {
            if (effects?.m_effectPrefabs == null) return false;
            foreach (var e in effects.m_effectPrefabs)
                if (e != null && e.m_enabled && e.m_prefab != null && test(e.m_prefab)) return true;
            return false;
        }

        // An EffectList of the named prefabs that exist (none found: an empty list, still harmless to Create).
        private static EffectList Fallback(params string[] prefabs)
        {
            var list = new List<EffectList.EffectData>();
            foreach (string name in prefabs)
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (prefab != null) list.Add(new EffectList.EffectData { m_prefab = prefab, m_enabled = true });
            }
            return new EffectList { m_effectPrefabs = list.ToArray() };
        }

        private static int Count(EffectList effects)
        {
            if (effects?.m_effectPrefabs == null) return 0;
            int n = 0;
            foreach (var e in effects.m_effectPrefabs) if (e != null && e.m_prefab != null && e.m_enabled) n++;
            return n;
        }

        private static float Modified(Character attacker, Character victim, HitLocation where, float multiplier)
        {
            foreach (var modifier in s_modifiers)
            {
                try { multiplier = modifier.Modify(attacker, victim, where, multiplier); }
                catch (Exception ex) { FiresLogger.LogWarning($"[LocationalDamage] {modifier.Owner}'s modifier threw: {ex.Message}"); }
            }
            return multiplier;
        }

        /// <summary>
        /// The attacker's step, from Core's Character.Damage prefix (before the hit is sent to the victim's owner): locate the hit
        /// as this client draws the victim, scale a head hit before armour, and send the part ahead of vanilla's RPC_Damage.
        /// Only the local player's own hits (not companions or tames it owns).
        /// </summary>
        internal static void OnAttackerHit(Character victim, HitData hit)
        {
            if (!Active) return;
            try
            {
                Player me = Player.m_localPlayer;
                if (hit == null || victim == null || me == null || victim == me || hit.GetAttacker() != me) return;
                ZNetView view = victim.GetComponent<ZNetView>();
                ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return;
                HitLocation where = HitLocator.Locate(victim, hit.m_point, hit.m_dir);
                if (where.Part == BodyPart.Unknown) return;
                float multiplier = Modified(me, victim, where, Multiplier(where.Part));
                bool head = where.Part == BodyPart.Head;
                if (head)
                {
                    float before = hit.GetTotalDamage();
                    hit.m_damage.Modify(multiplier);
                    // Always on (Fire: "prove they are actually applying the damage multipliers").
                    FiresLogger.LogInfo($"[LocationalDamage] applied Head x{multiplier:0.##} before armour on {NameOf(victim)}: {before:0.0} -> {hit.GetTotalDamage():0.0}");
                }
                EnsureRpc();
                var pkg = new ZPackage();
                pkg.Write(zdo.m_uid);
                pkg.Write(me.GetZDOID());
                pkg.Write((int)where.Part);
                pkg.Write(multiplier);
                pkg.Write(head);
                pkg.Write(hit.m_point);
                ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), HitPartRpc, pkg);
                // Always on while active (the pvp tests quote it): the part, how it was picked, the bone and its distance.
                FiresLogger.LogInfo($"[LocationalDamage] hit {NameOf(victim)} in the {where.Part} ({HitLocator.LastMethod}; {where.Distance:0.00} m from "
                                    + $"{where.Bone}) x{multiplier:0.##} ({(head ? "before" : "after")} armour)");
                Applied?.Invoke(me, victim, hit, where, multiplier);
                ShareHitFeedback(where.Part, hit.m_point, me.GetCenterPoint());
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[LocationalDamage] locate failed ({ex.GetType().Name}: {ex.Message}); the hit stays vanilla.");
            }
        }

        /// <summary>
        /// The victim's owner, from Core's RPC_Damage prefix: pick up the part sent for exactly this hit (same victim, attacker and
        /// point) and remember the HitData object, so only this hit is scaled after armour, even if vanilla returns early
        /// (dodge, parry, invulnerable) and the next ApplyDamage belongs to something else.
        /// </summary>
        internal static void OnVictimHit(Character victim, HitData hit)
        {
            EnsureRpc();
            s_current = null;
            if (!Active || hit == null || victim == null || s_pending.Count == 0) return;
            ZDOID id = victim.GetZDOID();
            int index = s_pending.FindIndex(p => p.Victim == id && p.Attacker == hit.m_attacker && (p.Point - hit.m_point).sqrMagnitude <= PointMatchSquared);
            if (index < 0) return;
            s_current = s_pending[index];
            s_current.Hit = hit;
            s_pending.RemoveAt(index);
            try
            {
                GameObject attackerObject = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(s_current.Attacker) : null;
                Character attacker = attackerObject != null ? attackerObject.GetComponent<Character>() : null;
                FiresLogger.LogInfo($"[LocationalDamage] received {s_current.Part} x{s_current.Multiplier:0.##} on {NameOf(victim)} from {NameOf(attacker)}");
                Received?.Invoke(victim, attacker, s_current.Part, s_current.Point, s_current.Multiplier);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[LocationalDamage] a Received listener failed: {ex.Message}");
            }
        }

        /// <summary>The victim's owner, after vanilla's armour: a non-head part scales this hit's damage, once.</summary>
        private static void AfterArmour(Character __instance, HitData hit)
        {
            Pending current = s_current;
            if (current == null || hit == null || !ReferenceEquals(hit, current.Hit)) return;
            s_current = null;
            if (current.HeadApplied) return;
            float before = hit.GetTotalDamage();
            hit.m_damage.Modify(current.Multiplier);
            // Always on: the part's multiplier as it lands on the health change (after vanilla's armour and resistances).
            FiresLogger.LogInfo($"[LocationalDamage] applied {current.Part} x{current.Multiplier:0.##} after armour on {NameOf(__instance)}: {before:0.0} -> {hit.GetTotalDamage():0.0}");
            if (s_verbose.Value) FiresLogger.LogInfo($"[LocationalDamage] {__instance?.m_name} took a {current.Part} hit x{current.Multiplier:0.##} after armour");
        }
    }
}
