using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

public sealed class LoginTests
{
    private readonly ITestOutputHelper _output;

    public LoginTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void NewCharacterStartsWithGearAndThreeAbilities()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        Assert.True(player.Bags.equipbag.iteminfos.Count >= 4, $"{player.Bags.equipbag.iteminfos.Count} items equipped");
        Assert.Equal(3, player.Skills.skillInfoVec.Count);
        Assert.Equal(3, player.Skills.skillShortcutVec.Count);
    }

    [Theory]
    [InlineData(ESex.eSex_Male)]
    [InlineData(ESex.eSex_Female)]
    public void CharacterKeepsTheChosenSex(int sex)
    {
        using var player = new TestPlayer(_output).LoginNewCharacter(sex);

        Assert.Equal(sex, player.Selected.sex.Get());
    }

    [Fact]
    public void LoginListsTheCharactersOfTheAccount()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        Send(player, ProtocolGame_SendRequest.UserLogin(player.Session.Account.Account, new EUserType(EUserType.eUserType_Steam),
            new EClientType(EClientType.eClientType_Standalone_Win), "", "token"));

        var characters = player.Take("On_UserLoginOK").Arg<vectorCharacterInfoLogins>(2);
        Assert.Single(characters);
    }

    [Fact]
    public void EnteringASceneSpawnsThePlayerAlive()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        var start = player.EnterScene("Hub_Village");

        Assert.Equal(1, start.objectID); // the client hardcodes 1 for its own avatar
        var health = start.attrVec.Cast<SAttributeChange>().First(a => a.attributeType.Get() == EAttributeType.ATTR_CurHP);
        Assert.True(health.value > 0);
    }

    [Fact]
    public void VillageEntryChainAnswersEveryStep()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        // The client only returns to the game screen after the last answer of this chain. Leaving
        // the loading screen it asks for the day reward and the friend reward at once
        // (LoadingScreenCtrl.ExitLoadScreen); with a day reward on screen, its "Next" asks again.
        Send(player, ProtocolGame_SendRequest.RequestDayReward());
        Send(player, ProtocolGame_SendRequest.GetFriendReward());
        player.Take("On_RequestDayRewardOK");
        Assert.False(player.Has("On_SendFriendRewardInfo"), "answered while the day reward is still on screen");
        Send(player, ProtocolGame_SendRequest.GetFriendReward());
        player.Take("On_SendFriendRewardInfo");
        Send(player, ProtocolGame_SendRequest.hasAssignedTalentPointReq());
        player.Take("On_hasAssignedTalentPointAck");
    }

    [Fact]
    public void TheFriendRewardIsAnsweredOncePerVisit()
    {
        // Day reward taken already: the client asks once after loading and again on code 109. A
        // second answer would close the mission summary the first one opened.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Session.Account.DayRewardLastDate = player.Clock.UtcNow.Year * 10000 + player.Clock.UtcNow.Month * 100 + player.Clock.UtcNow.Day;
        player.EnterScene("Hub_Village");
        player.ClearInbox();

        Send(player, ProtocolGame_SendRequest.RequestDayReward());
        Send(player, ProtocolGame_SendRequest.GetFriendReward());
        Send(player, ProtocolGame_SendRequest.GetFriendReward());

        Assert.Single(player.TakeAll("On_SendFriendRewardInfo"));
    }

    [Fact]
    public void PingIsAnswered()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        Send(player, ProtocolGame_SendRequest.pingRequest());

        player.Take("On_pingRequestAck");
    }

    [Fact]
    public void ANameAnotherPlayerAlreadyCarriesIsRefusedToEveryone()
    {
        // The first account takes the name; the door checks EVERY stored account, case-insensitively,
        // so nobody - not even the original holder on another character slot - can take it twice.
        using var owner = new TestPlayer(_output).LoginNewCharacter();
        string taken = owner.Character.Name;

        using var other = new TestPlayer(owner.World, _output);
        other.SendLogin("76561190000009001");
        other.Take("On_UserLoginOK");

        other.Send(ProtocolGame_SendRequest.UserCreateCharacter(new SCharacterCreationData
        {
            nickname = taken.ToUpperInvariant(), // same name, different case
            style = 4,
            avatarTraits = new mapIntInt(),
            sex = new ESex(ESex.eSex_Male),
        }));

        var error = other.Take<EServerErrorType>("On_UserCreateCharacterFailed", 0);
        Assert.Equal(EServerErrorType.eUnknownError, error.Get());
        Assert.False(other.Has("On_UserCreateCharacterOK"));
        Assert.Empty(other.Session.Account.Characters); // nothing was half-created on that account either
        Assert.Single(owner.Session.Account.Characters);
    }

    private static void Send(TestPlayer player, Packet request) => player.Send(request);
}
