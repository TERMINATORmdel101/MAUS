using Maus.Core.Fixes;

namespace Maus.Core.Tests.Fixes;

public class FixProfileTests
{
    private static PlannedChange Change(string module, bool recommended = true, bool advanced = false) => new()
    {
        Id = module + (advanced ? ".advanced" : recommended ? ".recommended" : ".optional"),
        ModuleId = module,
        Title = "t",
        Description = "d",
        Recommended = recommended,
        Advanced = advanced,
        Writes = [],
    };

    [Fact]
    public void Advanced_changes_are_only_selected_by_their_own_domain()
    {
        var driverBlocking = Change("M09", recommended: false, advanced: true);
        var privacyAdvanced = Change("M04", recommended: false, advanced: true);

        Assert.All(FixProfile.All, p => Assert.False(p.Selects(driverBlocking)));
        Assert.True(FixProfile.PrivacyMax.Selects(privacyAdvanced));
        Assert.False(FixProfile.Recommended.Selects(privacyAdvanced));
    }

    [Fact]
    public void Gamer_profile_keeps_security_and_skips_privacy()
    {
        Assert.True(FixProfile.Gamer.Selects(Change("M01")));
        Assert.True(FixProfile.Gamer.Selects(Change("M07")));
        Assert.False(FixProfile.Gamer.Selects(Change("M04")));
        Assert.False(FixProfile.Gamer.Selects(Change("M07", recommended: false)));
    }

    [Fact]
    public void None_selects_nothing_and_recommended_follows_the_preselection()
    {
        Assert.False(FixProfile.None.Selects(Change("M01")));
        Assert.True(FixProfile.Recommended.Selects(Change("M04")));
        Assert.False(FixProfile.Recommended.Selects(Change("M04", recommended: false)));
    }
}
