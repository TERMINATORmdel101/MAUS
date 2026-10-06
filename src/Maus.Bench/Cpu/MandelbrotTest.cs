using System.Numerics;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Cpu;

/// <summary>
/// Test « Vectoriel » du processeur : plongée dans l'ensemble de Mandelbrot en double précision, calculée par paquets
/// de nombres (Vector&lt;double&gt; : 2, 4 ou 8 calculs à la fois selon les instructions SSE2, AVX2 ou AVX-512 du
/// processeur) sur tous les cœurs. Les processeurs aux unités vectorielles larges s'y distinguent nettement.
/// </summary>
internal sealed class MandelbrotTest : CpuBenchTest
{
    // Point de la « vallée des hippocampes », souvent utilisé pour les plongées profondes (coordonnées mathématiques).
    private const double CenterX = -0.743643887037158704752191506114774;
    private const double CenterY = 0.131825904205311970493132056385139;
    private readonly float[] _smooth;

    public MandelbrotTest()
        : base(1920, 1080)
    {
        _smooth = new float[Width * Height];
    }

    public override string Id => "cpu-vector";

    public override string Title => T("Calcul vectoriel en double précision");

    public override string Subtitle => T("Plongée dans l'ensemble de Mandelbrot : {0} calculs à la fois par cœur", Vector<double>.Count);

    public override string Capability => "simd";

    public override string Unit => T("milliards d'itérations par seconde");

    public override double UnitScale => 1e9;

    public override double Duration => 35;

    public override void Run(CancellationToken token)
    {
        Threads = Environment.ProcessorCount;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Threads };
        while (!token.IsCancellationRequested)
        {
            // Zoom continu : ×1,5 par seconde de calcul, plus d'itérations à mesure que les détails s'affinent.
            var zoom = Math.Pow(1.5, watch.Elapsed.TotalSeconds);
            var scale = 3.2 / zoom / Width;
            var maxIterations = (int)Math.Min(20000, 600 + (250 * Math.Log2(zoom + 1)));
            var rowsDone = 0L;
            try
            {
                Parallel.For(0, Height, options, (y, state) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        state.Stop();
                        return;
                    }

                    AddWork(RenderRow(y, scale, maxIterations));
                    Interlocked.Increment(ref rowsDone);
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (Interlocked.Read(ref rowsDone) == Height)
            {
                Colorize(watch.Elapsed.TotalSeconds);
            }
        }
    }

    private long RenderRow(int y, double scale, int maxIterations)
    {
        var lanes = Vector<double>.Count;
        Span<double> xs = stackalloc double[lanes];
        Span<double> counts = stackalloc double[lanes];
        Span<double> radii = stackalloc double[lanes];
        var ci = new Vector<double>(CenterY + ((y - (Height / 2.0)) * scale));
        var four = new Vector<double>(4.0);
        var one = Vector<double>.One;
        long work = 0;
        for (var x = 0; x < Width; x += lanes)
        {
            for (var l = 0; l < lanes; l++)
            {
                xs[l] = CenterX + ((x + l - (Width / 2.0)) * scale);
            }

            var cr = new Vector<double>(xs);
            var zr = Vector<double>.Zero;
            var zi = Vector<double>.Zero;
            var iterations = Vector<double>.Zero;
            var r2 = Vector<double>.Zero;
            for (var i = 0; i < maxIterations; i++)
            {
                var zr2 = zr * zr;
                var zi2 = zi * zi;
                r2 = zr2 + zi2;
                var active = Vector.LessThanOrEqual(r2, four);
                if (Vector.EqualsAll(active, Vector<long>.Zero))
                {
                    break;
                }

                // Seules les voies encore actives avancent (masque) : z = z² + c.
                var newZi = (2.0 * zr * zi) + ci;
                var newZr = zr2 - zi2 + cr;
                zr = Vector.ConditionalSelect(active, newZr, zr);
                zi = Vector.ConditionalSelect(active, newZi, zi);
                iterations += Vector.ConditionalSelect(active, one, Vector<double>.Zero);
                work += lanes;
            }

            iterations.CopyTo(counts);
            r2.CopyTo(radii);
            for (var l = 0; l < lanes && x + l < Width; l++)
            {
                var n = counts[l];
                // Couleur continue : itérations corrigées par le logarithme du rayon d'échappement.
                _smooth[(y * Width) + x + l] = n >= maxIterations ? -1f : (float)(n + 1 - Math.Log2(Math.Max(1.0, Math.Log(Math.Max(radii[l], 1.0001)) * 0.5)));
            }
        }

        return work;
    }

    private void Colorize(double seconds)
    {
        var shift = (float)(seconds * 0.02);
        for (var i = 0; i < _smooth.Length; i++)
        {
            var v = _smooth[i];
            Vector3 color;
            if (v < 0)
            {
                color = new Vector3(0.01f, 0.01f, 0.02f);
            }
            else
            {
                // Palette cosinus aux tons de MAUS (bleu, rose, sable) ; t avance avec le nombre d'itérations.
                var t = (MathF.Log(v + 1f) * 0.35f) + shift;
                color = new Vector3(
                    0.5f + (0.5f * MathF.Cos(MathF.Tau * (t + 0.00f))),
                    0.5f + (0.5f * MathF.Cos(MathF.Tau * (t + 0.15f))),
                    0.5f + (0.5f * MathF.Cos(MathF.Tau * (t + 0.35f))));
                color *= color * 1.4f;
            }

            BitConverter.TryWriteBytes(Image.AsSpan(i * 4, 4), ToPixel(color));
        }
    }
}
