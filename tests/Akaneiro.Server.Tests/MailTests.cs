using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The mailbox. The client asks GetPlayerMail every minute and when the mail button is tapped
/// (UI_Hud_OtherBTN_Manager), opens letters without expecting an answer, and claims attachments
/// by their index inside the mail (UI_Mailbox_DetailManager). Accept-All (AcceptAllBTNClicked)
/// sends GetMailAll only - no separate money requests - so that call has to bank the money too.
/// </summary>
public sealed class MailTests
{
    private readonly ITestOutputHelper _output;

    public MailTests(ITestOutputHelper output) => _output = output;

    private static vectorMailInfos Mailbox(TestPlayer player)
    {
        player.Send(ProtocolGame_SendRequest.GetPlayerMail());
        return player.Take<vectorMailInfos>("On_GetPlayerMailResult");
    }

    private static SMailInfo Mail(TestPlayer player, string title) =>
        Assert.Single(Mailbox(player).Cast<SMailInfo>().Where(m => m.title == title));

    private static SMailInfo KickstarterMail(TestPlayer player) => Mail(player, "Kickstarter Exclusives");
    private static SMailInfo CatalogMail(TestPlayer player) => Mail(player, "The Complete Rare Catalog");

    [Fact]
    public void EveryNewCharacterIsMailedTheWelcomeLetters()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        Assert.Equal(3, Mailbox(player).Count); // all three letters arrive, once

        SMailInfo kickstarter = KickstarterMail(player);
        Assert.Equal("Spicy Horse", kickstarter.senderName);
        Assert.Equal("The Order of Akane thanks you for your contribution through Kickstarter", kickstarter.content);
        Assert.Equal(EMailType.eMailType_System, kickstarter.type.Get());
        Assert.Equal(EMailState.eMailState_Unread, kickstarter.state.Get());
        Assert.Equal(0, kickstarter.timeout); // MailSystem prints "Forever" for 0

