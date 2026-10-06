using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;

namespace Maus.Bench.Scenes;

/// <summary>
/// Matières des pièces du char (lues par battle.hlsl, champ « matière » des sommets). Une arête biseautée ajoute
/// <see cref="Edge"/> : la peinture y est usée jusqu'au métal.
/// </summary>
internal static class TankMaterial
{
    public const float Paint = 0f;
    public const float Track = 1f;
    public const float Wheel = 2f;
    public const float Steel = 3f;
    public const float Rubber = 4f;
    public const float Grille = 5f;
    public const float Wood = 6f;
    public const float Glass = 7f;
    public const float Lamp = 8f;
    public const float Edge = 10f;
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
/// Char d'assaut modélisé pour MAUS (repère : x vers l'avant, y vers le haut, z sur le côté ; chenilles au sol en y = 0).
/// Caisse inférieure entre les chenilles, superstructure à flancs inclinés au-dessus d'elles, glacis supérieur et plaque de
/// nez, vraies chenilles (maillons et crampons le long de la boucle de roulement), barbotin denté, roue folle, six galets
/// et trois rouleaux par côté, jupes en trois panneaux, tourelle polygonale à flancs inclinés avec masque de canon,
/// tourelleau, épiscopes, mitrailleuse, panier de rangement, lance-pots fumigènes, antenne. Les arêtes sont biseautées :
/// elles accrochent la lumière comme sur un vrai blindé.
/// </summary>
internal static class TankMeshes
{
    /// <summary>Axe de rotation de la tourelle (x) et hauteur du toit de la caisse, repris par battle.hlsl.</summary>
    public const float TurretPivotX = -0.25f;
    public const float DeckY = 1.66f;
    public const float TrackZ = 1.91f;
    private const float TrackHalfWidth = 0.31f;

