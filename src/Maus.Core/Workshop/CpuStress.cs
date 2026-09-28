using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Type de charge des tests processeur, choisi par l'utilisateur.</summary>
public enum CpuStressMode
{
    /// <summary>Mélange : hachage SHA-256, matrices, crible de nombres premiers (calculs entiers, vectoriels et accès mémoire).</summary>
    Automatic,

    /// <summary>Calculs entiers 64 bits seulement (multiplications, rotations, ou exclusifs), sans instruction vectorielle.</summary>
    Scalar,

    /// <summary>Calculs vectoriels 128 bits (SSE2, présent sur tous les processeurs 64 bits).</summary>
    Sse,

    /// <summary>Calculs vectoriels 256 bits (AVX), multiplications et additions séparées.</summary>
    Avx,

    /// <summary>Calculs vectoriels 256 bits en multiplication-addition fusionnée (AVX2 et FMA).</summary>
    Fma,

    /// <summary>Calculs vectoriels 512 bits (AVX-512), seulement sur les processeurs qui l'annoncent.</summary>
    Avx512,

    /// <summary>Écriture puis relecture d'un tableau de 8 Mo par fil : sollicite les caches et la mémoire vive.</summary>
    Memory,
}

/// <summary>
/// Charges des tests processeur. Chaque tour fait les mêmes opérations dans le même ordre (IEEE 754 pour les flottants),
/// donc donne toujours le même résultat sur un processeur sain ; il est comparé à une référence calculée au début : un écart
/// trahit une instabilité. Les charges vectorielles gardent leurs valeurs dans les registres, avec de nombreuses chaînes
/// indépendantes pour occuper toutes les unités de calcul. Les jeux d'instructions sont ceux que le processeur annonce
/// (CPUID, lu par .NET) : un mode que le processeur ne connaît pas n'est pas proposé.
/// </summary>
public static class CpuStress
{
    private const int Iterations = 40_000;
    private const int MemoryDoubles = 1 << 20;

    /// <summary>Le processeur sait faire ce type de charge.</summary>
    public static bool Supported(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Avx => Avx.IsSupported,
        CpuStressMode.Fma => Fma.IsSupported,
        CpuStressMode.Avx512 => Avx512F.IsSupported,
        _ => true,
    };

    /// <summary>Types de charge proposés sur ce processeur, du plus léger au plus lourd (le mélange d'abord).</summary>
    public static IReadOnlyList<CpuStressMode> Available() => Enum.GetValues<CpuStressMode>().Where(Supported).ToList();

    /// <summary>Instructions réellement utilisées par un mode, pour l'affichage et le rapport.</summary>
    public static string Instructions(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Scalar => T("calculs entiers 64 bits, sans vecteurs"),
        CpuStressMode.Sse => T("SSE2 (128 bits)"),
        CpuStressMode.Avx => T("AVX (256 bits)"),
        CpuStressMode.Fma => T("AVX2 et FMA (256 bits)"),
        CpuStressMode.Avx512 => T("AVX-512 (512 bits)"),
        CpuStressMode.Memory => Vector256.IsHardwareAccelerated ? T("vecteurs 256 bits sur un tableau de 8 Mo par fil (caches et mémoire vive)") : T("SSE2 sur un tableau de 8 Mo par fil (caches et mémoire vive)"),
        _ => T("mélange : SHA-256, matrices, nombres premiers"),
    };

    /// <summary>Calcul d'un tour pour ce mode (le mélange du test processeur pour <see cref="CpuStressMode.Automatic"/>).</summary>
    public static Func<ulong> Kernel(CpuStressMode mode) => mode == CpuStressMode.Automatic ? () => CpuTest.Round(0) : () => Round(mode);

    /// <summary>Un tour de charge déterministe pour ce mode ; un mode non pris en charge retombe sur SSE2.</summary>
    internal static ulong Round(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Automatic => CpuTest.Round(0),
        CpuStressMode.Scalar => RoundInteger(),
        CpuStressMode.Avx when Avx.IsSupported => RoundAvx(),
        CpuStressMode.Fma when Fma.IsSupported => RoundFma(),
        CpuStressMode.Avx512 when Avx512F.IsSupported => Round512(),
        CpuStressMode.Memory => RoundMemory(),
        _ => RoundSse(),
    };

