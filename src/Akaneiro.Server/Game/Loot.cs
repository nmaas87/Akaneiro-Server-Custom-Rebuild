using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Rolls the loot a monster or an object (chest, basket) leaves behind. The rules are rebuilt
    /// from the client's own data:
    ///   - how many items drop comes from the LootDrop component (EquipmentDrops/ConsumableDrops)
    ///   - which item drops is weighted by the drop chances in the weapon/armor/accessory tables
    ///   - which model it uses comes from the prefab tables, which list a level range per variant
    ///   - enchantments, elements and gems on equipment: *.stat picks which of the three (or none)
    ///     by item level, the attribute tables pick which one by attach chance and level range
    ///   - crafting materials: no prefab lists them, the original server rolled them itself; the
    ///     chances here are assumptions (see <see cref="MaterialChance"/>).
    /// </summary>
    public static class Loot
    {
        /// <summary>Chance in percent that a normal monster drops a crafting material.</summary>
        public const int MaterialChance = 6;

        /// <summary>Chance in percent that a chest or other object with loot holds a material.</summary>
        public const int ObjectMaterialChance = 35;

        public static List<ItemData> Roll(GameData.MonsterRow monster, GameData data, Random rng, bool materials = true)
        {
            var drops = new List<ItemData>();
            if (!monster.CanDropLoot)
                return drops;

            int level = Math.Max(1, monster.LootLevel);
            Roll(drops, level, monster.EquipmentDrops, monster.ConsumableDrops, monster.Materials, monster.UniqueDrops, data, rng);

            // Bosses always leave materials, other monsters now and then.
            int count = !materials ? 0 : monster.IsBoss ? 1 + rng.Next(2) : rng.Next(100) < MaterialChance ? 1 : 0;
            for (int i = 0; i < count; i++)
                drops.Add(RollMaterial(rng));
            return drops;
        }

        /// <summary>What a chest or another object with a LootDrop holds.</summary>
        public static List<ItemData> Roll(GameData.BreakableInfo obj, GameData data, Random rng)
        {
            var drops = new List<ItemData>();
            GameData.LootTable loot = obj.Loot;
            if (loot == null || !loot.CanDropLoot)
                return drops;

            int level = Math.Max(1, obj.LootLevel);
            Roll(drops, level, loot.EquipmentDrops, loot.ConsumableDrops, loot.Materials, loot.UniqueDrops, data, rng);
            if (rng.Next(100) < ObjectMaterialChance)
                drops.Add(RollMaterial(rng));
            return drops;
        }

        private static void Roll(List<ItemData> drops, int level, List<GameData.DropRoll> equipment, List<GameData.DropRoll> consumables,
            List<GameData.MaterialDrop> materials, List<GameData.UniqueDrop> uniques, GameData data, Random rng)
        {
            // Fixed items, like the Amano Harbinger's katana.
            foreach (GameData.UniqueDrop unique in uniques ?? new List<GameData.UniqueDrop>())
            {
                if (unique.ItemId != 0 && rng.Next(100) < unique.Chance)
                    drops.Add(new ItemData { ItemId = unique.ItemId, PrefabId = unique.PrefabId, Level = level, Count = 1 });
            }

            foreach (GameData.MaterialDrop material in materials ?? new List<GameData.MaterialDrop>())
            {
                if (material.ItemId != 0 && rng.Next(100) < material.Chance)
                    drops.Add(new ItemData { ItemId = material.ItemId, PrefabId = material.PrefabId, Level = 1, Count = Math.Max(1, material.Count) });
            }

            for (int i = 0; i < RollAmount(equipment, rng); i++)
            {
                ItemData item = RollEquipment(data, level, rng);
                if (item != null)
                    drops.Add(item);
            }

            for (int i = 0; i < RollAmount(consumables, rng); i++)
            {
                ItemData item = RollConsumable(data, level, rng);
                if (item != null)
                    drops.Add(item);
            }
        }

        /// <summary>
        /// A crafting material (Props.General): enchantment materials (6001-6009) are the most common,
        /// gems (7001-7009) and element cores (8001-8004) rarer, the Karma Core (9001) the rarest.
        /// PrefabId is the client's icon index, not a model variant (ItemType.IsModelIndex): the
        /// crafting panel and material lists build these as GetItemObject(id, 1, 0, 0, 0, 1).
        /// </summary>
        public static ItemData RollMaterial(Random rng)
        {
            int roll = rng.Next(100);
            int itemId = roll < 55 ? 6001 + rng.Next(9)
                : roll < 80 ? 7001 + rng.Next(9)
                : roll < 97 ? 8001 + rng.Next(4)
                : 9001;
            return new ItemData { ItemId = itemId, PrefabId = 1, Level = 1, Count = 1 };
        }

        /// <summary>Picks how many items drop; the entries are weights, not percentages.</summary>
        private static int RollAmount(List<GameData.DropRoll> rolls, Random rng)
        {
            if (rolls == null || rolls.Count == 0)
                return 0;

            int total = rolls.Sum(r => Math.Max(0, r.Chance));
            if (total <= 0)
                return 0;

            int roll = rng.Next(total);
            foreach (GameData.DropRoll entry in rolls)
            {
                roll -= Math.Max(0, entry.Chance);
                if (roll < 0)
                    return Math.Max(0, entry.Amount);
            }
            return 0;
        }

        public static ItemData RollEquipment(GameData data, int level, Random rng)
        {
            // Which kind of equipment: weighted by how likely the entries of each table are overall.
            var tables = new (IReadOnlyList<GameData.LootItemRow> Items, IReadOnlyList<GameData.ItemPrefabRow> Prefabs)[]
            {
                (data.WeaponDrops, data.WeaponPrefabs),
                (data.ArmorDrops, data.ArmorPrefabs),
                (data.AccessoryDrops, data.AccessoryPrefabs),
            };

            var weights = tables.Select(t => t.Items.Sum(i => Math.Max(0, i.DropChance))).ToArray();
            int table = PickIndex(weights, rng);
            if (table < 0)
                return null;

            GameData.LootItemRow item = PickWeighted(tables[table].Items, i => i.DropChance, rng);
            GameData.ItemPrefabRow prefab = PickPrefab(tables[table].Prefabs, level, rng);
            if (item == null || prefab == null)
                return null;

            var drop = new ItemData { ItemId = item.Id, PrefabId = prefab.PrefabId, Level = level, Count = 1 };
            if (data.LootAttributes != null && table < data.LootAttributes.Count)
                RollAttribute(drop, data.LootAttributes[table], rng);
            return drop;
        }

        /// <summary>
        /// Gives a dropped piece of equipment an enchantment, an element or a gem - or nothing - with
        /// the chances of its level (e.g. level 1: 30 none, 42 enchantment, 33 gem, 19 element).
        /// </summary>
        public static void RollAttribute(ItemData item, GameData.EquipmentAttributes attributes, Random rng)
        {
            if (attributes.ChancesByLevel.Count == 0)
                return;
            int level = attributes.ChancesByLevel.ContainsKey(item.Level) ? item.Level
                : attributes.ChancesByLevel.Keys.OrderBy(l => Math.Abs(l - item.Level)).First();
            var (none, enchant, gem, element) = attributes.ChancesByLevel[level];

            List<GameData.AttributeRow> Fitting(List<GameData.AttributeRow> rows) => rows
                .Where(r => r.Chance > 0 && (r.MinLevel == 0 || item.Level >= r.MinLevel) && (r.MaxLevel == 0 || item.Level <= r.MaxLevel))
                .ToList();

            switch (PickIndex(new[] { Math.Max(0, none), Math.Max(0, enchant), Math.Max(0, gem), Math.Max(0, element) }, rng))
            {
                case 1:
                    item.Enchant = PickWeighted(Fitting(attributes.Enchants), r => r.Chance, rng)?.Id ?? 0;
                    break;
                case 2:
                    item.Gem = PickWeighted(Fitting(attributes.Gems), r => r.Chance, rng)?.Id ?? 0;
                    break;
                case 3:
                    item.Element = PickWeighted(Fitting(attributes.Elements), r => r.Chance, rng)?.Id ?? 0;
                    break;
            }
        }

        private static ItemData RollConsumable(GameData data, int level, Random rng)
        {
            List<GameData.LootItemRow> usable = data.Consumables
                .Where(c => !data.ItemLevelRequirements.TryGetValue(c.Id, out int required) || required <= level)
                .ToList();
            if (usable.Count == 0)
                return null;

            GameData.LootItemRow item = usable[rng.Next(usable.Count)];
            // PrefabId is the client's icon index for a consumable, never 0 (the world model, which
            // carries no icon texture) - see ItemType.IsModelIndex.
            return new ItemData { ItemId = item.Id, PrefabId = 1, Level = level, Count = 1 };
        }

        /// <summary>Picks a model variant that covers the item's level, or the closest one.</summary>
        private static GameData.ItemPrefabRow PickPrefab(IReadOnlyList<GameData.ItemPrefabRow> prefabs, int level, Random rng)
        {
            List<GameData.ItemPrefabRow> fitting = prefabs.Where(p => level >= p.MinLevel && level <= p.MaxLevel).ToList();
            if (fitting.Count > 0)
                return fitting[rng.Next(fitting.Count)];
            return prefabs.OrderBy(p => Math.Abs(p.MinLevel - level)).FirstOrDefault();
        }

        /// <summary>
        /// A model variant for any equipment id, from the prefab table of its own kind (weapon,
        /// armor or accessory), that exists at the given level. The catalog/special rows carry no
        /// model variant of their own, but equipment needs a real one or the bag tile renders blank
        /// (ItemType.IsModelIndex). Deterministic (lowest variant) so a mailed item never changes look.
        /// </summary>
        public static int PrefabFor(GameData data, int itemId, int level)
        {
            IReadOnlyList<GameData.ItemPrefabRow> prefabs =
                data.ItemEquipSlots.GetValueOrDefault(itemId) is 7 or 8 ? data.WeaponPrefabs :
                data.ItemEquipSlots.GetValueOrDefault(itemId) is 2 or 5 ? data.AccessoryPrefabs :
                data.ArmorPrefabs;
            GameData.ItemPrefabRow best = prefabs
                .Where(p => level >= p.MinLevel && level <= p.MaxLevel)
                .OrderBy(p => p.PrefabId)
                .DefaultIfEmpty(prefabs.OrderBy(p => Math.Abs(p.MinLevel - level)).FirstOrDefault())
                .FirstOrDefault();
            return best?.PrefabId ?? 1;
        }

        private static T PickWeighted<T>(IReadOnlyList<T> items, Func<T, int> weight, Random rng) where T : class
        {
            int total = items.Sum(i => Math.Max(0, weight(i)));
            if (total <= 0)
                return items.Count > 0 ? items[rng.Next(items.Count)] : null;

            int roll = rng.Next(total);
            foreach (T item in items)
            {
                roll -= Math.Max(0, weight(item));
                if (roll < 0)
                    return item;
            }
            return items[^1];
        }

        private static int PickIndex(int[] weights, Random rng)
        {
            int total = weights.Sum();
            if (total <= 0)
                return -1;

            int roll = rng.Next(total);
            for (int i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll < 0)
                    return i;
            }
            return weights.Length - 1;
        }
    }
}
