using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Maus.App.ViewModels;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Engine.Broker;
using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using Maus.Core.Workshop;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Maus.App.Tests;

/// <summary>Les ViewModels vivent sur le fil de la fenêtre : chaque test tourne sur un vrai fil WPF, avec son répartiteur.</summary>
internal static class UiThread
{
    public static void Run(Func<Task> test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "le test n'a pas fini en une minute (fenêtre ouverte ?)");
        failure?.Throw();
    }
}

/// <summary>Moteur d'essai : note chaque opération et répond ce que le test a prévu (une exception prévue devient une tâche en échec).</summary>
public class FakeEngine : DispatchProxy
{
    private static readonly MethodInfo FromResult = typeof(Task).GetMethods().Single(m => m.Name == nameof(Task.FromResult));
    private static readonly MethodInfo FromException = typeof(Task).GetMethods().Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethod);

    public List<string> Calls { get; } = [];

    public Dictionary<string, Func<object?[], object?>> Answers { get; } = new(StringComparer.Ordinal);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Add(targetMethod.Name);
        var result = Answers.TryGetValue(targetMethod.Name, out var answer) ? answer(args ?? []) : null;
        var type = targetMethod.ReturnType;
        if (result is Task || !typeof(Task).IsAssignableFrom(type))
        {
            return result;
        }

        if (!type.IsGenericType)
        {
            return result is Exception failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        var value = type.GetGenericArguments()[0];
        return result is Exception error
            ? FromException.MakeGenericMethod(value).Invoke(null, [error])
            : FromResult.MakeGenericMethod(value).Invoke(null, [result]);
    }
}

/// <summary>Un ViewModel complet, branché sur un moteur d'essai ; ses historiques et ses journaux vont dans un dossier temporaire.</summary>
internal sealed class TestBench : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("maus-vm-").FullName;

    /// <param name="kept">Audits déjà gardés sur ce PC à l'ouverture de la fenêtre.</param>
    public TestBench(params AuditSnapshot[] kept)
    {
        Directory.CreateDirectory(_folder);
        var snapshots = new AuditSnapshotStore(Path.Combine(_folder, "snapshots.json"));
        foreach (var snapshot in kept)
        {
            snapshots.Add(snapshot);
        }

        var engine = DispatchProxy.Create<IMausEngine, FakeEngine>();
        Engine = engine;
        Fake = (FakeEngine)(object)engine;
        CrashLog.UseFolder(LogFolder);
        EngineHost.Current = engine;
        EngineHost.Preferences = Store;
        Fake.Answers["RunAuditAsync"] = args => RunAsync((IProgress<ModuleResult>?)args[0]);
        Fake.Answers["PlanAsync"] = _ => Plan;
        Fake.Answers["RunModuleAsync"] = args => Results.Find(r => r.ModuleId == (string)args[0]!);
        Fake.Answers["BrowseJournalAsync"] = _ => new JournalListing(Sessions, 0);
        Fake.Answers["GetScheduleAsync"] = _ => new ScheduleState(null, null);
        ViewModel = new MainViewModel(new AuditHistories(
            new BenchmarkHistory(Path.Combine(_folder, "health.json")),
            new BenchmarkHistory(Path.Combine(_folder, "measures.json")),
            snapshots,
            HealthLog: new Maus.Core.Follow.HealthLogStore(HealthLogPath)))
        {
            Engine = engine,
            PreferencesStore = Store,
            Confirm = (title, message) =>
            {
                Questions.Add((title, message));
                return Answer(title, message);
            },
            AskLaptopChoice = () =>
            {
                LaptopQuestions++;
                return null;
            },
        };
    }

    public IMausEngine Engine { get; }

    public int LaptopQuestions { get; private set; }

    /// <summary>Carnet de santé : il note aussi un audit sans droits, à part des historiques ci-dessous.</summary>
    public string HealthLogPath => Path.Combine(_folder, "carnet", "health-log.json");

    /// <summary>Fichiers d'historique écrits : scores, mesures, résumés d'audit.</summary>
    public IEnumerable<string> HistoryFiles => Directory.GetFiles(_folder).Select(Path.GetFileName)!;

    public IReadOnlyList<AuditSnapshot> KeptSnapshots => new AuditSnapshotStore(Path.Combine(_folder, "snapshots.json")).Load();

    public FakeEngine Fake { get; }

    public MainViewModel ViewModel { get; }

    public MemoryPreferencesStore Store { get; } = new();

    public string LogFolder => Path.Combine(_folder, "logs");

    public List<ModuleResult> Results { get; } = [];

    public List<PlannedChange> Plan { get; } = [];

    public List<JournalSession> Sessions { get; } = [];

    public EngineSnapshot Snapshot { get; set; } = new(
        DateTimeOffset.Now,
        new WindowsInfo("Windows 11 Pro", "Professional", "25H2", 26200, 1000),
        new HardwareProfile { FormFactor = FormFactor.Desktop },
        IsElevated: true,
        ElevatedAsAnotherUser: false,
        BlockingReason: null,
        UserPreferences.Default);

    public List<(string Title, string Message)> Questions { get; } = [];

    /// <summary>Réponse aux demandes de confirmation ; par défaut, oui.</summary>
    public Func<string, string, bool> Answer { get; set; } = (_, _) => true;

    public static Finding Finding(string id, FindingStatus status = FindingStatus.Improvable, bool fixable = true) => new()
    {
        Id = id,
        Title = "Titre de " + id,
        Status = status,
        Severity = Severity.Medium,
        Current = "valeur lue",
        Expected = "valeur attendue",
        Explanation = "Pourquoi",
        Fixable = fixable,
    };

    public static PlannedChange Change(string id, bool recommended = true, bool advanced = false, string? warning = null) => new()
    {
        Id = id,
        ModuleId = "M06",
        Title = "Corriger " + id,
        Description = "Explication",
        Recommended = recommended,
        Advanced = advanced,
        Warning = warning,
        Writes = [],
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dossier temporaire encore ouvert : Windows le videra.
        }
    }

    /// <summary>Comme le vrai moteur : les résultats arrivent un par un, avant la fin de l'audit.</summary>
    private async Task<AuditRun> RunAsync(IProgress<ModuleResult>? progress)
    {
        foreach (var result in Results)
        {
            progress?.Report(result);
        }

        await Task.Yield();
        return new AuditRun(Snapshot, [.. Results]);
    }
}
