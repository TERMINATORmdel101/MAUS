using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Maus.Core.Workshop;

/// <summary>Cœurs physiques lus par GetLogicalProcessorInformationEx, épinglage par SetThreadGroupAffinity.</summary>
public sealed unsafe partial class WindowsCoreTopology : ICoreTopology
{
    private const int RelationProcessorCore = 0;

    public IReadOnlyList<CpuCore> Cores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, start, ref length))
            {
                return [];
            }

            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX : Relationship (4), Size (4), puis PROCESSOR_RELATIONSHIP :
            // Flags (1), EfficiencyClass (1), Reserved (20), GroupCount (2), GROUP_AFFINITY[] à l'octet 32 (Mask 8, Group 2).
            var cores = new List<CpuCore>();
            for (uint offset = 0; offset + 48 <= length;)
            {
                var entry = start + offset;
                var size = *(uint*)(entry + 4);
                if (*(int*)entry == RelationProcessorCore && size >= 48)
                {
                    var mask = *(ulong*)(entry + 32);
                    var group = *(ushort*)(entry + 40);
                    if (mask != 0)
                    {
                        cores.Add(new CpuCore(cores.Count, group, 1UL << BitOperations.TrailingZeroCount(mask), entry[9]));
                    }
                }

                if (size == 0)
                {
                    break;
                }

                offset += size;
            }

            return cores;
        }
    }

    public bool PinCurrentThread(CpuCore core)
    {
        var affinity = stackalloc ulong[2];
        affinity[0] = core.Mask;
        affinity[1] = core.Group;
        return SetThreadGroupAffinity(GetCurrentThread(), affinity, null);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadGroupAffinity(nint thread, ulong* affinity, ulong* previous);
}

/// <summary>
/// Trace du test cœur par cœur dans %LOCALAPPDATA%\MAUS, écrite directement sur le disque (sans cache) pour survivre à un gel
/// ou à une coupure : si elle est encore là au lancement suivant, le test s'est interrompu brutalement sur ce cœur.
/// </summary>
/// <remarks>
/// Écriture « tout ou rien » (<see cref="Platform.AtomicFile.WriteAllText"/>) : la nouvelle trace est écrite sans cache dans un
/// fichier temporaire, puis remplace l'ancienne par MoveFileEx avec MOVEFILE_WRITE_THROUGH, qui ne rend la main qu'une fois le
/// remplacement fait sur le disque. <see cref="Save"/> est appelé avant de lancer la charge sur le cœur suivant : un gel pendant
/// l'écriture laisse l'ancienne trace, qui désigne alors le dernier cœur réellement testé ; un gel après laisse la nouvelle.
/// Jamais un fichier vide (un fichier vidé puis réécrit en laissait un si le PC gelait entre les deux).
/// </remarks>
public sealed class FileCoreTestCheckpoint(string path) : ICoreTestCheckpoint
{
    public static FileCoreTestCheckpoint CreateDefault() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "core-test.json"));

    public CoreTestCheckpoint? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CoreTestCheckpoint>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(CoreTestCheckpoint state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Platform.AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Trace non mise à jour : l'ancienne désignerait un autre cœur que celui qui va être testé. Mieux vaut aucune
            // explication au lancement suivant qu'une fausse.
            Clear();
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
