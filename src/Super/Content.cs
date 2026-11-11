using System.ComponentModel;
using System.Runtime.InteropServices;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>Names a fragment shader. <see cref="Content.Shader"/> hands these out.</summary>
public readonly record struct ShaderRef(int Index);

/// <summary>Names a texture. <see cref="Content.Texture(string)"/> hands these out.</summary>
public readonly record struct TextureRef(int Index);

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
/// speed of a disk can't change what a seed does.
/// </summary>
public sealed class Content
{
    /// <summary>How long the cooked manifest must be quiet before a reload, since a cook can write it more than once.</summary>
    private static readonly TimeSpan ReloadQuietTime = TimeSpan.FromMilliseconds(100);

    private readonly List<object> _items = [];
    private readonly List<bool> _itemLoaded = [];
    private readonly HashSet<int> _unloaded = [];
    private readonly List<int> _pendingUnloads = [];
    private readonly Dictionary<string, ModelRef> _models = [];
    private readonly Dictionary<string, Rig> _rigs = [];
    private readonly Dictionary<int, string[]> _meshNames = [];
    private readonly Dictionary<int, ulong> _identities = [];
    private readonly HashSet<int> _dynamicMeshes = [];
    private readonly Dictionary<string, GameImage> _images = [];
    private readonly List<SoundClip> _clips = [];
    private readonly Dictionary<string, SoundRef> _sounds = [];
    private readonly List<AmbienceDefinition> _ambiences = [];
    private readonly Dictionary<string, AmbienceRef> _ambienceRefs = [];
    private readonly Dictionary<string, ShaderRef> _shaders = [];
    private readonly Dictionary<string, TextureRef> _textures = [];
    private readonly Dictionary<string, Surface> _surfaces = [];
    private readonly Dictionary<(Type Type, string Folder), ICatalog> _catalogs = [];
    private readonly List<ContentReference> _waiting = [];
    private readonly ContentReader _reader;
    private readonly IContentFiles _files;
    private ContentWatcher? _watcher;
    private ICookProcess? _cook;
    private volatile bool _reloadWanted;
    private int _loaded;
    private int _batchStart;

    /// <summary>Reads cooked content from the given files, or from the cooked folder beside the program if there are none.</summary>
    public Content(IContentFiles? files = null)
    {
        _files = files ?? CookedFolder.BesideProgram();
        _reader = new ContentReader(this, _files);
    }

    internal IReadOnlyList<object> Items => _items;

    /// <summary>Where the cooked bytes come from. The window reads models and textures through it.</summary>
    internal IContentFiles Files => _files;

    /// <summary>True only in the interactive window, where loading happens over real time.</summary>
    internal bool Streaming { get; set; }

    /// <summary>Reads a surface shader's SPIR-V by name, for the bake identities of models that use it. Tests swap it.</summary>
    internal Func<string, byte[]> ShaderBytes { get; set; } = SceneRenderer.LoadShader;

    /// <summary>Set by tests, to keep a loading screen up for as long as they need it.</summary>
    internal bool Held { get; set; }

    /// <summary>Whether files are being watched. This never happens without a window.</summary>
    internal bool IsLive => _watcher is not null;

    /// <summary>Starts <c>super-cook --watch</c> over the sources into the cooked folder. Tests swap it for a stand-in.</summary>
    internal Func<DevSettings, string, ICookProcess> StartCook { get; set; } = (dev, outFolder) => new CookProcess(dev, outFolder);

    /// <summary>Set when a file changed. The next tick reads everything again.</summary>
    internal bool ReloadWanted
    {
        get => _reloadWanted;
        set => _reloadWanted = value;
    }

    /// <summary>Whether everything named so far is ready to draw.</summary>
    public bool IsLoaded => !Held && (!Streaming || _loaded >= _items.Count);

    /// <summary>
    /// How far loading has got, from 0 to 1. It counts what's been named since the last
    /// time everything was loaded, so a second loading screen starts again from 0.
    /// </summary>
    public float Progress
    {
        get
        {
            if (IsLoaded)
            {
                return 1f;
            }

            return Held ? 0f : (_loaded - _batchStart) / (float)(_items.Count - _batchStart);
        }
    }

