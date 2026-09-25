using Maus.Core.Fixes;
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

        Assert.Equal(1, protector.Calls);
        Assert.Equal(42, loaded.RestorePoint!.SequenceNumber);
        Assert.Null(loaded.Entries[0].Before);
        Assert.False(loaded.Entries[0].ContainerExisted);
        Assert.Equal(EntryState.Applied, loaded.Entries[0].State);
        Assert.Equal(SettingKind.Registry, loaded.Entries[0].Key.Kind);
        Assert.Equal(["a", "b"], (string[])loaded.Entries[1].Before!.ToRegistryObject());
        Assert.Single(store.List());
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

    [Fact]
    public void Corrupted_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "bad.json"), "{ pas du json");

        Assert.Empty(new FileJournalStore(_directory, new Protector()).List());
    }
}
