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
            Thread.Sleep(1000);

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
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "logs");
            Directory.CreateDirectory(directory);
            var text = $"{Environment.NewLine}MAUS {Maus.Core.AppVersion.Display} · {DateTime.Now.ToString("g", CultureInfo.InvariantCulture)}{Environment.NewLine}{title}{Environment.NewLine}"
                + string.Join(Environment.NewLine, lines.Select(l => "  " + l)) + Environment.NewLine;
            File.AppendAllText(Path.Combine(directory, "interface-bloquee.txt"), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Journal de diagnostic seulement.
        }
    }
}
