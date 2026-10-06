using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;

namespace Maus.Bench.Scenes;

/// <summary>Matières des pièces du char (lues par battle.hlsl, champ « matière » des sommets).</summary>
internal static class TankMaterial
{
    public const float Paint = 0f;
    public const float Track = 1f;
    public const float Wheel = 2f;
    public const float Steel = 3f;
    public const float Rubber = 4f;
}

/// <summary>Maillage prêt à dessiner : sommets de 28 octets (position, normale, matière) et indices 32 bits.</summary>
internal sealed class Mesh(IBuffer vertices, IBuffer indices, int indexCount) : IDisposable
{
    public const int Stride = 28;

    public int IndexCount { get; } = indexCount;

    public static Mesh Create(IGpuDevice device, string name, (float[] Vertices, uint[] Indices) data) => new(
        device.CreateBuffer(new BufferDesc(data.Vertices.Length * 4, BufferUsage.Vertex, Stride, name), MemoryMarshal.AsBytes(data.Vertices.AsSpan())),
        device.CreateBuffer(new BufferDesc(data.Indices.Length * 4, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(data.Indices.AsSpan())),
        data.Indices.Length);

    public void Bind(ICommandList cmd)
    {
        cmd.SetVertexBuffer(0, vertices, Stride);
        cmd.SetIndexBuffer(indices);
    }

    public void Dispose()
    {
        vertices.Dispose();
        indices.Dispose();
    }
}

/// <summary>
/// Char d'assaut modélisé pour MAUS : caisse à glacis, vraies chenilles (maillons et crampons posés le long de la boucle
/// de roulement), barbotin denté, roue folle, six galets et trois rouleaux par côté, jupes, garde-boue, tourelle avec
/// tourelleau, lance-pots fumigènes, antenne, canon à évacuateur de fumée et frein de bouche.
/// </summary>
internal static class TankMeshes
{
    private const float TrackZ = 1.91f;
    private const float TrackHalfWidth = 0.31f;

    public static (float[] Vertices, uint[] Indices) Hull()
    {
        var b = new MeshBuilder();

        // Caisse : glacis avant incliné, plage arrière, plaques de côté au-dessus des chenilles.
        b.Box(new Vector3(0.05f, 1.15f, 0f), new Vector3(3.0f, 0.5f, 1.5f), TankMaterial.Paint, frontSlope: 0.75f);
        b.Box(new Vector3(-0.2f, 1.36f, 1.88f), new Vector3(3.05f, 0.04f, 0.42f), TankMaterial.Paint);
        b.Box(new Vector3(-0.2f, 1.36f, -1.88f), new Vector3(3.05f, 0.04f, 0.42f), TankMaterial.Paint);
        b.Box(new Vector3(-0.1f, 1.07f, 2.25f), new Vector3(2.75f, 0.26f, 0.025f), TankMaterial.Paint);
        b.Box(new Vector3(-0.1f, 1.07f, -2.25f), new Vector3(2.75f, 0.26f, 0.025f), TankMaterial.Paint);

        // Plage arrière : grilles du moteur, caisses de rangement, échappements.
        b.Box(new Vector3(-2.2f, 1.68f, 0f), new Vector3(0.75f, 0.03f, 1.1f), TankMaterial.Steel);
        b.Box(new Vector3(-2.95f, 1.3f, 0.95f), new Vector3(0.12f, 0.2f, 0.35f), TankMaterial.Paint);
        b.Box(new Vector3(-2.95f, 1.3f, -0.95f), new Vector3(0.12f, 0.2f, 0.35f), TankMaterial.Paint);
        b.Cylinder(new Vector3(-3.15f, 1.15f, 0.55f), 0.09f, 0.12f, Vector3.UnitX, 10, TankMaterial.Steel);
        b.Cylinder(new Vector3(-3.15f, 1.15f, -0.55f), 0.09f, 0.12f, Vector3.UnitX, 10, TankMaterial.Steel);
        // Phares et crochets de remorquage à l'avant.
        b.Cylinder(new Vector3(2.75f, 1.55f, 1.1f), 0.1f, 0.06f, Vector3.UnitX, 10, TankMaterial.Steel);
        b.Cylinder(new Vector3(2.75f, 1.55f, -1.1f), 0.1f, 0.06f, Vector3.UnitX, 10, TankMaterial.Steel);
        b.Box(new Vector3(3.02f, 0.85f, 0.7f), new Vector3(0.08f, 0.06f, 0.08f), TankMaterial.Steel);
        b.Box(new Vector3(3.02f, 0.85f, -0.7f), new Vector3(0.08f, 0.06f, 0.08f), TankMaterial.Steel);

        foreach (var side in new[] { 1f, -1f })
        {
            AddRunningGear(b, side);
        }

        return b.Build();
    }

