using System.Numerics;
using Maus.Bench.Gpu;
using Maus.Bench.Scenes;

namespace Maus.Bench.Render;

/// <summary>
/// Construit les constantes de chaque image : matrices de la caméra (avec le décalage sous-pixel de l'anticrénelage),
/// matrices de l'image précédente pour le mouvement, temps, soleil, paramètres de la scène.
/// </summary>
internal sealed class FrameBuilder
{
    private Matrix4x4 _previousViewProj;
    private bool _hasPrevious;
    private long _frame;

    public FrameConstants Build(SceneState state, RenderSize size, double sceneTime, float deltaTime)
    {
        var view = state.Camera.ViewMatrix();
        var aspect = size.Aspect;
        var fov = state.Camera.FovYDegrees * MathF.PI / 180f;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, state.NearPlane, state.FarPlane);
        var viewProj = view * proj;

        var jitter = Halton.Jitter(_frame);
        var jittered = proj;
        jittered.M31 -= jitter.X * 2f / size.Width;
        jittered.M32 += jitter.Y * 2f / size.Height;
        var viewProjJittered = view * jittered;
        Matrix4x4.Invert(viewProjJittered, out var inverseViewProj);
        Matrix4x4.Invert(view, out var inverseView);

        if (!_hasPrevious || state.Cut)
        {
            _previousViewProj = viewProj;
        }

        var constants = new FrameConstants
        {
            View = view,
            Proj = jittered,
            ViewProj = viewProjJittered,
            InvViewProj = inverseViewProj,
            ViewProjNoJitter = viewProj,
            PrevViewProjNoJitter = _previousViewProj,
            InvView = inverseView,
            CameraPos = state.Camera.Position,
            Time = (float)sceneTime,
            Resolution = new Vector2(size.Width, size.Height),
            InvResolution = new Vector2(1f / size.Width, 1f / size.Height),
            Jitter = jitter,
            FrameIndex = _frame % 4096,
            DeltaTime = deltaTime,
            SunDir = Vector3.Normalize(state.SunDirection),
            Exposure = state.Grade.Exposure,
            SunColor = state.SunColor,
            SceneTime = (float)sceneTime,
            Params0 = state.Params0,
            Params1 = state.Params1,
            Params2 = state.Params2,
            Params3 = state.Params3,
        };

        _previousViewProj = viewProj;
        _hasPrevious = true;
        _frame++;
        return constants;
    }

    /// <summary>Nouvelle scène : pas de mouvement calculé depuis la précédente.</summary>
    public void Reset() => _hasPrevious = false;
}
