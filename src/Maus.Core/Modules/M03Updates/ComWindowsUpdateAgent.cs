using System.Runtime.InteropServices;
using Maus.Core.Platform;
using Microsoft.CSharp.RuntimeBinder;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M03Updates;

/// <summary>
/// Agent Windows Update réel, par liaison tardive COM (<c>Microsoft.Update.Session</c>, <c>Microsoft.Update.AutoUpdate</c>).
/// Seules des lectures sont faites : ni téléchargement, ni installation, ni modification de réglage.
/// </summary>
internal sealed class ComWindowsUpdateAgent : IWindowsUpdateAgent
{
    /// <summary>Type de mise à jour WUA : 2 = pilote (<c>utDriver</c>).</summary>
    private const int DriverType = 2;

    /// <summary>Codes <c>OperationResultCode</c> d'une recherche en échec (4) ou abandonnée (5).</summary>
    private const int ResultFailed = 4;
    private const int ResultAborted = 5;

    public AutomaticUpdatesResults? GetAutomaticUpdatesResults()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
        if (type is null)
        {
            return null;
        }

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            dynamic automaticUpdates = instance!;
            dynamic results = automaticUpdates.Results;
            object? lastSearch = results.LastSearchSuccessDate;
            return new AutomaticUpdatesResults(AsUtc(lastSearch));
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            Release(instance);
        }
    }

    public IReadOnlyList<UpdateHistoryEntry>? GetHistory(int maxEntries)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null)
        {
            return null;
        }

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            dynamic session = instance!;
            session.ClientApplicationID = T("MAUS (audit en lecture seule)");
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();

            // QueryHistory refuse un nombre nul (WU_E_INVALIDINDEX, Microsoft Learn) : historique vide, rien à demander.
            if (total <= 0)
            {
                return [];
            }

            var entries = new List<UpdateHistoryEntry>();
            dynamic collection = searcher.QueryHistory(0, Math.Min(total, maxEntries));
            int count = collection.Count;
            for (var i = 0; i < count; i++)
            {
                object item = collection[i];
                if (ToHistoryEntry(item) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lecture d'appoint : toute réponse imprévue de l'interop COM (E_INVALIDARG, E_NOTIMPL, E_POINTER, date illisible…)
            // donne « non lu » pour ce seul constat, jamais la perte du module.
            return null;
        }
        finally
        {
            Release(instance);
        }
    }

    /// <summary>Une ligne sans date ni titre ne dit rien d'utilisable : elle est ignorée.</summary>
    private static UpdateHistoryEntry? ToHistoryEntry(object item)
    {
        dynamic entry = item;
        object? date = entry.Date;
        string? title = entry.Title;
        if (AsUtc(date) is not { } dateUtc || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string? updateId = entry.UpdateIdentity.UpdateID;
        int operation = entry.Operation;
        int resultCode = entry.ResultCode;
        int hresult = entry.HResult;
        int unmapped = entry.UnmappedResultCode;
        string? client = entry.ClientApplicationID;
        string? service = entry.ServiceID;
        return new UpdateHistoryEntry(
            string.IsNullOrEmpty(updateId) ? title : updateId,
            title,
            dateUtc,
            operation,
            resultCode,
            hresult != 0 ? hresult : unmapped,
            client,
            service);
    }

    public async Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, bool online, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Le fil de recherche reçoit l'annulation : il demande lui-même à l'agent d'interrompre la recherche (RequestAbort),
        // au lieu de la laisser tourner dans Windows après le délai ou l'annulation.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        var token = stop.Token;
        try
        {
            return await RunOnDedicatedThread(() => Search(criteria, online, token), timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // La minuterie de la source pourrait sinon toucher une source déjà libérée.
            stop.Cancel();
        }
    }

    /// <summary>
    /// La recherche WUA tourne sur un fil dédié ; au-delà du délai, la tâche rend la main tout de suite et le fil interrompt
    /// la recherche de l'agent (voir <see cref="Search"/>).
    /// Aucune erreur ne sort de ce fil (elle fermerait MAUS sans trace) : toutes deviennent l'erreur de la tâche, donc un
    /// constat « recherche impossible ».
    /// </summary>
    internal static Task<IReadOnlyList<PendingUpdate>> RunOnDedicatedThread(
        Func<IReadOnlyList<PendingUpdate>> search, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<PendingUpdate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(search());
            }
            catch (Exception ex)
            {
                completion.TrySetException(AsSearchFailure(ex));
            }
        })
        {
            IsBackground = true,
            Name = "MAUS - recherche Windows Update",
        };
        thread.Start();
        return completion.Task.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Erreurs déjà décrites par le module (code COM, accès refusé, échec annoncé par l'agent) : telles quelles. Toute autre
    /// (liaison tardive, conversion, et ce que l'interop COM produit pour E_INVALIDARG, E_NOTIMPL ou E_POINTER :
    /// <see cref="ArgumentException"/>, <see cref="NotImplementedException"/>, <see cref="NullReferenceException"/>…) :
    /// « réponse inattendue ».
    /// </summary>
    internal static Exception AsSearchFailure(Exception exception) =>
        exception is COMException or UnauthorizedAccessException or MausAccessDeniedException
            or InvalidOperationException or DataSourceUnavailableException
            ? exception
            : new InvalidOperationException(T("Réponse inattendue de l'agent Windows Update."), exception);

    /// <summary>
    /// Recherche asynchrone de l'agent (BeginSearch / EndSearch, Microsoft Learn « IUpdateSearcher::BeginSearch ») surveillée
    /// toutes les 200 ms : à l'annulation ou au délai, <c>RequestAbort</c> l'interrompt dans Windows. Si l'agent refuse la
    /// forme asynchrone, recherche bloquante comme avant.
    /// </summary>
    private static List<PendingUpdate> Search(string criteria, bool online, CancellationToken cancellationToken)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new DataSourceUnavailableException("Agent Windows Update absent (Microsoft.Update.Session).");
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            dynamic session = instance!;
            session.ClientApplicationID = T("MAUS (audit en lecture seule)");
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = online;
            dynamic result = SearchWithAbort(searcher, criteria, cancellationToken);
            int resultCode = result.ResultCode;
            if (resultCode is ResultFailed or ResultAborted)
            {
                throw new InvalidOperationException(T("La recherche Windows Update a échoué (code de résultat {0}).", resultCode));
            }

            var updates = new List<PendingUpdate>();
            dynamic collection = result.Updates;
            int count = collection.Count;
            for (var i = 0; i < count; i++)
            {
                object item = collection[i];
                updates.Add(ToPendingUpdate(item));
            }

            return updates;
        }
        finally
        {
            Release(instance);
        }
    }

    private static dynamic SearchWithAbort(dynamic searcher, string criteria, CancellationToken cancellationToken)
    {
        dynamic job;
        try
        {
            // L'agent exige un objet de rappel (null est refusé : « Could not convert argument 1 », vérifié le 12/10/2026).
            job = searcher.BeginSearch(criteria, new SearchCompleted(), null);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return searcher.Search(criteria);
        }

        try
        {
            // Borne de sécurité : au plus une heure d'attente, quoi qu'il arrive (le délai du module est bien plus court).
            for (var waited = 0; !(bool)job.IsCompleted && waited < 3600 * 5; waited++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    job.RequestAbort();
                    for (var i = 0; !(bool)job.IsCompleted && i < 50; i++)
                    {
                        Thread.Sleep(200);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                Thread.Sleep(200);
            }

            return searcher.EndSearch(job);
        }
        finally
        {
            try
            {
                job.CleanUp();
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Tâche déjà libérée par l'agent.
            }
        }
    }

    private static PendingUpdate ToPendingUpdate(object item)
    {
        dynamic update = item;
        var categories = new List<string>();
        dynamic categoryCollection = update.Categories;
        int categoryCount = categoryCollection.Count;
        for (var i = 0; i < categoryCount; i++)
        {
            string? id = categoryCollection[i].CategoryID;
            if (!string.IsNullOrEmpty(id))
            {
                categories.Add(id);
            }
        }

        string? updateId = update.Identity.UpdateID;
        string? title = update.Title;
        string? severity = update.MsrcSeverity;
        int updateType = update.Type;
        bool browseOnly = update.BrowseOnly;
        return new PendingUpdate(
            updateId ?? title ?? string.Empty,
            title ?? string.Empty,
            categories,
            string.IsNullOrWhiteSpace(severity) ? null : severity,
            browseOnly,
            updateType == DriverType);
    }

    /// <summary>Les dates WUA sont en temps universel ; une date vide arrive comme <c>null</c> ou comme objet non daté.</summary>
    private static DateTime? AsUtc(object? value) =>
        value is DateTime date && date.Year > 1900 ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}

/// <summary>Interface de rappel de fin de recherche de l'agent Windows Update (wuapi.h, ISearchCompletedCallback).</summary>
[ComImport]
[Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISearchCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object searchJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

/// <summary>Rappel vide : MAUS surveille lui-même la fin de la recherche (IsCompleted).</summary>
[ComVisible(true)]
internal sealed class SearchCompleted : ISearchCompletedCallback
{
    public void Invoke(object searchJob, object callbackArgs)
    {
    }
}
