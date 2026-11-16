using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>One fog volume as volumetric_inject.comp reads it, std430. Must match FogVolume in fog_volumes.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuFogVolume
{
    /// <summary>From the world into the unit shape.</summary>
    public Matrix4x4 WorldToLocal;

    /// <summary>The colour in xyz and the density per metre in w.</summary>
    public Vector4 ColorDensity;

    /// <summary>x the <see cref="FogShape"/>, y the edge fade, z the noise, w 1 over the noise size.</summary>
    public Vector4 ShapeFadeNoise;

    /// <summary>The wind in xyz, and in w the radius of a sphere round the centre that holds the whole shape.</summary>
    public Vector4 WindBounds;

    /// <summary>The shape's centre in the world in xyz.</summary>
    public Vector4 Centre;
}

/// <summary>
/// Packs the frame's fog volumes for the shaders, and works out a volume's density at a point the way
/// fogVolumeDensity() in fog_volumes.glsl does, without the noise, so the tests can check the shapes.
/// </summary>
internal static class FogVolumePack
{
    /// <summary>The bytes one volume takes in the buffer.</summary>
    public static readonly int Stride = Marshal.SizeOf<GpuFogVolume>();

    // Each unit shape fits in the cube from −0.5 to 0.5, which fits in a sphere of this radius.
    private static readonly float UnitBounds = MathF.Sqrt(3f) / 2f;

    /// <summary>Writes each volume into <paramref name="target"/>, which holds at least as many.</summary>
    public static void Pack(ReadOnlySpan<FogVolumeDraw> volumes, Span<GpuFogVolume> target)
    {
        for (int i = 0; i < volumes.Length; i++)
        {
            target[i] = Pack(volumes[i]);
        }
    }

    private static GpuFogVolume Pack(in FogVolumeDraw volume)
    {
        var t = volume.Transform;
        Matrix4x4.Invert(t, out var inverse);
        var s = volume.Settings;
        float longest = MathF.Max(
            new Vector3(t.M11, t.M12, t.M13).Length(),
            MathF.Max(new Vector3(t.M21, t.M22, t.M23).Length(), new Vector3(t.M31, t.M32, t.M33).Length()));

        return new GpuFogVolume
        {
            WorldToLocal = inverse,
            ColorDensity = new Vector4(s.Color, s.Density),
            ShapeFadeNoise = new Vector4((float)volume.Shape, s.EdgeFade, s.Noise, 1f / s.NoiseSize),
            WindBounds = new Vector4(s.Wind, longest * UnitBounds),
            Centre = new Vector4(t.Translation, 0f),
        };
    }

    /// <summary>The volume's density per metre at a point in the world, with no noise: 0 outside, rising over the edge fade.</summary>
    public static float Density(in FogVolumeDraw volume, Vector3 position, float time)
    {
        Matrix4x4.Invert(volume.Transform, out var inverse);
        float inner = Inner(volume.Shape, Vector3.Transform(position, inverse));
        if (inner < 0f)
        {
            return 0f;
        }

        float fade = volume.Settings.EdgeFade;
        float edge = fade > 0f ? Math.Clamp(inner / fade, 0f, 1f) : 1f;
        return volume.Settings.Density * edge;
    }

    // How far in from the shape's surface a local point is, 0 on it, 1 at the middle and below 0 outside.
    private static float Inner(FogShape shape, Vector3 p)
    {
        float across = new Vector2(p.X, p.Z).Length();
        float ends = 1f - 2f * MathF.Abs(p.Y);
        return shape switch
        {
            FogShape.Box => 1f - 2f * MathF.Max(MathF.Abs(p.X), MathF.Max(MathF.Abs(p.Y), MathF.Abs(p.Z))),
            FogShape.Ellipsoid => 1f - 2f * p.Length(),
            FogShape.Cylinder => MathF.Min(1f - 2f * across, ends),
            _ => MathF.Min(1f - across / MathF.Max(0.5f * (0.5f - p.Y), 1e-6f), ends),
        };
    }
}
