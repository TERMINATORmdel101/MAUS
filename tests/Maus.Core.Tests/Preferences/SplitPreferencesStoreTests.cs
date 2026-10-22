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

        public void Save(UserPreferences preferences)
        {
            Saves++;
            Stored = preferences;
        }
    }

    private FilePreferencesStore UserFile() => new(_directory, new UserDirectoryProtector(), _ => { });

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
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
    public void First_opening_after_the_update_imports_display_choices_and_keeps_a_fresh_disclaimer_consent()
    {
        var machine = new Machine(UserPreferences.Default with { Language = "es", DisclaimerAccepted = 0, GameBarProfile = 2 });
        var store = new SplitPreferencesStore(UserFile(), machine);

        Assert.Null(store.Load().Language);
        store.Update(p => p with { DisclaimerAccepted = 1 });

        // Le moteur démarre (première action administrateur) : les anciens choix d'affichage sont repris.
        machine.IsAvailable = true;
        var read = store.Load();
        Assert.Equal("es", read.Language);
        Assert.Equal(1, read.DisclaimerAccepted);
        Assert.Equal(2, read.GameBarProfile);
        Assert.Equal("es", new SplitPreferencesStore(UserFile(), new Machine(UserPreferences.Default)).Load().Language);
    }
}
