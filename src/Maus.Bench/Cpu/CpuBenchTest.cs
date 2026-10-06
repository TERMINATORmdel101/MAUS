using System.Numerics;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Cpu;

/// <summary>
/// Un test du processeur : un calcul qui dessine progressivement une image (affichée en direct par la carte graphique,
/// qui reste presque au repos) et compte son travail. Score = travail par seconde pendant la mesure.
/// </summary>
internal abstract class CpuBenchTest
{
    private long _work;

    protected CpuBenchTest(int width, int height)
    {
        Width = width;
        Height = height;
        Image = new byte[width * height * 4];
    }

    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    /// <summary>Capacité mesurée (« multicore », « singlecore », « simd »).</summary>
    public abstract string Capability { get; }

    /// <summary>Unité affichée de la mesure, en clair (déjà divisée par <see cref="UnitScale"/>).</summary>
    public abstract string Unit { get; }

    /// <summary>Diviseur entre le travail compté et l'unité affichée (1e6 pour des millions…).</summary>
    public abstract double UnitScale { get; }

    public abstract double Duration { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Image en cours (RVBA 8 bits, déjà en sRVB), lue par l'affichage sans verrou : un pixel à moitié écrit ne gêne pas.</summary>
    public byte[] Image { get; }

    /// <summary>Nombre de fils de calcul utilisés.</summary>
    public int Threads { get; protected set; } = 1;

    public long Work => Interlocked.Read(ref _work);

    /// <summary>Calcule jusqu'à l'annulation (bloquant : à lancer sur une tâche à part).</summary>
    public abstract void Run(CancellationToken token);

    protected void AddWork(long amount) => Interlocked.Add(ref _work, amount);

    /// <summary>Lumière réelle vers sRVB 8 bits, avec la même courbe filmique que les scènes de la carte graphique.</summary>
    protected static uint ToPixel(Vector3 color)
    {
        static float Curve(float x)
        {
            x = MathF.Max(x, 0f);
            var y = x * ((2.51f * x) + 0.03f) / ((x * ((2.43f * x) + 0.59f)) + 0.14f);
            y = Math.Clamp(y, 0f, 1f);
            return y <= 0.0031308f ? y * 12.92f : (1.055f * MathF.Pow(y, 1f / 2.4f)) - 0.055f;
        }

        var r = (uint)(Curve(color.X) * 255f + 0.5f);
        var g = (uint)(Curve(color.Y) * 255f + 0.5f);
        var b = (uint)(Curve(color.Z) * 255f + 0.5f);
        return r | (g << 8) | (b << 16) | 0xFF000000u;
    }

    internal static string MillionsOfRays => T("millions de rayons par seconde");
}

/// <summary>Générateur pseudo-aléatoire rapide (xorshift 32 bits), un par fil de calcul.</summary>
internal struct FastRandom(uint seed)
{
    private uint _state = seed == 0 ? 0x9E3779B9u : seed;

    public float NextFloat()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (_state >> 8) * (1f / 16777216f);
    }

    public Vector3 InUnitSphere()
    {
        while (true)
        {
            var p = new Vector3((NextFloat() * 2f) - 1f, (NextFloat() * 2f) - 1f, (NextFloat() * 2f) - 1f);
            if (p.LengthSquared() < 1f)
            {
                return p;
            }
        }
    }

    public Vector3 UnitVector() => Vector3.Normalize(InUnitSphere() + new Vector3(1e-6f));

    public Vector2 InUnitDisk()
    {
        while (true)
        {
            var p = new Vector2((NextFloat() * 2f) - 1f, (NextFloat() * 2f) - 1f);
            if (p.LengthSquared() < 1f)
            {
                return p;
            }
        }
    }
}
