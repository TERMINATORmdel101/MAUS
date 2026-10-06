using System.Numerics;
using static Maus.Core.Localization.Texts;

namespace Maus.Bench.Cpu;

/// <summary>
/// Tests « Rendu » du processeur : lancer de rayons physique (chemins de lumière avec rebonds, verre, métal, flou de
/// profondeur, soleil échantillonné directement) sur une scène de 200 sphères rangées dans une hiérarchie de volumes
/// englobants. Sur tous les cœurs, puis sur un seul : l'écart entre les deux montre ce que valent les cœurs en plus.
/// Code écrit pour MAUS.
/// </summary>
internal sealed class PathTracerTest : CpuBenchTest
{
    private const int TileSize = 32;
    private const int MaxDepth = 6;
    private readonly bool _singleCore;
    private readonly PathScene _scene = PathScene.Create();
    private readonly Vector3[] _accumulation;
    private readonly int[] _samples;

    // Tous les cœurs : image en 1080p natif ; un seul cœur : 960×540 (sinon l'image n'aurait pas le temps de se former).
    public PathTracerTest(bool singleCore)
        : base(singleCore ? 960 : 1920, singleCore ? 540 : 1080)
    {
        _singleCore = singleCore;
        _accumulation = new Vector3[Width * Height];
        _samples = new int[Width * Height];
    }

    public override string Id => _singleCore ? "cpu-single" : "cpu-render";

    public override string Title => _singleCore ? T("Rendu sur un seul cœur") : T("Rendu sur tous les cœurs");

    public override string Subtitle => _singleCore
        ? T("Le même calcul sur un seul cœur : la vitesse d'un cœur, qui compte dans la plupart des jeux")
        : T("Lancer de rayons physique : chaque cœur calcule des millions de trajets de lumière par seconde");

    public override string Capability => _singleCore ? "singlecore" : "multicore";

    public override string Unit => MillionsOfRays;

    public override double UnitScale => 1e6;

    public override double Duration => _singleCore ? 35 : 50;

    public override void Run(CancellationToken token)
    {
        Threads = _singleCore ? 1 : Environment.ProcessorCount;
        var tilesX = (Width + TileSize - 1) / TileSize;
        var tilesY = (Height + TileSize - 1) / TileSize;
        var tileCount = tilesX * tilesY;
        var next = -1L;
        var workers = new Thread[Threads];
        for (var w = 0; w < Threads; w++)
        {
            var seed = (uint)(w * 7919) + 17u;
            workers[w] = new Thread(() =>
            {
                var random = new FastRandom(seed);
                while (!token.IsCancellationRequested)
                {
                    // Les tuiles tournent en spirale depuis le centre : l'image se dessine comme dans un logiciel de rendu.
                    var index = Interlocked.Increment(ref next);
                    var tile = SpiralOrder(index % tileCount, tilesX, tilesY);
                    var rays = RenderTile(tile % tilesX * TileSize, tile / tilesX * TileSize, ref random);
                    AddWork(rays);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "MAUS rendu " + w,
            };
            workers[w].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }
    }

    private long RenderTile(int x0, int y0, ref FastRandom random)
    {
        var rays = 0L;
        var camera = _scene.Camera;
        var x1 = Math.Min(x0 + TileSize, Width);
        var y1 = Math.Min(y0 + TileSize, Height);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var u = (x + random.NextFloat()) / Width;
                var v = (y + random.NextFloat()) / Height;
                var (origin, direction) = camera.Ray(u, v, ref random);
                var color = Trace(origin, direction, ref random, ref rays);
                if (!float.IsFinite(color.X + color.Y + color.Z))
                {
                    color = Vector3.Zero;
                }

                var i = (y * Width) + x;
                _accumulation[i] += Vector3.Min(color, new Vector3(64f));
                var n = ++_samples[i];
                BitConverter.TryWriteBytes(Image.AsSpan(i * 4, 4), ToPixel(_accumulation[i] / n));
            }
        }

        return rays;
    }

