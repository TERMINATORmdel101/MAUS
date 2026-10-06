using System.Numerics;
using System.Runtime.InteropServices;

namespace Maus.Bench.Render;

/// <summary>Constantes de l'image (registre b0), dans l'ordre exact du cbuffer FrameConstants de common.hlsli.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Matrix4x4 ViewProjNoJitter;
    public Matrix4x4 PrevViewProjNoJitter;
    public Matrix4x4 InvView;
    public Vector3 CameraPos;
    public float Time;
    public Vector2 Resolution;
    public Vector2 InvResolution;
    public Vector2 Jitter;
    public float FrameIndex;
    public float DeltaTime;
    public Vector3 SunDir;
    public float Exposure;
    public Vector3 SunColor;
    public float SceneTime;
    public Vector4 Params0;
    public Vector4 Params1;
    public Vector4 Params2;
    public Vector4 Params3;
}

/// <summary>Position et visée de la caméra à un instant donné.</summary>
internal readonly record struct CameraPose(Vector3 Position, Vector3 Target, float FovYDegrees = 60f, float RollDegrees = 0f)
{
    public Matrix4x4 ViewMatrix()
    {
        var forward = Vector3.Normalize(Target - Position);
        var up = MathF.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        if (RollDegrees != 0f)
        {
            up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, RollDegrees * MathF.PI / 180f));
        }

        return Matrix4x4.CreateLookAt(Position, Target, up);
    }

    public static CameraPose Lerp(CameraPose a, CameraPose b, float t) => new(
        Vector3.Lerp(a.Position, b.Position, t),
        Vector3.Lerp(a.Target, b.Target, t),
        a.FovYDegrees + ((b.FovYDegrees - a.FovYDegrees) * t),
        a.RollDegrees + ((b.RollDegrees - a.RollDegrees) * t));
}

/// <summary>Trajet de caméra : points clés reliés par des courbes de Catmull-Rom (passage exact par chaque point).</summary>
internal sealed class CameraPath
{
    private readonly (double Time, CameraPose Pose)[] _keys;

    public CameraPath(params (double Time, CameraPose Pose)[] keys)
    {
        if (keys.Length < 2)
        {
            throw new ArgumentException("Il faut au moins deux points clés.", nameof(keys));
        }

        _keys = keys;
    }

    public double Duration => _keys[^1].Time;

    public CameraPose Evaluate(double time)
    {
        if (time <= _keys[0].Time)
        {
            return _keys[0].Pose;
        }

        if (time >= _keys[^1].Time)
        {
            return _keys[^1].Pose;
        }

        var i = 0;
        while (i < _keys.Length - 2 && time > _keys[i + 1].Time)
        {
            i++;
        }

        var p0 = _keys[Math.Max(i - 1, 0)].Pose;
        var p1 = _keys[i].Pose;
        var p2 = _keys[i + 1].Pose;
        var p3 = _keys[Math.Min(i + 2, _keys.Length - 1)].Pose;
        var span = _keys[i + 1].Time - _keys[i].Time;
        var t = (float)((time - _keys[i].Time) / span);

        // Mouvement adouci aux extrémités du trajet seulement (départ et arrivée sans à-coup).
        if (i == 0)
        {
            t = EaseIn(t);
        }

        if (i == _keys.Length - 2)
        {
            t = EaseOut(t);
        }

        return new CameraPose(
            CatmullRom(p0.Position, p1.Position, p2.Position, p3.Position, t),
            CatmullRom(p0.Target, p1.Target, p2.Target, p3.Target, t),
            p1.FovYDegrees + ((p2.FovYDegrees - p1.FovYDegrees) * Smooth(t)),
            p1.RollDegrees + ((p2.RollDegrees - p1.RollDegrees) * Smooth(t)));
    }

    private static float Smooth(float t) => t * t * (3f - (2f * t));

    private static float EaseIn(float t) => t * t * (2f - t) * 0.5f + (t * 0.5f);

    private static float EaseOut(float t) => 1f - EaseIn(1f - t);

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * ((2f * p1) + ((-p0 + p2) * t) + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }
}

/// <summary>Suite de Halton (bases 2 et 3) : décalages sous-pixel de l'anticrénelage temporel.</summary>
internal static class Halton
{
    public static Vector2 Jitter(long frame)
    {
        var index = (int)(frame % 16) + 1;
        return new Vector2(Sequence(index, 2) - 0.5f, Sequence(index, 3) - 0.5f);
    }

    private static float Sequence(int index, int radix)
    {
        var result = 0f;
        var fraction = 1f / radix;
        while (index > 0)
        {
            result += index % radix * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }
}
