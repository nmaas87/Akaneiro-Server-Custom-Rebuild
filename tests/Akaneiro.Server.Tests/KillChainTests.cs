using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Kill chains ("GOOD!", "LETHAL!") and bounties of "Wanted" monsters.</summary>
public sealed class KillChainTests
{
    private readonly ITestOutputHelper _output;

    public KillChainTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(2, 24, 0)]
    [InlineData(3, 36, 18)]  // video: three wolves, "GOOD! +18 XP"
    [InlineData(6, 72, 54)]  // video: six wolves, "LETHAL! +54 XP"
    [InlineData(10, 120, 120)]
    public void TheBonusMatchesTheRecordings(int kills, int xp, int bonus) =>
        Assert.Equal((ulong)bonus, KillChain.Bonus(kills, (ulong)xp));

    [Fact]
    public void QuickKillsMakeAChainThatPaysWhenItEnds()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5141);
        player.AcceptMission(5141);
        player.EnterScene("A1_M4");
        player.Session.CurrentHp = 1_000_000;
        var wolves = player.Scene.Monsters.Where(m => m.IsActive).Take(3).ToList();
        foreach (var wolf in wolves)
            wolf.Hp = 1; // one hit each, all within the chain window

        foreach (var wolf in wolves)
        {
            player.MoveTo(wolf.Position);
            player.Attack(wolf.ObjectId);
        }

        Assert.Equal(2, player.Take("On_UpdateSerialKillNum").Arg<int>(0));
        Assert.False(player.Has("On_SerialKillOver"));

        player.Wait(KillChain.WindowSeconds + 0.5);

        var over = player.Take("On_SerialKillOver");
        Assert.Equal(3, over.Arg<int>(0));
        Assert.True(over.Arg<int>(1) > 0);
    }

    [Fact]
    public void WantedMonstersAreKnown()
    {
        var bloodroot = TestData.Data.Monsters.Values.Single(m => m.Name == "Bloodroot Thief");
        Assert.True(bloodroot.IsWanted);
        Assert.True(bloodroot.WantedXp > 0);
    }
}