    private Vector3 Trace(Vector3 origin, Vector3 direction, ref FastRandom random, ref long rays)
    {
        var throughput = Vector3.One;
        var radiance = Vector3.Zero;
        var specularBounce = true;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            rays++;
            if (!_scene.Intersect(origin, direction, out var hit))
            {
                radiance += throughput * _scene.Sky(direction, specularBounce);
                break;
            }

            var material = hit.Material;
            radiance += throughput * material.Emission;
            var normal = hit.Normal;
            var position = hit.Position;
            switch (material.Kind)
            {
                case MaterialKind.Diffuse:
                {
                    // Soleil échantillonné directement : beaucoup moins de bruit sur les surfaces mates.
                    var toSun = _scene.SampleSun(ref random);
                    var cosine = Vector3.Dot(normal, toSun);
                    if (cosine > 0)
                    {
                        rays++;
                        if (!_scene.Occluded(position + (normal * 1e-3f), toSun))
                        {
                            radiance += throughput * material.Albedo * _scene.SunRadiance * cosine;
                        }
                    }

                    direction = Vector3.Normalize(normal + random.UnitVector());
                    throughput *= material.Albedo;
                    specularBounce = false;
                    break;
                }

                case MaterialKind.Metal:
                {
                    var reflected = Vector3.Reflect(direction, normal);
                    direction = Vector3.Normalize(reflected + (random.InUnitSphere() * material.Fuzz));
                    if (Vector3.Dot(direction, normal) <= 0)
                    {
                        return radiance;
                    }

                    throughput *= material.Albedo;
                    specularBounce = true;
                    break;
                }

                default:
                {
                    // Verre : réfraction de Snell-Descartes, réflexion partielle selon l'approximation de Schlick.
                    var frontFace = hit.FrontFace;
                    var ratio = frontFace ? 1f / material.Ior : material.Ior;
                    var cosTheta = MathF.Min(Vector3.Dot(-direction, normal), 1f);
                    var sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - (cosTheta * cosTheta)));
                    var r0 = (1f - ratio) / (1f + ratio);
                    r0 *= r0;
                    var reflectance = r0 + ((1f - r0) * MathF.Pow(1f - cosTheta, 5f));
                    if (ratio * sinTheta > 1f || random.NextFloat() < reflectance)
                    {
                        direction = Vector3.Reflect(direction, normal);
                    }
                    else
                    {
                        var perpendicular = ratio * (direction + (cosTheta * normal));
                        var parallel = -MathF.Sqrt(MathF.Abs(1f - perpendicular.LengthSquared())) * normal;
                        direction = Vector3.Normalize(perpendicular + parallel);
                    }

                    throughput *= material.Albedo;
                    specularBounce = true;
                    break;
                }
            }

            origin = position + (direction * 1e-3f);

            // Roulette russe : les chemins sans énergie s'arrêtent tôt, sans biais.
            if (depth >= 3)
            {
                var keep = MathF.Min(MathF.Max(throughput.X, MathF.Max(throughput.Y, throughput.Z)), 0.95f);
                if (random.NextFloat() > keep)
                {
                    break;
                }

                throughput /= keep;
            }
        }

        return radiance;
    }

    private static int SpiralOrder(long index, int tilesX, int tilesY)
    {
        // Ordre des tuiles par distance au centre, calculé une fois.
        var order = s_order;
        if (order is null || order.Length != tilesX * tilesY)
        {
            var cx = (tilesX - 1) / 2f;
            var cy = (tilesY - 1) / 2f;
            order = Enumerable.Range(0, tilesX * tilesY)
                .OrderBy(t => MathF.Pow(t % tilesX - cx, 2) + MathF.Pow((t / tilesX) - cy, 2))
                .ThenBy(t => MathF.Atan2((t / tilesX) - cy, (t % tilesX) - cx))
                .ToArray();
            s_order = order;
        }

        return order[index];
    }

    private static int[]? s_order;
}

