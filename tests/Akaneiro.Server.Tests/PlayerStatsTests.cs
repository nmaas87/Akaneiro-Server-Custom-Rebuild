using Akaneiro.Server.Data;
using Akaneiro.Server.Game;
using Akaneiro.Server.Tests.Harness;

namespace Akaneiro.Server.Tests;

/// <summary>What gems, enchantments, elements, masteries and food do to the character's numbers.</summary>
public sealed class PlayerStatsTests
{
    private static GameData Data => TestData.Data;

    private static CharacterData Hunter(params ItemData[] equipment)
    {
        var c = new CharacterData { Level = 1, Style = StarterLoadout.Prowess };
        c.Equipment.AddRange(equipment);
        return c;
    }

    private static ItemData Katana(int gem = 0, int enchant = 0, int element = 0) =>
        new() { Slot = Inventory.PartMainHand, ItemId = Data.Weapons.Keys.First(), PrefabId = 1, Level = 1, Gem = gem, Enchant = enchant, Element = element };

    [Fact]
    public void ARubyAddsPower()
    {
        int without = PlayerStats.Compute(Hunter(Katana()), Data).Power;
        int with = PlayerStats.Compute(Hunter(Katana(gem: 501)), Data).Power; // Ruby: "Power +9"

        Assert.Equal(without + 9, with);
    }

    [Fact]
    public void RagingBoostsDamage()
    {
        float without = PlayerStats.Compute(Hunter(Katana()), Data).DamagePct;
        float with = PlayerStats.Compute(Hunter(Katana(enchant: 101)), Data).DamagePct; // Raging I: 5%

        Assert.Equal(without + 5f, with, 3);
    }

    [Fact]
    public void FrostSlowsOnHit()
    {
        var stats = PlayerStats.Compute(Hunter(Katana(element: 101)), Data); // Frost I: 5% chance to slow for 2.75s

        var proc = Assert.Single(stats.Procs);
        Assert.Equal("Slow", proc.Kind);
        Assert.Equal(5f, proc.Chance);
        Assert.Equal(2.75f, proc.Seconds, 3);
    }

    [Fact]
    public void ACloakIsArmorAndARingAnAccessory()
    {
        // Props.General Type 4 is the cloak, 5 the ring (the client's InventoryCtrl and ItemEquipTips).
        Assert.Equal(GameData.ItemType.Cloak, Data.Items[2010].Type);
        Assert.Equal(GameData.ItemType.Ring, Data.Items[3001].Type);

        Assert.Equal(1, PlayerStats.KindOf(new ItemData { ItemId = 2010 }, Data));
        Assert.Equal(2, PlayerStats.KindOf(new ItemData { ItemId = 3001 }, Data));
        Assert.Equal(2, PlayerStats.KindOf(new ItemData { ItemId = 3002 }, Data));
    }

    [Fact]
    public void FoodRaisesItsStatWhileItLasts()
    {
        var hunter = Hunter(Katana());
        var sushi = Data.Buffs[75002]; // Sea Urchin Sushi: more than doubles base POWER (10 mins)
        var now = new DateTime(2026, 1, 1);
        int normal = PlayerStats.Compute(hunter, Data).Power;

        var buffs = new[] { new ActiveBuff(sushi, now.AddMinutes(10)) };

        Assert.True(PlayerStats.Compute(hunter, Data, buffs, now).Power > 2 * normal);
        Assert.Equal(normal, PlayerStats.Compute(hunter, Data, buffs, now.AddMinutes(11)).Power);
    }

    [Fact]
    public void ShochuBoostsExperience()
    {
        var now = new DateTime(2026, 1, 1);
        var stats = PlayerStats.Compute(Hunter(), Data, new[] { new ActiveBuff(Data.Buffs[75502], now.AddMinutes(10)) }, now);

        Assert.Equal(80f, stats.XpPct);
    }

    [Fact]
    public void CriticalHitsHitHarder()
    {
        var stats = PlayerStats.Compute(Hunter(), Data);
        stats.CritChance = 100;

        var (damage, critical) = Combat.Boost(100, stats, new Random(1));

        Assert.True(critical);
        Assert.True(damage >= 135);
    }
}

/// <summary>Spirit helpers grow with their hunter and help with their bonus.</summary>
public sealed class SpiritGrowthTests
{
    [Fact]
    public void AnInugamiAddsPower()
    {
        var data = TestData.Data;
        var hunter = new CharacterData { Level = 1, Style = StarterLoadout.Prowess };
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int normal = PlayerStats.Compute(hunter, data, now: now).Power;

        hunter.Spirits.Add(new SpiritRental { PetId = 1005, BeginUnix = new DateTimeOffset(now).ToUnixTimeSeconds(), Seconds = 3600, Chosen = true });

        // Inugami level 5: "Increases Power 6%".
        Assert.Equal((int)Math.Round(normal * 1.06), PlayerStats.Compute(hunter, data, now: now).Power);
    }

    [Fact]
    public void TheChosenSpiritLevelsUpFromKills()
    {
        using var player = new TestPlayer().LoginNewCharacter();
        long now = new DateTimeOffset(player.Clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
        player.Session.Character.Spirits.Add(new SpiritRental { PetId = 1001, BeginUnix = now, Seconds = 3600, Chosen = true });

        player.Session.Spirits.GainExperience(2000); // level 2 needs 1664

        // The rental stays on its line id (the client only has the dog prefab for 1001);
        // the growth lives in the shared experience counter.
        Assert.Equal(1001, player.Session.Character.Spirits.Single().PetId);
        Assert.Equal(2000UL, player.Session.Character.SpiritExp[10]);
        Assert.Equal(2, player.Session.Spirits.Active().Level);
    }
}