        SMailInfo starterPack = Mail(player, "Starter Pack Gifts");
        Assert.Equal("Spicy Horse", starterPack.senderName);
        Assert.Equal("The Order of Akane thanks you for your contribution through Steam Early Access", starterPack.content);
        Assert.Equal(0, starterPack.timeout);
    }

    [Fact]
    public void TheKickstarterMailCarriesTheFourRewardItems()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        SMailInfo mail = KickstarterMail(player);
        // uniqueItem.rare rows 9000/9005/9010/9015, mapped to the real item ids they sell
        // (Props.General type ids) with the row's prefab/gem/enchant/level - the client
        // resolves every attachment through ItemDeployInfo.GetItemObject, which only knows
        // Props.General ids; the catalog row number itself would render as a null item.
        var expected = new[] { (2010, 4, 905, 800), (1002, 6, 1005, 800), (3001, 8, 1105, 800), (2001, 10, 805, 821) };
        Assert.Equal(expected.Length, mail.itemVec.Count);
        int index = 0;
        foreach (SItemInfo item in mail.itemVec.Cast<SItemInfo>())
        {
            Assert.Equal(expected[index].Item1, item.ID);
            Assert.Equal((uint)expected[index].Item2, item.level);
            Assert.Equal(expected[index].Item3, item.gem);
            Assert.Equal(expected[index].Item4, item.enchant);
            Assert.Equal((uint)index, item.slot); // the client addresses attachments by this index
            index++;
        }
    }

    [Fact]
    public void TheStarterPackCarriesTheEarlyAccessRewards()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        SMailInfo mail = Mail(player, "Starter Pack Gifts");
        Assert.Equal(25000, mail.karma);
        Assert.Equal(60, mail.crystal);

        // Foods (stacks of 3) and the scroll pack are plain Props.General consumables - their
        // "prefab" is the client's icon index 1 (index 0 is the world model, which has no icon
        // texture); the necklace and the katana are uniqueItem.rare rows 9500/9501 mapped to real
        // ids with their real model variants.
        var expected = new[]
        {
            (id: 20009, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Sea Urchin Sushi
            (id: 20010, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Kagami Mochi
            (id: 20014, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Dorayaki
            (id: 20015, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Fugu Tempura
            (id: 20016, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Sukiyaki
            (id: 20017, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Shochu
            (id: 20018, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Awamori
            (id: 20019, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Hojicha Tea
            (id: 20020, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 3),   // Chrysanthemum Tea
            (id: 20103, prefab: 1, level: 1, gem: 0, enchant: 0, element: 0, count: 1),   // 20 Shinigami Scrolls
            (id: 3002, prefab: 6, level: 1, gem: 101, enchant: 0, element: 0, count: 1),  // Steam Necklace
            (id: 1001, prefab: 9, level: 5, gem: 101, enchant: 103, element: 303, count: 1), // Raging Steam Blast Katana
        };
        var sent = mail.itemVec.Cast<SItemInfo>().ToList();
        Assert.Equal(expected.Length, sent.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            var want = expected[i];
            SItemInfo got = sent[i];
            Assert.Equal(want.id, got.ID);
            Assert.Equal(want.prefab, got.perfrab);
            Assert.Equal((uint)want.level, got.level);
            Assert.Equal(want.gem, got.gem);
            Assert.Equal(want.enchant, got.enchant);
            Assert.Equal(want.element, got.element);
            Assert.Equal((uint)want.count, got.count);
            Assert.Equal((uint)i, got.slot);
        }
    }

    [Fact]
    public void TheCompleteRareCatalogLetterCarriesEveryUniqueOnce()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        SMailInfo letter = CatalogMail(player);

        // Every distinct catalogued unique part (the 120 uniqueItem.rare rows fold to 116, the
        // hidden rows included) plus every named special of Props 4001-4062 (19) - Amaterasu's
        // Sword among them, which never stood on any shop shelf.
        Assert.Equal(116 + 19, letter.itemVec.Count);
        Assert.Equal("Spicy Horse", letter.senderName);
        Assert.Equal(0, letter.timeout); // "Forever"
        Assert.Contains(letter.itemVec.Cast<SItemInfo>(), i => i.ID == 4054 && i.level >= 1 && i.perfrab > 0);
        Assert.Contains(letter.itemVec.Cast<SItemInfo>(), i => i.ID == 1003 && i.enchant == 201 && i.gem == 703);
        Assert.Contains(letter.itemVec.Cast<SItemInfo>(), i => i.ID == 2010 && i.gem == 905 && i.enchant == 800);
        Assert.All(letter.itemVec.Cast<SItemInfo>(), i => Assert.True(i.level >= 1, $"level {i.level}"));
        Assert.All(letter.itemVec.Cast<SItemInfo>(), i => Assert.True(i.ID >= 1001, $"id {i.ID} is no equipment"));
        int slot = 0;
        foreach (SItemInfo item in letter.itemVec.Cast<SItemInfo>())
            Assert.Equal((uint)slot++, item.slot); // the client addresses attachments by index
    }

    [Fact]
    public void AUniqueFromTheCatalogLetterClaimsIntoTheBagWithItsParts()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        SMailInfo letter = CatalogMail(player);
        int slot = letter.itemVec.Cast<SItemInfo>().ToList().FindIndex(i => i.ID == 4054);
        Assert.True(slot >= 0);

        player.Send(ProtocolGame_SendRequest.GetMailItem(letter.id, slot));

        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailItemResult").Arg<EServerErrorType>(0).Get());
        ItemData sword = Assert.Single(player.Session.Character.Bag.Where(i => i.ItemId == 4054));
        Assert.True(sword.PrefabId > 0); // a real model variant, so the bag tile renders
        Assert.Equal(116 + 19 - 1, CatalogMail(player).itemVec.Count); // gone from the letter
    }

    [Fact]
    public void TheWelcomeMailsArriveOnlyOnce()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int characterId = player.Character.Id;
        Assert.Equal(4, KickstarterMail(player).itemVec.Count);
        Assert.Equal(12, Mail(player, "Starter Pack Gifts").itemVec.Count);

        player.ClearInbox();
        Assert.Equal(4, KickstarterMail(player).itemVec.Count); // polling does not add another one

        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(characterId));
        player.Take("On_UserSelectCharacterOK");
        Assert.Equal(3, Mailbox(player).Count); // re-selecting does not either
    }

    [Fact]
    public void LettersAreGrantedAtCharacterCreationOnly()
    {
        // A character that predates the mail feature - no grants recorded - must not be
        // retro-mailed when it is selected.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int characterId = player.Character.Id;
        player.Character.KickstarterMailGranted = false;
        player.Character.StarterPackMailGranted = false;
        player.Character.Mail.Clear();

        player.ClearInbox();
        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(characterId));
        player.Take("On_UserSelectCharacterOK");
        Assert.Empty(Mailbox(player));
    }

    [Fact]
    public void AcceptAllMovesEveryRewardIntoTheBag()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = KickstarterMail(player).id;

        player.Send(ProtocolGame_SendRequest.GetMailAll(mailId));
        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailAllItemsResult").Arg<EServerErrorType>(1).Get());

        Assert.Equal(4, player.Character.Bag.Count);
        Assert.Equal(new[] { 1002, 2001, 2010, 3001 }, player.Character.Bag.Select(i => i.ItemId).OrderBy(id => id));
        // The client was told about each bag slot it now has to draw.
        Assert.Equal(4, player.TakeAll("On_NotifyItemUpdate").Count);
    }

    [Fact]
    public void AcceptAllAlsoBanksTheMoneyAttachment()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        SMailInfo pack = Mail(player, "Starter Pack Gifts");
        int karma = player.Session.Account.Karma, crystals = player.Session.Account.Crystals;

        // The client's Accept-All button sends GetMailAll only - no GetMailKarma/GetMailCrystal.
        player.Send(ProtocolGame_SendRequest.GetMailAll(pack.id));
        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailAllItemsResult").Arg<EServerErrorType>(1).Get());

        Assert.Equal(karma + 25000, player.Session.Account.Karma);
        Assert.Equal(crystals + 60, player.Session.Account.Crystals);
        player.Take("On_UpdateMoneyInfo"); // the wallet update the client needs to see
        Assert.Empty(Mail(player, "Starter Pack Gifts").itemVec);

        // The money is spent, not doubled: taking it again is refused.
        player.Send(ProtocolGame_SendRequest.GetMailKarma(pack.id));
        Assert.Equal(EServerErrorType.eMailError_MailParamError,
            player.Take("On_GetMailKarmaResult").Arg<EServerErrorType>(1).Get());
    }

    [Fact]
    public void OneItemAtATimeRenumbersWhatIsLeft()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        SMailInfo mail = KickstarterMail(player);

        player.Send(ProtocolGame_SendRequest.GetMailItem(mail.id, 1)); // the axe
        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailItemResult").Arg<EServerErrorType>(0).Get());
        Assert.Equal(1002, Assert.Single(player.Character.Bag).ItemId);

        // The client shifts its remaining icons down; the mailbox has to agree.
        SMailInfo after = KickstarterMail(player);
        Assert.Equal(3, after.itemVec.Count);
        Assert.Equal(new uint[] { 0, 1, 2 }, after.itemVec.Cast<SItemInfo>().Select(i => i.slot));
        Assert.Equal(new[] { 2010, 3001, 2001 }, after.itemVec.Cast<SItemInfo>().Select(i => i.ID));
    }

    [Fact]
    public void AnUnknownAttachmentIndexIsRefused()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = KickstarterMail(player).id;

        player.Send(ProtocolGame_SendRequest.GetMailItem(mailId, 7));
        Assert.Equal(EServerErrorType.eMailError_MailItemSlotError,
            player.Take("On_GetMailItemResult").Arg<EServerErrorType>(0).Get());
        Assert.Empty(player.Character.Bag);
    }

    [Fact]
    public void ConsumablesCarryTheClientsIconIndexEverywhereTheyAreCreated()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        // Bought at the elder: the client builds its own shop cards as GetItemObject(id, 1, ...),
        // so the server's copy has to match or the bag slot stays pictureless.
        player.Session.Account.Karma = 1000; // the potion costs 250
        player.Send(ProtocolGame_SendRequest.BuyConsumeItem(2, 20058, 1)); // Small Health Potion
        player.Take("On_BuyConsumeItemSuccAck");
        Assert.Equal(1, player.Character.Bag.Single(i => i.ItemId == 20058).PrefabId);

        // Dropped by a monster (Loot.RollConsumable / RollMaterial).
        var rolled = Loot.RollMaterial(player.World.Data, player.World.Random);
        Assert.Equal(1, rolled.PrefabId);

        // Equipment keeps its real model variant: EnsurePrefab never touches it.
        var cloak = new ItemData { ItemId = 2010, PrefabId = 101 };
        Assert.False(player.World.Data.EnsurePrefab(cloak));
        Assert.Equal(101, cloak.PrefabId);
    }

    [Fact]
    public void OldSavesGetTheIconIndexOnTheNextLogin()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        string account = player.Session.Account.Account;
        int id = player.Character.Id;

        // Items saved before the index existed: blank, exactly as in the shipped account files -
        // one in the bag, one still sitting unclaimed in a letter.
        player.Character.Bag.Add(new ItemData { ItemId = 20058, PrefabId = 0, Slot = 5, Count = 2 });
        player.Character.Mail.First(m => m.Title == "Starter Pack Gifts").Items[0].PrefabId = 0;
        player.Session.SaveAccount();

        // A full logout/login reloads the account file, and with it the repair pass.
        player.Send(ProtocolGame_SendRequest.UserLogout());
        player.Session.Character = null;
        player.ClearInbox();
        player.Send(ProtocolGame_SendRequest.UserLogin(account, new EUserType(EUserType.eUserType_Steam),
            new EClientType(EClientType.eClientType_Standalone_Win), "", "token"));
        player.Take("On_UserLoginOK");
        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(id));
        player.Take("On_UserSelectCharacterOK");

        Assert.Equal(1, player.Character.Bag.Single(i => i.ItemId == 20058).PrefabId);
        // The foods and the scroll in the letter get the index; the Kickstarter gear in the other
        // letter keeps its model variant.
        Assert.All(player.Character.Mail.First(m => m.Title == "Starter Pack Gifts").Items.Take(10),
            item => Assert.Equal(1, item.PrefabId));
        Assert.Equal(new[] { 101, 101, 101, 1002 },
            player.Character.Mail.First(m => m.Title == "Kickstarter Exclusives").Items.Select(i => i.PrefabId));
        // Gear keeps its model variant through the same pass.
        Assert.All(player.Character.Equipment, gear => Assert.NotEqual(0, gear.PrefabId));
    }

    [Fact]
    public void OpeningALetterClearsTheUnreadBadge()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = KickstarterMail(player).id;

        // OpenMail has no answer (the client has no result handler for it).
        player.Send(ProtocolGame_SendRequest.OpenMail(mailId));
        Assert.Equal(EMailState.eMailState_Read, KickstarterMail(player).state.Get());
    }

    [Fact]
    public void DeletingAMailRemovesIt()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = KickstarterMail(player).id;

        player.Send(ProtocolGame_SendRequest.DeleteMail(mailId));
        player.Take("On_DeleteMailResult");
        var titles = Mailbox(player).Cast<SMailInfo>().Select(m => m.title).ToList();
        Assert.Equal(2, titles.Count);
        Assert.DoesNotContain("Kickstarter Exclusives", titles);
        Assert.Contains("Starter Pack Gifts", titles);
        Assert.Contains("The Complete Rare Catalog", titles);
    }

    [Fact]
    public void AcceptAllIntoANearlyFullBagKeepsWhatDoesNotFitOnTheLetter()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = KickstarterMail(player).id;
        int karma = player.Session.Account.Karma;

        // One free slot left: 19 junk katanas in a 20-slot bag.
        for (int slot = 0; slot < Inventory.BagSlots - 1; slot++)
            player.Character.Bag.Add(new ItemData { ItemId = 1001, PrefabId = 0, Level = 1, Slot = slot, Count = 1 });

        player.Send(ProtocolGame_SendRequest.GetMailAll(mailId));
        Assert.Equal(EServerErrorType.eItemError_BagFull,
            player.Take("On_GetMailAllItemsResult").Arg<EServerErrorType>(1).Get());

        // One reward made it into the free slot; the rest wait on the letter instead of
        // being silently destroyed.
        Assert.Equal(Inventory.BagSlots, player.Character.Bag.Count);
        Assert.Equal(1, player.Character.Bag.Count(i => i.ItemId != 1001));
        Assert.Equal(3, Mail(player, "Kickstarter Exclusives").itemVec.Count);
        Assert.Equal(karma, player.Session.Account.Karma); // the money comes only with the last item
    }

    [Fact]
    public void TheMailboxSurvivesSelectingTheCharacterAgain()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int characterId = player.Character.Id;
        Assert.Equal(3, Mailbox(player).Count);

        player.Send(ProtocolGame_SendRequest.UserLogout());
        player.Session.Character = null;
        player.ClearInbox();

        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(characterId));
        player.Take("On_UserSelectCharacterOK");
        Assert.Equal(4, KickstarterMail(player).itemVec.Count); // loaded from the account file
    }
}
