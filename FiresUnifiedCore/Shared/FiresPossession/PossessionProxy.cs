using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresPossession
{
    /// <summary>
    /// The Hidden body ("Proxy"; Druid Wildshape): the possessor's own Player does the moving and fighting with the
    /// whole player stack (climb / crawl / swim, jump, dodge, block, attack chains, the player camera), hidden; the creature is laid
    /// over it: its transform follows the player, its own physics and AI stand down, and when both avatars are humanoid its bones take
    /// the player's pose (HumanPoseHandler) every frame.
    ///
    /// Rules: the creature takes the hits (its health; it dies → the end), the player is untargetable; the weapon is the
    /// creature's (its damage, range, projectile), swung with the player's animation for the same skill when the player animator has
    /// no trigger of the creature's name. The weapon is an in-memory ItemData: never in the inventory, never saved; the player's own
    /// equipment is put back exactly at the end, and a save while possessing writes it as equipped.
    ///
    /// Network: the possessor owns the creature's ZDO (the engine), so its position syncs; other clients get its own animator
    /// parameters (speed, ground, the attack trigger), not the copied pose. The player carries "valcast_proxy" on its ZDO and every
    /// client with an engine copy hides that player's body; a vanilla client still sees it.
    /// </summary>
    internal static class PossessionProxy
    {
        internal const string ProxyKey = "valcast_proxy";
        private const string RemoteScanKey = "FiresPossession.RemoteScan";   // one engine copy per process hides other proxied players

        private static Player _player;
        private static Character _creature;
        private static IPossessionHost _host;
        private static Animator _playerAnim, _creatureAnim;
        // The pose copy lives in HumanPoseFollower (shared with VCS model swaps).
        private static FiresCore.Animation.HumanPoseFollower _follower;
        private static ZSyncAnimation _creatureZanim;
        private static Rigidbody _creatureBody;
        private static bool _bodyWasKinematic;
        private static AnimatorCullingMode _playerCulling;
        private static Vector3 _anchorPos;
        private static Quaternion _anchorRot;
        private static readonly List<Collider> _ignored = new List<Collider>();
        private static readonly object s_scanToken = new object();
        private static bool s_scanOwner;   // drop 2: this copy hides other players' proxied bodies (set once at Install)

        // The weapon: the player's own equipped items (put back at the end) and the creature's in memory.
        private static readonly List<ItemDrop.ItemData> _ownEquipped = new List<ItemDrop.ItemData>();
        private static ItemDrop.ItemData _temp;
        private static string _creatureAttackTrigger;
        private static Humanoid _source;
        private static ItemDrop.ItemData _tempFrom;

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> s_right = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> s_left = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        private static readonly AccessTools.FieldRef<Character, Rigidbody> s_body = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<Character, Animator> s_anim = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> s_zanim = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
        private static readonly AccessTools.FieldRef<Character, GameObject> s_visual = AccessTools.FieldRefAccess<Character, GameObject>("m_visual");
        private static readonly System.Reflection.MethodInfo s_memberwiseClone = AccessTools.Method(typeof(object), "MemberwiseClone");

        internal static bool Active => _creature != null;
        internal static Character Creature => _creature;

        /// <summary>Only while this engine copy holds the possession and the proxy is on.</summary>
        private static bool On => _creature != null && PossessionEngine.Mine;

        private static void Log(string line) => _host?.Log(line);

        private static ZDO Zdo(Character c)
        {
            var view = c != null ? c.GetComponent<ZNetView>() : null;
            return view != null && view.IsValid() ? view.GetZDO() : null;
        }

        // ── size (otherwise a possessed troll skates and the camera sits at its ankles) ────────────────────
        // s = the creature's height / the player's. The player body moves at the CREATURE's own speeds, and the player's animator
        // plays at 1/s while walking: a player stride drawn on a body s times bigger covers s times the ground per cycle, so 1/s keeps
        // the copied feet planted. The player camera's eye height, shoulder offset and distance scale by s.
        internal static float Size { get; private set; } = 1f;
        private static float _walk, _run, _crouch, _swim, _jump, _speed;
        private static float _minDist, _maxDist, _dist;
        private static readonly AccessTools.FieldRef<GameCamera, float> s_camDistance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");

        internal static float HeightOf(Character c)
        {
            try { float h = c.GetHeight() * c.transform.lossyScale.y; return h > 0.05f ? h : 1.8f; } catch { return 1.8f; }
        }

        private static void ApplySize(Player player, Character creature)
        {
            Size = Mathf.Clamp(HeightOf(creature) / Mathf.Max(0.5f, HeightOf(player)), 0.3f, 8f);
            _walk = player.m_walkSpeed; _run = player.m_runSpeed; _crouch = player.m_crouchSpeed; _swim = player.m_swimSpeed; _jump = player.m_jumpForce; _speed = player.m_speed;
            player.m_walkSpeed = creature.m_walkSpeed;
            player.m_runSpeed = creature.m_runSpeed;
            player.m_speed = creature.m_speed;
            player.m_crouchSpeed = _crouch * Size;
            player.m_swimSpeed = creature.m_swimSpeed > 0.01f ? creature.m_swimSpeed : _swim * Size;
            player.m_jumpForce = _jump * Mathf.Sqrt(Size);
            var cam = GameCamera.instance;
            if (cam != null)
            {
                _minDist = cam.m_minDistance; _maxDist = cam.m_maxDistance; _dist = s_camDistance(cam);
                cam.m_minDistance = _minDist * Size;
                cam.m_maxDistance = _maxDist * Size;
                s_camDistance(cam) = Mathf.Clamp(_dist * Size, cam.m_minDistance, cam.m_maxDistance);
            }
            Log($"size {Size:0.00} ({creature.gameObject.name} {HeightOf(creature):0.00} m): walk {player.m_walkSpeed:0.0} run {player.m_runSpeed:0.0} speed {player.m_speed:0.0} m/s, stride rate {1f / Size:0.00}, camera distance x{Size:0.00}");
        }

        private static void RestoreSize(Player player)
        {
            if (_run <= 0f) return;
            player.m_walkSpeed = _walk; player.m_runSpeed = _run; player.m_crouchSpeed = _crouch; player.m_swimSpeed = _swim; player.m_jumpForce = _jump; player.m_speed = _speed;
            var cam = GameCamera.instance;
            if (cam != null && _maxDist > 0f)
            {
                cam.m_minDistance = _minDist; cam.m_maxDistance = _maxDist;
                s_camDistance(cam) = Mathf.Clamp(Size > 0f ? s_camDistance(cam) / Size : _dist, _minDist, _maxDist);
            }
            Size = 1f; _run = 0f; _maxDist = 0f;
        }

        // A humanoid creature with a humanoid avatar (the pose copy) that doesn't fly (flying is the Frozen body's).
        internal static bool CanProxy(Character c, out string why)
        {
            why = null;
            if (!(c is Humanoid)) { why = "not a humanoid"; return false; }
            if (PossessionEngine.IsFlyer(c)) { why = "a flyer (flying is the Frozen body's)"; return false; }
            var anim = c.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) { why = "its rig has no humanoid avatar"; return false; }
            return true;
        }

        internal static void Begin(Player player, Character creature, IPossessionHost host)
        {
            _player = player;
            _creature = creature;
            _host = host;
            _anchorPos = player.transform.position;
            _anchorRot = player.transform.rotation;

            // The player steps into the creature.
            var pBody = s_body(player);
            player.transform.SetPositionAndRotation(creature.transform.position, Upright(creature.transform.position, creature.transform.rotation));
            if (pBody != null) { pBody.position = player.transform.position; pBody.linearVelocity = Vector3.zero; }
            player.SetLookDir(creature.transform.forward);

            // The creature stands down: kinematic, no collisions with the player.
            _creatureBody = s_body(creature);
            if (_creatureBody != null) { _bodyWasKinematic = _creatureBody.isKinematic; _creatureBody.isKinematic = true; }
            _ignored.Clear();
            var playerCols = player.GetComponentsInChildren<Collider>();
            foreach (var cc in creature.GetComponentsInChildren<Collider>())
                foreach (var pc in playerCols) Physics.IgnoreCollision(cc, pc, true);
            _ignored.AddRange(creature.GetComponentsInChildren<Collider>());

            // The pose copy.
            _playerAnim = s_anim(player);
            _creatureAnim = s_anim(creature) ?? creature.GetComponentInChildren<Animator>();
            _creatureZanim = s_zanim(creature);
            if (_playerAnim != null) { _playerCulling = _playerAnim.cullingMode; _playerAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            _follower?.Dispose();
            _follower = FiresCore.Animation.HumanPoseFollower.CanFollow(_playerAnim, _creatureAnim, out _)
                ? new FiresCore.Animation.HumanPoseFollower(_playerAnim, _creatureAnim) : null;

            EquipCreatureWeapon(player, creature as Humanoid);
            ApplySize(player, creature);
            Zdo(player)?.Set(ProxyKey, true);
            HideBody(player, true);
            Log($"proxy on {creature.gameObject.name}: pose copy {(_follower != null ? "on" : "off")}, weapon {(_temp != null ? _temp.m_shared.m_name + " (" + _temp.m_shared.m_attack.m_attackAnimation + ")" : "none")}");
        }

        /// <summary>Ends the proxy. atCreature: the player comes out where the creature stands (Wildshape), else back where the body stood.</summary>
        internal static void End(string reason, bool atCreature)
        {
            if (_creature == null && _player == null) return;
            var player = _player;
            var creature = _creature;
            _creature = null;
            _player = null;
            try
            {
                if (_creatureBody != null) _creatureBody.isKinematic = _bodyWasKinematic;
                if (player != null)
                {
                    var playerCols = player.GetComponentsInChildren<Collider>();
                    foreach (var cc in _ignored) if (cc != null) foreach (var pc in playerCols) Physics.IgnoreCollision(cc, pc, false);
                    RestoreOwnEquipment(player);
                    HideBody(player, false);
                    RestoreSize(player);
                    if (_playerAnim != null) _playerAnim.cullingMode = _playerCulling;
                    Zdo(player)?.Set(ProxyKey, false);
                    Vector3 pos = _anchorPos; Quaternion rot = _anchorRot;
                    if (atCreature && creature != null) { pos = creature.transform.position; rot = Upright(pos, creature.transform.rotation); }
                    var pBody = s_body(player);
                    player.transform.SetPositionAndRotation(pos, rot);
                    if (pBody != null) { pBody.position = pos; pBody.linearVelocity = Vector3.zero; }
                }
            }
            catch (System.Exception ex) { Log("WARN: proxy restore failed: " + ex.Message); }
            _ignored.Clear();
            _follower?.Dispose();
            _follower = null;
            _creatureBody = null; _playerAnim = _creatureAnim = null; _creatureZanim = null;
            Log($"proxy off ({reason}){(creature != null ? " " + creature.gameObject.name : "")}");
        }

        // ── the weapon ─────────────────────────────────────────────────────────────────────────────────────────────────────
        private static void EquipCreatureWeapon(Player player, Humanoid creature)
        {
            _ownEquipped.Clear();
            foreach (var it in player.GetInventory().GetEquippedItems()) _ownEquipped.Add(it);
            foreach (var it in _ownEquipped.Where(i => i.IsWeapon() || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield || i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch).ToList())
                player.UnequipItem(it, false);
            _temp = null; _tempFrom = null;
            _creatureAttackTrigger = null;
            _source = creature;
            UseAttack(player, PossessionAttacks.Pick(creature, PossessionAttacks.Main));
        }

        // The in-memory weapon becomes a copy of 'source' (never in the inventory, never saved).
        internal static void UseAttack(Player player, ItemDrop.ItemData source)
        {
            if (source == null || source.m_shared?.m_attack == null || source == _tempFrom) return;
            _tempFrom = source;
            _creatureAttackTrigger = source.m_shared.m_attack.m_attackAnimation;

            var temp = source.Clone();
            var shared = (ItemDrop.ItemData.SharedData)s_memberwiseClone.Invoke(source.m_shared, null);   // never touch the prefab's shared data
            shared.m_attack = source.m_shared.m_attack.Clone();
            shared.m_secondaryAttack = source.m_shared.m_secondaryAttack?.Clone();
            shared.m_useDurability = false;
            shared.m_ammoType = "";
            temp.m_shared = shared;
            temp.m_equipped = true;
            if (!HasTrigger(_playerAnim, shared.m_attack.m_attackAnimation)) UsePlayerSwing(shared.m_attack, shared, false);
            if (shared.m_secondaryAttack != null && !string.IsNullOrEmpty(shared.m_secondaryAttack.m_attackAnimation) && !HasTrigger(_playerAnim, shared.m_secondaryAttack.m_attackAnimation))
                UsePlayerSwing(shared.m_secondaryAttack, shared, true);
            _temp = temp;
            Assert(player);
        }

        internal static Humanoid Source => _source;

        // The swing of a player item with the same skill and hand use: animation, chain and random variants; the creature keeps its
        // damage, range, angle and projectile.
        private static void UsePlayerSwing(Attack attack, ItemDrop.ItemData.SharedData shared, bool secondary)
        {
            Attack best = null;
            if (ObjectDB.instance != null)
                foreach (var go in ObjectDB.instance.m_items)
                {
                    var s = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
                    var a = s == null ? null : secondary ? s.m_secondaryAttack : s.m_attack;
                    if (a == null || string.IsNullOrEmpty(a.m_attackAnimation) || s.m_skillType != shared.m_skillType) continue;
                    if (!HasTrigger(_playerAnim, a.m_attackAnimation)) continue;
                    if (best == null || s.m_itemType == shared.m_itemType) best = a;
                    if (s.m_itemType == shared.m_itemType) break;
                }
            if (best == null && Player.m_localPlayer != null)
            {
                var unarmed = Player.m_localPlayer.m_unarmedWeapon?.m_itemData?.m_shared;
                best = unarmed != null ? (secondary ? unarmed.m_secondaryAttack : unarmed.m_attack) : null;
            }
            if (best == null) return;
            attack.m_attackAnimation = best.m_attackAnimation;
            attack.m_attackChainLevels = best.m_attackChainLevels;
            attack.m_attackRandomAnimations = best.m_attackRandomAnimations;
        }

        private static readonly Dictionary<RuntimeAnimatorController, HashSet<string>> s_triggers = new Dictionary<RuntimeAnimatorController, HashSet<string>>();

        private static bool HasTrigger(Animator anim, string name)
        {
            if (anim == null || anim.runtimeAnimatorController == null || string.IsNullOrEmpty(name)) return false;
            if (!s_triggers.TryGetValue(anim.runtimeAnimatorController, out var set))
                s_triggers[anim.runtimeAnimatorController] = set = new HashSet<string>(anim.parameters.Select(p => p.name));
            return set.Contains(name) || set.Contains(name + "0");
        }

        // The temporary weapon stays in the right hand (nothing in vanilla re-checks it against the inventory, but a mod might).
        private static void Assert(Player player)
        {
            if (_temp == null) return;
            if (s_right(player) != _temp) s_right(player) = _temp;
            if (s_left(player) != null && s_left(player) != _temp) s_left(player) = null;
        }

        private static void RestoreOwnEquipment(Player player)
        {
            if (_temp != null && s_right(player) == _temp) s_right(player) = null;
            if (s_left(player) == _temp) s_left(player) = null;
            _temp = null; _tempFrom = null; _source = null;
            var inv = player.GetInventory();
            foreach (var it in _ownEquipped)
                if (it != null && inv.ContainsItem(it) && !player.IsItemEquiped(it)) player.EquipItem(it, false);
            _ownEquipped.Clear();
        }

        // ── the body: hidden here, and on every client with an engine copy from the ZDO flag ──────────────────────────────────
        private static void HideBody(Player player, bool hide)
        {
            var visual = player != null ? s_visual(player) : null;
            if (visual == null) return;
            foreach (var r in visual.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = hide;
        }

        private static float _nextRemoteScan;
        private static readonly HashSet<Player> s_remoteHidden = new HashSet<Player>();

        // The creature on the player, its bones in the player's pose, its animator told the player's speed (for other clients).
        // The body upright with r's heading. Off every ring (FlightModel.UpAt null or world up) = Euler(0, r.y, 0), the old rotation;
        // on the halo ring upright about the local up, facing r's forward along the wall.
        private static bool s_ringLogged;
        private static Quaternion Upright(Vector3 pos, Quaternion r)
        {
            Vector3 up = FlightModel.UpAt != null ? FlightModel.UpAt(pos) : Vector3.up;
            if (up == Vector3.up) return Quaternion.Euler(0f, r.eulerAngles.y, 0f);
            Vector3 fwd = Vector3.ProjectOnPlane(r * Vector3.forward, up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(r * Vector3.up, up);
            if (!s_ringLogged) { s_ringLogged = true; Debug.Log($"[Possession] halo ring: possessed bodies stand upright about the local up {up:F2}"); }
            return Quaternion.LookRotation(fwd, up);
        }

        private static void Follow()
        {
            var pt = _player.transform;
            var visualRot = s_visual(_player) != null ? s_visual(_player).transform.rotation : pt.rotation;
            _creature.transform.SetPositionAndRotation(pt.position, Upright(pt.position, visualRot));
            if (_creatureBody != null) _creatureBody.position = pt.position;
            _follower?.Step();
            if (_creatureZanim != null)
            {
                Vector3 v = _player.GetVelocity();
                _creatureZanim.SetFloat("forward_speed", Vector3.Dot(v, _creature.transform.forward));
                _creatureZanim.SetFloat("sideway_speed", Vector3.Dot(v, _creature.transform.right));
                _creatureZanim.SetBool("onGround", _player.IsOnGround());
                _creatureZanim.SetBool("inWater", _player.IsSwimming());
            }
        }

        private static bool IsProxied(Character c)
        {
            if (!(c is Player p)) return false;
            if (p == _player && _creature != null) return true;
            var zdo = Zdo(p);
            return zdo != null && zdo.GetBool(ProxyKey);
        }

        // ── patches (installed by PossessionEngine.Install with the host's Harmony id; each acts only while this copy holds the token) ──
        internal static void Install(Harmony harmony)
        {
            // The first engine copy in the process hides other players' proxied bodies (one scan, whichever copies are loaded).
            if (System.AppDomain.CurrentDomain.GetData(RemoteScanKey) == null) System.AppDomain.CurrentDomain.SetData(RemoteScanKey, s_scanToken);
            s_scanOwner = ReferenceEquals(System.AppDomain.CurrentDomain.GetData(RemoteScanKey), s_scanToken);   // drop 2: read once (it never changes hands)
            PossessionEngine.Patch(harmony, typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.CustomFixedUpdate), typeof(PossessionProxy), nameof(AnimEvent_Postfix), postfix: true, last: true);
            PossessionEngine.Patch(harmony, typeof(GameCamera), "GetCameraBaseOffset", typeof(PossessionProxy), nameof(CameraOffset_Postfix), postfix: true);
            PossessionEngine.Patch(harmony, typeof(GameCamera), "GetCameraOffset", typeof(PossessionProxy), nameof(CameraOffset_Postfix), postfix: true);
            PossessionEngine.Patch(harmony, typeof(GameCamera), "LateUpdate", typeof(PossessionProxy), nameof(LateUpdate_Postfix), postfix: true);
            PossessionEngine.Patch(harmony, typeof(Character), nameof(Character.CustomFixedUpdate), typeof(PossessionProxy), nameof(Character_CustomFixedUpdate_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate), typeof(PossessionProxy), nameof(Humanoid_CustomFixedUpdate_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Humanoid), nameof(Humanoid.StartAttack), typeof(PossessionProxy), nameof(StartAttack_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Humanoid), nameof(Humanoid.StartAttack), typeof(PossessionProxy), nameof(StartAttack_Postfix), postfix: true);
            PossessionEngine.Patch(harmony, typeof(Character), "RPC_Damage", typeof(PossessionProxy), nameof(RPC_Damage_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Character), nameof(Character.Damage), typeof(PossessionProxy), nameof(Damage_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Player), nameof(Player.Save), typeof(PossessionProxy), nameof(Save_Prefix), postfix: false);
            PossessionEngine.Patch(harmony, typeof(Player), nameof(Player.Save), typeof(PossessionProxy), nameof(Save_Postfix), postfix: true);
            harmony.Patch(AccessTools.Method(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) }),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PossessionProxy), nameof(IsEnemy_Postfix))));
        }

        // The stride rate: right after vanilla's per-frame reset (it only resets while walking, so attacks keep their timing).
        private static void AnimEvent_Postfix(CharacterAnimEvent __instance)
        {
            if (!On || _player == null || Mathf.Approximately(Size, 1f) || _playerAnim == null) return;
            if (__instance.gameObject != _playerAnim.gameObject) return;
            if (_player.InAttack() || _player.InMinorAction() || _player.InEmote() || !_player.CanMove()) return;
            if (_playerAnim.speed < 0.01f) return;   // a freeze frame
            _playerAnim.speed = _playerAnim.speed / Size;
        }

        private static void CameraOffset_Postfix(Player player, ref Vector3 __result)
        {
            if (On && player == _player && !Mathf.Approximately(Size, 1f)) __result *= Size;
        }

        private static void LateUpdate_Postfix()
        {
            try
            {
                if (On && _player != null && !_creature.IsDead() && !_player.IsDead())
                {
                    Follow();
                    HideBody(_player, true);   // VisEquipment rebuilds attachments (equip, hair) with their renderers on
                }
                if (s_scanOwner && Time.time >= _nextRemoteScan)
                {
                    _nextRemoteScan = Time.time + 0.5f;
                    foreach (var p in Player.GetAllPlayers())
                    {
                        if (p == null || p == Player.m_localPlayer) continue;
                        var zdo = Zdo(p);
                        bool hide = zdo != null && zdo.GetBool(ProxyKey);
                        if (hide == s_remoteHidden.Contains(p)) continue;
                        if (hide) s_remoteHidden.Add(p); else s_remoteHidden.Remove(p);
                        HideBody(p, hide);
                    }
                }
            }
            catch (System.Exception ex) { Log("WARN: proxy frame: " + ex.Message); }
        }

        // The creature's own update stands down (no AI walking, gravity or ground snap against the player's transform).
        private static bool Character_CustomFixedUpdate_Prefix(Character __instance) => !On || __instance != _creature;

        private static bool Humanoid_CustomFixedUpdate_Prefix(Humanoid __instance)
        {
            if (!On) return true;
            if (__instance == _player) Assert(_player);
            return __instance != _creature;
        }

        // LMB swings the creature's main attack, MMB (vanilla's secondary attack) the special (the cycle picks it) as a primary swing -
        // creature weapons have no secondary.
        private static void StartAttack_Prefix(Humanoid __instance, ref bool secondaryAttack)
        {
            if (!On || __instance != _player || _source == null || __instance.InAttack()) { ForcedNext = null; return; }
            UseAttack(_player, ForcedNext ?? PossessionAttacks.Pick(_source, secondaryAttack ? PossessionAttacks.Special : PossessionAttacks.Main));
            ForcedNext = null;
            secondaryAttack = false;
        }

        /// <summary>PossessionEngine.Fire(which): the next swing uses this attack instead of LMB / MMB's pick.</summary>
        internal static ItemDrop.ItemData ForcedNext;

        // Other clients see the creature's own attack: its trigger goes out when the player's attack starts.
        private static void StartAttack_Postfix(Humanoid __instance, bool __result)
        {
            if (!__result || !On || __instance != _player || _creatureZanim == null || string.IsNullOrEmpty(_creatureAttackTrigger)) return;
            _creatureZanim.SetTrigger(_creatureAttackTrigger);
        }

        // The creature takes the hits. A hit on the hidden player (any attacker, any client) goes to the creature.
        private static bool RPC_Damage_Prefix(Character __instance, HitData hit)
        {
            if (!On || __instance != _player || hit == null) return true;
            _creature.Damage(hit);
            return false;
        }

        // The player's swing starts inside the creature it wears: those hits never land on it.
        private static bool Damage_Prefix(Character __instance, HitData hit) => !On || __instance != _creature || hit == null || hit.GetAttacker() != _player;

        // ... and nothing picks a hidden player as a target (this one, or another client's from the ZDO flag).
        private static void IsEnemy_Postfix(Character a, Character b, ref bool __result)
        {
            if (!__result) return;
            // Performance: nothing hidden here (no proxy of ours, no hidden remote player seen by the scan) = no ZDO
            // reads per AI check.
            if (!On && (!s_scanOwner || s_remoteHidden.Count == 0)) return;
            if (IsProxied(a) || IsProxied(b)) __result = false;
        }

        // A save while possessing writes the player's own gear as equipped (the in-memory weapon is never in the inventory).
        private static void Save_Prefix(Player __instance, out List<ItemDrop.ItemData> __state)
        {
            __state = null;
            if (!On || __instance != _player || _ownEquipped.Count == 0) return;
            __state = _ownEquipped.Where(i => i != null && !i.m_equipped).ToList();
            foreach (var it in __state) it.m_equipped = true;
        }

        private static void Save_Postfix(List<ItemDrop.ItemData> __state)
        {
            if (__state != null) foreach (var it in __state) it.m_equipped = false;
        }
    }
}
