using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Level events: trigger volumes and what spawners set off when their monsters die.</summary>
public sealed class TriggerTests
{
    private const string Tutorial = "Hub_Village_Tutorial";
    private const int TutorialMission = 5011;

    // The tutorial's chain as the level designers built it (Hub_Village_Tutorial.unity):
    private const int FirstWolfTrigger = 246;   // walking in brings "The first Wolf"
    private const int FirstWolf = 104;          // its death fires 8004 (opens gate 540) and arms 8006/8007
    private const int ArenaTrigger = 51;        // walking in brings the first pack (spawner 106)
    private const int FirstPack = 106;          // 2 kills bring "The Ambush Wolf 1" (spawner 110)
    private const int AmbushWolf1 = 110;

    private readonly ITestOutputHelper _output;

    public TriggerTests(ITestOutputHelper output) => _output = output;

    private static bool IsAnimation(Message m, int objectId) => m.Arg<int>(0) == objectId;

    private TestPlayer InTutorial()
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.AcceptMission(TutorialMission);
        player.EnterScene(Tutorial);
        return player;
    }

    private static void KillSpawner(TestPlayer player, int spawnerId, int count = int.MaxValue)
    {
        foreach (var monster in player.Scene.MonstersOf(spawnerId).Where(m => m.IsActive).Take(count).ToList())
            player.Kill(monster.ObjectId);
    }

    [Fact]
    public void GateTriggerAddressesTheObjectByTheIdTheClientKnows()
    {
        using var player = InTutorial();
        TriggerInfo gate = player.Scene.TriggersById(8002).Single();

        player.MoveTo(gate.Center);

        // The SelfAnimation component's own id (530), not the combined id of the level export.
        player.Take("On_TriggerAnimationResult", m => IsAnimation(m, 530));
    }

    [Fact]
    public void TriggerFiresOnlyAsOftenAsAllowed()
    {
        using var player = InTutorial();
        TriggerInfo gate = player.Scene.TriggersById(8002).Single();

        player.MoveTo(gate.Center);
        player.MoveTo(player.Session.Position with { x = gate.Center[0] + 30 });
        player.MoveTo(gate.Center);

        Assert.Single(player.TakeAll("On_TriggerAnimationResult", m => IsAnimation(m, 530)));
    }

    [Fact]
    public void UnlimitedTriggerFiresOnEnteringNotOnEveryStep()
    {
        // A1_M1 "start" (MaxCount 0) hides an object each time the player walks in; walking around
        // inside its 18 m box used to resend that on every movement packet.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.AcceptMission(5111);
        player.EnterScene("A1_M1");
        TriggerInfo start = player.Scene.Triggers.Single(t => t.Name == "PF_Collision_Trigger (start)");
        player.MoveTo(start.Center.Select((v, i) => i == 0 ? v + 50 : v).ToArray());
        player.Scene.TriggerFireCount.Remove(start);

        player.MoveTo(start.Center);
        player.MoveTo(start.Center.Select((v, i) => i == 0 ? v + 1 : v).ToArray());
        player.MoveTo(start.Center.Select((v, i) => i == 2 ? v + 1 : v).ToArray());
        Assert.Equal(1, player.Scene.TriggerFireCount.GetValueOrDefault(start));

        player.MoveTo(start.Center.Select((v, i) => i == 0 ? v + 50 : v).ToArray());
        player.MoveTo(start.Center);
        Assert.Equal(2, player.Scene.TriggerFireCount.GetValueOrDefault(start));
    }

    [Fact]
    public void FirstWolfComesWhenThePlayerWalksUp()
    {
        // The tutorial starts at the villager; the wolf only appears once the player walks on
        // ("a wolf is up ahead"), after the tutorial has set up the quest arrow for it.
        using var player = InTutorial();
        Assert.Empty(player.AnnouncedMonsters());

        player.MoveTo(player.Scene.TriggersById(FirstWolfTrigger).Single().Center);

        var wolf = Assert.Single(player.Scene.MonstersOf(FirstWolf));
        Assert.False(wolf.Dormant);
        Assert.Contains(player.AnnouncedMonsters(), m => m.objectID == wolf.ObjectId);
    }
    [Fact]
    public void BarrierOpensASecondAfterTheFirstWolfDies()
    {
        using var player = InTutorial();
        player.MoveTo(player.Scene.TriggersById(FirstWolfTrigger).Single().Center);

        KillSpawner(player, FirstWolf);
        Assert.False(player.Has("On_TriggerAnimationResult", m => IsAnimation(m, 540)), "opened before its delay");
        player.Wait(1.2);

        // Gate 540 hidden (Args[5] = display) and walkable (Args[4] = collision).
        var open = player.Take("On_TriggerAnimationResult", m => IsAnimation(m, 540));
        Assert.False(open.Arg<bool>(4));
        Assert.False(open.Arg<bool>(5));
    }

    [Fact]
    public void FirstWolfArmsTheTriggersItToggles()
    {
        using var player = InTutorial();
        var cinematic = player.Scene.TriggersById(8006).Single();
        Assert.DoesNotContain(cinematic, player.Scene.ArmedTriggers);
        player.MoveTo(player.Scene.TriggersById(FirstWolfTrigger).Single().Center);

        KillSpawner(player, FirstWolf);

        Assert.Contains(cinematic, player.Scene.ArmedTriggers);
    }

    [Fact]
    public void OtherWolvesDoNotOpenTheBarrier()
    {
        using var player = InTutorial();
        player.MoveTo(player.Scene.TriggersById(ArenaTrigger).First().Center);
        var other = player.Scene.MonstersOf(FirstPack).First(m => m.IsActive);

        player.Kill(other.ObjectId);
        player.Wait(2);

        // Walking into the arena closes 540 behind the player (8003); it must not open again.
        Assert.False(player.Has("On_TriggerAnimationResult", m => IsAnimation(m, 540) && !m.Arg<bool>(5)));
    }

    [Fact]
    public void KillingAPackBringsTheNextWave()
    {
        using var player = InTutorial();
        player.MoveTo(player.Scene.TriggersById(ArenaTrigger).First().Center);
        Assert.All(player.Scene.MonstersOf(AmbushWolf1), m => Assert.True(m.Dormant));

        KillSpawner(player, FirstPack, 2);
        player.Session.CurrentHp = 1_000_000; // only the waves are under test
        player.Wait(5); // the pack comes one after the other

        var wave = player.Scene.MonstersOf(AmbushWolf1).ToList();
        Assert.NotEmpty(wave);
        Assert.All(wave, m => Assert.False(m.Dormant));
        Assert.Contains(player.AnnouncedMonsters(), m => wave.Any(w => w.ObjectId == m.objectID));
    }

    [Fact]
    public void TutorialWavesLeadToTheBoss()
    {
        using var player = InTutorial();
        player.MoveTo(player.Scene.TriggersById(ArenaTrigger).First().Center);
        var boss = player.Scene.MonstersOf(112).Single(); // "BOSS"
        Assert.True(boss.Dormant);

        // Each pack that falls brings the next one: 106 -> 110 -> 115 -> boss (112).
        foreach (int spawner in new[] { FirstPack, AmbushWolf1, 115 })
        {
            player.MoveTo(player.Scene.MonstersOf(spawner).First().Position);
            KillSpawner(player, spawner);
            player.Session.CurrentHp = 1_000_000; // only the waves are under test
            player.Wait(5); // the next pack comes one after the other
        }

        Assert.False(boss.Dormant, "the boss never came");
    }

    [Fact]
    public void AmbushMonstersWaitForTheirTrigger()
    {
        using var player = InTutorial();

        var waiting = player.Scene.Monsters.Where(m => m.Dormant).ToList();

        Assert.NotEmpty(waiting);
        Assert.DoesNotContain(player.AnnouncedMonsters(), m => waiting.Any(w => w.ObjectId == m.objectID));
    }

    /// <summary>Every armed spawn trigger of every scene, as (scene, index in the scene's list, id).</summary>
    public static IEnumerable<object[]> SpawnTriggers() =>
        TestData.Data.Triggers
            .SelectMany(s => s.Value
                .Select((t, index) => (Trigger: t, Index: index))
                .Where(x => x.Trigger.TriggerOn && x.Trigger.Results.Any(r => r.Type == TriggerResultType.Spawn && !r.DisableSpawner))
                .Select(x => new object[] { s.Key, x.Index, x.Trigger.Id }))
            .ToList();

    [Theory]
    [MemberData(nameof(SpawnTriggers))]
    public void WalkingIntoACombatTriggerWakesItsMonsters(string scene, int index, int triggerId)
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene(scene);
        TriggerInfo trigger = player.Scene.Triggers[index];
        Assert.Equal(triggerId, trigger.Id);
        var spawners = trigger.Results.Where(r => r.Type == TriggerResultType.Spawn).Select(r => r.SpawnerId).ToHashSet();
        // Later waves of repeating groups come only once the first wave is dead (see RepeatingGroupsComeInWaves).
        var ambush = player.Scene.Monsters.Where(m => spawners.Contains(m.SpawnerId) && m.Wave == 0).ToList();

        player.MoveTo(trigger.Center);
        player.Wait(ambush.Select(m => m.AppearDelay).DefaultIfEmpty(0).Max() + 0.3); // packs arrive one after the other

        Assert.All(ambush, m => Assert.False(m.Dormant, $"monster {m.ObjectId} of spawner {m.SpawnerId} still waiting"));
        // The client hears about the ones near the player at once, the others when the player comes closer.
        var announced = player.AnnouncedMonsters().Select(m => m.objectID).ToHashSet();
        var near = ambush.Where(m => SceneInstance.Distance(m.Position, player.Session.Position) < 50f);
        Assert.All(near, m => Assert.Contains(m.ObjectId, announced));
    }

    [Theory]
    [InlineData(1, 50, 38)]  // threat 1: the easy captains, not the hard ones
    [InlineData(4, 38, 50)]  // threat 4: the other way round
    public void ThreatLevelDecidesWhichSpawnersTakePart(int threat, int present, int absent)
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5130 + threat);
        player.AcceptMission(5130 + threat); // A1_M3
        player.EnterScene("A1_M3");

        Assert.Equal(threat, player.Scene.Threat);
        Assert.Contains(present, player.Scene.Spawners.Keys);
        Assert.DoesNotContain(absent, player.Scene.Spawners.Keys);
        Assert.DoesNotContain(player.Scene.Monsters, m => m.SpawnerId == absent);
    }

    [Fact]
    public void TriggersWithTheSameIdCountSeparately()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("A2_M1");
        var twins = player.Scene.TriggersById(67).ToList();
        Assert.True(twins.Count > 1);

        foreach (var trigger in twins)
            player.MoveTo(trigger.Center);

        Assert.All(twins, t => Assert.Equal(1, player.Scene.TriggerFireCount.GetValueOrDefault(t)));
    }

    [Fact]
    public void FinishedTaskSetsOffItsTriggers()
    {
        // A3_M2: once 20 Amano Brutes are dead, the task's triggers (785;786;103) bring the boss.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5321);
        player.AcceptMission(5321);
        player.EnterScene("A3_M2");
        var boss = player.Scene.MonstersOf(139).Single(); // "Boss_Thief Lord Min"
        Assert.True(boss.Dormant);

        for (int i = 0; i < 20; i++)
            player.Session.Battle.RecordMissionProgress(MissionObjectiveType.Hunt, 101);

        player.Take("On_taskCompleteNTF");
        Assert.False(boss.Dormant, "the boss did not come");
    }

    [Fact]
    public void PullingALeverFiresItsTrigger()
    {
        using var player = InTutorial();

        player.Send(ProtocolBattle_SendRequest.ActiveTriggerReq(8004));

        player.Take("On_TriggerAnimationResult", m => IsAnimation(m, 540));
    }

    [Fact]
    public void FirstWolfComesAlone()
    {
        // The client puts a quest arrow on every new monster of the tutorial's target type and
        // removes only one per kill, so any extra wolf at the start leaves arrows floating around.
        using var player = InTutorial();
        player.MoveTo(player.Scene.TriggersById(FirstWolfTrigger).Single().Center);

        var wolf = Assert.Single(player.AnnouncedMonsters());
        Assert.Equal(FirstWolf, player.Scene.Find(wolf.objectID).SpawnerId);
    }

    [Fact]
    public void TriggerBoxesAreTurnedLikeInTheLevel()
    {
        // 246 is turned by 56 degrees: the spawn point lies next to it, not inside. A box turned
        // the wrong way round would reach the spawn point and bring the wolf too early.
        using var player = InTutorial();
        var trigger = player.Scene.TriggersById(FirstWolfTrigger).Single();
        double yaw = trigger.RotationY * Math.PI / 180;
        float along = trigger.Size[2] / 2 - 0.5f; // just inside the box's long side
        var insideAlongLongSide = new UnityEngine.Vector3(
            trigger.Center[0] + (float)(Math.Sin(yaw) * along), trigger.Center[1], trigger.Center[2] + (float)(Math.Cos(yaw) * along));

        player.MoveTo(insideAlongLongSide);

        Assert.Single(player.AnnouncedMonsters());
    }
    [Fact]
    public void BasketWolvesWaitForTheBasket()
    {
        // Spawner 108 is released by breaking a basket (BRK_Basket), not at the start.
        using var player = InTutorial();

        Assert.All(player.Scene.MonstersOf(108), m => Assert.True(m.Dormant));
    }

    [Fact]
    public void CinematicPlaysItsAnimations()
    {
        // Objects shown without their animation stand in a T-pose.
        using var player = InTutorial();

        player.Send(ProtocolBattle_SendRequest.ActiveTriggerReq(8006));

        var shown = player.TakeAll("On_TriggerAnimationResult");
        Assert.NotEmpty(shown);
        Assert.All(shown, m => Assert.False(string.IsNullOrEmpty(m.Arg<string>(1)), $"object {m.Arg<int>(0)} without animation"));
    }
}

