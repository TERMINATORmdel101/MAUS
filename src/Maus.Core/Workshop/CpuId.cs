using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Maus.Core.Workshop;

/// <summary>Réponse brute de l'instruction <c>CPUID</c> pour une feuille et une sous-feuille.</summary>
public readonly record struct CpuIdRegisters(uint Eax, uint Ebx, uint Ecx, uint Edx);

/// <summary>Accès à l'instruction <c>CPUID</c>, exécutable en mode utilisateur (aucun pilote).</summary>
public interface ICpuIdSource
{
    bool IsSupported { get; }

    CpuIdRegisters Query(uint leaf, uint subleaf = 0);
}

public sealed class X86CpuIdSource : ICpuIdSource
{
    public bool IsSupported => X86Base.IsSupported;

    public CpuIdRegisters Query(uint leaf, uint subleaf = 0)
    {
        var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subleaf));
        return new CpuIdRegisters(unchecked((uint)eax), unchecked((uint)ebx), unchecked((uint)ecx), unchecked((uint)edx));
    }
}

/// <summary>Ce que <c>CPUID</c> dit du processeur.</summary>
public sealed record CpuIdInfo(
    string Vendor,
    string? Brand,
    int Family,
    int Model,
    int Stepping,
    IReadOnlyList<string> InstructionSets,
    bool VirtualizationCapable,
    bool Hybrid,
    bool RunningInVirtualMachine)
{
    public bool IsIntel => Vendor == "GenuineIntel";

    public bool IsAmd => Vendor == "AuthenticAMD";

    /// <summary>Famille, modèle et révision en hexadécimal, comme dans les fiches des fabricants (par exemple « 6-B7-1 »).</summary>
    public string Signature => $"{Family:X}-{Model:X}-{Stepping:X}";
}

public static class CpuIdParser
{
    public static CpuIdInfo? Read(ICpuIdSource source)
    {
        if (!source.IsSupported)
        {
            return null;
        }

        var leaf0 = source.Query(0);
        var maxLeaf = leaf0.Eax;
        var vendor = Ascii(leaf0.Ebx, leaf0.Edx, leaf0.Ecx);

        var leaf1 = maxLeaf >= 1 ? source.Query(1) : default;
        var baseFamily = (int)((leaf1.Eax >> 8) & 0xF);
        var baseModel = (int)((leaf1.Eax >> 4) & 0xF);
        var family = baseFamily == 0xF ? baseFamily + (int)((leaf1.Eax >> 20) & 0xFF) : baseFamily;
        var model = baseFamily is 0x6 or 0xF ? (int)(((leaf1.Eax >> 16) & 0xF) << 4) + baseModel : baseModel;
        var stepping = (int)(leaf1.Eax & 0xF);

        var leaf7 = maxLeaf >= 7 ? source.Query(7, 0) : default;
        var maxExtended = source.Query(0x8000_0000).Eax;
        var ext1 = maxExtended >= 0x8000_0001 ? source.Query(0x8000_0001) : default;

        string? brand = null;
        if (maxExtended >= 0x8000_0004)
        {
            var parts = new[] { source.Query(0x8000_0002), source.Query(0x8000_0003), source.Query(0x8000_0004) };
            brand = string.Concat(parts.Select(p => Ascii(p.Eax, p.Ebx, p.Ecx, p.Edx))).Trim('\0', ' ');
            brand = string.Join(' ', brand.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        var sets = new List<string>();
        void Add(bool present, string name)
        {
            if (present)
            {
                sets.Add(name);
            }
        }

        Add(Bit(leaf1.Edx, 26), "SSE2");
        Add(Bit(leaf1.Ecx, 0), "SSE3");
        Add(Bit(leaf1.Ecx, 9), "SSSE3");
        Add(Bit(leaf1.Ecx, 19), "SSE4.1");
        Add(Bit(leaf1.Ecx, 20), "SSE4.2");
        Add(Bit(leaf1.Ecx, 25), "AES");
        Add(Bit(leaf1.Ecx, 28), "AVX");
        Add(Bit(leaf1.Ecx, 12), "FMA3");
        Add(Bit(leaf7.Ebx, 5), "AVX2");
        Add(Bit(leaf7.Ebx, 8), "BMI2");
        Add(Bit(leaf7.Ebx, 16), "AVX-512");
        Add(Bit(leaf7.Ebx, 29), "SHA");

        var virtualization = Bit(leaf1.Ecx, 5) || Bit(ext1.Ecx, 2);
        return new CpuIdInfo(vendor, string.IsNullOrWhiteSpace(brand) ? null : brand, family, model, stepping, sets,
            virtualization, Hybrid: Bit(leaf7.Edx, 15), RunningInVirtualMachine: Bit(leaf1.Ecx, 31));
    }

    private static bool Bit(uint value, int bit) => ((value >> bit) & 1) == 1;

    private static string Ascii(params uint[] registers)
    {
        var bytes = registers.SelectMany(BitConverter.GetBytes).ToArray();
        return Encoding.ASCII.GetString(bytes);
    }
}
