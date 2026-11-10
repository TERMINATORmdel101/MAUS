using Maus.App.ViewModels.Workshop;
using Maus.Core.Workshop;

namespace Maus.App.Tests;

/// <summary>Le relevé de la carte « Batterie » : rien avant le clic, et « non lu » sans valeur devinée.</summary>
public sealed class BatteryHistoryCardTests
{
    private static BatteryCapacityPoint Point(int month, int full, bool changed = false) =>
        new(new DateTime(2026, month, 1), new DateTime(2026, month, 8), 50000, full, changed);

    [Fact]
    public async Task Nothing_is_read_before_the_click()
    {
        var reads = 0;
        var card = new BatteryHistoryViewModel(() =>
        {
            reads++;
            return Task.FromResult(new BatteryHistoryResult(new BatteryHistory([Point(1, 45000), Point(2, 44000)])));
        }, severalBatteries: false);

        Assert.Equal(0, reads);
        Assert.False(card.HasLines);
        Assert.False(card.HasCurve);

        await card.ReadAsync();

        Assert.Equal(1, reads);
        Assert.True(card.HasLines);
        Assert.True(card.HasCurve);
        Assert.Equal(90, card.Curve[0]);
        Assert.Equal(88, card.Curve[^1]);
    }

    [Fact]
    public async Task Not_read_shows_a_sentence_and_no_curve()
    {
        var card = new BatteryHistoryViewModel(() => Task.FromResult(new BatteryHistoryResult(null)), severalBatteries: false);

        await card.ReadAsync();

        Assert.Single(card.Lines);
        Assert.False(card.HasCurve);
    }

    [Fact]
    public async Task A_refusal_gives_its_own_reason()
    {
        var card = new BatteryHistoryViewModel(() => Task.FromResult(new BatteryHistoryResult(null, "raison")), severalBatteries: false);

        await card.ReadAsync();

        Assert.Equal("raison", Assert.Single(card.Lines));
    }

    [Fact]
    public async Task Several_batteries_are_said_not_to_be_told_apart()
    {
        var history = new BatteryHistory([Point(1, 45000), Point(2, 44000)]);
        var one = new BatteryHistoryViewModel(() => Task.FromResult(new BatteryHistoryResult(history)), severalBatteries: false);
        var two = new BatteryHistoryViewModel(() => Task.FromResult(new BatteryHistoryResult(history)), severalBatteries: true);

        await one.ReadAsync();
        await two.ReadAsync();

        Assert.Equal(one.Lines.Count + 1, two.Lines.Count);
    }

    [Fact]
    public async Task A_battery_change_marks_the_curve()
    {
        var card = new BatteryHistoryViewModel(() => Task.FromResult(new BatteryHistoryResult(new BatteryHistory([Point(1, 30000), Point(2, 49000, changed: true)]))), severalBatteries: false);

        await card.ReadAsync();

        Assert.Single(card.Marks);
    }
}
