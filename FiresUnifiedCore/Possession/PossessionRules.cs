using BepInEx.Configuration;
using FiresCore.Input;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// [Possession] config, the general "can this be possessed" rules, the crosshair pick and the input guard.
    /// Admin possession is OFF by default (Fire 10-07); class grants don't need it on.
    /// </summary>
    internal static class PossessionRules
    {
        internal const string Tag = "[Possession] ";
        internal const string AdminTag = "core-admin";
        /// <summary>FGN and Core's NPC brain gate on this key; it holds the possessor's player id (never a peer uid).</summary>
        internal const string PossessedKeyName = "valcast_possessed";
        internal static readonly int PossessedKey = PossessedKeyName.GetStableHashCode();

        internal static ConfigEntry<bool> AdminEnabled;
        internal static ConfigEntry<KeyboardShortcut> PossessKey;
        internal static ConfigEntry<KeyboardShortcut> ReleaseKey;
        internal static ConfigEntry<KeyCode> CycleModifier;
        internal static ConfigEntry<float> AdminMaxRange;
        internal static ConfigEntry<bool> AllowBosses;
        internal static ConfigEntry<bool> AllowRavens;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            AdminEnabled = config.Bind("Possession", "Enable admin possession", false,
                "SERVER. Admins can take control of the creature under the crosshair with the Possess key. The body stays where it is, untouchable. "
                + "Class skills (Druid, Necromancer) possess through their own rules and don't need this on.");
            // 0.2.363: a Core feature beside "Enable admin possession"; never owner-gated (stays visible without Valcast).
            AllowRavens = config.Bind("Possession", "Allow raven possession", true,
                "SERVER. With admin possession on, the Possess key on vanilla's tutorial ravens (Hugin, Munin) takes control of a flying "
                + "raven body at the perch; the vanilla raven is hidden on your client and comes back to its perch on release.");
            PossessKey = config.Bind("Possession", "Possess key", new KeyboardShortcut(KeyCode.G),
                "Admin: tap once to possess the creature under the crosshair; tap again while possessing to release it. While admin "
                + "possession is on and you are an admin, other mods' binds of this key (the guild screen on G) open on a double tap.");
            ReleaseKey = config.Bind("Possession", "Release key", new KeyboardShortcut(KeyCode.Escape),
                "Releases any possession (admin or class).");
            CycleModifier = config.Bind("Possession", "Attack cycle modifier", KeyCode.LeftAlt,
                "Hold this and turn the mouse wheel to pick among all the creature's attacks. LMB = main attack, MMB = special.");
            AdminMaxRange = config.Bind("Possession", "Admin max range m", 30f,
                new ConfigDescription("How far the crosshair reaches for admin possession.", new AcceptableValueRange<float>(2f, 100f)));
            AllowBosses = config.Bind("Possession", "Allow bosses", false,
                "Admin possession may take bosses (class skills never can).");
            configSync?.AddConfigEntry(AdminEnabled);
            configSync?.AddConfigEntry(AdminMaxRange);
            configSync?.AddConfigEntry(AllowBosses);
            configSync?.AddConfigEntry(AllowRavens);
            // 0.2.359: Core's own admin possession uses the Possess / Release keys, the admin range and the boss rule, so those
            // stay with "Enable admin possession"; only Valcast's attack-cycle modifier leaves the config without Valcast.
            FiresCore.Config.OwnerGatedConfig.Gate(FiresCore.Config.OwnerGatedConfig.Owners.VALCAST, false, CycleModifier);
            KeyTaps.ClaimSingleTaps("possession", () => PossessKey.Value, ClaimsKey);
        }

        /// <summary>
        /// 0.2.321 (Fire 10-07): admin possession owns the possess key's single taps while it can act: on (server-synced) and
        /// this player is an admin. The guild screen's G then opens on a double tap; otherwise single G stays the guild key.
        /// </summary>
        internal static bool ClaimsKey() => AdminEnabled != null && AdminEnabled.Value && PossessKey != null && FiresCore.Sync.AdminSyncing.IsLocalAdmin();

        /// <summary>The possess key's single tap completed this frame. Ask every frame while claimed: it is evaluated even
        /// when the keys are closed, so no press is lost and a double tap never leaks a single.</summary>
        internal static bool PossessTapped()
        {
            if (!ClaimsKey()) return false;
            bool tap = KeyTaps.SingleTapped(PossessKey.Value);
            return tap && KeysOpen();
        }

        /// <summary>No hotkeys while typing: chat, console, a text field, or a menu that owns the keyboard.</summary>
        internal static bool KeysOpen()
        {
            if (FiresInputBlock.IsCapturing) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            if (global::Console.IsVisible()) return false;
            if (Menu.IsVisible() || TextInput.IsVisible() || InventoryGui.IsVisible()) return false;
            return true;
        }

        internal static bool Pressed(ConfigEntry<KeyboardShortcut> key) => key != null && KeysOpen() && Chord.IsDown(key.Value);

        /// <summary>
        /// The creature the admin aims at, within <paramref name="range"/> of the player. 0.2.322: Fire's hand test showed "refused:
        /// nothing there" on every tap while aiming at a Troll. The old ray started at the third-person camera (about 8 m behind
        /// the player) and gave up at the first non-creature collider, including the geometry between the camera and the player.
        /// Now, in order:
        /// (1) vanilla Player.GetHoverCreature (the creature the game's HUD shows as hovered);
        /// (2) a camera ray on the character layers only, skipping anything behind the player;
        /// (3) a 0.6 m sphere along the same line.
        /// Then line of sight from the player's eye, not the camera. <paramref name="why"/> says how it was found, or why not.
        /// </summary>
        internal static Character PickUnderCrosshair(float range, out string why)
        {
            why = null;
            Player local = Player.m_localPlayer;
            if (GameCamera.instance == null || local == null) { why = "no camera or player"; return null; }

            Character hover = local.GetHoverCreature();
            if (hover != null && hover != local && !hover.IsDead() && Distance(local, hover) <= range) { why = "vanilla hover"; return hover; }

            Transform cam = GameCamera.instance.transform;
            Vector3 eye = local.m_eye != null ? local.m_eye.position : local.transform.position + Vector3.up * 1.6f;
            float behind = Mathf.Max(0f, Vector3.Dot(eye - cam.position, cam.forward));   // the stretch of the ray behind the player
            float reach = behind + range;
            int chars = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv");
            int world = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

            string via = "camera ray";
            Character c = FirstCreature(Physics.RaycastAll(cam.position, cam.forward, reach, chars, QueryTriggerInteraction.Ignore), local, behind);
            if (c == null)
            {
                via = "0.6 m sphere";
                c = FirstCreature(Physics.SphereCastAll(cam.position, 0.6f, cam.forward, reach, chars, QueryTriggerInteraction.Ignore), local, behind);
            }
            if (c == null) { why = DescribeMiss(local, cam, range, world, behind); return null; }

            float d = Distance(local, c);
            if (d > range) { why = $"{c.m_name} is {d:0.0} m away (max {range:0} m)"; return null; }
            if (Physics.Linecast(eye, c.GetCenterPoint(), out RaycastHit block, world, QueryTriggerInteraction.Ignore)
                && block.collider != null && block.collider.GetComponentInParent<Character>() == null)
            {
                why = $"{c.m_name} ({d:0.0} m) is behind {block.collider.name} (layer {LayerMask.LayerToName(block.collider.gameObject.layer)}, {block.distance:0.0} m from the eye)";
                return null;
            }
            why = $"{via}, {d:0.0} m";
            return c;
        }

        private static float Distance(Player local, Character c) => Vector3.Distance(local.transform.position, c.transform.position);

        // The nearest living non-player creature along the hits, ignoring the player and anything behind the player.
        private static Character FirstCreature(RaycastHit[] hits, Player local, float behind)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                Character c = hit.collider != null ? hit.collider.GetComponentInParent<Character>() : null;
                if (c == null || c == local || c.IsPlayer() || c.IsDead()) continue;
                if (hit.distance > 0f && hit.distance < behind - 0.5f) continue;
                return c;
            }
            return null;
        }

        // A miss, in words: what the view ray hits first beyond the player, and the nearest creature in range with its angle off the crosshair.
        private static string DescribeMiss(Player local, Transform cam, float range, int world, float behind)
        {
            string first = "nothing";
            var hits = Physics.RaycastAll(cam.position, cam.forward, behind + range, world, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == null || hit.distance < behind) continue;
                first = $"{hit.collider.name} (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) {hit.distance - behind:0.0} m past the player";
                break;
            }
            Character nearest = null;
            float best = float.MaxValue;
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch == null || ch == local || ch.IsPlayer() || ch.IsDead()) continue;
                float d = Distance(local, ch);
                if (d <= range && d < best) { best = d; nearest = ch; }
            }
            string near = nearest != null
                ? $"nearest creature {nearest.m_name} {best:0.0} m, {Vector3.Angle(cam.forward, nearest.GetCenterPoint() - cam.position):0} deg off the crosshair"
                : $"no creature within {range:0} m";
            return $"no creature under the crosshair (the view ray hits {first}; {near})";
        }

        /// <summary>The rules every possession shares (Tools\CLASSES_DRUID_NECROMANCER.md section 2), then the options' filter.</summary>
        internal static bool CanPossessLocal(Player p, Character target, PossessOptions o, out string reason)
        {
            reason = null;
            if (p == null) { reason = "no player"; return false; }
            if (target == null) { reason = "nothing there"; return false; }
            if (target.IsPlayer()) { reason = "players can't be possessed"; return false; }
            if (target.IsDead()) { reason = "it's dead"; return false; }
            bool admin = o == null || o.SourceTag == AdminTag;
            if (target.IsBoss() && !(admin && AllowBosses != null && AllowBosses.Value)) { reason = "bosses can't be possessed"; return false; }
            if (target.GetBaseAI() is FiresCore.Npc.AI.CompanionAI) { reason = "companions can't be possessed"; return false; }
            ZNetView nv = target.GetComponent<ZNetView>();
            ZDO zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (zdo == null) { reason = "not networked"; return false; }
            long holder = zdo.GetLong(PossessedKey, 0L);
            if (holder != 0L && holder != p.GetPlayerID()) { reason = "possessed by another player"; return false; }
            long summonOwner = zdo.GetLong(FiresCore.Summon.SummonKeys.Owner, 0L);
            if (target.IsTamed() && summonOwner != p.GetPlayerID() && !admin) { reason = "someone's tame"; return false; }
            if (PossessionClient.Busy) { reason = "already possessing"; return false; }
            if (o != null && o.Filter != null)
            {
                string why = null;
                try { why = o.Filter(target); } catch (System.Exception e) { why = "filter error: " + e.Message; }
                if (why != null) { reason = why; return false; }
            }
            return true;
        }
    }
}
