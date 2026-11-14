using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Stash tabs, the well and the spirit helpers.</summary>
public sealed class VillageTests
{
    private const int Inugami = 1001; // PetPrice: 250 karma per hour, level 2 required

    private readonly ITestOutputHelper _output;

    public VillageTests(ITestOutputHelper output) => _output = output;

    private TestPlayer InVillage(int level = 5, int karma = 10000)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.Session.Character.Level = level;
        player.Session.Account.Karma = karma;
        player.EnterScene("Hub_Village");
        player.ClearInbox();
        return player;
    }

    // ---- stash ------------------------------------------------------------------------------

    [Fact]
    public void AStashTabCanBeBoughtUpToFive()
    {
        using var player = InVillage();

        for (int tabs = 2; tabs <= Inventory.MaxStashTabs; tabs++)
        {
            player.Send(ProtocolGame_SendRequest.buyStash());
            var ack = player.Take("On_buyStashAck");
            Assert.Equal(tabs, ack.Arg<int>(0));
            Assert.Equal(EServerErrorType.eSuccess, ack.Arg<EServerErrorType>(1).Get());
        }
        player.Send(ProtocolGame_SendRequest.buyStash());
        Assert.NotEqual(EServerErrorType.eSuccess, player.Take("On_buyStashAck").Arg<EServerErrorType>(1).Get());

        // The second tab's slots can be used now.
        player.Session.Character.Bag.Add(new Akaneiro.Server.Data.ItemData { ItemId = 1001, Slot = 0 });
        player.Send(ProtocolGame_SendRequest.SwapItem(2, 0, 4, 12));
        Assert.True(player.Has("On_NotifyItemSwap"));
    }

    // ---- well -------------------------------------------------------------------------------

    [Fact]
    public void TheWellFillsInFifteenMinutes()
    {
        using var player = InVillage(karma: 0);

        player.Send(ProtocolGame_SendRequest.GetWellData());
        var data = player.Take("On_GetWellDataResult");
        Assert.Equal(1, data.Arg<SWellData>(0).speedLevel);
        long begin = data.Arg<SWellData>(0).beginTime;
        Assert.Equal((ulong)begin, data.Arg<ulong>(1)); // empty right now

        player.Send(ProtocolGame_SendRequest.GetWellKarma());
        Assert.NotEqual(EServerErrorType.eSuccess, player.Take<EServerErrorType>("On_GetWellKarmaResult").Get());

        player.Wait(15 * 60);
        player.Send(ProtocolGame_SendRequest.GetWellKarma());
        Assert.Equal(EServerErrorType.eSuccess, player.Take<EServerErrorType>("On_GetWellKarmaResult").Get());
        Assert.Equal(Well.KarmaPerFilling, player.Session.Account.Karma);
    }

    // ---- spirit helpers -----------------------------------------------------------------------

    [Fact]
    public void TheSpiritTrainerOpensWithTheRentedSpirits()
    {
        using var player = InVillage();

        player.Send(ProtocolGame_SendRequest.RolePetListReq());

        Assert.Empty(player.Take<vectorSPetInfo>("On_RolePetListAck"));
    }

    [Fact]
    public void RentingASpiritCostsKarmaAndItCanBeChosen()
    {
        using var player = InVillage();

        player.Send(ProtocolGame_SendRequest.BuyPet(Inugami, Spirits.RentHour));
        Assert.Equal(new object[] { Inugami, Spirits.RentHour }, player.Take("On_BuyPetAck").Args.Take(2).ToArray());
        Assert.Equal(10000 - 250, player.Session.Account.Karma);

        // The client chooses the spirit right after buying it.
        player.Send(ProtocolGame_SendRequest.choosePet(Inugami));
        Assert.Equal(EServerErrorType.eSuccess, player.Take("On_choosePetAck").Arg<EServerErrorType>(1).Get());

        player.Send(ProtocolGame_SendRequest.RolePetListReq());
        var rented = player.Take<vectorSPetInfo>("On_RolePetListAck").Cast<SPetInfo>().Single();
        Assert.Equal(Inugami, rented.petTypeID);
        Assert.Equal(3600, rented.leftTime);
    }

    [Fact]
    public void CallingAGrownSpiritAnswersTheLineIdTheClientHasAPrefabFor()
    {
        using var player = InVillage();
        player.Send(ProtocolGame_SendRequest.BuyPet(Inugami, Spirits.RentHour));
        player.Take("On_BuyPetAck");
        player.Character.SpiritExp[10] = 2000; // the dog grew to level 2 hunting with the hunter

        // The shop shows the kind at its current level and clicks that row: Inugami level 2 is
        // id 1002. The client spawns the spirit from the id the answer echoes, and it only owns
        // the dog prefab 1001 - echoing 1002 means the spirit silently never appears.
        player.Send(ProtocolGame_SendRequest.choosePet(1002));
        var ack = player.Take("On_choosePetAck");

        Assert.Equal(EServerErrorType.eSuccess, ack.Arg<EServerErrorType>(1).Get());
        Assert.Equal(Inugami, ack.Arg<int>(0));
        Assert.True(player.Character.Spirits.Single().Chosen);
        Assert.Equal(2, player.Session.Spirits.Active().Level); // the level-2 bonus is the one that counts
    }

    [Fact]
    public void TheSpiritListCarriesTheGrownLevelAndTheRemainingTime()
    {
        using var player = InVillage();
        player.Send(ProtocolGame_SendRequest.BuyPet(Inugami, Spirits.RentHour));
        player.Take("On_BuyPetAck");
        player.Send(ProtocolGame_SendRequest.choosePet(Inugami));
        player.Take("On_choosePetAck");

        player.Session.Spirits.GainExperience(2000); // level 2 needs 1664
        player.Wait(600);                            // ten minutes of the hour are over

        player.Send(ProtocolGame_SendRequest.RolePetListReq());
        var rented = player.Take<vectorSPetInfo>("On_RolePetListAck").Cast<SPetInfo>().Single();

        Assert.Equal(Inugami, rented.petTypeID);
        Assert.Equal(2, rented.petLevel);
        Assert.Equal(2000UL, rented.petExp);
        Assert.Equal(3000, rented.leftTime); // what is left of the hour, not the whole rent
    }

    [Fact]
    public void AnAccountFileWithAPromotedRentalIdFoldsBackIntoTheLine()
    {
        using var player = InVillage();
        long now = new DateTimeOffset(player.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
        player.Character.Spirits.Add(new SpiritRental { PetId = 1005, BeginUnix = now, Seconds = 3600, Chosen = true });

        player.Send(ProtocolGame_SendRequest.RolePetListReq());
        var rented = player.Take<vectorSPetInfo>("On_RolePetListAck").Cast<SPetInfo>().Single();

        // 1005 (Inugami level 5, needs 39862 experience) becomes the line id again, and the
        // level-5 experience carries over into the shared counter.
        Assert.Equal(Inugami, rented.petTypeID);
        Assert.Equal(5, rented.petLevel);
        Assert.Equal(39862UL, rented.petExp);
        Assert.Equal(Inugami, player.Character.Spirits.Single().PetId);
    }

    [Fact]
    public void ARentedSpiritGoesHomeWhenItsTimeIsUp()
    {
        using var player = InVillage();
        player.Send(ProtocolGame_SendRequest.BuyPet(Inugami, Spirits.RentHour));
        player.Send(ProtocolGame_SendRequest.choosePet(Inugami));

        player.Wait(3601);

        Assert.Equal(Inugami, player.Take<int>("On_PetRentTimeEndNTF"));
        Assert.Empty(player.Session.Character.Spirits);
    }

    [Fact]
    public void SpiritsHaveALevelRequirement()
    {
        using var player = InVillage(level: 1);

        player.Send(ProtocolGame_SendRequest.BuyPet(Inugami, Spirits.RentHour));

        Assert.Equal(EServerErrorType.eGS_LessLevel, player.Take("On_BuyPetAck").Arg<EServerErrorType>(2).Get());
    }
}

