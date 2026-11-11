using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>
/// Checks a frame's decals and packs them into the buffer <see cref="DecalPass"/> draws from:
/// each box, the inverse that takes the world back into it, and its weights.
/// </summary>
public static class DecalBatch
{
    /// <summary>How many decals one frame can draw. More throws.</summary>
    public const int MaxDecals = 256;

    /// <summary>
    /// Throws when a decal can't be drawn: a box squashed flat, which has no inside to project
    /// through, or a blend weight outside 0 to 1, named in the message.
    /// </summary>
    public static void Check(in Matrix4x4 box, DecalBlend blend)
    {
        if (!Matrix4x4.Invert(box, out _) || !IsFinite(box))
        {
            throw new ArgumentException(
                "The decal's box can't be inverted: it is squashed flat on some axis, or holds a number that isn't finite. "
                + "Give it a width, a height and a depth.",
                nameof(box));
        }

        CheckWeight(blend.Color, nameof(DecalBlend.Color));
        CheckWeight(blend.Normal, nameof(DecalBlend.Normal));
        CheckWeight(blend.Roughness, nameof(DecalBlend.Roughness));
        CheckWeight(blend.AngleFade, nameof(DecalBlend.AngleFade));
    }

    /// <summary>
    /// Checks each decal and writes it into <paramref name="into"/>, which must hold as many.
    /// Throws past <see cref="MaxDecals"/>.
    /// </summary>
    internal static void Pack(ReadOnlySpan<DecalDraw> decals, Span<GpuDecal> into)
    {
        if (decals.Length > MaxDecals)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame can draw at most {MaxDecals} decals, but it was given {decals.Length}."), nameof(decals));
        }

        for (int i = 0; i < decals.Length; i++)
        {
            ref readonly var decal = ref decals[i];
            Check(decal.Box, decal.Blend);
            Matrix4x4.Invert(decal.Box, out var inverse);
            var blend = decal.Blend;
            into[i] = new GpuDecal
            {
                Box = decal.Box,
                Inverse = inverse,
                Weights = new Vector4(blend.Color, blend.Normal, blend.Roughness, blend.AngleFade),
                Material = (uint)decal.Material.Index,
            };
        }
    }

    private static void CheckWeight(float weight, string name)
    {
        if (!(weight >= 0f && weight <= 1f))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The decal's blend {name} is {weight}, but it must be from 0 to 1."), "blend");
        }
    }

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}

/// <summary>One decal as the shaders read it, one instance of the box. Must match decal.vert.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDecal
{
    public Matrix4x4 Box;
    public Matrix4x4 Inverse;

    /// <summary>The blend's colour, normal and roughness weights, and its angle fade.</summary>
    public Vector4 Weights;
    public uint Material;
    private uint _pad0;
    private uint _pad1;
    private uint _pad2;
}
