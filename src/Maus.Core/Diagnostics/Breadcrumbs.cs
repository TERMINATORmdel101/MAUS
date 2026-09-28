using System.Globalization;

namespace Maus.Core.Diagnostics;

/// <summary>
/// Dernières actions de MAUS (« miettes de pain »), gardées en mémoire pour le diagnostic : si l'interface se fige, le
/// garde de l'interface les écrit dans un journal local. Rien de personnel, rien n'est envoyé.
/// </summary>
public static class Breadcrumbs
{
    private const int Capacity = 60;

    private static readonly Lock Gate = new();
    private static readonly Queue<string> Items = new();

    public static void Add(string step)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  [fil {Environment.CurrentManagedThreadId}] {step}";
        lock (Gate)
        {
            Items.Enqueue(line);
            while (Items.Count > Capacity)
            {
                Items.Dequeue();
            }
        }
    }

    /// <summary>Les dernières actions, de la plus ancienne à la plus récente.</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
        {
            return [.. Items];
        }
    }
}
