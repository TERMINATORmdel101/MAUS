using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

/// <summary>Compteurs de performance Windows (PDH) en lecture seule. Un compteur absent sur ce PC est simplement ignoré.</summary>
internal sealed partial class PdhQuery : IDisposable
{
    private const uint FormatDouble = 0x0000_0200;
    private const uint NoCap100 = 0x0000_8000;
    private const uint MoreData = 0x8000_07D2;

    private nint _query;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, 0, out _query) != 0)
        {
            _query = 0;
        }
    }

    /// <summary>Ajoute un compteur par son nom anglais (indépendant de la langue de Windows) ; 0 si indisponible.</summary>
    public nint Add(string path) =>
        _query != 0 && PdhAddEnglishCounterW(_query, path, 0, out var counter) == 0 ? counter : 0;

    public void Collect()
    {
        if (_query != 0)
        {
            _ = PdhCollectQueryData(_query);
        }
    }

    public static double? Value(nint counter)
    {
        if (counter == 0 || PdhGetFormattedCounterValue(counter, FormatDouble | NoCap100, out _, out var value) != 0 || value.Status != 0)
        {
            return null;
        }

        return value.Double;
    }

    public static unsafe IReadOnlyList<(string Name, double Value)> Array(nint counter)
    {
        if (counter == 0)
        {
            return [];
        }

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, out _, 0);
        if (status != MoreData || size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble | NoCap100, ref size, out var count, buffer) != 0)
            {
                return [];
            }

            var items = new List<(string, double)>((int)count);
            var item = (CounterItem*)buffer;
            for (var i = 0; i < count; i++)
            {
                if (item[i].Value.Status == 0)
                {
                    items.Add((Marshal.PtrToStringUni(item[i].Name) ?? string.Empty, item[i].Value.Double));
                }
            }

            return items;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
