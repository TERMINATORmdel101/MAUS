using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Type de charge du test processeur, choisi par l'utilisateur.</summary>
public enum CpuStressMode
{
    /// <summary>Mélange : hachage SHA-256, matrices, crible de nombres premiers (calculs entiers, vectoriels et accès mémoire).</summary>
    Automatic,

    /// <summary>Calculs vectoriels 256 bits (AVX, avec FMA si le processeur l'a) : chauffe plus que le mélange.</summary>
    Avx,

    /// <summary>La charge vectorielle la plus lourde que le processeur sait faire : AVX-512 s'il l'a, sinon 256 bits à pleine cadence.</summary>
    Heavy,
}

/// <summary>
/// Charges vectorielles du test processeur. Chaque tour enchaîne des multiplications-additions sur des vecteurs gardés
/// dans les registres, avec de nombreuses chaînes indépendantes pour occuper toutes les unités de calcul ; le résultat est
/// déterministe (mêmes opérations IEEE 754, même ordre) et comparé à une référence calculée au début : un écart trahit une
/// instabilité, comme pour le test mixte. Les jeux d'instructions sont ceux que le processeur annonce (CPUID, lu par .NET).
/// </summary>
public static class CpuStress
{
    private const int Iterations = 40_000;

    /// <summary>Le mode AVX est possible (AVX annoncé par le processeur et pris en charge par Windows).</summary>
    public static bool AvxSupported => Avx.IsSupported;

    /// <summary>AVX-512 (fondation) disponible : le mode « très lourd » s'en sert.</summary>
    public static bool Avx512Supported => Avx512F.IsSupported;

    /// <summary>Instructions réellement utilisées par un mode sur ce processeur, pour l'affichage et le rapport.</summary>
    public static string Instructions(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Avx when Fma.IsSupported => T("AVX2 et FMA (256 bits)"),
        CpuStressMode.Avx when Avx.IsSupported => T("AVX (256 bits)"),
        CpuStressMode.Heavy when Avx512F.IsSupported => T("AVX-512 (512 bits)"),
        CpuStressMode.Heavy when Fma.IsSupported => T("AVX2 et FMA (256 bits, pleine cadence) : pas d'AVX-512 sur ce processeur"),
        CpuStressMode.Heavy when Avx.IsSupported => T("AVX (256 bits, pleine cadence) : pas d'AVX-512 sur ce processeur"),
        CpuStressMode.Avx or CpuStressMode.Heavy => T("calculs vectoriels de .NET : pas d'AVX sur ce processeur"),
        _ => T("mélange : SHA-256, matrices, nombres premiers"),
    };

    /// <summary>Un tour de charge vectorielle déterministe pour ce mode.</summary>
    internal static ulong Round(CpuStressMode mode) => mode switch
    {
        CpuStressMode.Heavy when Avx512F.IsSupported => Round512(),
        CpuStressMode.Heavy when Fma.IsSupported => RoundFma256(heavy: true),
        CpuStressMode.Avx when Fma.IsSupported => RoundFma256(heavy: false),
        CpuStressMode.Avx or CpuStressMode.Heavy when Avx.IsSupported => RoundAvx256(),
        _ => RoundPortable(),
    };

