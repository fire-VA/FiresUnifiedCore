using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    // The game's own shader for a name is the instance its own objects draw with. A loaded copy that merely "runs" is
    // not proof: a ripped shader with no programs keeps its Fallback, reports isSupported and draws as that fallback
    // (the rip's Custom/Vegetation as Legacy Shaders/Diffuse, so FAT's skyland vines lost their cutout). So the
    // prefab and clutter lists are read before any mod injects into them, and the shaders on those objects are the
    // reference. Core-only: VanillaShaderRebind is source-linked into standalone mods, which never set its lookup.
    internal static class VanillaShaderSources
    {
        private static List<GameObject> s_vanillaPrefabs;
        private static List<GameObject> s_vanillaClutter;
        private static Dictionary<string, Shader> s_live;
        // 0.2.273: every supported instance per name (the game's asset bundles each carry their own copy), first one first.
        private static Dictionary<string, List<Shader>> s_allLive;
        private static readonly List<Material> s_slots = new List<Material>();
        private static readonly IReadOnlyList<Shader> s_none = new Shader[0];

        private static bool IsHeadless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static void Install()
        {
            VanillaShaderRebind.LiveShaderLookup = Live;
            VanillaShaderRebind.GameShadersLookup = AllLive;
        }

        private static Shader Live(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || !EnsureIndex()) return null;
            return s_live.TryGetValue(shaderName, out Shader shader) ? shader : null;
        }

        private static IReadOnlyList<Shader> AllLive(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || !EnsureIndex()) return s_none;
            return s_allLive.TryGetValue(shaderName, out List<Shader> shaders) ? shaders : s_none;
        }

        private static bool EnsureIndex()
        {
            if (s_live != null) return true;
            if (s_vanillaPrefabs == null) return false;
            BuildIndex();
            return true;
        }

        private static void BuildIndex()
        {
            var live = new Dictionary<string, Shader>(StringComparer.Ordinal);
            var all = new Dictionary<string, List<Shader>>(StringComparer.Ordinal);
            foreach (var prefab in s_vanillaPrefabs) AddShaders(prefab, live, all);
            if (s_vanillaClutter != null)
                foreach (var prefab in s_vanillaClutter) AddShaders(prefab, live, all);
            s_allLive = all;
            s_live = live;
        }

        private static void AddShaders(GameObject prefab, Dictionary<string, Shader> live, Dictionary<string, List<Shader>> all)
        {
            if (prefab == null) return;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                renderer.GetSharedMaterials(s_slots);
                foreach (var material in s_slots) Add(material, live, all);
            }
            foreach (var instanced in prefab.GetComponentsInChildren<InstanceRenderer>(true))
                Add(instanced.m_material, live, all);
        }

        private static void Add(Material material, Dictionary<string, Shader> live, Dictionary<string, List<Shader>> all)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null || !shader.isSupported) return;
            if (!all.TryGetValue(shader.name, out List<Shader> copies)) all[shader.name] = copies = new List<Shader>();
            if (!copies.Contains(shader)) copies.Add(shader);
            if (!live.ContainsKey(shader.name)) live[shader.name] = shader;
        }

        // ── 0.2.270 ([lead], the WildWeapons repro of "your core mod breaks all the textures in my mod"): `shaderrebind_test [item ...]`,
        // an F5 test of what Core's shader sweep did to other mods' items. Per item: each material's shader (name #instance), the game's
        // instance of that name, whether the sweep moved it at load and from what, and what the 0.2.268 rule (move every same-named copy)
        // and the 0.2.269 rule (move only the game's own shader) do with it. Then one item held and one dropped, each with its live
        // renderers listed and a screenshot in BepInEx\FiresTests. Without names it takes up to three items that aren't the game's and draw
        // with a game shader's name, those a rule tells apart first. FAIL when a listed material draws magenta (no shader / InternalErrorShader).
        private const string RebindTestCommand = "shaderrebind_test", RebindTestTag = "[ShaderRebindTest]";
        private const int RebindTestMaxItems = 3, RebindTestMaxLines = 12;
        private const float RebindTestSettle = 1.5f;
        private static bool s_rebindTestRunning, s_rebindTestRegistered;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class RebindTest_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_rebindTestRegistered) return;
                s_rebindTestRegistered = true;
                new Terminal.ConsoleCommand(RebindTestCommand,
                    "[F5 test] what Core's shader sweep did to other mods' items: per material its shader, the game's instance of that name, what "
                    + "the sweep moved at load, and the 0.2.268 vs 0.2.269 rule; then one item held and one dropped, with screenshots in "
                    + "BepInEx\\FiresTests. Args: [item prefab ...] (default: up to 3 items that aren't the game's and use a game shader's name).",
                    args =>
                    {
                        if (s_rebindTestRunning) { RebindTestSay($"{RebindTestTag} a run is already going."); return; }
                        if (ZNet.instance == null || Player.m_localPlayer == null || ObjectDB.instance == null) { RebindTestSay($"{RebindTestTag} join a world first."); return; }
                        var names = new List<string>();
                        for (int i = 1; i < args.Length; i++) if (!string.IsNullOrWhiteSpace(args[i])) names.Add(args[i].Trim());
                        // 0.2.276: `shaderrebind_test closeup [item ...]` - close-up screenshots of each item dropped and held.
                        if (names.Count > 0 && string.Equals(names[0], "closeup", StringComparison.OrdinalIgnoreCase))
                        {
                            names.RemoveAt(0);
                            ZNet.instance.StartCoroutine(RunRebindCloseups(names));
                            return;
                        }
                        ZNet.instance.StartCoroutine(RunRebindTest(names));
                    },
                    // 0.2.277 (release check): it spawns the named item, so it is a cheat - devcommands on (an admin's, on a server).
                    // Without this any player could type it and keep the item (walk into the drop, or log out mid-run).
                    isCheat: true);
            }
        }

        private static System.Collections.IEnumerator RunRebindTest(List<string> names)
        {
            s_rebindTestRunning = true;
            float started = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0;
            Player player = Player.m_localPlayer;
            ItemDrop.ItemData held = null;
            GameObject dropped = null;
            try
            {
                List<GameObject> items = PickRebindTestItems(names);
                int steps = Math.Max(1, items.Count) + 2;
                RebindTestSay($"{RebindTestTag} BEGIN {FiresUnifiedCore.PluginName} {FiresUnifiedCore.PluginVersion} world '{(ZNet.World != null ? ZNet.World.m_name : "?")}': {steps} steps");
                if (items.Count == 0)
                {
                    skip = steps;
                    RebindTestSay($"{RebindTestTag} 1/{steps} items SKIP - " + (names.Count > 0
                        ? $"none of [{string.Join(", ", names)}] is a prefab the game knows"
                        : "no item outside the game's own draws with a game shader's name"));
                    yield break;
                }
                int step = 0;
                foreach (var item in items)
                {
                    step++;
                    int magenta = DescribeRebindRenderers($"{step}/{steps} {item.name} (prefab)", item);
                    if (magenta > 0) { fail++; RebindTestSay($"{RebindTestTag} {step}/{steps} {item.name} FAIL - {magenta} material(s) draw magenta"); }
                    else { pass++; RebindTestSay($"{RebindTestTag} {step}/{steps} {item.name} PASS - its materials listed above"); }
                }

                // Held: the first item, equipped from the inventory as a player would.
                step++;
                // 0.2.275: the listed prefabs may include non-items (a tree, a piece): held and dropped use the first and last item.
                var heldPrefab = items.Find(prefab => prefab.GetComponent<ItemDrop>() != null);
                if (heldPrefab != null)
                {
                    held = heldPrefab.GetComponent<ItemDrop>().m_itemData.Clone();
                    held.m_dropPrefab = heldPrefab;
                    held.m_stack = 1;
                }
                bool equipped = held != null && player.GetInventory().AddItem(held) && player.EquipItem(held, true);
                if (!equipped)
                {
                    skip++;
                    RebindTestSay(heldPrefab == null
                        ? $"{RebindTestTag} {step}/{steps} held SKIP - no item among the listed prefabs"
                        : $"{RebindTestTag} {step}/{steps} held SKIP - {heldPrefab.name} couldn't be equipped (inventory full, or not equipable)");
                }
                else
                {
                    yield return new WaitForSeconds(RebindTestSettle);
                    var vis = player.m_visEquipment;
                    GameObject instance = vis != null ? (vis.m_rightItemInstance ?? vis.m_leftItemInstance) : null;
                    if (instance == null)
                    {
                        skip++;
                        RebindTestSay($"{RebindTestTag} {step}/{steps} held SKIP - {heldPrefab.name} equipped but no hand visual was built");
                    }
                    else
                    {
                        int magenta = DescribeRebindRenderers($"{step}/{steps} held {heldPrefab.name}", instance);
                        yield return RebindTestShot("held_" + heldPrefab.name);
                        if (magenta > 0) { fail++; RebindTestSay($"{RebindTestTag} {step}/{steps} held FAIL - {magenta} material(s) draw magenta"); }
                        else { pass++; RebindTestSay($"{RebindTestTag} {step}/{steps} held PASS - {heldPrefab.name} in hand, materials listed above"); }
                    }
                }

                // Dropped: the last item, spawned on the ground 2 m in front.
                step++;
                var dropPrefab = items.FindLast(prefab => prefab.GetComponent<ItemDrop>() != null);
                Vector3 at = player.transform.position + player.transform.forward * 2f + Vector3.up * 0.5f;
                if (dropPrefab != null)
                {
                    dropped = UnityEngine.Object.Instantiate(dropPrefab, at, Quaternion.identity);
                    var droppedItem = dropped.GetComponent<ItemDrop>();
                    if (droppedItem != null) droppedItem.m_autoPickup = false;   // 0.2.277: the test's drop is never picked up
                    yield return new WaitForSeconds(RebindTestSettle);
                }
                if (dropPrefab == null)
                {
                    skip++;
                    RebindTestSay($"{RebindTestTag} {step}/{steps} dropped SKIP - no item among the listed prefabs");
                }
                else if (dropped == null)
                {
                    skip++;
                    RebindTestSay($"{RebindTestTag} {step}/{steps} dropped SKIP - {dropPrefab.name} vanished");
                }
                else
                {
                    int magenta = DescribeRebindRenderers($"{step}/{steps} dropped {dropPrefab.name}", dropped);
                    yield return RebindTestShot("dropped_" + dropPrefab.name);
                    if (magenta > 0) { fail++; RebindTestSay($"{RebindTestTag} {step}/{steps} dropped FAIL - {magenta} material(s) draw magenta"); }
                    else { pass++; RebindTestSay($"{RebindTestTag} {step}/{steps} dropped PASS - {dropPrefab.name} on the ground, materials listed above"); }
                }
            }
            finally
            {
                try
                {
                    if (held != null && player != null)
                    {
                        player.UnequipItem(held, false);
                        player.GetInventory().RemoveItem(held);
                    }
                    if (dropped != null)
                    {
                        var nview = dropped.GetComponent<ZNetView>();
                        if (nview != null && nview.IsValid() && ZNetScene.instance != null) ZNetScene.instance.Destroy(dropped);
                        else UnityEngine.Object.Destroy(dropped);
                    }
                }
                catch (Exception ex) { RebindTestSay($"{RebindTestTag} cleanup: {ex.GetType().Name}: {ex.Message}"); }
                RebindTestSay($"{RebindTestTag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0} s");
                s_rebindTestRunning = false;
            }
        }

        // Named items, else up to three items that aren't the game's (not in the vanilla prefab snapshot) and draw with a game shader's
        // name: first those still on their own copy (a rule tells them apart), then those the sweep moved, one per shader name.
        private static List<GameObject> PickRebindTestItems(List<string> names)
        {
            var picked = new List<GameObject>();
            if (names.Count > 0)
            {
                foreach (string name in names)
                {
                    // 0.2.275: any prefab the game knows (an item, a tree, a piece); only items are held and dropped.
                    var prefab = ObjectDB.instance.GetItemPrefab(name) ?? (ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null);
                    if (prefab != null && !picked.Contains(prefab)) picked.Add(prefab);
                }
                return picked;
            }
            var vanilla = new HashSet<string>(StringComparer.Ordinal);
            if (s_vanillaPrefabs != null) foreach (var prefab in s_vanillaPrefabs) if (prefab != null) vanilla.Add(prefab.name);
            var ownCopy = new List<KeyValuePair<string, GameObject>>();
            var moved = new List<KeyValuePair<string, GameObject>>();
            var slots = new List<Material>();
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null || vanilla.Contains(prefab.name) || prefab.GetComponent<ItemDrop>() == null) continue;
                string own = null, wasMoved = null;
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.GetSharedMaterials(slots);
                    foreach (var material in slots)
                    {
                        var shader = material != null ? material.shader : null;
                        if (shader == null) continue;
                        Shader live = Live(shader.name);
                        if (live == null) continue;
                        if (!AllLive(shader.name).Contains(shader)) own = own ?? shader.name;
                        else if (VanillaShaderRebind.TryGetMovedFrom(material, out _)) wasMoved = wasMoved ?? shader.name;
                    }
                }
                if (own != null) ownCopy.Add(new KeyValuePair<string, GameObject>(own, prefab));
                else if (wasMoved != null) moved.Add(new KeyValuePair<string, GameObject>(wasMoved, prefab));
            }
            var shaders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in new[] { ownCopy, moved })
                foreach (var pair in list)
                {
                    if (picked.Count >= RebindTestMaxItems) return picked;
                    if (shaders.Add(pair.Key)) picked.Add(pair.Value);
                }
            return picked;
        }

        // One line per material on the object's renderers (at most RebindTestMaxLines); returns how many draw magenta.
        private static int DescribeRebindRenderers(string label, GameObject root)
        {
            int magenta = 0, lines = 0, more = 0;
            var slots = new List<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.GetSharedMaterials(slots);
                foreach (var material in slots)
                {
                    if (material == null) continue;
                    string line = DescribeRebindMaterial(material, out bool isMagenta);
                    if (isMagenta) magenta++;
                    if (lines < RebindTestMaxLines) { lines++; RebindTestSay($"{RebindTestTag} {label}: {renderer.name}{(renderer.enabled ? "" : " (off)")}: {line}"); }
                    else more++;
                }
            }
            if (lines == 0) RebindTestSay($"{RebindTestTag} {label}: no renderer with a material");
            if (more > 0) RebindTestSay($"{RebindTestTag} {label}: ... {more} more material(s)");
            return magenta;
        }

        private static string DescribeRebindMaterial(Material material, out bool magenta)
        {
            magenta = false;
            var shader = material.shader;
            if (shader == null || shader.name.Contains("InternalErrorShader"))
            {
                magenta = true;
                return $"material {material.name}: no working shader (magenta)";
            }
            Shader live = Live(shader.name);
            IReadOnlyList<Shader> copies = AllLive(shader.name);
            string movedFrom = VanillaShaderRebind.TryGetMovedFrom(material, out string was) ? $"; moved at load from {was}" : "";
            string now = $"material {material.name}: shader {shader.name} #{shader.GetInstanceID()} (queue {material.rawRenderQueue}){DescribeMainTexture(material)}{movedFrom}";
            if (live == null) return now + "; no game shader of that name, no rule touches it";
            string ids = string.Join(", ", copies.Select(copy => "#" + copy.GetInstanceID()));
            if (copies.Contains(shader))
                return now + $"; one of the game's own copies ({copies.Count}: {ids})" + (movedFrom.Length > 0 ? "" : ", it came that way")
                    + (shader == live ? "" : "; 0.2.268 rule: moved it onto #" + live.GetInstanceID() + "; 0.2.275 rule: leaves it");
            // 0.2.275: a bundle's copy of a Unity shader stays; any other copy moves to the first game copy it is the same shader as.
            if (VanillaShaderRebind.IsUnityShader(shader.name))
                return now + $"; the game's copies: {ids}; 0.2.268-0.2.274 rule: moves it onto #{live.GetInstanceID()}; "
                    + "0.2.275 rule: leaves it (Unity's own shader: its bundle's copy carries its materials' variants, the game's only Valheim's)";
            Shader match = null;
            string firstWhy = null;
            foreach (var copy in copies)
            {
                string why = VanillaShaderRebind.WhyNotSameShader(shader, copy);
                if (why == null) { match = copy; break; }
                firstWhy ??= why;
            }
            return now + $"; the game's copies: {ids}; 0.2.268 rule: moves it onto #{live.GetInstanceID()}; 0.2.275 rule: "
                + (match != null ? $"moves it onto #{match.GetInstanceID()}" : $"leaves it ({firstWhy})");
        }

        // 0.2.275 ([lead]: blocky leaves in today's frames, shader or texture?): the main texture's name, size, format and mip count.
        private static string DescribeMainTexture(Material material)
        {
            try
            {
                if (!material.HasProperty("_MainTex")) return "";
                Texture main = material.GetTexture("_MainTex");
                if (main == null) return "; _MainTex none";
                return $"; _MainTex {main.name} {main.width}x{main.height}"
                    + (main is Texture2D texture ? $" {texture.format} {texture.mipmapCount} mips" : $" {main.GetType().Name}")
                    + $" {main.filterMode}";
            }
            catch (Exception ex) { return $"; _MainTex unreadable ({ex.GetType().Name})"; }
        }

        // ── 0.2.276 (Fire, asked three times: a clear screenshot that WildWeapons' weapons render right on 0.2.275):
        // `shaderrebind_test closeup [item ...]`. Per item: the drop, frozen at chest height in front of the player, then the item held.
        // For each, the main camera is taken off GameCamera for a moment and aimed at the item (a drop at its broad face, a held item from
        // the player's right) from where it fills about 70 % of the frame, with a light beside the camera and the HUD hidden; the frame is
        // saved as closeup_drop_<item> / closeup_held_<item>. The item's materials are listed as in the plain run. Default: four
        // WildWeapons swords and SwordIron for comparison.
        private static readonly string[] CloseupDefault =
            { "BloodstoneSword_CAH", "SilvercrystalGreatsword_CAH", "VolcanoGreatsword_CAH", "OrcSword_CAH", "SwordIron" };
        private const float CloseupFill = 0.7f, CloseupSettle = 0.6f;

        private static System.Collections.IEnumerator RunRebindCloseups(List<string> names)
        {
            s_rebindTestRunning = true;
            float started = Time.realtimeSinceStartup;
            int pass = 0, fail = 0, skip = 0;
            Player player = Player.m_localPlayer;
            if (names.Count == 0) names.AddRange(CloseupDefault);
            int steps = names.Count * 2, step = 0;
            GameCamera gameCamera = GameCamera.m_instance;
            Camera camera = gameCamera != null && gameCamera.m_camera != null ? gameCamera.m_camera : Camera.main;
            GameObject hud = Hud.m_instance != null ? Hud.m_instance.m_rootObject : null;
            bool hudWasOn = hud != null && hud.activeSelf;
            GameObject lightObject = null;
            GameObject dropped = null;
            ItemDrop.ItemData held = null;
            try
            {
                RebindTestSay($"{RebindTestTag} BEGIN {FiresUnifiedCore.PluginName} {FiresUnifiedCore.PluginVersion} closeup world '{(ZNet.World != null ? ZNet.World.m_name : "?")}': {steps} steps");
                if (camera == null || player == null)
                {
                    fail = steps;
                    RebindTestSay($"{RebindTestTag} 1/{steps} closeup FAIL - no {(camera == null ? "main camera" : "local player")}");
                    yield break;
                }
                lightObject = new GameObject("FiresCloseupLight");
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 6f;
                light.intensity = 1.6f;
                light.color = Color.white;
                light.shadows = LightShadows.None;
                if (hud != null) hud.SetActive(false);

                foreach (string name in names)
                {
                    var prefab = ObjectDB.instance.GetItemPrefab(name);
                    if (prefab == null || prefab.GetComponent<ItemDrop>() == null)
                    {
                        skip += 2;
                        RebindTestSay($"{RebindTestTag} {++step}/{steps} closeup drop {name} SKIP - no such item in ObjectDB");
                        RebindTestSay($"{RebindTestTag} {++step}/{steps} closeup held {name} SKIP - no such item in ObjectDB");
                        continue;
                    }

                    // Dropped: frozen at chest height 1.6 m ahead, never picked up.
                    step++;
                    Vector3 at = player.transform.position + player.transform.forward * 1.6f + Vector3.up * 1.4f;
                    dropped = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
                    FreezeForCloseup(dropped);
                    yield return null;
                    int magenta = DescribeRebindRenderers($"{step}/{steps} closeup drop {name}", dropped);
                    yield return AimAndShoot(gameCamera, camera, light, dropped, null, "closeup_drop_" + name);
                    if (magenta > 0) { fail++; RebindTestSay($"{RebindTestTag} {step}/{steps} closeup drop {name} FAIL - {magenta} material(s) draw magenta"); }
                    else { pass++; RebindTestSay($"{RebindTestTag} {step}/{steps} closeup drop {name} PASS - shot above"); }
                    DestroyCloseupDrop(dropped);
                    dropped = null;

                    // Held: equipped from the inventory, shot from the player's right.
                    step++;
                    held = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                    held.m_dropPrefab = prefab;
                    held.m_stack = 1;
                    if (!(player.GetInventory().AddItem(held) && player.EquipItem(held, true)))
                    {
                        skip++;
                        RebindTestSay($"{RebindTestTag} {step}/{steps} closeup held {name} SKIP - couldn't be equipped (inventory full, or not equipable)");
                    }
                    else
                    {
                        yield return new WaitForSeconds(RebindTestSettle);
                        var vis = player.m_visEquipment;
                        GameObject instance = vis != null ? (vis.m_rightItemInstance ?? vis.m_leftItemInstance) : null;
                        if (instance == null)
                        {
                            skip++;
                            RebindTestSay($"{RebindTestTag} {step}/{steps} closeup held {name} SKIP - equipped but no hand visual was built");
                        }
                        else
                        {
                            magenta = DescribeRebindRenderers($"{step}/{steps} closeup held {name}", instance);
                            yield return AimAndShoot(gameCamera, camera, light, instance, player.transform.right, "closeup_held_" + name);
                            if (magenta > 0) { fail++; RebindTestSay($"{RebindTestTag} {step}/{steps} closeup held {name} FAIL - {magenta} material(s) draw magenta"); }
                            else { pass++; RebindTestSay($"{RebindTestTag} {step}/{steps} closeup held {name} PASS - shot above"); }
                        }
                    }
                    player.UnequipItem(held, false);
                    player.GetInventory().RemoveItem(held);
                    held = null;
                }
            }
            finally
            {
                try
                {
                    if (held != null && player != null) { player.UnequipItem(held, false); player.GetInventory().RemoveItem(held); }
                    if (dropped != null) DestroyCloseupDrop(dropped);
                    if (lightObject != null) UnityEngine.Object.Destroy(lightObject);
                    if (hud != null) hud.SetActive(hudWasOn);
                    if (gameCamera != null) gameCamera.enabled = true;
                }
                catch (Exception ex) { RebindTestSay($"{RebindTestTag} cleanup: {ex.GetType().Name}: {ex.Message}"); }
                RebindTestSay($"{RebindTestTag} END {(fail > 0 ? "FAIL" : "PASS")}: {pass} pass, {fail} fail, {skip} skip in {Time.realtimeSinceStartup - started:0} s");
                s_rebindTestRunning = false;
            }
        }

        // A drop that stays where it is put and is never picked up (the player stands 1.6 m away).
        private static void FreezeForCloseup(GameObject drop)
        {
            var itemDrop = drop.GetComponent<ItemDrop>();
            if (itemDrop != null) itemDrop.m_autoPickup = false;
            foreach (var body in drop.GetComponentsInChildren<Rigidbody>())
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.useGravity = false;
                body.isKinematic = true;
            }
        }

        private static void DestroyCloseupDrop(GameObject drop)
        {
            var nview = drop.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && ZNetScene.instance != null) ZNetScene.instance.Destroy(drop);
            else UnityEngine.Object.Destroy(drop);
        }

        // The item's mesh bounds (particles left out: an item's glow would inflate them).
        private static bool TryMeshBounds(GameObject target, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any;
        }

        // Takes the main camera off GameCamera, aims it at the target so its largest side fills CloseupFill of the frame height (a drop:
        // looking along its thinnest axis, at its broad face; a held item: from `side`), lights it, saves the frame, gives the camera back.
        private static System.Collections.IEnumerator AimAndShoot(GameCamera gameCamera, Camera camera, Light light, GameObject target, Vector3? side, string what)
        {
            if (target == null || !TryMeshBounds(target, out Bounds bounds))
            {
                RebindTestSay($"{RebindTestTag} SHOT {what} skipped: no visible mesh");
                yield break;
            }
            Vector3 size = bounds.size;
            Vector3 view = side.HasValue ? -side.Value.normalized
                : size.x <= size.y && size.x <= size.z ? Vector3.right
                : size.y <= size.z ? Vector3.down : Vector3.forward;
            float extent = Mathf.Max(0.2f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            float distance = Mathf.Clamp(extent * 0.5f / CloseupFill / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), camera.nearClipPlane + 0.3f, 6f);
            Vector3 up = Mathf.Abs(Vector3.Dot(view, Vector3.up)) > 0.9f
                ? (Player.m_localPlayer != null ? Player.m_localPlayer.transform.forward : Vector3.forward) : Vector3.up;
            Vector3 position = bounds.center - view * distance;
            Quaternion rotation = Quaternion.LookRotation(view, up);
            light.transform.position = position + up * 0.3f;
            if (gameCamera != null) gameCamera.enabled = false;
            camera.transform.SetPositionAndRotation(position, rotation);
            yield return new WaitForSecondsRealtime(CloseupSettle);
            camera.transform.SetPositionAndRotation(position, rotation);
            RebindTestSay($"{RebindTestTag} {what}: camera {distance:0.00} m from the item's centre, item {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m");
            yield return RebindTestShot(what);
            if (gameCamera != null) gameCamera.enabled = true;
        }

        // A screenshot of the frame, after it is drawn, into BepInEx\FiresTests.
        private static System.Collections.IEnumerator RebindTestShot(string what)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) what = what.Replace(bad, '_');
                var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                texture.Apply();
                byte[] png = texture.EncodeToPNG();
                UnityEngine.Object.Destroy(texture);
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                string file = System.IO.Path.Combine(folder, $"{RebindTestCommand}_{FiresUnifiedCore.PluginVersion}_{what}.png");
                System.IO.File.WriteAllBytes(file, png);
                RebindTestSay($"{RebindTestTag} SHOT {what}: {file}");
            }
            catch (Exception ex) { RebindTestSay($"{RebindTestTag} SHOT {what} failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // The log line, the console, and BepInEx\FiresTests\shaderrebind_test.txt (FDT's runner reads it).
        private static void RebindTestSay(string line)
        {
            Debug.Log(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, RebindTestCommand + ".txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) { }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_SnapshotVanillaPrefabs
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNetScene __instance)
            {
                if (IsHeadless || __instance == null || __instance.m_prefabs == null) return;
                s_vanillaPrefabs = new List<GameObject>(__instance.m_prefabs);
                s_live = null;
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        private static class ClutterSystem_Awake_SnapshotVanillaClutter
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ClutterSystem __instance)
            {
                if (IsHeadless || __instance == null || __instance.m_clutter == null) return;
                s_vanillaClutter = new List<GameObject>();
                foreach (var clutter in __instance.m_clutter)
                    if (clutter?.m_prefab != null) s_vanillaClutter.Add(clutter.m_prefab);
                s_live = null;
            }
        }
    }
}
