using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Studio
{
    /// <summary>
    /// The Studio God character (Fire 10-30: the god clone has its own character on the server, seeded from the real character,
    /// editable in the Studio, working with no client). Stored as a normal vanilla .fch in the SERVER's character folder and read /
    /// written only through vanilla's PlayerProfile (Load / LoadPlayerData / SavePlayerData / Save): no new format.
    /// A hidden "shadow" Player (instantiated with ZNetView.m_forceDisableInit, as vanilla's character preview does) holds the loaded
    /// data, so vanilla's own Player.Load / Save parse it. The clone copies its look, gear (real item copies) and skills from it.
    /// Server only. Add-only API.
    /// </summary>
    public static class GodProfile
    {
        public const string DefaultName = "StudioGod";
        private static PlayerProfile s_profile;
        private static Player s_shadow;
        private static readonly AccessTools.FieldRef<Player, int> s_model = AccessTools.FieldRefAccess<Player, int>("m_modelIndex");
        private static readonly AccessTools.FieldRef<Player, Vector3> s_skin = AccessTools.FieldRefAccess<Player, Vector3>("m_skinColor");
        private static readonly AccessTools.FieldRef<Player, Vector3> s_hair = AccessTools.FieldRefAccess<Player, Vector3>("m_hairColor");
        private static readonly AccessTools.FieldRef<Humanoid, string> s_beardItem = AccessTools.FieldRefAccess<Humanoid, string>("m_beardItem");
        private static readonly AccessTools.FieldRef<Humanoid, string> s_hairItem = AccessTools.FieldRefAccess<Humanoid, string>("m_hairItem");

        public static string Folder => SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);
        public static Player Shadow => s_shadow;
        public static bool Loaded => s_shadow != null && s_profile != null;
        /// <summary>The god character's name, from the profile (the shadow Player has no ZDO, so its GetPlayerName() is "").</summary>
        public static string Name => s_profile != null ? s_profile.GetName() : "";

        /// <summary>Copies a character file from the server's character folder over the god file (the seed). Both are names without .fch.</summary>
        public static bool Seed(string fromName, string godName, out string why)
        {
            why = "";
            try
            {
                string from = Path.Combine(Folder, Safe(fromName) + ".fch"), to = Path.Combine(Folder, Safe(godName ?? DefaultName) + ".fch");
                if (!File.Exists(from)) { why = "no character file " + from; return false; }
                File.Copy(from, to, true);
                Unload();
                why = $"seeded {Path.GetFileName(to)} from {Path.GetFileName(from)}";
                FiresLogger.LogDebug("[GodProfile] " + why);
                return true;
            }
            catch (Exception ex) { why = "seed failed: " + ex.Message; return false; }
        }

        /// <summary>Loads the god .fch into the shadow Player (vanilla PlayerProfile.Load + LoadPlayerData).</summary>
        public static bool Load(string godName, out string why)
        {
            why = "";
            if (ZNetScene.instance == null) { why = "no world loaded"; return false; }
            Unload();
            var profile = new PlayerProfile(Safe(godName ?? DefaultName), FileHelpers.FileSource.Local);
            if (!profile.Load()) { why = "no god character '" + profile.GetFilename() + "' in " + Folder + " (seed it first)"; return false; }
            var prefab = ZNetScene.instance.GetPrefab("Player");
            if (prefab == null) { why = "no Player prefab"; return false; }
            ZNetView.m_forceDisableInit = true;
            GameObject go;
            try { go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -5000f, 0f), Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }
            go.name = "StudioGodShadow";
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            var body = go.GetComponent<Rigidbody>(); if (body != null) body.isKinematic = true;
            var p = go.GetComponent<Player>();
            profile.LoadPlayerData(p);
            s_profile = profile; s_shadow = p;
            why = $"loaded '{profile.GetName()}' ({profile.GetFilename()}.fch): {p.GetInventory().GetAllItems().Count} items, {p.GetSkills().GetSkillList().Count} skills";
            FiresLogger.LogDebug("[GodProfile] " + why);
            // proof first ([lead] 10-30, the clone looked bald: "hair= beard="): the look exactly as vanilla Player.Load left it -
            // the fields Look() reads, and vanilla's own getters beside them - before anything changes
            Vector3 sk = s_skin(p), hr = s_hair(p);
            FiresLogger.LogDebug($"[GodProfile] loaded look: model {s_model(p)} (GetPlayerModel {p.GetPlayerModel()}), hair '{s_hairItem(p)}' (GetHair '{p.GetHair()}'), "
                + $"beard '{s_beardItem(p)}' (GetBeard '{p.GetBeard()}'), skin {sk}, hair colour {hr}");
            return true;
        }

        /// <summary>Writes the shadow back to the god .fch (vanilla SavePlayerData + Save).</summary>
        public static bool Save(out string why)
        {
            if (!Loaded) { why = "not loaded"; return false; }
            s_profile.SavePlayerData(s_shadow);
            bool ok = s_profile.Save();
            why = ok ? "saved " + s_profile.GetFilename() + ".fch" : "PlayerProfile.Save failed";
            FiresLogger.LogDebug("[GodProfile] " + why);
            return ok;
        }

        public static void Unload()
        {
            if (s_shadow != null) UnityEngine.Object.Destroy(s_shadow.gameObject);
            s_shadow = null; s_profile = null;
        }

        /// <summary>The god character's look, in the companion barber's own type.</summary>
        public static CompanionAppearance Look()
        {
            if (!Loaded) return null;
            Vector3 skin = s_skin(s_shadow), hair = s_hair(s_shadow);
            return new CompanionAppearance
            {
                Model = s_model(s_shadow), SkinColor = new Color(skin.x, skin.y, skin.z, 1f), HairColor = new Color(hair.x, hair.y, hair.z, 1f),
                Hair = s_hairItem(s_shadow) ?? "", Beard = s_beardItem(s_shadow) ?? "", Scale = 1f,
            };
        }

        /// <summary>Changes the god character's own look (Fire 10-30: model, hair, beard, colours are editable; never the real
        /// player's). Written into the shadow's vanilla fields, saved to the god .fch, and the clone re-dressed.</summary>
        public static bool SetLook(CompanionAppearance a, out string why)
        {
            if (!Loaded || a == null) { why = "not loaded"; return false; }
            s_model(s_shadow) = a.Model == 1 ? 1 : 0;
            s_skin(s_shadow) = new Vector3(a.SkinColor.r, a.SkinColor.g, a.SkinColor.b);
            s_hair(s_shadow) = new Vector3(a.HairColor.r, a.HairColor.g, a.HairColor.b);
            s_hairItem(s_shadow) = a.Hair ?? ""; s_beardItem(s_shadow) = a.Beard ?? "";
            if (!Save(out why)) return false;
            if (StudioClone.s_clone != null) StudioClone.Resync();
            why = "look saved";
            return true;
        }

        /// <summary>{"ok","name","file","items":[{name,stack,quality,equipped}],"skills":{SkillType:level}}</summary>
        public static string Describe()
        {
            if (!Loaded) return "{\"ok\":false,\"refused\":\"not loaded\"}";
            var sb = new StringBuilder(4096);
            sb.Append("{\"ok\":true,\"name\":").Append(Q(s_profile.GetName())).Append(",\"file\":").Append(Q(s_profile.GetFilename())).Append(",\"items\":[");
            bool first = true;
            foreach (var it in s_shadow.GetInventory().GetAllItems())
            {
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"name\":").Append(Q(it.m_dropPrefab != null ? it.m_dropPrefab.name : it.m_shared.m_name)).Append(",\"stack\":").Append(it.m_stack)
                  .Append(",\"quality\":").Append(it.m_quality).Append(",\"equipped\":").Append(it.m_equipped ? "true" : "false")
                  .Append(",\"x\":").Append(it.m_gridPos.x).Append(",\"y\":").Append(it.m_gridPos.y).Append('}');
            }
            sb.Append("],\"skills\":{");
            first = true;
            foreach (var s in s_shadow.GetSkills().GetSkillList())
            { if (!first) sb.Append(','); first = false; sb.Append(Q(s.m_info.m_skill.ToString())).Append(':').Append(s.m_level.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); }
            return sb.Append("}}").ToString();
        }

        /// <summary>Copies of every item the god character carries (the clone fights with these; never saved as the clone's own).</summary>
        public static List<ItemDrop.ItemData> ItemCopies(out List<ItemDrop.ItemData> equipped)
        {
            var all = new List<ItemDrop.ItemData>(); equipped = new List<ItemDrop.ItemData>();
            if (!Loaded) return all;
            foreach (var it in s_shadow.GetInventory().GetAllItems())
            {
                var copy = it.Clone();
                copy.m_equipped = false;
                all.Add(copy);
                if (it.m_equipped) equipped.Add(copy);
            }
            return all;
        }

        public static Dictionary<Skills.SkillType, float> SkillLevels()
        {
            var d = new Dictionary<Skills.SkillType, float>();
            if (Loaded) foreach (var s in s_shadow.GetSkills().GetSkillList()) d[s.m_info.m_skill] = s.m_level;
            return d;
        }

        // ---- one-time setup from the connected real player (Fire 10-30: "connect with the player at least once") ----------------
        // The client saves its own character (vanilla Game.SavePlayerProfile: inventory, skills, customData incl. class / RPGM data,
        // the explored map) and sends the .fch file's bytes; the server writes them as the god .fch. Admin senders only, size-capped.
        public const string SeedRpc = "Fires_StudioGodSeed", SeedRequestRpc = "Fires_StudioGodSeedRequest";
        private const int MaxSeedBytes = 64 * 1024 * 1024;
        private static bool s_rpcs;
        // Server: the ONE upload it asked for - from which peer, into which god file, until when. The client never names the file.
        private static long s_seedPeer; private static string s_seedName; private static float s_seedUntil;

        public static void RegisterRpcs()
        {
            if (s_rpcs || ZRoutedRpc.instance == null) return;
            s_rpcs = true;
            ZRoutedRpc.instance.Register<ZPackage>(SeedRpc, RPC_Seed);
            ZRoutedRpc.instance.Register<string>(SeedRequestRpc, RPC_SeedRequest);
            ZRoutedRpc.instance.Register<ZPackage>(ApplyRpc, RPC_Apply);
            ZRoutedRpc.instance.Register(RestoreRpc, RPC_Restore);
        }

        /// <summary>Server: asks the peer whose character ZDO is <paramref name="peerUid"/>'s to upload its character as the god .fch.</summary>
        public static bool RequestSeedFrom(long peerUid, string godName)
        {
            RegisterRpcs();
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return false;
            string god = Safe(godName ?? DefaultName);
            var peer = ZNet.instance.GetPeer(peerUid);
            if (peer == null || !ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
            { FiresLogger.LogWarning($"[GodProfile] setup refused: peer {peerUid} is not a connected admin"); return false; }
            // 1) The server's own character store (the [Characters] server-side profiles: <Platform>_<id>_<name>.fch in this same
            //    characters folder) already holds the connected player's character: copy it. No upload.
            string stored = FindStoredCharacter(peer);
            if (stored != null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = Seed(stored, god, out string why) && Load(god, out why);
                FiresLogger.LogDebug($"[GodProfile] setup copy + load took {sw.Elapsed.TotalMilliseconds:0.0} ms");
                if (ok) { FiresLogger.LogInfo($"[GodProfile] god character '{god}' set up from the server-stored character {stored}"); if (StudioClone.s_clone != null) StudioClone.Resync(); }
                else FiresLogger.LogWarning($"[GodProfile] setup from the server-stored character {stored} failed: {why}");
                return ok;
            }
            // 2) Fallback: ask the client to upload its local .fch.
            FiresLogger.LogInfo($"[GodProfile] no server-stored character for peer {peerUid} ({peer.m_playerName}); asking the client to upload");
            s_seedPeer = peerUid; s_seedName = god; s_seedUntil = Time.unscaledTime + 120f;
            ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, SeedRequestRpc, s_seedName);
            return true;
        }

        /// <summary>The newest "&lt;anything&gt;_&lt;steam id&gt;_&lt;character name&gt;.fch" in the server's characters folder for that peer, as a
        /// name without .fch; null when none.</summary>
        private static string FindStoredCharacter(ZNetPeer peer)
        {
            try
            {
                string id = peer.m_socket.GetHostName(), name = (peer.m_playerName ?? "").ToLowerInvariant();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !Directory.Exists(Folder)) return null;
                string want = "_" + id + "_" + name + ".fch";
                string best = null; DateTime at = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(Folder, "*.fch"))
                {
                    if (!f.EndsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > at) { at = t; best = f; }
                }
                return best != null ? Path.GetFileNameWithoutExtension(best) : null;
            }
            catch (Exception ex) { FiresLogger.LogWarning("[GodProfile] server character lookup failed: " + ex.Message); return null; }
        }

        /// <summary>Client: saves the local character the vanilla way and uploads the .fch bytes to the server.</summary>
        public static bool UploadLocalCharacter(string godName, out string why)
        {
            RegisterRpcs();
            why = "";
            if (Player.m_localPlayer == null || Game.instance == null) { why = "no local player"; return false; }
            try
            {
                Game.instance.SavePlayerProfile(true);
                var prof = Game.instance.GetPlayerProfile();
                var source = AccessTools.FieldRefAccess<PlayerProfile, FileHelpers.FileSource>("m_fileSource")(prof);
                string path = SaveSystem.GetCharacterPath(source, prof.GetFilename());
                byte[] bytes;
                // vanilla's own reader (PlayerProfile.LoadPlayerDataFromDisk), so a Cloud profile reads too
                var reader = new FileReader(path, source);
                try { using (var ms = new MemoryStream()) { reader.m_binary.BaseStream.CopyTo(ms); bytes = ms.ToArray(); } }
                finally { reader.Dispose(); }
                if (bytes.Length > MaxSeedBytes) { why = "character file too big"; return false; }
                var pkg = new ZPackage();
                pkg.Write(Safe(godName ?? DefaultName)); pkg.Write(bytes);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), SeedRpc, pkg);
                why = $"uploaded {bytes.Length / 1024} KB from {System.IO.Path.GetFileName(path)} ({source})";
                FiresLogger.LogInfo("[GodProfile] " + why);
                return true;
            }
            catch (Exception ex) { why = "upload failed: " + ex.Message; FiresLogger.LogWarning("[GodProfile] " + why); return false; }
        }

        private static void RPC_SeedRequest(long sender, string godName)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID()) return;   // only the server asks
            UploadLocalCharacter(godName, out string why);   // logs its own outcome (Info / Warning)
        }

        private static void RPC_Seed(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var peer = ZNet.instance.GetPeer(sender);
            string host = peer != null ? peer.m_socket.GetHostName() : "";
            if (peer != null && !ZNet.instance.IsAdmin(host)) { FiresLogger.LogWarning($"[GodProfile] seed refused: peer {sender} is not an admin"); return; }
            try
            {
                pkg.ReadString();   // the client's name is ignored: the server writes only the god file it asked for
                if (sender != s_seedPeer || s_seedName == null || Time.unscaledTime > s_seedUntil)
                { FiresLogger.LogWarning($"[GodProfile] seed refused: no pending setup request for peer {sender}"); return; }
                string name = s_seedName; s_seedPeer = 0L; s_seedName = null;
                byte[] bytes = pkg.ReadByteArray();
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxSeedBytes) { FiresLogger.LogWarning("[GodProfile] seed refused: bad size"); return; }
                Directory.CreateDirectory(Folder);
                string to = Path.Combine(Folder, name + ".fch");
                File.WriteAllBytes(to + ".tmp", bytes);
                if (File.Exists(to)) File.Replace(to + ".tmp", to, to + ".bak"); else File.Move(to + ".tmp", to);
                Unload();
                if (!Load(name, out string why)) { FiresLogger.LogWarning("[GodProfile] seeded file does not load: " + why); File.Delete(to); if (File.Exists(to + ".bak")) File.Move(to + ".bak", to); return; }
                FiresLogger.LogInfo($"[GodProfile] god character '{name}' set up from peer {sender} ({bytes.Length / 1024} KB)");
                if (StudioClone.s_clone != null) StudioClone.Resync();
            }
            catch (Exception ex) { FiresLogger.LogWarning("[GodProfile] seed failed: " + ex.Message); }
        }

        // ---- "Apply God -> my real player" (Fire 10-30: manual only, never automatic) -----------------------------------------
        // The server sends the god .fch's player data to the connected admin's client. The client backs up its own .fch first (a copy
        // named <file>_backup_god-<time>.fch, next to vanilla's own backups), then loads the god data into the LIVE Player with vanilla's
        // Player.Load (inventory, skills, customData incl. class data, look) keeping its own player id and name, and saves with
        // vanilla's Game.SavePlayerProfile. The client's own world map data is kept.
        public const string ApplyRpc = "Fires_StudioGodApply";
        private static readonly AccessTools.FieldRef<PlayerProfile, byte[]> s_playerData = AccessTools.FieldRefAccess<PlayerProfile, byte[]>("m_playerData");

        /// <summary>Server: pushes the god character onto that admin peer's live character. False = not allowed / not loaded.</summary>
        public static bool ApplyToPlayer(long peerUid, out string why)
        {
            RegisterRpcs();
            why = "";
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { why = "server only"; return false; }
            if (!Loaded) { why = "god character not loaded"; return false; }
            var peer = ZNet.instance.GetPeer(peerUid);
            if (peer == null || !ZNet.instance.IsAdmin(peer.m_socket.GetHostName())) { why = "that peer is not a connected admin"; return false; }
            s_profile.SavePlayerData(s_shadow);
            byte[] data = s_playerData(s_profile);
            if (data == null || data.Length == 0 || data.Length > MaxSeedBytes) { why = "no god data"; return false; }
            ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, ApplyRpc, new ZPackage(data));
            why = $"sent {data.Length / 1024} KB of god data to peer {peerUid}";
            FiresLogger.LogInfo("[GodProfile] " + why);
            return true;
        }

        // ---- 0.2.1033 ([wishbone], Fire 10-30 via [lead]: "reload our cloned stats onto our real player or re-clone our player into a new
        // God client", manual only, both ways, the real player online OR offline, the undo one click): the three Studio entries.
        // Online = that admin's own client does it (the live Player, its own .fch backed up first). Offline = the player's character in
        // the server's own store (FiresVAngarde's CharacterStore, by reflection - the same store the re-clone reads), through ITS save path
        // (SaveProfileBytes: waits out a pending disk write, keeps its rotating backups), after a "_backup_god-<time>.bytes" copy of the
        // stored bytes. Each returns JSON {"ok","why","online","backup"}.
        public const string RestoreRpc = "Fires_StudioGodRestore";
        private static System.Reflection.MethodInfo s_storeLoad, s_storeSave, s_storeFile, s_loadBytes, s_diskBytes;
        private static bool s_storeLooked;

        private static void LookStore()
        {
            if (s_storeLooked) return;
            s_storeLooked = true;
            s_storeLoad = FiresCore.Compat.OptionalTypes.Method("VerdantsAscent.Modules.Characters.CharacterStore:LoadProfileBytes");
            s_storeSave = FiresCore.Compat.OptionalTypes.Method("VerdantsAscent.Modules.Characters.CharacterStore:SaveProfileBytes");
            s_storeFile = FiresCore.Compat.OptionalTypes.Method("VerdantsAscent.Modules.Characters.CharacterStore:FileName");
            s_loadBytes = FiresCore.Compat.OptionalTypes.Method("VerdantsAscent.Modules.Characters.PlayerProfileBytes:LoadFromBytes");
            s_diskBytes = AccessTools.Method(typeof(PlayerProfile), "LoadPlayerDataFromDisk");
            FiresLogger.LogDebug($"[GodProfile] character store (FiresVAngarde): {(s_storeLoad != null && s_storeSave != null && s_loadBytes != null ? "found" : "NOT loaded - offline apply / restore unavailable")}");
        }

        private static string Result(bool ok, string why, bool online, string backup = "") =>
            "{\"ok\":" + (ok ? "true" : "false") + ",\"why\":" + Q(why) + ",\"online\":" + (online ? "true" : "false") + ",\"backup\":" + Q(backup) + "}";

        /// <summary>The connected admin peer for a Steam id + character name, or null (offline).</summary>
        private static ZNetPeer OnlinePeer(string steamId, string name)
        {
            foreach (var p in ZNet.instance.GetPeers())
                if (p != null && p.m_socket != null && p.m_socket.GetHostName() == steamId && string.Equals(p.m_playerName, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Studio: the god character onto the real one (steam id + character name). Online -> that admin's client; offline -> the server store.</summary>
        public static string ApplyTo(string requesterSteamId, string name)
        {
            string steamId = requesterSteamId;   // 0.2.1033 ([lead] security): ONLY the requesting admin's own characters - FGC passes the Studio session's paired admin id, never a client-supplied one
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Result(false, "server only", false);
            if (!Loaded) return Result(false, "god character not loaded (set it up first)", false);
            var peer = OnlinePeer(steamId, name);
            if (peer != null)
            {
                bool ok = ApplyToPlayer(peer.m_uid, out string why);
                return Result(ok, ok ? why + "; the client backs up its own character first, then applies (see its log)" : why, true);
            }
            if (!ZNet.instance.IsAdmin(steamId)) { FiresLogger.LogWarning($"[GodProfile] refused: requester {steamId} is not an admin (target {steamId}/{name})"); return Result(false, steamId + " is not an admin", false); }
            LookStore();
            if (s_storeLoad == null || s_storeSave == null || s_loadBytes == null || s_diskBytes == null || s_storeFile == null) return Result(false, "FiresVAngarde's character store is not loaded on this server", false);
            try
            {
                byte[] stored = (byte[])s_storeLoad.Invoke(null, new object[] { steamId, name });
                if (stored == null || stored.Length == 0) return Result(false, $"no stored character for {steamId}/{name}", false);
                string backup = Path.Combine(Folder, (string)s_storeFile.Invoke(null, new object[] { steamId, name }) + "_backup_god-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bytes");
                File.WriteAllBytes(backup, stored);
                if (new FileInfo(backup).Length != stored.Length) return Result(false, "the backup did not write whole: " + backup, false);
                // the stored profile, its player data replaced by the god's (its own id / name / map kept), back to wire bytes through a temp file
                s_profile.SavePlayerData(s_shadow);
                byte[] god = s_playerData(s_profile);
                string temp = "studio_god_apply_tmp";
                var prof = new PlayerProfile(temp, FileHelpers.FileSource.Local);
                if (!(bool)s_loadBytes.Invoke(null, new object[] { prof, stored })) return Result(false, "the stored character did not read", false, backup);
                s_playerData(prof) = god;
                prof.SavePlayerToDisk();
                byte[] wire = (s_diskBytes.Invoke(new PlayerProfile(temp, FileHelpers.FileSource.Local), null) as ZPackage)?.GetArray();
                try { File.Delete(SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, temp)); } catch { }
                if (wire == null || wire.Length == 0) return Result(false, "the new character bytes did not build", false, backup);
                bool saved = (bool)s_storeSave.Invoke(null, new object[] { steamId, name, wire });
                string w = saved ? $"god character written into {steamId}/{name}'s stored character (offline: served at the next login)" : "the store refused the save";
                FiresLogger.LogInfo("[GodProfile] " + w + "; backup " + Path.GetFileName(backup));
                return Result(saved, w, false, Path.GetFileName(backup));
            }
            catch (Exception ex) { var e = ex.InnerException ?? ex; FiresLogger.LogWarning("[GodProfile] offline apply failed: " + e.Message); return Result(false, "offline apply failed: " + e.Message, false); }
        }

        /// <summary>Studio: one-click undo - the newest god backup put back. Online -> that admin's client loads its newest _backup_god-*.fch;
        /// offline -> the newest _backup_god-*.bytes goes back into the server store.</summary>
        public static string RestoreFor(string requesterSteamId, string name)
        {
            string steamId = requesterSteamId;   // 0.2.1033 ([lead] security): ONLY the requesting admin's own characters - FGC passes the Studio session's paired admin id, never a client-supplied one
            RegisterRpcs();
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Result(false, "server only", false);
            var peer = OnlinePeer(steamId, name);
            if (peer != null)
            {
                if (!ZNet.instance.IsAdmin(steamId)) return Result(false, "that peer is not a connected admin", true);
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, RestoreRpc);
                FiresLogger.LogInfo($"[GodProfile] restore asked of peer {peer.m_uid} ({name})");
                return Result(true, "asked the client to load its newest god backup (see its log)", true);
            }
            if (!ZNet.instance.IsAdmin(steamId)) { FiresLogger.LogWarning($"[GodProfile] refused: requester {steamId} is not an admin (target {steamId}/{name})"); return Result(false, steamId + " is not an admin", false); }
            LookStore();
            if (s_storeSave == null || s_storeFile == null) return Result(false, "FiresVAngarde's character store is not loaded on this server", false);
            try
            {
                string stem = (string)s_storeFile.Invoke(null, new object[] { steamId, name });
                string best = null; DateTime at = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(Folder, stem + "_backup_god-*.bytes")) { var t = File.GetLastWriteTimeUtc(f); if (t > at) { at = t; best = f; } }
                if (best == null) return Result(false, "no god backup for " + steamId + "/" + name, false);
                bool saved = (bool)s_storeSave.Invoke(null, new object[] { steamId, name, File.ReadAllBytes(best) });
                string w = saved ? "restored " + Path.GetFileName(best) + " into the stored character (served at the next login)" : "the store refused the restore";
                FiresLogger.LogInfo("[GodProfile] " + w);
                return Result(saved, w, false, Path.GetFileName(best));
            }
            catch (Exception ex) { var e = ex.InnerException ?? ex; FiresLogger.LogWarning("[GodProfile] offline restore failed: " + e.Message); return Result(false, "offline restore failed: " + e.Message, false); }
        }

        /// <summary>Studio: re-clone - the god character set up again from that admin's character (RequestSeedFrom: the server store first, else the client's upload).</summary>
        public static string RecloneFrom(string requesterSteamId, string name)
        {
            string steamId = requesterSteamId;   // 0.2.1033 ([lead] security): ONLY the requesting admin's own characters - FGC passes the Studio session's paired admin id, never a client-supplied one
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return Result(false, "server only", false);
            var peer = OnlinePeer(steamId, name);
            if (peer == null) return Result(false, $"{name} must be online for a re-clone (the server store is read through their connection's id)", false);
            bool ok = RequestSeedFrom(peer.m_uid, DefaultName);
            return Result(ok, ok ? "re-clone started (see [GodProfile] lines)" : "refused (not a connected admin?)", true);
        }

        private static void RPC_Restore(long sender)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID()) return;   // only from the server
            var p = Player.m_localPlayer;
            if (p == null || Game.instance == null) return;
            try
            {
                var profile = Game.instance.GetPlayerProfile();
                string dir = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local);
                string best = null; DateTime at = DateTime.MinValue;
                foreach (var f in Directory.GetFiles(dir, profile.GetFilename() + "_backup_god-*.fch")) { var t = File.GetLastWriteTimeUtc(f); if (t > at) { at = t; best = f; } }
                if (best == null) { FiresLogger.LogInfo("[GodProfile] restore: no god backup for " + profile.GetFilename()); p.Message(MessageHud.MessageType.Center, "No God backup to restore"); return; }
                var backup = new PlayerProfile(Path.GetFileNameWithoutExtension(best), FileHelpers.FileSource.Local);
                if (!backup.Load()) { FiresLogger.LogWarning("[GodProfile] restore: " + Path.GetFileName(best) + " did not load"); return; }
                byte[] data = s_playerData(backup);
                if (data == null || data.Length == 0) { FiresLogger.LogWarning("[GodProfile] restore: " + Path.GetFileName(best) + " has no player data"); return; }
                long id = p.GetPlayerID(); string name = p.GetPlayerName();
                p.Load(new ZPackage(data));
                p.SetPlayerID(id, name);
                Game.instance.SavePlayerProfile(false);
                FiresLogger.LogInfo($"[GodProfile] restored {name} from {Path.GetFileName(best)}");
                p.Message(MessageHud.MessageType.Center, "Restored from " + Path.GetFileName(best));
            }
            catch (Exception ex) { FiresLogger.LogWarning("[GodProfile] restore failed: " + ex.Message); }
        }

        private static void RPC_Apply(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID()) return;   // only from the server
            var p = Player.m_localPlayer;
            if (p == null || Game.instance == null) return;
            try
            {
                var profile = Game.instance.GetPlayerProfile();
                Game.instance.SavePlayerProfile(false);
                // 0.2.1033 ([wishbone]; the same Cloud-profile bug as the seed 10-30: File.Copy(GetPath()) throws for a Cloud character, so
                // the apply stopped before the backup): the real character's bytes are read through vanilla's FileReader with its own file
                // source (Local or Cloud) and the backup is written as a LOCAL file next to the local characters - no backup, no apply.
                var source = AccessTools.FieldRefAccess<PlayerProfile, FileHelpers.FileSource>("m_fileSource")(profile);
                byte[] old;
                var reader = new FileReader(SaveSystem.GetCharacterPath(source, profile.GetFilename()), source);
                try { using (var ms = new MemoryStream()) { reader.m_binary.BaseStream.CopyTo(ms); old = ms.ToArray(); } }
                finally { reader.Dispose(); }
                if (old == null || old.Length == 0) { FiresLogger.LogWarning("[GodProfile] apply refused: the real character could not be read for its backup"); return; }
                string backup = SaveSystem.GetCharacterPath(FileHelpers.FileSource.Local, profile.GetFilename() + "_backup_god-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.WriteAllBytes(backup, old);
                if (!File.Exists(backup) || new FileInfo(backup).Length != old.Length) { FiresLogger.LogWarning("[GodProfile] apply refused: the backup did not write back whole (" + backup + ")"); return; }
                long id = p.GetPlayerID(); string name = p.GetPlayerName();
                p.Load(new ZPackage(pkg.GetArray()));
                p.SetPlayerID(id, name);
                Game.instance.SavePlayerProfile(false);
                FiresLogger.LogInfo($"[GodProfile] god character applied to {name}; the previous character is backed up as {Path.GetFileName(backup)}");
                p.Message(MessageHud.MessageType.Center, "God character applied (backup: " + Path.GetFileName(backup) + ")");
            }
            catch (Exception ex) { FiresLogger.LogWarning("[GodProfile] applying the god character failed: " + ex.Message); }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_RegisterGodRpcs
        {
            private static void Postfix() { s_rpcs = false; RegisterRpcs(); }
        }

        private static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name ?? "") if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
            return sb.Length > 0 ? sb.ToString() : DefaultName;
        }

        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
