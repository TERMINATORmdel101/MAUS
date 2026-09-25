using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public class RestorePointCreatorTests
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    private static (RestorePointCreator Creator, FakeRegistry Registry, FakeSystemRestore Restore) Setup()
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters();
        var settings = new SettingsAccessor(registry, registry, parameters, parameters);
        var restore = new FakeSystemRestore();
        restore.FrequencyProbe = () => settings.Read(RestorePointCreator.FrequencyKey);
        return (new RestorePointCreator(restore, settings), registry, restore);
    }

    [Fact]
    public void Lifts_the_24_hour_limit_during_creation_then_removes_it()
    {
        var (creator, registry, restore) = Setup();

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.True(outcome.Succeeded);
        Assert.Equal("0", restore.FrequencyDuringCreate!.Data);
        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void Keeps_a_frequency_value_set_by_the_user()
    {
        var (creator, registry, _) = Setup();
        registry.Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 60);

        creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(60, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void Success_reported_by_windows_without_new_point_is_a_failure()
    {
        var (creator, _, restore) = Setup();
        restore.SilentlySkip = true;

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(RestorePointStatus.Failed, outcome.Status);
    }

    [Fact]
    public void Disabled_protection_is_enabled_only_with_consent()
    {
        var (creator, _, restore) = Setup();
        restore.Protection = ProtectionState.Disabled;

        var refused = creator.Create("MAUS", enableProtectionIfNeeded: false);
        Assert.Equal(RestorePointStatus.ProtectionDisabled, refused.Status);
        Assert.Equal(0, restore.EnableCalls);

        var accepted = creator.Create("MAUS", enableProtectionIfNeeded: true);
        Assert.True(accepted.Succeeded);
        Assert.Equal(1, restore.EnableCalls);
    }

    [Fact]
    public void Protection_disabled_by_policy_is_never_forced()
    {
        var (creator, _, restore) = Setup();
        restore.Protection = ProtectionState.DisabledByPolicy;

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: true);

        Assert.Equal(RestorePointStatus.ProtectionDisabled, outcome.Status);
        Assert.Equal(0, restore.EnableCalls);
    }
}