    public static (float[] Vertices, uint[] Indices) Turret()
    {
        var b = new MeshBuilder();
        b.Box(new Vector3(-0.25f, 2.0f, 0f), new Vector3(1.55f, 0.34f, 1.22f), TankMaterial.Paint, frontSlope: 0.55f);
        b.Box(new Vector3(-1.95f, 1.98f, 0f), new Vector3(0.32f, 0.26f, 1.05f), TankMaterial.Paint);

        // Tourelleau du chef de char avec épiscopes, trappe du tireur.
        b.Cylinder(new Vector3(-0.65f, 2.47f, 0.48f), 0.36f, 0.14f, Vector3.UnitY, 16, TankMaterial.Paint);
        b.Cylinder(new Vector3(-0.65f, 2.64f, 0.48f), 0.3f, 0.04f, Vector3.UnitY, 16, TankMaterial.Steel);
        b.Cylinder(new Vector3(-0.4f, 2.37f, -0.5f), 0.27f, 0.03f, Vector3.UnitY, 14, TankMaterial.Steel);

        // Lance-pots fumigènes de part et d'autre, antenne à l'arrière.
        foreach (var side in new[] { 1f, -1f })
        {
            for (var k = 0; k < 3; k++)
            {
                b.Cylinder(new Vector3(0.55f + (k * 0.16f), 2.18f, side * 1.3f), 0.06f, 0.12f, Vector3.Normalize(new Vector3(0.5f, 0.6f, side)), 8, TankMaterial.Steel);
            }
        }

        b.Cylinder(new Vector3(-1.7f, 3.15f, -0.85f), 0.012f, 0.95f, Vector3.UnitY, 5, TankMaterial.Steel);

        // Canon : masque, tube, évacuateur de fumée, frein de bouche.
        b.Box(new Vector3(1.35f, 2.02f, 0f), new Vector3(0.18f, 0.24f, 0.36f), TankMaterial.Paint);
        b.Cylinder(new Vector3(3.4f, 2.06f, 0f), 0.11f, 1.95f, Vector3.UnitX, 14, TankMaterial.Paint);
        b.Cylinder(new Vector3(3.3f, 2.06f, 0f), 0.16f, 0.32f, Vector3.UnitX, 14, TankMaterial.Paint);
        b.Box(new Vector3(5.45f, 2.06f, 0f), new Vector3(0.14f, 0.12f, 0.2f), TankMaterial.Steel);
        return b.Build();
    }

    /// <summary>Éclat de blindage : plaque tordue, avec un bord arraché.</summary>
    public static (float[] Vertices, uint[] Indices) Shard()
    {
        var b = new MeshBuilder();
        b.Box(Vector3.Zero, new Vector3(0.5f, 0.12f, 0.45f), TankMaterial.Paint, frontSlope: 0.5f);
        b.Box(new Vector3(0.2f, 0.1f, 0.1f), new Vector3(0.2f, 0.1f, 0.25f), TankMaterial.Steel, frontSlope: 0.8f);
        return b.Build();
    }

