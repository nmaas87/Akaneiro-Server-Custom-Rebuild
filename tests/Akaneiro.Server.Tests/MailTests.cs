using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The mailbox. The client asks GetPlayerMail every minute and when the mail button is tapped
/// (UI_Hud_OtherBTN_Manager), opens letters without expecting an answer, and claims attachments
/// by their index inside the mail (UI_Mailbox_DetailManager).
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

    private static SMailInfo OnlyMail(TestPlayer player) => Assert.Single(Mailbox(player).Cast<SMailInfo>());

    [Fact]
    public void EveryNewCharacterIsMailedTheKickstarterExclusives()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        SMailInfo mail = OnlyMail(player);
        Assert.Equal("Kickstarter Exclusives", mail.title);
        Assert.Equal("Spicy Horse", mail.senderName);
        Assert.Equal("The Order of Akane thanks you for your contribution through Kickstarter", mail.content);
        Assert.Equal(EMailType.eMailType_System, mail.type.Get());
        Assert.Equal(EMailState.eMailState_Unread, mail.state.Get());
        Assert.Equal(0, mail.timeout); // MailSystem prints "Forever" for 0
    }

    [Fact]
    public void TheKickstarterMailCarriesTheFourRewardItems()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        SMailInfo mail = OnlyMail(player);
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
    public void TheKickstarterMailArrivesOnlyOnce()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int characterId = player.Character.Id;
        Assert.Equal(4, OnlyMail(player).itemVec.Count);

        player.ClearInbox();
        Assert.Equal(4, OnlyMail(player).itemVec.Count); // polling does not add another one

        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(characterId));
        player.Take("On_UserSelectCharacterOK");
        Assert.Equal(4, OnlyMail(player).itemVec.Count); // re-selecting does not either
    }

    [Fact]
    public void AcceptAllMovesEveryRewardIntoTheBag()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = OnlyMail(player).id;

        player.Send(ProtocolGame_SendRequest.GetMailAll(mailId));
        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailAllItemsResult").Arg<EServerErrorType>(1).Get());

        Assert.Equal(4, player.Character.Bag.Count);
        Assert.Equal(new[] { 1002, 2001, 2010, 3001 }, player.Character.Bag.Select(i => i.ItemId).OrderBy(id => id));
        // The client was told about each bag slot it now has to draw.
        Assert.Equal(4, player.TakeAll("On_NotifyItemUpdate").Count);
    }

    [Fact]
    public void OneItemAtATimeRenumbersWhatIsLeft()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        SMailInfo mail = OnlyMail(player);

        player.Send(ProtocolGame_SendRequest.GetMailItem(mail.id, 1)); // the axe
        Assert.Equal(EServerErrorType.eSuccess,
            player.Take("On_GetMailItemResult").Arg<EServerErrorType>(0).Get());
        Assert.Equal(1002, Assert.Single(player.Character.Bag).ItemId);

        // The client shifts its remaining icons down; the mailbox has to agree.
        SMailInfo after = OnlyMail(player);
        Assert.Equal(3, after.itemVec.Count);
        Assert.Equal(new uint[] { 0, 1, 2 }, after.itemVec.Cast<SItemInfo>().Select(i => i.slot));
        Assert.Equal(new[] { 2010, 3001, 2001 }, after.itemVec.Cast<SItemInfo>().Select(i => i.ID));
    }

    [Fact]
    public void AnUnknownAttachmentIndexIsRefused()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = OnlyMail(player).id;

        player.Send(ProtocolGame_SendRequest.GetMailItem(mailId, 7));
        Assert.Equal(EServerErrorType.eMailError_MailItemSlotError,
            player.Take("On_GetMailItemResult").Arg<EServerErrorType>(0).Get());
        Assert.Empty(player.Character.Bag);
    }

    [Fact]
    public void OpeningALetterClearsTheUnreadBadge()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = OnlyMail(player).id;

        // OpenMail has no answer (the client has no result handler for it).
        player.Send(ProtocolGame_SendRequest.OpenMail(mailId));
        Assert.Equal(EMailState.eMailState_Read, OnlyMail(player).state.Get());
    }

    [Fact]
    public void DeletingAMailRemovesIt()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int mailId = OnlyMail(player).id;

        player.Send(ProtocolGame_SendRequest.DeleteMail(mailId));
        player.Take("On_DeleteMailResult");
        Assert.Empty(Mailbox(player));
    }

    [Fact]
    public void TheMailboxSurvivesSelectingTheCharacterAgain()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        int characterId = player.Character.Id;
        Assert.Single(Mailbox(player));

        player.Send(ProtocolGame_SendRequest.UserLogout());
        player.Session.Character = null;
        player.ClearInbox();

        player.Send(ProtocolGame_SendRequest.UserSelectCharacter(characterId));
        player.Take("On_UserSelectCharacterOK");
        Assert.Equal(4, OnlyMail(player).itemVec.Count); // loaded from the account file
    }
}
