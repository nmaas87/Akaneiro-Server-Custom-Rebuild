using System;
using System.Linq;
using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Protocol;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The per-request gates and background upkeep in battle: only one swing may be in flight,
/// a scene change ends the old scene's fighting immediately, the server mirrors the client's
/// HP regeneration on every tick, food buffs die when their time is up (even after a refresh
/// or a map change), missions must be unlocked to be accepted, and a scroll revive pays with
/// a scroll.
/// </summary>
public sealed class BattleGuardTests
{
    private const byte Bag = 2;
    private const int ChrysanthemumTea = 20020, TeaBuff = 75702, ReviveScroll = 20103;

    private readonly ITestOutputHelper _output;

    public BattleGuardTests(ITestOutputHelper output) => _output = output;

    private TestPlayer NextToAMonster(out SMonsterEnter monster)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        monster = player.AnnouncedMonsters()[0];
        player.MoveTo(monster.pos);
        return player;
    }

    private TestPlayer InVillage()
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village");
        player.ClearInbox();
        return player;
    }

    private static ItemData Give(TestPlayer player, int itemId, int slot, int count = 1)
    {
        var item = new ItemData { ItemId = itemId, PrefabId = 0, Level = 1, Slot = slot, Count = count };
        player.Session.Character.Bag.Add(item);
        return item;
    }

    private bool FromMonster(TestPlayer player, Message m) =>
        m.Name == "On_UseNormalAttackResult" && m.Arg<SUseSkillResult>(1).sourceObjectID != (uint)Session.PlayerObjectId;

    [Fact]
    public void ASecondSwingCannotStartBeforeTheFirstOneLanded()
    {
        using var player = NextToAMonster(out var monster);
        player.Session.CurrentHp = 1_000_000;
        player.ClearInbox();

        // Two attack requests back to back, without letting the clock pass the swing time:
        // a spamming client gets one answer, not two - the honest ones already gate on the
        // animation and never send the second.
        player.Send(ProtocolBattle_SendRequest.UseNormalAttack(new EWeaponType(EWeaponType.mainWeapon), (uint)monster.objectID, 0));
        player.Send(ProtocolBattle_SendRequest.UseNormalAttack(new EWeaponType(EWeaponType.mainWeapon), (uint)monster.objectID, 0));
        player.Wait(BattleHandler.MaxSwingHit + 1);

        // exactly one answer for the two requests
        Assert.Single(player.TakeAll("On_UseNormalAttackResult",
            m => m.Arg<SUseSkillResult>(1).sourceObjectID == (uint)Session.PlayerObjectId));
    }

    [Fact]
    public void EnteringANewSceneStopsTheOldMonstersMidLoad()
    {
        using var player = NextToAMonster(out _);
        player.Session.CurrentHp = 1_000_000;

        // The wolves fight: while standing next to them damage lands.
        player.Wait(3);
        Assert.True(player.Has("On_UseNormalAttackResult", m => FromMonster(player, m)), "the monsters should be attacking");

        player.ClearInbox();
        player.Send(ProtocolBattle_SendRequest.EnterScene("Hub_Village")); // client starts loading

        // the old scene world must be gone at once
        Assert.Null(player.Session.Scene);
        player.Wait(5); // many AI ticks worth of the old wolves' attacks

        Assert.False(player.Has("On_UseNormalAttackResult", m => FromMonster(player, m)),
            "monsters of the old map must not fight during the load");

        player.Send(ProtocolBattle_SendRequest.MapLoadOK());
        player.Take<SCharacterStartInfo>("On_CharacterStartInfo");
        Assert.Empty(player.Scene.Monsters); // and none of them carry over
    }

    [Fact]
    public void HealthGrowsOnItsOwnWhileThePlayerJustStandsThere()
    {
        using var player = InVillage();
        Give(player, ChrysanthemumTea, 0, count: 1);
        player.Send(ProtocolGame_SendRequest.useItem(Bag, 0)); // doubles the HP regen rate for 10 min
        player.Session.CurrentHp = 100;
        player.ClearInbox();

        player.Wait(30); // standing, doing nothing: no skill, no attribute request

        Assert.True(player.Session.CurrentHp > 100,
            "the client regenerates continuously; the server must mirror that every tick, not only on skill use");
    }

    [Fact]
    public void ARefreshedBuffSurvivesTheOldExpiryAndDiesOnceAtTheNewOne()
    {
        using var player = InVillage();
        Give(player, ChrysanthemumTea, 0, count: 2);

        player.Send(ProtocolGame_SendRequest.useItem(Bag, 0)); // drink one: ends at t+600 s
        player.Wait(300);
        player.Send(ProtocolGame_SendRequest.useItem(Bag, 0)); // drink again: ends at t+900 s

        player.ClearInbox();
        player.Wait(330); // past the FIRST expiry - the old schedule must not fire

        Assert.False(HasTeaDelete(player), "a refreshed buff must not be deleted at the old expiry");

        player.Wait(300); // past the second one

        var deletes = Deletes(player).Where(e => e.statustID == TeaBuff).Count();
        Assert.Equal(1, deletes);
        Assert.DoesNotContain(player.Session.ActiveBuffs, b => b.Buff.Id == TeaBuff);
    }

    [Fact]
    public void BuffsStillExpireAcrossAMapChange()
    {
        using var player = InVillage();
        Give(player, ChrysanthemumTea, 0);
        player.Send(ProtocolGame_SendRequest.useItem(Bag, 0));

        player.Wait(120);
        player.EnterScene("Hub_Village_Tutorial"); // the buff rides along; the new map must still end it
        player.ClearInbox();
        player.Wait(500); // now past the 600 s duration

        Assert.True(HasTeaDelete(player), "the icon must come off after the map change too");
        Assert.Empty(player.Session.ActiveBuffs.Where(b => b.Buff.Id == TeaBuff));
    }

    private static bool HasTeaDelete(TestPlayer player) => Deletes(player).Any(e => e.statustID == TeaBuff);

    private static System.Collections.Generic.IEnumerable<SStatusEffect> Deletes(TestPlayer player) =>
        player.Inbox.Where(m => m.Name == "On_UpdateStatusEffect")
            .SelectMany(m => m.Arg<vectorStatusEffects>(0).Cast<SStatusEffect>())
            .Where(e => e.statusOperateType.Get() == EStatusOperateType.eStatusType_Delete);

    [Fact]
    public void LockedAndUnknownMissionsAreRefused()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();

        // A1_M4 is far into the progression; a fresh character may not jump into it.
        player.Send(ProtocolGame_SendRequest.AcceptMission(1001, 5141));
        player.Take("On_AcceptMissionFail");

        player.Send(ProtocolGame_SendRequest.AcceptMission(1001, 99999)); // not even a mission
        player.Take("On_AcceptMissionFail");

        Assert.Null(player.Session.Mission); // nothing was accepted
    }

    [Fact]
    public void AScrollReviveFailsWithoutAScroll()
    {
        using var player = DieNextToMonsters();

        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(3))); // item revive
        var result = player.Take("On_ReviveResult");

        Assert.Equal(EServerErrorType.eGS_LessReviveItem, result.Arg<EServerErrorType>(1).Get());
        Assert.True(player.Session.IsDead, "a refused revive leaves the player lying there");
    }

    [Fact]
    public void AScrollReviveSpendsOneScrollAndUpdatesTheCounter()
    {
        using var player = DieNextToMonsters();
        Give(player, ReviveScroll, 0, count: 2);
        player.ClearInbox();

        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(3)));

        Assert.Equal(EServerErrorType.eSuccess, player.Take("On_ReviveResult").Arg<EServerErrorType>(1).Get());
        Assert.False(player.Session.IsDead);
        Assert.Equal(1, player.Character.Bag.Single(i => i.ItemId == ReviveScroll).Count);
        Assert.Equal(1, player.Take("On_UpdateReviveNum").Arg<int>(0)); // one scroll left on the account
    }

    [Fact]
    public void TheAprilRevivalPrayerCostsOneShard()
    {
        using var player = DieNextToMonsters();
        player.Session.Dialect = ClientDialect.Apr2013;
        player.Session.Account.Crystals = 5;
        player.ClearInbox();

        // The April menu's middle option: stand up where you fell, one crystal (shard) gone.
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1)));

        Assert.Equal(EServerErrorType.eSuccess, player.Take("On_ReviveResult").Arg<EServerErrorType>(1).Get());
        Assert.False(player.Session.IsDead);
        Assert.Equal(4, player.Session.Account.Crystals);

        // And the 2013-shaped scroll count notice (one int, scrolls on the account): the 2014
        // harness receiver can't decode it, so it waits in the raw bin.
        byte[] ntf = Assert.Single(player.RawPackets((int)ProtocolBattle_METHOD.uMethodID_PlayerReviveNumNTF));
        Assert.Equal(8, ntf.Length); // method id + exactly one payload int
    }

    [Fact]
    public void TheAprilShinigamiScrollButtonSpendsTheScroll()
    {
        using var player = DieNextToMonsters();
        player.Session.Dialect = ClientDialect.Apr2013;
        Give(player, ReviveScroll, 0, count: 1);
        player.ClearInbox();

        // Both scroll buttons (the build sends 4 and 3) pay with the bag's Shinigami Scroll.
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(4)));

        Assert.Equal(EServerErrorType.eSuccess, player.Take("On_ReviveResult").Arg<EServerErrorType>(1).Get());
        Assert.False(player.Session.IsDead);
        Assert.Equal(0, player.Character.Bag.Count(i => i.ItemId == ReviveScroll));
    }

    [Fact]
    public void TheAprilSkillListCarriesTheInnateReviveShockwave()
    {
        using var apr = new TestPlayer(_output).LoginNewCharacter();
        apr.Session.Dialect = ClientDialect.Apr2013;
        apr.ClearInbox();
        apr.Send(ProtocolGame_SendRequest.UserSelectCharacter(apr.Character.Id));

        // UseAbilityFromID only finds a state that this list registered - without 12001 the
        // client's revive stand-up is a silent no-op and the hunter stays on the ground.
        var skills = apr.Take<SPlayerSkillInfo>("On_SendSkillInfo");
        Assert.Contains(skills.skillInfoVec.Cast<SSkillInfo>(), s => s.skillID == BattleHandler.ReviveShockwave);

        using var modern = new TestPlayer(_output).LoginNewCharacter();
        modern.ClearInbox();
        modern.Send(ProtocolGame_SendRequest.UserSelectCharacter(modern.Character.Id));
        var modernSkills = modern.Take<SPlayerSkillInfo>("On_SendSkillInfo");
        Assert.DoesNotContain(modernSkills.skillInfoVec.Cast<SSkillInfo>(), s => s.skillID == BattleHandler.ReviveShockwave);
    }

    [Fact]
    public void TheAccountScreenCountsTheScrollsInTheBags()
    {
        using var first = new TestPlayer(_output).LoginAs("acct-scrollcount");
        Give(first, ReviveScroll, 0, count: 2);
        first.Session.SaveAccount();
        first.Send(ProtocolGame_SendRequest.UserLogout()); // fresh login: the account must come off disk

        using var second = new TestPlayer(first.World, _output);
        second.SendLogin("acct-scrollcount");

        var account = second.Take<SAccountInfo>("On_UserLoginOK", 1);
        Assert.Equal(2, account.reviveNum);
    }

    private TestPlayer DieNextToMonsters()
    {
        var player = NextToAMonster(out _);
        for (int second = 0; second < 600 && !player.Session.IsDead; second++)
            player.Wait(1);
        if (!player.Session.IsDead)
            throw new Xunit.Sdk.XunitException("the monsters never killed the player");
        return player;
    }

    [Fact]
    public void TheReviveSlamHitsAndHurlsBackTheMonstersAroundThePlayer()
    {
        using var player = NextToAMonster(out var nearest);
        var wolf = player.Scene.Find(nearest.objectID);
        wolf.Hp = 5000; // it should survive the slam, so its shove can be seen
        for (int second = 0; second < 600 && !player.Session.IsDead; second++)
            player.Wait(1);
        Assert.True(player.Session.IsDead, "the monsters never killed the player");

        var pos = player.Session.Position;
        int wolfHp = wolf.Hp;

        player.ClearInbox();
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1))); // free revival
        player.Take("On_ReviveResult");

        // What the client does the instant a revive succeeds (On_ReviveResult ->
        // UseAbilityFromID -> UseFightSkill): the shockwave, which the hunter never learned.
        player.Send(ProtocolBattle_SendRequest.UseFightSkill(BattleHandler.ReviveShockwave, 0, pos, 0));
        var result = player.Take<SUseSkillResult>("On_UseFightSkillResult");

        // It lands on the wolves around the grave: damage, and the table's knockback status
        // (40002) rides along under each hit.
        var effects = result.skillEffectVec.Cast<SSkillEffect>().ToList();
        Assert.Contains(effects, e => e.targetID == wolf.ObjectId);
        Assert.True(wolf.Hp < wolfHp, "the slam never damaged the nearest wolf");
        Assert.All(effects, e => Assert.Contains(e.statusEffectVec.Cast<SStatusEffect>(), s => s.statustID == 40002));

        // The physical shove away from the player (behavior 1 is what the client applies).
        player.Take("On_SetKnockBackSpeed", m => m.Arg<int>(0) == wolf.ObjectId);

        // And the 7 s revival invulnerability on the caster (40018).
        Assert.Contains(result.statusEffectVec.Cast<SStatusEffect>(), s => s.statustID == 40018);
        Assert.True(player.Session.InvulnerableUntil > player.Clock.UtcNow);
    }

    [Fact]
    public void TheRisenPlayerCannotBeKnockedDownAgainInsideTheShield()
    {
        using var player = DieNextToMonsters();
        Give(player, ReviveScroll, 0);

        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(3))); // scroll revival
        Assert.Equal(EServerErrorType.eSuccess, player.Take("On_ReviveResult").Arg<EServerErrorType>(1).Get());

        // The scroll revive casts RevivePrayer_Shockwave (12002): a 12 s shield, not 7.
        player.Send(ProtocolBattle_SendRequest.UseFightSkill(
            BattleHandler.RevivePrayerShockwave, 0, player.Session.Position, 0));
        var result = player.Take<SUseSkillResult>("On_UseFightSkillResult");
        Assert.Contains(result.statusEffectVec.Cast<SStatusEffect>(), s => s.statustID == 40019);

        int full = player.Session.CurrentHp;
        Assert.True(player.Session.InvulnerableUntil > player.Clock.UtcNow.AddSeconds(7),
            "the scroll shield must outlast the plain one");

        // The wolves get up and swing inside the window - the shield eats every blow.
        player.Wait(6);
        Assert.Equal(full, player.Session.CurrentHp);
    }

    [Fact]
    public void TheRevivalShieldGlowsItsSevenSecondsAndThenComesOff()
    {
        using var player = DieNextToMonsters();
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1)));
        player.Take("On_ReviveResult");
        player.Send(ProtocolBattle_SendRequest.UseFightSkill(
            BattleHandler.ReviveShockwave, 0, player.Session.Position, 0));
        player.Take("On_UseFightSkillResult");
        player.ClearInbox();

        player.Wait(6); // still inside the window: the glow keeps its icon
        Assert.False(Deletes(player).Any(e => e.statustID == 40018), "the shield is still running");

        player.Wait(2); // past its 7 s

        Assert.True(Deletes(player).Any(e => e.statustID == 40018),
            "glow and buff-list entry only leave when the server sends the Delete");
        Assert.DoesNotContain(player.Session.ActiveBuffs, b => b.Buff.Id == 40018);
    }

    [Fact]
    public void TheRevivalShieldComesOffOnTheNextMap()
    {
        using var player = DieNextToMonsters();
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1)));
        player.Take("On_ReviveResult");
        player.Send(ProtocolBattle_SendRequest.UseFightSkill(
            BattleHandler.ReviveShockwave, 0, player.Session.Position, 0));
        player.Take("On_UseFightSkillResult");

        player.ClearInbox();
        player.EnterScene(player.Session.SceneName); // the shield does not ride into the next map

        Assert.True(Deletes(player).Any(e => e.statustID == 40018),
            "the old map's glow and icon must come off at the scene change, not at the next revive");
        Assert.DoesNotContain(player.Session.ActiveBuffs, b => b.Buff.Id == 40018);
    }

    [Fact]
    public void IconsThatRanOutWhileDeadAreDeletedAgainOnTheRevive()
    {
        using var player = DieNextToMonsters();
        var teaRow = TestData.Data.Buffs[TeaBuff];

        // A buff still running while the corpse lies there, ending two seconds later: its Delete
        // goes out behind the death panel (deterministic injection instead of racing the wolves).
        player.Session.ActiveBuffs.Add(new ActiveBuff(teaRow, player.Clock.UtcNow.AddSeconds(2)));
        player.Wait(10); // now expired, deleted while dead
        Assert.DoesNotContain(player.Session.ActiveBuffs, b => b.Buff.Id == TeaBuff);

        player.ClearInbox(); // forget the Delete that went out during the death
        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1)));
        player.Take("On_ReviveResult");

        Assert.True(Deletes(player).Any(e => e.statustID == TeaBuff),
            "the revive must hand the buff list back clean, even for icons that died behind the death panel");
    }
}
