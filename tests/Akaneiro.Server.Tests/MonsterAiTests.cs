using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

public sealed class MonsterAiTests
{
    private const int AniChase = 1;

    private readonly ITestOutputHelper _output;

    public MonsterAiTests(ITestOutputHelper output) => _output = output;

    private static bool FromMonster(Message m, int monster) => m.Arg<SUseSkillResult>(m.Name == "On_BeginUseSkill" ? 0 : 1).sourceObjectID == monster;

    private TestPlayer NextToAMonster(out SMonsterEnter monster)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        monster = player.AnnouncedMonsters()[0];
        player.MoveTo(monster.pos);
        return player;
    }

    [Fact]
    public void MonsterNoticesThePlayerAndAttacks()
    {
        using var player = NextToAMonster(out var monster);

        player.Wait(3);

        player.Take("On_UpdateAnimationNotify", m => m.Arg<int>(0) == monster.objectID && m.Arg<short>(2) == AniChase);
        var attack = player.Take("On_UseNormalAttackResult", m => FromMonster(m, monster.objectID)).Arg<SUseSkillResult>(1);
        Assert.Equal(1, attack.destObejctID);
    }

    [Fact]
    public void DamageLandsWhenTheAttackAnimationStrikes()
    {
        using var player = NextToAMonster(out var monster);

        // Step until the swing starts: the animation comes first, the damage only later.
        for (int step = 0; step < 100 && !player.Has("On_BeginUseSkill", m => FromMonster(m, monster.objectID)); step++)
            player.Wait(0.05);

        player.Take("On_BeginUseSkill", m => FromMonster(m, monster.objectID));
        Assert.False(player.Has("On_UseNormalAttackResult", m => FromMonster(m, monster.objectID)), "damage arrived with the animation start");

        player.Wait(0.6);
        player.Take("On_UseNormalAttackResult", m => FromMonster(m, monster.objectID));
    }

    [Fact]
    public void MonsterWalksTowardsAPlayerItHasSeen()
    {
        using var player = NextToAMonster(out var monster);
        player.Wait(1.5); // woken up and noticed

        var away = new UnityEngine.Vector3(monster.pos.x + 8f, monster.pos.y, monster.pos.z);
        player.MoveTo(away);
        player.Wait(1);

        Assert.True(player.Has("On_SetObjectVecPosition", m => m.Arg<int>(1) == monster.objectID), "no movement sent");
        var position = player.Scene.Find(monster.objectID).Position;
        Assert.True(position.x > monster.pos.x, "monster did not move towards the player");
    }

    [Fact]
    public void PlayerDiesAndCanBeRevived()
    {
        using var player = NextToAMonster(out _);

        for (int second = 0; second < 600 && !player.Session.IsDead; second++)
            player.Wait(1);

        Assert.True(player.Session.IsDead, "the monsters never killed the player");
        // Only a full attribute update with zero health opens the revive screen.
        var attributes = player.Take<vectorAttrChange>("On_ChangeCharacterAttribNTF");
        Assert.Equal(0, attributes.Cast<SAttributeChange>().First(a => a.attributeType.Get() == EAttributeType.ATTR_CurHP).value);

        // Dead players are left alone.
        player.ClearInbox();
        player.Wait(5);
        Assert.False(player.Has("On_UseNormalAttackResult"));

        player.Send(ProtocolBattle_SendRequest.ReviveReq(new EReviveType(1))); // free revival
        var revived = player.Take("On_ReviveResult");
        Assert.Equal(0, revived.Arg<EServerErrorType>(1).Get());
        int health = revived.Arg<vectorAttrChange>(2).Cast<SAttributeChange>().First(a => a.attributeType.Get() == EAttributeType.ATTR_CurHP).value;
        Assert.True(health > 0);
        Assert.False(player.Session.IsDead);
    }

    [Fact]
    public void MonstersDoNotJumpAfterAPause()
    {
        using var player = NextToAMonster(out var monster);
        player.Wait(0.5);
        var away = new UnityEngine.Vector3(monster.pos.x + 10f, monster.pos.y, monster.pos.z);
        player.MoveTo(away);
        player.Session.CurrentHp = 0; // lying dead: the AI pauses

        player.Wait(20);
        player.Session.CurrentHp = 100;
        var before = player.Scene.Find(monster.objectID).Position;
        player.Wait(0.15);

        var after = player.Scene.Find(monster.objectID).Position;
        Assert.True(Akaneiro.Server.Game.SceneInstance.Distance(before, after) < 2f, "monster covered the whole pause in one step");
    }

    [Fact]
    public void WaitingMonstersDoNothing()
    {
        // Monsters waiting for their trigger are not in the client's world: they must not attack.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        var waiting = player.Scene.Monsters.First(m => m.Dormant);
        player.MoveTo(waiting.Position);

        player.Wait(10);

        Assert.False(player.Has("On_UpdateAnimationNotify", m => m.Arg<int>(0) == waiting.ObjectId));
        Assert.False(player.Has("On_BeginUseSkill", m => m.Arg<SUseSkillResult>(0).sourceObjectID == waiting.ObjectId));
        Assert.Equal(waiting.Position.x, player.Scene.Find(waiting.ObjectId).Position.x);
    }

    private const int AniSleep = 5, AniSpawn = 6, AniWakeUp = 8;

    [Fact]
    public void SleepingMonsterWakesUpBeforeItChases()
    {
        // The tutorial's first wolf sleeps (spawner entrance Sleep).
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        var wolf = player.AnnouncedMonsters().Single();
        Assert.Equal(AniSleep, player.Take("On_UpdateAnimationNotify", m => m.Arg<int>(0) == wolf.objectID).Arg<short>(2));

        player.MoveTo(wolf.pos);
        player.Wait(0.2);
        Assert.Equal(AniWakeUp, player.Take("On_UpdateAnimationNotify", m => m.Arg<int>(0) == wolf.objectID).Arg<short>(2));
        Assert.False(player.Has("On_BeginUseSkill", m => FromMonster(m, wolf.objectID)), "attacked while waking up");

        player.Wait(2);
        Assert.Equal(AniChase, player.Take("On_UpdateAnimationNotify", m => m.Arg<int>(0) == wolf.objectID).Arg<short>(2));
    }

    [Fact]
    public void AmbushPlaysItsEntranceAndComesForThePlayer()
    {
        // Spawner 117 (entrance Spawn) waits behind the village wall and jumps in when the arena starts.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        player.MoveTo(player.Scene.TriggersById(254).Single().Center);
        var ambusher = player.Scene.MonstersOf(117).First();
        Assert.Equal(AniSpawn, player.Take("On_UpdateAnimationNotify", m => m.Arg<int>(0) == ambusher.ObjectId).Arg<short>(2));

        // Stand out of its sight: an ambusher knows where the player is anyway.
        var far = new UnityEngine.Vector3(ambusher.Position.x + 15f, ambusher.Position.y, ambusher.Position.z);
        player.MoveTo(far);
        // The entrance lasts as long as its animation (+ AdditinalResetTime) in the prefab.
        var times = ambusher.Type.SpawnSeconds;
        double entrance = times.Count > ambusher.SpawnAnimIndex && times[ambusher.SpawnAnimIndex] > 0 ? times[ambusher.SpawnAnimIndex] : 1.5;
        player.Wait(entrance - 0.3);
        Assert.False(player.Has("On_UpdateAnimationNotify", m => m.Arg<int>(0) == ambusher.ObjectId && m.Arg<short>(2) == AniChase), "moved during its entrance");

        player.Wait(1);
        Assert.True(player.Has("On_UpdateAnimationNotify", m => m.Arg<int>(0) == ambusher.ObjectId && m.Arg<short>(2) == AniChase));
    }
}

