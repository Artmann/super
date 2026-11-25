using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Names a fragment shader. <see cref="Content.Shader"/> hands these out.</summary>
public readonly record struct ShaderRef(int Index);

/// <summary>Names a texture. <see cref="Content.Texture(string)"/> hands these out.</summary>
public readonly record struct TextureRef(int Index)
{
    /// <summary>
    /// The texture where a Rendition setting asks for one, such as <see cref="ScreenEffects.Canvas"/>. It still names the
    /// content's texture; the engine swaps in the renderer's own when it draws the frame.
    /// </summary>
    public static implicit operator TextureHandle(TextureRef texture) => new(texture.Index);
}

/// <summary>Names a stack of same-sized textures. <see cref="Content.TextureArray"/> hands these out.</summary>
public readonly record struct TextureArrayRef(int Index);

/// <summary>A shader the game named, read from shaders/&lt;name&gt;.spv beside the program.</summary>
internal sealed record ShaderItem(string Name);

/// <summary>
/// A texture the game named: a cooked texture, or pixels made in code. A cooked texture's kind and
/// normal-map flag come from its header, so <see cref="Kind"/> and <see cref="NormalMap"/> only matter for pixels made in code.
/// </summary>
internal sealed record TextureItem(string? Name, GeneratedTexture? Generated, IContentFiles Files, TextureKind Kind, bool NormalMap);

/// <summary>A texture array the game named, from textures named before it.</summary>
internal sealed record TextureArrayItem(TextureItem[] Layers);

/// <summary>A photographed sky the game named, read from its cooked file.</summary>
internal sealed record SkyItem(string Name, IContentFiles Files);

/// <summary>An artist's colour table the game named, read from its cooked file.</summary>
internal sealed record LutItem(string Name, IContentFiles Files);

/// <summary>Stands where an unloaded item was, so its data can be collected while its index stays taken.</summary>
internal sealed record UnloadedItem
{
    public static UnloadedItem Instance { get; } = new();
}

/// <summary>
/// Names what a game wants to draw, and reads its JSON files. It touches no GPU, so games
/// run the same with no window. The window loads what's named here in the background, and
/// a loading screen watches <see cref="IsLoaded"/> and <see cref="Progress"/>. Headless
/// runs, screenshots, recordings and profiles report everything loaded at once, so the
/// speed of a disk can't change what a seed does. Sounds, interface images and what a model is made of have their
/// own parts: <see cref="Sounds"/>, <see cref="UiImages"/> and <see cref="Models"/>.
/// </summary>
public sealed class Content
{
    /// <summary>Reads cooked content from the given files, or from the cooked folder beside the program if there are none.</summary>
    public Content(IContentFiles? files = null)
    {
        Store = new ContentItems(files ?? CookedFolder.BesideProgram());
        Data = new ContentReader(this, Store, () => Store.Files is CookedFolder { Dev: { } dev } folder ? (folder.Folder, dev) : null);
        Sounds = new ContentSounds(Store.Files, AmbienceReader.Read);
        UiImages = new ContentUiImages(Store);
        Fonts = new ContentFonts(Store.Files);
        Models = new ContentModels(Store, new AnimatorRunner(Sounds.Sound));
    }

    /// <summary>Everything named, by index, and what was let go. The window reads and loads the items through it.</summary>
    internal ContentItems Store { get; }

    /// <summary>The catalogs read with <see cref="LoadAll"/>, their schemas, and reading them again as the files change.</summary>
    internal ContentReader Data { get; }

    /// <summary>The sounds and ambiences the game named.</summary>
    public ContentSounds Sounds { get; }

    /// <summary>The pictures the game shows in its interface.</summary>
    public ContentUiImages UiImages { get; }

    /// <summary>The typefaces the game's interface uses besides the built-in one.</summary>
    public ContentFonts Fonts { get; }

    /// <summary>A named model's meshes by name, whether it's ready to draw, and letting it go.</summary>
    public ContentModels Models { get; }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => Store.Loading.IsLoaded;

    /// <summary>
    /// How far loading has got, from 0 to 1. It counts what's been named since the last
    /// time everything was loaded, so a second loading screen starts again from 0.
    /// </summary>
    public float Progress => Store.Loading.Fraction;

    /// <summary>
    /// Names a cooked model by its name: the source's path and extension, such as <c>props/log.glb</c>,
    /// with <c>#part</c> for one part of it. Naming it again gives the same one. A name that isn't in the
    /// cooked content, or isn't a model, stops here with a <see cref="ContentException"/>.
    /// </summary>
    public ModelRef Model(string name)
    {
        if (!Store.TryNamed(name, out ModelRef model))
        {
            ContentNames.Require(Store.Files, name, ContentKind.Model);
            model = Store.Add(name);
            Store.Remember(name, model);
        }

        return model;
    }

