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
            object? lastInstallation = results.LastInstallationSuccessDate;
            return new AutomaticUpdatesResults(AsUtc(lastSearch), AsUtc(lastInstallation));
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

    public async Task<IReadOnlyList<PendingUpdate>> SearchAsync(string criteria, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Le fil de recherche reçoit l'annulation : il demande lui-même à l'agent d'interrompre la recherche (RequestAbort),
        // au lieu de la laisser tourner dans Windows après le délai ou l'annulation.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        var token = stop.Token;
        return await RunOnDedicatedThread(() => Search(criteria, token), timeout, cancellationToken).ConfigureAwait(false);
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
    private static List<PendingUpdate> Search(string criteria, CancellationToken cancellationToken)
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

        var articles = new List<string>();
        dynamic articleCollection = update.KBArticleIDs;
        int articleCount = articleCollection.Count;
        for (var i = 0; i < articleCount; i++)
        {
            string? article = articleCollection[i];
            if (!string.IsNullOrEmpty(article))
            {
                articles.Add(article);
            }
        }

        string? updateId = update.Identity.UpdateID;
        string? title = update.Title;
        string? severity = update.MsrcSeverity;
        int updateType = update.Type;
        return new PendingUpdate(
            updateId ?? title ?? string.Empty,
            title ?? string.Empty,
            articles,
            categories,
            string.IsNullOrWhiteSpace(severity) ? null : severity,
            ReadBrowseOnly(item),
            updateType == DriverType);
    }

    /// <summary><c>IUpdate3.BrowseOnly</c> : absent des agents très anciens, d'où la lecture protégée.</summary>
    private static bool ReadBrowseOnly(object item)
    {
        dynamic update = item;
        try
        {
            bool browseOnly = update.BrowseOnly;
            return browseOnly;
        }
        catch (RuntimeBinderException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
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
