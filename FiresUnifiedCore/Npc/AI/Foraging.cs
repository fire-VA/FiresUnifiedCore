using System.Linq;
using FiresCore.Npc.Combat;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Food on the way while fleeing (Core 0.2.223; Fire 20:27, R90 run 6: "the fleeing bot at low health ran right by mushrooms it
    /// could have picked to give it a second food item and regen health"). Asked from a flee tick (FDT's survival rule; a companion's
    /// later): a ripe pickable whose item is food, ahead within a corridor of the flee line, when the body has a free food slot and
    /// no foe is within a swing of it. The caller picks it, eats it and flees on.
    /// </summary>
    public static class Foraging
    {
        /// <summary>A player body eats at most this many foods at once (vanilla).</summary>
        public const int MaxFoods = 3;
        /// <summary>A foe this much beyond its weapon's reach still counts as "within a swing" (m).</summary>
        private const float SwingSlack = 1.5f;

        /// <summary>
        /// The pickable to grab while fleeing along <paramref name="fleeDir"/> from <paramref name="body"/>: ripe, its item food
        /// (m_food &gt; 0) that the body can eat now (vanilla Player.CanEat: a kind already eaten only once it is half burnt, and a free
        /// slot or one that can be eaten again), within <paramref name="corridor"/> m of the flee line and no more than
        /// <paramref name="ahead"/> m along it. False with <paramref name="why"/> when nothing can be eaten, a foe is within a swing,
        /// or nothing qualifies.
        /// </summary>
        public static bool FleeSnack(Humanoid body, Vector3 fleeDir, out GameObject pickable, out string why, float corridor = 2f, float ahead = 12f)
        {
            pickable = null;
            if (!(body is Player player)) { why = "only a player body eats this way so far"; return false; }
            if (player.GetFoods().Count >= MaxFoods && !player.GetFoods().Any(f => f.m_item != null && f.CanEatAgain())) { why = "no free food slot"; return false; }
            Vector3 at = body.transform.position;
            fleeDir.y = 0f;
            if (fleeDir.sqrMagnitude < 0.01f) { why = "no flee direction"; return false; }
            fleeDir.Normalize();

            foreach (Character foe in Character.GetAllCharacters())
            {
                if (foe == null || foe.IsDead() || foe == body || !BaseAI.IsEnemy(body, foe)) continue;
                ItemDrop.ItemData weapon = ThreatLevel.FoeWeapon(foe);
                float reach = weapon?.m_shared?.m_attack != null ? Mathf.Max(1f, weapon.m_shared.m_attack.m_attackRange) : 2f;
                float d = Vector3.Distance(at, foe.transform.position);
                if (d <= reach + SwingSlack) { why = $"{ThreatLevel.FoeName(foe)} is within a swing ({d:0.0} m)"; return false; }
            }

            float bestAlong = float.MaxValue;
            string bestName = null, eaten = null;
            foreach (var data in ResourceDataHelper.FindResourcesInRange(at, ahead + corridor))
            {
                if (data == null || !data.IsPickable || data.GameObject == null) continue;
                Vector3 to = data.InteractionPosition - at;
                to.y = 0f;
                float along = Vector3.Dot(to, fleeDir);
                if (along < 0f || along > ahead) continue;
                if ((to - fleeDir * along).magnitude > corridor) continue;
                if (!IsRipeFood(data, out string item, out ItemDrop.ItemData food)) continue;
                // Vanilla refuses a kind still in the belly until it is half burnt (0.2.227, [seasons] R90 run 9: Coop2 with Mushroom +
                // Raspberry eaten picked a Mushroom and never ate it, where Honey or a Raspberry later would have bought health).
                if (!player.CanEat(food, false)) { eaten = eaten ?? item; continue; }
                if (along >= bestAlong) continue;
                bestAlong = along;
                pickable = data.GameObject;
                bestName = item;
            }
            if (pickable == null)
            {
                why = eaten != null ? $"no ripe food it can eat within {corridor:0} m of the way for {ahead:0} m ({eaten} already eaten)"
                    : $"no ripe food within {corridor:0} m of the way for {ahead:0} m";
                return false;
            }
            why = $"{bestName} {bestAlong:0} m ahead on the way, food slots {player.GetFoods().Count}/{MaxFoods}";
            return true;
        }

        // A pickable that can be picked now and gives food (berries, mushrooms, carrots), or a placed pickable item that is food.
        private static bool IsRipeFood(ResourceDataHelper.ResourceData data, out string item, out ItemDrop.ItemData food)
        {
            item = null;
            food = null;
            if (data.Pickable != null)
            {
                if (!data.Pickable.CanBePicked()) return false;
                ItemDrop drop = data.Pickable.m_itemPrefab != null ? data.Pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData.m_shared.m_food <= 0f) return false;
                item = drop.name;
                food = drop.m_itemData;
                return true;
            }
            if (data.PickableItem != null)
            {
                ItemDrop drop = data.PickableItem.m_itemPrefab;
                if (drop == null || drop.m_itemData.m_shared.m_food <= 0f) return false;
                item = drop.name;
                food = drop.m_itemData;
                return true;
            }
            return false;
        }
    }
}