    /// <summary>
    /// Reads a cooked model's skeleton and clips, by its name, so a game can build its
    /// <see cref="AnimationController{TState, TAction}"/> at once. The rig sits at the front of
    /// the cooked model, so this is quick, and it's read on the calling thread. It names the model
    /// too, and the game loop moves on every animator started from it. Asking again gives the
    /// same rig. A model cooked without bones stops here with an <see cref="InvalidOperationException"/>.
    /// </summary>
    public Rig Rig(string name)
    {
        if (Store.TryNamed(name, out Rig rig))
        {
            return rig;
        }

        ContentNames.Require(Store.Files, name, ContentKind.Model);
        // Streamed: the rig is at the front, so the rest of the model is never read.
        Skeleton? skeleton;
        AnimationClip[] clips;
        string[] meshNames;
        using (var stream = Store.Files.Open(name))
        {
            (skeleton, clips) = ModelFile.ReadRig(stream, name, out meshNames);
        }

        if (skeleton is null)
        {
            throw new InvalidOperationException($"{name} was cooked without a skeleton, so it has no rig to animate.");
        }

        rig = new Rig(skeleton, clips) { Model = Model(name) };

        // The mesh names sit just before the rig, so they're kept for Meshes, which then needn't open the file again.
        if (meshNames.Length > 0)
        {
            Models.RememberMeshNames(rig.Model.Value.Index, meshNames);
        }

        // Hands the rig's animators to the game loop.
        rig.Runner = Models.Animators;
        Store.Remember(name, rig);
        return rig;
    }

    /// <summary>
    /// Names a photographed sky by its name, such as <c>skies/meadow.hdr</c>, for <see cref="SkySettings.Image"/>. Its
    /// sun's direction is read here, on the calling thread; the rest loads with the other content. Naming it again
    /// gives the same one. A name that isn't a cooked sky stops here with a <see cref="ContentException"/> naming it.
    /// </summary>
    public SkyImage Sky(string name)
    {
        if (!Store.TryNamed(name, out SkyImage sky))
        {
            ContentNames.Require(Store.Files, name, ContentKind.Data);
            Vector3 sun;
            try
            {
                using var stream = Store.Files.Open(name);
                sun = SkyImageData.ReadSunDirection(stream, name);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                throw new ContentException([$"Could not read the sky {name}: {problem.Message}"]);
            }

            sky = new SkyImage(Store.Add(new SkyItem(name, Store.Files)).Index, name, sun);
            Store.Remember(name, sky);
        }

        return sky;
    }

    /// <summary>
    /// Names an artist's colour table by its name, such as <c>grades/dusk.cube</c> or <c>grades/dusk.lut.png</c>, for
    /// <see cref="ColorGrade.Lut"/>. It loads with the other content, and naming it again gives the same one. A name
    /// that isn't cooked stops here with a <see cref="ContentException"/> naming it. See "A colour table" in
    /// docs/rendering.md.
    /// </summary>
    public ColorLut Lut(string name)
    {
        if (!Store.TryNamed(name, out ColorLut lut))
        {
            ContentNames.Require(Store.Files, name, ContentKind.Data);
            lut = new ColorLut(Store.Add(new LutItem(name, Store.Files)).Index);
            Store.Remember(name, lut);
        }

        return lut;
    }

    /// <summary>
    /// Lets go of a colour table. The window frees it once the frames that read it are done, and a picture whose
    /// <see cref="ColorGrade.Lut"/> still names it afterwards throws. Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(ColorLut lut)
    {
        var item = ItemChecks.Require<LutItem>(Store, lut.Index, nameof(lut), "colour table");
        Store.MarkUnloaded(lut.Index);
        Store.ForgetName<ColorLut>(item.Name);
    }

    /// <summary>
    /// Hands over a surface made by code. Anything its surface names must have been named
    /// first, which also means it's loaded first. A mesh that will be drawn static must be made
    /// with <paramref name="isStatic"/>, which gives it lightmap UVs once, here, through
    /// <see cref="GeneratedMesh.WithLightmapUvs"/>, so what's uploaded and what its bake hashes
    /// are the same mesh. Drawing a mesh made without it static throws on the next render.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="isStatic"/> is set and the mesh's charts don't fit even <see cref="LightmapUnwrap.LargestSize"/>,
    /// or its <see cref="Vertex.Uv2"/> doesn't fit a lightmap but its surface reads the occlusion texture through it.
    /// </exception>
    public ModelRef Mesh(GeneratedMesh mesh, bool isStatic = false)
    {
        ItemChecks.RequireSurface(Store, mesh.Surface);
        if (!isStatic)
        {
            var model = Store.Add(mesh);
            Store.Identities.MarkDynamic(model.Index);
            return model;
        }

        return Store.Add(mesh.WithLightmapUvs());
    }

