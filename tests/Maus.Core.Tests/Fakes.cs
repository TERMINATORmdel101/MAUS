using Maus.Core;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests;

/// <summary>Registre en mémoire : clé « HKLM\chemin » ou « HKCU\chemin », puis nom de valeur.</summary>
public sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<string, Dictionary<string, object?>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistry Set(RegistryHive hive, string path, string name, object? value)
    {
        var key = KeyOf(hive, path);
        if (!_keys.TryGetValue(key, out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _keys[key] = values;
        }

        values[name] = value;
        return this;
    }

    public FakeRegistry Deny(RegistryHive hive, string path)
    {
        _denied.Add(KeyOf(hive, path));
        return this;
    }

    public object? GetValue(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64)
    {
        ThrowIfDenied(hive, path);
        return _keys.TryGetValue(KeyOf(hive, path), out var values) ? values.GetValueOrDefault(name) : null;
    }

    public bool KeyExists(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        ThrowIfDenied(hive, path);
        var prefix = KeyOf(hive, path);
        return _keys.Keys.Any(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        ThrowIfDenied(hive, path);
        return _keys.TryGetValue(KeyOf(hive, path), out var values) ? values.Keys.ToList() : [];
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        ThrowIfDenied(hive, path);
        var prefix = KeyOf(hive, path) + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ThrowIfDenied(RegistryHive hive, string path)
    {
        if (_denied.Contains(KeyOf(hive, path)))
        {
            throw new MausAccessDeniedException("refusé");
        }
    }

    private static string KeyOf(RegistryHive hive, string path) =>
        (hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + path.Trim('\\');
}

/// <summary>CIM en mémoire : réponses indexées par requête WQL exacte (et portée).</summary>
public sealed class FakeCim : ICimReader
{
    private readonly Dictionary<string, Func<IReadOnlyList<CimRow>>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public FakeCim Answer(string wql, params Dictionary<string, object?>[] rows) => Answer(wql, CimScopes.Default, rows);

    public FakeCim Answer(string wql, string scope, params Dictionary<string, object?>[] rows)
    {
        _answers[$"{scope}|{wql}"] = () => rows.Select(r => new CimRow(r)).ToList();
        return this;
    }

    public FakeCim Throw(string wql, Exception exception, string scope = CimScopes.Default)
    {
        _answers[$"{scope}|{wql}"] = () => throw exception;
        return this;
    }

    /// <summary>Requêtes reçues, pour vérifier qu'un module interroge ce qu'il faut.</summary>
    public List<string> Received { get; } = [];

    public IReadOnlyList<CimRow> Query(string wql, string scope = CimScopes.Default)
    {
        Received.Add(wql);
        return _answers.TryGetValue($"{scope}|{wql}", out var answer) ? answer() : [];
    }

    public CimRow? InvokeMethod(string wql, string method, IReadOnlyDictionary<string, object?>? parameters = null, string scope = CimScopes.Default) =>
        _answers.TryGetValue($"{scope}|{wql}|{method}", out var answer) && answer() is { Count: > 0 } rows ? rows[0] : null;

    public FakeCim AnswerMethod(string wql, string method, Dictionary<string, object?> outParams, string scope = CimScopes.Default)
    {
        _answers[$"{scope}|{wql}|{method}"] = () => [new CimRow(outParams)];
        return this;
    }
}

public sealed class FakeSystemParameters : ISystemParametersReader
{
    public Dictionary<uint, bool?> Values { get; } = [];

    public bool? MinimizeAnimation { get; set; } = false;

    public bool? GetBool(uint action) => Values.GetValueOrDefault(action);

    public bool? GetMinimizeAnimation() => MinimizeAnimation;
}

public sealed class FakeCommands : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _results = new(StringComparer.OrdinalIgnoreCase);

    public FakeCommands Answer(string commandLine, string stdout, int exitCode = 0)
    {
        _results[commandLine] = new CommandResult(exitCode, stdout, string.Empty, false);
        return this;
    }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var line = $"{executable} {string.Join(' ', arguments)}".Trim();
        return _results.TryGetValue(line, out var result)
            ? Task.FromResult(result)
            : throw new DataSourceUnavailableException($"Pas de réponse simulée pour : {line}");
    }
}

/// <summary>Journaux simulés : événements ajoutés par journal, filtrés comme le vrai lecteur.</summary>
public sealed class FakeEventLogs : IEventLogReader
{
    private readonly List<(string Log, EventRecordInfo Record)> _events = [];
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);

    public FakeEventLogs Add(string logName, string provider, int id, DateTime timeCreated, Dictionary<string, string>? data = null)
    {
        _events.Add((logName, new EventRecordInfo(id, provider, timeCreated, data ?? [])));
        return this;
    }

    public FakeEventLogs Deny(string logName)
    {
        _denied.Add(logName);
        return this;
    }

    public IReadOnlyList<EventRecordInfo> Query(string logName, string? provider, IReadOnlyCollection<int> eventIds, DateTime since, int maxEvents = 200, bool includeMessage = false)
    {
        if (_denied.Contains(logName))
        {
            throw new MausAccessDeniedException("refusé");
        }

        return _events
            .Where(e => e.Log.Equals(logName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Record)
            .Where(r => provider is null || r.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .Where(r => eventIds.Count == 0 || eventIds.Contains(r.Id))
            .Where(r => r.TimeCreated >= since)
            .OrderByDescending(r => r.TimeCreated)
            .Take(maxEvents)
            .ToList();
    }
}

public sealed class FakePackages : IPackageInventory
{
    public List<InstalledPackage> Packages { get; } = [];

    public FakePackages Add(string name, string version = "1.0.0.0")
    {
        Packages.Add(new InstalledPackage(name, $"{name}_8wekyb3d8bbwe", version, "CN=Microsoft Corporation"));
        return this;
    }

    public IReadOnlyList<InstalledPackage> GetUserPackages() => Packages;
}

/// <summary>Système de fichiers en mémoire (chemins insensibles à la casse).</summary>
public sealed class FakeFiles : IFileSystemReader
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Company, string? Product, string? Version)> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long, long)> _drives = new(StringComparer.OrdinalIgnoreCase);

    public FakeFiles AddFile(string path, string content = "")
    {
        _files[path] = content;
        return this;
    }

    public FakeFiles SetVersionInfo(string path, string? company, string? product = null, string? version = null)
    {
        _versions[path] = (company, product, version);
        return this;
    }

    public FakeFiles SetDrive(string root, long freeBytes, long totalBytes)
    {
        _drives[root] = (freeBytes, totalBytes);
        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    public string ReadAllText(string path) => _files.TryGetValue(path, out var content) ? content : throw new FileNotFoundException(path);

    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern = "*")
    {
        var prefix = directory.TrimEnd('\\') + "\\";
        var extension = searchPattern.StartsWith("*.", StringComparison.Ordinal) ? searchPattern[1..] : null;
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !f[prefix.Length..].Contains('\\'))
            .Where(f => extension is null || f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public (string? Company, string? Product, string? Version) GetVersionInfo(string path) =>
        _versions.TryGetValue(path, out var info) ? info : (null, null, null);

    public (long FreeBytes, long TotalBytes)? GetDriveSpace(string root) =>
        _drives.TryGetValue(root, out var space) ? space : null;
}

public static class TestContext
{
    public static AuditContext Create(
        FakeRegistry? registry = null,
        FakeCim? cim = null,
        FakeCommands? commands = null,
        FakeSystemParameters? parameters = null,
        HardwareProfile? hardware = null,
        WindowsInfo? windows = null,
        bool elevated = true,
        DateTimeOffset? now = null,
        FakeEventLogs? eventLogs = null,
        FakePackages? packages = null,
        FakeFiles? files = null) => new()
    {
        Registry = registry ?? new FakeRegistry(),
        Cim = cim ?? new FakeCim(),
        Commands = commands ?? new FakeCommands(),
        SystemParameters = parameters ?? new FakeSystemParameters(),
        EventLogs = eventLogs ?? new FakeEventLogs(),
        Packages = packages ?? new FakePackages(),
        Files = files ?? new FakeFiles(),
        Hardware = hardware ?? new HardwareProfile { FormFactor = FormFactor.Desktop },
        Windows = windows ?? new WindowsInfo("Windows 11 Pro", "Professional", "25H2", 26200, 1000),
        IsElevated = elevated,
        Now = now ?? new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2)),
    };
}
