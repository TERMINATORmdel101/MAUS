using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Lecture de <c>bcdedit /enum {current}</c> : un élément par ligne, nom puis valeur (noms non traduits, valeurs parfois traduites).</summary>
internal static class BcdEditParser
{
    private static string[] YesWords => ["Yes", T("Oui"), "Ja", "Sí", "Si", "Sì", "Sim", "On", "True"];

    /// <summary>Éléments de la première entrée, ou dictionnaire vide si la sortie est illisible.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? output)
    {
        var elements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return elements;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('-'))
            {
                continue;
            }

            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            var value = line[separator..].Trim();
            if (value.Length > 0)
            {
                elements.TryAdd(name, value);
            }
        }

        // Une sortie sans aucun élément connu est un message d'erreur, pas une entrée de démarrage.
        return elements.ContainsKey("path") || elements.ContainsKey("osdevice") || elements.ContainsKey("nx") || elements.ContainsKey("description")
            ? elements
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsYes(IReadOnlyDictionary<string, string> elements, string name) =>
        elements.TryGetValue(name, out var value) && YesWords.Any(word => value.Equals(word, StringComparison.OrdinalIgnoreCase));
}

internal enum WinHttpProxyKind
{
    Unknown,
    Direct,
    Proxy,
}

internal sealed record WinHttpProxy(WinHttpProxyKind Kind, string? Server = null);

/// <summary>Lecture de <c>netsh winhttp show proxy</c>, en français comme en anglais.</summary>
internal static class WinHttpProxyParser
{
    private static string[] DirectMarkers => ["Direct access", T("Accès direct"), "Acces direct", T("sans serveur proxy"), T("no proxy server")];

    public static WinHttpProxy Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new WinHttpProxy(WinHttpProxyKind.Unknown);
        }

        if (DirectMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new WinHttpProxy(WinHttpProxyKind.Direct);
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var separator = rawLine.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (key.Contains("proxy", StringComparison.OrdinalIgnoreCase) && value.Length > 0 && !value.StartsWith('('))
            {
                return new WinHttpProxy(WinHttpProxyKind.Proxy, value);
            }
        }

        return new WinHttpProxy(WinHttpProxyKind.Unknown);
    }
}

/// <summary>Lecture de <c>reagentc /info</c> : état de l'environnement de récupération (WinRE).</summary>
internal static class ReAgentInfoParser
{
    /// <summary>Vrai si WinRE est activé, faux s'il est désactivé, <c>null</c> si la sortie est illisible.</summary>
    public static bool? ParseEnabled(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var separator = rawLine.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || !rawLine[..separator].Contains("Windows RE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = rawLine[(separator + 1)..].Trim().ToLowerInvariant();
            if (value.StartsWith("enabled", StringComparison.Ordinal) || value.StartsWith("activ", StringComparison.Ordinal))
            {
                return true;
            }

            if (value.StartsWith("disabled", StringComparison.Ordinal) || (value.StartsWith('d') && value.Contains("sactiv", StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return null;
    }
}

internal sealed record HostsEntry(string Address, string HostName);

/// <summary>Lecture du fichier hosts : lignes actives qui redirigent un domaine Microsoft.</summary>
internal static class HostsFileParser
{
    private static readonly string[] MicrosoftDomains =
    [
        "microsoft.com", "windowsupdate.com", "live.com", "msftconnecttest.com", "msftncsi.com", "windows.com", "microsoftonline.com",
    ];

    public static IReadOnlyList<HostsEntry> Parse(string? content)
    {
        var entries = new List<HostsEntry>();
        if (string.IsNullOrEmpty(content))
        {
            return entries;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var comment = rawLine.IndexOf('#', StringComparison.Ordinal);
            var line = (comment >= 0 ? rawLine[..comment] : rawLine).Trim();
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < tokens.Length; i++)
            {
                entries.Add(new HostsEntry(tokens[0], tokens[i]));
            }
        }

        return entries;
    }

    public static IReadOnlyList<HostsEntry> FindMicrosoftEntries(string? content) =>
        Parse(content).Where(entry => IsMicrosoftDomain(entry.HostName)).ToList();

    public static bool IsMicrosoftDomain(string hostName)
    {
        var host = hostName.Trim().TrimEnd('.');
        return MicrosoftDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Extraction de l'exécutable d'une ligne de commande des clés Run et RunOnce.</summary>
internal static class StartupCommandParser
{
    public static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        // Chemin non protégé par des guillemets, espaces compris : on coupe après « .exe ».
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return text[..(exe + 4)];
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? text[..space] : text;
    }
}

/// <summary>État d'un produit déclaré dans le Centre de sécurité Windows (<c>root\SecurityCenter2</c>).</summary>
internal sealed record SecurityProduct(string Name, long State)
{
    /// <summary>Bits 12 à 15 de <c>productState</c> : 1 = actif, 0 = coupé, 2 = en veille, 3 = expiré.</summary>
    public bool IsActive => (State & 0xF000) == 0x1000;

    public bool IsMicrosoft =>
        Name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Pare-feu Windows", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Windows Firewall", StringComparison.OrdinalIgnoreCase);

    public static SecurityProduct? ActiveThirdParty(IReadOnlyList<SecurityProduct>? products) =>
        products?.FirstOrDefault(p => p.IsActive && !p.IsMicrosoft);
}
