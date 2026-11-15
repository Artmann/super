using System.Numerics;

namespace Rendition.Passes;

/// <summary>Turns a frame's <see cref="OutlineSettings"/> into the uniforms lighting.frag draws outlines from.</summary>
internal static class Outlines
{
    /// <summary>
    /// The colour and thickness, and the fade distances. The thickness is in the picture's pixels, so it's scaled by
    /// <paramref name="sceneToPicture"/>, how many scene pixels make one of the picture's, to look the same at any
    /// render scale. A probe capture draws none, since a line one pixel wide on a small cube face is a smear.
    /// </summary>
    public static (Vector4 Outline, Vector4 Fade) Pack(in OutlineSettings settings, float sceneToPicture, bool capturingProbe)
    {
        float thickness = capturingProbe ? 0f : settings.Thickness * sceneToPicture;
        return (new Vector4(settings.Color, thickness), new Vector4(settings.FadeStart, settings.FadeEnd, 0f, 0f));
    }
}
