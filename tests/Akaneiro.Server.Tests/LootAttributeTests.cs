using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;

namespace Akaneiro.Server.Tests;

/// <summary>Enchantments, elements, gems and materials on loot; what chests hold.</summary>
public sealed class LootAttributeTests
{
    private static List<ItemData> RollEquipment(int level, int count)
    {
        var rng = new Random(7);
        return Enumerable.Range(0, count).Select(_ => Loot.RollEquipment(TestData.Data, level, rng)).Where(i => i != null).ToList();
    }

    [Fact]
    public void DroppedEquipmentSometimesCarriesAnEnchantmentAnElementOrAGem()
    {
        var items = RollEquipment(level: 1, count: 1000);

        // Level 1 (*.stat): 30 none, 42 enchantment, 33 gem, 19 element - at most one of them.
        Assert.All(items, i => Assert.True((i.Enchant != 0 ? 1 : 0) + (i.Gem != 0 ? 1 : 0) + (i.Element != 0 ? 1 : 0) <= 1));
        Assert.InRange(items.Count(i => i.Enchant == 0 && i.Gem == 0 && i.Element == 0), 150, 350);
        Assert.Contains(items, i => i.Enchant != 0);
        Assert.Contains(items, i => i.Gem != 0);
        Assert.Contains(items, i => i.Element != 0);
    }

    [Fact]
    public void Level1LootOnlyGetsTheWeakestAttributes()
    {
        var items = RollEquipment(level: 1, count: 1000);

        // "Raging II" and higher need item level 3 (Min column); gems of rank IV never drop.
        Assert.All(items.Where(i => i.Enchant != 0), i => Assert.Equal(1, i.Enchant % 100));
        Assert.All(items.Where(i => i.Gem != 0), i => Assert.NotEqual(4, i.Gem % 100));
    }

    [Fact]
    public void MaterialsAreCraftingIngredients()
    {
        var rng = new Random(3);
        var ids = Enumerable.Range(0, 500).Select(_ => Loot.RollMaterial(TestData.Data, rng).ItemId).ToHashSet();

        Assert.All(ids, id => Assert.True(id is >= 6001 and <= 6009 or >= 7001 and <= 7009 or >= 8001 and <= 8004 or 9001, $"{id}"));
        Assert.Contains(ids, id => id / 1000 == 6);
        Assert.Contains(ids, id => id / 1000 == 7);
    }

    [Fact]
    public void ChestsHoldLoot()
    {
        var chest = TestData.Data.Breakables["A1_M4"].First(b => b.Name == "GAM_Chest_Treasure_A1");
        Assert.True(chest.Loot?.CanDropLoot);

        var rng = new Random(5);
        var drops = Enumerable.Range(0, 50).SelectMany(_ => Loot.Roll(chest, TestData.Data, rng)).ToList();

        Assert.NotEmpty(drops);
    }
}

/// <summary>Several items dropping at once reach the client one after the other (name labels).</summary>
public sealed class DropStaggerTests
{
    [Fact]
    public void ItemsFromOneKillArriveSpacedOut()
    {
        using var player = new Akaneiro.Server.Tests.Harness.TestPlayer().LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.ClearInbox();
        var drop = typeof(BattleHandler).GetMethod("DropItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var loot = Enumerable.Range(0, 3).Select(_ => new ItemData { ItemId = 6001, Count = 1 }).ToList();

        drop.Invoke(player.Session.Battle, new object[] { loot, new UnityEngine.Vector3(0, 0, 0) });

        Assert.Single(player.TakeAll("On_ItemEnter"));
        player.Wait(BattleHandler.DropGapSeconds + 0.05);
        Assert.Single(player.TakeAll("On_ItemEnter"));
        player.Wait(BattleHandler.DropGapSeconds + 0.05);
        Assert.Single(player.TakeAll("On_ItemEnter"));
    }
}