/// <summary>Every ambusher plays an entrance it really has (mixed groups ask for one index for all).</summary>
public sealed class EntranceTests
{
    [Theory]
    [InlineData("A2_M4")]
    [InlineData("A1_M3")]
    [InlineData("A2_M3")]
    public void AmbushersGetAnEntranceAnimationOfTheirOwn(string sceneName)
    {
        var scene = SceneInstance.Build(sceneName, 1, TestData.Data, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new Random(1));

        foreach (var monster in scene.Monsters.Where(m => m.Entrance == MonsterEntrance.Spawn && m.Type.SpawnSeconds.Count > 0))
        {
            Assert.InRange(monster.SpawnAnimIndex, 0, monster.Type.SpawnSeconds.Count - 1);
            Assert.True(monster.Type.SpawnSeconds[monster.SpawnAnimIndex] > 0, $"{monster.Type.Name} has no clip {monster.SpawnAnimIndex}");
        }
    }
}

/// <summary>A hit monster comes for the player; a swing the player walked out of misses.</summary>
public sealed class ProvokeTests
{
    private const short AniChase = 1;
    private readonly ITestOutputHelper _output;

    public ProvokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AMonsterHitFromAfarComesForThePlayer()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        var wolf = player.Scene.Find(player.AnnouncedMonsters()[0].objectID);
        player.MoveTo(new UnityEngine.Vector3(wolf.Position.x + 30, wolf.Position.y, wolf.Position.z));
        player.ClearInbox();

