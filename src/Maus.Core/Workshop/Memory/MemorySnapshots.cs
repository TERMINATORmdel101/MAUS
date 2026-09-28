using System.Globalization;
using System.Text.Json;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory;

/// <summary>Une valeur d'une fiche mémoire enregistrée : libellé (« tCL », « Horloge mémoire ») et valeur affichée.</summary>
public sealed record MemoryValue(string Key, string Value);

/// <summary>Écart entre deux fiches : valeur avant, valeur après (<c>null</c> si absente d'un côté).</summary>
public sealed record MemoryDifference(string Key, string? Before, string? After);

/// <summary>
/// Fiche mémoire enregistrée après chaque lecture, pour comparer avant / après un réglage dans le BIOS. Elle ne garde que
/// des valeurs techniques (horloges, réglages, timings, références des barrettes, tensions), rien de personnel.
/// </summary>
public sealed record MemorySnapshot
{
    public required DateTimeOffset At { get; init; }

    /// <summary>Résumé sur une ligne (« DDR4-3467 · 16-19-19-39 · 2N »).</summary>
    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<MemoryValue> Values { get; init; } = [];

    /// <summary>Fiche tirée d'une lecture : contrôleur (Intel ou AMD), barrettes, vitesse vue par Windows, tension mesurée.</summary>
    public static MemorySnapshot From(MemoryDetailReport report, IReadOnlyList<MemorySlotConfiguration> slots, string? measuredVoltage, DateTimeOffset at)
    {
        var values = new List<MemoryValue>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && values.All(v => v.Key != key))
            {
                values.Add(new MemoryValue(key, value));
            }
        }

        static string Mhz(double mhz) => mhz.ToString("0", CultureInfo.InvariantCulture) + " MHz";

        IReadOnlyList<MemoryTiming> timings = [];
        string? type = null;
        double? dclk = null;
        string? commandRate = null;
        if (report.Intel is { } intel)
        {
            type = intel.MemoryType;
            dclk = intel.DclkMhz;
            foreach (var clock in intel.Clocks)
            {
                Add(ClockName(clock.Key), Mhz(clock.Mhz));
            }

            foreach (var setting in intel.Settings)
            {
                Add(setting.Key == "CommandRate" ? T("Command rate") : setting.Key, setting.Value);
            }

            commandRate = intel.Settings.FirstOrDefault(s => s.Key == "CommandRate")?.Value;
            timings = intel.Timings;
        }
        else if (report.Live is { } live)
        {
            type = live.Ddr5 ? "DDR5" : "DDR4";
            dclk = live.MclkMhz;
            Add("MCLK", Mhz(live.MclkMhz));
            if (report.Clocks is { } clocks)
            {
                Add("FCLK", clocks.FclkMhz is { } f ? Mhz(f) : null);
                Add("UCLK", clocks.UclkMhz is { } u ? Mhz(u) : null);
            }

            commandRate = live.Settings.Command2T ? "2T" : "1T";
            Add(T("Command rate"), commandRate);
            Add("Gear Down", live.Settings.GearDownMode ? T("activé") : T("désactivé"));
            Add("Power Down", live.Settings.PowerDown ? T("activé") : T("désactivé"));
            timings = live.Timings;
        }

        foreach (var timing in timings)
        {
            Add(timing.Key, timing.Clocks?.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var slot in slots)
        {
            Add(T("Vitesse appliquée ({0})", slot.Slot), slot.ConfiguredMts is { } mts ? string.Create(CultureInfo.InvariantCulture, $"{mts} MT/s") : null);
            Add(T("Barrette {0}", slot.Slot), string.Join(" ", new[] { slot.Manufacturer, slot.PartNumber }.Where(p => !string.IsNullOrWhiteSpace(p))));
        }

        Add(T("Tension mesurée"), measuredVoltage);

        string? Timing(string key) => timings.FirstOrDefault(t => t.Key == key)?.Clocks?.ToString(CultureInfo.InvariantCulture);
        var primaries = new[] { Timing("tCL"), Timing("tRCD") ?? Timing("tRCDRD"), Timing("tRP"), Timing("tRAS") };
        var summary = new List<string>();
        if (type is not null)
        {
            summary.Add(dclk is { } d ? string.Create(CultureInfo.InvariantCulture, $"{type}-{d * 2:0}") : type);
        }
        else if (slots.Count > 0 && slots[0].ConfiguredMts is { } applied)
        {
            summary.Add(string.Create(CultureInfo.InvariantCulture, $"{applied} MT/s"));
        }

        if (primaries.All(p => p is not null))
        {
            summary.Add(string.Join("-", primaries));
        }

        if (commandRate is not null)
        {
            summary.Add(commandRate);
        }

        return new MemorySnapshot { At = at, Summary = string.Join(" · ", summary), Values = values };
    }

    /// <summary>Valeurs qui diffèrent entre <paramref name="before"/> et <paramref name="after"/>, dans l'ordre de la fiche la plus récente.</summary>
    public static IReadOnlyList<MemoryDifference> Compare(MemorySnapshot before, MemorySnapshot after)
    {
        var previous = before.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var differences = new List<MemoryDifference>();
        foreach (var value in after.Values)
        {
            previous.TryGetValue(value.Key, out var old);
            if (!string.Equals(old, value.Value, StringComparison.Ordinal))
            {
                differences.Add(new MemoryDifference(value.Key, old, value.Value));
            }

            previous.Remove(value.Key);
        }

        differences.AddRange(before.Values.Where(v => previous.ContainsKey(v.Key)).Select(v => new MemoryDifference(v.Key, v.Value, null)));
        return differences;
    }

    /// <summary>Texte des écarts, une ligne par valeur : « tCL : 16 → 15 ».</summary>
    public static string Describe(IReadOnlyList<MemoryDifference> differences) => differences.Count == 0
        ? T("Aucune différence : même vitesse, mêmes réglages et mêmes timings.")
        : string.Join(Environment.NewLine, differences.Select(d => $"{d.Key} : {d.Before ?? "—"} → {d.After ?? "—"}"));

    private static string ClockName(string key) => key switch
    {
        "DCLK" => T("Horloge mémoire"),
        "RING" => "Ring",
        "SA" => T("Agent système"),
        _ => key,
    };
}

/// <summary>Fiches mémoire enregistrées dans <c>%LOCALAPPDATA%\MAUS\memoire</c> (les 30 dernières).</summary>
public sealed class MemorySnapshotStore(string directory)
{
    public const int Keep = 30;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MemorySnapshotStore CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "memoire"));

    public void Save(MemorySnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        var name = "fiche-" + snapshot.At.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(snapshot, Json));
        foreach (var old in Files().Skip(Keep))
        {
            File.Delete(old);
        }
    }

    /// <summary>Fiches enregistrées, de la plus récente à la plus ancienne ; un fichier illisible est ignoré.</summary>
    public IReadOnlyList<MemorySnapshot> List()
    {
        var snapshots = new List<MemorySnapshot>();
        foreach (var file in Files())
        {
            try
            {
                if (JsonSerializer.Deserialize<MemorySnapshot>(File.ReadAllText(file), Json) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fiche abîmée : elle est simplement ignorée.
            }
        }

        return snapshots;
    }

    private IEnumerable<string> Files() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "fiche-*.json").OrderByDescending(f => f, StringComparer.Ordinal)
        : [];
}