internal enum MaterialKind
{
    Diffuse,
    Metal,
    Glass,
}

internal readonly record struct PathMaterial(MaterialKind Kind, Vector3 Albedo, float Fuzz = 0f, float Ior = 1.5f)
{
    public Vector3 Emission { get; init; }
}

internal readonly record struct Sphere(Vector3 Center, float Radius, int Material);

internal struct Hit
{
    public Vector3 Position;
    public Vector3 Normal;
    public bool FrontFace;
    public PathMaterial Material;
}

/// <summary>Caméra à lentille mince (flou de profondeur).</summary>
internal readonly record struct PathCamera(Vector3 Origin, Vector3 LowerLeft, Vector3 Horizontal, Vector3 Vertical, Vector3 U, Vector3 V, float LensRadius)
{
    public static PathCamera Create(Vector3 from, Vector3 at, float fovDegrees, float aspect, float aperture, float focus)
    {
        var h = MathF.Tan(fovDegrees * MathF.PI / 360f);
        var height = 2f * h;
        var width = aspect * height;
        var w = Vector3.Normalize(from - at);
        var u = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, w));
        var v = Vector3.Cross(w, u);
        var horizontal = focus * width * u;
        var vertical = focus * height * v;
        return new PathCamera(from, from - (horizontal / 2f) - (vertical / 2f) - (focus * w), horizontal, vertical, u, v, aperture / 2f);
    }

    public (Vector3 Origin, Vector3 Direction) Ray(float s, float t, ref FastRandom random)
    {
        var disk = random.InUnitDisk() * LensRadius;
        var offset = (U * disk.X) + (V * disk.Y);
        var target = LowerLeft + (s * Horizontal) + ((1f - t) * Vertical);
        return (Origin + offset, Vector3.Normalize(target - Origin - offset));
    }
}

/// <summary>La scène : sol poli, quatre grandes sphères aux couleurs de MAUS, 200 petites sphères, ciel de fin d'après-midi.</summary>
internal sealed class PathScene
{
    private readonly Sphere[] _spheres;
    private readonly PathMaterial[] _materials;
    private readonly BvhNode[] _nodes;
    private readonly Vector3 _sunDirection = Vector3.Normalize(new Vector3(-0.55f, 0.32f, -0.45f));
    private readonly PathMaterial _floor = new(MaterialKind.Metal, new Vector3(0.32f, 0.33f, 0.36f), 0.18f);

    private PathScene(Sphere[] spheres, PathMaterial[] materials, PathCamera camera)
    {
        _spheres = spheres;
        _materials = materials;
        Camera = camera;
        var nodes = new List<BvhNode>();
        var indices = Enumerable.Range(0, spheres.Length).ToArray();
        Build(nodes, indices, 0, indices.Length);
        _nodes = [.. nodes];
        _spheres = indices.Select(i => spheres[i]).ToArray();
    }

    public PathCamera Camera { get; }

    public Vector3 SunRadiance { get; } = new Vector3(1f, 0.82f, 0.62f) * 3.2f;

