using System;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Shared base setup for networked NPC/character prefabs — the components every Fires NPC needs
    /// (the Marketplace static NPC, companions, …): ZNetView, Rigidbody, collider, transform +
    /// animation sync, and a configured player-faction Humanoid. Companion-specific behavior
    /// (CompanionAI, controllers) layers on top in the companion mod; "Press-E" interaction
    /// (NpcController) layers on in the host mod. Pure vanilla Valheim — no mod dependency, so it
    /// lives in Core and both the Marketplace mod and the companion mod build their NPCs on it.
    /// </summary>
    public static class NpcPrefabSetup
    {
        /// <summary>Add + configure the shared networked-character components on <paramref name="prefab"/>.</summary>
        public static void SetupBaseComponents(GameObject prefab)
        {
            if (prefab == null) return;

            // 1. ZNetView — networking
            var zNetView = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
            zNetView.m_persistent = true;
            zNetView.m_type = ZDO.ObjectType.Default;

            // 2. Rigidbody — physics
            var rigidbody = prefab.GetComponent<Rigidbody>() ?? prefab.AddComponent<Rigidbody>();
            rigidbody.mass = 50f;
            rigidbody.useGravity = true;
            rigidbody.isKinematic = false;
            rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // 3. CapsuleCollider — collision
            var collider = prefab.GetComponent<CapsuleCollider>() ?? prefab.AddComponent<CapsuleCollider>();
            collider.height = 1.8f;
            collider.radius = 0.3f;
            collider.center = new Vector3(0, 0.9f, 0);

            // 4. ZSyncTransform — network position
            var syncTransform = prefab.GetComponent<ZSyncTransform>() ?? prefab.AddComponent<ZSyncTransform>();
            syncTransform.m_syncPosition = true;
            syncTransform.m_syncRotation = true;
            syncTransform.m_syncScale = false;
            syncTransform.m_syncBodyVelocity = true;

            // 5. ZSyncAnimation — network animation (only when an Animator is present)
            if (prefab.GetComponentInChildren<Animator>(true) != null && prefab.GetComponent<ZSyncAnimation>() == null)
                prefab.AddComponent<ZSyncAnimation>();

            // 6. Humanoid — equipment + combat base
            var humanoid = prefab.GetComponent<Humanoid>() ?? prefab.AddComponent<Humanoid>();
            ConfigureHumanoid(humanoid, rigidbody);
        }

        /// <summary>Apply the shared player-faction humanoid defaults (movement, swimming, eye/visual transforms).</summary>
        public static void ConfigureHumanoid(Humanoid humanoid, Rigidbody rigidbody)
        {
            if (humanoid == null) return;
            var prefab = humanoid.gameObject;

            humanoid.m_name = "NPC";
            humanoid.m_group = "player";
            // Dverger (attackable); owner/ally immunity is enforced in code, not by faction.
            humanoid.m_faction = Character.Faction.Dverger;
            humanoid.m_health = 200f;
            humanoid.m_walkSpeed = 2f;
            humanoid.m_speed = 4f;
            humanoid.m_runSpeed = 7f;
            humanoid.m_turnSpeed = 300f;
            humanoid.m_acceleration = 1f;
            humanoid.m_jumpForce = 8f;
            humanoid.m_staggerWhenBlocked = true;
            humanoid.m_staggerDamageFactor = 0.3f;
            humanoid.m_tolerateWater = true;
            humanoid.m_tolerateFire = false;
            humanoid.m_tolerateSmoke = true;
            humanoid.m_canSwim = true;
            humanoid.m_swimSpeed = 2f;
            humanoid.m_swimTurnSpeed = 100f;
            humanoid.m_swimAcceleration = 0.05f;

            Transform eyeTransform = FindTransformByNames(prefab.transform, new[] { "Eye", "eye", "Head", "head", "EyePos" });
            if (eyeTransform == null)
            {
                var eyeObj = new GameObject("Eye");
                eyeObj.transform.SetParent(prefab.transform);
                eyeObj.transform.localPosition = new Vector3(0, 1.6f, 0.1f);
                eyeObj.transform.localRotation = Quaternion.identity;
                eyeTransform = eyeObj.transform;
            }
            humanoid.m_eye = eyeTransform;

            Transform visualTransform = FindTransformByNames(prefab.transform, new[] { "Visual", "visual" }) ?? prefab.transform;

            // Character.m_body / m_visual are protected — set via reflection.
            try
            {
                var characterType = typeof(Character);
                var bodyField = characterType.GetField("m_body", BindingFlags.NonPublic | BindingFlags.Instance);
                if (bodyField != null && bodyField.FieldType == typeof(Rigidbody))
                    bodyField.SetValue(humanoid, rigidbody);

                var visualField = characterType.GetField("m_visual", BindingFlags.NonPublic | BindingFlags.Instance);
                if (visualField != null && visualField.FieldType == typeof(Transform))
                    visualField.SetValue(humanoid, visualTransform);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcPrefabSetup] Failed to set protected Character fields: {ex.Message}");
            }
        }

        /// <summary>Depth-first search for the first child transform matching any of <paramref name="names"/> (case-insensitive).</summary>
        public static Transform FindTransformByNames(Transform root, string[] names)
        {
            foreach (var name in names)
            {
                var found = FindTransformRecursive(root, name);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return parent;
            foreach (Transform child in parent)
            {
                var found = FindTransformRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