    public static (float[] Vertices, uint[] Indices) Hull()
    {
        var b = new MeshBuilder();

        // Caisse inférieure, entre les chenilles : plaque de nez inclinée vers le bas, plancher, arrière.
        b.Prism(
            [new(3.0f, 0.98f), new(2.55f, 0.62f), new(-2.7f, 0.62f), new(-3.0f, 0.92f), new(-3.02f, 1.26f), new(3.0f, 1.26f)],
            1.42f, 1.42f, 0.03f, TankMaterial.Paint);

        // Superstructure : glacis supérieur incliné, toit, plage moteur un peu plus basse ; flancs inclinés au-dessus des chenilles.
        b.Prism(
            [new(3.05f, 1.0f), new(3.02f, 1.24f), new(1.8f, DeckY - 0.04f), new(1.6f, DeckY), new(-1.55f, DeckY), new(-1.7f, 1.6f), new(-2.95f, 1.58f), new(-3.07f, 1.28f), new(-3.0f, 1.22f), new(2.9f, 1.22f)],
            2.0f, 1.84f, 0.035f, TankMaterial.Paint);

        // Jupes de protection : trois panneaux par côté, légèrement inclinés, avec leurs supports.
        foreach (var side in new[] { 1f, -1f })
        {
            for (var p = 0; p < 3; p++)
            {
                var x = -2.0f + (p * 1.85f);
                b.BeveledBox(new Vector3(x, 1.02f, side * 2.24f), new Vector3(0.9f, 0.28f, 0.022f), 0.012f, TankMaterial.Paint);
                b.Box(new Vector3(x, 1.27f, side * 2.12f), new Vector3(0.05f, 0.03f, 0.12f), TankMaterial.Steel);
            }

            // Câble de remorquage le long du flanc, boîte à outils, pelle et pioche sur le garde-boue.
            b.Cylinder(new Vector3(-0.2f, 1.42f, side * 1.97f), 0.025f, 2.3f, Vector3.UnitX, 6, TankMaterial.Steel);
            b.BeveledBox(new Vector3(-2.35f, 1.71f, side * 1.7f), new Vector3(0.38f, 0.1f, 0.13f), 0.02f, TankMaterial.Paint);
            b.Cylinder(new Vector3(0.9f, 1.7f, side * 1.55f), 0.022f, 0.55f, Vector3.UnitX, 6, TankMaterial.Wood);
            b.Box(new Vector3(1.5f, 1.7f, side * 1.55f), new Vector3(0.12f, 0.012f, 0.09f), TankMaterial.Steel);
            AddRunningGear(b, side);
        }

        // Plage arrière : grilles du moteur, jerricans, échappements avec leurs pare-chaleur.
        b.BeveledBox(new Vector3(-2.25f, 1.6f, 0f), new Vector3(0.62f, 0.035f, 1.0f), 0.015f, TankMaterial.Grille);
        foreach (var z in new[] { 1.2f, 0.95f, -0.95f, -1.2f })
        {
            b.BeveledBox(new Vector3(-3.18f, 1.18f, z), new Vector3(0.09f, 0.2f, 0.11f), 0.02f, TankMaterial.Paint);
        }

        foreach (var z in new[] { 0.5f, -0.5f })
        {
            b.Cylinder(new Vector3(-3.22f, 1.38f, z), 0.09f, 0.16f, Vector3.UnitX, 12, TankMaterial.Steel);
            b.Cylinder(new Vector3(-3.36f, 1.38f, z), 0.065f, 0.04f, Vector3.UnitX, 10, TankMaterial.Rubber);
        }

        // Avant : phares, crochets de remorquage, visière du pilote, rotule de mitrailleuse, maillons de rechange sur le glacis.
        foreach (var z in new[] { 1.58f, -1.58f })
        {
            b.Cylinder(new Vector3(2.6f, 1.44f, z), 0.1f, 0.06f, Vector3.Normalize(new Vector3(1f, 0.35f, 0f)), 12, TankMaterial.Steel);
            b.Cylinder(new Vector3(2.66f, 1.46f, z), 0.075f, 0.012f, Vector3.Normalize(new Vector3(1f, 0.35f, 0f)), 12, TankMaterial.Lamp);
            b.BeveledBox(new Vector3(3.02f, 0.82f, z * 0.44f), new Vector3(0.09f, 0.07f, 0.09f), 0.02f, TankMaterial.Steel);
        }

        var glacis = Vector3.Normalize(new Vector3(-1.22f, 0.38f, 0f));
        var glacisUp = Vector3.Normalize(new Vector3(0.38f, 1.22f, 0f));
        b.OrientedBox(new Vector3(2.15f, 1.55f, 0.55f), glacis, glacisUp, new Vector3(0.2f, 0.05f, 0.24f), TankMaterial.Steel);
        b.OrientedBox(new Vector3(2.12f, 1.62f, 0.58f), glacis, glacisUp, new Vector3(0.05f, 0.03f, 0.08f), TankMaterial.Glass);
        b.Cylinder(new Vector3(2.35f, 1.48f, -0.55f), 0.13f, 0.06f, Vector3.Normalize(new Vector3(1f, 0.32f, 0f)), 12, TankMaterial.Steel);
        b.Cylinder(new Vector3(2.6f, 1.49f, -0.55f), 0.03f, 0.22f, Vector3.UnitX, 8, TankMaterial.Steel);
        foreach (var z in new[] { 0.95f, 1.25f, -0.95f, -1.25f })
        {
            b.OrientedBox(new Vector3(2.82f, 1.335f, z), glacis, glacisUp, new Vector3(0.12f, 0.03f, 0.13f), TankMaterial.Track);
        }

        return b.Build();
    }

