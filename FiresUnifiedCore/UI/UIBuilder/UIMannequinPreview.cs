using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using FiresCore.Npc;
using FiresCore.Storage;

namespace FiresCore.UI
{
    /// <summary>
    /// Renders a remote player's appearance for the leaderboard preview. Unlike
    /// <see cref="UIOverridePlayerPreview"/>, which binds the local player, it stages an isolated mannequin far above
    /// the world and renders it with distance isolation, so it never bleeds into the live scene. One rig (camera,
    /// RenderTexture, stage, light) is reused across rows, rendering happens on demand, and headless builds never
    /// create it.
    /// </summary>
    public class UIMannequinPreview : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        public int TextureWidth = 512;
        public int TextureHeight = 768;

        public float FieldOfView = 28f;
        public float MinDistance = 0.5f;
        public float MaxDistance = 20f;

        public float YawSpeed = 0.4f;
        public float PitchSpeed = 0.4f;
        public float ZoomSpeed = 1.0f;

        private RawImage _rawImage;
        private Camera _camera;
        private GameObject _cameraGO;
        private RenderTexture _renderTexture;

        private PreviewStage _stage;
        private GameObject _mannequin;
        private Light _keyLight;

        private float _yaw = 180f;
        private float _pitch = 8f;
        private float _distance = 4f;
        private Vector3 _focus = Vector3.zero;

        private bool _isDragging;
        private bool _hasMannequin;

        // SetAppearance may be called before Start builds the rig (a cached record applied the same frame the
        // widget is created). Hold the latest request and apply it once the rig exists.
        private PlayerAppearance _pendingAppearance;
        private bool _hasPending;

        /// <summary>The RenderTexture for binding to a RawImage directly, if the widget isn't on one.</summary>
        public RenderTexture Texture => _renderTexture;

        /// <summary>True when a mannequin is currently staged and renderable.</summary>
        public bool HasPreview => _hasMannequin;

        /// <summary>
        /// Adds a <see cref="UIMannequinPreview"/> to <paramref name="rawImage"/> and binds it. Returns
        /// null on dedi/headless (no rig is created server-side).
        /// </summary>
        public static UIMannequinPreview Create(RawImage rawImage)
        {
            if (PreviewStage.IsHeadless || rawImage == null) return null;
            var widget = rawImage.gameObject.GetComponent<UIMannequinPreview>();
            if (widget == null) widget = rawImage.gameObject.AddComponent<UIMannequinPreview>();
            return widget;
        }

        private void Start()
        {
            if (PreviewStage.IsHeadless) { enabled = false; return; }

            _rawImage = GetComponent<RawImage>();
            BuildRig();

            if (_hasPending)
            {
                _hasPending = false;
                var pending = _pendingAppearance;
                var pendingNpc = _pendingNpc;
                _pendingAppearance = null;
                _pendingNpc = null;
                if (pendingNpc != null) SetNpcAppearance(pendingNpc);   // an NPC request held before the rig existed
                else SetAppearance(pending);
            }
        }

        private void OnEnable()
        {
            if (_cameraGO != null) _cameraGO.SetActive(true);
        }

        private void OnDisable()
        {
            if (_cameraGO != null) _cameraGO.SetActive(false);
        }

