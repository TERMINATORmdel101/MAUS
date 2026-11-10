using Maus.Core.Hardware;
using Microsoft.Win32;

namespace Maus.Core.Tests.Hardware;

public class HardwareProfileBuilderTests
{
    private const string Enrollments = @"SOFTWARE\Microsoft\Enrollments";

    [Fact]
    public void Built_in_windows_enrollments_do_not_make_the_pc_managed()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\E17005D5-A50E-4E57-BE94-1D4FA69C6F93", "ProviderID", "Local Authority")
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\8345CBE6-CEFC-462A-8219-78F3FC0377C1", "ProviderID", "Deploy Authority")
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\C429BE2D-071B-4E13-B616-4141C334ECDF", "ProviderID", "Cloud Authority");

        Assert.False(HardwareProfileBuilder.HasOrganizationEnrollment(registry));
        Assert.False(HardwareProfileBuilder.Build(new FakeCim(), registry).IsManaged);
    }

    [Fact]
    public void Real_mdm_enrollment_is_recognised()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\E17005D5-A50E-4E57-BE94-1D4FA69C6F93", "ProviderID", "Local Authority")
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\0A1B2C3D-0000-0000-0000-000000000000", "ProviderID", "MS DM Server");

        Assert.True(HardwareProfileBuilder.HasOrganizationEnrollment(registry));
        Assert.True(HardwareProfileBuilder.Build(new FakeCim(), registry).IsManaged);
    }

    [Fact]
    public void Unreadable_enrollments_make_the_management_unknown_not_absent()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, Enrollments);

        Assert.Null(HardwareProfileBuilder.HasOrganizationEnrollment(registry));

        var profile = HardwareProfileBuilder.Build(NotInDomain(), registry);
        Assert.False(profile.IsManaged);
        Assert.True(profile.ManagementUnknown);
    }

    [Fact]
    public void Unreadable_wmi_makes_the_management_unknown_not_absent()
    {
        var profile = HardwareProfileBuilder.Build(new FakeCim(), new FakeRegistry());

        Assert.False(profile.IsManaged);
        Assert.True(profile.ManagementUnknown);
    }

    [Fact]
    public void One_unreadable_enrollment_is_unknown_but_a_real_one_is_still_a_proof()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{Enrollments}\A", "ProviderID", "Local Authority")
            .Deny(RegistryHive.LocalMachine, $@"{Enrollments}\A");
        Assert.Null(HardwareProfileBuilder.HasOrganizationEnrollment(registry));

        registry.Set(RegistryHive.LocalMachine, $@"{Enrollments}\B", "ProviderID", "MS DM Server");
        var profile = HardwareProfileBuilder.Build(NotInDomain(), registry);
        Assert.True(profile.IsManaged);
        Assert.False(profile.ManagementUnknown);
    }

    [Fact]
    public void A_normal_pc_read_correctly_is_neither_managed_nor_unknown()
    {
        var withBuiltIn = new FakeRegistry().Set(RegistryHive.LocalMachine, $@"{Enrollments}\E17005D5-A50E-4E57-BE94-1D4FA69C6F93", "ProviderID", "Local Authority");

        foreach (var registry in new[] { withBuiltIn, new FakeRegistry() })
        {
            var profile = HardwareProfileBuilder.Build(NotInDomain(), registry);
            Assert.False(profile.IsManaged);
            Assert.False(profile.ManagementUnknown);
        }
    }

    private static FakeCim NotInDomain() => new FakeCim().Answer(
        "SELECT Manufacturer, Model, PCSystemTypeEx, PartOfDomain FROM Win32_ComputerSystem",
        new Dictionary<string, object?> { ["PartOfDomain"] = false, ["PCSystemTypeEx"] = 1L });

    [Fact]
    public void Domain_member_is_managed()
    {
        var cim = new FakeCim().Answer(
            "SELECT Manufacturer, Model, PCSystemTypeEx, PartOfDomain FROM Win32_ComputerSystem",
            new Dictionary<string, object?> { ["PartOfDomain"] = true, ["PCSystemTypeEx"] = 1L });

        Assert.True(HardwareProfileBuilder.Build(cim, new FakeRegistry()).IsManaged);
    }
}
