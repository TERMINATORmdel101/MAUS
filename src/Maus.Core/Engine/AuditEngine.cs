using System.Diagnostics;
using System.Reflection;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine;

/// <summary>Exécute la détection de tous les modules, sans jamais écrire sur le système.</summary>
public sealed class AuditEngine
{
    public AuditEngine(IEnumerable<IAuditModule> modules)
    {
        Modules = modules.OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<IAuditModule> Modules { get; }

    /// <summary>Modules intégrés, trouvés par réflexion : ajouter un module ne demande aucune inscription manuelle.</summary>
    public static AuditEngine CreateWithBuiltInModules() => new(DiscoverModules(typeof(IAuditModule).Assembly));

    public static IEnumerable<IAuditModule> DiscoverModules(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAuditModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IAuditModule)Activator.CreateInstance(t)!);

    public async Task<IReadOnlyList<ModuleResult>> RunAsync(
        AuditContext context,
        IProgress<ModuleResult>? progress = null,
        int maxParallelism = 4,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = Modules.Select(async module =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await RunModuleAsync(module, context, cancellationToken).ConfigureAwait(false);
                progress?.Report(result);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public static async Task<ModuleResult> RunModuleAsync(IAuditModule module, AuditContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(module.Timeout);
        try
        {
            // Task.Run isole les modules qui bloquent (WMI, COM) du thread appelant.
            var findings = await Task.Run(() => module.DetectAsync(context, timeout.Token), timeout.Token)
                .WaitAsync(module.Timeout, cancellationToken)
                .ConfigureAwait(false);
            return Preferences.Acknowledgements.Apply(new ModuleResult(module.Id, module.Title, findings, stopwatch.Elapsed), context.Preferences);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return new ModuleResult(module.Id, module.Title, [], stopwatch.Elapsed, T("Délai dépassé ({0:0} s).", module.Timeout.TotalSeconds));
        }
        catch (Exception ex)
        {
            return new ModuleResult(module.Id, module.Title, [], stopwatch.Elapsed, T("Erreur inattendue : {0}", ex.Message));
        }
    }
}