    /// <summary>
    /// Names a cooked model by its name: the source's path and extension, such as <c>props/log.glb</c>,
    /// with <c>#part</c> for one part of it. Naming it again gives the same one. A name that isn't in the
    /// cooked content, or isn't a model, stops here with a <see cref="ContentException"/>.
    /// </summary>
    public ModelRef Model(string name)
    {
        if (!_models.TryGetValue(name, out var model))
        {
            ContentNames.Require(_files, name, ContentKind.Model);
            model = Add(name);
            _models.Add(name, model);
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
        if (!_rigs.TryGetValue(name, out var rig))
        {
            ContentNames.Require(_files, name, ContentKind.Model);
            // Streamed: the rig is at the front, so the rest of the model is never read.
            Skeleton? skeleton;
            AnimationClip[] clips;
            string[] meshNames;
            using (var stream = _files.Open(name))
            {
                (skeleton, clips) = ModelData.ReadRig(stream, name, out meshNames);
            }

            if (skeleton is null)
            {
                throw new InvalidOperationException($"{name} was cooked without a skeleton, so it has no rig to animate.");
            }

            rig = new Rig(skeleton, clips) { Model = Model(name) };

            // The mesh names sit just before the rig, so they're kept for Meshes, which then needn't open the file again.
            if (meshNames.Length > 0)
            {
                _meshNames.TryAdd(rig.Model.Value.Index, meshNames);
            }

            Adopt(rig);
            _rigs.Add(name, rig);
        }

        return rig;
    }

    /// <summary>
    /// Picks the meshes of a cooked model that have these names, for a draw to show only those. A
    /// mesh is named by the node that holds it in the source file, the name an artist sees in the
    /// outliner, such as <c>Head_05b</c>. A mesh with several materials is cooked as several pieces
    /// under one name, and a name picks all of them. Names must match exactly. They're read from the
    /// front of the cooked model, so the geometry is never read. A name the model has no mesh called,
    /// an empty list, a model that isn't cooked, or a static model cooked without <c>keepMeshes</c>
    /// stops here with an <see cref="ArgumentException"/> or an <see cref="InvalidOperationException"/>.
    /// Not the same as <c>file#part</c> in a model's name, which is a split made at cook time into
    /// separate models.
    /// </summary>
    public MeshMask Meshes(ModelRef model, params ReadOnlySpan<string> names)
    {
        if (names.IsEmpty)
        {
            throw new ArgumentException("A mesh mask needs at least one mesh name.", nameof(names));
        }

        string[] all = MeshNamesOf(model, nameof(model));
        var kept = new List<int>();
        foreach (string name in names)
        {
            int before = kept.Count;
            for (int part = 0; part < all.Length; part++)
            {
                if (all[part] == name)
                {
                    kept.Add(part);
                }
            }

            if (kept.Count == before)
            {
                throw new ArgumentException(MissingMesh(ModelName(model.Index), name, all), nameof(names));
            }
        }

        return new MeshMask(model.Index, all.Length, CollectionsMarshal.AsSpan(kept));
    }

    /// <summary>
    /// The mesh names of a cooked model, once each, in the order they were cooked, so a game can
    /// see what <see cref="Meshes"/> takes.
    /// </summary>
    public IReadOnlyList<string> MeshNames(ModelRef model) => MeshNamesOf(model, nameof(model)).Distinct().ToArray();

    // One name for each piece the model is drawn in, so a name repeats for a mesh with several materials.
    private string[] MeshNamesOf(ModelRef model, string field)
    {
        if (model.IsBuiltIn)
        {
            throw new ArgumentException("The built-in shapes, such as the cube, are not cooked, so they have no mesh names.", field);
        }

        if (model.Index < 1 || model.Index > _items.Count)
        {
            throw new ArgumentException($"{field} names {model.Index}, which this content never named, so it has no mesh names.", field);
        }

        RequireLoaded(model.Index, field);
        if (_meshNames.TryGetValue(model.Index, out var known))
        {
            return known;
        }

        if (_items[model.Index - 1] is not string name)
        {
            throw new ArgumentException($"{field} names {model.Index}, which was made in code, not cooked, so it has no mesh names.", field);
        }

        string[] names;
        using (var stream = _files.Open(name))
        {
            names = ModelData.ReadMeshNames(stream, name);
        }

        if (names.Length == 0)
        {
            throw new InvalidOperationException(
                $"{name} has no mesh names, because its file was cooked without them. A rigged model always has them. " +
                $"A static model needs \"keepMeshes\": true in {name}.cook.json, and then to be cooked again.");
        }

        _meshNames.Add(model.Index, names);
        return names;
    }

    private string ModelName(int index) => _items[index - 1] as string ?? $"model {index}";

    // The first 20 names are listed, so a model with hundreds of meshes still gives a short message.
    private static string MissingMesh(string model, string name, string[] all)
    {
        const int Shown = 20;
        string[] distinct = all.Distinct().ToArray();
        string list = string.Join(", ", distinct.Take(Shown));
        string more = distinct.Length > Shown ? $", and {distinct.Length - Shown} more" : "";
        return $"{model} has no mesh called {name}. It has {distinct.Length} {(distinct.Length == 1 ? "mesh" : "meshes")}: {list}{more}.";
    }

    /// <summary>The animators the game loop moves on each tick: every one started from a rig this content read.</summary>
    internal AnimatorRunner Animators { get; } = new();

    /// <summary>Hands a rig's animators to the game loop, as <see cref="Rig(string)"/> does. Tests use it for rigs built in code.</summary>
    internal void Adopt(Rig rig) => rig.Runner = Animators;

    /// <summary>
    /// Names an image, by the name of its cooked texture. Naming it again gives the same one.
    /// Its size comes from the cooked header, so layout never waits for it. A name that isn't
    /// a cooked texture stops here with a <see cref="ContentException"/>. It's a catalog's
    /// mistake, not a drawing one.
    /// </summary>
    public GameImage Image(string name)
    {
        if (_images.TryGetValue(name, out var known))
        {
            return known;
        }

        ContentNames.Require(_files, name, ContentKind.Texture);
        byte[] cooked = ReadFile(name, "image");
        var (width, height) = CookedTexture.ReadSize(cooked, name);

        // The item's place is its index, as for models, because index 0 is the built-in cube.
        var image = new GameImage(name, width, height, cooked, _items.Count + 1);
        Add(image);
        _images.Add(name, image);
        return image;
    }

    /// <summary>
    /// Hands over an image painted in code, to show in the interface like a cooked one. Each
    /// call makes a new one, so a picture that changes is painted again and the old one let go
    /// with <see cref="Unload(GameImage)"/>. <paramref name="pixels"/> are rows from the top,
    /// four bytes each (red, green, blue, alpha), in sRGB with straight alpha, and are kept as
    /// they are, so don't change them afterwards.
    /// </summary>
    public GameImage Image(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"A {width} by {height} image takes {width * height * 4} bytes, four a pixel, not {pixels.Length}.", nameof(pixels));
        }