    public static (float[] Vertices, uint[] Indices) Turret()
    {
        var b = new MeshBuilder();
        const float x0 = TurretPivotX;

        // Tourelle à flancs inclinés (vue de dessus : avant pointu, nuque allongée), arêtes biseautées.
        b.Frustum(
            [new(x0 + 1.35f, 0.42f), new(x0 + 0.85f, 1.12f), new(x0 - 0.9f, 1.25f), new(x0 - 1.75f, 0.98f), new(x0 - 2.05f, 0.4f),
             new(x0 - 2.05f, -0.4f), new(x0 - 1.75f, -0.98f), new(x0 - 0.9f, -1.25f), new(x0 + 0.85f, -1.12f), new(x0 + 1.35f, -0.42f)],
            DeckY, 2.36f, 0.86f, 0.04f, TankMaterial.Paint);

        // Masque du canon, tube, manchon, évacuateur de fumée, frein de bouche à deux chambres.
        b.BeveledBox(new Vector3(1.28f, 2.03f, 0f), new Vector3(0.2f, 0.25f, 0.42f), 0.05f, TankMaterial.Paint);
        b.Cylinder(new Vector3(1.6f, 2.06f, 0f), 0.2f, 0.16f, Vector3.UnitX, 18, TankMaterial.Paint);
        b.Cylinder(new Vector3(3.4f, 2.06f, 0f), 0.105f, 1.95f, Vector3.UnitX, 16, TankMaterial.Paint);
        b.Cylinder(new Vector3(3.25f, 2.06f, 0f), 0.155f, 0.3f, Vector3.UnitX, 16, TankMaterial.Paint);
        b.Cylinder(new Vector3(5.32f, 2.06f, 0f), 0.15f, 0.1f, Vector3.UnitX, 14, TankMaterial.Steel);
        b.Cylinder(new Vector3(5.56f, 2.06f, 0f), 0.15f, 0.1f, Vector3.UnitX, 14, TankMaterial.Steel);
        b.Cylinder(new Vector3(5.44f, 2.06f, 0f), 0.1f, 0.03f, Vector3.UnitX, 12, TankMaterial.Steel);

        // Tourelleau du chef avec épiscopes et mitrailleuse, trappe du chargeur, viseur.
        b.Cylinder(new Vector3(x0 - 0.45f, 2.47f, 0.5f), 0.36f, 0.12f, Vector3.UnitY, 18, TankMaterial.Paint);
        b.Cylinder(new Vector3(x0 - 0.45f, 2.62f, 0.5f), 0.31f, 0.035f, Vector3.UnitY, 18, TankMaterial.Steel);
        for (var k = 0; k < 6; k++)
        {
            var a = k * MathF.Tau / 6f;
            b.BeveledBox(new Vector3(x0 - 0.45f + (MathF.Cos(a) * 0.36f), 2.52f, 0.5f + (MathF.Sin(a) * 0.36f)), new Vector3(0.05f, 0.04f, 0.05f), 0.01f, TankMaterial.Glass);
        }

        b.Cylinder(new Vector3(x0 - 0.25f, 2.78f, 0.5f), 0.025f, 0.45f, Vector3.Normalize(new Vector3(1f, 0.08f, 0f)), 6, TankMaterial.Steel);
        b.BeveledBox(new Vector3(x0 - 0.6f, 2.7f, 0.5f), new Vector3(0.12f, 0.05f, 0.05f), 0.015f, TankMaterial.Steel);
        b.Cylinder(new Vector3(x0 - 0.3f, 2.39f, -0.55f), 0.28f, 0.03f, Vector3.UnitY, 16, TankMaterial.Steel);
        b.BeveledBox(new Vector3(x0 + 0.7f, 2.4f, -0.35f), new Vector3(0.14f, 0.07f, 0.09f), 0.02f, TankMaterial.Steel);
        b.BeveledBox(new Vector3(x0 + 0.84f, 2.41f, -0.35f), new Vector3(0.012f, 0.045f, 0.06f), 0.005f, TankMaterial.Glass);

        // Panier de rangement à l'arrière (barreaux et sacs), lance-pots fumigènes, antenne.
        b.Box(new Vector3(x0 - 2.32f, 2.12f, 0f), new Vector3(0.28f, 0.015f, 0.85f), TankMaterial.Steel);
        b.Box(new Vector3(x0 - 2.58f, 2.28f, 0f), new Vector3(0.015f, 0.17f, 0.85f), TankMaterial.Steel);
        b.Box(new Vector3(x0 - 2.32f, 2.44f, 0.85f), new Vector3(0.28f, 0.015f, 0.015f), TankMaterial.Steel);
        b.Box(new Vector3(x0 - 2.32f, 2.44f, -0.85f), new Vector3(0.28f, 0.015f, 0.015f), TankMaterial.Steel);
        b.BeveledBox(new Vector3(x0 - 2.3f, 2.26f, 0.38f), new Vector3(0.22f, 0.13f, 0.32f), 0.08f, TankMaterial.Wood);
        b.BeveledBox(new Vector3(x0 - 2.32f, 2.24f, -0.42f), new Vector3(0.2f, 0.11f, 0.3f), 0.07f, TankMaterial.Rubber);
        foreach (var side in new[] { 1f, -1f })
        {
            for (var k = 0; k < 3; k++)
            {
                b.Cylinder(new Vector3(x0 + 0.55f + (k * 0.17f), 2.2f, side * 1.08f), 0.055f, 0.13f, Vector3.Normalize(new Vector3(0.5f, 0.55f, side)), 10, TankMaterial.Steel);
            }
        }

        b.Cylinder(new Vector3(x0 - 1.65f, 2.92f, -0.82f), 0.012f, 0.6f, Vector3.UnitY, 5, TankMaterial.Steel);
        b.Cylinder(new Vector3(x0 - 1.65f, 2.36f, -0.82f), 0.05f, 0.04f, Vector3.UnitY, 8, TankMaterial.Steel);
        return b.Build();
    }

