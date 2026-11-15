using System;
using System.Collections.Generic;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Protocol;
using Akaneiro.Server.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            new SAcceptMissionRelate2 { missionID = 5110, coolDownTime = 99, IsHaveCompleted = 1,
                IsHaveTriggered = true, star = 2, lastPassTime = 7, canAcceptMaxLvl = 10, lastPassLvl = 6 },
            new SAcceptMissionRelate2 { missionID = 5120, coolDownTime = 5, lastPassLvl = 3 },
        };
        Packet cs = GameResponses.MissionListAck(list);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal(2, ins.ReadInt());
        Assert.Equal(5111, ins.ReadInt()); // threat-1 id: the client arms its slots by matching these
        Assert.Equal(1, ins.ReadInt());   // IsHaveCompleted right after the id: coolDownTime is gone
        Assert.True(ins.ReadBool());
        Assert.Equal(2, ins.ReadInt());
        Assert.Equal(7, ins.ReadInt());
        Assert.Equal(10, ins.ReadInt());  // canAcceptMaxLvl ends the entry: no lastPassLvl
        // two entries, two dropped int pairs each
        Assert.Equal(16, Len(cs) - Len(apr));
    }

    [Fact]
    public void TheAprilListDropsTheTutorialAndFourthMissions()
    {
        // the tutorial never shows on the bounty map (the village NPC starts it), and each
        // region's fourth mission arrived with the 2014 client
        var list = new vectorSAcceptMissionRelate
        {
            new SAcceptMissionRelate2 { missionID = 5010 },
            new SAcceptMissionRelate2 { missionID = 5140 },
            new SAcceptMissionRelate2 { missionID = 5110 },
        };
        Packet ins = Reader(Translated(GameResponses.MissionListAck(list)));
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(5111, ins.ReadInt()); // written as the threat-1 id the client's lookup expects
    }

    [Fact]
    public void TheMissionProcessKeepsOneReliveCounter()
    {
        var process = Proto.New<SMissionProcess>();
        process.missionID = 5011; // the id crosses untouched (the panel math works on it)
        process.crystalReliveNum = 1;
        process.karmaReliveNum = 2;
        process.exp = 999;
        process.karma = 500;
        Packet cs = GameResponses.SendMissionProcess(process);
        Packet apr = Translated(cs);

        Packet ins = Reader(apr);
        Assert.Equal(5011u, ins.ReadUint());
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

    [Fact]
    public void HuntRequestsFromTheClientTablesLandOnServerMissions()
    {
        // the hunt buttons send the April table ids (1RM from the shipped region prefabs); the
        // server's own 5RTM ids - which the panel's region math uses - pass through untouched
        Assert.Equal(5111, Apr2013Dialect.ToServerMissionId(111)); // the threat 1 variant
        Assert.Equal(5011, Apr2013Dialect.ToServerMissionId(101)); // the tutorial
        Assert.Equal(5141, Apr2013Dialect.ToServerMissionId(114)); // an id the tables lack stays mappable anyway
        Assert.Equal(5110, Apr2013Dialect.ToServerMissionId(5110)); // map bases
        Assert.Equal(5112, Apr2013Dialect.ToServerMissionId(5112)); // threat variants too
        Assert.Equal(5100, Apr2013Dialect.ToClientAreaId(5112));   // areas are whole regions
        Assert.Equal(5000, Apr2013Dialect.ToClientAreaId(5011));
    }

    [Fact]
    public void MissionNotificationsCarryTheServerIdsUntouched()
    {
        // the client matches these against the ids it got in the list (5RTM): rewriting them to
        // the table family strands the progress panel
        Packet branch = GameResponses.branchCompleteNTF(5111, 2, 3, 40, 50);
        Assert.Same(branch, Apr2013Dialect.Translate(branch));
        Packet done = GameResponses.MissionCompleteNTF(5121, 100, 60);
        Assert.Same(done, Apr2013Dialect.Translate(done));
        Packet fail = GameResponses.MissionFailAck(5131, 1);
        Assert.Same(fail, Apr2013Dialect.Translate(fail));
    }

    [Fact]
    public void ErrorCodesMoveToTheAprilBuildsNumbering()
    {
        // both builds' TypeDefine blocks, diffed: 2014 inserted codes, everything after shifts.
        // Sending 109 ("already got the day reward") shows as the April client's 109
        // "eMission_Dont_Have_ReqLevel" - the mystery popup on every hub visit after claiming.
        Assert.Equal(0, Apr2013Dialect.ToClientError(0));    // eSuccess matches
        Assert.Equal(1, Apr2013Dialect.ToClientError(1));    // eUnknownError matches
        Assert.Equal(51, Apr2013Dialect.ToClientError(51));  // less karma: same number in both
        Assert.Equal(107, Apr2013Dialect.ToClientError(109)); // day reward already got
        Assert.Equal(92, Apr2013Dialect.ToClientError(94));   // bag full
        Assert.Equal(113, Apr2013Dialect.ToClientError(115)); // mission/area not in the table
        Assert.Equal(120, Apr2013Dialect.ToClientError(123)); // unassigned talent points
    }

    [Fact]
    public void A2013ClientGetsTheAreaPushThatOpensItsBountyPanel()
    {
        // the 2013 mission panel is only brought up by the area-info push that follows the list;
        // read the raw wire here, the 2014 decode of the rewritten list would throw
        using var player = new TestPlayer(_output).LoginNewCharacter();
        var recording = new RecordingSink();
        var apr = new Session(player.World, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Apr2013,
            Character = player.Character,
        };

        Packet req = ProtocolGame_SendRequest.MissionListReq();
        apr.HandlePacket(req.GetByteStream()[6..]);

        Assert.Equal(2, recording.Packets.Count); // list, then area push (the 2013 UI opens on that)

        Packet list = Reader(recording.Packets[0]);
        var ids = new List<int>();
        int count = list.ReadInt();
        Assert.True(count > 0);
        for (int i = 0; i < count; i++)
        {
            ids.Add(list.ReadInt()); // threat-1 id of the act 1 map (5111); the tutorial is not listed
            list.ReadInt(); list.ReadBool(); list.ReadInt(); list.ReadInt(); list.ReadInt();
            // completed, triggered, star, lastPassTime, canAcceptMaxLvl - the April entry,
            // no coolDownTime after the id and no lastPassLvl at the end
        }
        // the client arms its mission slots by matching these ids (MissionLevelToID searches the
        // threat-1 variant) and opens regions by id / 100 % 10; the tutorial (0-region) is dropped
        Assert.DoesNotContain(5010, ids);
        Assert.All(ids, id => Assert.InRange(id, 5111, 5843));

        // the push addresses whole areas (area n -> 5000 + n * 100); maps of one region collapse
        // into a single entry, and isBuy marks karma-bought areas (unlocked is unlocked either
        // way); the tutorial's area has no region icon on the April map and is not pushed
        Packet areas = Reader(recording.Packets[1]);
        var areaIds = new List<int>();
        int areaCount = areas.ReadInt();
        for (int i = 0; i < areaCount; i++)
        {
            areaIds.Add(areas.ReadInt());
            areas.ReadBool();
        }
        Assert.Equal(ids.Select(id => 5000 + id / 10 % 10 * 100).Distinct(), areaIds);
        Assert.DoesNotContain(5000, areaIds); // no tutorial area on the 2013 bounty map
    }

    [Fact]
    public void A2013ClientBuysARegionWithKarmaAndItOpens()
    {
        // "Jawbone Ravine - Unlock 50 shards" (April MissionArea.Area: 5200 costs 50 karma, not
        // free). The client sends unLockAreaReq(areaID); the ack is (areaID, error) and only
        // eSuccess lights the region icon up. The refreshed list then opens act 2 (base 5210)
        // and the push carries area 5200 marked bought.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-unlock-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")))
        {
            Data2013 = TestData2013.Data,
        };
        var account = Account(); // 500 karma
        var character = Character();
        var recording = new RecordingSink();
        var apr = new Session(world, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Apr2013,
            Account = account,
            Character = character,
        };

        apr.HandlePacket(ProtocolGame_SendRequest.unLockAreaReq(5200).GetByteStream()[6..]);

        // the handler answers: ack, money bar, refreshed list, refreshed area push
        Packet ack = Reader(recording.Packets[0]);
        Assert.Equal(5200, ack.ReadInt());
        Assert.Equal(0, ack.ReadInt()); // eSuccess
        Assert.Equal(450, account.Karma); // the table's 50 shards
        Assert.Equal(new[] { 5200 }, character.UnlockedAreas);

        var ids = new List<int>();
        Packet list = Reader(recording.Packets[2]);
        int count = list.ReadInt();
        for (int i = 0; i < count; i++)
        {
            ids.Add(list.ReadInt());
            list.ReadInt(); list.ReadBool(); list.ReadInt(); list.ReadInt(); list.ReadInt();
        }
        Assert.Contains(5111, ids); // act 1, still there
        Assert.Contains(5211, ids); // act 2 opened by the purchase

        Packet areas = Reader(recording.Packets[3]);
        var bought = new Dictionary<int, bool>();
        int areaCount = areas.ReadInt();
        for (int i = 0; i < areaCount; i++)
        {
            int areaId = areas.ReadInt();
            bought[areaId] = areas.ReadBool();
        }
        Assert.Equal(new[] { 5100, 5200 }, bought.Keys.OrderBy(id => id));
        Assert.True(bought[5200]); // flagged as karma-bought

        // asking again charges nothing and the area stays owned once (already-owned ack, no resend)
        apr.HandlePacket(ProtocolGame_SendRequest.unLockAreaReq(5200).GetByteStream()[6..]);
        Packet again = Reader(recording.Packets[^1]);
        Assert.Equal(5200, again.ReadInt());
        Assert.Equal(0, again.ReadInt());
        Assert.Equal(450, account.Karma);
        Assert.Equal(new[] { 5200 }, character.UnlockedAreas);
    }

    [Fact]
    public void A2013ClientWithoutKarmaGetsTheShortfallAck()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-poor-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")))
        {
            Data2013 = TestData2013.Data,
        };
        var account = Account();
        account.Karma = 49; // one shard under the 50 Jawbone costs
        var recording = new RecordingSink();
        var apr = new Session(world, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Apr2013,
            Account = account,
            Character = Character(),
        };

        apr.HandlePacket(ProtocolGame_SendRequest.unLockAreaReq(5200).GetByteStream()[6..]);

        Packet ack = Reader(recording.Packets[0]);
        Assert.Equal(5200, ack.ReadInt());
        Assert.Equal(51, ack.ReadInt()); // eGS_LessMoney_karma: the karma-shortfall code (51 in both eras)
        Assert.Equal(49, account.Karma);
        Assert.Empty(recording.Packets.Where(p => p.GetByteStream().Length > 0
                                                  && BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_MissionAreaInfoAck));
    }

    [Fact]
    public void A2014ClientNeverSeesThe2013AreaPush()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Send(ProtocolGame_SendRequest.MissionListReq());
        player.Take("On_MissionListAck");
        Assert.False(player.Has("On_MissionAreaInfoAck"));
    }

    [Fact]
    public void The2013DailyGiftGrantsAllFourTracksAndMarksTheStreakDay()
    {
        // "Gifts from the village": RequestDayReward hands out the streak day of every gift
        // table (DayReward.reward .. reward4), marks that day (1) in the status vector the
        // window lights up, rotates the shown track via SelectDayRewardNTF, and answers a
        // second same-day visit with the already-claimed code the client swallows (107 in the
        // April build's numbering).
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-daily-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")))
        {
            Data2013 = TestData2013.Data,
        };
        var account = Account();
        var character = Character();
        var recording = new RecordingSink();
        var apr = new Session(world, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Apr2013,
            Account = account,
            Character = character,
        };

        apr.HandlePacket(ProtocolGame_SendRequest.RequestDayReward().GetByteStream()[6..]);

        // the granted gift items announce themselves (NotifyItemUpdate) before the window answer;
        // the window itself is the NTF + OK pair
        Packet ntf = Reader(recording.Packets.Single(p =>
            BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_SelectDayRewardNTF));
        Assert.Equal(0, ntf.ReadInt()); // first streak day shows the first gift table
        Packet ok = Reader(recording.Packets.Single(p =>
            BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_RequestDayRewardOK));
        Assert.Equal(1, ok.ReadInt());  // dayID: streak day one
        ok.ReadLong();
        EServerErrorType.Read(ok, out var code);
        Assert.Equal(0, code.Get());
        vectorInt.Read(ok, out var status);
        int days = TestData2013.Data.DayRewardTracks[0].Count;
        Assert.Equal(days, status.Count);
        Assert.Equal(1, (int)status[0]);
        if (days > 1)
            Assert.Equal(0, (int)status[1]);

        // day one of every track lands on the account: karma, crystals, and the bag
        int karma = 500, crystals = 12, bag = 1; // the fixture's single item stays
        foreach (var track in TestData2013.Data.DayRewardTracks)
        {
            var row = track.FirstOrDefault(r => r.Day == 1);
            if (row == null) continue;
            switch (row.Type)
            {
                case 1: karma += row.Count; break;
                case 2: crystals += row.Count; break;
                default: if (row.Item != null) bag++; break;
            }
        }
        Assert.Equal(karma, account.Karma);
        Assert.Equal(crystals, account.Crystals);
        Assert.Equal(bag, character.Bag.Count);

        // same-day again: no second grant, and the April client's already-claimed code
        apr.HandlePacket(ProtocolGame_SendRequest.RequestDayReward().GetByteStream()[6..]);
        Packet again = Reader(recording.Packets.Last(p =>
            BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_RequestDayRewardOK));
        again.ReadInt();
        again.ReadLong();
        EServerErrorType.Read(again, out var late);
        Assert.Equal(107, late.Get()); // April's eDayRewardError_AlreadyGet: no popup, straight to the friend reward
    }

    [Fact]
    public void BothEraContentSetsCarryFourGiftTracks()
    {
        Assert.Equal(4, TestData.Data.DayRewardTracks.Count);
        Assert.Equal(4, TestData2013.Data.DayRewardTracks.Count);
        Assert.All(TestData2013.Data.DayRewardTracks, track => Assert.True(track.Count > 0));
    }

    [Fact]
    public void AprilCharactersStartWithTheirDisciplineAbility()
    {
        // The April bundle's CreateCharaInfo table has no ability columns; the grant falls
        // back to the modern id family, whose tier-one starters all exist in the April
        // Ability table (10501 Rain of Blows I, 11101, 11401) - no April hunter may start
        // with an empty ability bar.
        var starts = TestData2013.Data.StartAbilities;
        Assert.NotEmpty(starts);
        Assert.All(starts.Values, ids => Assert.True(ids.Any(id => id != 0)));
        var all = starts.Values.SelectMany(ids => ids).Where(id => id != 0).ToList();
        Assert.Contains(10501, all);
        Assert.Contains(11101, all);
        Assert.Contains(11401, all);
        Assert.NotEmpty(TestData.Data.StartAbilities); // modern grant untouched
    }

    [Fact]
    public void TheAprilThreatButtonsTradeKarmaShardsOnADoublingLadder()
    {
        // The 2013 panel's tips: "Spend Karma Shards to raise/lower the threat level." The
        // amounts were 2013 server-side data (no mission components in the April bundles), so
        // the revival's ladder mirrors the region prices: 50 / 100 / 200 per target threat.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-threat-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")))
        {
            Data2013 = TestData2013.Data,
        };
        var account = Account(); // 500 karma
        var character = Character();
        var recording = new RecordingSink();
        var apr = new Session(world, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Apr2013,
            Account = account,
            Character = character,
        };

        apr.HandlePacket(ProtocolGame_SendRequest.buyThreatLevelByKarma(5110, 1).GetByteStream()[6..]);

        Packet ack = Reader(recording.Packets[^1]);
        Assert.Equal(0, ack.ReadInt()); // eSuccess
        Assert.Equal(5110, ack.ReadInt());
        Assert.Equal(1, ack.ReadInt());
        Assert.Equal(0, ack.ReadInt()); // nextThreatTime: 2013 had no rotating threat cycle
        Assert.Equal(450, account.Karma); // up to threat 2: 50 shards
        Assert.Equal(2, character.ThreatLevel);

        apr.HandlePacket(ProtocolGame_SendRequest.buyThreatLevelByKarma(5110, 1).GetByteStream()[6..]);
        Assert.Equal(350, account.Karma); // up to threat 3: 100 shards
        Assert.Equal(3, character.ThreatLevel);

        apr.HandlePacket(ProtocolGame_SendRequest.buyThreatLevelByKarma(5110, -1).GetByteStream()[6..]);
        Assert.Equal(300, account.Karma); // lowering spends too: down to threat 2 costs 50
        Assert.Equal(2, character.ThreatLevel);

        // An unknown map is refused with April's mission-not-exist code (113).
        apr.HandlePacket(ProtocolGame_SendRequest.buyThreatLevelByKarma(9990, 1).GetByteStream()[6..]);
        Packet bad = Reader(recording.Packets[^1]);
        Assert.Equal(113, bad.ReadInt());
        Assert.Equal(300, account.Karma);
    }

    [Fact]
    public void ModernThreatSwitchesStayFreeAndUntouched()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-threat-mod-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")));
        var account = Account();
        var character = Character();
        var recording = new RecordingSink();
        var modern = new Session(world, recording, NullLogger.Instance)
        {
            Account = account,
            Character = character,
        };

        modern.HandlePacket(ProtocolGame_SendRequest.buyThreatLevelByKarma(5110, 1).GetByteStream()[6..]);

        Packet ack = Reader(recording.Packets[^1]);
        Assert.Equal(0, ack.ReadInt()); // success, no charge: modern had no threat trade
        Assert.Equal(500, account.Karma);
        Assert.Equal(2, character.ThreatLevel); // still tracked, so accepts stay consistent
    }

    [Fact]
    public void TheAprilMaterialRollsStickToItemsThatClientCanShow()
    {
        // The April bundle ships icon bundles for every gem (ICN_Gem_Ruby..Golden) and element
        // core (Core_Ice..Storm, Core_Karma) but never shipped one for the 6xxx enchantment
        // materials - the client's registry entry loads a bundle that does not exist, so pelt
        // and scrap stacks sit in the bag as placeholders. The era also has no crafting UI
        // (no UpgradeRecipes table in its bundle), so enchantment materials would be dead
        // weight regardless. April loot rolls gems and cores instead: every id below has both
        // a Props.General row and a real icon bundle in that client.
        var rng = new Random(7);
        var ids = Enumerable.Range(0, 500).Select(_ => Loot.RollMaterial(TestData2013.Data, rng).ItemId).ToHashSet();

        Assert.All(ids, id => Assert.True(id is >= 7001 and <= 7009 or >= 8001 and <= 8004 or 9001, $"{id}"));
        Assert.False(TestData2013.Data.EraHasCrafting); // April's own tables carry no recipe file
        Assert.True(TestData.Data.EraHasCrafting);      // 2014 rolls stay exactly as shipped

        string props = File.ReadAllText(Path.Combine(
            TestData.FindClientAssets("client-source-2013"), "Resources", "Localization", "EN", "Props.General.EN.txt"));
        Assert.All(ids, id => Assert.Contains($"\n{id}\t", props)); // every roll resolves in April's table
    }

    [Fact]
    public void The2013TutorialMapCarriesEnoughKillsForTheDoorMilestones()
    {
        // The April client opens the village doors at tutorial kill milestones 2, 11 and 22
        // (TutorialLogic.UpdateTutrialLogicByHunt); the 2014 layout only spawns 17.
        static int Kills(GameData data) =>
            data.Spawners["Hub_Village_Tutorial"].Sum(sp => sp.Groups.Sum(g => g.Count));
        Assert.True(Kills(TestData2013.Data) >= 22, $"April tutorial spawns {Kills(TestData2013.Data)} monsters");
        Assert.Equal(17, Kills(TestData.Data)); // the fork widened only the April table
    }

    [Fact]
    public void The2013ReviveNumPacketCarriesASingleScrollCount()
    {
        // The 2014 PlayerReviveNumNTF carries two counters (crystal/karma revivals); the
        // April build reads one int - its death panel's Shinigami Scroll count.
        Packet p = Apr2013Dialect.PlayerReviveNumNTF2013(7);
        byte[] bytes = p.GetByteStream();
        Assert.Equal((int)ProtocolBattle_METHOD.uMethodID_PlayerReviveNumNTF, BitConverter.ToInt32(bytes, 6));
        Assert.Equal(7, BitConverter.ToInt32(bytes, 10));
        Assert.Equal(14, bytes.Length); // size+tag+method and exactly one payload int
    }

    private sealed class RecordingSink : Akaneiro.Server.Net.IPacketSink
    {
        public List<Packet> Packets { get; } = new List<Packet>();
        public void Send(Packet packet) => Packets.Add(packet);
        public void Close() { }
    }
}