public sealed class SpawnWaveTests
{
    private readonly ITestOutputHelper _output;

    public SpawnWaveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RepeatingGroupsComeInWaves()
    {
        // A1_M4 spawner 144 (threat 1): 4 wolves, SpawnRepeatCount 2 -> a second pack of 4 once
        // the first one is dead.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5141);
        player.AcceptMission(5141);
        player.EnterScene("A1_M4");
        var pack = player.Scene.MonstersOf(144).ToList();
        Assert.Equal(8, pack.Count);
        Assert.Equal(4, pack.Count(m => m.Wave == 1));
        Assert.All(pack.Where(m => m.Wave == 1), m => Assert.True(m.Dormant));

        // Let the first wave appear wherever its spawner is woken, then kill it.
        foreach (var monster in pack.Where(m => m.Wave == 0))
            monster.Dormant = false;
        foreach (var monster in pack.Where(m => m.Wave == 0))
        {
            player.MoveTo(monster.Position);
            player.Kill(monster.ObjectId);
        }
        player.Wait(10);

        Assert.All(pack.Where(m => m.Wave == 1), m => Assert.False(m.Dormant));
    }

    [Fact]
    public void GroupsWithSeveralPrefabsMixTheirMonsters()
    {
        // A1_M4 has packs of Insane and Tainted Wolves (NpcPrefabArray with IsRandomSpawnNpc);
        // Blind Rage on Low needs 18 Insane Wolves.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5141);
        player.AcceptMission(5141);
        player.EnterScene("A1_M4");

        Assert.True(player.Scene.Monsters.Count(m => m.Type.TypeId == 212) >= 18);
        Assert.True(player.Scene.Monsters.Select(m => m.Type.TypeId).Distinct().Count() > 3);
    }
}

