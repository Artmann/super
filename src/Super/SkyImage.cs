using System.Numerics;

namespace Super;

/// <summary>
/// A photographed sky, cooked from an equirectangular Radiance .hdr. <see cref="Content.Sky"/> hands these out, and
/// <see cref="SkySettings.Image"/> shows one. See "A photographed sky" in docs/rendering.md.
/// </summary>
public sealed class SkyImage
{
    internal SkyImage(int index, string name, Vector3 sunDirection)
    {
        Index = index;
        Name = name;
        SunDirection = sunDirection;
    }

    internal int Index { get; }

    /// <summary>The name it was loaded by, such as <c>skies/meadow.hdr</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Towards the sun in the photograph, before any <see cref="SkySettings.Rotation"/>, or zero when the cooker found
    /// none. Aim the view's sun here, with <see cref="SunDirectionAt"/>, so the shadows fall the way the picture's do.
    /// </summary>
    public Vector3 SunDirection { get; }

    /// <summary>Whether the cooker found a sun in the photograph.</summary>
    public bool HasSun => SunDirection != Vector3.Zero;

    /// <summary>Towards the photograph's sun with the sky turned by <paramref name="rotation"/>, as <see cref="SkySettings.Rotation"/> turns it.</summary>
    public Vector3 SunDirectionAt(float rotation) =>
        Vector3.Transform(SunDirection, Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotation));
}
