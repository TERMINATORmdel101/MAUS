using System.Numerics;
using System.Text.Json;

namespace Maus.Bench.Render;

/// <summary>Matière d'un modèle glTF (PBR « métal-rugosité » du format, chemins des images sur le disque).</summary>
internal sealed record GltfMaterial(
    string Name,
    string? BaseColor,
    string? Normal,
    string? MetalRough,
    Vector4 BaseColorFactor,
    float Metallic,
    float Roughness,
    bool Glass)
{
    /// <summary>
    /// Les images « arm » de Poly Haven portent aussi l'occlusion ambiante dans le rouge ; les images « rough » non (on ne
    /// lit alors que la rugosité, dans le vert, comme le veut glTF).
    /// </summary>
    public bool AoInRed => MetalRough?.Contains("_arm_", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Une partie d'un modèle : sommets (position, normale, tangente, coordonnées de texture : 12 flottants), indices.</summary>
internal sealed record GltfPrimitive(float[] Vertices, uint[] Indices, int Material);

/// <summary>
/// Modèle glTF 2.0 (format ouvert du Khronos Group) lu sans bibliothèque : nœuds à plat (translation, rotation,
/// échelle appliquées aux sommets), attributs POSITION, NORMAL, TEXCOORD_0, indices 16 ou 32 bits, matières PBR.
/// Les tangentes sont calculées au chargement (méthode d'E. Lengyel, 2001) pour les cartes de normales.
/// </summary>
internal sealed class GltfModel
{
    public const int FloatsPerVertex = 12;

    public List<GltfPrimitive> Primitives { get; } = [];

    public List<GltfMaterial> Materials { get; } = [];

    public Vector3 Min { get; private set; } = new(float.MaxValue);

    public Vector3 Max { get; private set; } = new(float.MinValue);

    public static GltfModel Load(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var buffers = root.GetProperty("buffers").EnumerateArray().Select(b => File.ReadAllBytes(Path.Combine(folder, b.GetProperty("uri").GetString()!))).ToArray();
        var views = root.GetProperty("bufferViews").EnumerateArray().ToArray();
        var accessors = root.GetProperty("accessors").EnumerateArray().ToArray();
        var images = root.TryGetProperty("images", out var imageList) ? imageList.EnumerateArray().Select(i => Path.Combine(folder, i.GetProperty("uri").GetString()!)).ToArray() : [];
        var textures = root.TryGetProperty("textures", out var textureList) ? textureList.EnumerateArray().Select(t => t.GetProperty("source").GetInt32()).ToArray() : [];
        string? Image(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out var reference) && reference.TryGetProperty("index", out var index) ? images[textures[index.GetInt32()]] : null;

        var model = new GltfModel();
        foreach (var m in root.GetProperty("materials").EnumerateArray())
        {
            var pbr = m.TryGetProperty("pbrMetallicRoughness", out var p) ? p : default;
            var hasPbr = pbr.ValueKind == JsonValueKind.Object;
            var factor = hasPbr && pbr.TryGetProperty("baseColorFactor", out var f) ? f.EnumerateArray().Select(x => x.GetSingle()).ToArray() : [1f, 1f, 1f, 1f];
            var transmission = m.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("KHR_materials_transmission", out var t)
                && t.TryGetProperty("transmissionFactor", out var tf) && tf.GetSingle() > 0f;
            var blend = m.TryGetProperty("alphaMode", out var mode) && mode.GetString() == "BLEND";
            var baseColor = hasPbr ? Image(pbr, "baseColorTexture") : null;
            model.Materials.Add(new GltfMaterial(
                m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                baseColor,
                Image(m, "normalTexture"),
                hasPbr ? Image(pbr, "metallicRoughnessTexture") : null,
                new Vector4(factor[0], factor[1], factor[2], factor[3]),
                hasPbr && pbr.TryGetProperty("metallicFactor", out var mf) ? mf.GetSingle() : 1f,
                hasPbr && pbr.TryGetProperty("roughnessFactor", out var rf) ? rf.GetSingle() : 1f,
                // Verre : matière transparente, ou transmission sans image de couleur (un objectif). Le laiton de la
                // lanterne porte une transmission sans être transparent : il garde son image et reste opaque.
                blend || (transmission && baseColor is null)));
        }

        var meshes = root.GetProperty("meshes").EnumerateArray().ToArray();
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("mesh", out var meshIndex))
            {
                continue;
            }

            var transform = NodeTransform(node);
            var normalTransform = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
            foreach (var primitive in meshes[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                var attributes = primitive.GetProperty("attributes");
                var positions = ReadVec(accessors, views, buffers, attributes.GetProperty("POSITION").GetInt32(), 3);
                var normals = ReadVec(accessors, views, buffers, attributes.GetProperty("NORMAL").GetInt32(), 3);
                var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uv) ? ReadVec(accessors, views, buffers, uv.GetInt32(), 2) : new float[positions.Length / 3 * 2];
                var count = positions.Length / 3;
                var indices = primitive.TryGetProperty("indices", out var ix) ? ReadIndices(accessors, views, buffers, ix.GetInt32()) : [.. Enumerable.Range(0, count).Select(i => (uint)i)];
                var p = new Vector3[count];
                var nrm = new Vector3[count];
                var tex = new Vector2[count];
                for (var i = 0; i < count; i++)
                {
                    p[i] = Vector3.Transform(new Vector3(positions[i * 3], positions[(i * 3) + 1], positions[(i * 3) + 2]), transform);
                    nrm[i] = Vector3.Normalize(Vector3.TransformNormal(new Vector3(normals[i * 3], normals[(i * 3) + 1], normals[(i * 3) + 2]), normalTransform));
                    tex[i] = new Vector2(uvs[i * 2], uvs[(i * 2) + 1]);
                    model.Min = Vector3.Min(model.Min, p[i]);
                    model.Max = Vector3.Max(model.Max, p[i]);
                }

                var tangents = Tangents(p, nrm, tex, indices);
                var vertices = new float[count * FloatsPerVertex];
                for (var i = 0; i < count; i++)
                {
                    var o = i * FloatsPerVertex;
                    (vertices[o], vertices[o + 1], vertices[o + 2]) = (p[i].X, p[i].Y, p[i].Z);
                    (vertices[o + 3], vertices[o + 4], vertices[o + 5]) = (nrm[i].X, nrm[i].Y, nrm[i].Z);
                    (vertices[o + 6], vertices[o + 7], vertices[o + 8], vertices[o + 9]) = (tangents[i].X, tangents[i].Y, tangents[i].Z, tangents[i].W);
                    (vertices[o + 10], vertices[o + 11]) = (tex[i].X, tex[i].Y);
                }

                model.Primitives.Add(new GltfPrimitive(vertices, indices, primitive.TryGetProperty("material", out var mat) ? mat.GetInt32() : 0));
            }
        }

        return model;
    }

    private static Matrix4x4 NodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var v = matrix.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            // glTF range les matrices par colonnes ; System.Numerics les lit par lignes (vecteur × matrice) : même ordre en mémoire.
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }

        var scale = node.TryGetProperty("scale", out var s) ? Vec3(s) : Vector3.One;
        var rotation = node.TryGetProperty("rotation", out var r) ? Quat(r) : Quaternion.Identity;
        var translation = node.TryGetProperty("translation", out var t) ? Vec3(t) : Vector3.Zero;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

        static Vector3 Vec3(JsonElement e)
        {
            var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new Vector3(v[0], v[1], v[2]);
        }

        static Quaternion Quat(JsonElement e)
        {
            var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new Quaternion(v[0], v[1], v[2], v[3]);
        }
    }

    private static float[] ReadVec(JsonElement[] accessors, JsonElement[] views, byte[][] buffers, int index, int components)
    {
        var accessor = accessors[index];
        if (accessor.GetProperty("componentType").GetInt32() != 5126)
        {
            throw new InvalidDataException("Attribut glTF non flottant : non pris en charge.");
        }

        var count = accessor.GetProperty("count").GetInt32();
        var (data, offset, stride) = View(accessor, views, buffers, components * 4);
        var result = new float[count * components];
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < components; c++)
            {
                result[(i * components) + c] = BitConverter.ToSingle(data, offset + (i * stride) + (c * 4));
            }
        }

        return result;
    }

    private static uint[] ReadIndices(JsonElement[] accessors, JsonElement[] views, byte[][] buffers, int index)
    {
        var accessor = accessors[index];
        var type = accessor.GetProperty("componentType").GetInt32();
        var size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("Indices glTF inconnus.") };
        var count = accessor.GetProperty("count").GetInt32();
        var (data, offset, stride) = View(accessor, views, buffers, size);
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var at = offset + (i * stride);
            result[i] = size switch { 1 => data[at], 2 => BitConverter.ToUInt16(data, at), _ => BitConverter.ToUInt32(data, at) };
        }

        return result;
    }

    private static (byte[] Data, int Offset, int Stride) View(JsonElement accessor, JsonElement[] views, byte[][] buffers, int elementSize)
    {
        var view = views[accessor.GetProperty("bufferView").GetInt32()];
        var offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var stride = view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : elementSize;
        return (buffers[view.GetProperty("buffer").GetInt32()], offset, stride);
    }

    /// <summary>
    /// Tangente (le long de u) et signe de la bitangente pour chaque sommet : somme des directions par triangle, puis
    /// orthogonalisation (E. Lengyel, « Computing Tangent Space Basis Vectors for an Arbitrary Mesh », 2001).
    /// </summary>
    private static Vector4[] Tangents(Vector3[] p, Vector3[] n, Vector2[] uv, uint[] indices)
    {
        var tan1 = new Vector3[p.Length];
        var tan2 = new Vector3[p.Length];
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = ((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
            var e1 = p[b] - p[a];
            var e2 = p[c] - p[a];
            var d1 = uv[b] - uv[a];
            var d2 = uv[c] - uv[a];
            var det = (d1.X * d2.Y) - (d2.X * d1.Y);
            if (MathF.Abs(det) < 1e-12f)
            {
                continue;
            }

            var r = 1f / det;
            var s = ((e1 * d2.Y) - (e2 * d1.Y)) * r;
            var t = ((e2 * d1.X) - (e1 * d2.X)) * r;
            tan1[a] += s;
            tan1[b] += s;
            tan1[c] += s;
            tan2[a] += t;
            tan2[b] += t;
            tan2[c] += t;
        }

        var result = new Vector4[p.Length];
        for (var i = 0; i < p.Length; i++)
        {
            var t = tan1[i] - (n[i] * Vector3.Dot(n[i], tan1[i]));
            if (t.LengthSquared() < 1e-12f)
            {
                // Pas de coordonnées de texture exploitables : une tangente quelconque perpendiculaire à la normale.
                t = Vector3.Cross(n[i], MathF.Abs(n[i].Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            }

            t = Vector3.Normalize(t);
            var w = Vector3.Dot(Vector3.Cross(n[i], tan1[i]), tan2[i]) < 0f ? -1f : 1f;
            result[i] = new Vector4(t, w);
        }

        return result;
    }
}