    /// <summary>Train de roulement d'un côté : galets, rouleaux, barbotin, roue folle et chenille.</summary>
    private static void AddRunningGear(MeshBuilder b, float side)
    {
        var z = side * TrackZ;
        for (var w = 0; w < 6; w++)
        {
            var x = -2.25f + (w * 0.9f);
            b.Cylinder(new Vector3(x, 0.42f, z), 0.36f, 0.24f, Vector3.UnitZ, 18, TankMaterial.Wheel);
            b.Cylinder(new Vector3(x, 0.42f, z + (side * 0.25f)), 0.14f, 0.03f, Vector3.UnitZ, 10, TankMaterial.Steel);
        }

        for (var r = 0; r < 3; r++)
        {
            b.Cylinder(new Vector3(-1.6f + (r * 1.6f), 1.0f, z), 0.1f, 0.2f, Vector3.UnitZ, 10, TankMaterial.Wheel);
        }

        b.Gear(new Vector3(2.95f, 0.66f, z), 0.4f, 0.48f, 0.22f, 13, TankMaterial.Steel);
        b.Cylinder(new Vector3(-3.0f, 0.62f, z), 0.4f, 0.22f, Vector3.UnitZ, 18, TankMaterial.Wheel);

        // Boucle de roulement vue de côté : brin inférieur au sol, montée sur le barbotin, brin supérieur, descente sur la roue folle.
        var path = TrackPath();
        var length = 0f;
        for (var i = 0; i < path.Count; i++)
        {
            length += Vector2.Distance(path[i], path[(i + 1) % path.Count]);
        }

        const int links = 92;
        var spacing = length / links;
        for (var k = 0; k < links; k++)
        {
            var (position, tangent) = Sample(path, k * spacing);
            var outward = new Vector2(tangent.Y, -tangent.X);
            var center = new Vector3(position.X, position.Y, z);
            var forward = new Vector3(tangent.X, tangent.Y, 0f);
            var up = new Vector3(outward.X, outward.Y, 0f);
            // Maillon (plaque), puis crampon sur la face extérieure, puis guide central sur la face intérieure.
            b.OrientedBox(center, forward, up, new Vector3(spacing * 0.44f, 0.035f, TrackHalfWidth), TankMaterial.Track);
            b.OrientedBox(center + (up * 0.05f), forward, up, new Vector3(spacing * 0.16f, 0.025f, TrackHalfWidth * 0.95f), TankMaterial.Track);
            b.OrientedBox(center - (up * 0.06f), forward, up, new Vector3(spacing * 0.2f, 0.04f, 0.035f), TankMaterial.Steel);
        }
    }

    private static List<Vector2> TrackPath()
    {
        var points = new List<Vector2>();
        void Arc(Vector2 center, float radius, float from, float to, int steps)
        {
            for (var i = 0; i <= steps; i++)
            {
                var a = from + ((to - from) * i / steps);
                points.Add(center + (new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius));
            }
        }

        // Sens trigonométrique vu du côté droit : avant vers +x.
        points.Add(new Vector2(-2.55f, 0.03f));
        points.Add(new Vector2(2.55f, 0.03f));
        Arc(new Vector2(2.95f, 0.66f), 0.56f, -MathF.PI * 0.62f, MathF.PI * 0.5f, 10);
        points.Add(new Vector2(-3.0f, 1.18f));
        Arc(new Vector2(-3.0f, 0.62f), 0.56f, MathF.PI * 0.5f, MathF.PI * 1.38f, 10);
        return points;
    }

    private static (Vector2 Position, Vector2 Tangent) Sample(List<Vector2> path, float distance)
    {
        for (var i = 0; ; i = (i + 1) % path.Count)
        {
            var a = path[i];
            var c = path[(i + 1) % path.Count];
            var segment = Vector2.Distance(a, c);
            if (distance <= segment || segment <= 0f)
            {
                var t = segment > 0f ? distance / segment : 0f;
                return (Vector2.Lerp(a, c, t), segment > 0f ? Vector2.Normalize(c - a) : Vector2.UnitX);
            }

            distance -= segment;
        }
    }
}

/// <summary>Assemble pavés, pavés orientés, cylindres et roues dentées en un maillage à normales par face.</summary>
internal sealed class MeshBuilder
{
    private readonly List<float> _vertices = [];
    private readonly List<uint> _indices = [];