    /// <summary>
    /// Names a fragment shader compiled from the game's Shaders folder, by file name
    /// without extension. Naming it again gives the same one.
    /// </summary>
    public ShaderRef Shader(string name)
    {
        if (!Store.TryNamed(name, out ShaderRef shader))
        {
            shader = new ShaderRef(Store.Add(new ShaderItem(name)).Index);
            Store.Remember(name, shader);
        }

        return shader;
    }

    /// <summary>
    /// Names a cooked texture by its name, such as <c>textures/grass.png</c>. Naming it again
    /// gives the same one. Whether it's colour or linear data, and whether it's a normal map,
    /// is set where it's cooked, in <c>cook.json</c>. A name that isn't a cooked texture stops
    /// here with a <see cref="ContentException"/>.
    /// </summary>
    public TextureRef Texture(string name)
    {
        if (!Store.TryNamed(name, out TextureRef texture))
        {
            ContentNames.Require(Store.Files, name, ContentKind.Texture);
            texture = new TextureRef(Store.Add(new TextureItem(name, null, Store.Files, TextureKind.Color, false)).Index);
            Store.Remember(name, texture);
        }

        return texture;
    }

    /// <summary>
    /// A surface for a cooked material graph or variant, such as <c>materials/bricks.material.cs</c>.
    /// It holds the graph's colour, normal and surface textures and its width in metres, which
    /// shapes tile at unless given <c>uvPerMetre</c>. Colour, roughness and metallic are 1, because
    /// the surface texture's values pass straight through. Naming it again gives the same one, without
    /// reading the material again. A name that isn't a cooked material, or a material whose width
    /// isn't a positive number, stops here with a <see cref="ContentException"/>.
    /// </summary>
    public Surface Surface(string name)
    {
        if (Store.TryNamed(name, out Surface known))
        {
            return known;
        }

        float width = CookedBytes.MaterialWidth(Store.Files, name);
        var surface = new Surface
        {
            Color = Vector3.One,
            Roughness = 1f,
            Metallic = 1f,
            ColorTexture = Texture(name + "#color"),
            NormalTexture = Texture(name + "#normal"),
            SurfaceTexture = Texture(name + "#surface"),
            TileWidth = width,
        };
        Store.Remember(name, surface);
        return surface;
    }

    /// <summary>Names an image made in code. Each call makes a new one.</summary>
    public TextureRef Texture(GeneratedTexture texture, TextureKind kind = TextureKind.Color, bool normalMap = false) =>
        new(Store.Add(new TextureItem(null, texture, Store.Files, kind, normalMap)).Index);

    /// <summary>
    /// Stacks textures named before into one array that a shader indexes by layer. All
    /// must be the same size.
    /// </summary>
    public TextureArrayRef TextureArray(params ReadOnlySpan<TextureRef> layers)
    {
        if (layers.IsEmpty)
        {
            throw new ArgumentException("A texture array needs at least one layer.", nameof(layers));
        }

        var items = new TextureItem[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            items[i] = ItemChecks.Require<TextureItem>(Store, layers[i].Index, $"{nameof(layers)}[{i}]", "texture");
        }

        return new TextureArrayRef(Store.Add(new TextureArrayItem(items)).Index);
    }

    /// <summary>
    /// Lets go of a texture. The window frees it, and a mesh named afterwards can't use
    /// it. A mesh that names it must be unloaded first, since the window would otherwise
    /// draw with a texture that is gone. Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(TextureRef texture)
    {
        var item = ItemChecks.Require<TextureItem>(Store, texture.Index, nameof(texture), "texture");
        var items = Store.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is GeneratedMesh mesh && ItemChecks.Names(mesh.Surface, texture))
            {
                throw new InvalidOperationException($"Texture {texture.Index} is still named by model {i + 1}. Unload the model first.");
            }
        }

        Store.MarkUnloaded(texture.Index);
        if (item.Name is { } name)
        {
            Store.ForgetName<TextureRef>(name);
        }

        // A surface that holds the texture would hand it out again after it's gone, so it's read afresh next time.
        Store.ForgetWhere<Surface>(surface => ItemChecks.Names(surface, texture));
    }

    /// <summary>
    /// Reads every JSON file under a folder of the content folder, one record per file.
    /// Anything wrong stops here with a <see cref="ContentException"/> listing every
    /// problem. Loading the same folder again hands back the same catalog.
    /// </summary>
    public Catalog<T> LoadAll<T>(string folder)
        where T : notnull => Data.LoadAll<T>(folder);
}
