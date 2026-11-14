using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>The village vendors: Gato's equipment and the elder's consumables.</summary>
public sealed class ShopTests
{
    private const byte Bag = 2;
    private const int SmallHealthPotion = 20058;

    private readonly ITestOutputHelper _output;

    public ShopTests(ITestOutputHelper output) => _output = output;

    private TestPlayer InVillage(int karma = 100000)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.Session.Account.Karma = karma;
        player.ClearInbox();
        return player;
    }

    private static List<SShopItemInfo> OpenShop(TestPlayer player, out List<SShopItemInfo> rare)
    {
        // What the client sends when Gato is clicked (ShopNpc: SHOPNPC).
        player.Send(ProtocolGame_SendRequest.RequestPlayerShopInfo(false));
        player.Send(ProtocolGame_SendRequest.RequestPlayerShopInfo(true));
        rare = player.Take("On_SendShopRareitemTrackInfo").Arg<vectorShopItemInfos>(1).Cast<SShopItemInfo>().ToList();
        return player.Take("On_SendShopIteminfo").Arg<vectorShopItemInfos>(1).Cast<SShopItemInfo>().ToList();
    }

    private static SBuyitemInfo BuyInfo(SShopItemInfo offer) => new SBuyitemInfo
    {
        UUID = offer.UUID,
        ID = offer.ID,
        perfrab = offer.perfrab,
        level = offer.level,
        count = 1,
        currencyType = 1,
    };

    [Fact]
    public void GatoOffersEquipmentForTheCharactersLevel()
    {
        using var player = InVillage();

        var items = OpenShop(player, out var rare);

        Assert.Equal(Shop.OffersPerStock, items.Count);
        Assert.Equal(Shop.RareOffers, rare.Count);
        Assert.All(items, i => Assert.Equal(1u, i.level));
        Assert.All(items, i => Assert.True(i.price > 0 && i.moneyType == EMoneyType.eMoneyType_SK));
        Assert.Equal(items.Count, items.Select(i => i.UUID).Distinct().Count());
        // Every offer is an item the client can show (weapon, armor or accessory table).
        Assert.All(items, i => Assert.True(player.Session.World.Data.ItemValues.ContainsKey(i.ID), $"item {i.ID}"));
    }

    [Fact]
    public void TheStockStaysForTwelveHours()
    {
        using var player = InVillage();
        var first = OpenShop(player, out _);

        player.Wait(60 * 60 * 11);
        Assert.Equal(first.Select(i => i.UUID * 100000 + i.ID), OpenShop(player, out _).Select(i => i.UUID * 100000 + i.ID));

        player.Wait(60 * 60 * 2);
        player.Session.Character.Shop.Items.Clear(); // proves a new stock replaces the old one
        Assert.Equal(Shop.OffersPerStock, OpenShop(player, out _).Count);
    }

    [Fact]
    public void BuyingPutsTheItemIntoTheBagAndTakesTheKarma()
    {
        using var player = InVillage(karma: 100000);
        SShopItemInfo offer = OpenShop(player, out _).First();

        player.Send(ProtocolGame_SendRequest.BuyItem(false, BuyInfo(offer)));

        Assert.Equal(offer.UUID, player.Take("On_BuyItemSuccAck").Arg<SBuyitemInfo>(1).UUID);
        Assert.Equal(100000 - offer.price, player.Session.Account.Karma);
        Assert.Equal(100000 - offer.price, player.Take<SMoneyInfo>("On_UpdateMoneyInfo").Karma);
        Assert.Contains(player.Session.Character.Bag, i => i.ItemId == offer.ID && i.PrefabId == offer.perfrab);
        Assert.DoesNotContain(OpenShop(player, out _), o => o.UUID == offer.UUID); // sold out
    }

    [Fact]
    public void BuyingNeedsEnoughKarma()
    {
        using var player = InVillage(karma: 0);
        SShopItemInfo offer = OpenShop(player, out _).First();

        player.Send(ProtocolGame_SendRequest.BuyItem(false, BuyInfo(offer)));

        Assert.Equal(EServerErrorType.eItemError_BuyNotEnoughMoneny, player.Take<EServerErrorType>("On_BuyItemFailed").Get());
        Assert.Empty(player.Session.Character.Bag);
    }

    [Fact]
    public void SellingPaysWhatTheTooltipShows()
    {
        using var player = InVillage(karma: 0);
        // A level 3 head armor (base value 12): 3 * 12 * 0.4 = 14 (PlayerDataManager.GetItemValue(2, ...)).
        player.Session.Character.Bag.Add(new ItemData { ItemId = 2001, Level = 3, Slot = 5 });

        player.Send(ProtocolGame_SendRequest.SaleItem(Bag, 5));

        Assert.Equal(new object[] { Bag, 5u }, player.Take("On_NotifyItemDestroy").Args);
        Assert.Equal(14, player.Session.Account.Karma);
        Assert.Empty(player.Session.Character.Bag);
    }

    [Fact]
    public void TheElderSellsPotionsAtTheClientsPrice()
    {
        using var player = InVillage(karma: 1000);
        int price = player.Session.World.Data.Items[SmallHealthPotion].SellPrice; // 250, as the shop card shows

        player.Send(ProtocolGame_SendRequest.BuyConsumeItem(2, SmallHealthPotion, 3));

        Assert.Equal(new object[] { 2, SmallHealthPotion, 3 }, player.Take("On_BuyConsumeItemSuccAck").Args);
        Assert.Equal(1000 - 3 * price, player.Session.Account.Karma);
        Assert.Equal(3, player.Session.Character.Bag.Single(i => i.ItemId == SmallHealthPotion).Count);
    }

    [Fact]
    public void BoughtPotionsStackOntoDroppedOnes()
    {
        using var player = InVillage();
        player.Session.Character.Bag.Add(new ItemData { ItemId = SmallHealthPotion, Level = 6, Count = 2, Slot = 0 });

        player.Send(ProtocolGame_SendRequest.BuyConsumeItem(2, SmallHealthPotion, 1));

        Assert.Equal(3, player.Session.Character.Bag.Single().Count);
    }
}

public sealed class ItemTableTests
{
    private readonly ITestOutputHelper _output;

    public ItemTableTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EquipmentTablesKeepTheirChancesAndDamage()
    {
        // The tables' headers carry trailing spaces ("DropChance ", "Chance ", "DamageRange ");
        // reading them with the untrimmed names once left every equipment chance at 0, so no
        // weapon, armor or accessory ever dropped.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        var data = player.Session.World.Data;

        Assert.True(data.WeaponDrops.Sum(w => w.DropChance) > 0);
        Assert.True(data.ArmorDrops.Sum(a => a.DropChance) > 0);
        Assert.True(data.AccessoryDrops.Sum(a => a.DropChance) > 0);
        Assert.Equal(200, data.Weapons[1001].DamageMin); // Katana 200-320
        Assert.Equal(320, data.Weapons[1001].DamageMax);
        Assert.Equal(12f, data.ItemValues[2001]);        // Head Armor "Value"
    }
}
