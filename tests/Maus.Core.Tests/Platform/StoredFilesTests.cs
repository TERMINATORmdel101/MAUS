using Maus.Core.Fixes;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Workshop;

namespace Maus.Core.Tests.Platform;

/// <summary>
/// Défaut 7 de la 0.5.3 : un fichier de MAUS momentanément illisible, tronqué ou abîmé n'est jamais pris pour un fichier
/// vide puis écrasé ; une écriture interrompue laisse l'ancien fichier intact.
/// </summary>
public sealed class StoredFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "maus-stored-" + Guid.NewGuid().ToString("N"));

    private readonly List<TimeSpan> _waits = [];

    public StoredFilesTests() => Directory.CreateDirectory(_directory);

    private sealed class Protector : IDirectoryProtector
    {
        public void EnsureProtected(string directory) => Directory.CreateDirectory(directory);

        public bool IsTrusted(string file) => true;

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

    private string PathOf(string name) => Path.Combine(_directory, name);

    private void Wait(TimeSpan delay) => _waits.Add(delay);

    /// <summary>Ouvre le fichier sans partage, comme un antivirus ou une sauvegarde qui l'analyse.</summary>
    private static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private string[] Copies(string name) => Directory.GetFiles(_directory, name + ".illisible-*");

    private string[] Temporaries() => Directory.GetFiles(_directory, "*.tmp");

    private static JournalSession Session(string id) => new() { Id = id, CreatedAt = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero) };

    [Fact]
    public void Atomic_write_replaces_the_file_and_leaves_no_temporary()
    {
        var path = PathOf("a.json");
        AtomicFile.WriteAllText(path, "ancien");
        AtomicFile.WriteAllText(path, "nouveau");

        Assert.Equal("nouveau", File.ReadAllText(path));
        Assert.Empty(Temporaries());
    }

    [Fact]
    public void Interrupted_write_keeps_the_old_file_intact()
    {
        var path = PathOf("a.json");
        AtomicFile.WriteAllText(path, "ancien");

        Assert.Throws<IOException>(() => AtomicFile.WriteAllText(path, "nouveau", _ => throw new IOException("coupure simulée")));

        Assert.Equal("ancien", File.ReadAllText(path));
        Assert.Empty(Temporaries());
    }

    [Fact]
    public void Locked_file_is_retried_then_reported_unavailable_never_absent()
    {
        var path = PathOf("a.json");
        File.WriteAllText(path, "contenu");

        using (Lock(path))
        {
            var read = AtomicFile.ReadText(path, wait: Wait);

            Assert.Equal(StoredFileState.Unavailable, read.State);
            Assert.NotNull(read.Error);
            Assert.Equal(AtomicFile.RetryDelays, _waits);
        }

        Assert.Equal(StoredFileState.Absent, AtomicFile.ReadText(PathOf("absent.json"), wait: Wait).State);
    }

    [Fact]
    public void File_freed_during_the_retries_is_read()
    {
        var path = PathOf("a.json");
        File.WriteAllText(path, "contenu");
        var holder = Lock(path);

        var read = AtomicFile.ReadText(path, wait: _ => holder.Dispose());

        Assert.Equal(StoredFileState.Read, read.State);
        Assert.Equal("contenu", read.Text);
    }

    [Fact]
    public void Locked_preferences_are_kept_in_memory_and_never_overwritten()
    {
        var path = PathOf("preferences.json");
        new FilePreferencesStore(_directory, new Protector()).Save(UserPreferences.Default with { GameBarProfile = 2 });
        var original = File.ReadAllText(path);
        var store = new FilePreferencesStore(_directory, new Protector(), Wait);

        using (Lock(path))
        {
            Assert.Same(UserPreferences.Default, store.Load());
            Assert.Equal(PreferencesFileState.Unavailable, store.State);
            Assert.NotNull(store.Notice);

            var error = Assert.Throws<PreferencesNotSavedException>(() => store.Save(UserPreferences.Default with { GameBarProfile = 3 }));
            Assert.Equal(3, error.Kept.GameBarProfile);
        }

        // Fichier libéré : cette séance ne l'écrit toujours pas, le choix reste en mémoire.
        Assert.Throws<PreferencesNotSavedException>(() => store.Save(UserPreferences.Default with { GameBarProfile = 3 }));
        Assert.Equal(3, store.Load().GameBarProfile);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(2, new FilePreferencesStore(_directory, new Protector()).Load().GameBarProfile);
    }

    [Fact]
    public void Truncated_preferences_are_copied_before_being_replaced()
    {
        var path = PathOf("preferences.json");
        File.WriteAllText(path, "{ \"gameBarProfile\": 2, \"Langu");
        var store = new FilePreferencesStore(_directory, new Protector(), Wait);

        Assert.Equal(UserPreferences.Default.GameBarProfile, store.Load().GameBarProfile);
        Assert.Equal(PreferencesFileState.Damaged, store.State);

        store.Save(UserPreferences.Default with { GameBarProfile = 1 });

        var copy = Assert.Single(Copies("preferences.json"));
        Assert.Equal("{ \"gameBarProfile\": 2, \"Langu", File.ReadAllText(copy));
        Assert.Equal(1, new FilePreferencesStore(_directory, new Protector()).Load().GameBarProfile);
    }

    [Fact]
    public void Unknown_choice_from_a_newer_version_keeps_the_other_choices()
    {
        File.WriteAllText(PathOf("preferences.json"), "{ \"gameBarProfile\": 2, \"laptopPower\": \"ChoixDuFutur\", \"language\": \"en\" }");
        var store = new FilePreferencesStore(_directory, new Protector(), Wait);

        var loaded = store.Load();

        Assert.Equal(2, loaded.GameBarProfile);
        Assert.Equal("en", loaded.Language);
        Assert.Equal(default, loaded.LaptopPower);
        Assert.Equal(PreferencesFileState.Damaged, store.State);
    }

    [Fact]
    public void Locked_journal_session_is_neither_read_as_missing_nor_overwritten()
    {
        new FileJournalStore(_directory, new Protector()).Save(Session("s1"));
        var path = PathOf("s1.json");
        var original = File.ReadAllText(path);
        var store = new FileJournalStore(_directory, new Protector(), Wait);

        using (Lock(path))
        {
            Assert.Throws<JournalUnreadableException>(() => store.Load("s1"));
            Assert.Throws<JournalUnsafeException>(() => store.Save(Session("s1")));
            Assert.Equal(1, store.Browse().Unreadable);
        }

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Truncated_journal_session_is_counted_and_copied_before_being_replaced()
    {
        var path = PathOf("s1.json");
        File.WriteAllText(path, "{ \"Id\": \"s1\", \"Entr");
        var store = new FileJournalStore(_directory, new Protector(), Wait);

        var listing = store.Browse();
        Assert.Empty(listing.Sessions);
        Assert.Equal(1, listing.Unreadable);
        Assert.NotNull(listing.UnreadableNotice);

        store.Save(Session("s1"));

        Assert.Single(Copies("s1.json"));
        Assert.Equal("s1", store.Load("s1")?.Id);
    }

    [Fact]
    public void Score_history_is_never_rewritten_from_an_unreadable_file()
    {
        var path = PathOf("history.json");
        var first = new BenchmarkEntry("cpu", 100, true, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var second = first with { At = first.At.AddDays(1) };
        new BenchmarkHistory(path).Add(first);
        var original = File.ReadAllText(path);
        var history = new BenchmarkHistory(path, Wait);

        using (Lock(path))
        {
            Assert.ThrowsAny<IOException>(() => history.Add(second));
        }

        Assert.Equal(original, File.ReadAllText(path));
        history.Add(second);
        Assert.Equal(2, history.Load().Count);
    }

    [Fact]
    public void Truncated_score_history_is_copied_then_restarts()
    {
        var path = PathOf("history.json");
        File.WriteAllText(path, "[{ \"Kind\": \"cpu\", \"Sco");
        var history = new BenchmarkHistory(path, Wait);

        Assert.Empty(history.Load());
        history.Add(new BenchmarkEntry("cpu", 100, true, DateTimeOffset.Now));

        Assert.Single(Copies("history.json"));
        Assert.Single(history.Load());
    }
}
