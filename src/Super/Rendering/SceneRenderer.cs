using System.Numerics;
using Rendition;
using Super.Profiling;

namespace Super;

/// <summary>
/// Turns what a <see cref="View"/> wrote down into Rendition draws. Each frame it brings the content's items up to
/// date on the GPU, then has its parts make the frame: <see cref="DressedDraws"/> the draws, <see cref="DecalMaterials"/>
/// the decals and <see cref="FrameMaker"/> the rest of the <see cref="FrameView"/>.
/// </summary>
internal sealed class SceneRenderer : IDisposable
{
    private const float DefaultRoughness = 0.4f;

    private readonly UploadedItems _items;
    private readonly ItemUploads _uploads;
    private readonly DressedDraws _draws;
    private readonly DecalMaterials _decals;
    private readonly OverlayUpkeep _overlay;
    private readonly FrameMaker _frames;
    private readonly ReflectionProbeUpdater _probes;

    public SceneRenderer(RendererOptions options)
    {
        Renderer = new Renderer(options);
        Baking = new SceneBakes(Renderer);

        // Shots, recordings and profiles show each bake whole from the first frame; play in a window fades them in.
        Renderer.Baking.FadeSeconds = 0f;
        // White, so a draw's color comes out exactly as asked. The built-in shapes are greyed where they're drawn.
        var white = Renderer.CreateMaterial(new Material { BaseColor = new Vector4(Vector3.One, 1f), Roughness = DefaultRoughness });
        _items = new UploadedItems(Renderer, white, (prepared, content) => _uploads!.Upload(prepared, content));
        _uploads = new ItemUploads(Renderer, _items);
        _draws = new DressedDraws(Renderer, _items);
        _decals = new DecalMaterials(Renderer, _items);
        _overlay = new OverlayUpkeep(Renderer, _items);
        _frames = new FrameMaker(Renderer, _items, _draws, _decals);
        _probes = new ReflectionProbeUpdater(Renderer);
    }

    /// <summary>The renderer the scene is drawn with, for its settings and what it reports.</summary>
    public Renderer Renderer { get; }

    /// <summary>Where the scene's light bakes are read from and saved to.</summary>
    internal SceneBakes Baking { get; }

    /// <summary>The effects left out of every frame from now on.</summary>
    public RenderEffects Off { get; set; }

    /// <summary>What every frame shows: the lit scene, or a look inside the renderer. See <see cref="Rendition.DebugView"/>.</summary>
    public DebugView DebugView { get; set; }

    public void Render(View view, Content content, int width, int height, float time)
    {
        var frame = Ready(view, content, new FrameRequest(width, height, time));
        _probes.Update(view, frame, Off);
        Renderer.Render(frame, _draws.Shown);
        Baking.SayWhyNotBaking(view, Off);
    }

    public byte[] Capture(View view, Content content, int width, int height, float time, out int capturedWidth, out int capturedHeight)
    {
        var frame = Ready(view, content, new FrameRequest(width, height, time));
        _probes.Update(view, frame, Off);
        return Renderer.Capture(frame, _draws.Shown, out capturedWidth, out capturedHeight);
    }

    /// <summary>
    /// Bakes the view's baked space from the same frame <see cref="Render"/> would draw, and returns the
    /// <c>.lightbake</c> file's bytes. See <see cref="RendererBaking.Bake"/>.
    /// </summary>
    public byte[] Bake(View view, Content content, FrameRequest request, BakeSettings settings) =>
        Renderer.Baking.Bake(Ready(view, content, request), _draws.Shown, settings);

    /// <summary>
    /// Bakes chunk <paramref name="chunk"/> of the view from the same frame <see cref="Render"/> would draw, and returns
    /// the <c>.lightbake</c> file's bytes. See <see cref="RendererBaking.BakeChunk"/>.
    /// </summary>
    public byte[] BakeChunk(View view, Content content, FrameRequest request, int chunk, BakeSettings settings) =>
        Renderer.Baking.BakeChunk(Ready(view, content, request), _draws.Shown, chunk, settings);

    /// <summary>
    /// Adds up what the view drew, model by model. Copies with different mesh masks are
    /// counted apart, since each one costs only the triangles it shows.
    /// </summary>
    internal List<ModelCost> CountModels(View view, Content content)
    {
        var copies = new Dictionary<(int Model, MeshMask? Meshes), int>();
        foreach (ref readonly var draw in view.Drawn)
        {
            var key = (draw.Model.Index, draw.Meshes);
            copies[key] = copies.GetValueOrDefault(key) + 1;
        }

        var costs = new List<ModelCost>();
        foreach (var ((model, mask), count) in copies)
        {
            string name = UploadedItems.NameOf(model, content, "generated mesh");
            costs.Add(new ModelCost(name, count, MeshMaskParts.ShownTriangles(_items.ModelFor(model, content).Parts, mask)));
        }

        return costs;
    }

    public void Dispose()
    {
        Renderer.Dispose();

        // A bake still being written would leave a temp file in the sources if the process ended under it.
        Baking.WaitForSaver();
    }

    /// <summary>Brings the content up to date on the GPU, then makes the frame the view asks for.</summary>
    private FrameView Ready(View view, Content content, FrameRequest request)
    {
        Baking.Follow(content);
        _items.Update(content);

        // Decal materials point at the textures being freed, so they go too, and are made again when next used.
        if (content.Store.PendingUnloads.Length > 0)
        {
            _decals.DestroyAll();
        }

        _uploads.UnloadPending(content);
        _frames.KeepEnvironment(view, Off);
        _overlay.Update(view, content);
        _draws.Fill(view, content);
        _decals.Fill(view, content, Off);
        return _frames.Create(view, content, request, Off, DebugView);
    }
}