    public void Box(Vector3 center, Vector3 half, float material, float frontSlope = 0f)
    {
        var c = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var sx = (i & 1) == 0 ? -1f : 1f;
            var sy = (i & 2) == 0 ? -1f : 1f;
            var sz = (i & 4) == 0 ? -1f : 1f;
            var p = new Vector3(sx * half.X, sy * half.Y, sz * half.Z);
            if (sx > 0 && sy > 0)
            {
                // Arête avant-haute reculée : glacis incliné.
                p.X -= half.X * frontSlope * 0.5f;
            }

            c[i] = center + p;
        }

        Faces(c, material);
    }

    /// <summary>Pavé tourné : axe avant, axe haut (le troisième s'en déduit), demi-tailles le long de ces axes.</summary>
    public void OrientedBox(Vector3 center, Vector3 forward, Vector3 up, Vector3 half, float material)
    {
        var f = Vector3.Normalize(forward);
        var u = Vector3.Normalize(up);
        var s = Vector3.Cross(f, u);
        var c = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var sx = (i & 1) == 0 ? -1f : 1f;
            var sy = (i & 2) == 0 ? -1f : 1f;
            var sz = (i & 4) == 0 ? -1f : 1f;
            c[i] = center + (f * sx * half.X) + (u * sy * half.Y) + (s * sz * half.Z);
        }

        Faces(c, material);
    }

    public void Cylinder(Vector3 center, float radius, float halfLength, Vector3 axis, int segments, float material) =>
        Gear(center, radius, radius, halfLength, segments, material, axis);

    /// <summary>Cylindre dont le bord alterne entre deux rayons (dents d'un barbotin) ; mêmes rayons = cylindre lisse.</summary>
    public void Gear(Vector3 center, float inner, float outer, float halfLength, int teeth, float material, Vector3? axisOrNull = null)
    {
        var axis = Vector3.Normalize(axisOrNull ?? Vector3.UnitZ);
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY, axis));
        var v = Vector3.Cross(axis, u);
        var toothed = MathF.Abs(outer - inner) > 1e-4f;
        var segments = toothed ? teeth * 2 : teeth;
        Vector3 Rim(int s)
        {
            var angle = s * MathF.Tau / segments;
            var radius = toothed && (s & 1) == 1 ? inner : outer;
            return ((u * MathF.Cos(angle)) + (v * MathF.Sin(angle))) * radius;
        }

        var top = center + (axis * halfLength);
        var bottom = center - (axis * halfLength);
        for (var s = 0; s < segments; s++)
        {
            var r0 = Rim(s);
            var r1 = Rim(s + 1);
            Quad(bottom + r0, top + r0, top + r1, bottom + r1, material);
            Triangle(top, top + r1, top + r0, material);
            Triangle(bottom, bottom + r0, bottom + r1, material);
        }
    }

    public (float[] Vertices, uint[] Indices) Build() => ([.. _vertices], [.. _indices]);

    private void Faces(Vector3[] c, float material)
    {
        Quad(c[0], c[2], c[3], c[1], material);
        Quad(c[4], c[5], c[7], c[6], material);
        Quad(c[0], c[4], c[6], c[2], material);
        Quad(c[1], c[3], c[7], c[5], material);
        Quad(c[0], c[1], c[5], c[4], material);
        Quad(c[2], c[6], c[7], c[3], material);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float material)
    {
        Triangle(a, b, c, material);
        Triangle(a, c, d, material);
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, float material)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a) + new Vector3(1e-9f));
        foreach (var p in new[] { a, b, c })
        {
            _indices.Add((uint)(_vertices.Count / 7));
            _vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, material]);
        }
    }
}
