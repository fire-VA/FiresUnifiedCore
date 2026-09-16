using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc
{
    /// <summary>
    /// Scale corrections for equipment on scaled companions. Weapons already inherit the body's scale through the
    /// bone hierarchy; this enlarges helmets on giants so they don't look undersized.
    /// </summary>
    public class CompanionWeaponScaler : MonoBehaviour
    {
        private CompanionRandomLoadout _randomLoadout;
        private NpcVisEquipment _npcVisEquipment;
        private VisEquipment _visEquipment;
        private float _lastScale = 1.0f;
        private float _updateTimer;
        private const float UpdateInterval = 0.5f;
        
        // Track scaled items (key = instance ID, value = applied scale)
        private Dictionary<int, float> _scaledInstances = new Dictionary<int, float>();
        
        // For giants, scale helmets slightly larger
        private const float GiantHelmetExtraScale = 1.1f;
        
        // Set to true to apply inverse scaling to weapons (makes them appear normal-sized on scaled characters)
        // Set to false to let weapons scale with the character (giants have big swords)
        public static bool ApplyInverseWeaponScaling = false;
        
        public static bool VerboseLogging = false;

        private void Awake()
        {
            _randomLoadout = GetComponent<CompanionRandomLoadout>();
        }

        private void Start()
        {
            _npcVisEquipment = GetComponent<NpcVisEquipment>();
            
            if (_npcVisEquipment != null)
            {
                _visEquipment = _npcVisEquipment.VisEquipment;
            }
            
            if (_visEquipment == null)
            {
                _visEquipment = GetComponent<VisEquipment>();
            }
        }

        private void LateUpdate()
        {
            _updateTimer += Time.deltaTime;
            if (_updateTimer < UpdateInterval) return;
            _updateTimer = 0f;
            
            float currentScale = _randomLoadout != null ? _randomLoadout.GetScale() : 1.0f;
            
            // Skip if no scaling needed
            if (Mathf.Abs(currentScale - 1.0f) < 0.01f) return;
            
            if (_visEquipment == null)
            {
                if (_npcVisEquipment != null)
                    _visEquipment = _npcVisEquipment.VisEquipment;
                if (_visEquipment == null)
                    _visEquipment = GetComponent<VisEquipment>();
                if (_visEquipment == null) return;
            }
            
            ScaleEquipmentInstances(currentScale);
            _lastScale = currentScale;
        }

        private void ScaleEquipmentInstances(float companionScale)
        {
            if (_visEquipment == null) return;
            
            bool isGiant = _randomLoadout != null && _randomLoadout.IsGiant();
            bool isDwarf = _randomLoadout != null && _randomLoadout.IsDwarf();
            
            // Calculate inverse scale for weapons if enabled
            // inverseScale * companionScale = 1.0 (normal world size)
            float inverseScale = ApplyInverseWeaponScaling ? (1.0f / companionScale) : 1.0f;
            
            // Scale weapon instances
            ScaleItemInstance("m_rightItemInstance", inverseScale, companionScale);
            ScaleItemInstance("m_leftItemInstance", inverseScale, companionScale);
            ScaleItemInstance("m_leftBackItemInstance", inverseScale, companionScale);
            ScaleItemInstance("m_rightBackItemInstance", inverseScale, companionScale);
            
            // Giants get slightly larger helmets
            if (isGiant)
            {
                ScaleItemInstance("m_helmetItemInstance", GiantHelmetExtraScale, companionScale);
            }
        }

        private void ScaleItemInstance(string fieldName, float targetLocalScale, float companionScale)
        {
            try
            {
                var field = typeof(VisEquipment).GetField(fieldName, 
                    System.Reflection.BindingFlags.NonPublic | 
                    System.Reflection.BindingFlags.Instance);
                
                if (field == null) return;
                
                var itemInstance = field.GetValue(_visEquipment) as GameObject;
                if (itemInstance == null) return;
                
                int instanceId = itemInstance.GetInstanceID();
                
                // Skip if already processed
                if (_scaledInstances.ContainsKey(instanceId)) return;
                
                // Apply scale if not default
                if (Mathf.Abs(targetLocalScale - 1.0f) > 0.01f)
                {
                    itemInstance.transform.localScale = Vector3.one * targetLocalScale;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionWeaponScaler] {fieldName}: Applied localScale={targetLocalScale:F2}, " +
                            $"worldScale={itemInstance.transform.lossyScale}, companionScale={companionScale:F2}");
                    }
                }
                else if (VerboseLogging)
                {
                    Debug.Log($"[CompanionWeaponScaler] {fieldName}: localScale={itemInstance.transform.localScale}, " +
                        $"worldScale={itemInstance.transform.lossyScale}, companionScale={companionScale:F2}");
                }
                
                _scaledInstances[instanceId] = targetLocalScale;
            }
            catch (System.Exception ex)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[CompanionWeaponScaler] Failed to scale {fieldName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears tracked instances when equipment changes.
        /// </summary>
        public void ClearTrackedInstances()
        {
            _scaledInstances.Clear();
        }

        /// <summary>
        /// Forces immediate scaling check.
        /// </summary>
        public void ForceScaleEquipment()
        {
            _scaledInstances.Clear();
            _updateTimer = UpdateInterval;
        }
    }
}
