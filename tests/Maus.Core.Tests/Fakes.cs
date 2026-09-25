using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Platform;
using Microsoft.Win32;

namespace Maus.Core.Tests;

/// <summary>Registre en mémoire : clé « HKLM\chemin » ou « HKCU\chemin », puis nom de valeur.</summary>
public sealed class FakeRegistry : IRegistryReader, IRegistryWriter
{
    private readonly Dictionary<string, Dictionary<string, object?>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegistryValueKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writeDenied = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writeIgnored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Écritures reçues, dans l'ordre (« set HKCU\chemin\nom=valeur », « delete … »).</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Refuse l'écriture sous cette clé (droits insuffisants).</summary>
    public FakeRegistry DenyWrite(RegistryHive hive, string path)
    {
        _writeDenied.Add(KeyOf(hive, path));
        return this;
    }

    /// <summary>Accepte l'écriture sans la garder, comme une valeur aussitôt réécrite par Windows.</summary>
    public FakeRegistry IgnoreWrites(RegistryHive hive, string path)
    {
        _writeIgnored.Add(KeyOf(hive, path));
        return this;
    }

    public FakeRegistry SetTyped(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        Set(hive, path, name, value);
        _kinds[KeyOf(hive, path) + "|" + name] = kind;
        return this;
    }

    public RegistryValueKind? GetValueKind(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64)
    {
        var value = GetValue(hive, path, name, view);
        if (value is null)
        {
            return null;
        }

        return _kinds.TryGetValue(KeyOf(hive, path) + "|" + name, out var kind) ? kind : value switch
        {
            int => RegistryValueKind.DWord,
            long => RegistryValueKind.QWord,
            string[] => RegistryValueKind.MultiString,
            byte[] => RegistryValueKind.Binary,
            _ => RegistryValueKind.String,
        };
    }

