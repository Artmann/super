using System.Numerics;

namespace Rendition.Passes;

/// <summary>Packs the ambient occlusion settings for the frame block.</summary>
internal static class Occlusion
{
    /// <summary>x: how far a point looks for surfaces that hide the sky, in metres; y: how dark they make it.</summary>
    public static Vector4 Pack(in OcclusionSettings settings) => new(settings.Radius, settings.Strength, 0f, 0f);
}