/// <summary>Portals (Trigger_Unified of type Teleport): walking in moves the player to the other end.</summary>
public sealed class PortalTests
{
    private readonly ITestOutputHelper _output;

    public PortalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WalkingIntoAPortalTakesThePlayerUpTheCliff()
    {
        // A1_M4 "Blind Rage": portal 1o (133.9, 2.8, 249.2) leads up to (198.6, 19.7, 133.9),
        // where the switches for the rest of the bridge are.
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5141);
        player.AcceptMission(5141);
        player.EnterScene("A1_M4");
        var portal = player.Scene.TriggersById(8028).Single();

        player.MoveTo(portal.Center);

        var teleport = player.Take("On_TelePortMoveResult");
        Assert.Equal(8028, teleport.Arg<int>(0));
        var target = teleport.Arg<UnityEngine.Vector3>(1);
        Assert.InRange(target.x, 198f, 199f);
        Assert.InRange(target.y, 19f, 20f);
        Assert.InRange(target.z, 133f, 134f);
        Assert.Equal(target, player.Session.Position);
    }
}

/// <summary>Speech bubbles (PF_Speech_Trigger, Unified speech): the server tells the client who says what.</summary>
public sealed class SpeechTests
{
    private readonly ITestOutputHelper _output;

    public SpeechTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WalkingIntoASpeechTriggerShowsTheBubble()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.AcceptMission(5011);
        player.EnterScene("Hub_Village_Tutorial");
        var trigger = player.Scene.ArmedTriggers.First(t => t.Results.Any(r => r.Type == TriggerResultType.Speech && r.Words?.Count > 0));
        var speech = trigger.Results.First(r => r.Type == TriggerResultType.Speech);