    public static PathScene Create()
    {
        var materials = new List<PathMaterial>
        {
            new(MaterialKind.Glass, new Vector3(0.98f), Ior: 1.5f),
            new(MaterialKind.Metal, new Vector3(1f, 0.71f, 0.33f), 0.02f),
            new(MaterialKind.Diffuse, new Vector3(0.47f, 0.69f, 1f) * 0.85f),
            new(MaterialKind.Diffuse, new Vector3(1f, 0.55f, 0.66f) * 0.2f) { Emission = new Vector3(1f, 0.45f, 0.6f) * 4f },
        };
        var spheres = new List<Sphere>
        {
            new(new Vector3(0f, 1f, 0f), 1f, 0),
            new(new Vector3(-2.3f, 1f, 0.6f), 1f, 1),
            new(new Vector3(2.3f, 1f, 0.6f), 1f, 2),
            new(new Vector3(0.9f, 0.45f, 2.2f), 0.45f, 3),
        };

        var random = new FastRandom(20261006);
        for (var a = -11; a < 11; a++)
        {
            for (var b = -11; b < 11; b++)
            {
                if (spheres.Count > 204)
                {
                    break;
                }

                var center = new Vector3(a + (0.9f * random.NextFloat()), 0.2f, b + (0.9f * random.NextFloat()));
                if ((center - new Vector3(0, 0.2f, 0)).Length() < 1.4f || (center - new Vector3(-2.3f, 0.2f, 0.6f)).Length() < 1.3f
                    || (center - new Vector3(2.3f, 0.2f, 0.6f)).Length() < 1.3f || (center - new Vector3(0.9f, 0.2f, 2.2f)).Length() < 0.8f
                    || random.NextFloat() < 0.55f)
                {
                    continue;
                }

                var choice = random.NextFloat();
                PathMaterial material;
                if (choice < 0.55f)
                {
                    var palette = new[] { new Vector3(0.47f, 0.69f, 1f), new Vector3(1f, 0.55f, 0.66f), new Vector3(0.46f, 0.92f, 0.74f), new Vector3(1f, 0.82f, 0.5f), new Vector3(0.75f, 0.64f, 1f) };
                    material = new PathMaterial(MaterialKind.Diffuse, palette[(int)(random.NextFloat() * palette.Length) % palette.Length] * (0.55f + (0.35f * random.NextFloat())));
                }
                else if (choice < 0.85f)
                {
                    material = new PathMaterial(MaterialKind.Metal, new Vector3(0.6f + (0.4f * random.NextFloat()), 0.6f + (0.3f * random.NextFloat()), 0.6f + (0.4f * random.NextFloat())), 0.35f * random.NextFloat());
                }
                else if (choice < 0.95f)
                {
                    material = new PathMaterial(MaterialKind.Glass, new Vector3(0.97f), Ior: 1.5f);
                }
                else
                {
                    material = new PathMaterial(MaterialKind.Diffuse, new Vector3(0.1f)) { Emission = new Vector3(1f, 0.75f, 0.4f) * 6f };
                }

                materials.Add(material);
                spheres.Add(new Sphere(center, 0.2f, materials.Count - 1));
            }
        }

        var camera = PathCamera.Create(new Vector3(9.2f, 3.1f, 6.0f), new Vector3(0f, 0.55f, 0.4f), 27f, 16f / 9f, 0.12f, 10.6f);
        return new PathScene([.. spheres], [.. materials], camera);
    }

    public Vector3 Sky(Vector3 direction, bool includeSun)
    {
        // Ciel de fin d'après-midi : horizon doré, zénith bleu profond.
        var t = MathF.Max(direction.Y, 0f);
        var sky = Vector3.Lerp(new Vector3(1f, 0.62f, 0.38f) * 1.1f, new Vector3(0.12f, 0.24f, 0.62f) * 0.8f, MathF.Pow(t, 0.45f));
        var sun = MathF.Max(0f, Vector3.Dot(direction, _sunDirection));
        sky += new Vector3(1f, 0.75f, 0.5f) * (MathF.Pow(sun, 24f) * 0.6f);
        if (includeSun && sun > 0.9995f)
        {
            sky += SunRadiance * 400f;
        }

        return sky;
    }

    public Vector3 SampleSun(ref FastRandom random) => Vector3.Normalize(_sunDirection + (random.InUnitSphere() * 0.02f));

