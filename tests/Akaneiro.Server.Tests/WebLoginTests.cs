using System;
using Akaneiro.Server.Data;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The TCP side of the web login ("client ver"): the account service (client.php) hands the client a
/// uid plus a gameCode, and the game server sees them as account + token of UserLogin. The Steam
/// login must keep working untouched next to it.
/// </summary>
public sealed class WebLoginTests
{
    private readonly ITestOutputHelper _output;

    public WebLoginTests(ITestOutputHelper output) => _output = output;

    /// <summary>A TestPlayer with WebLogin enabled and one web account (uid + issued gameCode).</summary>
    private static (TestPlayer Player, string Uid, string GameCode) WebSetup(ITestOutputHelper output)
    {
        var player = new TestPlayer(output, webLogin: true);
        var user = player.World.WebUsers.Create("hunter@example.com", "pw123");
        string code = player.World.WebUsers.IssueToken(user.Uid, player.Clock.UtcNow, TimeSpan.FromHours(12));
        return (player, user.Uid, code);
    }

    private static void TcpLogin(TestPlayer player, string account, string token, int userType = EUserType.eUserType_SpicyHorse) =>
        player.Send(ProtocolGame_SendRequest.UserLogin(account, new EUserType(userType),
            new EClientType(EClientType.eClientType_Standalone_Win), "", token));

    [Fact]
    public void WebLoginAcceptsTheIssuedGameCode()
    {
        var (player, uid, code) = WebSetup(_output);

        TcpLogin(player, uid, code);

        var account = player.Take<SAccountInfo>("On_UserLoginOK", 1);
        Assert.Equal(uid, account.account);
        player.Dispose();
    }

    [Fact]
    public void WebUidWithoutGameCodeIsRejected()
    {
        var (player, uid, _) = WebSetup(_output);

        TcpLogin(player, uid, "");

        var error = player.Take<EServerErrorType>("On_UserLoginFailed", 0);
        Assert.Equal(EServerErrorType.eWrongAccountOrPwd, error.Get());
        Assert.False(player.Has("On_UserLoginOK"));
        player.Dispose();
    }

    [Fact]
    public void GameCodeIsBoundToItsUid()
    {
        var (player, uid, code) = WebSetup(_output);
        var other = player.World.WebUsers.Create("other@example.com", "pw123");

        TcpLogin(player, other.Uid, code); // a web uid with somebody else's token

        player.Take("On_UserLoginFailed");
        player.Dispose();
    }

    [Fact]
    public void ExpiredGameCodeIsRejected()
    {
        var (player, uid, code) = WebSetup(_output);

        player.Wait(13 * 3600); // the token lives 12 hours
        TcpLogin(player, uid, code);

        player.Take("On_UserLoginFailed");
        player.Dispose();
    }

    [Fact]
    public void SteamLoginIsUntouchedWhenWebLoginIsOn()
    {
        var (player, _, _) = WebSetup(_output);

        // Exactly what a Steam build sends: Steam ID as account, a made-up web token.
        TcpLogin(player, "76561198000000001", "whatever", EUserType.eUserType_Steam);

        player.Take("On_UserLoginOK");
        player.Dispose();
    }

    [Fact]
    public void WebStoreKeepsPasswordsHashed()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "akaneiro-webusers-" + Guid.NewGuid().ToString("N"));
        var store = new WebUserStore(dir);
        store.Create("hunter@example.com", "pw123");

        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "webusers.json"));
        Assert.DoesNotContain("pw123", json);
        Assert.True(store.Verify("hunter@example.com", "pw123"));
        Assert.False(store.Verify("hunter@example.com", "wrong"));
        Assert.Null(store.Create("HUNTER@example.com", "dup")); // emails are unique, case-insensitive
    }
}
