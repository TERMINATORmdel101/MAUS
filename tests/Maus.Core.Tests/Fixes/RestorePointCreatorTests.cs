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
    public void Frequency_that_cannot_be_put_back_does_not_hide_the_point_just_created()
    {
        var (creator, registry, restore) = Setup();
        restore.FrequencyProbe = () =>
        {
            registry.FailWrite(RegistryHive.LocalMachine, SystemRestoreKey, new IOException("refusé"));
            return null;
        };

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Point);
        Assert.Contains("fréquence", outcome.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(null)]
    [InlineData(60)]
    public void Failed_creation_puts_the_frequency_back(int? original)
    {
        var (creator, registry, restore) = Setup();
        if (original is { } value)
        {
            registry.Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", value);
        }

        restore.CreateFailure = new InvalidOperationException("WMI en panne");

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(RestorePointStatus.Failed, outcome.Status);
        Assert.Contains("WMI en panne", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(original, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void Failed_activation_writes_nothing()
    {
        var (creator, registry, restore) = Setup();
        restore.Protection = ProtectionState.Disabled;
        restore.EnableFailure = new InvalidOperationException("refus");

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: true);

        Assert.Equal(RestorePointStatus.Failed, outcome.Status);
        Assert.Empty(registry.Writes);
        Assert.Equal(0, restore.CreateCalls);
    }

    [Fact]
    public void Unreadable_list_of_points_never_touches_the_frequency()
    {
        var (creator, registry, restore) = Setup();
        restore.ListFailure = new DataSourceUnavailableException("liste illisible");

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(RestorePointStatus.Failed, outcome.Status);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void Unknown_protection_state_still_tries_to_create()
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters();
        var unknown = new UnknownProtection(new FakeSystemRestore());

        var outcome = new RestorePointCreator(unknown, new SettingsAccessor(registry, registry, parameters, parameters)).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(RestorePointStatus.Created, outcome.Status);
    }

    private sealed class UnknownProtection(FakeSystemRestore inner) : ISystemRestore
    {
        public ProtectionState GetProtectionState(string drive) => ProtectionState.Unknown;

        public void EnableProtection(string drive) => inner.EnableProtection(drive);

        public IReadOnlyList<RestorePointInfo> ListRestorePoints() => inner.ListRestorePoints();

        public void CreateRestorePoint(string description) => inner.CreateRestorePoint(description);
    }

    [Fact]
    public void Refused_frequency_means_no_creation_and_no_exception()
    {
        var (creator, registry, restore) = Setup();
        registry.DenyWrite(RegistryHive.LocalMachine, SystemRestoreKey);

        Assert.Equal(RestorePointStatus.Failed, creator.Create("MAUS", enableProtectionIfNeeded: false).Status);
        Assert.Equal(0, restore.CreateCalls);
    }

    [Fact]
    public void Unexpected_windows_error_on_the_frequency_is_a_failure_not_an_exception()
    {
        var (creator, registry, _) = Setup();
        registry.FailWrite(RegistryHive.LocalMachine, SystemRestoreKey, new IOException("clé marquée pour suppression"));

        Assert.Equal(RestorePointStatus.Failed, creator.Create("MAUS", enableProtectionIfNeeded: false).Status);
    }

    [Fact]
    public void Frequency_refused_after_the_zero_says_it_stayed_at_zero()
    {
        var (creator, registry, _) = Setup();
        registry.AfterWrite = (hive, path) => registry.DenyWrite(hive, path);

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Contains("reste à 0", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void Windows_that_stops_answering_gives_a_failure_and_the_frequency_back()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 60);
        var parameters = new FakeSystemParameters();
        using var hang = new ManualResetEventSlim();
        var restore = new FakeSystemRestore { CreateHang = hang };
        var creator = new RestorePointCreator(restore, new SettingsAccessor(registry, registry, parameters, parameters), callTimeout: TimeSpan.FromMilliseconds(100));

        try
        {
            var outcome = creator.Create("MAUS", enableProtectionIfNeeded: false);

            Assert.Equal(RestorePointStatus.Failed, outcome.Status);
            Assert.Contains("pas répondu", outcome.Message, StringComparison.Ordinal);
            Assert.Equal(60, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
        }
        finally
        {
            hang.Set();
        }
    }

    private sealed class MemoryFrequency : IRestoreFrequencyMemo
    {
        public RememberedFrequency? Kept { get; set; }

        public bool FailToWrite { get; set; }

        public void Remember(SettingValue? original)
        {
            if (FailToWrite)
            {
                throw new IOException("disque plein");
            }

            Kept = new RememberedFrequency(original);
        }

        public RememberedFrequency? Recall() => Kept;

        public void Forget() => Kept = null;
    }

    [Fact]
    public void Frequency_is_kept_on_disk_only_while_it_is_at_zero()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 60);
        var parameters = new FakeSystemParameters();
        var memo = new MemoryFrequency();
        RememberedFrequency? during = null;
        var restore = new FakeSystemRestore();
        restore.FrequencyProbe = () =>
        {
            during = memo.Kept;
            return null;
        };

        var outcome = new RestorePointCreator(restore, new SettingsAccessor(registry, registry, parameters, parameters), memo: memo).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.True(outcome.Succeeded);
        Assert.Equal("60", during!.Value!.Data);
        Assert.Null(memo.Kept);
    }

    [Fact]
    public void Creation_after_an_interrupted_one_finds_the_true_original_frequency()
    {
        // MAUS coupé pendant la création précédente : le 0 est resté dans le registre, la vraie valeur dans le fichier.
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 0);
        var parameters = new FakeSystemParameters();
        var memo = new MemoryFrequency { Kept = new RememberedFrequency(SettingValue.Dword(60)) };

        new RestorePointCreator(new FakeSystemRestore(), new SettingsAccessor(registry, registry, parameters, parameters), memo: memo).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(60, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
        Assert.Null(memo.Kept);
    }

    [Fact]
    public void Interrupted_creation_whose_value_was_absent_removes_the_zero()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 0);
        var parameters = new FakeSystemParameters();
        var memo = new MemoryFrequency { Kept = new RememberedFrequency(null) };

        new RestorePointCreator(new FakeSystemRestore(), new SettingsAccessor(registry, registry, parameters, parameters), memo: memo).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Null(registry.GetValue(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void A_value_changed_since_the_interruption_is_not_overwritten()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency", 120);
        var parameters = new FakeSystemParameters();
        var memo = new MemoryFrequency { Kept = new RememberedFrequency(SettingValue.Dword(60)) };

        new RestorePointCreator(new FakeSystemRestore(), new SettingsAccessor(registry, registry, parameters, parameters), memo: memo).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.Equal(120, registry.GetDword(RegistryHive.LocalMachine, SystemRestoreKey, "SystemRestorePointCreationFrequency"));
    }

    [Fact]
    public void A_memo_that_cannot_be_written_does_not_stop_the_restore_point()
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters();
        var memo = new MemoryFrequency { FailToWrite = true };

        var outcome = new RestorePointCreator(new FakeSystemRestore(), new SettingsAccessor(registry, registry, parameters, parameters), memo: memo).Create("MAUS", enableProtectionIfNeeded: false);

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void Frequency_memo_file_survives_a_new_process_and_refuses_what_is_not_a_dword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maus-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new OpenProtector();
            new FileRestoreFrequencyMemo(directory, protector).Remember(SettingValue.Dword(60));
            Assert.Equal("60", new FileRestoreFrequencyMemo(directory, protector).Recall()!.Value!.Data);

            new FileRestoreFrequencyMemo(directory, protector).Remember(null);
            Assert.Null(new FileRestoreFrequencyMemo(directory, protector).Recall()!.Value);

            new FileRestoreFrequencyMemo(directory, protector).Remember(SettingValue.Text("x"));
            Assert.Null(new FileRestoreFrequencyMemo(directory, protector).Recall());

            File.WriteAllText(Path.Combine(directory, "restore-frequency.memo"), "{ pas du json");
            Assert.Null(new FileRestoreFrequencyMemo(directory, protector).Recall());

            new FileRestoreFrequencyMemo(directory, protector).Forget();
            Assert.Null(new FileRestoreFrequencyMemo(directory, protector).Recall());
            new FileRestoreFrequencyMemo(directory, protector).Forget();

            // Le fichier n'est pas compté parmi les séances du journal.
            new FileRestoreFrequencyMemo(directory, protector).Remember(SettingValue.Dword(60));
            var listing = new FileJournalStore(directory, protector).Browse();
            Assert.Empty(listing.Sessions);
            Assert.Equal(0, listing.Unreadable);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class OpenProtector : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => Directory.CreateDirectory(directory);

        public bool IsTrusted(string file) => true;

        public void ProtectFile(string file)
        {
        }
    }

    private const string Clients = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string RestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";
    private static readonly string[] DriveC = [@"\\?\Volume{x}\:(C%3A)"];

    [Fact]
    public void Protection_cut_by_policy_is_read_from_the_policy()
    {
        var registry = new FakeRegistry()
            .Set(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR", 1)
            .Set(RegistryHive.LocalMachine, Clients, RestoreClient, DriveC);

        Assert.Equal(ProtectionState.DisabledByPolicy, new WmiSystemRestore(registry).GetProtectionState(@"C:\"));
    }

    [Fact]
    public void Protection_state_is_unknown_without_the_clients_key()
    {
        Assert.Equal(ProtectionState.Unknown, new WmiSystemRestore(new FakeRegistry()).GetProtectionState(@"C:\"));
    }

    [Fact]
    public void Protection_state_is_unknown_when_the_key_is_refused()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, Clients, "Autre", 1).Deny(RegistryHive.LocalMachine, Clients);

        Assert.Equal(ProtectionState.Unknown, new WmiSystemRestore(registry).GetProtectionState(@"C:\"));
    }

    [Fact]
    public void Protection_is_disabled_when_the_value_is_absent_or_empty()
    {
        var absent = new FakeRegistry().Set(RegistryHive.LocalMachine, Clients, "Autre", 1);
        var empty = new FakeRegistry().Set(RegistryHive.LocalMachine, Clients, RestoreClient, Array.Empty<string>());

        Assert.Equal(ProtectionState.Disabled, new WmiSystemRestore(absent).GetProtectionState(@"C:\"));
        Assert.Equal(ProtectionState.Disabled, new WmiSystemRestore(empty).GetProtectionState(@"C:\"));
    }

    [Theory]
    [InlineData(@"\\?\Volume{x}\:(C%3A)", ProtectionState.Enabled)]
    [InlineData(@"\\?\Volume{x}\:(D%3A)", ProtectionState.Disabled)]
    [InlineData("C:", ProtectionState.Enabled)]
    public void Protection_looks_for_the_requested_drive_in_the_list(string volume, ProtectionState expected)
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, Clients, RestoreClient, new[] { volume });

        Assert.Equal(expected, new WmiSystemRestore(registry).GetProtectionState(@"C:\"));
    }

    [Fact]
    public void Protection_accepts_a_plain_text_value()
    {
        var registry = new FakeRegistry().Set(RegistryHive.LocalMachine, Clients, RestoreClient, "activé");

        Assert.Equal(ProtectionState.Enabled, new WmiSystemRestore(registry).GetProtectionState(@"C:\"));
    }

    [Fact]
    public void Protection_disabled_by_policy_is_never_forced()
    {
        var (creator, _, restore) = Setup();
        restore.Protection = ProtectionState.DisabledByPolicy;

        var outcome = creator.Create("MAUS", enableProtectionIfNeeded: true);

        Assert.Equal(RestorePointStatus.ProtectionDisabledByPolicy, outcome.Status);
        Assert.Equal(0, restore.EnableCalls);
    }

    [Fact]
    public void A_point_that_windows_lists_a_little_later_is_still_found()
    {
        var registry = new FakeRegistry();
        var parameters = new FakeSystemParameters();
        var restore = new LateRestore();
        var creator = new RestorePointCreator(restore, new SettingsAccessor(registry, registry, parameters, parameters), listWait: TimeSpan.FromSeconds(10));

        var outcome = creator.Create("essai", enableProtectionIfNeeded: false);

        Assert.Equal(RestorePointStatus.Created, outcome.Status);
    }

    /// <summary>Le point n'entre dans la liste qu'à la deuxième lecture après la création.</summary>
    private sealed class LateRestore : ISystemRestore
    {
        private readonly FakeSystemRestore _inner = new();
        private IReadOnlyList<RestorePointInfo>? _stale;

        public ProtectionState GetProtectionState(string drive) => _inner.GetProtectionState(drive);

        public void EnableProtection(string drive) => _inner.EnableProtection(drive);

        public void CreateRestorePoint(string description)
        {
            _stale = _inner.ListRestorePoints();
            _inner.CreateRestorePoint(description);
        }

        public IReadOnlyList<RestorePointInfo> ListRestorePoints()
        {
            if (_stale is { } stale)
            {
                _stale = null;
                return stale;
            }

            return _inner.ListRestorePoints();
        }
    }
}
