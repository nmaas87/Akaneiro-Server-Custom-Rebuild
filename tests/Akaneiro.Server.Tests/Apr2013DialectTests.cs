using System;
using System.Collections.Generic;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Protocol;
using Akaneiro.Server.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The wire contract for the April 2013 client build (the English "Win04162" download client):
/// it already parses most of the 2014 wire (shop visibility, purchase UUID, day rewards, friend
/// look, well, gifts, ping), but the account block, character blocks, bag group, mission process
/// and mission list entries still carry their April 2013 field lists - rewritten packets must
/// parse exactly with the field lists read out of that build's shipped Assembly-CSharp.dll.
/// </summary>
public sealed class Apr2013DialectTests
{
    private readonly ITestOutputHelper _output;

    public Apr2013DialectTests(ITestOutputHelper output) => _output = output;

    // ---- fixtures -------------------------------------------------------------------------------

    private static AccountData Account() => new AccountData
    {
        Account = "april@example.com",
        Karma = 500,
        Crystals = 12,
    };

    private static CharacterData Character()
    {
        var character = new CharacterData { Id = 1, Name = "Urikal", Level = 7 };
        character.Bag.Add(new ItemData { Slot = 0, ItemId = 1001, PrefabId = 3, Level = 5, Count = 1 });
        return character;
    }

    private static Packet Translated(Packet cs)
    {
        Assert.True(Apr2013Dialect.Supports(cs));
        Packet apr = Apr2013Dialect.Translate(cs);
        return ReferenceEquals(apr, cs) ? throw new InvalidOperationException("expected a rewrite, got the 2014 packet") : apr;
    }

    private static Packet Reader(Packet packet)
    {
        byte[] bytes = packet.GetByteStream();
        return new Packet(bytes[10..]); // past the packet's int32 size + int16 tag + int32 method id
    }

    private static int Len(Packet p) => p.GetByteStream().Length;

    // ---- the April 2013 readers, field lists from the shipped client -----------------------------

