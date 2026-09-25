using System.Management;
using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>Implémentation CIM par System.Management.</summary>
public sealed class WmiCimReader : ICimReader
{
    private const int WbemInvalidNamespace = unchecked((int)0x8004100E);
    private const int WbemInvalidClass = unchecked((int)0x80041010);
    private const int WbemAccessDenied = unchecked((int)0x80041003);
    private const int EAccessDenied = unchecked((int)0x80070005);

    public IReadOnlyList<CimRow> Query(string wql, string scope = CimScopes.Default) => Guard(scope, () =>
    {
        using var searcher = new ManagementObjectSearcher(Connect(scope), new ObjectQuery(wql), new System.Management.EnumerationOptions
        {
            ReturnImmediately = true,
            Rewindable = false,
            Timeout = TimeSpan.FromSeconds(30),
        });
        using var results = searcher.Get();
        var rows = new List<CimRow>();
        foreach (var item in results)
        {
            using (item)
            {
                rows.Add(ToRow(item));
            }
        }

        return (IReadOnlyList<CimRow>)rows;
    });

    public CimRow? InvokeMethod(string wql, string method, IReadOnlyDictionary<string, object?>? parameters = null, string scope = CimScopes.Default) => Guard(scope, () =>
    {
        using var searcher = new ManagementObjectSearcher(Connect(scope), new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (ManagementObject instance in results)
        {
            using (instance)
            {
                using var inParams = instance.GetMethodParameters(method);
                if (parameters is not null)
                {
                    foreach (var (name, value) in parameters)
                    {
                        inParams[name] = value;
                    }
                }

                using var outParams = instance.InvokeMethod(method, inParams, null);
                return outParams is null ? null : ToRow(outParams);
            }
        }

        return null;
    });

    private static ManagementScope Connect(string scope)
    {
        var managementScope = new ManagementScope(scope, new ConnectionOptions
        {
            EnablePrivileges = true,
            Impersonation = ImpersonationLevel.Impersonate,
            Timeout = TimeSpan.FromSeconds(15),
        });
        managementScope.Connect();
        return managementScope;
    }

    private static T Guard<T>(string scope, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new MausAccessDeniedException($"Accès WMI refusé ({scope}).", ex);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotFound)
        {
            throw new DataSourceUnavailableException($"Source WMI absente ({scope}).", ex);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidQuery or ManagementStatus.InvalidProperty
                                                 or ManagementStatus.NotSupported or ManagementStatus.InvalidMethod or ManagementStatus.ProviderLoadFailure)
        {
            // Propriété ou méthode absente sur cette version de Windows : la donnée est indisponible, pas une erreur du module.
            throw new DataSourceUnavailableException($"Requête WMI non prise en charge ({scope}) : {ex.ErrorCode}.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MausAccessDeniedException($"Accès WMI refusé ({scope}).", ex);
        }
        catch (COMException ex) when (ex.HResult is WbemAccessDenied or EAccessDenied)
        {
            throw new MausAccessDeniedException($"Accès WMI refusé ({scope}).", ex);
        }
        catch (COMException ex) when (ex.HResult is WbemInvalidNamespace or WbemInvalidClass)
        {
            throw new DataSourceUnavailableException($"Source WMI absente ({scope}).", ex);
        }
    }

    private static CimRow ToRow(ManagementBaseObject source) =>
        new(source.Properties.Cast<PropertyData>().Select(p => new KeyValuePair<string, object?>(p.Name, Convert(p))));

    private static object? Convert(PropertyData property) => property.Value switch
    {
        null => null,
        string text when property.Type == CimType.DateTime => ParseCimDateTime(text),
        ManagementBaseObject nested => ToRow(nested),
        ManagementBaseObject[] nested => nested.Select(ToRow).ToArray(),
        var value => value,
    };

    private static object? ParseCimDateTime(string text)
    {
        try
        {
            // Les intervalles CIM se terminent par « :000 » ; les dates, par un décalage horaire.
            return text.EndsWith(":000", StringComparison.Ordinal)
                ? ManagementDateTimeConverter.ToTimeSpan(text)
                : ManagementDateTimeConverter.ToDateTime(text);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