        player.Attack(wolf.ObjectId); // e.g. a ranged ability; the server does not check the distance

        Assert.True(player.Has("On_UpdateAnimationNotify", m => m.Arg<int>(0) == wolf.ObjectId && m.Arg<short>(2) == AniChase));
    }

    [Fact]
    public void WalkingOutOfASwingDodgesIt()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        player.Session.CurrentHp = 1_000_000;
        var wolf = player.Scene.Find(player.AnnouncedMonsters()[0].objectID);
        player.MoveTo(wolf.Position);

        // Wait until a swing starts, then step far away before it lands.
        for (int i = 0; i < 40 && !player.Has("On_BeginUseSkill", m => m.Arg<SUseSkillResult>(0).sourceObjectID == wolf.ObjectId); i++)
            player.Wait(0.15);
        player.MoveTo(new UnityEngine.Vector3(wolf.Position.x + 20, wolf.Position.y, wolf.Position.z));
        player.ClearInbox();
        player.Wait(0.6);

        var hit = player.TakeAll("On_UseNormalAttackResult").Select(m => m.Arg<SUseSkillResult>(1)).FirstOrDefault(r => r.sourceObjectID == wolf.ObjectId);
        Assert.NotNull(hit);
        Assert.Empty(hit.skillEffectVec);
    }
}

/// <summary>The hit of a basic attack reaches the client when the swing lands (AttackNotify of the clip).</summary>
public sealed class SwingTimingTests
{
    [Fact]
    public void TheHitComesWhenTheSwingLands()
    {
        using var player = new TestPlayer().LoginNewCharacter();
        player.EnterTutorial();
        var wolf = player.AnnouncedMonsters()[0];
        player.MoveTo(wolf.pos);
        player.ClearInbox();

        player.Send(ProtocolBattle_SendRequest.UseNormalAttack(new EWeaponType(EWeaponType.mainWeapon), (uint)wolf.objectID, 0));
        player.Wait(0.1);
        Assert.False(player.Has("On_UseNormalAttackResult", m => m.Arg<SUseSkillResult>(1).sourceObjectID == 1), "the hit came before the swing");

        player.Wait(BattleHandler.MaxSwingHit);
        Assert.True(player.Has("On_UseNormalAttackResult", m => m.Arg<SUseSkillResult>(1).sourceObjectID == 1));
    }
}

/// <summary>The client's monster states: it only walks paths in the state they belong to.</summary>
public sealed class ClientStateTests
{
    private const short AniChase = 1, AniAttack = 3;
    private readonly ITestOutputHelper _output;

    public ClientStateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AfterASwingTheMonsterIsToldToChaseAgainBeforeItFollows()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        player.Session.CurrentHp = 1_000_000;
        var wolf = player.Scene.Find(player.AnnouncedMonsters()[0].objectID);
        player.MoveTo(wolf.Position);
        for (int i = 0; i < 40 && !player.Has("On_UpdateAnimationNotify", m => m.Arg<int>(0) == wolf.ObjectId && m.Arg<short>(2) == AniAttack); i++)
            player.Wait(0.15);
        player.Wait(1);
        player.ClearInbox();

