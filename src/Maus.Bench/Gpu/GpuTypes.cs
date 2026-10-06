using System.Numerics;

namespace Maus.Bench.Gpu;

/// <summary>Interface de programmation graphique choisie par l'utilisateur au lancement du benchmark.</summary>
public enum GpuApi
{
    Direct3D11,
    Direct3D12,
}

/// <summary>Formats de pixels utilisés par le benchmark (sous-ensemble commun à Direct3D 11 et 12).</summary>
public enum PixelFormat
{
    Unknown,
    Rgba8Unorm,

    /// <summary>Couleurs d'images (sRVB) : la carte les convertit en lumière linéaire à la lecture.</summary>
    Rgba8UnormSrgb,
    Bgra8Unorm,
    Rgba16Float,
    Rgba32Float,
    Rg16Float,
    R16Float,
    R32Float,
    R11G11B10Float,
    R32Uint,
    R8Unorm,
    D32Float,
}

[Flags]
public enum TextureUsage
{
    None = 0,
    Sampled = 1,
    RenderTarget = 2,
    DepthStencil = 4,
    Storage = 8,
}

public enum TextureKind
{
    Texture2D,
    Texture3D,
    TextureCube,
}

/// <summary>Description d'une texture : 2D, 3D (profondeur) ou cube (6 faces).</summary>
public sealed record TextureDesc(
    TextureKind Kind,
    int Width,
    int Height,
    int Depth,
    int MipLevels,
    PixelFormat Format,
    TextureUsage Usage,
    string Name)
{
    public static TextureDesc Target(int width, int height, PixelFormat format, string name, int mips = 1, bool storage = false) =>
        new(TextureKind.Texture2D, width, height, 1, mips, format, TextureUsage.Sampled | TextureUsage.RenderTarget | (storage ? TextureUsage.Storage : 0), name);

    public static TextureDesc DepthTarget(int width, int height, string name) =>
        new(TextureKind.Texture2D, width, height, 1, 1, PixelFormat.D32Float, TextureUsage.DepthStencil | TextureUsage.Sampled, name);

    public static TextureDesc Image(int width, int height, PixelFormat format, string name, int mips = 1) =>
        new(TextureKind.Texture2D, width, height, 1, mips, format, TextureUsage.Sampled, name);

    public static TextureDesc Volume(int size, PixelFormat format, string name) =>
        new(TextureKind.Texture3D, size, size, size, 1, format, TextureUsage.Sampled | TextureUsage.Storage, name);

    public static TextureDesc Cube(int size, int mips, PixelFormat format, string name, bool renderTarget = true) =>
        new(TextureKind.TextureCube, size, size, 6, mips, format, TextureUsage.Sampled | (renderTarget ? TextureUsage.RenderTarget : 0), name);

    /// <summary>Taille d'un niveau de détail.</summary>
    public int MipWidth(int mip) => Math.Max(1, Width >> mip);

    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    public int MipDepth(int mip) => Kind == TextureKind.Texture3D ? Math.Max(1, Depth >> mip) : Depth;
}

[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1,
    Index = 2,

    /// <summary>Lu par les shaders (StructuredBuffer, ou ByteAddressBuffer avec <see cref="Raw"/>).</summary>
    Structured = 4,

    /// <summary>Écrit par les shaders de calcul (RWStructuredBuffer, ou RWByteAddressBuffer avec <see cref="Raw"/>).</summary>
    Storage = 8,

    /// <summary>Vues brutes (ByteAddressBuffer) au lieu de structurées : obligatoire pour les arguments de dessin indirect.</summary>
    Raw = 16,

    /// <summary>Arguments de DrawIndexedInstancedIndirect écrits par un shader de calcul (5 entiers de 32 bits par dessin).</summary>
    IndirectArgs = 32,
}

public sealed record BufferDesc(long SizeInBytes, BufferUsage Usage, int Stride, string Name);

public enum BlendMode
{
    Opaque,
    Additive,
    AlphaBlend,

    /// <summary>Couleur déjà multipliée par l'opacité (interface, nuages composés).</summary>
    Premultiplied,

    /// <summary>Assombrit la cible (absorption par la poussière).</summary>
    Multiply,
}

public enum DepthMode
{
    None,
    TestWrite,
    TestOnly,
}

public enum CullMode
{
    None,
    Back,
    Front,
}

public enum VertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    UInt1,
    Unorm4,
}

/// <summary>Un attribut de sommet (sémantique HLSL, format, décalage dans le sommet, flux 0 = par sommet, 1 = par instance).</summary>
public sealed record VertexElement(string Semantic, int SemanticIndex, VertexFormat Format, int Offset, int Slot = 0, bool PerInstance = false);

/// <summary>Code compilé d'un shader (DXBC, accepté par Direct3D 11 et 12).</summary>
public sealed record ShaderCode(string Name, ReadOnlyMemory<byte> Bytecode);

/// <summary>Tout ce qui définit un passage de rendu : shaders, sommets, mélange, profondeur, faces, cibles.</summary>
public sealed record GraphicsPipelineDesc(
    string Name,
    ShaderCode VertexShader,
    ShaderCode? PixelShader,
    IReadOnlyList<VertexElement> Layout,
    BlendMode Blend,
    DepthMode Depth,
    CullMode Cull,
    IReadOnlyList<PixelFormat> RenderTargets,
    PixelFormat DepthFormat = PixelFormat.Unknown,
    int DepthBias = 0,
    float SlopeScaledDepthBias = 0,
    bool DepthClamp = false,
    bool DepthGreater = false);

/// <summary>Couleur RVBA en flottants (0 à 1, ou plus pour les cibles HDR).</summary>
public readonly record struct ColorF(float R, float G, float B, float A)
{
    public static ColorF Black => new(0, 0, 0, 1);

    public static ColorF Transparent => new(0, 0, 0, 0);

    public Vector4 ToVector() => new(R, G, B, A);
}

/// <summary>Résolution interne de rendu, fixe pour que les scores restent comparables d'un écran à l'autre.</summary>
public readonly record struct RenderSize(int Width, int Height)
{
    public float Aspect => Width / (float)Height;
}