        player.MoveTo(trigger.Center);

        var bubble = player.Take("On_SpeechResult", m => m.Arg<int>(0) == speech.NpcId);
        Assert.Equal(speech.Words, bubble.Arg<vectorInt>(4).Cast<int>().ToList());
        Assert.Equal(speech.Delay, bubble.Arg<float>(3));
    }
}

/// <summary>Every portal of every level takes the player somewhere outside all portals.</summary>
public sealed class PortalDataTests
{
    public static IEnumerable<object[]> Scenes() =>
        TestData.Data.Triggers.Where(kv => kv.Value.Any(t => t.Results.Any(r => r.Type == TriggerResultType.Teleport)))
            .Select(kv => new object[] { kv.Key });

    [Theory]
    [MemberData(nameof(Scenes))]
    public void NoPortalLandsThePlayerInAnotherPortal(string scene)
    {
        var portals = TestData.Data.Triggers[scene].Where(t => t.Results.Any(r => r.Type == TriggerResultType.Teleport)).ToList();
        foreach (var portal in portals)
        {
            var target = portal.Results.First(r => r.Type == TriggerResultType.Teleport).TeleportPos;
            Assert.NotNull(target);
            foreach (var other in portals)
            {
                bool inside = Math.Abs(target[0] - other.Center[0]) <= other.Size[0] / 2 && Math.Abs(target[2] - other.Center[2]) <= other.Size[2] / 2
                              && Math.Abs(target[1] - other.Center[1]) <= other.Size[1] / 2;
                Assert.False(inside && Math.Abs(other.RotationY) < 0.01f, $"{scene}: portal {portal.Id} lands inside portal {other.Id}");
            }
        }
    }
}
