using Rendition.Gpu;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>Where scene draws are recorded: the command list, and the bind group each draw's material reads through.</summary>
internal readonly record struct SceneTarget(CommandList Commands, MaterialBinding Materials);

/// <summary>
/// A pass's pipelines for scene draws and the resource table whose materials pick between them. With one entry in
/// <paramref name="Shaded"/> everything draws with it; otherwise each material picks its surface shader. Materials without
/// holes use <see cref="Solids"/> when it's given, picked the same way.
/// </summary>
internal readonly record struct ScenePipelines(List<PipelineVariants> Shaded, ResourceTable Resources)
{
    /// <summary>The pipelines for materials without holes, which need no fragment shader to know their depth; or null.</summary>
    public List<PipelineVariants>? Solids { get; init; }
}

/// <summary>
/// What a pass records of the scene: the draws, the meshes and part details they name, and the buffer each copy's
/// matrix goes into.
/// </summary>
internal readonly ref struct SceneGeometry(
    ReadOnlySpan<DrawItem> draws, List<Mesh?> meshes, List<PartDetail> details, InstanceBuffer instances)
{
    public ReadOnlySpan<DrawItem> Draws { get; } = draws;

    public List<Mesh?> Meshes { get; } = meshes;

    /// <summary>Each part's lowest point, from which materials that sway in the wind sway.</summary>
    public List<PartDetail> Details { get; } = details;

    public InstanceBuffer Instances { get; } = instances;

    /// <summary>The same, with only <paramref name="count"/> of the draws from <paramref name="first"/>.</summary>
    public SceneGeometry Slice(int first, int count) => new(Draws.Slice(first, count), Meshes, Details, Instances);
}

/// <summary>
/// How <see cref="SceneDraws.Record"/> draws: the draws that belong in <paramref name="Pass"/>, for which cascade or atlas
/// tile, whether still, and whether depth first.
/// </summary>
internal readonly record struct SceneDrawOptions(ScenePass Pass)
{
    /// <summary>Which sun cascade the shadow pass is drawing, or from <see cref="ShadowPass.MaxCascades"/> on, which tile of the atlas.</summary>
    public int Cascade { get; init; }

    /// <summary>Whether nothing sways, for views that must not follow the wind.</summary>
    public bool Still { get; init; }

    /// <summary>
    /// When given, the batches it pays for are drawn twice: first all of them for their depth alone, then with the rest
    /// to be shaded, where only their pixels that show are.
    /// </summary>
    public DepthFirst? DepthFirst { get; init; }
}

/// <summary>
/// Copies that all look like the first, drawn as one instanced draw by <see cref="SceneDraws.Draw"/>: the draws at
/// <paramref name="indices"/>, with the meshes they name and the buffer their matrices go into.
/// </summary>
internal readonly ref struct SceneCopies(ReadOnlySpan<DrawItem> draws, ReadOnlySpan<int> indices, List<Mesh?> meshes, InstanceBuffer instances)
{
    public ReadOnlySpan<DrawItem> Draws { get; } = draws;

    public ReadOnlySpan<int> Indices { get; } = indices;

    public List<Mesh?> Meshes { get; } = meshes;

    public InstanceBuffer Instances { get; } = instances;
}
