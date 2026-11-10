using Maus.Core.Preferences;

namespace Maus.Core.Tests.Preferences;

/// <summary>0.8.1 : choix d'affichage dans le fichier de l'utilisateur, choix protégés seulement par le moteur.</summary>
public sealed class SplitPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "maus-choix-" + Guid.NewGuid().ToString("N"));

    private sealed class Machine(UserPreferences stored) : IMachinePreferences
    {
        public bool IsAvailable { get; set; }

        public UserPreferences Stored { get; private set; } = stored;

        public int Saves { get; private set; }

        public int Loads { get; private set; }

        public UserPreferences Load()
        {
            Loads++;
            return Stored;
        }

        /// <summary>L'enregistrement attend ce signal, comme la confirmation Oui/Non du moteur.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public ManualResetEventSlim Saving { get; } = new();

        public void Save(UserPreferences preferences)
        {
            Saving.Set();
            Hold?.Wait(TimeSpan.FromSeconds(20));
            Saves++;
            Stored = preferences;
        }
    }

    private sealed class CopyingMachine : IMachinePreferences
    {
        public bool IsAvailable => true;

        public int Saves { get; private set; }

        public int Copies { get; private set; }

        public UserPreferences Load() => UserPreferences.Default;

        public void Save(UserPreferences preferences) => Saves++;

        public void CopyIfRunning(UserPreferences preferences) => Copies++;
    }

    private FilePreferencesStore UserFile() => new(_directory, new UserDirectoryProtector(), _ => { });

    [Fact]
    public void Unchanged_protected_choices_are_only_copied_and_a_change_is_saved()
    {
        var machine = new CopyingMachine();
        var store = new SplitPreferencesStore(UserFile(), machine);
        store.Refresh();

        store.Save(UserPreferences.Default with { Theme = ThemeChoice.Dark });
        Assert.Equal((0, 1), (machine.Saves, machine.Copies));

        store.Save(UserPreferences.Default with { Theme = ThemeChoice.Dark, GameBarProfile = 3 });
        Assert.Equal((1, 1), (machine.Saves, machine.Copies));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Le moteur attend un « Oui » dans sa fenêtre : la fenêtre de MAUS doit pouvoir lire ses choix pendant ce temps.</summary>
    [Fact]
    public async Task Reading_choices_never_waits_for_the_answer_in_the_engine_window()
    {
        using var hold = new ManualResetEventSlim();
        var machine = new Machine(UserPreferences.Default) { IsAvailable = true, Hold = hold };
        var store = new SplitPreferencesStore(UserFile(), machine);
        store.Load();

        var save = Task.Run(() => store.Save(UserPreferences.Default with { GameBarProfile = 3 }));
        Assert.True(machine.Saving.Wait(TimeSpan.FromSeconds(20)));

        var read = Task.Run(store.Load);
        var refresh = Task.Run(store.Refresh);
        try
        {
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(20))));
            Assert.Null((await read).GameBarProfile);
        }
        finally
        {
            hold.Set();
        }

        await save;
        await refresh;
        Assert.Equal(3, store.Load().GameBarProfile);
    }

    [Fact]
    public void The_engine_is_read_once_then_again_only_when_it_starts()
    {
        var machine = new Machine(UserPreferences.Default with { GameBarProfile = 2 }) { IsAvailable = true };
        var store = new SplitPreferencesStore(UserFile(), machine);

        store.Load();
        store.Load();
        Assert.Equal(1, machine.Loads);

        store.Refresh();
        Assert.Equal(2, machine.Loads);
    }

    [Fact]
    public void Display_choices_are_saved_without_asking_for_permission()
    {
        var machine = new Machine(UserPreferences.Default);
        var store = new SplitPreferencesStore(UserFile(), machine);

        store.Update(p => p with { Language = "en", DisclaimerAccepted = 1 });

        Assert.Equal(0, machine.Saves);
        Assert.Equal(0, machine.Loads);
        Assert.Equal("en", new SplitPreferencesStore(UserFile(), machine).Load().Language);
    }

    [Fact]
    public void Protected_choices_go_through_the_engine_and_override_the_user_file()
    {
        var machine = new Machine(UserPreferences.Default with { GameBarProfile = 2 });
        var store = new SplitPreferencesStore(UserFile(), machine);

        store.Update(p => p with { GameBarProfile = 3 });
        Assert.Equal(1, machine.Saves);
        Assert.Equal(3, machine.Stored.GameBarProfile);

        // Le fichier de l'utilisateur, modifiable sans droits, ne décide jamais des choix protégés.
        UserFile().Save(UserPreferences.Default with { GameBarProfile = 1, Language = "es" });
        machine.IsAvailable = true;
        var read = new SplitPreferencesStore(UserFile(), machine).Load();
        Assert.Equal(3, read.GameBarProfile);
        Assert.Equal("es", read.Language);
    }

    [Fact]
    public void Engine_running_receives_every_choice_so_the_weekly_audit_speaks_the_same_language()
    {
        var machine = new Machine(UserPreferences.Default) { IsAvailable = true };
        var store = new SplitPreferencesStore(UserFile(), machine);

        store.Update(p => p with { Language = "es" });

        Assert.Equal("es", machine.Stored.Language);
    }

    [Fact]
    public void Display_choices_of_the_protected_file_never_replace_those_of_the_user()
    {
        var machine = new Machine(UserPreferences.Default with { Language = "es", DisclaimerAccepted = 0, GameBarProfile = 2 });
        var store = new SplitPreferencesStore(UserFile(), machine);

        Assert.Null(store.Load().Language);
        store.Update(p => p with { DisclaimerAccepted = 1 });

        // Le moteur démarre (première action administrateur) : les anciens choix d'affichage sont repris.
        machine.IsAvailable = true;
        var read = store.Load();
        Assert.Null(read.Language);
        Assert.Equal(1, read.DisclaimerAccepted);
        Assert.Equal(2, read.GameBarProfile);
    }
}
