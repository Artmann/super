using Rendition;

namespace Super;

/// <summary>
/// The content's items as the GPU holds them, by the index each ref carries, and the built-in shapes other than the
/// cube, which sit below zero, outside the content. Items are read on background threads by <see cref="ItemReads"/>
/// and handed to the GPU by the upload given to the constructor.
/// </summary>
internal sealed class UploadedItems
{
    private readonly ContentLoader<ItemOnGpu> _loader;

    /// <summary>The built-in shapes other than the cube, made once and kept for the renderer's life.</summary>
    private readonly ModelOnGpu[] _shapes;

    private IContentFiles? _files;

    /// <summary>Makes the built-in shapes, drawn with <paramref name="material"/>, and the cube, item 0.</summary>
    public UploadedItems(Renderer renderer, MaterialHandle material, Func<object, Content, ItemOnGpu> upload)
    {
        _loader = new ContentLoader<ItemOnGpu>(
            new ModelOnGpu([renderer.Assets.CreateDrawItem(Cube.Vertices, Cube.Indices, material)], null, null),
            item => ItemReads.Prepare(item, _files!),
            upload);
        _shapes = Array.ConvertAll(
            Shapes.BuiltIn(),
            mesh => new ModelOnGpu([renderer.Assets.CreateDrawItem(mesh.Vertices, mesh.Indices, material)], null, null));
    }

    /// <summary>Starts reading anything newly named, then uploads what this frame should.</summary>
    public void Update(Content content)
    {
        // Set before anything new starts preparing, since models are read through it on background threads.
        _files = content.Store.Files;
        _loader.Update(content);
    }

    /// <summary>Takes what an item made, leaving a marker that fails clearly if it's used again.</summary>
    public ItemOnGpu? Take(int index, Content content) => _loader.Take(index, ItemLetGo.Instance, content);

    /// <summary>The item at <paramref name="index"/>, waiting for it if it isn't ready. Throws when it was unloaded.</summary>
    public T Require<T>(int index, Content content)
        where T : ItemOnGpu =>
        _loader.Get(index, content) as T
            ?? throw new InvalidOperationException($"Item {index} was unloaded, so nothing can use it.");

    /// <summary>
    /// The item a ref names, by its index and generation. Throws when it was unloaded, even when a later item has
    /// taken its index since.
    /// </summary>
    public T Require<T>(int index, int generation, Content content)
        where T : ItemOnGpu =>
        content.Store.Slots.IsGone(index, generation)
            ? throw new InvalidOperationException($"Item {index} was unloaded, so nothing can use it.")
            : Require<T>(index, content);

    /// <summary>The texture a ref names, or null for none.</summary>
    public TextureHandle? TextureFor(TextureRef? texture, Content content) =>
        texture is { } named ? Require<TextureOnGpu>(named.Index, named.Generation, content).Handle : null;

    /// <summary>A model by its ref's index. The built-in shapes other than the cube sit below zero.</summary>
    public ModelOnGpu ModelFor(int index, Content content) =>
        index < 0 ? _shapes[-index - 1] : Require<ModelOnGpu>(index, content);

    /// <summary>What a model is called in reports: its path, the built-in shape's name, or what it is.</summary>
    public static string NameOf(int index, Content content, string generated) => index switch
    {
        0 => "cube",
        < 0 => Shapes.BuiltInNames[-index - 1],
        _ => content.Store.Items[index - 1] is string path ? path : generated,
    };
}
