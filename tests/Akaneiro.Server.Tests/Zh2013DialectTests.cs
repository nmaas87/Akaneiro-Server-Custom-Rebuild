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
/// The wire contract for the 2013 client build (the Chinese fat client): every packet the dialect
/// layer rewrites must parse exactly with the 2013 field lists read out of the shipped
/// Assembly-CSharp.dll - and after the last 2013 field the packet must be done. That last check is
/// what the real client died on in January 2026 ("count is less than 0"): it started reading a
/// field at an offset where the 2014 packet had five more.
/// </summary>
public sealed class Zh2013DialectTests
{
    private readonly ITestOutputHelper _output;

    public Zh2013DialectTests(ITestOutputHelper output) => _output = output;

    // ---- fixtures -------------------------------------------------------------------------------

    private static AccountData Account() => new AccountData
    {
        Account = "zh-fan@example.com",
        Karma = 500,
        Crystals = 12,
    };

    private static CharacterData Character()
    {
        var character = new CharacterData { Id = 1, Name = "Balance", Level = 7 };
        character.Bag.Add(new ItemData { Slot = 0, ItemId = 1001, PrefabId = 3, Level = 5, Count = 1 });
        return character;
    }

    /// <summary>The packet as the 2014 client would receive it, run through the dialect filter.</summary>
    private static Packet Translated(Packet cs)
    {
        Assert.True(Zh2013Dialect.Supports(cs));
        Packet zh = Zh2013Dialect.Translate(cs);
        return ReferenceEquals(zh, cs) ? throw new InvalidOperationException("expected a rewrite, got the 2014 packet") : zh;
    }

    private static Packet Reader(Packet packet)
    {
        byte[] bytes = packet.GetByteStream();
        return new Packet(bytes[10..]); // past the packet's int32 size + int16 tag + int32 method id
    }

    private static int Len(Packet p) => p.GetByteStream().Length;

    // ---- the 2013 readers, field lists from the shipped client ----------------------------------

