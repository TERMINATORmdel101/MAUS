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
        DateTimeOffset? now = null) => new()
    {
        Registry = registry ?? new FakeRegistry(),
        Cim = cim ?? new FakeCim(),
        Commands = commands ?? new FakeCommands(),
        SystemParameters = parameters ?? new FakeSystemParameters(),
        Hardware = hardware ?? new HardwareProfile { FormFactor = FormFactor.Desktop },
        Windows = windows ?? new WindowsInfo("Windows 11 Pro", "Professional", "25H2", 26200, 1000),
        IsElevated = elevated,
        Now = now ?? new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2)),
    };
}