    /// <summary>Éclat de blindage : plaque tordue, avec un bord arraché.</summary>
    public static (float[] Vertices, uint[] Indices) Shard()
    {
        var b = new MeshBuilder();
        b.Box(Vector3.Zero, new Vector3(0.5f, 0.12f, 0.45f), TankMaterial.Paint, frontSlope: 0.5f);
        b.Box(new Vector3(0.2f, 0.1f, 0.1f), new Vector3(0.2f, 0.1f, 0.25f), TankMaterial.Steel + TankMaterial.Edge, frontSlope: 0.8f);
        return b.Build();
    }

    /// <summary>Train de roulement d'un côté : galets, rouleaux, barbotin, roue folle et chenille.</summary>
    private static void AddRunningGear(MeshBuilder b, float side)
    {
        var z = side * TrackZ;
        for (var w = 0; w < 6; w++)
        {
            var x = -2.25f + (w * 0.9f);
            b.Cylinder(new Vector3(x, 0.42f, z), 0.36f, 0.24f, Vector3.UnitZ, 20, TankMaterial.Wheel);
            b.Cylinder(new Vector3(x, 0.42f, z + (side * 0.25f)), 0.15f, 0.03f, Vector3.UnitZ, 12, TankMaterial.Steel);
            b.Cylinder(new Vector3(x, 0.42f, z + (side * 0.29f)), 0.06f, 0.02f, Vector3.UnitZ, 8, TankMaterial.Steel + TankMaterial.Edge);
        }

        for (var r = 0; r < 3; r++)
        {
            b.Cylinder(new Vector3(-1.6f + (r * 1.6f), 1.0f, z), 0.1f, 0.2f, Vector3.UnitZ, 12, TankMaterial.Wheel);
        }

        b.Gear(new Vector3(2.95f, 0.66f, z), 0.4f, 0.48f, 0.22f, 13, TankMaterial.Steel);
        b.Cylinder(new Vector3(2.95f, 0.66f, z + (side * 0.24f)), 0.18f, 0.03f, Vector3.UnitZ, 12, TankMaterial.Steel + TankMaterial.Edge);
        b.Cylinder(new Vector3(-3.0f, 0.62f, z), 0.4f, 0.22f, Vector3.UnitZ, 20, TankMaterial.Wheel);

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

/// <summary>
/// Assemble pavés (biseautés ou non), prismes, troncs de pyramide, cylindres et roues dentées en un maillage à normales
/// par face. Les faces de biseau portent la matière + <see cref="TankMaterial.Edge"/>.
/// </summary>
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

    /// <summary>Pavé aux douze arêtes biseautées (largeur <paramref name="bevel"/>) : six faces, douze biseaux, huit coins.</summary>
    public void BeveledBox(Vector3 center, Vector3 half, float bevel, float material)
    {
        var edge = material + TankMaterial.Edge;
        var b = MathF.Min(bevel, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) * 0.9f);
        var inner = half - new Vector3(b);

        // Point d'un coin (sx, sy, sz) décalé de b sur l'axe « axis » (0 x, 1 y, 2 z) : face perpendiculaire à cet axe.
        Vector3 P(float sx, float sy, float sz, int axis)
        {
            var p = new Vector3(sx * inner.X, sy * inner.Y, sz * inner.Z);
            return center + axis switch
            {
                0 => p + new Vector3(sx * b, 0, 0),
                1 => p + new Vector3(0, sy * b, 0),
                _ => p + new Vector3(0, 0, sz * b),
            };
        }

        // Faces principales (orientées vers l'extérieur).
        foreach (var s in new[] { 1f, -1f })
        {
            QuadOut(P(s, -1, -1, 0), P(s, 1, -1, 0), P(s, 1, 1, 0), P(s, -1, 1, 0), center, material);
            QuadOut(P(-1, s, -1, 1), P(1, s, -1, 1), P(1, s, 1, 1), P(-1, s, 1, 1), center, material);
            QuadOut(P(-1, -1, s, 2), P(1, -1, s, 2), P(1, 1, s, 2), P(-1, 1, s, 2), center, material);
        }

        // Biseaux le long des arêtes, entre les deux faces voisines.
        foreach (var sy in new[] { 1f, -1f })
        {
            foreach (var sz in new[] { 1f, -1f })
            {
                QuadOut(P(-1, sy, sz, 1), P(1, sy, sz, 1), P(1, sy, sz, 2), P(-1, sy, sz, 2), center, edge);
            }
        }

        foreach (var sx in new[] { 1f, -1f })
        {
            foreach (var sz in new[] { 1f, -1f })
            {
                QuadOut(P(sx, -1, sz, 0), P(sx, 1, sz, 0), P(sx, 1, sz, 2), P(sx, -1, sz, 2), center, edge);
            }

            foreach (var sy in new[] { 1f, -1f })
            {
                QuadOut(P(sx, sy, -1, 0), P(sx, sy, 1, 0), P(sx, sy, 1, 1), P(sx, sy, -1, 1), center, edge);
            }
        }

        // Coins : petits triangles entre les trois biseaux.
        foreach (var sx in new[] { 1f, -1f })
        {
            foreach (var sy in new[] { 1f, -1f })
            {
                foreach (var sz in new[] { 1f, -1f })
                {
                    TriangleOut(P(sx, sy, sz, 0), P(sx, sy, sz, 1), P(sx, sy, sz, 2), center, edge);
                }
            }
        }
    }

