using System.Numerics;

namespace Rendition.Passes;

/// <summary>Packs the ambient occlusion and screen bounce settings for the frame block.</summary>
internal static class Occlusion
{
    /// <summary>
    /// x: how far a point looks for surfaces that hide the sky, in metres; y: how dark they make it; z: how strong the
    /// bounce from the screen is, 0 when it isn't found this frame; w: how far a point looks for surfaces that light
    /// it, in metres. The bounce is found by the occlusion pass from last frame's picture, so it needs both, and a
    /// probe's capture has no last frame.
    /// </summary>
    public static Vector4 Pack(in OcclusionSettings occlusion, in BounceSettings bounce, RenderEffects off, bool historyValid)
    {
        bool bouncing = historyValid && (off & (RenderEffects.AmbientOcclusion | RenderEffects.ScreenBounce)) == 0;
        return new(occlusion.Radius, occlusion.Strength, bouncing ? bounce.Strength : 0f, bounce.Radius);
    }
}
