using System;
using System.Linq;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The crash safety net: saves are otherwise event-driven only (kill, mission, logout), so a
/// crash between events loses everything since the last one. Every claimed account is rewritten
/// to disk every Akaneiro:AutoSaveSeconds; 0 turns the net off.
/// </summary>
public sealed class AutoSaveTests
{
    private readonly ITestOutputHelper _output;

    public AutoSaveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PlayingWithoutAnEventSaveStillReachesDiskAfterTheInterval()
    {
        using var player = new TestPlayer(_output);
        player.LoginNewCharacter();

        // Something the server changed but no event save covered yet (a mid-fight experience
        // gain would save; this mirrors "anything since the last save").
        player.Character.Exp = 987_654;

        var fromDisk = player.World.Accounts.LoadOrCreate(player.Session.AccountKey);
        Assert.NotEqual(987_654UL, fromDisk.Characters.Single(c => c.Id == player.Character.Id).Exp);

        player.Clock.Advance(TimeSpan.FromSeconds(180));

        fromDisk = player.World.Accounts.LoadOrCreate(player.Session.AccountKey);
        Assert.Equal(987_654UL, fromDisk.Characters.Single(c => c.Id == player.Character.Id).Exp);
    }

    [Fact]
    public void AutoSaveOffLeavesOnlyTheEventSaves()
    {
        using var player = new TestPlayer(_output, autoSaveSeconds: 0);
        player.LoginNewCharacter();

        player.Character.Exp = 987_654;
        player.Clock.Advance(TimeSpan.FromMinutes(30));

        var fromDisk = player.World.Accounts.LoadOrCreate(player.Session.AccountKey);
        Assert.NotEqual(987_654UL, fromDisk.Characters.Single(c => c.Id == player.Character.Id).Exp);
    }
}