        player.MoveTo(new UnityEngine.Vector3(wolf.Position.x + 8, wolf.Position.y, wolf.Position.z));
        player.Wait(0.5);

        var order = player.Inbox.Where(m => (m.Name == "On_UpdateAnimationNotify" || m.Name == "On_SetObjectVecPosition") && m.Arg<int>(m.Name == "On_SetObjectVecPosition" ? 1 : 0) == wolf.ObjectId).ToList();
        Assert.NotEmpty(order);
        Assert.Equal("On_UpdateAnimationNotify", order[0].Name);
        Assert.Equal(AniChase, order[0].Arg<short>(2));
    }

    [Fact]
    public void AnAmbushLandsWhereItsJumpEnds()
    {
        // Spawner 117 jumps over the village wall when the arena starts (trigger 254).
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        player.MoveTo(player.Scene.TriggersById(254).Single().Center);
        var ambusher = player.Scene.MonstersOf(117).First(m => m.Waypoint != null);

        // During its spawn state the client only takes a single position (SetObjectPosition).
        var landing = player.Take("On_SetObjectPosition", m => m.Arg<int>(0) == ambusher.ObjectId).Arg<UnityEngine.Vector3>(1);
        Assert.Equal(ambusher.Waypoint!.Value.x, landing.x, 3);
        Assert.Equal(ambusher.Waypoint!.Value.z, landing.z, 3);
    }
}

/// <summary>Ranged monsters (Amano Lobber, Ghostclaw Priest, ...) throw balls instead of hitting from afar.</summary>
public sealed class ThrowerTests
{
    private readonly ITestOutputHelper _output;

    public ThrowerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ALobberThrowsABallThatHitsThePlayer()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5131);
        player.AcceptMission(5131);
        player.EnterScene("A1_M3");
        player.Session.CurrentHp = 1_000_000;
        var lobber = player.Scene.Monsters.First(m => m.Type.Name == "Amano Lobber");
        lobber.Dormant = false;
        player.MoveTo(new UnityEngine.Vector3(lobber.Position.x + 6, lobber.Position.y, lobber.Position.z));

        for (int i = 0; i < 80 && !player.Has("On_BulletHit"); i++)
            player.Wait(0.15);

        var begin = player.Take("On_BeginUseSkill", m => m.Arg<SUseSkillResult>(0).sourceObjectID == lobber.ObjectId).Arg<SUseSkillResult>(0);
        Assert.Equal(30006, begin.skillID);
        var ball = player.Take("On_SkillObjectEnter").Arg<SSkillObjectEnter>(0);
        Assert.Equal(lobber.ObjectId, ball.ownerObjectID);
        var hit = player.Take("On_BulletHit");
        Assert.Equal(ball.objectID, hit.Arg<int>(0));
        Assert.NotEmpty(hit.Arg<SSkillEffect>(1).attributeChangeVec);
    }
}

/// <summary>The ball leaves the hand when the throw animation lets go.</summary>
public sealed class ThrowTimingTests
{
    [Fact]
    public void TheLobbersBallFliesWhenItsArmComesDown()
    {
        using var player = new TestPlayer().LoginNewCharacter();
        player.Unlock(5131);
        player.AcceptMission(5131);
        player.EnterScene("A1_M3");
        player.Session.CurrentHp = 1_000_000;
        var lobber = player.Scene.Monsters.First(m => m.Type.Name == "Amano Lobber");
        lobber.Dormant = false;
        player.MoveTo(new UnityEngine.Vector3(lobber.Position.x + 6, lobber.Position.y, lobber.Position.z));
        for (int i = 0; i < 80 && !player.Has("On_BeginUseSkill", m => m.Arg<SUseSkillResult>(0).sourceObjectID == lobber.ObjectId); i++)
            player.Wait(0.05);

        player.Wait(0.8);
        Assert.False(player.Has("On_SkillObjectEnter"), "the ball flew before the arm came down (1.03 s)");
        player.Wait(0.4);
        Assert.True(player.Has("On_SkillObjectEnter"));
    }
}
