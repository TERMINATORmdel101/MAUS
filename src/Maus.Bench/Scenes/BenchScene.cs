using System.Numerics;
using System.Runtime.InteropServices;
using Maus.Bench.Gpu;
using Maus.Bench.Render;

namespace Maus.Bench.Scenes;

/// <summary>État d'une scène à un instant : caméra, soleil, paramètres des shaders, étalonnage.</summary>
internal sealed record SceneState(CameraPose Camera, ColorGrade Grade)
{
    public Vector3 SunDirection { get; init; } = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.4f));

    public Vector3 SunColor { get; init; } = new(1f, 0.95f, 0.85f);

    public Vector4 Params0 { get; init; }

    public Vector4 Params1 { get; init; }

    public Vector4 Params2 { get; init; }

    public Vector4 Params3 { get; init; }

    /// <summary>Anticrénelage temporel (décalage sous-pixel et historique) ; coupé pour les scènes de particules.</summary>
    public bool Temporal { get; init; } = true;

    public float NearPlane { get; init; } = 0.05f;

    public float FarPlane { get; init; } = 2000f;
}

/// <summary>Emprise au sol d'un objet posé dans une scène.</summary>
internal static class BoxFootprint
{
    /// <summary>Bas de l'objet tourné (y minimal) et centre de son emprise au sol, d'après les coins de sa boîte.</summary>
    public static (float Low, Vector2 Center) Of(Vector3 boxMin, Vector3 boxMax, Matrix4x4 local)
    {
        var low = float.MaxValue;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (var k = 0; k < 8; k++)
        {
            var corner = new Vector3((k & 1) == 0 ? boxMin.X : boxMax.X, (k & 2) == 0 ? boxMin.Y : boxMax.Y, (k & 4) == 0 ? boxMin.Z : boxMax.Z);
            var p = Vector3.Transform(corner, local);
            low = MathF.Min(low, p.Y);
            min = Vector2.Min(min, new Vector2(p.X, p.Z));
            max = Vector2.Max(max, new Vector2(p.X, p.Z));
        }

        return (low, (min + max) * 0.5f);
    }
}

/// <summary>Maillage prêt à dessiner : un tampon de sommets (<paramref name="stride"/> octets par sommet) et des indices 32 bits.</summary>
internal sealed class IndexedMesh(IBuffer vertices, IBuffer indices, int indexCount, int stride) : IDisposable
{
    public int IndexCount { get; } = indexCount;

    public static IndexedMesh Create(IGpuDevice device, string name, float[] vertices, uint[] indices, int stride) => new(
        device.CreateBuffer(new BufferDesc(vertices.Length * 4L, BufferUsage.Vertex, stride, name), MemoryMarshal.AsBytes(vertices.AsSpan())),
        device.CreateBuffer(new BufferDesc(indices.Length * 4L, BufferUsage.Index, 4, name + " : indices"), MemoryMarshal.AsBytes(indices.AsSpan())),
        indices.Length,
        stride);

    public void Bind(ICommandList cmd)
    {
        cmd.SetVertexBuffer(0, vertices, stride);
        cmd.SetIndexBuffer(indices);
    }

    public void Dispose()
    {
        vertices.Dispose();
        indices.Dispose();
    }
}

/// <summary>Ce dont une scène a besoin pour dessiner : carte, shaders, cibles communes.</summary>
internal sealed class SceneContext(IGpuDevice device, ShaderLibrary shaders, PostProcess post)
{
    public IGpuDevice Device { get; } = device;

    public ShaderLibrary Shaders { get; } = shaders;

    public PostProcess Post { get; } = post;

    public ICommandList Commands => Device.Commands;

    public RenderSize Size => Post.Size;

    /// <summary>
    /// Mode léger (720p, pour les cartes intégrées et les petits portables) : chaque scène garde ses images mais allège son
    /// travail (moins de triangles, de particules, d'étoiles, de pas de calcul). Points comparables seulement entre passes légères.
    /// </summary>
    public bool Light { get; init; }

    /// <summary>Définitions de compilation des shaders : MAUS_LIGHT en mode léger.</summary>
    public (string Name, string Value)[] Defines => Light ? [("MAUS_LIGHT", "1")] : [];

    /// <summary>Constantes de l'image en cours (registre b0), déjà envoyées avant <see cref="BenchScene.Render"/>.</summary>
    public FrameConstants Frame { get; set; }

    /// <summary>
    /// Dessine un triangle plein écran en bandes horizontales envoyées une à une : sur une carte lente, aucune commande
    /// ne dure assez pour que Windows croie la carte bloquée (délai de 2 secondes, « TDR »).
    /// </summary>
    public void DrawFullscreenInBands(int bands)
    {
        var cmd = Commands;
        var height = Size.Height;
        for (var i = 0; i < bands; i++)
        {
            var top = height * i / bands;
            var bottom = height * (i + 1) / bands;
            cmd.SetScissor(0, top, Size.Width, bottom);
            cmd.Draw(3);
            if (i < bands - 1)
            {
                cmd.Flush();
            }
        }

        cmd.SetScissor(0, 0, Size.Width, Size.Height);
    }
}

/// <summary>Un test de la carte graphique : une scène animée, rendue image par image pendant la mesure.</summary>
internal abstract class BenchScene : IDisposable
{
    /// <summary>Identifiant stable (résultats, ligne de commande).</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    /// <summary>Ce que la scène sollicite, en une phrase.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Durée mesurée, en secondes (1 min 36 par scène : 8 minutes de carte graphique pour les cinq scènes).</summary>
    public virtual double Duration => 96;

    /// <summary>Vrai si la scène dessine aussi des effets transparents après l'anticrénelage (feu, fumée).</summary>
    public virtual bool HasOverlay => false;

    /// <summary>Effets transparents dessinés après l'anticrénelage, dans l'image HDR <paramref name="target"/>.</summary>
    public virtual void RenderOverlay(SceneContext context, ITexture target)
    {
    }

    /// <summary>Temps du trajet de caméra : le trajet entier est parcouru pendant la durée de la scène.</summary>
    protected double PathTime(double time, CameraPath path) => time * path.Duration / Duration;

    public abstract void Load(SceneContext context);

    /// <summary>État de la scène au temps <paramref name="time"/> (secondes depuis le début de la scène).</summary>
    public abstract SceneState Evaluate(double time);

    /// <summary>Dessine l'image : écrit l'image HDR, le mouvement (et la profondeur si besoin) de <see cref="PostProcess"/>.</summary>
    public abstract void Render(SceneContext context);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
