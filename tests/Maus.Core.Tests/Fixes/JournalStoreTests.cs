using Maus.Core.Fixes;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests.Fixes;

public sealed class JournalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "maus-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class Protector(bool trusted = true) : IDirectoryProtector
    {
        public int Calls { get; private set; }

        public void EnsureProtected(string directory)
        {
            Calls++;
            Directory.CreateDirectory(directory);
        }

        public bool IsTrusted(string file) => trusted;

        public void ProtectFile(string file)
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static JournalSession Sample(string id) => new()
    {
        Id = id,
        CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        WindowsBuild = 26200,
        RestorePoint = new RestorePointInfo(42, "MAUS", null),
        Entries =
        {
            new JournalEntry
            {
                ChangeId = "M06.taskview",
                ModuleId = "M06",
                ChangeTitle = "Vue des tâches",
                Key = SettingKey.Registry("HKCU", @"Software\X", "ShowTaskViewButton"),
                Before = null,
                ContainerExisted = false,
                After = SettingValue.Dword(0),
                State = EntryState.Applied,
            },
            new JournalEntry
            {
                ChangeId = "M06.multi",
                ModuleId = "M06",
                ChangeTitle = "Multi",
                Key = SettingKey.Registry("HKLM", @"Software\X", "Liste"),
                Before = new SettingValue(RegistryValueKind.MultiString, "a\0b"),
                After = SettingValue.Text("c"),
            },
        },
    };

    [Fact]
    public void Round_trips_a_session_through_a_protected_directory()
    {
        var protector = new Protector();
        var store = new FileJournalStore(_directory, protector);

        store.Save(Sample("20260925-100000-abcdef"));
        var loaded = store.Load("20260925-100000-abcdef")!;

        Assert.Equal(2, protector.Calls);
        Assert.Equal(42, loaded.RestorePoint!.SequenceNumber);
        Assert.Null(loaded.Entries[0].Before);
        Assert.False(loaded.Entries[0].ContainerExisted);
        Assert.Equal(EntryState.Applied, loaded.Entries[0].State);
        Assert.Equal(SettingKind.Registry, loaded.Entries[0].Key.Kind);
        Assert.Equal(["a", "b"], (string[])loaded.Entries[1].Before!.ToRegistryObject());
        Assert.Single(store.List());
    }

    /// <summary>Fichier écrit par la 0.8.5, recopié tel quel : ce qui est sur le disque des utilisateurs doit rester lisible et annulable.</summary>
    private const string Session085 = """
        {
          "id": "20261030-100000-abcdef",
          "createdAt": "2026-10-30T10:00:00+00:00",
          "mausVersion": "0.8.5",
          "windowsBuild": 26200,
          "restorePointNote": "Point de restauration non demandé par l'utilisateur.",
          "entries": [
            {
              "changeId": "M06.taskview",
              "moduleId": "M06",
              "changeTitle": "Vue des tâches",
              "key": { "kind": "Registry", "hive": "HKCU", "path": "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "name": "ShowTaskViewButton", "spiGet": 0, "spiSet": 0, "spiUseUiParam": false },
              "before": { "kind": "DWord", "data": "1" },
              "containerExisted": true,
              "after": { "kind": "DWord", "data": "0" },
              "state": "Applied",
              "appliedAt": "2026-10-30T10:00:01+00:00"
            },
            {
              "changeId": "M06.widgets",
              "moduleId": "M06",
              "changeTitle": "Widgets",
              "key": { "kind": "Registry", "hive": "HKLM", "path": "SOFTWARE\\Policies\\Microsoft\\Dsh", "name": "AllowNewsAndInterests", "spiGet": 0, "spiSet": 0, "spiUseUiParam": false },
              "containerExisted": false,
              "after": { "kind": "DWord", "data": "0" },
              "state": "Applied",
              "appliedAt": "2026-10-30T10:00:02+00:00"
            }
          ]
        }
        """;

    [Fact]
    public void A_session_written_by_0_8_5_is_read_and_undone()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "20261030-100000-abcdef.json"), Session085);
        var store = new FileJournalStore(_directory, new Protector());
        const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string Dsh = @"SOFTWARE\Policies\Microsoft\Dsh";
        var registry = new FakeRegistry()
            .Set(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton", 0)
            .Set(RegistryHive.LocalMachine, Dsh, "AllowNewsAndInterests", 0);

        var session = store.Load("20261030-100000-abcdef")!;

        Assert.True(session.CanRevert);
        Assert.Null(session.UserSid);
        Assert.Equal("1", session.Entries[0].Before!.Data);
        Assert.Null(session.Entries[1].Before);
        Assert.False(session.Entries[1].ContainerExisted);

        // Séance sans compte noté : elle s'annule comme avant, quel que soit le compte actuel.
        var audit = TestContext.Create(registry, hardware: new Maus.Core.Hardware.HardwareProfile { FormFactor = Maus.Core.Hardware.FormFactor.Desktop });
        var reverted = new FixEngine(TestFixContext.Create(audit, registry, journal: store, userSid: "S-1-5-21-1-1002")).Revert(session.Id);

        Assert.True(reverted.Completed, string.Join(" ; ", reverted.Entries.Select(e => e.Message)));
        Assert.Equal(1, registry.GetDword(RegistryHive.CurrentUser, Advanced, "ShowTaskViewButton"));
        Assert.False(registry.KeyExists(RegistryHive.LocalMachine, Dsh));
        Assert.False(store.Load(session.Id)!.CanRevert);
    }

    [Fact]
    public void The_account_of_a_session_survives_the_file()
    {
        var store = new FileJournalStore(_directory, new Protector());
        store.Save(new JournalSession { Id = "20261102-100000-abcdef", CreatedAt = DateTimeOffset.UnixEpoch, UserSid = "S-1-5-21-1-1001" });

        Assert.Contains("\"userSid\": \"S-1-5-21-1-1001\"", File.ReadAllText(Path.Combine(_directory, "20261102-100000-abcdef.json")), StringComparison.Ordinal);
        Assert.Equal("S-1-5-21-1-1001", new FileJournalStore(_directory, new Protector()).Load("20261102-100000-abcdef")!.UserSid);
    }

    [Fact]
    public void Untrusted_files_are_ignored()
    {
        new FileJournalStore(_directory, new Protector()).Save(Sample("20260925-100000-abcdef"));
        var store = new FileJournalStore(_directory, new Protector(trusted: false));

        Assert.Null(store.Load("20260925-100000-abcdef"));
        Assert.Empty(store.List());
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Path_like_identifiers_are_rejected(string id)
    {
        Assert.Null(new FileJournalStore(_directory, new Protector()).Load(id));
    }

    private sealed class UnsafeFolder : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => throw new JournalUnsafeException("dossier non sûr");

        public bool IsTrusted(string file) => throw new InvalidOperationException("rien ne doit être lu");

        public void ProtectFile(string file)
        {
        }
    }

    [Fact]
    public void Nothing_is_read_from_a_folder_that_cannot_be_protected()
    {
        new FileJournalStore(_directory, new Protector()).Save(Sample("20260925-100000-abcdef"));
        var store = new FileJournalStore(_directory, new UnsafeFolder());

        Assert.Throws<JournalUnsafeException>(() => store.Load("20260925-100000-abcdef"));
        Assert.Throws<JournalUnsafeException>(() => store.Browse());
    }

    [Fact]
    public void Reading_does_not_create_an_absent_folder()
    {
        var store = new FileJournalStore(_directory, new Protector());

        Assert.Null(store.Load("20260925-100000-abcdef"));
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(_directory));
    }

    /// <summary>Protecteur qui juge le fichier sur les droits qu'une version précédente lui a laissés.</summary>
    private sealed class RightsOfAnEarlierVersion : IDirectoryProtector
    {
        public void EnsureProtected(string directory)
        {
        }

        public bool IsTrusted(string file)
        {
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetSecurityDescriptorSddlForm("O:BAD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)");
            return WindowsDirectoryProtector.IsTrustedFile(security, AtomicFile.LinkCount(file));
        }

        public void ProtectFile(string file)
        {
        }
    }

    [Fact]
    public void A_session_written_by_an_earlier_version_with_correct_rights_stays_readable()
    {
        new FileJournalStore(_directory, new Protector()).Save(Sample("20261030-100000-abcdef"));
        var store = new FileJournalStore(_directory, new RightsOfAnEarlierVersion());

        var loaded = store.Load("20261030-100000-abcdef");

        Assert.Equal("20261030-100000-abcdef", loaded!.Id);
        Assert.Single(store.Browse().Sessions);
    }

    [Fact]
    public void Corrupted_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "bad.json"), "{ pas du json");

        Assert.Empty(new FileJournalStore(_directory, new Protector()).List());
    }
}
