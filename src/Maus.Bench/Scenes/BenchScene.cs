using System.Numerics;
using Maus.Bench.Gpu;
using Maus.Bench.Render;

namespace Maus.Bench.Scenes;

/// <summary>Capacité de la carte graphique qu'un test met à genoux (pour le bilan « points forts, points faibles »).</summary>
public enum GpuCapability
{
    Geometry,
    Bandwidth,
    Compute,
    Textures,
    Volumetrics,
    Effects,
}

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

    /// <summary>Plan de coupe : l'anticrénelage oublie l'image précédente.</summary>
    public bool Cut { get; init; }

    public float NearPlane { get; init; } = 0.05f;

    public float FarPlane { get; init; } = 2000f;
}

/// <summary>Ce dont une scène a besoin pour dessiner : carte, shaders, cibles communes.</summary>
internal sealed class SceneContext(IGpuDevice device, ShaderLibrary shaders, PostProcess post)
{
    public IGpuDevice Device { get; } = device;

    public ShaderLibrary Shaders { get; } = shaders;

    public PostProcess Post { get; } = post;

    public ICommandList Commands => Device.Commands;

    public RenderSize Size => Post.Size;

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

    public abstract GpuCapability Capability { get; }

    /// <summary>Durée mesurée, en secondes (2 minutes par scène : environ 8 minutes de carte graphique en tout).</summary>
    public virtual double Duration => 120;

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
