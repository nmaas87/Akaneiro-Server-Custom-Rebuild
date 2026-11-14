using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;
using Xunit.Abstractions;

namespace Akaneiro.Server.Tests;

/// <summary>Baskets, chests and switches (InteractiveObj), and what they bring along.</summary>
public sealed class BreakableTests
{
    private const int Basket = 1002;        // tutorial: BRK_Basket, brings spawner 108 after 2 s
    private const int BasketWolves = 108;   // 2 wolves; after both, the Amano Harbinger (116)
    private const int Harbinger = 116;

    private readonly ITestOutputHelper _output;

    public BreakableTests(ITestOutputHelper output) => _output = output;

    private TestPlayer AtTheBasket(out Breakable basket)
    {
        var player = new TestPlayer(_output).LoginNewCharacter();
        player.AcceptMission(5011);
        player.EnterScene("Hub_Village_Tutorial");
        basket = player.Scene.Breakables.Single(b => b.Info.TypeId == Basket);
        player.MoveTo(basket.Position);
        player.Session.CurrentHp = 1_000_000; // the fights are not under test
        return player;
    }

    [Fact]
    public void ObjectsAreAnnouncedWhenThePlayerComesNear()
    {
        using var player = AtTheBasket(out var basket);

        var entered = player.Take<SBreakableActorEnter>("On_BreakableActorEnter", 0);
        var all = new[] { entered }.Concat(player.TakeAll("On_BreakableActorEnter").Select(m => m.Arg<SBreakableActorEnter>(0))).ToList();
        var announced = Assert.Single(all, b => b.objectID == basket.ObjectId);
        Assert.Equal(Basket, announced.ActorID);
        Assert.True(announced.scale.x > 0, "an object without scale is invisible");
    }

    [Fact]
    public void HittingABasketBreaksIt()
    {
        using var player = AtTheBasket(out var basket);

        var hit = player.Attack(basket.ObjectId);

        Assert.Contains(hit.skillEffectVec.Cast<SSkillEffect>(), e => e.targetID == basket.ObjectId);
        // ObjectLeave plays the breaking, counts the "destroy" objective and removes the arrow.
        player.Take("On_ObjectLeave", m => m.Arg<vectorInt>(0).Contains(basket.ObjectId));
        player.Take("On_moneyEnter"); // karma shards
        Assert.Null(player.Scene.FindBreakable(basket.ObjectId));
    }

    [Fact]
    public void TheBasketBringsItsWolvesAndThenTheHarbinger()
    {
        using var player = AtTheBasket(out var basket);
        Assert.All(player.Scene.MonstersOf(BasketWolves), m => Assert.True(m.Dormant));

        player.Attack(basket.ObjectId);
        player.Wait(1);
        Assert.All(player.Scene.MonstersOf(BasketWolves), m => Assert.True(m.Dormant, "came before the basket's delay"));
        player.Wait(5);
        var wolves = player.Scene.MonstersOf(BasketWolves).ToList();
        Assert.Equal(2, wolves.Count);
        Assert.All(wolves, m => Assert.False(m.Dormant));

        foreach (var wolf in wolves)
            player.Kill(wolf.ObjectId);
        player.Session.CurrentHp = 1_000_000;
        player.Wait(5);

        var harbinger = Assert.Single(player.Scene.MonstersOf(Harbinger));
        Assert.False(harbinger.Dormant, "the Harbinger never came");
    }

    [Fact]
    public void KillingAWolfGivesTheExperienceOfTheOriginal()
    {
        // Recordings of the original show 12 experience for an Insane Wolf (prefab XP 3).
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterTutorial();
        var wolf = player.AnnouncedMonsters().Single();

        player.Kill(wolf.objectID);

        Assert.Equal(12UL, player.Take<ulong>("On_AddExpNTF", 1));
    }

    [Fact]
    public void APackStandsSpreadOutInItsSpawnArea()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village_Tutorial");

        var pack = player.Scene.MonstersOf(106).Select(m => (m.Position.x, m.Position.z)).ToList();