    // Chaque accumulateur converge vers 1 (x = x·m + a avec m = 1 − a) : valeurs bornées, jamais d'infini ni de NaN.
    private const double Multiplier = 0.999_999_9;
    private const double Addend = 0.000_000_1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundInteger()
    {
        // Six états indépendants mélangés par multiplication, rotation et ou exclusif (constantes de SplitMix64).
        ulong a = 1, b = 2, c = 3, d = 4, e = 5, f = 6;
        for (var i = 0; i < Iterations * 2; i++)
        {
            a = ulong.RotateLeft(a * 0x9E3779B97F4A7C15UL, 31) ^ 0xBF58476D1CE4E5B9UL;
            b = ulong.RotateLeft(b * 0x9E3779B97F4A7C15UL, 27) ^ 0x94D049BB133111EBUL;
            c = ulong.RotateLeft(c * 0xBF58476D1CE4E5B9UL, 29) ^ 0x9E3779B97F4A7C15UL;
            d = ulong.RotateLeft(d * 0x94D049BB133111EBUL, 33) ^ 0xBF58476D1CE4E5B9UL;
            e = ulong.RotateLeft(e * 0xBF58476D1CE4E5B9UL, 23) ^ 0x94D049BB133111EBUL;
            f = ulong.RotateLeft(f * 0x9E3779B97F4A7C15UL, 37) ^ 0xBF58476D1CE4E5B9UL;
        }

        return a ^ b ^ c ^ d ^ e ^ f;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundSse()
    {
        var m = Vector128.Create(Multiplier);
        var a = Vector128.Create(Addend);
        Vector128<double> x0 = Seed128(0), x1 = Seed128(1), x2 = Seed128(2), x3 = Seed128(3), x4 = Seed128(4), x5 = Seed128(5), x6 = Seed128(6), x7 = Seed128(7);
        for (var i = 0; i < Iterations; i++)
        {
            x0 = (x0 * m) + a;
            x1 = (x1 * m) + a;
            x2 = (x2 * m) + a;
            x3 = (x3 * m) + a;
            x4 = (x4 * m) + a;
            x5 = (x5 * m) + a;
            x6 = (x6 * m) + a;
            x7 = (x7 * m) + a;
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5) ^ Fold(x6) ^ Fold(x7);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundAvx()
    {
        var m = Vector256.Create(Multiplier);
        var a = Vector256.Create(Addend);
        Vector256<double> x0 = Seed256(0), x1 = Seed256(1), x2 = Seed256(2), x3 = Seed256(3), x4 = Seed256(4), x5 = Seed256(5), x6 = Seed256(6), x7 = Seed256(7);
        for (var i = 0; i < Iterations; i++)
        {
            x0 = Avx.Add(Avx.Multiply(x0, m), a);
            x1 = Avx.Add(Avx.Multiply(x1, m), a);
            x2 = Avx.Add(Avx.Multiply(x2, m), a);
            x3 = Avx.Add(Avx.Multiply(x3, m), a);
            x4 = Avx.Add(Avx.Multiply(x4, m), a);
            x5 = Avx.Add(Avx.Multiply(x5, m), a);
            x6 = Avx.Add(Avx.Multiply(x6, m), a);
            x7 = Avx.Add(Avx.Multiply(x7, m), a);
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5) ^ Fold(x6) ^ Fold(x7);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundFma()
    {
        var m = Vector256.Create(Multiplier);
        var a = Vector256.Create(Addend);
        Vector256<double> x0 = Seed256(0), x1 = Seed256(1), x2 = Seed256(2), x3 = Seed256(3), x4 = Seed256(4), x5 = Seed256(5);
        Vector256<double> x6 = Seed256(6), x7 = Seed256(7), x8 = Seed256(8), x9 = Seed256(9), x10 = Seed256(10), x11 = Seed256(11);
        for (var i = 0; i < Iterations; i++)
        {
            // Douze chaînes indépendantes : assez pour occuper les unités FMA malgré leur latence.
            x0 = Fma.MultiplyAdd(x0, m, a);
            x1 = Fma.MultiplyAdd(x1, m, a);
            x2 = Fma.MultiplyAdd(x2, m, a);
            x3 = Fma.MultiplyAdd(x3, m, a);
            x4 = Fma.MultiplyAdd(x4, m, a);
            x5 = Fma.MultiplyAdd(x5, m, a);
            x6 = Fma.MultiplyAdd(x6, m, a);
            x7 = Fma.MultiplyAdd(x7, m, a);
            x8 = Fma.MultiplyAdd(x8, m, a);
            x9 = Fma.MultiplyAdd(x9, m, a);
            x10 = Fma.MultiplyAdd(x10, m, a);
            x11 = Fma.MultiplyAdd(x11, m, a);
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5) ^ Fold(x6) ^ Fold(x7) ^ Fold(x8) ^ Fold(x9) ^ Fold(x10) ^ Fold(x11);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Round512()
    {
        var m = Vector512.Create(Multiplier);
        var a = Vector512.Create(Addend);
        Vector512<double> x0 = Seed512(0), x1 = Seed512(1), x2 = Seed512(2), x3 = Seed512(3), x4 = Seed512(4), x5 = Seed512(5);
        Vector512<double> x6 = Seed512(6), x7 = Seed512(7), x8 = Seed512(8), x9 = Seed512(9), x10 = Seed512(10), x11 = Seed512(11);
        for (var i = 0; i < Iterations; i++)
        {
            x0 = Avx512F.FusedMultiplyAdd(x0, m, a);
            x1 = Avx512F.FusedMultiplyAdd(x1, m, a);
            x2 = Avx512F.FusedMultiplyAdd(x2, m, a);
            x3 = Avx512F.FusedMultiplyAdd(x3, m, a);
            x4 = Avx512F.FusedMultiplyAdd(x4, m, a);
            x5 = Avx512F.FusedMultiplyAdd(x5, m, a);
            x6 = Avx512F.FusedMultiplyAdd(x6, m, a);
            x7 = Avx512F.FusedMultiplyAdd(x7, m, a);
            x8 = Avx512F.FusedMultiplyAdd(x8, m, a);
            x9 = Avx512F.FusedMultiplyAdd(x9, m, a);
            x10 = Avx512F.FusedMultiplyAdd(x10, m, a);
            x11 = Avx512F.FusedMultiplyAdd(x11, m, a);
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5) ^ Fold(x6) ^ Fold(x7) ^ Fold(x8) ^ Fold(x9) ^ Fold(x10) ^ Fold(x11);
    }

    [ThreadStatic]
    private static double[]? s_buffer;

    /// <summary>
    /// Écrit dans un tableau de 8 Mo une valeur calculée d'après la position, puis le relit en sommant par vecteurs :
    /// même résultat à chaque tour, et un trafic qui dépasse les caches quand tous les fils travaillent.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundMemory()
    {
        var buffer = s_buffer ??= GC.AllocateUninitializedArray<double>(MemoryDoubles);
        var span = buffer.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = (i & 1023) * 0.001 + 0.5;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<double> s0 = Vector256<double>.Zero, s1 = Vector256<double>.Zero;
            for (var i = 0; i + 8 <= span.Length; i += 8)
            {
                s0 += Vector256.Create<double>(span.Slice(i, 4));
                s1 += Vector256.Create<double>(span.Slice(i + 4, 4));
            }

            return Fold(s0) ^ (Fold(s1) << 3);
        }

        Vector128<double> t0 = Vector128<double>.Zero, t1 = Vector128<double>.Zero;
        for (var i = 0; i + 4 <= span.Length; i += 4)
        {
            t0 += Vector128.Create<double>(span.Slice(i, 2));
            t1 += Vector128.Create<double>(span.Slice(i + 2, 2));
        }

        return Fold(t0) ^ (Fold(t1) << 3);
    }

    private static Vector128<double> Seed128(int chain) => Vector128.Create(0.25 + chain, 0.5 + chain);

    private static Vector256<double> Seed256(int chain) => Vector256.Create(0.25 + chain, 0.5 + chain, 0.75 + chain, 1.0 + chain);

    private static Vector512<double> Seed512(int chain) => Vector512.Create(Seed256(chain), Seed256(chain + 12));

    private static ulong Fold(Vector128<double> v) => Rotate(v.GetElement(0), 0) ^ Rotate(v.GetElement(1), 17);

    private static ulong Fold(Vector256<double> v) => Fold(v.GetLower()) ^ (Fold(v.GetUpper()) << 1);

    private static ulong Fold(Vector512<double> v) => Fold(v.GetLower()) ^ (Fold(v.GetUpper()) << 2);

    private static ulong Rotate(double value, int bits) => ulong.RotateLeft(BitConverter.DoubleToUInt64Bits(value), bits);
}