    public bool Intersect(Vector3 origin, Vector3 direction, out Hit hit)
    {
        hit = default;
        var closest = 1e30f;
        var found = false;

        // Sol : plan y = 0, motif de dalles polies.
        if (direction.Y < -1e-5f)
        {
            var t = -origin.Y / direction.Y;
            if (t > 1e-4f && t < closest)
            {
                closest = t;
                found = true;
                hit.Position = origin + (direction * t);
                hit.Normal = Vector3.UnitY;
                hit.FrontFace = true;
                var checker = ((int)MathF.Floor(hit.Position.X * 0.5f) + (int)MathF.Floor(hit.Position.Z * 0.5f)) & 1;
                hit.Material = checker == 0 ? _floor : _floor with { Albedo = new Vector3(0.12f, 0.12f, 0.14f), Fuzz = 0.08f };
            }
        }

        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var best = -1;
        while (top > 0)
        {
            var node = _nodes[stack[--top]];
            if (!node.Hit(origin, inverse, closest))
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (var i = node.First; i < node.First + node.Count; i++)
                {
                    var s = _spheres[i];
                    var oc = origin - s.Center;
                    var half = Vector3.Dot(oc, direction);
                    var c = oc.LengthSquared() - (s.Radius * s.Radius);
                    var discriminant = (half * half) - c;
                    if (discriminant < 0)
                    {
                        continue;
                    }

                    var root = MathF.Sqrt(discriminant);
                    var t = -half - root;
                    if (t < 1e-4f)
                    {
                        t = -half + root;
                    }

                    if (t > 1e-4f && t < closest)
                    {
                        closest = t;
                        best = i;
                    }
                }
            }
            else
            {
                stack[top++] = node.Left;
                stack[top++] = node.Left + 1;
            }
        }

        if (best >= 0)
        {
            var s = _spheres[best];
            hit.Position = origin + (direction * closest);
            var outward = (hit.Position - s.Center) / s.Radius;
            hit.FrontFace = Vector3.Dot(direction, outward) < 0;
            hit.Normal = hit.FrontFace ? outward : -outward;
            hit.Material = _materials[s.Material];
            return true;
        }

        return found;
    }

    public bool Occluded(Vector3 origin, Vector3 direction) => Intersect(origin, direction, out _);

    private int Build(List<BvhNode> nodes, int[] indices, int start, int end)
    {
        var index = nodes.Count;
        nodes.Add(default);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = start; i < end; i++)
        {
            var s = _spheres[indices[i]];
            min = Vector3.Min(min, s.Center - new Vector3(s.Radius));
            max = Vector3.Max(max, s.Center + new Vector3(s.Radius));
        }

        if (end - start <= 4)
        {
            nodes[index] = new BvhNode(min, max, start, end - start, -1);
            return index;
        }

        var extent = max - min;
        var axis = extent.X > extent.Y && extent.X > extent.Z ? 0 : extent.Y > extent.Z ? 1 : 2;
        Array.Sort(indices, start, end - start, Comparer<int>.Create((a, b) => Axis(_spheres[a].Center, axis).CompareTo(Axis(_spheres[b].Center, axis))));
        var middle = (start + end) / 2;

        // Les deux enfants sont rangés côte à côte : le parcours empile left et left + 1.
        var leftSlot = nodes.Count;
        nodes.Add(default);
        nodes.Add(default);
        var left = Build(nodes, indices, start, middle);
        var right = Build(nodes, indices, middle, end);
        nodes[leftSlot] = nodes[left];
        nodes[leftSlot + 1] = nodes[right];
        nodes[index] = new BvhNode(min, max, 0, 0, leftSlot);
        return index;
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private readonly record struct BvhNode(Vector3 Min, Vector3 Max, int First, int Count, int Left)
    {
        public bool Hit(Vector3 origin, Vector3 inverse, float closest)
        {
            var t0 = (Min - origin) * inverse;
            var t1 = (Max - origin) * inverse;
            var near = Vector3.Min(t0, t1);
            var far = Vector3.Max(t0, t1);
            var enter = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 1e-4f));
            var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, closest));
            return enter <= exit;
        }
    }
}
