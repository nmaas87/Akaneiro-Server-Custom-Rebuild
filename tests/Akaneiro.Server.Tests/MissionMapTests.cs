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
    public void BuyingARegionOpensItsWholeMap()
    {
        // the 2013-era clients buy a whole region with karma (unLockAreaReq, area 5200 = act 2)
        var hunter = new CharacterData { UnlockedAreas = { 5200 } };

        var open = MissionMap.Unlocked(hunter, TestData.Data);

        Assert.Contains(5211, open); // Jawbone Ravine
        Assert.Contains(5214, open); // and its threats, the region opens as a whole
        Assert.DoesNotContain(5311, open); // act 3 stays closed
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
    public void ActEightStaysDarkBecauseTheClientShipsNoLevelForIt()
    {
        // The mission tables open Act 8 once Act 7's "Wicked Beasts" is beaten on all threats;
        // the client has no level, scene XML or minimap texture for the Act 8 maps.
        var hunter = new CharacterData { CompletedMissions = { 5731, 5732, 5733, 5734 } };

        var open = MissionMap.Unlocked(hunter, TestData.Data);

        Assert.Contains(5734, open); // Act 7 itself stays playable
        Assert.DoesNotContain(5811, open); // Knock, Knock (A8_M1)
        Assert.DoesNotContain(5821, open); // Fire in the Dark (A8_M2)
        Assert.DoesNotContain(5831, open); // What Lies Below (A8_M3)
    }

    [Fact]
    public void EveryOpenMissionSitsOnAMapTheClientShips()
    {
        var hunter = new CharacterData();
        foreach (int id in TestData.Data.Missions.Keys)
            hunter.CompletedMissions.Add(id);

        foreach (int id in MissionMap.Unlocked(hunter, TestData.Data))
            Assert.True(MissionMap.ClientHasMap(TestData.Data.Missions[id].MapName),
                $"mission {id} sits on the map '{TestData.Data.Missions[id].MapName}' the client cannot load");
    }

    [Fact]
    public void The2013ChineseClientOnlyGetsTheFifteenMapsItBundles()
    {
        // The untouched ZH build bundles exactly A1_M1..A5_M3 as .unity3d + Relation pairs: no
        // M4 map, no Act 6/7/8. A fully finished hunter still never sees a map it would hang on.
        var hunter = new CharacterData();
        foreach (int id in TestData.Data.Missions.Keys)
            hunter.CompletedMissions.Add(id);

        var open = MissionMap.Unlocked(hunter, TestData.Data, Akaneiro.Server.Protocol.ClientDialect.Zh2013);

        Assert.Contains(5534, open); // Act 5's last threat stays playable ("Wicked Beasts" of Act 5)
        Assert.DoesNotContain(5141, open); // A1_M4 - fourth-mission content this build never shipped
        Assert.DoesNotContain(5611, open); // A6_M1
        Assert.DoesNotContain(5711, open); // A7_M1
        Assert.DoesNotContain(5811, open); // A8_M1
        foreach (int id in open)
            Assert.True(MissionMap.ClientHasMap(TestData.Data.Missions[id].MapName,
                Akaneiro.Server.Protocol.ClientDialect.Zh2013),
                $"mission {id} sits on the map '{TestData.Data.Missions[id].MapName}' the 2013 client cannot load");
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
