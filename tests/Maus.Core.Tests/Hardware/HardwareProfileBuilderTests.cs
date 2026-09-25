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
    public void Unreadable_enrollments_are_not_a_managed_pc()
    {
        var registry = new FakeRegistry().Deny(RegistryHive.LocalMachine, Enrollments);

        Assert.False(HardwareProfileBuilder.HasOrganizationEnrollment(registry));
    }

    [Fact]
    public void Domain_member_is_managed()
    {
        var cim = new FakeCim().Answer(
            "SELECT Manufacturer, Model, PCSystemTypeEx, PartOfDomain FROM Win32_ComputerSystem",
            new Dictionary<string, object?> { ["PartOfDomain"] = true, ["PCSystemTypeEx"] = 1L });

        Assert.True(HardwareProfileBuilder.Build(cim, new FakeRegistry()).IsManaged);
    }
}