    /// <summary>
    /// Prisme : profil vu de côté (x, y), convexe, extrudé selon z ; demi-largeur <paramref name="halfBottom"/> en bas et
    /// <paramref name="halfTop"/> en haut (flancs inclinés). Coins du profil et bords des flancs biseautés.
    /// </summary>
    public void Prism(IReadOnlyList<Vector2> profile, float halfBottom, float halfTop, float bevel, float material)
    {
        var edge = material + TankMaterial.Edge;
        var cut = CutCorners(profile, bevel, out var isCorner);
        var minY = profile.Min(p => p.Y);
        var maxY = profile.Max(p => p.Y);
        float Half(float y) => halfBottom + ((halfTop - halfBottom) * (y - minY) / MathF.Max(maxY - minY, 1e-4f));
        var centroid = new Vector2(profile.Average(p => p.X), profile.Average(p => p.Y));
        var center = new Vector3(centroid, 0f);
        var n = cut.Count;
        Vector3 Outer(int i, float side) => new(cut[i].X, cut[i].Y, side * (Half(cut[i].Y) - bevel));
        Vector3 Inner(int i, float side)
        {
            var p = cut[i] + (Vector2.Normalize(centroid - cut[i]) * bevel * 1.3f);
            return new Vector3(p.X, p.Y, side * Half(p.Y));
        }

        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var corner = isCorner[i];
            QuadOut(Outer(i, 1), Outer(j, 1), Outer(j, -1), Outer(i, -1), center, corner ? edge : material);
            foreach (var side in new[] { 1f, -1f })
            {
                QuadOut(Outer(i, side), Outer(j, side), Inner(j, side), Inner(i, side), center, edge);
                TriangleOut(new Vector3(centroid.X, centroid.Y, side * Half(centroid.Y)), Inner(i, side), Inner(j, side), center, material);
            }
        }
    }

