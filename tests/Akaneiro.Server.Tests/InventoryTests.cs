using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Protocol;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Bag, equipment and stash as the client's inventory screen uses them.</summary>
public sealed class InventoryTests
{
    private const byte Equip = 1, Bag = 2, Stash = 4;
    private const int Katana = 1001, Nodachi = 1005, SmallHealthPotion = 20058;

    private readonly ITestOutputHelper _output;

    public InventoryTests(ITestOutputHelper output) => _output = output;

    private TestPlayer InVillage()
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.ClearInbox();
        return player;
    }

    private static ItemData Give(TestPlayer player, int itemId, int slot, int level = 1, int count = 1)
    {
        var item = new ItemData { ItemId = itemId, PrefabId = 0, Level = level, Slot = slot, Count = count };
        player.Session.Character.Bag.Add(item);
        return item;
    }

    private static ItemData Worn(TestPlayer player, int part) => player.Session.Character.Equipment.SingleOrDefault(e => e.Slot == part);

    [Fact]
    public void EquippingSwapsWithTheWornItem()
    {
        using var player = InVillage();
        player.Session.Character.Equipment.RemoveAll(e => e.Slot == Inventory.PartOffHand);
        ItemData before = Worn(player, Inventory.PartMainHand);
        ItemData katana = Give(player, Katana, 3);

        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartMainHand, Bag, 3));

        var swap = player.Take("On_NotifyItemSwap");
        Assert.Equal(new object[] { Bag, 3u, Equip, (uint)Inventory.PartMainHand }, swap.Args);
        Assert.Same(katana, Worn(player, Inventory.PartMainHand));
        Assert.Equal(3, before.Slot);
        Assert.Contains(before, player.Session.Character.Bag);
        // Gear changes the attributes, but must not refill health.
        Assert.True(player.Has("On_ChangeCharacterAttribNTF"));
    }

    [Fact]
    public void WithBothHandsFullTheNewWeaponReplacesTheOlderOne()
    {
        using var player = InVillage();
        var c = player.Session.Character;
        c.Equipment.RemoveAll(e => e.Slot is Inventory.PartMainHand or Inventory.PartOffHand);
        var oldMain = new ItemData { ItemId = Katana, Slot = Inventory.PartMainHand };
        var oldOff = new ItemData { ItemId = Katana, Slot = Inventory.PartOffHand };
        c.Equipment.Add(oldMain);
        c.Equipment.Add(oldOff);
        ItemData first = Give(player, Katana, 3);
        ItemData second = Give(player, Katana, 4);

        // The client always asks for the off hand (InventoryCtrl.EquipItem).
        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartOffHand, Bag, 3));
        Assert.Equal(new object[] { Bag, 3u, Equip, (uint)Inventory.PartMainHand }, player.Take("On_NotifyItemSwap").Args);
        Assert.Same(first, Worn(player, Inventory.PartMainHand));

        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartOffHand, Bag, 4));
        Assert.Equal(new object[] { Bag, 4u, Equip, (uint)Inventory.PartOffHand }, player.Take("On_NotifyItemSwap").Args);
        Assert.Same(second, Worn(player, Inventory.PartOffHand));
    }

    [Fact]
    public void DroppingAStackOnTheSameItemJoinsThem()
    {
        // Potions of different origin (shop, day reward, loot) carry different model numbers.
        using var player = InVillage();
        var c = player.Session.Character;
        c.StashTabs = Math.Max(1, c.StashTabs);
        var inStash = new ItemData { ItemId = SmallHealthPotion, PrefabId = 1, Slot = 0, Count = 3 };
        c.Stash.Add(inStash);
        ItemData inBag = Give(player, SmallHealthPotion, 2, count: 4);

        player.Send(ProtocolGame_SendRequest.SwapItem(Bag, 2, Stash, 0));

        Assert.Equal(7, inStash.Count);
        Assert.DoesNotContain(inBag, c.Bag);
        player.Take("On_NotifyItemDestroy");
        Assert.False(player.Has("On_NotifyItemSwap"));
    }

    [Fact]
    public void ItemsAboveTheCharactersLevelCannotBeWorn()
    {
        using var player = InVillage();
        Give(player, Katana, 0, level: 5);

        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartMainHand, Bag, 0));

        Assert.Equal(EServerErrorType.eItemError_PlayerLvlNotEnough, player.Take<EServerErrorType>("On_EquipErrorFailed").Get());
        Assert.False(player.Has("On_NotifyItemSwap"));
    }

    [Fact]
    public void ItemsOnlyGoOnTheirPart()
    {
        using var player = InVillage();
        Give(player, Katana, 0);

        player.Send(ProtocolGame_SendRequest.EquipItem(0, Bag, 0)); // a katana is no helmet

        Assert.Equal(EServerErrorType.eItemError_CantBeEquiped, player.Take<EServerErrorType>("On_EquipErrorFailed").Get());
    }

    [Fact]
    public void TwoHandedWeaponsNeedAFreeOffHand()
    {
        using var player = InVillage();
        player.Session.Character.Equipment.Add(new ItemData { ItemId = Katana, Slot = Inventory.PartOffHand });
        Give(player, Nodachi, 0);

        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartMainHand, Bag, 0));
        Assert.True(player.Has("On_EquipErrorFailed"));

        // The client takes the off-hand weapon off first, then it works.
        player.Send(ProtocolGame_SendRequest.UnequipItem(Inventory.PartOffHand, Bag, 5));
        player.Send(ProtocolGame_SendRequest.EquipItem(Inventory.PartMainHand, Bag, 0));
        Assert.Equal(Nodachi, Worn(player, Inventory.PartMainHand).ItemId);
        Assert.Null(Worn(player, Inventory.PartOffHand));
    }

    [Fact]
    public void UnequippingNeedsAnEmptyBagSlot()
    {
        using var player = InVillage();
        ItemData head = Worn(player, 0);
        Assert.NotNull(head);
        Give(player, SmallHealthPotion, 0);

        player.Send(ProtocolGame_SendRequest.UnequipItem(0, Bag, 0));
        Assert.True(player.Has("On_UnEquipErrorFailed"));

        player.Send(ProtocolGame_SendRequest.UnequipItem(0, Bag, 1));
        var swap = player.Take("On_NotifyItemSwap");
        Assert.Equal(new object[] { Equip, 0u, Bag, 1u }, swap.Args);
        Assert.Null(Worn(player, 0));
        Assert.Equal(1, head.Slot);
    }

    [Fact]
    public void ItemsMoveBetweenBagAndStash()
    {
        using var player = InVillage();
        ItemData potion = Give(player, SmallHealthPotion, 2, count: 5);
        ItemData katana = Give(player, Katana, 4);

        player.Send(ProtocolGame_SendRequest.SwapItem(Bag, 2, Stash, 7));
        Assert.Equal(new object[] { Bag, 2u, Stash, 7u }, player.Take("On_NotifyItemSwap").Args);
        Assert.Contains(potion, player.Session.Character.Stash);

        player.Send(ProtocolGame_SendRequest.SwapItem(Bag, 4, Bag, 0));
        Assert.Equal(0, katana.Slot);

        // One tab holds 12 slots; the second tab has to be bought first.
        player.Send(ProtocolGame_SendRequest.SwapItem(Bag, 0, Stash, 12));
        Assert.True(player.Has("On_SwapItemFailed"));
    }

    [Fact]
    public void TheStashReportsTwelveSlotsPerTab()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        // capacity / 12 is the number of tabs the client offers (PlayerDataManager.SetStashMaxTab).
        Assert.Equal(12u, player.Bags.stashbag.capacity);
    }

    [Fact]
    public void DeletingEmptiesTheSlot()
    {
        using var player = InVillage();
        Give(player, Katana, 6);
        int karma = player.Session.Account.Karma;

        player.Send(ProtocolGame_SendRequest.DelItem(Bag, 6));

        Assert.Equal(new object[] { Bag, 6u }, player.Take("On_NotifyItemDestroy").Args);
        Assert.Empty(player.Session.Character.Bag);
        Assert.Equal(karma, player.Session.Account.Karma); // modern DelItem is a plain discard
    }

    [Fact]
    public void AHealthPotionHealsAndUsesUpOneOfTheStack()
    {
        using var player = InVillage();
        player.Session.CurrentHp = 100;
        ItemData potions = Give(player, SmallHealthPotion, 0, count: 3);

        player.Send(ProtocolGame_SendRequest.useItem(Bag, 0));

        Assert.Equal(2, potions.Count);
        Assert.Equal(2u, player.Take<SItemInfo>("On_NotifyItemUpdate", 1).count);
        var effect = player.Take<vectorStatusEffects>("On_UpdateStatusEffect").Cast<SStatusEffect>().Single();
        Assert.Equal(75705, effect.statustID); // "Small Health Potion" buff
        // 1500 health, but never above the maximum (500 on level 1).
        int maxHp = player.Session.World.Data.Level(1).MaxHp;
        var healed = effect.attribueVec.Cast<SAttributeChange>().Single(a => a.attributeType.Get() == EAttributeType.ATTR_CurHP);
        Assert.Equal(Math.Min(1500, maxHp - 100), healed.value);
        Assert.Equal(Math.Min(1600, maxHp), player.Session.CurrentHp);
    }

    [Fact]
    public void TheLastPotionEmptiesItsSlot()
    {
        using var player = InVillage();
        Give(player, SmallHealthPotion, 4);

        player.Send(ProtocolGame_SendRequest.useItem(Bag, 4));

        Assert.Equal(new object[] { Bag, 4u }, player.Take("On_NotifyItemDestroy").Args);
    }

    [Fact]
    public void PickedUpFoodStacksAndGearTakesTheNextFreeVisibleSlot()
    {
        using var player = InVillage();
        ItemData stack = Give(player, SmallHealthPotion, 0, count: 98);

        Assert.True(player.Session.Inventory.AddToBag(new ItemData { ItemId = SmallHealthPotion, Count = 3 }));

        Assert.Equal(99, stack.Count);
        Assert.Equal(2, player.Session.Character.Bag.Single(i => i.Slot == 1).Count);
        for (int slot = 2; slot < Inventory.BagSlots; slot++)
            Give(player, Katana, slot);
        Assert.False(player.Session.Inventory.AddToBag(new ItemData { ItemId = Katana }));
        Assert.DoesNotContain(player.Session.Character.Bag, i => i.Slot >= Inventory.BagSlots);
    }
}
