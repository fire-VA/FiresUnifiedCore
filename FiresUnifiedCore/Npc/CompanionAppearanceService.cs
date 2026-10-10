using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>0.2.357: one companion look, as the barber UI edits it. Vanilla has ONE hair colour for hair and beard.</summary>
    public sealed class CompanionAppearance
    {
        public string Hair;
        public string Beard;
        public Color HairColor;
        /// <summary>0.2.368: the beard's own colour (ZDO companion_beardColor). Alpha 0 = not set: the beard follows HairColor.</summary>
        public Color BeardColor;
        public Color SkinColor;

        public bool HasBeardColor => BeardColor.a > 0f;
        public Color EffectiveBeardColor => HasBeardColor ? new Color(BeardColor.r, BeardColor.g, BeardColor.b, 1f) : HairColor;
        public int Model; // 0 male, 1 female
        public float Scale;

        public CompanionAppearance Clone() => (CompanionAppearance)MemberwiseClone();
    }

    // ── The companion barber (0.2.357) ──
    // Server-authoritative: the client asks with Fires_CompanionAppearance; the server checks the sender owns the companion
    // and the values are barber values, charges the fee through the requesting client (its own inventory, vanilla
    // CountItems/RemoveItem), and only on a paid ack writes the companion_* keys the loadout restores from. Every peer then
    // re-applies the look (Applied broadcast now, CompanionInventory's appearance hash as the fallback).
    public static class CompanionAppearanceService
    {
        public const string RequestRpc = "Fires_CompanionAppearance";
        public const string ChargeRpc = "Fires_CompanionAppearanceCharge";
        public const string ChargeAckRpc = "Fires_CompanionAppearanceChargeAck";
        public const string ResultRpc = "Fires_CompanionAppearanceResult";
        public const string AppliedRpc = "Fires_CompanionAppearanceApplied";

        private const string LogPrefix = "[CompanionBarber]";
        private const float RequestTimeoutSeconds = 10f;
        private const float ServerChargeTimeoutSeconds = 30f;
        private const string CoinsToken = "$item_coins";

        private static ConfigEntry<int> s_cost;
        private static ConfigEntry<float> s_minScale;
        private static ConfigEntry<float> s_maxScale;

        public static int Cost => s_cost != null ? Mathf.Clamp(s_cost.Value, 0, 1000) : 75;
        public static float MinScale => s_minScale != null ? Mathf.Min(s_minScale.Value, MaxScaleRaw) : 0.50f;
        public static float MaxScale => MaxScaleRaw;
        private static float MaxScaleRaw => s_maxScale != null ? s_maxScale.Value : 1.30f;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_cost = config.Bind("Barber", "Appearance change cost", 75,
                new ConfigDescription("SERVER. Coins a companion's look change costs at the barber, taken from the asking player's inventory. 0 = free.",
                    new AcceptableValueRange<int>(0, 1000)));
            // 0.2.371: Fire set the range to wild creature size variance, 0.50-1.30. New key names so cfgs holding the old
            // 0.90/1.10 defaults pick the new range up; the old "Scale minimum"/"Scale maximum" lines are left unread.
            s_minScale = config.Bind("Barber", "Size minimum", 0.50f,
                new ConfigDescription("SERVER. Smallest companion size the barber allows.", new AcceptableValueRange<float>(0.5f, 1.3f)));
            s_maxScale = config.Bind("Barber", "Size maximum", 1.30f,
                new ConfigDescription("SERVER. Largest companion size the barber allows.", new AcceptableValueRange<float>(0.5f, 1.3f)));
            configSync?.AddConfigEntry(s_cost);
            configSync?.AddConfigEntry(s_minScale);
            configSync?.AddConfigEntry(s_maxScale);
            FiresCore.Config.OwnerGatedConfig.Gate(FiresCore.Config.OwnerGatedConfig.Owners.COMP, true, s_cost, s_minScale, s_maxScale);   // 0.2.359
        }

        // ── style lists ────────────────────────────────────────────────────────────────────────────────────────────────────────

        public static IReadOnlyList<string> HairStyles => WithNone(CompanionRandomLoadout.GetAvailableHairStyles(), "HairNone");
        public static IReadOnlyList<string> BeardStyles => WithNone(CompanionRandomLoadout.GetAvailableBeardStyles(), "BeardNone");

        private static IReadOnlyList<string> WithNone(List<string> styles, string none)
        {
            var list = styles ?? new List<string>();
            if (!list.Contains(none)) list.Insert(0, none);
            return list;
        }

        private static bool InList(IReadOnlyList<string> list, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return true;
            return false;
        }

        // ── read / preview ─────────────────────────────────────────────────────────────────────────────────────────────────────

        public static CompanionAppearance Read(GameObject companion)
        {
            var a = new CompanionAppearance { Hair = "HairNone", Beard = "BeardNone", HairColor = Color.white, SkinColor = Color.white, Model = 0, Scale = 1f };
            if (companion == null) return a;

            var vis = companion.GetComponent<NpcVisEquipment>();
            a.Scale = companion.transform.localScale.x;
            if (vis != null) a.Model = vis.GetModelIndex() == 1 ? 1 : 0;

            var nview = companion.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            if (zdo == null) return a;

            string hair = zdo.GetString("companion_hair", "");
            string beard = zdo.GetString("companion_beard", "");
            if (!string.IsNullOrEmpty(hair)) a.Hair = hair;
            if (!string.IsNullOrEmpty(beard)) a.Beard = beard;
            Vector3 hc = zdo.GetVec3(ZDOVars.s_hairColor, Vector3.one);
            Vector3 sc = zdo.GetVec3(ZDOVars.s_skinColor, Vector3.one);
            a.HairColor = new Color(hc.x, hc.y, hc.z);
            a.SkinColor = new Color(sc.x, sc.y, sc.z);
            a.Model = zdo.GetBool("companion_isfemale", false) || zdo.GetInt(ZDOVars.s_modelIndex, a.Model) == 1 ? 1 : 0;
            a.Scale = zdo.GetFloat("companion_scale", a.Scale);
            a.BeardColor = TryReadBeardColor(zdo, out Color bc) ? bc : new Color(a.HairColor.r, a.HairColor.g, a.HairColor.b, 0f);
            return a;
        }

        /// <summary>0.2.368: ZDO key of the independent beard colour (Vector3). Absent = the beard follows the hair colour.</summary>
        public const string BeardColorKey = "companion_beardColor";
        private static readonly Vector3 NoBeardColor = new Vector3(-1f, -1f, -1f);

        public static bool TryReadBeardColor(ZDO zdo, out Color color)
        {
            color = new Color(1f, 1f, 1f, 0f);
            if (zdo == null) return false;
            Vector3 v = zdo.GetVec3(BeardColorKey, NoBeardColor);
            if (v.x < 0f) return false;
            color = new Color(v.x, v.y, v.z, 1f);
            return true;
        }

        // 0.2.368: the drawn gear of a companion, as vanilla UpdateEquipmentVisuals reads it: the ZDO's keys when there is a ZDO
        // (a non-owner's fields stay 0), else the fields. { item field, variant field or null, ZDO key, ZDO variant key or null }
        private static readonly string[][] GearSlots =
        {
            new[] { "m_rightItem", null, "RightItem", null },
            new[] { "m_leftItem", "m_leftItemVariant", "LeftItem", "LeftItemVariant" },
            new[] { "m_chestItem", null, "ChestItem", null },
            new[] { "m_legItem", null, "LegItem", null },
            new[] { "m_helmetItem", null, "HelmetItem", null },
            new[] { "m_shoulderItem", "m_shoulderItemVariant", "ShoulderItem", "ShoulderItemVariant" },
            new[] { "m_utilityItem", null, "UtilityItem", null },
            new[] { "m_rightBackItem", null, "RightBackItem", null },
            new[] { "m_leftBackItem", "m_leftBackItemVariant", "LeftBackItem", "LeftBackItemVariant" },
        };

        /// <summary>0.2.368: copies the companion's whole drawn gear (hands, back, armour, utility) onto a ZDO-less preview clone's
        /// VisEquipment fields; the clone's own VisEquipment update then instantiates the attachments (back items included:
        /// Core draws the back cells through these same vanilla back slots). Returns a hash of what was copied.</summary>
        public static int SyncPreviewGear(VisEquipment src, VisEquipment dst)
        {
            if (src == null || dst == null) return 0;
            var nview = src.GetComponent<ZNetView>();
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            int hash = 17;
            foreach (var s in GearSlots)
            {
                string token = zdo != null ? zdo.GetInt(s[2], 0).ToString() : FieldToken(src, s[0]);
                if (token == "0") token = "";
                VisEquipmentCompat.SetItemFieldDirect(dst, s[0], token);
                hash = hash * 31 + token.GetHashCode();
                if (s[1] == null) continue;
                int variant = zdo != null ? zdo.GetInt(s[3], 0) : Traverse.Create(src).Field(s[1]).GetValue<int>();
                Traverse.Create(dst).Field(s[1]).SetValue(variant);
                hash = hash * 31 + variant;
            }
            if (!dst.enabled) dst.enabled = true;   // vanilla instantiates attachments from its own update
            return hash;
        }

        private static string FieldToken(VisEquipment v, string field)
        {
            object o = Traverse.Create(v).Field(field).GetValue();
            return o == null ? "" : o.ToString();
        }

        private static MaterialPropertyBlock s_mpb;
        private static readonly int HairColorId = Shader.PropertyToID("_HairColor");
        private static readonly int SkinColorId = Shader.PropertyToID("_SkinColor");

        /// <summary>0.2.368: colours a bare preview clone (no NpcVisEquipment, not m_isPlayer, so vanilla UpdateColors never runs
        /// for it): hair "_HairColor" = HairColor, beard "_HairColor" = the beard colour (vanilla's beard shader property), body
        /// "_SkinColor" = SkinColor. MaterialPropertyBlocks, no material copies; cheap enough to call every frame, which also
        /// catches hair/beard instances VisEquipment spawns on later frames.</summary>
        public static void TintPreview(VisEquipment vis, CompanionAppearance a)
        {
            if (vis == null || a == null) return;
            try
            {
                if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
                var tr = Traverse.Create(vis);
                Tint(tr.Field("m_hairItemInstance").GetValue<GameObject>(), HairColorId, a.HairColor);
                Tint(tr.Field("m_beardItemInstance").GetValue<GameObject>(), HairColorId, a.EffectiveBeardColor);
                if (vis.m_bodyModel != null) TintRenderer(vis.m_bodyModel, SkinColorId, a.SkinColor);
            }
            catch (Exception ex) { FiresLogger.LogDebug($"{LogPrefix} preview tint failed: {ex.Message}"); }
        }

        private static void Tint(GameObject go, int id, Color c)
        {
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) TintRenderer(r, id, c);
        }

        private static void TintRenderer(Renderer r, int id, Color c)
        {
            r.GetPropertyBlock(s_mpb);
            s_mpb.SetColor(id, c);
            r.SetPropertyBlock(s_mpb);
        }

        /// <summary>Local-only look on any companion-like object (the UI preview clone). Never writes a ZDO.</summary>
        public static void ApplyPreview(GameObject target, CompanionAppearance a) => ApplyPreview(target, a, null);

        /// <summary>0.2.368: as ApplyPreview; <paramref name="source"/> (the live companion) supplies the body meshes when the
        /// bare clone switches model, since vanilla only swaps the body mesh for m_isPlayer bodies.</summary>
        public static void ApplyPreview(GameObject target, CompanionAppearance a, GameObject source)
        {
            if (target == null || a == null) return;
            ApplyVisuals(target, a, scaleColliders: false);
            var nview = target.GetComponent<ZNetView>();
            bool hasZdo = nview != null && nview.IsValid() && nview.GetZDO() != null;
            var vis = target.GetComponent<VisEquipment>();
            if (!hasZdo && vis != null && target.GetComponent<NpcVisEquipment>() == null) SwitchPreviewModel(vis, a.Model, source);
        }

        private static void SwitchPreviewModel(VisEquipment vis, int model, GameObject source)
        {
            var body = vis.m_bodyModel;
            if (body == null) return;
            Mesh pick = null;
            string from = "none";
            var srcVis = source != null ? source.GetComponent<VisEquipment>() : null;
            if (srcVis != null && srcVis.m_bodyModel != null && srcVis.GetModelIndex() == model
                && NpcBodyMeshGuard.IsAssignable(srcVis.m_bodyModel.sharedMesh, body)) { pick = srcVis.m_bodyModel.sharedMesh; from = "companion body"; }
            var playerPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            var tables = new[] { vis.m_models, srcVis != null ? srcVis.m_models : null, playerPrefab != null ? playerPrefab.GetComponent<VisEquipment>()?.m_models : null };
            for (int i = 0; pick == null && i < tables.Length; i++)
            {
                var t = tables[i];
                if (t == null || model < 0 || model >= t.Length || t[model] == null || t[model].m_mesh == null) continue;
                if (NpcBodyMeshGuard.IsAssignable(t[model].m_mesh, body)) { pick = t[model].m_mesh; from = i == 0 ? "clone table" : i == 1 ? "companion table" : "Player table"; }
            }
            if (pick == null) { FiresLogger.LogDebug($"{LogPrefix} preview model {model}: no assignable body mesh"); return; }
            if (ReferenceEquals(body.sharedMesh, pick)) return;
            body.sharedMesh = pick;
            FiresLogger.LogDebug($"{LogPrefix} preview model {model}: body mesh '{pick.name}' ({from})");
        }

        private static readonly string[] PreviewHashFields = { "m_currentHairItemHash", "m_currentBeardItemHash", "m_currentModelIndex" };

        /// <summary>0.2.370: forces a ZDO-less preview VisEquipment to rebuild now (hair, beard, base model, colours).</summary>
        private static void RebuildPreview(VisEquipment vis)
        {
            foreach (var f in PreviewHashFields)
            {
                var field = AccessTools.Field(typeof(VisEquipment), f);
                if (field != null && field.FieldType == typeof(int)) { try { field.SetValue(vis, -1); } catch { } }
            }
            foreach (var m in new[] { "UpdateEquipmentVisuals", "UpdateBaseModel", "UpdateColors" })
            {
                try { AccessTools.Method(typeof(VisEquipment), m, Type.EmptyTypes)?.Invoke(vis, null); }
                catch (Exception ex) { FiresLogger.LogDebug($"{LogPrefix} preview {m} failed: {(ex.InnerException ?? ex).Message}"); }
            }
            if (!vis.enabled) vis.enabled = true;   // its own update keeps late changes coming
        }

        private static void ApplyVisuals(GameObject target, CompanionAppearance a, bool scaleColliders)
        {
            Vector3 hair = new Vector3(a.HairColor.r, a.HairColor.g, a.HairColor.b);
            Vector3 skin = new Vector3(a.SkinColor.r, a.SkinColor.g, a.SkinColor.b);
            string beard = a.Model == 1 ? null : a.Beard;

            var nview = target.GetComponent<ZNetView>();
            bool hasZdo = nview != null && nview.IsValid() && nview.GetZDO() != null;
            var npcVis = target.GetComponent<NpcVisEquipment>();
            if (npcVis != null)
            {
                npcVis.SetModel(a.Model);
                npcVis.SetHairColor(hair);
                npcVis.SetSkinColor(skin);
                string colorStr = FiresCore.Bridge.NpcFashionBridge.ColorToString(a.HairColor);
                FiresCore.Bridge.NpcFashionBridge.ApplyHair(target, a.Hair, colorStr);
                FiresCore.Bridge.NpcFashionBridge.ApplyBeard(target, beard, FiresCore.Bridge.NpcFashionBridge.ColorToString(a.EffectiveBeardColor));
                npcVis.SetBeardColor(a.HasBeardColor ? a.EffectiveBeardColor : (Color?)null);
            }
            else if (!hasZdo)
            {
                // A bare preview body: vanilla VisEquipment keeps the values in its fields when there is no ZDO.
                var vis = target.GetComponent<VisEquipment>();
                if (vis != null)
                {
                    // 0.2.370: vanilla only spawns hair/beard and swaps the base model for m_isPlayer bodies; the companion
                    // prefab's VisEquipment is not one, so the clone stayed bald and male. Mark it a player body, write the
                    // fields directly (ZDO-less, hash-or-name aware), then rebuild synchronously.
                    vis.m_isPlayer = true;
                    vis.m_isArmorStand = false;
                    if ((vis.m_models == null || vis.m_models.Length < 2) && ZNetScene.instance != null)
                    {
                        var pve = ZNetScene.instance.GetPrefab("Player")?.GetComponent<VisEquipment>();
                        if (pve != null && pve.m_models != null && pve.m_models.Length >= 2) vis.m_models = pve.m_models;
                    }
                    vis.SetModel(a.Model);
                    Traverse.Create(vis).Field("m_modelIndex").SetValue(a.Model);
                    VisEquipmentCompat.SetItemFieldDirect(vis, "m_hairItem", a.Hair ?? "");
                    VisEquipmentCompat.SetItemFieldDirect(vis, "m_beardItem", beard ?? "");
                    vis.SetHairColor(hair);
                    vis.SetSkinColor(skin);
                    RebuildPreview(vis);
                    TintPreview(vis, a);   // hair/beard instances spawned later are tinted by the UI's per-frame TintPreview
                    var tr = Traverse.Create(vis);
                    FiresLogger.LogDebug($"[Barber] preview: model {a.Model} hair {a.Hair} beard {beard ?? "none"} (applied: hair {(tr.Field("m_hairItemInstance").GetValue<GameObject>() != null ? "on" : "off")}, beard {(tr.Field("m_beardItemInstance").GetValue<GameObject>() != null ? "on" : "off")})");
                }
            }

            float scale = Mathf.Approximately(a.Scale, 0f) ? 1f : a.Scale;
            var loadout = scaleColliders ? target.GetComponent<CompanionRandomLoadout>() : null;
            if (loadout != null) loadout.ApplyScale(scale, skipHealthAdjustment: true);
            else target.transform.localScale = new Vector3(scale, scale, scale);
        }

        // ── client: request ────────────────────────────────────────────────────────────────────────────────────────────────────

        private sealed class ClientPending
        {
            public Action<bool, string> OnResult;
            public float Deadline;
            public int Charged;
            public string LocalFailure;
        }

        private static readonly Dictionary<int, ClientPending> s_clientPending = new Dictionary<int, ClientPending>();
        private static int s_nextRequestId = 1;

        public static void RequestChange(GameObject companion, CompanionAppearance a, Action<bool, string> onResult)
        {
            var nview = companion != null ? companion.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || a == null)
            {
                onResult?.Invoke(false, "That companion is not loaded");
                return;
            }
            Register();
            if (ZRoutedRpc.instance == null)
            {
                onResult?.Invoke(false, "No answer from the server");
                return;
            }

            int id = s_nextRequestId++;
            s_clientPending[id] = new ClientPending { OnResult = onResult, Deadline = Time.unscaledTime + RequestTimeoutSeconds };
            TimeoutRunner.Ensure();

            var pkg = new ZPackage();
            pkg.Write(id);
            pkg.Write(nview.GetZDO().m_uid);
            WriteAppearance(pkg, a);
            FiresLogger.LogDebug($"{LogPrefix} request {id}: {nview.GetZDO().m_uid} hair={a.Hair} beard={a.Beard} model={a.Model} scale={a.Scale:F2}");
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpc, pkg);
        }

        private static void Finish(int id, bool ok, string message)
        {
            if (!s_clientPending.TryGetValue(id, out var p)) return;
            s_clientPending.Remove(id);
            if (!ok && p.Charged > 0) Refund(p.Charged);
            if (!ok && !string.IsNullOrEmpty(p.LocalFailure)) message = p.LocalFailure;
            try { p.OnResult?.Invoke(ok, message ?? ""); }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} result callback threw: {ex.Message}"); }
        }

        private static void Refund(int coins)
        {
            Player me = Player.m_localPlayer;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Coins") : null;
            if (me == null || prefab == null) return;
            me.GetInventory().AddItem(prefab, coins);
            FiresLogger.LogDebug($"{LogPrefix} refunded {coins} coin(s): the change was not applied");
        }

        internal static void TickTimeouts()
        {
            if (s_clientPending.Count == 0) return;
            float now = Time.unscaledTime;
            List<int> late = null;
            foreach (var kv in s_clientPending)
                if (now >= kv.Value.Deadline) (late ?? (late = new List<int>())).Add(kv.Key);
            if (late == null) return;
            foreach (int id in late)
            {
                FiresLogger.LogDebug($"{LogPrefix} request {id}: no answer within {RequestTimeoutSeconds:0} s");
                if (s_clientPending.TryGetValue(id, out var p)) p.LocalFailure = null;
                Finish(id, false, "No answer from the server");
            }
        }

        private sealed class TimeoutRunner : MonoBehaviour
        {
            private static TimeoutRunner s_instance;

            internal static void Ensure()
            {
                if (s_instance != null) return;
                var go = new GameObject("FiresCompanionBarberTimeouts");
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_instance = go.AddComponent<TimeoutRunner>();
            }

            private void Update() => TickTimeouts();
        }

        // Client: the server asks this player to pay.
        private static void RPC_Charge(long sender, int cost, int id)
        {
            if (ZRoutedRpc.instance == null) return;
            bool ok = false;
            s_clientPending.TryGetValue(id, out var p);
            Player me = Player.m_localPlayer;
            if (p != null && me != null && cost >= 0)
            {
                Inventory inv = me.GetInventory();
                int have = inv.CountItems(CoinsToken);
                if (have >= cost)
                {
                    inv.RemoveItem(CoinsToken, cost);
                    p.Charged = cost;
                    ok = true;
                }
                else p.LocalFailure = $"You have {have} Coins (need {cost})";
                FiresLogger.LogDebug($"{LogPrefix} charge {id}: {cost} coin(s) asked, {have} held, paid={ok}");
            }
            else FiresLogger.LogDebug($"{LogPrefix} charge {id}: no pending request or no local player; declined");
            if (p != null) p.Deadline = Time.unscaledTime + RequestTimeoutSeconds;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ChargeAckRpc, id, ok);
        }

        private static void RPC_Result(long sender, int id, bool ok, string message)
        {
            FiresLogger.LogDebug($"{LogPrefix} result {id}: ok={ok} '{message}'");
            Finish(id, ok, message);
        }

        // Every peer: the server applied a look; show it now and keep the ZDO owner's copy in step.
        private static void RPC_Applied(long sender, ZPackage pkg)
        {
            ZDOID zid = pkg.ReadZDOID();
            CompanionAppearance a = ReadAppearance(pkg);
            GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zid) : null;
            if (go == null) return;
            var nview = go.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && nview.IsOwner())
            {
                WriteKeys(nview.GetZDO(), a);
                go.GetComponent<CompanionInventory>()?.MarkVaultDirty();
            }
            ApplyVisuals(go, a, scaleColliders: true);
            FiresLogger.LogDebug($"{LogPrefix} applied look on {zid} (hair={a.Hair} beard={a.Beard} model={a.Model} scale={a.Scale:F2})");
        }

        // ── server ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private sealed class ServerPending
        {
            public long Sender;
            public int Id;
            public ZDOID Companion;
            public CompanionAppearance Look;
            public float Expires;
        }

        private static readonly Dictionary<string, ServerPending> s_serverPending = new Dictionary<string, ServerPending>();
        private static string Key(long sender, int id) => sender + ":" + id;

        private static void RPC_Request(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            int id = pkg.ReadInt();
            ZDOID zid = pkg.ReadZDOID();
            CompanionAppearance a = ReadAppearance(pkg);
            FiresLogger.LogDebug($"{LogPrefix} server: request {id} from {sender} for {zid}: hair={a.Hair} beard={a.Beard} model={a.Model} scale={a.Scale:F2}");

            string error = Validate(sender, zid, a);
            if (error != null)
            {
                FiresLogger.LogDebug($"{LogPrefix} server: request {id} refused: {error}");
                SendResult(sender, id, false, error);
                return;
            }

            DropExpired();
            int cost = Cost;
            if (cost <= 0)
            {
                Apply(sender, id, zid, a);
                return;
            }
            s_serverPending[Key(sender, id)] = new ServerPending
            {
                Sender = sender, Id = id, Companion = zid, Look = a, Expires = Time.unscaledTime + ServerChargeTimeoutSeconds
            };
            FiresLogger.LogDebug($"{LogPrefix} server: charging {cost} coin(s) for request {id}");
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ChargeRpc, cost, id);
        }

        private static void RPC_ChargeAck(long sender, int id, bool ok)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string key = Key(sender, id);
            if (!s_serverPending.TryGetValue(key, out var p)) return;
            s_serverPending.Remove(key);
            FiresLogger.LogDebug($"{LogPrefix} server: charge ack {id} from {sender}: ok={ok}");
            if (!ok)
            {
                SendResult(sender, id, false, $"Not enough Coins (need {Cost})");
                return;
            }
            Apply(sender, id, p.Companion, p.Look);
        }

        private static void Apply(long sender, int id, ZDOID zid, CompanionAppearance a)
        {
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(zid) : null;
            if (zdo == null)
            {
                SendResult(sender, id, false, "That companion is not loaded");
                return;
            }
            WriteKeys(zdo, a);
            FiresLogger.LogDebug($"{LogPrefix} server: wrote look for {zid} (request {id})");

            var pkg = new ZPackage();
            pkg.Write(zid);
            WriteAppearance(pkg, a);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, AppliedRpc, pkg);
            SendResult(sender, id, true, "");
        }

        private static string Validate(long sender, ZDOID zid, CompanionAppearance a)
        {
            ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(zid) : null;
            if (zdo == null) return "That companion is not loaded";
            long owner = zdo.GetLong("companion_owner", 0L);
            long asker = PlayerIdOfPeer(sender);
            if (owner == 0L || asker == 0L || owner != asker) return "Only its owner can change this companion";
            if (!InList(HairStyles, a.Hair)) return $"Unknown hair style '{a.Hair}'";
            if (!InList(BeardStyles, a.Beard)) return $"Unknown beard style '{a.Beard}'";
            if (a.Model != 0 && a.Model != 1) return "Unknown body model";
            a.Scale = Mathf.Clamp(float.IsNaN(a.Scale) ? 1f : a.Scale, MinScale, MaxScale);
            a.HairColor = ClampColor(a.HairColor);
            a.SkinColor = ClampColor(a.SkinColor);
            if (a.HasBeardColor) a.BeardColor = ClampColor(a.BeardColor);   // 0.2.368: alpha stays 1 (set)
            return null;
        }

        private static Color ClampColor(Color c) => new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);

        // A remote peer's player id from its character ZDO; the listen host (no peer entry) is the local player.
        private static long PlayerIdOfPeer(long sender)
        {
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer != null)
            {
                if (peer.m_characterID.IsNone()) return 0L;
                ZDO body = ZDOMan.instance.GetZDO(peer.m_characterID);
                return body != null ? body.GetLong(ZDOVars.s_playerID, 0L) : 0L;
            }
            if (!ZNet.instance.IsDedicated() && sender == ZNet.GetUID() && Player.m_localPlayer != null)
                return Player.m_localPlayer.GetPlayerID();
            return 0L;
        }

        private static void DropExpired()
        {
            if (s_serverPending.Count == 0) return;
            float now = Time.unscaledTime;
            List<string> late = null;
            foreach (var kv in s_serverPending)
                if (now >= kv.Value.Expires) (late ?? (late = new List<string>())).Add(kv.Key);
            if (late != null) foreach (string k in late) s_serverPending.Remove(k);
        }

        private static void SendResult(long target, int id, bool ok, string message) =>
            ZRoutedRpc.instance.InvokeRoutedRPC(target, ResultRpc, id, ok, message ?? "");

        private static void WriteKeys(ZDO zdo, CompanionAppearance a)
        {
            zdo.Set("companion_hair", a.Hair ?? "");
            zdo.Set("companion_beard", a.Beard ?? "");
            zdo.Set("companion_isfemale", a.Model == 1);
            zdo.Set(ZDOVars.s_modelIndex, a.Model);
            zdo.Set(ZDOVars.s_hairColor, new Vector3(a.HairColor.r, a.HairColor.g, a.HairColor.b));
            zdo.Set(ZDOVars.s_skinColor, new Vector3(a.SkinColor.r, a.SkinColor.g, a.SkinColor.b));
            zdo.Set("companion_scale", a.Scale);
            if (a.HasBeardColor) zdo.Set(BeardColorKey, new Vector3(a.BeardColor.r, a.BeardColor.g, a.BeardColor.b));
            zdo.Set("companion_isgiant", a.Scale >= CompanionRandomLoadout.GIANT_THRESHOLD);
            zdo.Set("companion_isdwarf", a.Scale <= CompanionRandomLoadout.DWARF_THRESHOLD);
        }

        // ── wire format ────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static void WriteAppearance(ZPackage pkg, CompanionAppearance a)
        {
            pkg.Write(a.Hair ?? "");
            pkg.Write(a.Beard ?? "");
            pkg.Write(new Vector3(a.HairColor.r, a.HairColor.g, a.HairColor.b));
            pkg.Write(new Vector3(a.SkinColor.r, a.SkinColor.g, a.SkinColor.b));
            pkg.Write(a.Model);
            pkg.Write(a.Scale);
            // 0.2.368: appended, so an older reader stops before it. x < 0 = no own beard colour.
            pkg.Write(a.HasBeardColor ? new Vector3(a.BeardColor.r, a.BeardColor.g, a.BeardColor.b) : NoBeardColor);
        }

        private static CompanionAppearance ReadAppearance(ZPackage pkg)
        {
            var a = new CompanionAppearance { Hair = pkg.ReadString(), Beard = pkg.ReadString() };
            Vector3 hc = pkg.ReadVector3();
            Vector3 sc = pkg.ReadVector3();
            a.HairColor = new Color(hc.x, hc.y, hc.z);
            a.SkinColor = new Color(sc.x, sc.y, sc.z);
            a.Model = pkg.ReadInt();
            a.Scale = pkg.ReadSingle();
            a.BeardColor = new Color(a.HairColor.r, a.HairColor.g, a.HairColor.b, 0f);
            try
            {
                if (pkg.GetPos() < pkg.Size())
                {
                    Vector3 bc = pkg.ReadVector3();
                    if (bc.x >= 0f) a.BeardColor = new Color(bc.x, bc.y, bc.z, 1f);
                }
            }
            catch { }
            return a;
        }

        // ── registration ───────────────────────────────────────────────────────────────────────────────────────────────────────

        private static bool s_registered;

        private static void Register()
        {
            if (s_registered || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.Register<ZPackage>(RequestRpc, RPC_Request);
            ZRoutedRpc.instance.Register<int, int>(ChargeRpc, RPC_Charge);
            ZRoutedRpc.instance.Register<int, bool>(ChargeAckRpc, RPC_ChargeAck);
            ZRoutedRpc.instance.Register<int, bool, string>(ResultRpc, RPC_Result);
            ZRoutedRpc.instance.Register<ZPackage>(AppliedRpc, RPC_Applied);
            s_registered = true;
        }

        // Every peer registers the routes on world load (the server needs the request route before any client asks).
        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Register
        {
            private static void Postfix()
            {
                s_registered = false;
                s_clientPending.Clear();
                s_serverPending.Clear();
                try { Register(); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} RPC registration failed: {ex.Message}"); }
            }
        }
    }
}
