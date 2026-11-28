using Rendition.Vulkan;

namespace Rendition;

/// <summary>Makes the renderer's assets and bakes, which lean on each other.</summary>
internal static class AssetsAndBakes
{
    /// <summary>
    /// Makes the bakes' parts, where bakes are read from, the chunk bakes on the GPU, the frame's baked areas, the
    /// jobs that bake and the bakes made in the background, and the assets, which stop the background bake when it
    /// reads what they let go.
    /// </summary>
    public static (RendererBaking Baking, RendererAssets Assets) Make(FrameParts parts)
    {
        var (stores, sky, stages) = (parts.SceneStores, parts.SceneLighting.EnvironmentSky, parts.FrameStages);
        var (context, resources, retired) = (stores.Context, stores.Resources, stores.Retired);

        // The space bake needs the textures, and the textures need the background bake made from it. Before it is
        // made there is no bake to stop.
        BackgroundBakes? background = null;
        void StopBakesThat(Func<BakeJob, bool> reads) => background?.StopIfReads(reads);
        var textures = new TextureAssets(stores, StopBakesThat, stages.FinishStage);

        // Bakes are asked through this, so a bake that can't be read counts as missing until the store reloads.
        var readableBakes = new ReadableBakes();
        var chunkPool = new ChunkBakePool(context, resources, ChunkBakePool.ChunkUploadBudget, readableBakes.ChunkUnreadable);
        var areaInputs = new AreaInputs(stores.Meshes.PartBounds, () => sky.Hash);
        var spaceBake = new SpaceBake(readableBakes, data => textures.Create(data, TextureEdges.Clamp), textures.Destroy);
        var frameBakes = new FrameBakes(readableBakes, chunkPool, retired, areaInputs, spaceBake);
        var bakeJobs = new BakeJobs(context, new PreparedBakers(context, sky.Map, () => new Baker(context, stores)),
            stores.Meshes, resources, areaInputs);
        background = new BackgroundBakes((context, stores.Gpu), stores.Ring, retired, bakeJobs,
            new BackgroundBakes.BakeWants(frameBakes.Resolver, readableBakes, areaInputs.Lights));
        var baking = new RendererBaking((context, stores.Uploads, sky), frameBakes, (bakeJobs, background, chunkPool));

        var assets = new RendererAssets(new MeshAssets(stores, StopBakesThat),
            textures, new MaterialAssets(stores, StopBakesThat, stages.OpaqueStage.Pass, textures));
        return (baking, assets);
    }
}