    private static void ReadAccountInfo2013(Packet ins)
    {
        Assert.Equal("zh-fan@example.com", ins.ReadString()); // account ...
        Assert.Equal("123456", ins.ReadString());             // password (constructor default in the protocol class)
        Assert.Equal("zh-fan@example.com", ins.ReadString()); // nickName = the account (Views.AccountInfo)
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt();
        EUserType.Read(ins, out _);
        ins.ReadInt();
        ins.ReadString();
        ins.ReadInt();
        ins.ReadNatureTime(); ins.ReadNatureTime(); ins.ReadNatureTime();
        ins.ReadString(); ins.ReadNatureTime(); ins.ReadString();
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); ins.ReadInt(); // FK/SK/level
    }

    private static SCharacterInfoLogin ReadCharacterLogin2013(Packet ins)
    {
        var v = new SCharacterInfoLogin();
        v.ID = ins.ReadInt();
        v.nickname = ins.ReadString();
        v.badgeNum = ins.ReadInt();
        ESex.Read(ins, out v.sex);
        v.style = ins.ReadInt();
        v.level = ins.ReadInt();
        v.Exp = ins.ReadUint64();
        v.creationTime = ins.ReadNatureTime();
        v.status = ins.ReadInt();
        vectorSItemuuid.Read(ins, out v.equipinfo);
        v.isTutorial = ins.ReadBool();
        return v; // and NOTHING more: no attrVec
    }

    // ---- rewritten packets parse with 2013 layouts, to the last byte ------------------------------

    [Fact]
    public void The2013AccountBlockEndsWithLevelAndNothingElse()
    {
        Packet cs = GameResponses.UserLoginOK(false, Views.AccountInfo(Account()),
            Views.CharacterList(Account(), TestData.Data));
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.False(ins.ReadBool()); // reconnected
        ReadAccountInfo2013(ins);
        int count = ins.ReadInt();
        Assert.Equal(0, count); // the account has no characters yet
        // 2014 tails gone: curCharSlotNum + reviveNum + empty mapEvent + SWellData(20) + isGetRevivalCard
        Assert.Equal(4 + 4 + 4 + 20 + 4, Len(cs) - Len(zh));
    }

    [Fact]
    public void The2013CharacterListCarriesTheLoginEntriesWithoutAttrVec()
    {
        AccountData account = Account();
        account.Characters.Add(Character());
        Packet cs = GameResponses.UserLoginOK(true, Views.AccountInfo(account),
            Views.CharacterList(account, TestData.Data));
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.True(ins.ReadBool());
        ReadAccountInfo2013(ins);
        Assert.Equal(1, ins.ReadInt());
        SCharacterInfoLogin entry = ReadCharacterLogin2013(ins);
        Assert.Equal("Balance", entry.nickname);
        Assert.Equal(7, entry.level);
        // the whole attrVec block (count + every attribute) plus the 36-byte account tail is gone
        Packet probe = new Packet();
        vectorAttrChange.Write(probe, Views.Attributes(Character(), TestData.Data));
        Assert.Equal(36 + probe.GetByteStream().Length, Len(cs) - Len(zh));
    }

    [Fact]
    public void The2013BagGroupHasNoStashBag()
    {
        CharacterData character = Character();
        character.Stash.Add(new ItemData { Slot = 0, ItemId = 3001, PrefabId = 2, Level = 9, Count = 1 });
        SBagGroup group = Views.Bags(character);
        Packet cs = GameResponses.SendBagInfo(group);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        SBagInfo equip = new SBagInfo(); SBagInfo.Read(ins, out equip);
        SBagInfo basebag = new SBagInfo(); SBagInfo.Read(ins, out basebag);
        SBagInfo bank = new SBagInfo(); SBagInfo.Read(ins, out bank);
        Assert.Single(basebag.iteminfos); // the item from the character's bag
        // the stash bag the 2014 wire carries is gone, exactly as big as it was serialized
        Packet probe = new Packet();
        SBagInfo.Write(probe, group.stashbag);
        Assert.Equal(probe.GetByteStream().Length, Len(cs) - Len(zh));
    }

    [Fact]
    public void The2013ShelfItemHasNoVisibilityFlag()
    {
        var offer = new SShopItemInfo
        {
            UUID = 7, ID = 1003, perfrab = 2, level = 10, guid = new ItemGuid(),
            enchant = 201, element = 0, gem = 703, moneyType = 1, price = 40000,
            isVisible = true, isRare = 0, leftBuyCount = 0, leftBuyTime = 0, isDiscount = 0,
        };
        var list = new vectorShopItemInfos { offer };
        Packet cs = GameResponses.SendShopIteminfo(false, list);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.False(ins.ReadBool());
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(7, ins.ReadInt());   // UUID is still first ...
        Assert.Equal(1003, ins.ReadInt());
        Assert.Equal(2, ins.ReadInt());
        Assert.Equal(10u, ins.ReadUint());
        ItemGuid.Read(ins, out _);
        Assert.Equal(201, ins.ReadInt());
        Assert.Equal(0, ins.ReadInt());
        Assert.Equal(703, ins.ReadInt());
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(40000, ins.ReadInt());
        Assert.Equal(0, ins.ReadInt());   // isRare right after price: isVisible is gone
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt();
        Assert.Equal(1, Len(cs) - Len(zh)); // exactly the one visibility bool was dropped
    }

    [Fact]
    public void The2013PurchaseAckLeavesTheUuidOut()
    {
        var info = new SBuyitemInfo
        {
            UUID = 42, ID = 20009, perfrab = 1, level = 1, enchant = 0, element = 0, gem = 0,
            count = 3, currencyType = 1,
        };
        Packet cs = GameResponses.BuyItemSuccAck(0, info);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.Equal(0, ins.ReadInt()); // isRare
        Assert.Equal(20009, ins.ReadInt()); // ID first - the 2014 UUID is dropped
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(1u, ins.ReadUint());
        ins.ReadInt(); ins.ReadInt(); ins.ReadInt();
        Assert.Equal(3u, ins.ReadUint());
        Assert.Equal((sbyte)1, ins.ReadInt8());
        Assert.Equal(4, Len(cs) - Len(zh)); // the leading UUID int is gone
    }

    [Fact]
    public void The2013DayRewardAnswerCarriesAFlagNotAStatusVector()
    {
        var reward = Proto.New<SDayReward>();
        reward.dayID = 3;
        reward.daytime = 2026_11_15;
        reward.returnCode = Proto.Error(EServerErrorType.eSuccess);
        Packet cs = GameResponses.RequestDayRewardOK(reward);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.Equal(3, ins.ReadInt());
        Assert.Equal(2026_11_15L, ins.ReadLong());
        Assert.True(ins.ReadBool());  // the 2013 plain success flag
        Assert.Equal(0, ins.ReadInt()); // empty vectorItemInfos
        // 2014 carries EServerErrorType(4) + empty dayRewardStatusVec(4); 2013 a bool + empty vec
        Assert.Equal(3, Len(cs) - Len(zh));
    }

    [Fact]
    public void The2013MissionProcessKeepsOneReliveCounter()
    {
        var process = Proto.New<SMissionProcess>();
        process.missionID = 101;
        process.crystalReliveNum = 1;
        process.karmaReliveNum = 2;
        process.exp = 999;
        process.karma = 500;
        Packet cs = GameResponses.SendMissionProcess(process);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.Equal(101u, ins.ReadUint());
        vectorSBranch.Read(ins, out _);
        Assert.Equal(2, ins.ReadInt()); // the karma relives - what the 2013 client could buy
        // crystalReliveNum + exp + karma + empty materialMap are gone
        Assert.Equal(16, Len(cs) - Len(zh));
    }

    [Fact]
    public void The2013FriendRewardOmitsLookAndSex()
    {
        var rewards = new mapFriendHireReward
        {
            [5] = new SFriendHireReward { charID = 5, charName = "Comrade", exp = 120, sk = 3, style = 4, sex = ESex.eSex_Female },
        };
        Packet cs = GameResponses.SendFriendRewardInfo(rewards);
        Packet zh = Translated(cs);

        Packet ins = Reader(zh);
        Assert.Equal(1, ins.ReadInt());
        Assert.Equal(5, ins.ReadInt());
        Assert.Equal(5, ins.ReadInt());
        Assert.Equal("Comrade", ins.ReadString());
        Assert.Equal(120, ins.ReadInt());
        Assert.Equal(3, ins.ReadInt());
        Assert.Equal(8, Len(cs) - Len(zh)); // no style/sex tail
    }

    // ---- what gets dropped, and how the dialect is chosen ----------------------------------------

    [Fact]
    public void MethodsthatThe2013ClientNeverHadAreDropped()
    {
        // the whole mail system: the 2013 client has no parser for any of it
        Assert.False(Zh2013Dialect.Supports(GameResponses.GetPlayerMailResult(new vectorMailInfos())));
        Assert.False(Zh2013Dialect.Supports(GameResponses.RecvNewMail(new vectorMailInfos())));
        Assert.False(Zh2013Dialect.Supports(GameResponses.GetWellDataResult(new SWellData(), 0)));
        // the one battle method newer than this build (its battle table has 74 of the 2014 75)
        Assert.False(Zh2013Dialect.Supports(BattleResponses.SkillObjectActive(7)));
        // and everything the client can answer to must survive
        Assert.True(Zh2013Dialect.Supports(GameResponses.UserLoginOK(false,
            Views.AccountInfo(Account()), Views.CharacterList(Account(), TestData.Data))));
        Assert.True(Zh2013Dialect.Supports(GameResponses.SendShopIteminfo(false, new vectorShopItemInfos())));
        Assert.True(Zh2013Dialect.Supports(BattleResponses.PickupItemSuccAck(7, Proto.Error(EServerErrorType.eSuccess))));
        // the 2013 revive notice shape the January build also reads must pass the filter
        Assert.True(Zh2013Dialect.Supports(Apr2013Dialect.PlayerReviveNumNTF2013(2)));
    }

    [Fact]
    public void ErrorCodesReachThe2013ClientInThe2013Numbering()
    {
        // EServerErrorType table of the shipped Assembly-CSharp.dll: the January build counts the
        // shared names under lower numbers than 2014 (its On_ReviveResult checks for raw 51 for
        // "not enough karma", which is where eGS_LessMoney_karma sits in both tables).
        Assert.Equal(51, Zh2013Dialect.ToClientError(51));  // less karma: same number in both
        Assert.Equal(51, Zh2013Dialect.ToClientError((int)EServerErrorType.eGS_LessMoney_fk));
        Assert.Equal(56, Zh2013Dialect.ToClientError(60));   // character name already taken
        Assert.Equal(89, Zh2013Dialect.ToClientError(94));   // bag full
        Assert.Equal(104, Zh2013Dialect.ToClientError(109)); // day reward already got
        Assert.Equal(110, Zh2013Dialect.ToClientError(115)); // mission/area not in the table
        Assert.Equal(117, Zh2013Dialect.ToClientError(123)); // unassigned talent points
        // codes this build never had pass through: no 2013 message exists for them anyway
        Assert.Equal(148, Zh2013Dialect.ToClientError(148)); // mail error: dropped upstream
    }

    [Fact]
    public void OnlyWebLoginsWithoutAVersionFieldAreTreatedAs2013Clients()
    {
        using var player = new Akaneiro.Server.Tests.Harness.TestPlayer(_output, webLogin: true);
        var user = player.World.WebUsers.Create("zh@example.com", "pw123");

        string modern = player.World.WebUsers.IssueToken(user.Uid, player.Clock.UtcNow, TimeSpan.FromHours(12));
        string legacy = player.World.WebUsers.IssueToken(user.Uid, player.Clock.UtcNow, TimeSpan.FromHours(12),
            ClientDialect.Zh2013);

        Assert.True(player.World.WebUsers.ValidateToken(modern, user.Uid, player.Clock.UtcNow, out ClientDialect modernDialect));
        Assert.Equal(ClientDialect.Modern, modernDialect);
        Assert.True(player.World.WebUsers.ValidateToken(legacy, user.Uid, player.Clock.UtcNow, out ClientDialect legacyDialect));
        Assert.Equal(ClientDialect.Zh2013, legacyDialect);
    }

    [Fact]
    public void A2013SessionNeverSeesMailPacketsOnTheWire()
    {
        using var player = new Akaneiro.Server.Tests.Harness.TestPlayer(_output, webLogin: true);
        var recording = new RecordingSink();
        var session = new Session(player.World, recording, NullLogger.Instance) { Dialect = ClientDialect.Zh2013 };

        session.Send(GameResponses.RecvNewMail(new vectorMailInfos()));
        session.Send(GameResponses.UserCharacterInfo(Views.CharacterBasic(Character())));

        Assert.Single(recording.Packets); // the mail push is gone, the character info stays
        Packet ins = Reader(recording.Packets[0]);
        Assert.Equal(1, ins.ReadInt()); // character id
        Assert.Equal("Balance", ins.ReadString()); // 2013 basic block starts
    }

    [Fact]
    public void TheJanuaryBountyPanelGetsThreatSuffixedEntriesAndWholeRegionAreas()
    {
        // MissionLevelToID arms a mission slot only on an exact match against the threat-suffixed
        // ids (5xy1..5xy4); the map base ids the 2014 client takes never open one, and
        // UpdateMapLockState lights a region icon only when the push carries its baked 5R00 id
        // (the UnlockArea button computes (map + 5) * 1000 + region * 100). Read the raw wire:
        // the 2014 decode cannot tell these id families apart.
        using var player = new Akaneiro.Server.Tests.Harness.TestPlayer(_output, webLogin: true).LoginNewCharacter();
        var recording = new RecordingSink();
        var zh = new Session(player.World, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Zh2013,
            Character = player.Character,
        };

        zh.HandlePacket(ProtocolGame_SendRequest.MissionListReq().GetByteStream()[6..]);
        Assert.Equal(2, recording.Packets.Count); // list, then area push (the 2013 UI opens on that)

        Packet list = Reader(recording.Packets[0]);
        var ids = new List<int>();
        int count = list.ReadInt();
        for (int i = 0; i < count; i++)
        {
            ids.Add(list.ReadInt());
            list.ReadInt(); list.ReadInt(); list.ReadBool(); list.ReadInt(); list.ReadInt(); list.ReadInt(); list.ReadInt();
            // coolDownTime, IsHaveCompleted, IsHaveTriggered, star, lastPassTime, canAcceptMaxLvl,
            // lastPassLvl - the January build shares the 2014 entry field list
        }
        // a fresh character: act 1 map 1 at threat 1, and nothing else; the tutorial is not
        // listed (its 0-region has no slot) and no entry carries the 2014 map base suffix
        Assert.Equal(new[] { 5111 }, ids);

        Packet areas = Reader(recording.Packets[1]);
        var areaIds = new List<int>();
        int areaCount = areas.ReadInt();
        for (int i = 0; i < areaCount; i++)
        {
            areaIds.Add(areas.ReadInt());
            areas.ReadBool();
        }
        Assert.Equal(new[] { 5100 }, areaIds); // whole act 1 region; no tutorial area, no map bases
    }

    [Fact]
    public void TheJanuaryUnlockChargesThePriceItsPanelShows()
    {
        // the January client displays its bundled MissionArea.Area ladder (raw karma before the
        // shard reform): Jawbone is 2400 there, not the shipped table's 50; the unlock must not
        // contradict the number the player clicked
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aka-zharea-" + Guid.NewGuid().ToString("N"));
        var world = new World(Options.Create(new ServerOptions { AutoSaveSeconds = 0 }),
            new AccountStore(dir), TestData.Data, new WebUserStore(dir),
            new LoginAllowlist(System.IO.Path.Combine(dir, "allowlist.txt")));
        var account = Account();
        account.Karma = 2399; // one karma under what the panel shows for Jawbone
        var character = Character();
        var recording = new RecordingSink();
        var zh = new Session(world, recording, NullLogger.Instance)
        {
            Dialect = ClientDialect.Zh2013,
            Account = account,
            Character = character,
        };

        zh.HandlePacket(ProtocolGame_SendRequest.unLockAreaReq(5200).GetByteStream()[6..]);
        Packet ack = Reader(recording.Packets[0]);
        Assert.Equal(5200, ack.ReadInt());
        Assert.Equal(Zh2013Dialect.ToClientError((int)EServerErrorType.eGS_LessMoney_karma), ack.ReadInt());
        Assert.Equal(2399, account.Karma);
        Assert.Empty(character.UnlockedAreas);

        // the same click with the shown price: charged exactly that, and the re-push arms the
        // region - threat-suffixed entry plus the 5R00 area flagged as bought
        account.Karma = 2400;
        recording.Packets.Clear();
        zh.HandlePacket(ProtocolGame_SendRequest.unLockAreaReq(5200).GetByteStream()[6..]);
        ack = Reader(recording.Packets[0]);
        Assert.Equal(5200, ack.ReadInt());
        Assert.Equal(0, ack.ReadInt());
        Assert.Equal(0, account.Karma);
        Assert.Equal(new[] { 5200 }, character.UnlockedAreas);

        Packet areas = Reader(recording.Packets.Single(p =>
            BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_MissionAreaInfoAck));
        var bought = new Dictionary<int, bool>();
        int areaCount = areas.ReadInt();
        for (int i = 0; i < areaCount; i++)
        {
            int areaId = areas.ReadInt();
            bought[areaId] = areas.ReadBool();
        }
        Assert.Equal(new[] { 5100, 5200 }, bought.Keys.OrderBy(id => id));
        Assert.True(bought[5200]);

        Packet list = Reader(recording.Packets.Single(p =>
            BitConverter.ToInt32(p.GetByteStream(), 6) == (int)ProtocolGame_METHOD.uMethodID_MissionListAck));
        var ids = new List<int>();
        int count = list.ReadInt();
        for (int i = 0; i < count; i++)
        {
            ids.Add(list.ReadInt());
            list.ReadInt(); list.ReadInt(); list.ReadBool(); list.ReadInt(); list.ReadInt(); list.ReadInt(); list.ReadInt();
        }
        Assert.Contains(5211, ids); // act 2's first map, threat-1 id, arms the new region's slot
    }

    private sealed class RecordingSink : Akaneiro.Server.Net.IPacketSink
    {
        public List<Packet> Packets { get; } = new List<Packet>();
        public void Send(Packet packet) => Packets.Add(packet);
        public void Close() { }
    }
}