    /// <summary>
    /// Tronc de pyramide : polygone convexe vu de dessus (x, z) entre <paramref name="bottom"/> et <paramref name="top"/>,
    /// réduit de <paramref name="topScale"/> au sommet (flancs inclinés) ; arêtes verticales et bord du toit biseautés.
    /// </summary>
    public void Frustum(IReadOnlyList<Vector2> outline, float bottom, float top, float topScale, float bevel, float material)
    {
        var edge = material + TankMaterial.Edge;
        var cut = CutCorners(outline, bevel, out var isCorner);
        var centroid = new Vector2(outline.Average(p => p.X), outline.Average(p => p.Y));
        var center = new Vector3(centroid.X, (bottom + top) * 0.5f, centroid.Y);
        var n = cut.Count;
        Vector3 At(int i, float y, float scale, float inset)
        {
            var p = centroid + ((cut[i] - centroid) * scale);
            p += Vector2.Normalize(centroid - p) * inset;
            return new Vector3(p.X, y, p.Y);
        }

        var shoulder = top - bevel;
        var shoulderScale = 1f + ((topScale - 1f) * ((shoulder - bottom) / (top - bottom)));
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var corner = isCorner[i];
            QuadOut(At(i, bottom, 1f, 0f), At(j, bottom, 1f, 0f), At(j, shoulder, shoulderScale, 0f), At(i, shoulder, shoulderScale, 0f), center, corner ? edge : material);
            QuadOut(At(i, shoulder, shoulderScale, 0f), At(j, shoulder, shoulderScale, 0f), At(j, top, topScale, bevel * 1.3f), At(i, top, topScale, bevel * 1.3f), center, edge);
            TriangleOut(new Vector3(centroid.X, top, centroid.Y), At(i, top, topScale, bevel * 1.3f), At(j, top, topScale, bevel * 1.3f), center, material);
            TriangleOut(new Vector3(centroid.X, bottom, centroid.Y), At(i, bottom, 1f, 0f), At(j, bottom, 1f, 0f), center, material);
        }
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
            Quad(bottom + r0, bottom + r1, top + r1, top + r0, material);
            Triangle(top, top + r0, top + r1, material);
            Triangle(bottom, bottom + r1, bottom + r0, material);
        }
    }

    public (float[] Vertices, uint[] Indices) Build() => ([.. _vertices], [.. _indices]);

    /// <summary>Remplace chaque coin d'un polygone par un petit pan (biseau) ; <paramref name="isCorner"/> marque les pans de coin.</summary>
    private static List<Vector2> CutCorners(IReadOnlyList<Vector2> polygon, float bevel, out List<bool> isCorner)
    {
        var result = new List<Vector2>();
        isCorner = [];
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var p = polygon[i];
            var previous = polygon[(i + n - 1) % n];
            var next = polygon[(i + 1) % n];
            var a = Vector2.Distance(previous, p);
            var c = Vector2.Distance(next, p);
            result.Add(p + (Vector2.Normalize(previous - p) * MathF.Min(bevel, a * 0.4f)));
            isCorner.Add(true);
            result.Add(p + (Vector2.Normalize(next - p) * MathF.Min(bevel, c * 0.4f)));
            isCorner.Add(false);
        }

        return result;
    }

    private void Faces(Vector3[] c, float material)
    {
        Quad(c[0], c[2], c[3], c[1], material);
        Quad(c[4], c[5], c[7], c[6], material);
        Quad(c[0], c[4], c[6], c[2], material);
        Quad(c[1], c[3], c[7], c[5], material);
        Quad(c[0], c[1], c[5], c[4], material);
        Quad(c[2], c[6], c[7], c[3], material);
    }

    /// <summary>Quadrilatère dont la face visible regarde à l'opposé de <paramref name="inside"/> (ordre des sommets corrigé).</summary>
    private void QuadOut(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside, float material)
    {
        TriangleOut(a, b, c, inside, material);
        TriangleOut(a, c, d, inside, material);
    }

    private void TriangleOut(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, float material)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-14f)
        {
            return;
        }

        // Même convention que Triangle() : la normale calculée par Cross(b - a, c - a) doit pointer vers l'extérieur.
        if (Vector3.Dot(n, ((a + b + c) / 3f) - inside) < 0f)
        {
            (b, c) = (c, b);
        }

        Triangle(a, b, c, material);
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
