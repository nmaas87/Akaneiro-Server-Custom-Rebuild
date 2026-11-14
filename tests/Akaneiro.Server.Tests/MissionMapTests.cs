using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;

namespace Akaneiro.Server.Tests;

/// <summary>Which mission maps are open, and how long a finished map rests.</summary>
public sealed class MissionMapTests
{
    private static readonly long Now = 1_700_000_000;

    [Fact]
    public void ANewHunterOnlyHasTheFirstMap()
    {
        var open = MissionMap.Unlocked(new CharacterData(), TestData.Data);

        Assert.Contains(5111, open);
        Assert.DoesNotContain(5121, open);
        Assert.DoesNotContain(5211, open);
    }

    [Fact]
    public void FinishingAMissionOpensTheNextMapAndThreat()
    {
        var hunter = new CharacterData { CompletedMissions = { 5111 } };

        var open = MissionMap.Unlocked(hunter, TestData.Data);

        Assert.Contains(5112, open); // Wicked Beasts on the next threat
        Assert.Contains(5121, open); // Rotten Core
        Assert.DoesNotContain(5141, open);
    }

    [Fact]
    public void BlindRageFollowsRottenCore()
    {
        var hunter = new CharacterData { CompletedMissions = { 5111, 5121 } };

        Assert.Contains(5141, MissionMap.Unlocked(hunter, TestData.Data));
    }

    [Fact]
    public void AFinishedMapRestsLongerWithEveryRun()
    {
        var hunter = new CharacterData();

        MissionMap.Finished(hunter, 5111, Now, TestData.Data);
        Assert.Equal(5 * 60, MissionMap.CooldownSeconds(hunter, 5112, Now));

        MissionMap.Finished(hunter, 5111, Now + 600, TestData.Data);
        Assert.Equal(20 * 60, MissionMap.CooldownSeconds(hunter, 5111, Now + 600));
        Assert.Equal(0, MissionMap.CooldownSeconds(hunter, 5121, Now + 600));
    }

    [Fact]
    public void TheRestCanBeCutShort()
    {
        var hunter = new CharacterData();
        MissionMap.Finished(hunter, 5111, Now, TestData.Data);
        MissionMap.Finished(hunter, 5111, Now, TestData.Data); // 20 minutes

        MissionMap.Shorten(hunter, 5111, 0, Now);

        Assert.Equal(0, MissionMap.CooldownSeconds(hunter, 5111, Now));
    }
}

/// <summary>Finished maps are playable again at once (cooldowns are switched off).</summary>
public sealed class NoCooldownTests
{
    [Fact]
    public void TheMissionListShowsNoWaitingTime()
    {
        using var player = new TestPlayer().LoginNewCharacter();
        player.Session.Character.CompletedMissions.Add(5111);
        player.Session.Character.MissionReadyUnix[5110] = long.MaxValue / 2;

        player.Send(ProtocolGame_SendRequest.MissionListReq());

        var maps = player.Take<vectorSAcceptMissionRelate>("On_MissionListAck").Cast<SAcceptMissionRelate2>().ToList();
        Assert.All(maps, m => Assert.Equal(0, m.coolDownTime));
    }
}
