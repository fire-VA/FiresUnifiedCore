using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Ore detection, mining logic, and attack mechanics.
    /// </summary>
    public partial class ResourceGatheringBehavior
    {
        /// <summary>
        /// Nearest deposit mined with a pickaxe (MineRock, MineRock5, or a fresh Destructible deposit such as rock4_copper
        /// or MineRock_Tin) that yields one of <paramref name="wantedDrops"/> and needs no higher tier than
        /// <paramref name="maxToolTier"/>, so an unmineable vein (1.0 goldvein, tier 5) never blocks the ones that can be mined.
        /// </summary>
        private GameObject FindNearbyOreDeposit(Vector3 position, float radius, ICollection<string> wantedDrops, int maxToolTier)
        {
            // 0.2.274: no pick at all no longer ends the search; a node is offered when one can be crafted from base stock.
            if (wantedDrops.Count == 0) return null;

            var colliders = Physics.OverlapSphere(position, radius);
            float closestDist = float.MaxValue;
            GameObject closest = null;
            // 0.2.274: the nearest node beyond the best pick, offered when a pick for it can be crafted from base stock.
            float beyondDist = float.MaxValue;
            GameObject beyond = null;
            int beyondTier = 0;
            var processed = new HashSet<GameObject>();
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var depositComponent = (Component)collider.GetComponentInParent<MineRock5>()
                    ?? (Component)collider.GetComponentInParent<MineRock>()
                    ?? collider.GetComponentInParent<Destructible>();
                if (depositComponent == null) continue;
                
                var target = depositComponent.gameObject;
                if (!processed.Add(target)) continue;
                if (IsUnreached(target)) continue;   // 0.2.268: a run timed out short of it lately

                if (!ResourceDataHelper.YieldsAnyOf(target, wantedDrops)) continue;
                var deposit = ResourceDataHelper.GetResourceData(target);
                if (deposit == null || deposit.RequiredTool != ResourceDataHelper.ToolType.Pickaxe) continue;
                if (deposit.MinToolTier > maxToolTier)
                {
                    float beyondHere = Vector3.Distance(position, target.transform.position);
                    if (CanCraftToolFromStock(ResourceDataHelper.ToolType.Pickaxe, deposit.MinToolTier))
                    {
                        if (beyondHere < beyondDist) { beyondDist = beyondHere; beyond = target; beyondTier = deposit.MinToolTier; }
                        continue;
                    }
                    // 0.2.268 ([lead]: say when a node is beyond the best pick it can get; the tier is the runtime prefab's).
                    int id = target.GetInstanceID();
                    if (!_tierSkipSaid.TryGetValue(id, out float said) || Time.time - said >= UnreachedSkipSeconds)
                    {
                        _tierSkipSaid[id] = Time.time;
                        Vector3 p = target.transform.position;
                        AI.ChoreBrain.ChoreDone(Companion?.companionName, "mining",
                            $"skipping {Utils.GetPrefabName(target)} at ({p.x:0}, {p.z:0}) (needs tool tier {deposit.MinToolTier}, best pick tier {maxToolTier}, carried or in a chest; "
                            + $"can't craft one from base stock: {StockCraftWhy(ResourceDataHelper.ToolType.Pickaxe, deposit.MinToolTier)})");
                    }
                    continue;
                }
                
                float dist = Vector3.Distance(position, target.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = target;
                }
            }

            // A node it can mine now first; else the nearest one a pick crafted from base stock can mine (Start crafts it first).
            if (closest == null && beyond != null && Companion != null)
            {
                int beyondId = beyond.GetInstanceID();
                if (!_goingForSaid.TryGetValue(beyondId, out float goingSaid) || Time.time - goingSaid >= 60f)
                {
                    _goingForSaid[beyondId] = Time.time;
                    AI.ChoreBrain.ChoreDone(Companion.companionName, "mining",
                        $"going for {Utils.GetPrefabName(beyond)} (needs tool tier {beyondTier}, best pick tier {maxToolTier}): a pick for it can be crafted from base stock");
                }
            }
            return closest ?? beyond;
        }
        
        private bool UpdateAttacking()
        {
            // CRITICAL: Check if current target is a stump BEFORE checking if it's destroyed
            // We need to save the stump info while the GameObject still exists
            if (_targetResource != null && _targetResource.GameObject != null && !_wasTargetingStump)
            {
                if (ResourceDataHelper.IsTreeStump(_targetResource.GameObject))
                {
                    _wasTargetingStump = true;
                    _targetStumpName = Utils.GetPrefabName(_targetResource.GameObject);
                    _targetStumpPosition = _targetResource.InteractionPosition;
                    
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} targeting stump: {_targetStumpName} at {_targetStumpPosition}");
                }
            }
            
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                SayChoreHits("broke");
                // Resource was destroyed - check if it was a stump
                if (_wasTargetingStump)
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} destroyed stump: {_targetStumpName}");
                    
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                    _targetStumpName = null;
                    
                    // Stump destroyed - check for more stumps or logs nearby
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                if (_wasTargetingTree)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} tree destroyed, waiting {LogCheckWait}s before checking for logs");
                    
                    _resourcesGathered++;
                    _aoeDamageAttempts = 0;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            if (_targetResource.Destructible == null)
            {
                SayChoreHits("broke");
                // Destructible component is gone but GameObject still exists (rare case)
                if (_wasTargetingStump)
                {
                    if (VerboseLogging || CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion?.companionName} stump destructible gone: {_targetStumpName}");
                    
                    OnStumpDestroyed(_targetStumpPosition, _targetStumpName);
                    _wasTargetingStump = false;
                    _targetStumpName = null;
                    
                    _resourcesGathered++;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} destructible gone for {_targetResource.Name}");
                
                if (_wasTargetingTree)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} tree destructible gone, waiting {LogCheckWait}s before checking for logs");
                    
                    _resourcesGathered++;
                    _aoeDamageAttempts = 0;
                    SetPhase(GatherPhase.WaitingForLogs);
                    return false;
                }
                
                _resourcesGathered++;
                SetPhase(GatherPhase.WaitingForDrops);
                return false;
            }
            
            // Check if this is an unreachable log (stuck in air on another tree)
            bool useAOEDamage = _targetResource.TreeLog != null && IsLogUnreachable(_targetResource);
            
            if (_consecutiveNoColliderHits >= MaxNoColliderBeforeReposition && !useAOEDamage)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} repositioning after {_consecutiveNoColliderHits} failed hits");
                
                _consecutiveNoColliderHits = 0;
                SetPhase(GatherPhase.Repositioning);
                return false;
            }
            
            // CRITICAL: Don't move or rotate while attack animation is playing!
            // Check if the character is currently in an attack animation
            bool isAttacking = _character != null && _character.InAttack();
            
            if (!isAttacking)
            {
                if (useAOEDamage)
                {
                    // For AOE damage, get as close as possible but don't need to reach the exact spot
                    float dist = Vector3.Distance(Transform.position, _targetResource.InteractionPosition);
                    if (dist > AoeDamageRadius * 2f)
                    {
                        // Move closer but don't try to reach the exact position
                        Vector3 dirToLog = (_targetResource.InteractionPosition - Transform.position).normalized;
                        dirToLog.y = 0; // Keep horizontal
                        Vector3 approachPoint = Transform.position + dirToLog * 2f;
                        MoveToPosition(approachPoint);
                    }
                    else
                    {
                        StopMovement();
                    }
                }
                else
                {
                    // Normal movement - try to reach the target (0.2.271: a rock's nearest face, from just off it)
                    float dist = GatherReach();
                    if (RockUnwedge(dist)) return false;
                    if (dist > AttackRange)
                    {
                        MoveToPosition(IsRockTarget ? StandPointFor(GatherAim()) : _targetResource.InteractionPosition);
                    }
                    else
                    {
                        StopMovement();
                    }
                }

                // Only face target when NOT attacking to prevent turning mid-swing
                FaceTarget(GatherAim());
            }
            else
            {
                // During attack, ensure movement is stopped (no sliding)
                StopMovement();
            }
            
            if (Time.time - _lastAttackTime >= AttackInterval)
            {
                if (useAOEDamage)
                {
                    // Use AOE damage for unreachable logs
                    var weapon = GetEquippedWeaponOrTool();
                    PlayAttackAnimation(weapon);
                    Companion.StartCoroutine(ApplyAOEDamageDelayed(weapon, DamageDelay));
                }
                else
                {
                    // Normal direct attack
                    AttackResource();
                }
                _lastAttackTime = Time.time;
            }
            
            if (Time.time - _phaseStartTime > 60f)
            {
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} attack phase timeout");
                _aoeDamageAttempts = 0;
                SetPhase(GatherPhase.WaitingForDrops);
            }
            
            return false;
        }
        
        /// <summary>
        /// Coroutine to apply AOE damage after animation delay.
        /// </summary>
        private System.Collections.IEnumerator ApplyAOEDamageDelayed(ItemDrop.ItemData weapon, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (_targetResource == null || _targetResource.GameObject == null)
            {
                yield break;
            }
            
            ApplyAOEDamageToLog(weapon);
        }
        
        private void AttackResource()
        {
            if (_targetResource == null || _targetResource.GameObject == null || _humanoid == null) return;
            
            var weapon = GetEquippedWeaponOrTool();
            
            if (_targetResource.RequiresCombat && _targetResource.RequiredTool != ResourceDataHelper.ToolType.None)
            {
                bool hasRightTool = ResourceDataHelper.IsToolAppropriate(weapon, _targetResource.RequiredTool, _targetResource.MinToolTier);
                if (!hasRightTool)
                {
                    if (VerboseLogging)
                    {
                        string weaponName = weapon?.m_shared?.m_name ?? "nothing";
                        Debug.Log($"[ResourceGathering] {Companion.companionName} has {weaponName} but needs {_targetResource.RequiredTool} tier {_targetResource.MinToolTier}");
                    }
                    
                    if (TryEquipToolFromStorage(_targetResource.RequiredTool, _targetResource.MinToolTier))
                    {
                        weapon = GetEquippedWeaponOrTool();
                        if (VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} equipped {weapon?.m_shared?.m_name} from storage");
                    }
                    else
                    {
                        if (VerboseLogging)
                            Debug.Log($"[ResourceGathering] {Companion.companionName} has no appropriate tool for {_targetResource.Name} - stopping");
                        
                        var owner = Companion.GetOwner();
                        if (owner != null && owner == Player.m_localPlayer)
                        {
                            string toolName = _targetResource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe ? "pickaxe" : "axe";
                            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                                $"{Companion.GetDisplayName()} needs a {toolName} to gather {_targetResource.Name}");
                        }
                        
                        SetPhase(GatherPhase.Complete);
                        return;
                    }
                }
            }
            
            PlayAttackAnimation(weapon);
            
            Companion.StartCoroutine(ApplyDamageDelayed(weapon, DamageDelay));
        }
        
        private IEnumerator ApplyDamageDelayed(ItemDrop.ItemData weapon, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (_targetResource == null || _targetResource.GameObject == null || _targetResource.Destructible == null)
            {
                yield break;
            }

            // 0.2.271: a fractured rock is struck at its nearest standing area (see StrikeMineRock5).
            if (_targetResource.MineRock5 != null)
            {
                StrikeMineRock5(weapon);
                yield break;
            }

            Collider hitCollider = null;
            // 0.2.271: any other rock is struck at its nearest face, not its root.
            Vector3 aim = GatherAim();
            Vector3 hitPoint = aim;
            Vector3 hitDir = (hitPoint - Transform.position).normalized;

            Vector3 rayOrigin = Transform.position + Vector3.up * 1.0f;
            Vector3 rayDir = (aim - rayOrigin).normalized;
            float rayDistance = Vector3.Distance(rayOrigin, aim) + 1f;
            
            int hitMask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain", "piece_nonsolid");
            
            RaycastHit[] hits = Physics.RaycastAll(rayOrigin, rayDir, rayDistance, hitMask);
            foreach (var hit in hits)
            {
                if (hit.collider != null)
                {
                    if (hit.collider.transform == _targetResource.GameObject.transform || 
                        hit.collider.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(hit.collider.transform))
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }
                    
                    var parentDestructible = hit.collider.GetComponentInParent<IDestructible>();
                    if (parentDestructible != null && 
                        parentDestructible == _targetResource.Destructible)
                    {
                        hitCollider = hit.collider;
                        hitPoint = hit.point;
                        break;
                    }
                }
            }
            
            if (hitCollider == null)
            {
                Collider[] colliders = Physics.OverlapSphere(aim, 0.5f, hitMask);
                foreach (var collider in colliders)
                {
                    if (collider.transform == _targetResource.GameObject.transform || 
                        collider.transform.IsChildOf(_targetResource.GameObject.transform) ||
                        _targetResource.GameObject.transform.IsChildOf(collider.transform))
                    {
                        hitCollider = collider;
                        hitPoint = collider.bounds.center;
                        break;
                    }
                }
            }
            
            if (hitCollider == null)
            {
                _consecutiveNoColliderHits++;
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] {Companion.companionName} hit {_targetResource.Name} with no collider (count: {_consecutiveNoColliderHits})");
            }
            else
            {
                _consecutiveNoColliderHits = 0;
            }
            
            var hitData = ResourceDataHelper.CreateResourceHitData(
                _targetResource,
                _character,
                weapon,
                hitPoint,
                hitDir,
                hitCollider
            );
            
            _targetResource.Destructible?.Damage(hitData);
            CountChoreHit(weapon);

            if (hitCollider == null && _targetResource.MineRock5 != null)
            {
                ApplyFallbackMineRock5Damage(_targetResource.MineRock5, hitData);
            }
            
            if (hitCollider == null && _targetResource.MineRock != null)
            {
                ApplyFallbackMineRockDamage(_targetResource.MineRock, hitData);
            }
            
            var skills = Companion.GetSkills();
            if (skills != null && weapon != null)
            {
                skills.RaiseSkill(weapon.m_shared.m_skillType, 1f);
            }
            
            if (VerboseLogging)
            {
                string weaponName = weapon?.m_shared?.m_name ?? "unarmed";
                string colliderInfo = hitCollider != null ? hitCollider.name : "NO COLLIDER";
                int toolTier = weapon?.m_shared?.m_toolTier ?? 0;
                Debug.Log($"[ResourceGathering] {Companion.companionName} hit {_targetResource.Name} with {weaponName} (tier {toolTier}, collider: {colliderInfo})");
            }
        }
        
        private void PlayAttackAnimation(ItemDrop.ItemData weapon)
        {
            if (_zanim == null && _animator == null) return;
            
            var targetType = _targetResource?.Destructible?.GetDestructibleType() ?? DestructibleType.Default;
            string trigger = _swingChain.Swing(_zanim, _animator, weapon, _character.GetTimeSinceLastAttack(), targetType);
            
            if (VerboseLogging)
                Debug.Log($"[ResourceGathering] {Companion.companionName} playing animation: {trigger}");
        }
                
        // 0.2.255 (Fire's homestead test): one always-on line per node / tree / stump the companion worked,
        // with its hit count, when it breaks, when the companion moves to another target, or when gathering ends.
        private GameObject _choreObject;
        private string _choreName;
        private ResourceDataHelper.ToolType _choreTool;
        private int _choreHits;

        // 0.2.268 ([lead]: the runtime tier, not a guess): one line per node prefab per session at its first hit.
        private static readonly HashSet<string> s_tierSaid = new HashSet<string>();

        private void CountChoreHit(ItemDrop.ItemData weapon)
        {
            if (_targetResource == null) return;
            if (!ReferenceEquals(_choreObject, _targetResource.GameObject))
            {
                string prefab = _targetResource.GameObject != null ? Utils.GetPrefabName(_targetResource.GameObject) : "?";
                if (s_tierSaid.Add(prefab))
                    AI.ChoreBrain.ChoreDone(Companion?.companionName, _targetResource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe ? "mining" : "wood",
                        $"first hit on {prefab}: it needs tool tier {_targetResource.MinToolTier}; hitting with {(weapon?.m_dropPrefab != null ? weapon.m_dropPrefab.name : weapon?.m_shared?.m_name ?? "bare hands")} (tool tier {weapon?.m_shared?.m_toolTier ?? 0})");
                // 0.2.268: only a node already hit has a "moved on" line (HR3: the first hit on a node printed "0 hits, never reached it").
                if (_choreObject != null && _choreHits > 0) SayChoreHits("left standing, moved on");
                _choreHits = 0;
                _choreObject = _targetResource.GameObject;
                _choreName = _targetResource.Name;
                _choreTool = _targetResource.RequiredTool;
            }
            _choreHits++;
        }

        // 0.2.268 (HR3: the yard's mudpile chased every run, "never reached it"): nodes and trees a run timed out short of, per companion.
        private const float UnreachedSkipSeconds = 600f;
        private readonly Dictionary<int, float> _unreachedUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _tierSkipSaid = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _goingForSaid = new Dictionary<int, float>();   // 0.2.274: "going for … crafted from base stock", once a minute per node

        private bool IsUnreached(GameObject target) =>
            target != null && _unreachedUntil.TryGetValue(target.GetInstanceID(), out float until) && Time.time < until;

        private void SayChoreHits(string how)
        {
            if (_choreHits > 0)
                AI.ChoreBrain.ChoreDone(Companion?.companionName, _choreTool == ResourceDataHelper.ToolType.Pickaxe ? "mining" : "wood",
                    $"{_choreName ?? "?"}: {_choreHits} hit(s), {how}");
            // 0.2.267 (HR2: three 31 s ResourceGathering runs after "iron ore", no hit and no line): a run that ends without one hit on
            // the node or tree it went for says so, with how far off it still was.
            // The no-hit line only at the end of a run (gathering done / cancelled), about the node it was going for.
            else if (!how.Contains("moved on") && _targetResource != null && _targetResource.RequiresCombat && _targetResource.GameObject != null && _character != null)
            {
                Vector3 at = _targetResource.InteractionPosition;
                // 0.2.271: a rock's distance is to its nearest face (its root sits inside it).
                float off = IsRockTarget ? GatherReach() : Vector3.Distance(_character.transform.position, at);
                bool pickaxe = _targetResource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe;
                // 0.2.268: a run that ended on its own (timed out) short of the node skips it for a while; a cancelled one (an order) doesn't.
                bool skip = how.Contains("gathering done");
                if (skip) _unreachedUntil[_targetResource.GameObject.GetInstanceID()] = Time.time + UnreachedSkipSeconds;
                AI.ChoreBrain.ChoreDone(Companion?.companionName, pickaxe ? "mining" : "wood",
                    $"{Utils.GetPrefabName(_targetResource.GameObject)} (\"{_targetResource.Name}\") at ({at.x:0}, {at.z:0}): 0 hits, never reached it ({off:0.0} m off; {how.Replace("left standing, ", "")})" +
                    (pickaxe ? $"; needs pickaxe tier {_targetResource.MinToolTier}, can get {GetBestObtainableToolTier(ResourceDataHelper.ToolType.Pickaxe)}" : "") +
                    (skip ? $"; skipping it for {UnreachedSkipSeconds / 60f:0} min" : ""));
            }
            _choreObject = null;
            _choreName = null;
            _choreHits = 0;
        }

        // ── 0.2.271 (Fire, on stream: "Companions get stuck in the minerock... make them do some damage or something if they cannot
        // properly aim and connect with the minerock meshes when they break up into multiple parts"). A pickaxe target is aimed at the
        // closest point of its nearest live surface (a MineRock5's nearest standing hit area), not its root inside the rock: the walk
        // goes to a spot just off that face, reach is measured chest -> face, and the swing strikes that area. After
        // MineMissesBeforeStrike swings that couldn't reach a face, one within MineDirectStrikeReach is struck through vanilla's own hit
        // (same HitData, tier rules, drops). A body that makes no headway for WedgeSeconds short of every face steps back out first.
        private const float MineSwingReach = 3f;            // AttackRange plus a swing's arc, chest to face
        private const float MineDirectStrikeReach = 4f;
        private const int MineMissesBeforeStrike = 3;
        private const float AimRefreshSeconds = 0.25f;
        private const float StandOffFace = 0.8f;
        private const float WedgeSeconds = 3f, WedgeMinMove = 0.3f, StepBackDistance = 2.5f, StepBackSeconds = 2f;

        private GameObject _aimFor;
        private float _aimAt = -1f;
        private Vector3 _aimPoint;
        private Collider _aimCollider;
        private int _aimIndex = -1;
        private GameObject _mineMissedFor;
        private int _mineMissed;
        private Vector3 _wedgeFrom;
        private float _wedgeSince = -1f;
        private float _stepBackUntil;
        private Vector3 _stepBackTo;
        private readonly HashSet<int> _wedgeSaid = new HashSet<int>();

        private Vector3 Chest => Transform.position + Vector3.up;

        private bool IsRockTarget => _targetResource != null && _targetResource.GameObject != null
            && (_targetResource.MineRock5 != null || _targetResource.MineRock != null || _targetResource.RequiredTool == ResourceDataHelper.ToolType.Pickaxe);

        // The point the companion works at: a rock's nearest live face (refreshed every AimRefreshSeconds), else the resource's position.
        private Vector3 GatherAim()
        {
            if (_targetResource == null) return Transform.position;
            if (!IsRockTarget) return _targetResource.InteractionPosition;
            if (!ReferenceEquals(_aimFor, _targetResource.GameObject) || Time.time - _aimAt > AimRefreshSeconds
                || _aimCollider == null || !_aimCollider.enabled)
            {
                _aimFor = _targetResource.GameObject;
                _aimAt = Time.time;
                bool found = _targetResource.MineRock5 != null
                    ? ResourceDataHelper.TryNearestMineArea(_targetResource.MineRock5, Chest, out _aimCollider, out _aimPoint, out _aimIndex)
                    : ResourceDataHelper.TryNearestSolidCollider(_targetResource.GameObject, Chest, out _aimCollider, out _aimPoint);
                if (!found) { _aimCollider = null; _aimIndex = -1; _aimPoint = _targetResource.InteractionPosition; }
            }
            return _aimPoint;
        }

        // How far the work is: chest to the rock's nearest face, or (anything else) feet to the resource's position as before.
        private float GatherReach() => IsRockTarget ? Vector3.Distance(Chest, GatherAim()) : Vector3.Distance(Transform.position, _targetResource.InteractionPosition);

        // Where to walk for a rock: just off its nearest face, on the companion's side.
        private Vector3 StandPointFor(Vector3 face)
        {
            Vector3 away = Transform.position - face;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) { away = -Transform.forward; away.y = 0f; }
            Vector3 stand = face + away.normalized * StandOffFace;
            if (FiresCore.World.Surface.GroundNear(stand, out float groundY)) stand.y = groundY;
            return stand;
        }

        // True while stepping back out of a rock (the caller does nothing else this tick). Starts a step back when the body has made no
        // headway (under WedgeMinMove) for WedgeSeconds while short of every face.
        private bool RockUnwedge(float reach)
        {
            if (Time.time < _stepBackUntil) { MoveToPosition(_stepBackTo); return true; }
            if (!IsRockTarget || reach <= AttackRange || (_character != null && _character.InAttack())) { _wedgeSince = -1f; return false; }
            if (_wedgeSince < 0f || (Transform.position - _wedgeFrom).sqrMagnitude > WedgeMinMove * WedgeMinMove)
            {
                _wedgeFrom = Transform.position;
                _wedgeSince = Time.time;
                return false;
            }
            if (Time.time - _wedgeSince < WedgeSeconds) return false;

            Vector3 away = Transform.position - _targetResource.GameObject.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) { away = -Transform.forward; away.y = 0f; }
            _stepBackTo = Transform.position + away.normalized * StepBackDistance;
            if (FiresCore.World.Surface.GroundNear(_stepBackTo, out float groundY)) _stepBackTo.y = groundY;
            _stepBackUntil = Time.time + StepBackSeconds;
            _wedgeSince = -1f;
            _aimAt = -1f;   // aim again from where the step ends
            if (_wedgeSaid.Add(_targetResource.GameObject.GetInstanceID()))
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "mining",
                    $"{Utils.GetPrefabName(_targetResource.GameObject)}: wedged among the fragments for {WedgeSeconds:0} s ({reach:0.0} m from the nearest face); stepping back {StepBackDistance:0.0} m");
            MoveToPosition(_stepBackTo);
            return true;
        }

        // A MineRock5 swing: the nearest standing area, struck when its face is within MineSwingReach; else a miss, and after
        // MineMissesBeforeStrike misses in a row an area within MineDirectStrikeReach is struck directly (logged).
        private void StrikeMineRock5(ItemDrop.ItemData weapon)
        {
            var rock = _targetResource.MineRock5;
            if (!ReferenceEquals(_mineMissedFor, _targetResource.GameObject)) { _mineMissedFor = _targetResource.GameObject; _mineMissed = 0; }
            if (!ResourceDataHelper.TryNearestMineArea(rock, Chest, out Collider area, out Vector3 point, out int index)) return;   // nothing standing
            float reach = Vector3.Distance(Chest, point);
            bool direct = false;
            if (reach > MineSwingReach)
            {
                _mineMissed++;
                if (_mineMissed < MineMissesBeforeStrike || reach > MineDirectStrikeReach)
                {
                    if (VerboseLogging)
                        Debug.Log($"[ResourceGathering] {Companion.companionName} swing short of {_targetResource.Name}: nearest face {reach:0.0} m (miss {_mineMissed})");
                    return;
                }
                direct = true;
            }
            var hitData = ResourceDataHelper.CreateResourceHitData(_targetResource, _character, weapon, point, (point - Chest).normalized, area);
            if (!ResourceDataHelper.StrikeMineArea(rock, area, point, hitData)) return;
            if (direct)
                AI.ChoreBrain.ChoreDone(Companion?.companionName, "mining",
                    $"{Utils.GetPrefabName(_targetResource.GameObject)}: {_mineMissed} swings missed; struck area {index} directly ({hitData.m_damage.m_pickaxe:0} pickaxe damage, tool tier {hitData.m_toolTier}, face {reach:0.0} m off)");
            _mineMissed = 0;
            CountChoreHit(weapon);
            var skills = Companion.GetSkills();
            if (skills != null && weapon != null) skills.RaiseSkill(weapon.m_shared.m_skillType, 1f);
        }

        private void ApplyFallbackMineRock5Damage(MineRock5 rock, HitData hitData)
        {
            if (rock == null) return;
            
            var colliders = rock.GetComponentsInChildren<Collider>();
            if (colliders == null || colliders.Length == 0) return;
            
            Collider closestArea = null;
            float closestDist = float.MaxValue;
            
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                
                float dist = Vector3.Distance(Transform.position, collider.bounds.center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestArea = collider;
                }
            }
            
            if (closestArea != null && closestDist < 5f)
            {
                hitData.m_hitCollider = closestArea;
                hitData.m_point = closestArea.bounds.center;
                hitData.m_radius = 0f;
                
                rock.Damage(hitData);
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Applied fallback damage to MineRock5 area: {closestArea.name}");
            }
        }
        
        private void ApplyFallbackMineRockDamage(MineRock rock, HitData hitData)
        {
            if (rock == null) return;
            
            var colliders = rock.GetComponentsInChildren<Collider>();
            if (colliders == null || colliders.Length == 0) return;
            
            Collider closestCollider = null;
            float closestDist = float.MaxValue;
            
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled) continue;
                
                float dist = Vector3.Distance(Transform.position, collider.bounds.center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestCollider = collider;
                }
            }
            
            if (closestCollider != null && closestDist < 5f)
            {
                hitData.m_hitCollider = closestCollider;
                hitData.m_point = closestCollider.bounds.center;
                hitData.m_radius = 0f;
                
                rock.Damage(hitData);
                
                if (VerboseLogging)
                    Debug.Log($"[ResourceGathering] Applied fallback damage to MineRock collider: {closestCollider.name}");
            }
        }
    }
}