    public void SetValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        ThrowIfWriteDenied(hive, path);
        Writes.Add($"set {KeyOf(hive, path)}\\{name}={RegistryReaderExtensions.Normalize(value)}");
        if (!_writeIgnored.Contains(KeyOf(hive, path)))
        {
            SetTyped(hive, path, name, value, kind);
        }
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        ThrowIfWriteDenied(hive, path);
        Writes.Add($"delete {KeyOf(hive, path)}\\{name}");
        if (_keys.TryGetValue(KeyOf(hive, path), out var values))
        {
            values.Remove(name);
            _kinds.Remove(KeyOf(hive, path) + "|" + name);
        }
    }

    public void DeleteKeyIfEmpty(RegistryHive hive, string path)
    {
        var key = KeyOf(hive, path);
        if (_keys.TryGetValue(key, out var values) && values.Count == 0 && GetSubKeyNames(hive, path).Count == 0)
        {
            _keys.Remove(key);
            Writes.Add($"delete-key {key}");
        }
    }

    private void ThrowIfWriteDenied(RegistryHive hive, string path)
    {
        if (_writeDenied.Contains(KeyOf(hive, path)))
        {
            throw new MausAccessDeniedException("écriture refusée");
        }
    }

    public FakeRegistry Set(RegistryHive hive, string path, string name, object? value)
    {
        var key = KeyOf(hive, path);
        if (!_keys.TryGetValue(key, out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _keys[key] = values;
        }

        values[name] = value;
        _kinds.Remove(key + "|" + name);
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

public sealed class FakeSystemParameters : ISystemParametersReader, ISystemParametersWriter
{
    /// <summary>Code SPI_SET* vers le code SPI_GET* correspondant.</summary>
    private static readonly Dictionary<uint, uint> SetToGet = new()
    {
        [SpiSet.DragFullWindows] = SpiGet.DragFullWindows,
        [SpiSet.FontSmoothing] = SpiGet.FontSmoothing,
        [SpiSet.ComboBoxAnimation] = SpiGet.ComboBoxAnimation,
        [SpiSet.ListBoxSmoothScrolling] = SpiGet.ListBoxSmoothScrolling,
        [SpiSet.MenuAnimation] = SpiGet.MenuAnimation,
        [SpiSet.SelectionFade] = SpiGet.SelectionFade,
        [SpiSet.TooltipAnimation] = SpiGet.TooltipAnimation,
        [SpiSet.CursorShadow] = SpiGet.CursorShadow,
        [SpiSet.DropShadow] = SpiGet.DropShadow,
        [SpiSet.ClientAreaAnimation] = SpiGet.ClientAreaAnimation,
    };

    public Dictionary<uint, bool?> Values { get; } = [];

    public bool? MinimizeAnimation { get; set; } = false;

    /// <summary>Windows refuse tout SPI_SET*.</summary>
    public bool RejectWrites { get; set; }

    /// <summary>Appels reçus (code SET, valeur, passage par uiParam).</summary>
    public List<(uint Action, bool Value, bool UiParam)> Writes { get; } = [];

    public bool? GetBool(uint action) => Values.GetValueOrDefault(action);

    public bool? GetMinimizeAnimation() => MinimizeAnimation;

    public bool SetBool(uint setAction, bool value, bool useUiParam)
    {
        Writes.Add((setAction, value, useUiParam));
        if (RejectWrites)
        {
            return false;
        }

        Values[SetToGet[setAction]] = value;
        return true;
    }

    public bool SetMinimizeAnimation(bool value)
    {
        if (RejectWrites)
        {
            return false;
        }

        MinimizeAnimation = value;
        return true;
    }
}

/// <summary>Restauration du système simulée : liste de points, protection activable.</summary>
public sealed class FakeSystemRestore : ISystemRestore
{
    public ProtectionState Protection { get; set; } = ProtectionState.Enabled;

    public List<RestorePointInfo> Points { get; } = [new RestorePointInfo(41, "Windows Update", null)];

    /// <summary>Simule la limite de 24 heures : l'appel réussit sans rien créer.</summary>
    public bool SilentlySkip { get; set; }

    /// <summary>Fréquence lue au moment de l'appel, pour vérifier que MAUS l'a bien mise à 0.</summary>
    public Func<SettingValue?>? FrequencyProbe { get; set; }

    public SettingValue? FrequencyDuringCreate { get; private set; }

    public int EnableCalls { get; private set; }

    public ProtectionState GetProtectionState(string drive) => Protection;

    public void EnableProtection(string drive)
    {
        EnableCalls++;
        Protection = ProtectionState.Enabled;
    }

    public IReadOnlyList<RestorePointInfo> ListRestorePoints() => Points.ToList();

    public void CreateRestorePoint(string description)
    {
        FrequencyDuringCreate = FrequencyProbe?.Invoke();
        if (!SilentlySkip && Protection == ProtectionState.Enabled)
        {
            Points.Add(new RestorePointInfo(Points.Max(p => p.SequenceNumber) + 1, description, null));
        }
    }
}

/// <summary>Journal en mémoire, avec copie à chaque enregistrement (comme un fichier).</summary>
public sealed class InMemoryJournalStore : IJournalStore
{
    private readonly Dictionary<string, string> _sessions = [];

    public int SaveCount { get; private set; }

    public bool Unsafe { get; set; }

    public void Save(JournalSession session)
    {
        if (Unsafe)
        {
            throw new JournalUnsafeException("dossier non sûr");
        }

        SaveCount++;
        _sessions[session.Id] = System.Text.Json.JsonSerializer.Serialize(session, FileJournalStore.JsonOptions);
    }

    public JournalSession? Load(string id) =>
        _sessions.TryGetValue(id, out var json) ? System.Text.Json.JsonSerializer.Deserialize<JournalSession>(json, FileJournalStore.JsonOptions) : null;

    public IReadOnlyList<JournalSession> List() => _sessions.Keys.Select(Load).OfType<JournalSession>().OrderByDescending(s => s.CreatedAt).ToList();
}

public sealed class FakeNotifier : ISettingChangeNotifier
{
    public int Broadcasts { get; private set; }

    public void Broadcast() => Broadcasts++;
}

public static class TestFixContext
{
    public static FixContext Create(
        AuditContext audit,
        FakeRegistry registry,
        FakeSystemParameters? parameters = null,
        IJournalStore? journal = null,
        FakeSystemRestore? restore = null,
        FakeNotifier? notifier = null,
        bool elevatedAsAnotherUser = false)
    {
        parameters ??= (FakeSystemParameters)audit.SystemParameters;
        return new FixContext
        {
            Audit = audit,
            Settings = new SettingsAccessor(registry, registry, parameters, parameters),
            Journal = journal ?? new InMemoryJournalStore(),
            SystemRestore = restore ?? new FakeSystemRestore(),
            Notifier = notifier ?? new FakeNotifier(),
            ElevatedAsAnotherUser = elevatedAsAnotherUser,
        };
    }
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
