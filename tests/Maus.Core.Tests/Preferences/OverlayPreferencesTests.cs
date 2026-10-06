using Maus.Core.Preferences;

namespace Maus.Core.Tests.Preferences;

public class OverlayPreferencesTests
{
    [Theory]
    [InlineData(0.1, 0.6)]
    [InlineData(1.5, 1.5)]
    [InlineData(40, 2.5)]
    [InlineData(double.NaN, 1)]
    public void Overlay_size_from_a_hand_edited_file_stays_usable(double stored, double used)
    {
        Assert.Equal(used, (UserPreferences.Default with { OverlayScale = stored }).OverlayScaleUsed);
    }

    [Theory]
    [InlineData(0, 0.2)]
    [InlineData(0.5, 0.5)]
    [InlineData(3, 1)]
    public void Overlay_opacity_never_makes_the_counter_invisible(double stored, double used)
    {
        Assert.Equal(used, (UserPreferences.Default with { OverlayOpacity = stored }).OverlayOpacityUsed);
    }

    [Fact]
    public void Overlay_is_shown_by_default_at_the_top_left()
    {
        Assert.True(UserPreferences.Default.OverlayEnabled);
        Assert.Equal(OverlayCorner.TopLeft, UserPreferences.Default.OverlayCorner);
    }
}
