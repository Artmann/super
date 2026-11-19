namespace Rendition;

/// <summary>What every step of a frame reads: the shared stores, what lights the scene, the stages and the targets.</summary>
internal sealed class FrameParts(SceneStores stores, SceneLighting lights, FrameStages stages, FrameTargets targets)
{
    /// <summary>The shared stores.</summary>
    public SceneStores SceneStores => stores;

    /// <summary>The shadow maps, the sky, the probes and the fog.</summary>
    public SceneLighting SceneLighting => lights;

    /// <summary>The stages a frame is drawn through.</summary>
    public FrameStages FrameStages => stages;

    /// <summary>The swapchain and the scene's targets.</summary>
    public FrameTargets FrameTargets => targets;
}