        Assert.Equal(3, pack.Count);
        Assert.Equal(3, pack.Distinct().Count());
    }

    [Fact]
    public void AnAmbushLandsOnItsWaypoint()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village_Tutorial");
        var ambusher = player.Scene.MonstersOf(117).First();
        Assert.NotNull(ambusher.Waypoint);

        player.MoveTo(player.Scene.TriggersById(254).Single().Center);
        player.Wait(3);

        Assert.True(player.Has("On_SetObjectVecPosition", m => m.Arg<int>(1) == ambusher.ObjectId), "no entrance path sent");
    }

    [Fact]
    public void TheHarbingerDropsHisKatanaAndThePeltTheTutorialWaitsFor()
    {
        // The tutorial opens the barrier to the temple itself once the player picks up a pelt
        // (ActiveCollect 6001); the Harbinger also always drops his katana (UniqueItemsPrefab).
        using var player = AtTheBasket(out var basket);
        player.Attack(basket.ObjectId);
        player.Wait(6);
        foreach (var wolf in player.Scene.MonstersOf(BasketWolves).ToList())
            player.Kill(wolf.ObjectId);
        player.Session.CurrentHp = 1_000_000;
        player.Wait(5);
        player.ClearInbox();

        player.Kill(player.Scene.MonstersOf(Harbinger).Single().ObjectId);
        player.Wait(2); // several items come one after the other (BattleHandler.DropGapSeconds)

        var drops = player.TakeAll("On_ItemEnter").Select(m => m.Arg<SItemEnter>(0).iteminfo).ToList();
        Assert.Contains(drops, i => i.ID == 6001);                     // Pelts
        Assert.Contains(drops, i => i.ID == 1001 && i.perfrab == 2);   // the katana
    }

    [Fact]
    public void ThePeltDropsOnlyOnce()
    {
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.EnterScene("Hub_Village_Tutorial");
        Assert.Single(player.Scene.PendingCollectTargets);

        foreach (var harbinger in player.Scene.Monsters.Where(m => m.Type.TypeId == 125).ToList())
        {
            harbinger.Dormant = false;
            player.MoveTo(harbinger.Position);
            player.Kill(harbinger.ObjectId);
        }

        Assert.Single(player.TakeAll("On_ItemEnter"), m => m.Arg<SItemEnter>(0).iteminfo.ID == 6001);
    }

    [Fact]
    public void EveryPieceOfTheBlindRageBridgeHasItsSwitch()
    {
        // B1 58, B2 59, B4 60 and B3 - whose SelfAnimation id is 0, which is still a valid target.
        var anims = TestData.Data.Breakables["A1_M4"].Where(b => b.TypeId == 4000).SelectMany(b => b.Animations).Select(a => a.RealId).ToList();
        Assert.Equal(new[] { 0, 58, 59, 60 }, anims.OrderBy(id => id));
    }

    [Fact]
    public void TheForestSwitchLowersItsBridge()
    {
        // A1_M4 "Blind Rage": the switch at (213, 235) lowers "Bridge1" (SelfAnimation 58).
        using var player = new TestPlayer(_output).LoginNewCharacter();
        player.Unlock(5141);
        player.AcceptMission(5141);
        player.EnterScene("A1_M4");
        player.Session.CurrentHp = 1_000_000;
        var theSwitch = player.Scene.Breakables.Single(b => b.Info.TypeId == 4000 && b.Info.Animations.Any(a => a.RealId == 58));
        player.MoveTo(theSwitch.Position);
        player.Take("On_BreakableActorEnter", m => m.Arg<SBreakableActorEnter>(0).objectID == theSwitch.ObjectId);

        player.Attack(theSwitch.ObjectId);

        player.Take("On_ObjectLeave", m => m.Arg<vectorInt>(0).Contains(theSwitch.ObjectId));
        player.Take("On_TriggerAnimationResult", m => m.Arg<int>(0) == 58 && m.Arg<string>(1) == "Bridge1");
    }
}
