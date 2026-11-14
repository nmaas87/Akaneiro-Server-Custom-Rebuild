using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// One live session per account, and the protocol's reconnect handshake. A dropped connection
/// the server has not noticed yet keeps the old session alive; a second login on the same
/// account then runs UserReconnectNotify -> UserReconnect(true|false). Either answer evicts the
/// old session and reloads the account from disk, so the old session's periodic whole-file
/// saves can never roll back the new one's progress.
/// </summary>
public sealed class ReconnectTests
{
    private readonly ITestOutputHelper _output;

    public ReconnectTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SecondLoginOnALiveAccountAsksInsteadOfDoublingUp()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-dupe-ask");
        using var second = new TestPlayer(first.World, _output);

        second.SendLogin("acct-dupe-ask");

        var notify = second.Take("On_UserReconnectNotify");
        Assert.Equal(first.Character.Name, notify.Arg<string>(0)); // the client shows this name
        Assert.False(second.Has("On_UserLoginOK"), "the second session must not log in yet");

        // Ownership stays with the first session until the client answers.
        Assert.Same(first.Session, first.World.OwnerOf("acct-dupe-ask"));
    }

    [Fact]
    public void ReconnectAnswerFalseEvictsTheOldSessionAndLogsInFresh()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-dupe-false");
        using var second = new TestPlayer(first.World, _output);

        second.SendLogin("acct-dupe-false");
        second.Take("On_UserReconnectNotify");
        second.Send(ProtocolGame_SendRequest.UserReconnect(false)); // what shipped clients answer

        Assert.False(second.Take("On_UserLoginOK").Arg<bool>(0), "answer false is a fresh login");
        Assert.True(first.Closed, "the zombie's connection must be closed");
        Assert.True(first.Session.Superseded, "the zombie must not save again");
        Assert.Same(second.Session, first.World.OwnerOf("acct-dupe-false"));
    }

    [Fact]
    public void ReconnectAnswerTrueReloadsAndMarksTheLoginAsResumed()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-dupe-true");
        using var second = new TestPlayer(first.World, _output);

        second.SendLogin("acct-dupe-true");
        second.Take("On_UserReconnectNotify");
        second.Send(ProtocolGame_SendRequest.UserReconnect(true));

        Assert.True(second.Take("On_UserLoginOK").Arg<bool>(0), "the client asked to resume");
        Assert.True(first.Closed);
        Assert.Same(second.Session, first.World.OwnerOf("acct-dupe-true"));
    }

    [Fact]
    public void TheEvictedSessionCannotRollBackTheNewOnesProgress()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-dupe-clobber");

        first.Session.Account.Karma = 5_000;
        first.Session.SaveAccount(); // progress on disk

        using var second = new TestPlayer(first.World, _output);
        second.SendLogin("acct-dupe-clobber");
        second.Take("On_UserReconnectNotify");
        second.Send(ProtocolGame_SendRequest.UserReconnect(false));
        second.Take("On_UserLoginOK");

        Assert.Equal(5_000, second.Session.Account.Karma); // the new session loads the old one's save

        second.Session.Account.Karma = 100;
        second.Session.SaveAccount();

        // The zombie's save timer firing late must change nothing.
        first.Session.SaveAccount();

        // a superseded session may not write its stale copy back
        Assert.Equal(100, first.World.Accounts.LoadOrCreate("acct-dupe-clobber").Karma);
    }

    [Fact]
    public void AReconnectAnswerWithoutAHandshakeIsIgnoredNotCrashed()
    {
        using var player = new TestPlayer(_output);

        player.Send(ProtocolGame_SendRequest.UserReconnect(false));
        player.Wait(0.2); // an exception here would surface as a logged error

        Assert.False(player.Has("On_UserLoginOK"), "a stray answer must not log anybody in");

        player.LoginNewCharacter(); // the session keeps working normally (this takes UserLoginOK)
        Assert.NotNull(player.Selected);
    }

    [Fact]
    public void LogoutFreesTheAccountForTheNextLogin()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-logout");
        first.Send(ProtocolGame_SendRequest.UserLogout());

        using var second = new TestPlayer(first.World, _output);
        second.SendLogin("acct-logout");

        Assert.False(second.Take("On_UserLoginOK").Arg<bool>(0));
        Assert.False(second.Has("On_UserReconnectNotify"), "no handshake after a clean logout");
        Assert.Same(second.Session, second.World.OwnerOf("acct-logout"));
    }

    [Fact]
    public void ReloadMapLoadsEveryNamedMapLikeTheTestModeClientExpects()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.ClearInbox();

        var maps = new vectorString();
        maps.Add("Hub_Village_Tutorial");
        maps.Add("Hub_Village");
        player.Send(ProtocolGame_SendRequest.ReloadMap(maps));

        Assert.Equal("Hub_Village_Tutorial", player.Take("On_EnterSceneOk").Arg<string>(0));
        Assert.Equal("Hub_Village", player.Take("On_EnterSceneOk").Arg<string>(0));
        Assert.Equal("Hub_Village", player.Session.SceneName);
    }

    [Fact]
    public void FriendRewardIsAnsweredWithAnEmptyRewardNotSilence()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.ClearInbox();

        player.Send(ProtocolGame_SendRequest.ProcessFriendReward());

        // No friends on a single-player server: the client waits for this answer before
        // it clears the reward screen; silence would leave the modal stuck.
        player.Take("On_SendFriendRewardInfo");
    }

    [Fact]
    public void DeletingMailBeforeChoosingACharacterHarmlesslyDoesNothing()
    {
        using var player = new TestPlayer(_output);

        player.SendLogin("acct-nomail");
        player.Take("On_UserLoginOK"); // logged in, no character selected
        player.Send(ProtocolGame_SendRequest.DeleteMail(1));
        player.Wait(0.2); // an NRE here would surface as a logged error

        Assert.False(player.Has("On_DeleteMailResult"), "there is no mailbox without a character");
    }
}
