using System;
using System.Linq;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using UnityEngine;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The summon window's friend allies: the list offers three companions sized to the
/// character, hiring puts one next to the player, the client-driven ally hits monsters
/// (kills counted for the player), monsters turn on the ally when it stands closer, its
/// health is the server's to report, and the hire lasts 30 minutes over map changes.
/// </summary>
public sealed class FriendAllyTests
{
    private const int Shockwave = Friends.Shockwave;

    private readonly ITestOutputHelper _output;

    public FriendAllyTests(ITestOutputHelper output) => _output = output;

    private TestPlayer NextToAMonster(out SMonsterEnter monster)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        monster = player.AnnouncedMonsters()[0];
        player.MoveTo(monster.pos);
        return player;
    }

    private static int FriendId(TestPlayer player, int style) => Friends.FriendIdFor(player.Session.Character, style);

    private static SFriendCharInfo ListEntry(TestPlayer player, int friendId) =>
        player.Take("On_GetFriendListResult").Arg<vectorFriendCharInfo>(0)
            .Cast<SFriendCharInfo>().First(info => info.ID == friendId);

    [Fact]
    public void TheSummonWindowOffersThreeCompanionsSizedToTheCharacter()
    {
        using var player = NextToAMonster(out _);
        player.ClearInbox();

        player.Send(ProtocolBattle_SendRequest.GetFriendList());

        var list = player.Take("On_GetFriendListResult").Arg<vectorFriendCharInfo>(0);
        Assert.Equal(3, list.Count);
        Assert.All(list.Cast<SFriendCharInfo>(), info =>
        {
            Assert.Equal(player.Session.Character.Level, info.level);
            Assert.Contains(info.style, new[] { StarterLoadout.Prowess, StarterLoadout.Fortitude, StarterLoadout.Cunning });
            Assert.False(string.IsNullOrEmpty(info.nickname));
            Assert.Contains(info.equipinfo.Cast<itemuuid>(), item => item.slotPart == 6 && item.itemID != 0);
            Assert.Contains(info.skillVec.Cast<int>(), skillId => skillId == Shockwave);
        });
        Assert.NotEmpty(list.Cast<SFriendCharInfo>().Select(info => info.style).Distinct().ToList());
    }

    [Fact]
    public void AHiredFriendEntersTheWorldNextToThePlayer()
    {
        using var player = NextToAMonster(out _);
        int friendId = FriendId(player, StarterLoadout.Prowess);
        player.ClearInbox();

        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(friendId));

        var result = player.Take("On_SelectFriendAllyResult");
        Assert.Equal(friendId, result.Arg<int>(0));
        Assert.Equal(EServerErrorType.eSuccess, result.Arg<EServerErrorType>(1).Get());

        SFriendAllyEnter ally = player.Take<SFriendAllyEnter>("On_FriendAllyEnter");
        Assert.Equal(friendId, ally.charID);
        Assert.Equal(StarterLoadout.Prowess, ally.style);
        Assert.Contains(ally.attrVec.Cast<SAttributeChange>(), a =>
            a.attributeType.Get() == EAttributeType.ATTR_MaxHP && a.value > 0);
        Assert.True(ally.equipinfo.Count >= 4); // weapon, head, chest, legs
        Assert.NotEqual(0, player.Session.FriendAlly.ObjectId);
        Assert.True(SceneInstance.Distance(ally.pos, player.Session.Position) <= 3f);
    }

    [Fact]
    public void OnlyOneAllyServesAtATime()
    {
        using var player = NextToAMonster(out _);
        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Prowess)));
        player.Take("On_SelectFriendAllyResult");
        player.ClearInbox();

        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Fortitude)));

        Assert.Equal(EServerErrorType.eAlreadyHaveAlly,
            player.Take("On_SelectFriendAllyResult").Arg<EServerErrorType>(1).Get());
        Assert.Equal(StarterLoadout.Prowess, player.Session.FriendAlly.Character.Style);

        // and someone who is not on the list does not get hired either
        player.ClearInbox();
        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(12345));
        Assert.Equal(EServerErrorType.eFriendError_NoExist,
            player.Take("On_SelectFriendAllyResult").Arg<EServerErrorType>(1).Get());
    }

    [Fact]
    public void TheAllysSwingsFightMonstersAndTheirKillsPayThePlayer()
    {
        using var player = NextToAMonster(out var monster);
        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Prowess)));
        player.Take("On_FriendAllyEnter");
        int allyId = player.Session.FriendAlly.ObjectId;
        player.ClearInbox();

        ulong expBefore = player.Session.Character.Exp;
        Monster wolf = player.Session.Scene.Monsters.First(m => m.ObjectId == monster.objectID);

        for (int swing = 0; swing < 20 && wolf.IsAlive; swing++)
        {
            player.Send(ProtocolBattle_SendRequest.UseAllyNormalAttack(
                new EWeaponType(EWeaponType.mainWeapon), (uint)monster.objectID, 0));
            player.Wait(0.6);
        }

        Assert.False(wolf.IsAlive, "the companion's swings should have brought the wolf down");
        var answer = player.Take("On_UseNormalAttackResult");
        Assert.Equal(allyId, (int)answer.Arg<SUseSkillResult>(1).sourceObjectID);
        Assert.Contains(player.TakeAll("On_ObjectLeave"),
            m => m.Arg<vectorInt>(0).Cast<int>().Contains(monster.objectID));
        Assert.True(player.Session.Character.Exp > expBefore, "kills by the ally count for the player");
    }

    [Fact]
    public void TheAllyCastsWhatTheClientGaveItAndNothingElse()
    {
        using var player = NextToAMonster(out var monster);
        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Prowess)));
        player.Take("On_FriendAllyEnter");
        int allyId = player.Session.FriendAlly.ObjectId;
        player.ClearInbox();

        var at = monster.pos;
        player.Session.FriendAlly.Mp = 100_000; // enough energy for the test
        player.Send(ProtocolBattle_SendRequest.UseAllySkill((uint)allyId, (uint)Shockwave, 0, at, 0));

        var result = player.Take<SUseSkillResult>("On_UseFightSkillResult");
        Assert.Equal(Shockwave, (int)result.skillID);
        Assert.Equal(allyId, (int)result.sourceObjectID);
        Assert.True(result.skillEffectVec.Cast<SSkillEffect>().Any(e =>
            e.attributeChangeVec.Cast<SAttributeChange>().Any(a => a.value < 0)),
            "the shockwave hits around its position");

        // an ability the companion does not have fails like for the player
        player.ClearInbox();
        player.Send(ProtocolBattle_SendRequest.UseAllySkill((uint)allyId, 29999, 0, at, 0));
        Assert.Equal(EServerErrorType.eGS_SkillNotExist,
            player.Take("On_UseFightSkillFailed").Arg<EServerErrorType>(2).Get());
    }

    [Fact]
    public void MonstersTurnOnTheAllyWhenItStandsCloserAndItsHealthIsTheServers()
    {
        using var player = NextToAMonster(out var monster);
        player.Session.CurrentHp = 1_000_000; // so it is obvious who the wolves bite
        var wolfAt = monster.pos;

        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Fortitude)));
        player.Take("On_FriendAllyEnter");
        int allyId = player.Session.FriendAlly.ObjectId;

        player.MoveTo(new Vector3(wolfAt.x + 30f, wolfAt.y, wolfAt.z)); // the player steps well back
        player.Session.FriendAlly.Position = wolfAt;      // the ally stayed by the wolf
        player.ClearInbox();

        player.Wait(6);

        var sync = player.TakeAll("On_AllyRegenerate")
            .Where(m => m.Arg<int>(0) == allyId)
            .ToList();
        Assert.NotEmpty(sync);
        int lastHp = sync.Last().Arg<int>(1);
        Assert.True(0 < lastHp && lastHp < player.Session.FriendAlly.MaxHp,
            "the wolves bite the nearer companion, and its health is reported, not guessed");

        // and it goes down for good: no health left, and the player revives nobody automatically
        player.Session.FriendAlly.Hp = 1;
        player.ClearInbox();
        player.Wait(6);
        Assert.Contains(player.TakeAll("On_AllyRegenerate"),
            m => m.Arg<int>(0) == allyId && m.Arg<int>(1) == 0);
        Assert.Null(player.Session.FriendAlly);
    }

    [Fact]
    public void TheCompanionComesAlongIntoTheNextMapAndGoesHomeAfterThirtyMinutes()
    {
        using var player = NextToAMonster(out _);
        player.Send(ProtocolBattle_SendRequest.SelectFriendAlly(FriendId(player, StarterLoadout.Prowess)));
        int firstObjectId = player.Take<SFriendAllyEnter>("On_FriendAllyEnter").objectID;
        Assert.NotEqual(firstObjectId, 0);

        player.ClearInbox();
        player.Send(ProtocolBattle_SendRequest.EnterScene("Hub_Village"));
        player.Send(ProtocolBattle_SendRequest.MapLoadOK());
        player.Take<SCharacterStartInfo>("On_CharacterStartInfo");

        var again = player.Take<SFriendAllyEnter>("On_FriendAllyEnter");
        Assert.NotEqual(0, again.objectID);
        Assert.NotEqual(firstObjectId, again.objectID); // a new object in the new world
        Assert.Equal(player.Session.FriendAlly.ObjectId, again.objectID);

        player.Wait(30 * 60 + 5); // the promised duration, plus a tick

        Assert.Null(player.Session.FriendAlly);
    }
}
