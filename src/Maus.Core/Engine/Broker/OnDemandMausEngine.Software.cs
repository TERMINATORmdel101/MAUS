using Maus.Core.Hardware;
using Maus.Core.Modules.M20Software;
using Maus.Core.Platform;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// La liste des logiciels à mettre à jour (M20) se lit toujours sans droits administrateur (0.8.6) : winget est un outil propre
/// au compte de l'utilisateur, et le moteur ne le lance jamais.
/// </summary>
public sealed partial class OnDemandMausEngine
{
    /// <summary>
    /// Le module M20 seul, dans ce processus, celui de la fenêtre sans droits. Il n'a besoin ni du matériel ni des choix : les
    /// marques « voulu » sont appliquées ensuite avec les choix du moteur.
    /// </summary>
    private static async Task<ModuleResult> ReadSoftwareHereAsync(CancellationToken cancellationToken)
    {
        var registry = new WindowsRegistryReader();
        var context = new AuditContext
        {
            Registry = registry,
            Cim = new WmiCimReader(),
            Commands = new ReadOnlyCommandRunner(),
            Windows = await Task.Run(() => WindowsInfo.Read(registry), cancellationToken).ConfigureAwait(false),
            Hardware = new HardwareProfile(),
            IsElevated = ProcessElevation.IsElevated(),
        };
        return await AuditEngine.RunModuleAsync(new SoftwareUpdatesModule(), context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Audit fait par le moteur : son M20 ne lance pas winget. Dès que le moteur le rend, la liste est lue ici, sans droits ; elle
    /// prend sa place dans l'avancement (un seul résultat par module, la barre reste juste) comme dans le résultat final, avec
    /// les marques « voulu » du moteur. Un M20 que l'utilisateur n'a pas choisi reste tel quel : winget n'est pas lancé.
    /// </summary>
    internal sealed class SoftwareRelay(Func<CancellationToken, Task<ModuleResult>> read, IProgress<ModuleResult>? progress, CancellationToken cancellationToken)
        : IProgress<ModuleResult>, IDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly Lock _gate = new();
        private Task<ModuleResult>? _reading;

        /// <summary>Résultat du moteur à remplacer : M20, lancé (pas « non choisi »).</summary>
        public static bool IsReplaced(ModuleResult result) =>
            result is { ModuleId: SoftwareModuleId, NotChosen: false };

        public void Report(ModuleResult value)
        {
            if (IsReplaced(value))
            {
                Start(value);
                return;
            }

            progress?.Report(value);
        }

        /// <summary>Met la liste lue ici à la place de celle du moteur, et l'annonce dans l'avancement.</summary>
        public async Task<AuditRun> CompleteAsync(AuditRun run)
        {
            // Un moteur d'essai peut ne rien rendre : il n'y a rien à remplacer.
            if (run?.Results is not { } results || results.FirstOrDefault(IsReplaced) is not { } elevated)
            {
                return run!;
            }

            var local = Acknowledgements.Apply(await Start(elevated).ConfigureAwait(false), run.Snapshot.Preferences);
            progress?.Report(local);
            return run with { Results = [.. results.Select(r => IsReplaced(r) ? local : r)] };
        }

        /// <summary>Lecture qui ne lève jamais : une erreur devient un module en erreur, comme dans un audit.</summary>
        public static async Task<ModuleResult> ReadSafelyAsync(Func<CancellationToken, Task<ModuleResult>> read, ModuleResult elevated, CancellationToken cancellationToken)
        {
            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return elevated with { Findings = [], Error = T("Erreur inattendue : {0}", ex.Message) };
            }
        }

        public void Dispose()
        {
            // Audit abandonné ou en erreur : la lecture en cours s'arrête, son résultat n'attend plus personne.
            _stop.Cancel();
            _stop.Dispose();
        }

        private Task<ModuleResult> Start(ModuleResult elevated)
        {
            lock (_gate)
            {
                return _reading ??= ReadSafelyAsync(read, elevated, _stop.Token);
            }
        }
    }

    /// <summary>Identifiant du module des logiciels à mettre à jour.</summary>
    private const string SoftwareModuleId = "M20";
}
