using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Platform;

public class InstalledProgramsTests
{
    [Fact]
    public void All_three_sources_are_read_with_name_version_and_publisher()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\A", "DisplayName", "Programme A")
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\A", "DisplayVersion", "1.2")
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\A", "Publisher", "Éditeur")
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\SansNom", "DisplayVersion", "9")
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.Wow64UninstallPath}\B", "DisplayName", "Programme B")
            .Set(RegistryHive.CurrentUser, $@"{InstalledPrograms.UninstallPath}\C", "DisplayName", "Programme C");

        var programs = InstalledPrograms.Read(registry, out var readable);

        Assert.True(readable);
        Assert.Equal(["Programme A", "Programme B", "Programme C"], programs.Select(p => p.Name));
        Assert.Equal(new InstalledProgram("Programme A", "1.2", "Éditeur"), programs[0]);
    }

    [Fact]
    public void A_refused_source_leaves_the_others_and_only_three_refusals_mean_unreadable()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, $@"{InstalledPrograms.UninstallPath}\C", "DisplayName", "Programme C")
            .Deny(RegistryHive.LocalMachine, InstalledPrograms.UninstallPath)
            .Deny(RegistryHive.LocalMachine, InstalledPrograms.Wow64UninstallPath);

        Assert.Equal("Programme C", Assert.Single(InstalledPrograms.Read(registry, out var readable)).Name);
        Assert.True(readable);

        registry.Deny(RegistryHive.CurrentUser, InstalledPrograms.UninstallPath);
        Assert.Empty(InstalledPrograms.Read(registry, out readable));
        Assert.False(readable);
    }

    [Fact]
    public void A_refused_entry_does_not_stop_the_others()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\A", "DisplayName", "Programme A")
            .Set(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\B", "DisplayName", "Programme B")
            .Deny(RegistryHive.LocalMachine, $@"{InstalledPrograms.UninstallPath}\A");

        Assert.Equal("Programme B", Assert.Single(InstalledPrograms.Read(registry, out _)).Name);
    }
}