    // Chaque accumulateur converge vers 1 (x = x·m + a avec m = 1 − a) : valeurs bornées, jamais d'infini ni de NaN.
    private const double Multiplier = 0.999_999_9;
    private const double Addend = 0.000_000_1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundFma256(bool heavy)
    {
        var m = Vector256.Create(Multiplier);
        var a = Vector256.Create(Addend);
        Vector256<double> x0 = Seed256(0), x1 = Seed256(1), x2 = Seed256(2), x3 = Seed256(3), x4 = Seed256(4), x5 = Seed256(5);
        Vector256<double> x6 = Seed256(6), x7 = Seed256(7), x8 = Seed256(8), x9 = Seed256(9), x10 = Seed256(10), x11 = Seed256(11);
        var iterations = heavy ? Iterations * 2 : Iterations;
        for (var i = 0; i < iterations; i++)
        {
            x0 = Fma.MultiplyAdd(x0, m, a);
            x1 = Fma.MultiplyAdd(x1, m, a);
            x2 = Fma.MultiplyAdd(x2, m, a);
            x3 = Fma.MultiplyAdd(x3, m, a);
            x4 = Fma.MultiplyAdd(x4, m, a);
            x5 = Fma.MultiplyAdd(x5, m, a);
            if (heavy)
            {
                // Douze chaînes indépendantes : assez pour saturer les deux unités FMA malgré leur latence.
                x6 = Fma.MultiplyAdd(x6, m, a);
                x7 = Fma.MultiplyAdd(x7, m, a);
                x8 = Fma.MultiplyAdd(x8, m, a);
                x9 = Fma.MultiplyAdd(x9, m, a);
                x10 = Fma.MultiplyAdd(x10, m, a);
                x11 = Fma.MultiplyAdd(x11, m, a);
            }
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5) ^ Fold(x6) ^ Fold(x7) ^ Fold(x8) ^ Fold(x9) ^ Fold(x10) ^ Fold(x11);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundAvx256()
    {
        var m = Vector256.Create(Multiplier);
        var a = Vector256.Create(Addend);
        Vector256<double> x0 = Seed256(0), x1 = Seed256(1), x2 = Seed256(2), x3 = Seed256(3), x4 = Seed256(4), x5 = Seed256(5);
        for (var i = 0; i < Iterations; i++)
        {
            x0 = Avx.Add(Avx.Multiply(x0, m), a);
            x1 = Avx.Add(Avx.Multiply(x1, m), a);
            x2 = Avx.Add(Avx.Multiply(x2, m), a);
            x3 = Avx.Add(Avx.Multiply(x3, m), a);
            x4 = Avx.Add(Avx.Multiply(x4, m), a);
            x5 = Avx.Add(Avx.Multiply(x5, m), a);
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3) ^ Fold(x4) ^ Fold(x5);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Round512()
    {
        var m = Vector512.Create(Multiplier);
        var a = Vector512.Create(Addend);
        Vector512<double> x0 = Seed512(0), x1 = Seed512(1), x2 = Seed512(2), x3 = Seed512(3), x4 = Seed512(4), x5 = Seed512(5);
        Vector512<double> x6 = Seed512(6), x7 = Seed512(7), x8 = Seed512(8), x9 = Seed512(9), x10 = Seed512(10), x11 = Seed512(11);
        for (var i = 0; i < Iterations * 2; i++)
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

    /// <summary>Processeur sans AVX : même calcul avec les vecteurs portables de .NET (SSE2 au minimum).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RoundPortable()
    {
        var m = Vector128.Create(Multiplier);
        var a = Vector128.Create(Addend);
        Vector128<double> x0 = Vector128.Create(0.25, 0.5), x1 = Vector128.Create(0.75, 1.25), x2 = Vector128.Create(1.5, 1.75), x3 = Vector128.Create(2.0, 2.25);
        for (var i = 0; i < Iterations; i++)
        {
            x0 = (x0 * m) + a;
            x1 = (x1 * m) + a;
            x2 = (x2 * m) + a;
            x3 = (x3 * m) + a;
        }

        return Fold(x0) ^ Fold(x1) ^ Fold(x2) ^ Fold(x3);
    }

    private static Vector256<double> Seed256(int chain) => Vector256.Create(0.25 + chain, 0.5 + chain, 0.75 + chain, 1.0 + chain);

    private static Vector512<double> Seed512(int chain) => Vector512.Create(Seed256(chain), Seed256(chain + 12));

    private static ulong Fold(Vector128<double> v) => Rotate(v.GetElement(0), 0) ^ Rotate(v.GetElement(1), 17);

    private static ulong Fold(Vector256<double> v) => Fold(v.GetLower()) ^ (Fold(v.GetUpper()) << 1);

    private static ulong Fold(Vector512<double> v) => Fold(v.GetLower()) ^ (Fold(v.GetUpper()) << 2);

    private static ulong Rotate(double value, int bits) => ulong.RotateLeft(BitConverter.DoubleToUInt64Bits(value), bits);
}
