using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// What a frame draws: the camera's picked draws, the whole scene's, which probes and rays take, and the scene's
/// top-level structure on a frame that traces rays.
/// </summary>
internal readonly ref struct FrameDraws(ReadOnlySpan<DrawItem> camera, ReadOnlySpan<DrawItem> scene, TopLevelStructure? traced = null)
{
    public ReadOnlySpan<DrawItem> Camera { get; } = camera;

    public ReadOnlySpan<DrawItem> Scene { get; } = scene;

    public TopLevelStructure? Traced { get; } = traced;
}
