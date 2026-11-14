using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Named uniques: the uniqueItem.rare catalog and Goto's rare shelf that sells them.</summary>
public sealed class UniqueShopTests
{
    private readonly ITestOutputHelper _output;

    public UniqueShopTests(ITestOutputHelper output) => _output = output;

    private TestPlayer InVillage(int karma = 100000)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.Session.Account.Karma = karma;
        player.ClearInbox();
        return player;
    }

    private static List<SShopItemInfo> RareShelf(TestPlayer player)
    {
        player.Send(ProtocolGame_SendRequest.RequestPlayerShopInfo(true));
        return player.Take("On_SendShopRareitemTrackInfo").Arg<vectorShopItemInfos>(1).Cast<SShopItemInfo>().ToList();
    }

    /// <summary>True when the offer carries a whole catalogued row: id, prefab, gem, element, enchant.</summary>
    private static bool MatchesUniqueRow(SShopItemInfo offer, GameData data, out GameData.UniqueShopRow row)
    {
        row = data.UniqueShop.Values.FirstOrDefault(r =>
            r.BaseId == offer.ID && r.PrefabId == offer.perfrab &&
            r.Gem == offer.gem && r.Element == offer.element && r.Enchant == offer.enchant);
        return row != null;
    }

    [Fact]
    public void TheRareCatalogIsLoadedWithItsRealItemIds()
    {
        var data = TestData.Data;

        Assert.Equal(120, data.UniqueShop.Count);
        Assert.Equal(15, data.UniqueShop.Values.Count(r => r.Visible)); // the original shop hid the rest
        // The row id is a catalog number; the item the client resolves is the TypeID column.
        GameData.UniqueShopRow emeraldClub = data.UniqueShop[1080];
        Assert.Equal(1003, emeraldClub.BaseId); // Club
        Assert.Equal(3, emeraldClub.PrefabId);
        Assert.Equal(703, emeraldClub.Gem);     // Malachite
        Assert.Equal(201, emeraldClub.Enchant);
        Assert.Equal(3, emeraldClub.RequiredLevel);
        Assert.True(emeraldClub.Visible);
        // Row 9000 is the Red Cap's Cloak the kickback mail delivers: same row parts, but the
        // original shop never showed it.
        GameData.UniqueShopRow cloak = data.UniqueShop[9000];
        Assert.Equal(2010, cloak.BaseId);       // Cloak
        Assert.Equal(101, cloak.PrefabId);
        Assert.Equal(905, cloak.Gem);
        Assert.Equal(800, cloak.Enchant);
        Assert.False(cloak.Visible);
    }

    [Fact]
    public void UniquesCarryTheirRowPartsAtTheShelfLevel()
    {
        var rng = new Random(11);
        var data = TestData.Data;
        var rolls = Enumerable.Range(0, 300)
            .Select(_ => Loot.RollUniqueShop(data, 30, rng))
            .Where(i => i != null)
            .ToList();

        Assert.All(rolls, i =>
        {
            GameData.UniqueShopRow row = data.UniqueShop.Values.First(r =>
                r.BaseId == i.ItemId && r.PrefabId == i.PrefabId &&
                r.Gem == i.Gem && r.Element == i.Element && r.Enchant == i.Enchant);
            Assert.True(row.Visible);
            Assert.True(row.RequiredLevel <= 30, $"row {row.Id} needs {row.RequiredLevel}");
            Assert.Equal(30, i.Level); // the shelf level, not the row's minimum
            Assert.True(row.Gem != 0 || row.Element != 0 || row.Enchant != 0, $"row {row.Id} carries no magic");
        });
        Assert.True(rolls.Select(i => i.ItemId).Distinct().Count() > 2); // 300 rolls over 15 rows
    }

    [Fact]
    public void SmallShelvesExhaustTheFittingRows()
    {
        var data = TestData.Data;
        var fitting = data.UniqueShop.Values
            .Where(r => r.Visible && r.RequiredLevel <= 3 && data.ItemValues.ContainsKey(r.BaseId))
            .Select(r => r.BaseId)
            .ToList();

        Assert.NotEmpty(fitting);
        var rng = new Random(3);
        Assert.Null(Loot.RollUniqueShop(data, 3, rng, fitting)); // everything that fits was offered
        Assert.NotNull(Loot.RollUniqueShop(data, 30, rng, fitting)); // higher shelves have more to show
    }

    [Fact]
    public void GotoShelfSellsUniquesAtAheadLevels()
    {
        using var player = InVillage();
        var data = player.Session.World.Data;

        var rare = RareShelf(player);
        int level = player.Session.Character.Level + 2;

        Assert.Equal(Shop.RareOffers, rare.Count);
        var uniques = rare.Where(o => MatchesUniqueRow(o, data, out _)).ToList();
        // A fresh character: the catalog fits exactly two rows at the shelf level, and both show up.
        int fitting = data.UniqueShop.Values.Count(r => r.Visible && r.RequiredLevel <= level);
        Assert.Equal(Math.Min(fitting, Shop.RareOffers), uniques.Count);
        Assert.All(rare, i => Assert.Equal((uint)level, i.level));
        Assert.All(rare, i => Assert.Equal(EMoneyType.eMoneyType_SK, i.moneyType));
        Assert.All(rare, i => Assert.Equal((int)(level * data.ItemValues[i.ID]) * 2, i.price));
        Assert.All(uniques, i => Assert.True(i.enchant != 0 || i.gem != 0 || i.element != 0));
        // The catalog never repeats itself on one shelf.
        Assert.Equal(uniques.Count, uniques.Select(i => i.ID).Distinct().Count());
    }

    [Fact]
    public void BuyingARareUniqueDeliversTheRowParts()
    {
        using var player = InVillage();
        var data = player.Session.World.Data;

        SShopItemInfo offer = RareShelf(player).First(o => MatchesUniqueRow(o, data, out _));
        int karma = player.Session.Account.Karma;

        player.Send(ProtocolGame_SendRequest.BuyItem(true, new SBuyitemInfo
        {
            UUID = offer.UUID, ID = offer.ID, perfrab = offer.perfrab, level = offer.level, count = 1, currencyType = 1,
        }));

        Assert.Equal(offer.UUID, player.Take("On_BuyItemSuccAck").Arg<SBuyitemInfo>(1).UUID);
        Assert.Equal(karma - offer.price, player.Session.Account.Karma);
        ItemData bought = player.Session.Character.Bag.First(i => i.ItemId == offer.ID);
        Assert.Equal(offer.perfrab, bought.PrefabId);
        Assert.Equal(offer.gem, bought.Gem);
        Assert.Equal(offer.enchant, bought.Enchant);
        Assert.Equal(offer.element, bought.Element);
        Assert.DoesNotContain(RareShelf(player), o => o.UUID == offer.UUID); // gone from the shelf
    }

    [Fact]
    public void TheNormalShelfStillSellsRolledGear()
    {
        using var player = InVillage();

        player.Send(ProtocolGame_SendRequest.RequestPlayerShopInfo(false));
        var items = player.Take("On_SendShopIteminfo").Arg<vectorShopItemInfos>(1).Cast<SShopItemInfo>().ToList();

        Assert.Equal(Shop.OffersPerStock, items.Count);
        Assert.All(items, i => Assert.True(TestData.Data.ItemValues.ContainsKey(i.ID), $"item {i.ID}"));
        Assert.All(items, i => Assert.Equal(1u, i.level));
        Assert.All(items, i => Assert.Equal((int)(1 * TestData.Data.ItemValues[i.ID]), i.price));
    }
}
