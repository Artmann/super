using System.Numerics;

namespace Rendition.Passes;

/// <summary>Turns a frame's <see cref="OutlineSettings"/> into the uniforms lighting.frag draws outlines from.</summary>
internal static class Outlines
{
    /// <summary>
    /// The colour and thickness, and the fade distances. The thickness is in the picture's pixels, so it's scaled by
    /// <paramref name="sceneToPicture"/>, how many scene pixels make one of the picture's, to look the same at any
    /// render scale.
    /// </summary>
    public static (Vector4 Outline, Vector4 Fade) Pack(in OutlineSettings settings, float sceneToPicture)
    {
        float thickness = settings.Thickness * sceneToPicture;
        return (new Vector4(settings.Color, thickness), new Vector4(settings.FadeStart, settings.FadeEnd, 0f, 0f));
    }
}
