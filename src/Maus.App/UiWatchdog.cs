using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Maus.Core.Diagnostics;

namespace Maus.App;

/// <summary>
/// Garde de l'interface : un fil à part vérifie chaque seconde que la fenêtre répond. Si elle reste figée plus de
/// <see cref="Threshold"/>, il écrit les dernières actions de MAUS dans <c>%LOCALAPPDATA%\MAUS\logs\interface-bloquee.txt</c>,
/// puis la durée du gel quand elle revient. Journal local, jamais envoyé.
/// </summary>
public sealed class UiWatchdog
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private long _lastPongTicks = DateTime.UtcNow.Ticks;

    private UiWatchdog(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static void Start(Dispatcher dispatcher)
    {
        var watchdog = new UiWatchdog(dispatcher);
        new Thread(watchdog.Run) { IsBackground = true, Name = "MAUS garde de l'interface", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void Run()
    {
        DateTime? frozenSince = null;
        while (!_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks));
            var before = System.Diagnostics.Stopwatch.GetTimestamp();
            Thread.Sleep(1000);

            // Sommeil bien plus long que prévu : le PC était en veille, pas figé.
            if (System.Diagnostics.Stopwatch.GetElapsedTime(before) > TimeSpan.FromSeconds(3))
            {
                Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);
                continue;
            }

            var silence = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastPongTicks), DateTimeKind.Utc);
            if (silence > Threshold && frozenSince is null)
            {
                frozenSince = DateTime.Now - silence;
                Write($"Interface figée depuis {frozenSince:HH:mm:ss} ({silence.TotalSeconds:0} s). Dernières actions :", Breadcrumbs.Snapshot());
            }
            else if (silence < TimeSpan.FromSeconds(2) && frozenSince is { } since)
            {
                Write($"Interface revenue à {DateTime.Now:HH:mm:ss}, après {(DateTime.Now - since).TotalSeconds:0} s de gel.", []);
                frozenSince = null;
            }
        }
    }

    private static void Write(string title, IReadOnlyList<string> lines)
    {
        try
        {
            // Fenêtre lancée en administrateur sans dossier protégé : rien sur le disque (0.8.6).
            if (Maus.Core.Diagnostics.CrashLog.Folder is not { } directory)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var text = $"{Environment.NewLine}MAUS {Maus.Core.AppVersion.Full} · {DateTime.Now.ToString("g", CultureInfo.InvariantCulture)}{Environment.NewLine}{title}{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines.Select(l => "  " + l)) + Environment.NewLine;
            File.AppendAllText(Path.Combine(directory, "interface-bloquee.txt"), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Journal de diagnostic seulement.
        }
    }
}