        var image = new GameImage(width, height, pixels, _items.Count + 1);
        Add(image);
        return image;
    }

    /// <summary>
    /// Lets go of an image. The window frees it, and showing it afterwards fails. Naming the
    /// same file again later makes a new one.
    /// </summary>
    public void Unload(GameImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        RequireNamed(image.Index, nameof(image));
        RequireLoaded(image.Index, nameof(image));
        if (!ReferenceEquals(_items[image.Index - 1], image))
        {
            throw new ArgumentException($"{nameof(image)} isn't one this content made.", nameof(image));
        }

        MarkUnloaded(image.Index);
        if (image.Cooked is not null)
        {
            _images.Remove(image.Path);
        }
    }

    /// <summary>
    /// Names a cooked sound by its name, such as <c>sounds/hit.wav</c>. Naming it again gives
    /// the same one. A name that isn't a cooked sound, or can't be read, stops here with a
    /// <see cref="ContentException"/> naming it.
    /// </summary>
    public SoundRef Sound(string name)
    {
        if (!_sounds.TryGetValue(name, out var sound))
        {
            ContentNames.Require(_files, name, ContentKind.Sound);
            _clips.Add(SoundClip.Read(name, ReadFile(name, "sound")));
            sound = new SoundRef(_clips.Count - 1);
            _sounds.Add(name, sound);
        }

        return sound;
    }

    /// <summary>
    /// Names an ambience by the name of its .ambience.json file. Naming it again gives the
    /// same one. Any mistake in the file, or in a sound it names, stops here with a
    /// <see cref="ContentException"/> naming the file.
    /// </summary>
    public AmbienceRef Ambience(string name)
    {
        if (!_ambienceRefs.TryGetValue(name, out var ambience))
        {
            ContentNames.Require(_files, name, ContentKind.Data);
            _ambiences.Add(AmbienceReader.Read(name, ReadFile(name, "ambience"), clipPath => Clip(Sound(clipPath))));
            ambience = new AmbienceRef(_ambiences.Count - 1);
            _ambienceRefs.Add(name, ambience);
        }

        return ambience;
    }

    internal SoundClip Clip(SoundRef sound) => _clips[sound.Index];

    /// <summary>Names a clip made in code, for tests whose clip has to fail or be watched as it plays.</summary>
    internal SoundRef AddClip(SoundClip clip)
    {
        _clips.Add(clip);
        return new SoundRef(_clips.Count - 1);
    }

    internal AmbienceDefinition AmbienceOf(AmbienceRef ambience) => _ambiences[ambience.Index];

    /// <summary>
    /// Hands over a surface made by code. Anything its surface names must have been named
    /// first, which also means it's loaded first. A mesh that will be drawn static must be made
    /// with <paramref name="isStatic"/>, which gives it lightmap UVs once, here, through
    /// <see cref="GeneratedMesh.WithLightmapUvs"/>, so what's uploaded and what its bake hashes
    /// are the same mesh. Drawing a mesh made without it static throws on the next render.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="isStatic"/> is set and the mesh's charts don't fit even <see cref="LightmapUnwrap.LargestSize"/>.
    /// </exception>
    public ModelRef Mesh(GeneratedMesh mesh, bool isStatic = false)
    {
        RequireSurface(mesh.Surface);
        if (!isStatic)
        {
            var model = Add(mesh);
            _dynamicMeshes.Add(model.Index);
            return model;
        }

        return Add(mesh.WithLightmapUvs());
    }

    /// <summary>
    /// False only for a mesh made in code without <c>isStatic</c>, which has no lightmap UVs. Built-in shapes and
    /// cooked models always have them.
    /// </summary>
    internal bool MadeStatic(ModelRef model) => !_dynamicMeshes.Contains(model.Index);

    /// <summary>
    /// Throws unless everything the surface names was named here first and its arrays fit, as
    /// for a generated mesh or a decal.
    /// </summary>
    internal void RequireSurface(Surface surface)
    {
        RequireTexture(surface.ColorTexture, nameof(surface.ColorTexture));
        RequireTexture(surface.NormalTexture, nameof(surface.NormalTexture));
        RequireTexture(surface.SurfaceTexture, nameof(surface.SurfaceTexture));
        RequireTexture(surface.HeightTexture, nameof(surface.HeightTexture));
        RequireTexture(surface.DetailColorTexture, nameof(surface.DetailColorTexture));
        RequireTexture(surface.DetailNormalTexture, nameof(surface.DetailNormalTexture));
        if (surface.Shader is { } shader)
        {
            Require<ShaderItem>(shader.Index, nameof(surface.Shader), "shader");
        }

        RequireCount(surface.Parameters.Length, Super.Surface.MaxParameters, nameof(surface.Parameters));
        RequireCount(surface.ShaderTextures.Length, Super.Surface.MaxShaderTextures, nameof(surface.ShaderTextures));
        RequireCount(surface.ShaderArrays.Length, Super.Surface.MaxShaderTextures, nameof(surface.ShaderArrays));
        for (int i = 0; i < surface.ShaderTextures.Length; i++)
        {
            RequireTexture(surface.ShaderTextures[i], $"{nameof(surface.ShaderTextures)}[{i}]");
        }

        for (int i = 0; i < surface.ShaderArrays.Length; i++)
        {
            Require<TextureArrayItem>(surface.ShaderArrays[i].Index, $"{nameof(surface.ShaderArrays)}[{i}]", "texture array");
        }
    }

    /// <summary>
    /// Names a fragment shader compiled from the game's Shaders folder, by file name
    /// without extension. Naming it again gives the same one.
    /// </summary>
    public ShaderRef Shader(string name)
    {
        if (!_shaders.TryGetValue(name, out var shader))
        {
            shader = new ShaderRef(Add(new ShaderItem(name)).Index);
            _shaders.Add(name, shader);
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
        if (!_textures.TryGetValue(name, out var texture))
        {
            ContentNames.Require(_files, name, ContentKind.Texture);
            texture = new TextureRef(Add(new TextureItem(name, null, _files, TextureKind.Color, false)).Index);
            _textures.Add(name, texture);
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
        if (_surfaces.TryGetValue(name, out var known))
        {
            return known;
        }

        string part = name + "#material";
        if (_files.KindOf(part) != ContentKind.Data)
        {
            if (_files.KindOf(name) is null)
            {
                throw new ContentException([ContentNames.Missing(_files, name)]);
            }

            throw new ContentException([$"{name} is not a cooked material. Materials are .material.cs and .material.json files."]);
        }

        float width;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(ReadFile(part, "material"));
            width = json.RootElement.GetProperty("width").GetSingle();
        }
        catch (Exception problem) when (problem is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ContentException([$"{part} can't be read: {problem.Message}"]);
        }

        // Shapes divide by the width to tile, so zero, a negative or a non-number would spread NaN or flip the UVs.
        if (!float.IsFinite(width) || width <= 0f)
        {
            throw new ContentException([$"{part} has a width of {width.ToString(System.Globalization.CultureInfo.InvariantCulture)} metres; it must be a positive number. Cook it again."]);
        }

        var surface = new Surface
        {
            Color = System.Numerics.Vector3.One,
            Roughness = 1f,
            Metallic = 1f,
            ColorTexture = Texture(name + "#color"),
            NormalTexture = Texture(name + "#normal"),
            SurfaceTexture = Texture(name + "#surface"),
            TileWidth = width,
        };
        _surfaces.Add(name, surface);
        return surface;
    }

    /// <summary>Names an image made in code. Each call makes a new one.</summary>
    public TextureRef Texture(GeneratedTexture texture, TextureKind kind = TextureKind.Color, bool normalMap = false) =>
        new(Add(new TextureItem(null, texture, _files, kind, normalMap)).Index);

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
            items[i] = Require<TextureItem>(layers[i].Index, $"{nameof(layers)}[{i}]", "texture");
        }

        return new TextureArrayRef(Add(new TextureArrayItem(items)).Index);
    }

    /// <summary>
    /// Whether this one model is ready to draw. Always true without a window, and false
    /// once it's unloaded.
    /// </summary>
    public bool IsReady(ModelRef model)
    {
        if (model.IsBuiltIn)
        {
            return true;
        }

        RequireNamed(model.Index, nameof(model));
        return !_unloaded.Contains(model.Index) && !Held && (!Streaming || _itemLoaded[model.Index - 1]);
    }

    /// <summary>
    /// Lets go of a mesh or model. The window frees it, and drawing it afterwards fails.
    /// Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(ModelRef model)
    {
        if (model.IsBuiltIn)
        {
            throw new ArgumentException("The built-in shapes, such as the cube, can't be unloaded.", nameof(model));
        }

        RequireNamed(model.Index, nameof(model));
        RequireLoaded(model.Index, nameof(model));
        object item = _items[model.Index - 1];
        if (item is not (string or GeneratedMesh))
        {
            throw new ArgumentException($"{nameof(model)} names {model.Index}, which is not a model.", nameof(model));
        }

        MarkUnloaded(model.Index);
        _meshNames.Remove(model.Index);
        if (item is string name)
        {
            _models.Remove(name);
        }
    }

    /// <summary>
    /// Lets go of a texture. The window frees it, and a mesh named afterwards can't use
    /// it. A mesh that names it must be unloaded first, since the window would otherwise
    /// draw with a texture that is gone. Naming the same file again later makes a new one.
    /// </summary>
    public void Unload(TextureRef texture)
    {
        var item = Require<TextureItem>(texture.Index, nameof(texture), "texture");
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i] is GeneratedMesh mesh && Names(mesh.Surface, texture))
            {
                throw new InvalidOperationException($"Texture {texture.Index} is still named by model {i + 1}. Unload the model first.");
            }
        }

        MarkUnloaded(texture.Index);
        if (item.Name is { } name)
        {
            _textures.Remove(name);
        }

        // A surface that holds the texture would hand it out again after it's gone, so it's read afresh next time.
        foreach (var (material, _) in _surfaces.Where(pair => Names(pair.Value, texture)).ToList())
        {
            _surfaces.Remove(material);
        }
    }

    private static bool Names(Surface surface, TextureRef texture)
    {
        if (surface.ColorTexture == texture || surface.NormalTexture == texture || surface.SurfaceTexture == texture
            || surface.HeightTexture == texture || surface.DetailColorTexture == texture || surface.DetailNormalTexture == texture)
        {
            return true;
        }

        foreach (var named in surface.ShaderTextures)
        {
            if (named == texture)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an item was let go. Cheap, so drawing can ask every frame.</summary>
    internal bool IsUnloaded(int index) => _unloaded.Count > 0 && _unloaded.Contains(index);

    /// <summary>Items let go since the window last looked. Indices into the loader, not <see cref="Items"/>.</summary>
    internal ReadOnlySpan<int> PendingUnloads => CollectionsMarshal.AsSpan(_pendingUnloads);

    internal void ClearPendingUnloads() => _pendingUnloads.Clear();

    /// <summary>
    /// Reads every JSON file under a folder of the content folder, one record per file.
    /// Anything wrong stops here with a <see cref="ContentException"/> listing every
    /// problem. Loading the same folder again hands back the same catalog.
    /// </summary>
    public Catalog<T> LoadAll<T>(string folder)
        where T : notnull
    {
        if (_catalogs.TryGetValue((typeof(T), folder), out var known))
        {
            return (Catalog<T>)known;
        }

        _reader.RequireId(typeof(T));
        var catalog = new Catalog<T>(folder);
        Load([catalog], [.. _catalogs.Values, catalog]);
        _catalogs.Add((typeof(T), folder), catalog);
        return catalog;
    }

    /// <summary>
    /// Fails if a reference still has no catalog to point at. Files can load in any order
    /// within one Load or one tick, so this runs when those end.
    /// </summary>
    internal void RequireReferences()
    {
        if (_waiting.Count == 0)
        {
            return;
        }

        var problems = _waiting.ConvertAll(reference =>
            $"{reference.Place}: {reference.Field} points at {reference.Id}, but no {reference.Target.Name} was loaded");
        _waiting.Clear();
        throw new ContentException(problems);
    }

    /// <summary>The schema for a type of record, as text.</summary>
    internal string SchemaText(Type type) => ContentSchema.Text(type, _reader.Options);

    /// <summary>Writes a schema for every catalog into a content folder.</summary>
    internal void WriteSchemas(string root)
    {
        foreach (var catalog in _catalogs.Values)
        {
            ContentSchema.Write(root, catalog.Folder, catalog.RecordType, _reader.Options);
        }
    }

    /// <summary>
    /// Starts development help for a game run from source, which the build marks with
    /// <c>content.dev.json</c> beside the cooked folder: it writes the schemas into the first
    /// content root, starts <c>super-cook --watch</c>, and asks for a reload whenever the cooked
    /// manifest is written again. Without that file, as in a published game, it does nothing.
    /// </summary>
    internal void GoLive()
    {
        if (_watcher is not null || _files is not CookedFolder { Dev: { } dev } folder)
        {
            return;
        }

        if (dev.Roots.Length > 0 && Directory.Exists(dev.Roots[0]))
        {
            WriteSchemas(dev.Roots[0]);
        }

        // Watching starts first, so a cook that lands while the tool starts up isn't missed.
        Directory.CreateDirectory(folder.Folder);
        _watcher = new ContentWatcher(folder.Folder, ContentManifest.FileName, () => ReloadWanted = true, ReloadQuietTime);
        try
        {
            _cook = StartCook(dev, folder.Folder);
        }
        catch (Win32Exception problem)
        {
            // Without dotnet on the path the game still runs, and a cook started by hand is still heard.
            Console.Error.WriteLine($"Could not start super-cook to watch the content: {problem.Message}");
        }
    }

    /// <summary>Stops the cook and stops watching files.</summary>
    internal void StopLive()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cook?.Dispose();
        _cook = null;
    }

    /// <summary>
    /// Reads every catalog again, all or nothing. A mistake is written to the log and the old
    /// catalog data stays, because a typo shouldn't end a play session. The cooked folder has
    /// already swapped to the new manifest by then, so what's read by name afterwards sees the
    /// new content. A file that can't be read yet is likely still being saved, so it's tried
    /// again on the next tick.
    /// </summary>
    internal bool Reload(TextWriter log)
    {
        ReloadWanted = false;
        ICatalog[] all = [.. _catalogs.Values];
        try
        {
            _files.Refresh();
            _identities.Clear(); // A cooked file named again may hash differently now.
            Load(all, all);
        }
        catch (ContentException mistake)
        {
            log.WriteLine($"Content not reloaded:{Environment.NewLine}{mistake.Message}");
            return false;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            foreach (var catalog in all)
            {
                catalog.Drop();
            }

            ReloadWanted = true;
            return false;
        }

        foreach (var catalog in all)
        {
            catalog.Announce();
        }

        log.WriteLine("Content reloaded.");
        return true;
    }

    /// <summary>Counts one more item as ready. The window calls this as it finishes each one.</summary>
    internal void ItemLoaded(int index)
    {
        _itemLoaded[index - 1] = true;
        _loaded++;
        if (_loaded == _items.Count)
        {
            _batchStart = _loaded;
        }
    }

    /// <summary>
    /// Reads catalogs all or nothing, so a mistake in one leaves every one of them as it
    /// was. References are checked against everything known, including what's being read.
    /// </summary>
    private void Load(ICatalog[] catalogs, ICatalog[] known)
    {
        var problems = new List<string>();
        var references = new List<ContentReference>();
        foreach (var catalog in catalogs)
        {
            catalog.Check(_reader, problems, references);
        }

        var waiting = new List<ContentReference>(_waiting);
        waiting.AddRange(references);
        var stillWaiting = Resolve(waiting, known, problems);

        if (problems.Count == 0)
        {
            foreach (var catalog in catalogs)
            {
                catalog.Build(_reader, problems);
            }
        }

        foreach (var catalog in catalogs)
        {
            if (problems.Count > 0)
            {
                catalog.Drop();
            }
            else
            {
                catalog.Commit();
            }
        }

        if (problems.Count > 0)
        {
            throw new ContentException(problems);
        }

        _waiting.Clear();
        _waiting.AddRange(stillWaiting);
    }

    /// <summary>Reports references that point at nothing, and hands back those with no catalog yet.</summary>
    private static List<ContentReference> Resolve(List<ContentReference> references, ICatalog[] known, List<string> problems)
    {
        var stillWaiting = new List<ContentReference>();
        foreach (var reference in references)
        {
            var targets = known.Where(catalog => catalog.RecordType == reference.Target).ToArray();
            if (targets.Length == 0)
            {
                stillWaiting.Add(reference);
            }
            else if (!targets.Any(catalog => catalog.Has(reference.Id)))
            {
                problems.Add($"{reference.Place}: {reference.Field} points at {reference.Id}, but there is no {reference.Target.Name} called that");
            }
        }

        return stillWaiting;
    }

    private byte[] ReadFile(string path, string kind)
    {
        try
        {
            return _files.Read(path);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new ContentException([$"Could not read the {kind} {path}: {problem.Message}"]);
        }
    }

    private ModelRef Add(object item)
    {
        _items.Add(item);
        _itemLoaded.Add(false);

        // Index 0 is the built-in cube, and the other built-in shapes are below zero.
        return new ModelRef(_items.Count);
    }

    /// <summary>
    /// What a model is, as a stable hash for the bake ids of its static draws. Worked out the first time a static
    /// draw asks, since hashing a big mesh made in code takes a while, and kept until the model is unloaded.
    /// </summary>
    internal ulong IdentityOf(ModelRef model)
    {
        if (!_identities.TryGetValue(model.Index, out ulong identity))
        {
            identity = BakeIdentity.Model(model, _items, _files, ShaderBytes);
            _identities[model.Index] = identity;
        }

        return identity;
    }

    /// <summary>Forgets an item's data and tells the window to free what it made of it.</summary>
    private void MarkUnloaded(int index)
    {
        _identities.Remove(index);
        _dynamicMeshes.Remove(index);
        _unloaded.Add(index);
        _pendingUnloads.Add(index);
        _items[index - 1] = UnloadedItem.Instance;
    }

    private void RequireLoaded(int index, string field)
    {
        if (_unloaded.Contains(index))
        {
            throw new ArgumentException($"{field} names {index}, which was unloaded.", field);
        }
    }

    private void RequireTexture(TextureRef? texture, string field)
    {
        if (texture is { } named)
        {
            Require<TextureItem>(named.Index, field, "texture");
        }
    }

    private void RequireNamed(int index, string field)
    {
        if (index < 1 || index > _items.Count)
        {
            throw new ArgumentException($"{field} names {index}, but nothing was named with that index.", field);
        }
    }

    private T Require<T>(int index, string field, string kind)
        where T : class
    {
        RequireNamed(index, field);
        RequireLoaded(index, field);
        if (_items[index - 1] is not T item)
        {
            throw new ArgumentException($"{field} names {index}, which is not a {kind}.", field);
        }

        return item;
    }

    private static void RequireCount(int count, int max, string field)
    {
        if (count > max)
        {
            throw new ArgumentException($"{field} has {count} entries, but a surface shader takes at most {max}.", field);
        }
    }
}
