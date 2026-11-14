using System;
using Akaneiro.Server.Net;
using Xunit;

namespace Akaneiro.Server.Tests;

/// <summary>
/// The flood guard's token bucket (Akaneiro:PacketPerSecond / Akaneiro:PacketBurst): a real
/// client stays far under it, a flooding client runs it dry and gets disconnected.
/// </summary>
public sealed class PacketBudgetTests
{
    private DateTime _now = new DateTime(2013, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private void Advance(TimeSpan span) => _now += span;

    [Fact]
    public void AFloodRunsTheBurstDryAndIsRefused()
    {
        var budget = new PacketBudget(100, 250, () => _now);

        for (int i = 0; i < 250; i++)
            Assert.True(budget.TrySpend(), $"frame {i} within the burst");
        Assert.False(budget.TrySpend()); // the flood ran out

        Advance(TimeSpan.FromSeconds(1)); // a second of quiet pays 100 frames back
        for (int i = 0; i < 100; i++)
            Assert.True(budget.TrySpend(), $"refilled frame {i}");
        Assert.False(budget.TrySpend());
    }

    [Fact]
    public void IdleConnectionsOnlyBankUpToTheBurst()
    {
        var budget = new PacketBudget(100, 250, () => _now);

        Advance(TimeSpan.FromMinutes(10)); // an hour of idle does not bank unlimited frames

        for (int i = 0; i < 250; i++)
            Assert.True(budget.TrySpend(), $"burst frame {i}");
        Assert.False(budget.TrySpend());
    }

    [Fact]
    public void ASteadyClientUnderTheRateIsNeverRefused()
    {
        var budget = new PacketBudget(100, 250, () => _now);

        // 60 seconds at 90 packets a second: always under the refill.
        for (int second = 0; second < 60; second++)
        {
            for (int i = 0; i < 90; i++)
                Assert.True(budget.TrySpend());
            Advance(TimeSpan.FromSeconds(1));
        }
    }
}