        private void OnDestroy()
        {
            StopAllCoroutines();

            _stage?.Clear();
            _stage = null;
            _mannequin = null;

            if (_camera != null) _camera.targetTexture = null;
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_cameraGO != null) { Destroy(_cameraGO); _cameraGO = null; }
            _camera = null;
            _keyLight = null;
        }

        // ── rig ────────────────────────────────────────────────────────────────

        private void BuildRig()
        {
            _stage = new PreviewStage();

            _renderTexture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32);
            _renderTexture.antiAliasing = 2;
            _renderTexture.Create();

            _cameraGO = new GameObject("FiresMannequinPreviewCamera");
            _cameraGO.transform.SetParent(null);

            _camera = _cameraGO.AddComponent<Camera>();
            _camera.targetTexture = _renderTexture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0, 0, 0, 0); // transparent
            _camera.cullingMask = ~0;                        // distance isolation, not layer
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 50f;
            _camera.depth = -10;
            _camera.enabled = false;                         // render-on-demand

            if (_rawImage != null)
            {
                _rawImage.texture = _renderTexture;
                _rawImage.color = Color.white;
            }
        }

        private void EnsureLight()
        {
            // A dedicated light parented to the stage container — scene lighting at +2000 m under a
            // transparent clear can't be relied on. Soft key + low ambient fill.
            if (_stage == null || _stage.Container == null) return;
            if (_keyLight != null)
            {
                _keyLight.transform.SetParent(_stage.Container.transform, false);
                return;
            }

            var lightGO = new GameObject("FiresMannequinKeyLight");
            lightGO.transform.SetParent(_stage.Container.transform, false);
            lightGO.transform.localRotation = Quaternion.Euler(35f, 200f, 0f);
            _keyLight = lightGO.AddComponent<Light>();
            _keyLight.type = LightType.Directional;
            _keyLight.color = Color.white;
            _keyLight.intensity = 1.1f;
            _keyLight.shadows = LightShadows.None;
            _keyLight.renderMode = LightRenderMode.ForcePixel;
            // a directional light lights the whole
            // world, so it is on only for the preview's own Render (RenderOnce).
            _keyLight.enabled = false;
        }

        // ── public API ───────────────────────────────────────────────────────

        /// <summary>
        /// (Re)dresses the staged mannequin from <paramref name="appearance"/> and renders once. A null
        /// or empty record clears the box to the transparent "unavailable" state. The rig is reused —
        /// only the mannequin GameObject inside the stage is rebuilt.
        /// </summary>
        public void SetAppearance(PlayerAppearance appearance)
        {
            if (PreviewStage.IsHeadless) return;
            if (_stage == null)
            {
                // Rig not built yet (Start hasn't run); remember the request and apply it then.
                _pendingAppearance = appearance;
                _pendingNpc = null;
                _hasPending = true;
                return;
            }

            StopAllCoroutines();
            _hasNpcSignature = false;   // a player appearance replaced any NPC build

            if (IsEmpty(appearance))
            {
                ShowUnavailable();
                return;
            }

            var built = FiresMannequinBuilder.Build(appearance);
            if (built == null)
            {
                ShowUnavailable();
                return;
            }

            // Reuse the rig: stage the new mannequin (PreviewStage.Stage clears the previous one).
            if (!_stage.Stage(built, ownsTarget: true))
            {
                Destroy(built);
                ShowUnavailable();
                return;
            }
            _mannequin = built;
            _hasMannequin = true;

            EnsureLight();
            StandPose("staged"); FrameToBounds(_stage.Bounds);
            RenderOnce();

            // Two-frame deferred reframe — SkinnedMeshRenderer bounds are degenerate until bones bind.
            StartCoroutine(_stage.ReframeAfterDelay(b =>
            {
                StandPose("settled"); FrameToBounds(b);
                RenderOnce();
            }));
        }


        // the last NPC signature built, so SetNpcAppearance is a no-op while nothing changed.
        private int _npcSignature;
        private bool _hasNpcSignature;
        private NpcAppearance _pendingNpc;

        /// <summary>raised after each real SetNpcAppearance rebuild with the built (visual-only) root, before its render; RPGmaker copies dyes here. Add-only.</summary>
        public event Action<GameObject> NpcRebuilt;

        /// <summary>renders the staged mannequin again (after a NpcRebuilt handler changed it). Add-only.</summary>
        public void RequestRender() { if (!PreviewStage.IsHeadless && _stage != null) RenderOnce(); }

        /// <summary>
        /// RPGmaker Dressing room live preview: (re)builds the staged NPC from <paramref name="appearance"/> and renders,
        /// only when its <see cref="NpcAppearance.GetSignature"/> changed; called on every edit, otherwise a no-op. Add-only.
        /// </summary>
        public void SetNpcAppearance(NpcAppearance appearance)
        {
            if (PreviewStage.IsHeadless) return;
            if (_stage == null) { _pendingNpc = appearance; _hasPending = true; _pendingAppearance = null; return; }
            if (appearance == null) { _hasNpcSignature = false; ShowUnavailable(); return; }

            int sig = appearance.GetSignature();
            if (_hasNpcSignature && sig == _npcSignature && _hasMannequin) return;

            StopAllCoroutines();
            var built = FiresMannequinBuilder.Build(appearance);
            if (built == null) { _hasNpcSignature = false; ShowUnavailable(); return; }
            if (!_stage.Stage(built, ownsTarget: true)) { Destroy(built); _hasNpcSignature = false; ShowUnavailable(); return; }
            _mannequin = built;
            _hasMannequin = true;
            _npcSignature = sig;
            _hasNpcSignature = true;
            FiresCore.Logging.FiresLogger.LogDebug($"[MannequinPreview] npc rebuild sig {sig} scale {appearance.Scale:0.00} model {(string.IsNullOrEmpty(appearance.ModelOverridePrefab) ? "human" : appearance.ModelOverridePrefab)}");

            try { NpcRebuilt?.Invoke(built); } catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[MannequinPreview] a NpcRebuilt handler threw: {ex.Message}"); }
            EnsureLight();
            StandPose("staged"); FrameToBounds(_stage.Bounds);
            RenderOnce();
            StartCoroutine(_stage.ReframeAfterDelay(b => { StandPose("settled"); FrameToBounds(b); RenderOnce(); }));
        }

        /// <summary>Clears the box to the transparent "preview unavailable" state.</summary>
        public void ShowUnavailable()
        {
            _hasMannequin = false;
            _stage?.Clear();
            _mannequin = null;
            RenderOnce(); // transparent clear → empty box
        }

        // ── camera / render ────────────────────────────────────────────────────

        // ZOOM fits EVERY visible renderer (body, armour, held weapons,
        // shields, capes) with a 10 % margin, but the camera stays CENTRED horizontally on the BODY (a long spear never shoves the body
        // off-centre; the vertical aim is the full bounds' middle). Level pitch (reused from VAInventory CharacterPreview: level look-at
        // the body); the renderer walk is PreviewStage.CalculateBounds'. Drag / wheel unchanged. Every rebuild and the delayed reframe
        // (each equip change rebuilds) land here.
        // the stripped clone has no Character driving its Animator, so "onGround" stays at its
        // default false and the controller plays the fall/crouch pose. Standing idle = what Character.UpdateGroundContact/UpdateWalking set
        // on a live one (onGround true, inWater false, speeds 0). Applied after staging (activation resets parameters) and again on the
        // delayed reframe; one Debug line names the state actually playing so the fix is proven, not guessed. Live characters: VAInventory Bind.
        private string _lastPoseLog;
        private void StandPose(string from)
        {
            if (_mannequin == null) return;
            var anim = _mannequin.GetComponentInChildren<Animator>(true);
            if (anim == null || anim.runtimeAnimatorController == null) { LogPose(from, "no Animator/controller (static mesh: nothing to pose)"); return; }
            // params alone never left the state it was in.
            // Wake the animator, rebind to the rest pose, set the params, then PLAY a standing state that exists in this controller.
            anim.enabled = true; anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; anim.Rebind();
            int set = 0;
            foreach (var p in anim.parameters)
            {
                switch (p.name)
                {
                    case "onGround": if (p.type == AnimatorControllerParameterType.Bool) { anim.SetBool(p.nameHash, true); set++; } break;
                    case "inWater": case "crouching": case "encumbered": case "flying": case "attach_chair": case "attach_bed": case "attach_throne": case "attach_sitship": case "emote_sit":
                        if (p.type == AnimatorControllerParameterType.Bool) { anim.SetBool(p.nameHash, false); set++; } break;
                    case "forward_speed": case "sideway_speed": case "turn_speed":
                        if (p.type == AnimatorControllerParameterType.Float) { anim.SetFloat(p.nameHash, 0f); set++; } break;
                }
            }
            string played = "none found";
            foreach (string s in new[] { "Movement", "Idle", "idle", "Locomotion", "Grounded", "Stand" })
            {
                int h = Animator.StringToHash(s);
                if (anim.HasState(0, h)) { anim.Play(h, 0, 0f); played = s; break; }
            }
            anim.Update(0.1f);
            from += ", played state " + played;
            string clip = "?";
            try { var ci = anim.GetCurrentAnimatorClipInfo(0); if (ci.Length > 0 && ci[0].clip != null) clip = ci[0].clip.name; } catch { }
            bool hasGround = false; foreach (var p in anim.parameters) if (p.name == "onGround") { hasGround = true; break; }
            LogPose(from, $"{set} param(s) set, onGround {(hasGround ? anim.GetBool("onGround").ToString() : "n/a")}, playing clip '{clip}' (state {anim.GetCurrentAnimatorStateInfo(0).shortNameHash})");
        }

        private void LogPose(string from, string what)
        {
            string line = $"[MannequinPreview] pose ({from}): {what}";
            if (line != _lastPoseLog) { _lastPoseLog = line; FiresCore.Logging.FiresLogger.LogDebug(line); }
        }

        private const float FrameMargin = 0.10f;
        private string _lastFrameLog;
        private void FrameToBounds(Bounds bounds)
        {
            Bounds full = _mannequin != null && VisibleBounds(_mannequin, out var v) ? v : bounds;
            Bounds body = _mannequin != null && BodyBounds(_mannequin, out var b) ? b : full;
            _focus = new Vector3(body.center.x, full.center.y, body.center.z);
            _pitch = 0f;
            float fov = (_camera != null ? _camera.fieldOfView : FieldOfView) * Mathf.Deg2Rad;
            float aspect = _camera != null && _camera.aspect > 0.01f ? _camera.aspect : (float)TextureWidth / TextureHeight;
            float tanV = Mathf.Tan(fov * 0.5f);
            float margin = full.size.y * FrameMargin;
            // half-extents measured from the (body-centred) focus, so the far side of a long weapon still fits
            float halfH = Mathf.Max(full.max.y - _focus.y, _focus.y - full.min.y);
            float halfW = Mathf.Max(Mathf.Max(full.max.x - _focus.x, _focus.x - full.min.x), Mathf.Max(full.max.z - _focus.z, _focus.z - full.min.z));   // any yaw
            float fitV = (halfH + margin) / tanV;
            float fitH = (halfW + margin) / (tanV * aspect);
            _distance = Mathf.Clamp(Mathf.Max(fitV, fitH) + halfW, MinDistance, MaxDistance);   // + halfW: the near face sits that much closer
            if (_camera != null)
            {
                _camera.nearClipPlane = Mathf.Max(_distance * 0.01f, 0.02f);
                _camera.farClipPlane = _distance * 10f + 50f;
            }
            ApplyCameraTransform();
            CheckFraming(full);
        }

        /// <summary>Every visible renderer (body, armour, weapons, shields, capes); particles and PreviewStage's outliers (> 100 m) skipped.</summary>
        private static bool VisibleBounds(GameObject root, out Bounds result)
        {
            result = default;
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                var rb = r.bounds;
                if (rb.size.sqrMagnitude < 1e-6f || rb.size.x > 100f || rb.size.y > 100f || rb.size.z > 100f) continue;
                if (!any) { result = rb; any = true; } else result.Encapsulate(rb);
            }
            return any;
        }

        /// <summary>The body / armour SkinnedMeshRenderers only: anything under a VisEquipment attach point ("*attach*") or an ItemDrop
        /// (held / back weapons, shields) is left out. False when nothing qualifies (the caller keeps the stage bounds).</summary>
        private static bool BodyBounds(GameObject root, out Bounds result)
        {
            result = default;
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r == null || !r.enabled || r.bounds.size.sqrMagnitude < 1e-4f) continue;
                if (UnderAttach(r.transform, root.transform)) continue;
                if (!any) { result = r.bounds; any = true; } else result.Encapsulate(r.bounds);
            }
            return any;
        }

        private static bool UnderAttach(Transform t, Transform root)
        {
            for (var p = t; p != null && p != root; p = p.parent)
                if (p.name.IndexOf("attach", StringComparison.OrdinalIgnoreCase) >= 0 || p.GetComponent<ItemDrop>() != null) return true;
            return false;
        }

        // The proof: all 8 corners of the full visible bounds inside the viewport, else a FAIL warning; one Debug line per new framing.
        private void CheckFraming(Bounds body)
        {
            if (_camera == null) return;
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            Vector3 c = body.center, e = body.extents;
            for (int i = 0; i < 8; i++)
            {
                var p = _camera.WorldToViewportPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            if (minX < 0f || maxX > 1f || minY < 0f || maxY > 1f)
                FiresCore.Logging.FiresLogger.LogWarning($"[MannequinPreview] FAIL framing: bounds leave the view (x {minX:0.00}..{maxX:0.00}, y {minY:0.00}..{maxY:0.00})");
            string line = $"[MannequinPreview] framed {Mathf.Max(body.size.x, body.size.z):0.00}x{body.size.y:0.00} m at {_distance:0.00} m (view x {minX:0.00}..{maxX:0.00}, y {minY:0.00}..{maxY:0.00})";
            if (line != _lastFrameLog) { _lastFrameLog = line; FiresCore.Logging.FiresLogger.LogDebug(line); }
        }

        private void ApplyCameraTransform()
        {
            if (_camera == null) return;
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 dir = rot * Vector3.forward;
            _camera.transform.position = _focus - dir * _distance;
            _camera.transform.LookAt(_focus);
        }

        private void RenderOnce()
        {
            if (_camera == null) return;
            ApplyCameraTransform();
            if (_keyLight != null)
            {
                _keyLight.enabled = true;
                if (!_keyLightLogged) { _keyLightLogged = true; FiresCore.Logging.FiresLogger.LogDebug("[MannequinPreview] key light on for render only"); }
            }
            try { _camera.Render(); } catch { }
            finally { if (_keyLight != null) _keyLight.enabled = false; }
        }
        private bool _keyLightLogged;

        // ── preview animation (Dressing hover previews; add-only) ─────────────────────
        // Plays a Core PlayerAnimationCatalog entry (the keys the Idle/Interact/Greet/Bye dropdowns store) on the staged
        // mannequin's own Animator, the way an NPC plays it (OneShot trigger, Loop emote trigger, Hold bool), and renders every
        // frame only while it plays; then the rig goes back to render-on-demand.
        private FiresCore.Npc.Animation.PlayerAnimation _previewAnim;
        private Animator _previewAnimator;
        private float _previewUntil;

        /// <summary>Plays a PlayerAnimationCatalog key on the preview mannequin. "(none)", empty or unknown = Stop.</summary>
        public void PlayPreviewAnimation(string catalogKey)
        {
            var anim = string.IsNullOrEmpty(catalogKey) || catalogKey == "(none)" ? null : FiresCore.Npc.Animation.PlayerAnimationCatalog.Find(catalogKey);
            if (anim == null || _mannequin == null) { StopPreviewAnimation(); return; }
            var animator = _mannequin.GetComponentInChildren<Animator>(true);
            if (animator == null) { Debug.Log($"[MannequinPreview] preview animation '{catalogKey}': the mannequin has no Animator"); return; }
            if (_previewAnimator != null) FiresCore.Npc.Animation.PlayerAnimationCatalog.StopAll(null, _previewAnimator);
            animator.enabled = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime;   // menus can pause game time
            bool ok = FiresCore.Npc.Animation.PlayerAnimationCatalog.Play(null, animator, anim);
            _previewAnim = anim; _previewAnimator = animator;
            _previewUntil = anim.Kind == FiresCore.Npc.Animation.PlayerAnimKind.OneShot ? Time.unscaledTime + Mathf.Max(0.5f, anim.Seconds) + 0.4f : float.MaxValue;
            Debug.Log($"[MannequinPreview] preview animation '{anim.Key}' ({anim.Parameter}, {anim.Kind}) -> {(ok ? "playing" : "not in this animator")}");
        }

        /// <summary>Ends the preview animation; the mannequin goes back to idle.</summary>
        public void StopPreviewAnimation()
        {
            if (_previewAnimator != null)
            {
                FiresCore.Npc.Animation.PlayerAnimationCatalog.StopAll(null, _previewAnimator);
                Debug.Log($"[MannequinPreview] preview animation stopped{(_previewAnim != null ? " ('" + _previewAnim.Key + "')" : "")}");
                _previewUntil = Time.unscaledTime + 0.8f;   // render the blend back to idle, then stop
            }
            _previewAnim = null;
        }

        private void LateUpdate()
        {
            if (_previewAnimator == null) return;
            if (_mannequin == null || _previewAnimator == null) { _previewAnimator = null; return; }
            if (Time.unscaledTime > _previewUntil)
            {
                if (_previewAnim != null && _previewAnim.Kind == FiresCore.Npc.Animation.PlayerAnimKind.OneShot) _previewAnim = null;
                if (_previewAnim == null) { RenderOnce(); _previewAnimator = null; }   // last frame, then render-on-demand again
                return;
            }
            RenderOnce();
        }

        // ── input ────────────────────────────────────────────────────────────

        public void OnBeginDrag(PointerEventData eventData) => _isDragging = true;

        public void OnDrag(PointerEventData eventData)
        {
            if (!_isDragging || !_hasMannequin) return;
            _yaw += eventData.delta.x * YawSpeed;
            _pitch = Mathf.Clamp(_pitch - eventData.delta.y * PitchSpeed, -80f, 80f);
            ApplyCameraTransform();
            RenderOnce();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _isDragging = false;
            RenderOnce();
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!_hasMannequin) return;
            _distance -= eventData.scrollDelta.y * _distance * ZoomSpeed * 0.1f;
            _distance = Mathf.Clamp(_distance, MinDistance, MaxDistance);
            ApplyCameraTransform();
            RenderOnce();
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static bool IsEmpty(PlayerAppearance appearance)
        {
            if (appearance == null) return true;
            // A record with no items, no hair/beard, and no colors is effectively a blank capture.
            return string.IsNullOrEmpty(appearance.RightItem) && string.IsNullOrEmpty(appearance.LeftItem)
                && string.IsNullOrEmpty(appearance.ChestItem) && string.IsNullOrEmpty(appearance.LegItem)
                && string.IsNullOrEmpty(appearance.HelmetItem) && string.IsNullOrEmpty(appearance.ShoulderItem)
                && string.IsNullOrEmpty(appearance.UtilityItem) && string.IsNullOrEmpty(appearance.LeftBackItem)
                && string.IsNullOrEmpty(appearance.RightBackItem) && string.IsNullOrEmpty(appearance.HairItem)
                && string.IsNullOrEmpty(appearance.BeardItem)
                && string.IsNullOrEmpty(appearance.SkinColorRgba) && string.IsNullOrEmpty(appearance.HairColorRgba);
        }
    }
}