    private static void ReadAccountInfo2013(Packet ins)
    {
        Assert.Equal("april@example.com", ins.ReadString()); // account ...
        Assert.Equal("123456", ins.ReadString());            // password (constructor default in the protocol class)
        Assert.Equal("april@example.com", ins.ReadString()); // nickName = the account (Views.AccountInfo)
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt();
        EUserType.Read(ins, out _);
        ins.ReadInt();
        ins.ReadString();
        ins.ReadInt();
        ins.ReadNatureTime(); ins.ReadNatureTime(); ins.ReadNatureTime();
        ins.ReadString(); ins.ReadNatureTime(); ins.ReadString();
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); // FK/SK/level
        ins.ReadInt();                          // reviveNum - the April build already has it
        mapEvent.Read(ins, out _);              // ... and the event map
        SWellData.Read(ins, out _);             // ... and the well block
        // and NOTHING more: no curCharSlotNum before the map, no isGetRevivalCard after the well
    }

    // ---- rewritten packets parse with April 2013 layouts, to the last byte -----------------------

    [Fact]
    public void TheAccountBlockKeepsTheWellButDropsSlotAndCardFlags()
    {
        Packet cs = GameResponses.UserLoginOK(false, Views.AccountInfo(Account()),
            Views.CharacterList(Account(), TestData.Data));
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.False(ins.ReadBool()); // reconnected
        ReadAccountInfo2013(ins);
        Assert.Equal(0, ins.ReadInt()); // no characters yet
        // only curCharSlotNum + isGetRevivalCard are newer than April 2013
        Assert.Equal(4 + 4, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheCharacterListCarriesNoAttrVector()
    {
        AccountData account = Account();
        account.Characters.Add(Character());
        Packet cs = GameResponses.UserLoginOK(true, Views.AccountInfo(account),
            Views.CharacterList(account, TestData.Data));
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.True(ins.ReadBool());
        ReadAccountInfo2013(ins);
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(1, ins.ReadInt());            // entry id
        Assert.Equal("Urikal", ins.ReadString());  // the April login entry: 11 fields and
        ins.ReadInt();                             // badgeNum, sex, style, level, exp,
        ESex.Read(ins, out _); ins.ReadInt(); ins.ReadInt();
        ins.ReadUint64(); ins.ReadNatureTime(); ins.ReadInt();
        vectorSItemuuid.Read(ins, out _);
        ins.ReadBool();                            // isTutorial - and nothing more: no attrVec
        // attrVec only exists on the 2014 wire: exactly that block plus the 8 account bytes are gone
        Packet probe = new Packet();
        vectorAttrChange.Write(probe, Views.Attributes(Character(), TestData.Data));
        Assert.Equal(8 + probe.GetByteStream().Length, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheBagGroupHasNoStashBag()
    {
        CharacterData character = Character();
        character.Stash.Add(new ItemData { Slot = 0, ItemId = 3001, PrefabId = 2, Level = 9, Count = 1 });
        SBagGroup group = Views.Bags(character);
        Packet cs = GameResponses.SendBagInfo(group);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        SBagInfo equip = new SBagInfo(); SBagInfo.Read(ins, out equip);
        SBagInfo basebag = new SBagInfo(); SBagInfo.Read(ins, out basebag);
        SBagInfo bank = new SBagInfo(); SBagInfo.Read(ins, out bank);
        Assert.Single(basebag.iteminfos); // the item from the character's bag
        // the fourth (stash) bag is 2014-only: exactly its serialized size is gone
        Packet probe = new Packet();
        SBagInfo.Write(probe, group.stashbag);
        Assert.Equal(probe.GetByteStream().Length, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheBasicBlockKeepsEventsButCarriesNoCooldownMap()
    {
        SCharacterInfoBasic basic = Views.CharacterBasic(Character());
        Packet cs = GameResponses.UserCharacterInfo(basic);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal(1, ins.ReadInt());   // character id
        Assert.Equal("Urikal", ins.ReadString());
        ins.ReadInt(); ins.ReadInt();
        ins.ReadUint64();
        ESex.Read(ins, out _);
        ins.ReadInt(); ins.ReadUint();
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt();
        ins.ReadNatureTime();
        ins.ReadBool();
        mapEvent.Read(ins, out _);        // the event map IS part of the April block
        // only the trailing cooldown map is newer: exactly its serialized size is gone
        Packet probe = new Packet();
        mapTypeCooldown.Write(probe, basic.cooldownData);
        Assert.Equal(probe.GetByteStream().Length, Len(cs) - Len(apr));
    }

    [Fact]
    public void MissionListEntriesSkipTheCooldownAndLastLevelFields()
    {
        var list = new vectorSAcceptMissionRelate
        {
            new SAcceptMissionRelate2 { missionID = 101, coolDownTime = 99, IsHaveCompleted = 1,
                IsHaveTriggered = true, star = 2, lastPassTime = 7, canAcceptMaxLvl = 10, lastPassLvl = 6 },
            new SAcceptMissionRelate2 { missionID = 102, coolDownTime = 5, lastPassLvl = 3 },
        };
        Packet cs = GameResponses.MissionListAck(list);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal(2, ins.ReadInt());
        Assert.Equal(101, ins.ReadInt());
        Assert.Equal(1, ins.ReadInt());   // IsHaveCompleted right after the id: coolDownTime is gone
        Assert.True(ins.ReadBool());
        Assert.Equal(2, ins.ReadInt());
        Assert.Equal(7, ins.ReadInt());
        Assert.Equal(10, ins.ReadInt());  // canAcceptMaxLvl ends the entry: no lastPassLvl
        // two entries, two dropped int pairs each
        Assert.Equal(16, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheMissionProcessKeepsOneReliveCounter()
    {
        var process = Proto.New<SMissionProcess>();
        process.missionID = 101;
        process.crystalReliveNum = 1;
        process.karmaReliveNum = 2;
        process.exp = 999;
        process.karma = 500;
        Packet cs = GameResponses.SendMissionProcess(process);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal(101u, ins.ReadUint());
        vectorSBranch.Read(ins, out _);
        Assert.Equal(2, ins.ReadInt()); // the karma relives - what the April relive shop spent
        // crystalReliveNum + exp + karma + empty materialMap are gone
        Assert.Equal(16, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheRedeemGiftAckUsesThe2013GiftBlock()
    {
        var gift = Proto.New<SRedeemGift>();
        var result = Proto.Error(EServerErrorType.eUnknownError); // the server's "no gifts here" answer
        Packet cs = GameResponses.ProcessRedeemGiftResult(gift, result);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal("", ins.ReadString());  // title, sender, redeemCode, content ...
        Assert.Equal("", ins.ReadString());
        Assert.Equal("", ins.ReadString());
        Assert.Equal("", ins.ReadString());
        Assert.Equal(0, ins.ReadInt());      // karma
        Assert.Equal(0, ins.ReadInt());      // gold
        Assert.Equal(0, ins.ReadInt());      // item id vector (2013: bare ids, not item infos)
        Assert.Equal(0, ins.ReadInt());      // pet id vector
        EServerErrorType.Read(ins, out EServerErrorType back);
        Assert.Equal(EServerErrorType.eUnknownError, back.Get());
        // same field count except the 2014 "content" string: exactly one empty string is gone
        Assert.Equal(4, Len(cs) - Len(apr));
    }

    // ---- what survives unchanged, and what gets dropped ------------------------------------------

    [Fact]
    public void TheShopAndDayRewardWireAreAlreadyApril2013Shaped()
    {
        var offer = new SShopItemInfo
        {
            UUID = 7, ID = 1003, perfrab = 2, level = 10, guid = new ItemGuid(),
            moneyType = 1, price = 40000, isVisible = true, isRare = 0,
        };
        var shelf = GameResponses.SendShopIteminfo(false, new vectorShopItemInfos { offer });
        Assert.True(Apr2013Dialect.Supports(shelf));
        Assert.True(ReferenceEquals(shelf, Apr2013Dialect.Translate(shelf))); // visibility flag stays

        var reward = Proto.New<SDayReward>();
        reward.dayID = 3;
        Packet day = GameResponses.RequestDayRewardOK(reward);
        Assert.True(ReferenceEquals(day, Apr2013Dialect.Translate(day)));     // status vector stays

        var rewards = new mapFriendHireReward
        {
            [5] = new SFriendHireReward { charID = 5, charName = "Comrade", exp = 120, sk = 3, style = 4, sex = ESex.eSex_Female },
        };
        Packet friend = GameResponses.SendFriendRewardInfo(rewards);
        Assert.True(ReferenceEquals(friend, Apr2013Dialect.Translate(friend))); // look/sex stay

        var info = new SBuyitemInfo { UUID = 42, ID = 20009, count = 3, currencyType = 1 };
        Packet buy = GameResponses.BuyItemSuccAck(0, info);
        Assert.True(ReferenceEquals(buy, Apr2013Dialect.Translate(buy)));       // purchase UUID stays
    }

    [Fact]
    public void WellGiftsAndPingSurviveButMailSlotsAndStashAreDropped()
    {
        // the April 2013 build already knows these ...
        Assert.True(Apr2013Dialect.Supports(GameResponses.GetWellDataResult(new SWellData(), 0)));
        Assert.True(Apr2013Dialect.Supports(GameResponses.UpgradeWellResult(Proto.New<EWellType>(), 1,
            Proto.Error(EServerErrorType.eSuccess))));
        Assert.True(Apr2013Dialect.Supports(GameResponses.UpdateReviveNum(3)));
        Assert.True(Apr2013Dialect.Supports(GameResponses.pingRequestAck()));
        Assert.True(Apr2013Dialect.Supports(GameResponses.SelectDayRewardNTF(2)));
        // ... and has no parser for any of the 2014 additions
        Assert.False(Apr2013Dialect.Supports(GameResponses.GetPlayerMailResult(new vectorMailInfos())));
        Assert.False(Apr2013Dialect.Supports(GameResponses.RecvNewMail(new vectorMailInfos())));
        Assert.False(Apr2013Dialect.Supports(GameResponses.BuyCharSlotResult(Proto.Error(EServerErrorType.eSuccess))));
        Assert.False(Apr2013Dialect.Supports(GameResponses.ChangeNameResult(1, "NewName",
            Proto.Error(EServerErrorType.eSuccess))));
        Assert.False(Apr2013Dialect.Supports(GameResponses.UpdateCoolDown(new ECooldownType(), 3, 12345)));
    }

    [Fact]
    public void AnApril2013SessionSeesNoMailButDoesSeeTheWell()
    {
        using var player = new Akaneiro.Server.Tests.Harness.TestPlayer(_output, webLogin: true);
        var recording = new RecordingSink();
        var session = new Session(player.World, recording, NullLogger.Instance) { Dialect = ClientDialect.Apr2013 };

        SBagGroup group = Views.Bags(Character());
        session.Send(GameResponses.RecvNewMail(new vectorMailInfos()));
        session.Send(GameResponses.GetWellDataResult(new SWellData(), 1234));
        session.Send(GameResponses.SendBagInfo(group));

        Assert.Equal(2, recording.Packets.Count); // mail gone, well and (rewritten) bag info stay
        Packet ins = Reader(recording.Packets[0]);
        SWellData well = new SWellData(); SWellData.Read(ins, out well); // 2014 well block parses as-is
        Assert.Equal(1234UL, ins.ReadUint64());
        // the bag info on the wire lost exactly the stash bag
        Packet probe = new Packet();
        SBagInfo.Write(probe, group.stashbag);
        Assert.Equal(Len(GameResponses.SendBagInfo(group)) - probe.GetByteStream().Length,
            Len(recording.Packets[1]));
    }

    private sealed class RecordingSink : Akaneiro.Server.Net.IPacketSink
    {
        public List<Packet> Packets { get; } = new List<Packet>();
        public void Send(Packet packet) => Packets.Add(packet);
        public void Close() { }
    }
}
