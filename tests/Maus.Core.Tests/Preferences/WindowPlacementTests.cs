using Maus.Core.Preferences;

namespace Maus.Core.Tests.Preferences;

public class WindowPlacementTests
{
    [Theory]
    [InlineData(1200, 960, 1920, 1200)]
    [InlineData(3000, 960, 1920, 1920)]
    [InlineData(300, 960, 1920, 960)]
    [InlineData(1200, 960, 911, 960)]
    [InlineData(700, 960, 800, 960)]
    public void The_minimum_wins_even_on_a_desktop_smaller_than_it(double saved, double minimum, double screen, double expected) =>
        Assert.Equal(expected, WindowPlacement.FitSize(saved, minimum, screen));
}
