using System;
using System.IO;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The optional "who may play" gate (Akaneiro:LoginAllowlist). The file's ABSENCE changes
/// nothing; once it exists it decides alone who gets past the login door. Web logins present
/// only their numeric uid, so the address behind that uid is what the list must match - the
/// fat client's mail login has to keep working when the host turns the gate on.
/// </summary>
public sealed class AllowlistTests
{
    private readonly ITestOutputHelper _output;

    public AllowlistTests(ITestOutputHelper output) => _output = output;

    private static void TcpWebLogin(TestPlayer player, string uid, string code) =>
        player.Send(ProtocolGame_SendRequest.UserLogin(uid, new EUserType(EUserType.eUserType_SpicyHorse),
            new EClientType(EClientType.eClientType_Standalone_Win), "", code));

    [Fact]
    public void WhileTheFileDoesNotExistAnyoneMayLogin()
    {
        using var player = new TestPlayer(_output);

        player.LoginAs("76561190000000001"); // LoginAs asserts On_UserLoginOK arrived
        Assert.False(player.Has("On_UserLoginFailed"));
    }

    [Fact]
    public void OnceTheFileExistsItDecidesAlone()
    {
        using var listed = new TestPlayer(_output);
        File.WriteAllText(listed.AllowlistPath, "76561190000000001\n# the huntress\nhunter@example.com\n");

        listed.LoginAs("76561190000000001"); // listed: straight through

        // A second account on the same server, not on the list.
        using var stranger = new TestPlayer(listed.World, _output);
        stranger.SendLogin("76561190000000002");
        var error = stranger.Take<EServerErrorType>("On_UserLoginFailed", 0);
        Assert.Equal(EServerErrorType.eWrongAccountOrPwd, error.Get());
        Assert.False(stranger.Has("On_UserLoginOK"));
    }

    [Fact]
    public void AWebLoginPassesOnItsEmailAddressNotItsUid()
    {
        using var player = new TestPlayer(_output, webLogin: true);
        var user = player.World.WebUsers.Create("hunter@example.com", "pw123");
        string code = player.World.WebUsers.IssueToken(user.Uid, player.Clock.UtcNow, TimeSpan.FromHours(12));
        Assert.NotEqual(user.Uid, "hunter@example.com"); // the client really does only send the uid

        File.WriteAllText(player.AllowlistPath, "hunter@example.com\n");

        TcpWebLogin(player, user.Uid, code);
        player.Take("On_UserLoginOK"); // address on the list: the mail login works
    }

    [Fact]
    public void AWebLoginWhoseAddressIsNotListedIsRefusedEvenWithAGameCode()
    {
        using var player = new TestPlayer(_output, webLogin: true);
        var user = player.World.WebUsers.Create("stowaway@example.com", "pw123");
        string code = player.World.WebUsers.IssueToken(user.Uid, player.Clock.UtcNow, TimeSpan.FromHours(12));

        File.WriteAllText(player.AllowlistPath, "hunter@example.com"); // valid code, wrong person

        TcpWebLogin(player, user.Uid, code);
        player.Take("On_UserLoginFailed");
        Assert.False(player.Has("On_UserLoginOK"));
    }

    [Fact]
    public void TheFileIsReReadWhenItChangesWhileTheServerRuns()
    {
        using var first = new TestPlayer(_output, webLogin: true);
        var user = first.World.WebUsers.Create("late@example.com", "pw123");
        string code = first.World.WebUsers.IssueToken(user.Uid, first.Clock.UtcNow, TimeSpan.FromHours(12));
        File.WriteAllText(first.AllowlistPath, "hunter@example.com");

        TcpWebLogin(first, user.Uid, code);
        first.Take("On_UserLoginFailed"); // not on the list yet

        // The host adds the address; no restart, next login already sees it.
        File.AppendAllText(first.AllowlistPath, "\nlate@example.com # added while running\n");
        code = first.World.WebUsers.IssueToken(user.Uid, first.Clock.UtcNow, TimeSpan.FromHours(12));

        using var second = new TestPlayer(first.World, _output);
        TcpWebLogin(second, user.Uid, code);
        second.Take("On_UserLoginOK");
    }
}