public sealed class DailyRewardTests
{
    private readonly ITestOutputHelper _output;

    public DailyRewardTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheFirstLoginOfADayBringsTheDaysReward()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Session.Account.Karma = 0;

        player.Send(ProtocolGame_SendRequest.RequestDayReward());
        var reward = player.Take<SDayReward>("On_RequestDayRewardOK");
        Assert.Equal(EServerErrorType.eSuccess, reward.returnCode.Get());
        Assert.Equal(1, reward.dayID);
        // Day 1: one "Spicy Sushi - Power" (DayReward.reward).
        Assert.Contains(player.Session.Character.Bag, i => i.ItemId == 20000);

        // Only once per day: code 109 makes the client skip the reward screen.
        player.Send(ProtocolGame_SendRequest.RequestDayReward());
        Assert.Equal(EServerErrorType.eDayRewardError_AlreadyGet, player.Take<SDayReward>("On_RequestDayRewardOK").returnCode.Get());
    }

    [Fact]
    public void ComingBackTheNextDayCountsUp()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Session.Account.Karma = 0;
        player.Send(ProtocolGame_SendRequest.RequestDayReward());
        player.ClearInbox();

        player.Wait(24 * 60 * 60);
        player.Send(ProtocolGame_SendRequest.RequestDayReward());

        Assert.Equal(2, player.Take<SDayReward>("On_RequestDayRewardOK").dayID);
        Assert.Equal(200, player.Session.Account.Karma); // day 2: 200 karma shards
    }

    [Fact]
    public void AMissedDayStartsOver()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Send(ProtocolGame_SendRequest.RequestDayReward());
        player.ClearInbox();

        player.Wait(3 * 24 * 60 * 60);
        player.Send(ProtocolGame_SendRequest.RequestDayReward());

        Assert.Equal(1, player.Take<SDayReward>("On_RequestDayRewardOK").dayID);
    }
}
