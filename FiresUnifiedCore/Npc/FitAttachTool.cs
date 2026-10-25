using System.Collections.Generic;
using System;
using System.Reflection;
using FiresCore.UI.Gizmo;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Console tool to position, rotate, scale and save each hair, beard and helmet attach with a gizmo.
    /// `fires_fitattach` (devcommands) toggles the tool. It works only while a preview is open: FCC's creator (FccApi.CreatorPreviewBody),
    /// the Companions barber (CompanionBarberScreen.PreviewBody) or RPGmaker's Dressing room (DressingPreviewApi.PreviewBody). It edits
    /// the attached hair / beard / helmet of that body with the gizmo reused from FDT (FitTransformGizmo).
    /// Keys while on: Tab = next part (Hair / Beard / Helmet), Q / W / E = move / rotate / scale handles, arrows + PageUp/PageDown =
    /// nudge (Shift = fine), Enter = save for this model, Backspace = reset this model's part to the computed fit, Esc = leave.
    /// Saves go through AttachFits (server table, admins only, synced to everyone).
    /// </summary>
    [HarmonyPatch]   // without the class attribute Core's PatchAll skipped the InitTerminal postfix: fires_fitattach never existed
    public sealed class FitAttachTool : MonoBehaviour
    {
        private static FitAttachTool s_instance;
        private AttachFits.Part _part = AttachFits.Part.Hair;
        private GameObject _body;
        private string _toast; private float _toastUntil;

        public static bool Active => s_instance != null && s_instance.enabled;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        [HarmonyPostfix]
        private static void AddCommand()
        {
            if (s_added) return;
            s_added = true;
            new Terminal.ConsoleCommand("fires_fitattach",
                "Fit hair / beard / helmet on the model shown in the character creator, companion barber or Dressing room (gizmo; Enter saves per model)",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (s_instance == null)
                    {
                        var go = new GameObject("FiresFitAttachTool");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        s_instance = go.AddComponent<FitAttachTool>();
                        s_instance.enabled = false;
                    }
                    s_instance.enabled = !s_instance.enabled;
                    if (!s_instance.enabled) s_instance.Leave();
                    Debug.Log("[FitAttachTool] fit tool " + (s_instance.enabled ? "ON" : "OFF"));   // the proof line tests wait for
                    args.Context?.AddString(s_instance.enabled
                        ? "fit tool ON: Tab part, the Move / Rotate / Scale / All and Local / World pills (Ctrl while rotating = 15 degree steps), arrows + PgUp/PgDn nudge (Shift fine), Enter save, Backspace reset, Esc leave"
                        : "fit tool OFF");
                }), isCheat: true);
        }
        private static bool s_added;

        // ---- which preview is open -----------------------------------------------------------------------------------------------
        private static readonly (string type, string member)[] s_hooks =
        {
            ("FiresCharacterCreator.FccApi, FiresCharacterCreator", "CreatorPreviewBody"),
            ("VerdantsAscent.Modules.Companions.CompanionBarberScreen, FiresCompanions", "PreviewBody"),
            ("FiresRPGmaker.Integration.DressingPreviewApi, FiresRPGmaker", "PreviewBody"),
        };
        private static MemberInfo[] s_members;

        private static GameObject PreviewBody()
        {
            if (s_members == null)
            {
                s_members = new MemberInfo[s_hooks.Length];
                for (int i = 0; i < s_hooks.Length; i++)
                {
                    var t = Type.GetType(s_hooks[i].type, false);
                    if (t == null) continue;
                    s_members[i] = (MemberInfo)t.GetProperty(s_hooks[i].member, BindingFlags.Public | BindingFlags.Static)
                        ?? t.GetField(s_hooks[i].member, BindingFlags.Public | BindingFlags.Static);
                }
            }
            foreach (var m in s_members)
            {
                if (m == null) continue;
                object v = null;
                try { v = m is PropertyInfo p ? p.GetValue(null) : ((FieldInfo)m).GetValue(null); } catch { }
                if (v is GameObject go && go != null) return go;
            }
            return null;
        }

        // Hair / beard on a model-swapped PLAYER: FCC 1.0.44 draws them as its own copies on the model's head
        // (FccApi.ModelHairCopy / ModelBeardCopy); the vanilla items stay hidden. Those copies are the target when FCC has them.
        private static MethodInfo s_fccHair, s_fccBeard, s_fccSave, s_fccReset; private static bool s_fccLooked;
        private static void LookFcc()
        {
            if (s_fccLooked) return;
            s_fccLooked = true;
            var t = Type.GetType("FiresCharacterCreator.FccApi, FiresCharacterCreator", false);
            if (t == null) return;
            s_fccHair = t.GetMethod("ModelHairCopy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            s_fccBeard = t.GetMethod("ModelBeardCopy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
            s_fccSave = t.GetMethod("SaveModelCopyFit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(bool) }, null);
            s_fccReset = t.GetMethod("ResetModelCopyFit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(bool) }, null);
        }
        private static GameObject FccCopy(GameObject body, AttachFits.Part part)
        {
            if (part == AttachFits.Part.Helmet || body == null) return null;
            LookFcc();
            var m = part == AttachFits.Part.Hair ? s_fccHair : s_fccBeard;
            try { return m != null ? m.Invoke(null, new object[] { body }) as GameObject : null; } catch { return null; }
        }
        // With TossinShade hair PHYSICS on swapped models (FCC 1.0.47 then makes no copy), the physics chain root TS hangs under
        // m_helmet ("FiresHairRoot" / "FiresBeardRoot") is the target; its local pose is saved as Core AttachFit row "<Model>|HairPhysics_attach"
        // ("BeardPhysics_attach"), which TS reads when it builds the chain.
        // Say once per part / target which
        // transform the tool edits, and whether an edit is still there a frame later (something else - TS rebuilding its chain - resets it).
        private string _loggedTarget; private Transform _heldT; private Vector3 _heldPos; private Quaternion _heldRot; private int _heldFrame = -1; private bool _heldWarned;
        private void LogTarget(GameObject target)
        {
            string key = _part + ":" + (target != null ? Path(target.transform) : "none");
            if (key == _loggedTarget) return;
            _loggedTarget = key; _heldWarned = false;
            string kind = target == null ? "none" : FccCopy(_body, _part) == target ? "FCC static copy" : target.name.StartsWith("Fires") && target.name.EndsWith("Root") ? "TossinShade physics root" : "attach instance";
            Debug.Log($"[FitAttachTool] target {_part}: {kind} {(target != null ? Path(target.transform) : "")} on preview {(_body != null ? _body.name : "none")}");
        }
        private void CheckHeld(Transform t)
        {
            if (_heldT == t && _heldFrame == Time.frameCount - 1 && !_heldWarned
                && ((t.localPosition - _heldPos).sqrMagnitude > 1e-8f || Quaternion.Angle(t.localRotation, _heldRot) > 0.01f)
                && !UnityEngine.Input.GetMouseButton(0))
            {
                _heldWarned = true;
                Debug.LogWarning($"[FitAttachTool] {Path(t)} was changed between frames by something else (not the tool): {_heldPos} -> {t.localPosition}");
            }
        }
        private void LateUpdate()
        {
            var t = _body != null ? Target(_body, _body.GetComponent<VisEquipment>(), _part) : null;
            _heldT = t != null ? t.transform : null;
            if (_heldT != null) { _heldPos = _heldT.localPosition; _heldRot = _heldT.localRotation; _heldFrame = Time.frameCount; }
        }
        private static string Path(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null && sb.Length < 200; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static Transform PhysicsRoot(GameObject body, AttachFits.Part part)
        {
            if (body == null || part == AttachFits.Part.Helmet) return null;
            string name = part == AttachFits.Part.Hair ? "FiresHairRoot" : "FiresBeardRoot";
            foreach (var t in body.GetComponentsInChildren<Transform>(false)) if (t.name == name) return t;
            // the creator preview is a clone without TossinShade's chain: the live local player carries it
            var me = Player.m_localPlayer != null ? Player.m_localPlayer.gameObject : null;
            if (me != null && me != body) foreach (var t in me.GetComponentsInChildren<Transform>(false)) if (t.name == name) return t;
            return null;
        }
        private static string FccModel(GameObject body)
        {
            var p = (body != null ? body.GetComponent<Player>() : null) ?? Player.m_localPlayer; if (p == null) return null;
            var t = Type.GetType("FiresCharacterCreator.FccApi, FiresCharacterCreator", false);
            try { return t?.GetMethod("GetModel", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Player) }, null)?.Invoke(null, new object[] { p }) as string; } catch { return null; }
        }
        private static GameObject Target(GameObject body, VisEquipment vis, AttachFits.Part part) => FccCopy(body, part) ?? PhysicsRoot(body, part)?.gameObject ?? AttachFits.Instance(vis, part);

        private static string ModelOf(GameObject body)
        {
            var st = body != null ? body.GetComponent<BodyModelState>() : null;
            return st != null && !string.IsNullOrEmpty(st.Applied) ? st.Applied : "";
        }

        // ---- frame ---------------------------------------------------------------------------------------------------------------
        private void Update()
        {
            _body = PreviewBody();
            var vis = _body != null ? _body.GetComponent<VisEquipment>() : null;
            AttachFits.EditingVis = vis;
            if (_body == null) { FitTransformGizmo.Mode = FitTransformGizmo.Tool.None; return; }
            if (FitTransformGizmo.Mode == FitTransformGizmo.Tool.None) FitTransformGizmo.Mode = FitTransformGizmo.Tool.Transform;   // opens in All (rings + arrows + planes + uniform)
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Escape)) { enabled = false; Leave(); return; }
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Tab)) _part = (AttachFits.Part)(((int)_part + 1) % 3);
            // W is vanilla "forward": no Q / W / E mode keys; the pills in OnGUI switch the mode
            
            
            var target = Target(_body, vis, _part);
            string model = ModelOf(_body);
            LogTarget(target);
            if (target != null) { CheckHeld(target.transform); Nudge(target.transform); }
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Return) || FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.KeypadEnter))
            {
                var phys = FccCopy(_body, _part) == null ? PhysicsRoot(_body, _part) : null;
                string physModel = phys != null ? FccModel(_body) : null;
                if (phys != null && string.IsNullOrEmpty(physModel)) physModel = ModelOf(_body);   // a Core-swapped model (companion / NPC) has no FCC model
                Debug.Log($"[FitAttachTool] save {_part}: fcc copy {(FccCopy(_body, _part) != null)}, physics root {(phys != null ? Path(phys) : "none")}, model '{physModel ?? model}'");
                if (phys != null && !string.IsNullOrEmpty(physModel))
                {
                    string attach = _part == AttachFits.Part.Hair ? "HairPhysics_attach" : "BeardPhysics_attach";
                    bool ok2 = FiresCore.Visuals.AttachFit.Set(physModel, attach, new FiresCore.Visuals.AttachFit.Tune { Off = phys.localPosition, Euler = phys.localEulerAngles, Scale = phys.localScale.x });
                    Toast(ok2 ? $"Saved {_part} fit (physics hair, TossinShade) for {physModel}" : "Fit not saved (see log)");
                }
                else { var fccCopy = FccCopy(_body, _part);
                if (fccCopy != null)
                {
                    LookFcc();
                    object ok = null; try { ok = s_fccSave?.Invoke(null, new object[] { _body, _part == AttachFits.Part.Hair }); } catch (Exception ex) { Debug.LogWarning("[FitAttachTool] FCC save threw: " + ex.Message); }
                    Toast(ok is bool b && b ? $"Saved {_part} fit (character creator model)" : "FCC could not save this fit (needs FCC with SaveModelCopyFit)");
                }
                else if (string.IsNullOrEmpty(model)) Toast("This body uses its own model: fits are saved per swapped model only");
                else if (target == null) Toast($"No {_part} on this body to save");
                else
                {
                    var t = target.transform;
                    AttachFits.Save(model, _part, new AttachFits.Fit { Pos = t.localPosition, Euler = t.localEulerAngles, Scale = t.localScale }, false);
                    Toast($"Saved {_part} fit for {model}");
                }
                }
            }
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Backspace) && FccCopy(_body, _part) == null && PhysicsRoot(_body, _part) != null && !string.IsNullOrEmpty(FccModel(_body)))
            {
                FiresCore.Visuals.AttachFit.Remove(FccModel(_body), _part == AttachFits.Part.Hair ? "HairPhysics_attach" : "BeardPhysics_attach");
                Toast($"Reset {_part} fit (physics hair, TossinShade)");
            }
            else if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Backspace) && FccCopy(_body, _part) != null)
            {
                LookFcc();
                try { s_fccReset?.Invoke(null, new object[] { _body, _part == AttachFits.Part.Hair }); } catch { }
                Toast($"Reset {_part} fit (character creator model)");
            }
            else if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.Backspace) && !string.IsNullOrEmpty(model))
            {
                AttachFits.Save(model, _part, default, true);
                if (target != null) { target.transform.localPosition = Vector3.zero; target.transform.localRotation = Quaternion.identity; target.transform.localScale = Vector3.one; }
                Toast($"Reset {_part} fit for {model}");
            }
        }

        // Every key read goes through Core's FiresKeys
        // seam (exactly UnityEngine.Input for a player; a test's injected key reaches it too).
        private static void Nudge(Transform t)
        {
            bool fine = FiresCore.Input.FiresKeys.GetKey(KeyCode.LeftShift) || FiresCore.Input.FiresKeys.GetKey(KeyCode.RightShift);
            Vector3 d = Vector3.zero;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.LeftArrow)) d.x -= 1f;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.RightArrow)) d.x += 1f;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.UpArrow)) d.z += 1f;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.DownArrow)) d.z -= 1f;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.PageUp)) d.y += 1f;
            if (FiresCore.Input.FiresKeys.GetKeyDown(KeyCode.PageDown)) d.y -= 1f;
            if (d == Vector3.zero) return;
            switch (FitTransformGizmo.Mode)
            {
                case FitTransformGizmo.Tool.Rotate: t.localRotation = t.localRotation * Quaternion.Euler(d * (fine ? 0.5f : 5f)); break;
                case FitTransformGizmo.Tool.Scale: t.localScale *= 1f + (d.x + d.y + d.z) * (fine ? 0.005f : 0.05f); break;
                default: t.localPosition += d * (fine ? 0.001f : 0.01f); break;
            }
        }

        private void Toast(string s)
        {
            _toast = s; _toastUntil = Time.unscaledTime + 3f;
            try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, s); } catch { }
            Debug.Log("[FitAttachTool] " + s);
        }

        private void Leave()
        {
            FitTransformGizmo.Clear();
            AttachFits.EditingVis = null;
        }

        private void OnDisable() => Leave();

        private void OnGUI()
        {
            if (_body == null) { GUI.Label(new Rect(20, 20, 700, 24), "fit tool: open the character creator, companion barber or Dressing room"); return; }
            var vis = _body.GetComponent<VisEquipment>();
            var target = Target(_body, vis, _part);
            var cam = CameraFor(_body);
            // The panel and the pills scale with the screen height; the pills come first so their clicks never reach the gizmo
            float k = Mathf.Max(1f, Screen.height / 1080f);
            var keep = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1f));
            string model = ModelOf(_body);
            GUI.Box(new Rect(20, 20, 600, 130), "");
            GUI.Label(new Rect(30, 24, 580, 22), $"Fit: {(string.IsNullOrEmpty(model) ? "own body (not saved)" : model)}   Part: {_part}{(target == null ? " (none on this body)" : "")}   Tool: {ToolName(FitTransformGizmo.Mode)} ({(FitTransformGizmo.LocalSpace ? "Local" : "World")})");
            float px = 30f;
            foreach (var (tool, label) in s_modes)
            {
                bool on = FitTransformGizmo.Mode == tool;
                if (FiresCore.UI.Shell.Kit.FiresImguiWidgets.PillSwitch(Pill(label, px, 48, 80, k), on, label, label) != on) FitTransformGizmo.Mode = tool;
                px += 86f;
            }
            FitTransformGizmo.LocalSpace = FiresCore.UI.Shell.Kit.FiresImguiWidgets.PillSwitch(Pill("Space", px + 10f, 48, 90, k), FitTransformGizmo.LocalSpace, "Local", "World");
            // The part as pills too (Tab still cycles)
            px = 30f;
            foreach (AttachFits.Part part in new[] { AttachFits.Part.Hair, AttachFits.Part.Beard, AttachFits.Part.Helmet })
            {
                bool on = _part == part;
                string label = part.ToString();
                if (FiresCore.UI.Shell.Kit.FiresImguiWidgets.PillSwitch(Pill(label, px, 74, 80, k), on, label, label) != on) _part = part;
                px += 86f;
            }
            GUI.Label(new Rect(30, 100, 580, 22), "Tab part  Ctrl = 15 deg steps  arrows PgUp/PgDn nudge (Shift fine)  Enter save  Backspace reset  Esc leave");
            if (_toast != null && Time.unscaledTime < _toastUntil) GUI.Label(new Rect(30, 122, 580, 22), _toast);
            GUI.matrix = keep;
            if (target != null && cam != null) FitTransformGizmo.OnGui(cam, target, true);
        }

        // Each pill's rect (tests click the real pills) as drawn, in GUI coordinates (px, y DOWN, Event.mousePosition's
        // space, the screen-height scale applied). Names: "Move", "Rotate", "Scale", "All", "Space" (Local/World), "Hair", "Beard", "Helmet".
        private static readonly Dictionary<string, Rect> s_pillRects = new Dictionary<string, Rect>();
        private static Rect Pill(string name, float x, float y, float w, float k)
        {
            var r = new Rect(x, y, w, 22f);
            s_pillRects[name] = new Rect(x * k, y * k, w * k, 22f * k);
            return r;
        }
        /// <summary>The drawn rect of a fit-tool pill (see above), or Rect.zero before the tool drew it. Add-only.</summary>
        public static Rect PillRect(string name) => s_pillRects.TryGetValue(name ?? "", out var r) ? r : Rect.zero;

        private static readonly (FitTransformGizmo.Tool, string)[] s_modes =
        {
            (FitTransformGizmo.Tool.Move, "Move"), (FitTransformGizmo.Tool.Rotate, "Rotate"), (FitTransformGizmo.Tool.Scale, "Scale"), (FitTransformGizmo.Tool.Transform, "All"),
        };
        private static string ToolName(FitTransformGizmo.Tool t) => t == FitTransformGizmo.Tool.Transform ? "All" : t.ToString();

        // the enabled camera that has the body in view (the preview camera first: highest depth wins)
        private static Camera CameraFor(GameObject body)
        {
            Camera best = null;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.enabled) continue;
                var vp = c.WorldToViewportPoint(body.transform.position + Vector3.up);
                if (vp.z <= 0f || vp.x < -0.2f || vp.x > 1.2f || vp.y < -0.2f || vp.y > 1.2f) continue;
                if ((c.cullingMask & (1 << body.layer)) == 0) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best;
        }
    }
}
