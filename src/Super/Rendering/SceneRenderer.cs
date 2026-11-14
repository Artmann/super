using System.Globalization;
using System.Numerics;
using Rendition;
using Super.Profiling;
using Super.Ui;

namespace Super;

/// <summary>Turns what a <see cref="View"/> wrote down into Rendition draws.</summary>
internal sealed class SceneRenderer : IDisposable
{
    private const int MaxLights = 8;
    private const float DefaultRoughness = 0.4f;

    /// <summary>The built-in shapes' color when a draw doesn't give one.</summary>
    private static readonly Vector3 CubeColor = new(0.7f);

    private readonly Renderer _renderer;
    private readonly MaterialHandle _defaultMaterial;
    private readonly DirectionalLight[] _lights = new DirectionalLight[MaxLights];

    /// <summary>Everything the content named, by the index its ref carries.</summary>
    private readonly ContentLoader<Loaded> _items;

    /// <summary>The built-in shapes other than the cube, made once and kept for the renderer's life.</summary>
    private readonly LoadedModel[] _shapes;

    /// <summary>Kept, since asking for a new one each frame would be garbage.</summary>
    private readonly Func<GameImage, int> _textureOf;

    private DrawItem[] _draws = new DrawItem[64];
    private Matrix4x4[] _bones = new Matrix4x4[256];
    private readonly JointPose[] _pose = new JointPose[Skeleton.MaxJoints];
    private readonly JointPose[] _poseScratch = new JointPose[Skeleton.MaxJoints];
    private int _boneCount;
    private BakeSphere[] _partBounds = new BakeSphere[16];
    private readonly BakeIdentity.Scratch _bakeScratch = new();
    private Bands _bakedBands;
    private int _uploadedAtlas;
    private Content? _content;
    private IContentFiles? _files;
    private int _filesReloads;
    private readonly ReflectionProbeSchedule _reflectionProbes = new();

    /// <summary>
    /// A material for each surface decals have used, made the first time, with the frame it was last used in.
    /// Kept until content unloads, or until there are more than <see cref="MaxDecalMaterials"/>.
    /// </summary>
    private readonly Dictionary<Surface, (MaterialHandle Handle, long Frame)> _decalMaterials = [];

    /// <summary>
    /// How many decal materials are kept. Past this, the ones this frame didn't use are let go, so a game that
    /// makes a new <see cref="Surface"/> value every frame doesn't fill the material slots. Twice what one
    /// frame can use, so the materials of a steady scene are never made again.
    /// </summary>
    internal const int MaxDecalMaterials = 2 * Rendition.Passes.DecalBatch.MaxDecals;

    private long _decalFrame;
    private readonly List<Surface> _unusedDecalSurfaces = [];
    private DecalDraw[] _decals = new DecalDraw[16];
    private int _decalCount;

    /// <summary>Surface shaders named by cooked models, made once for each name and kept for the renderer's life.</summary>
    private readonly Dictionary<string, SurfaceShaderHandle> _modelShaders = [];

    public SceneRenderer(RendererOptions options)
    {
        _renderer = new Renderer(options);
        Bakes = new LightBakeStore(ReadBake, Console.Error);
        _renderer.Bakes = Bakes;

        // Shots, recordings and profiles show each bake whole from the first frame; play in a window fades them in.
        _renderer.BakeFadeSeconds = 0f;
        // White, so a draw's color comes out exactly as asked. CubeColor greys the rest.
        _defaultMaterial = _renderer.CreateMaterial(CreateMaterial(Vector3.One));
        _items = new ContentLoader<Loaded>(
            new LoadedModel([_renderer.CreateDrawItem(Cube.Vertices, Cube.Indices, _defaultMaterial)], null, null),
            PrepareItem,
            Upload);
        _shapes = Array.ConvertAll(
            Shapes.BuiltIn(),
            mesh => new LoadedModel([_renderer.CreateDrawItem(mesh.Vertices, mesh.Indices, _defaultMaterial)], null, null));
        _textureOf =image => OverlayQuad.For(Require<LoadedTexture>(image.Index, _content!).Handle);
        _renderer.SetEnvironment(new EnvironmentDescription
        {
            Ceiling = Studio.Ceiling,
            Horizon = Studio.Horizon,
            Floor = Studio.Floor,
        });
        _bakedBands = Studio;
    }

    /// <summary>The grey room a world without a sky sits in.</summary>
    private static readonly Bands Studio = new(new Vector3(0.45f, 0.5f, 0.6f), new Vector3(0.5f, 0.5f, 0.52f), new Vector3(0.25f, 0.24f, 0.23f));

    internal readonly record struct Bands(Vector3 Ceiling, Vector3 Horizon, Vector3 Floor);

    /// <summary>
    /// The bands baked into the environment. Water wins over the sky, which wins over the studio.
    /// A clear sky keeps its three colours here, and the renderer uses only the ground below its horizon.
    /// </summary>
    internal static Bands SurroundingColors(View view)
    {
        var water = view.WaterSettings;
        if (water.Absorption != Vector3.Zero)
        {
            return new Bands(water.ShallowColor, Vector3.Lerp(water.ShallowColor, water.DeepColor, 0.5f), water.DeepColor);
        }

        if (view.SkySettings is { } sky)
        {
            return new Bands(sky.Zenith, sky.Horizon, sky.Ground);
        }

        return Studio;
    }

    /// <summary>The view's clear sky, or none under water, which takes over the sky.</summary>
    internal static AnalyticSky FrameSky(View view) =>
        view.WaterSettings.Absorption == Vector3.Zero && view.SkySettings is { } sky
            ? new AnalyticSky { Turbidity = sky.Turbidity }
            : default;

    public int ValidationErrors => _renderer.ValidationErrors;

    public bool MeasureGpu
    {
        set => _renderer.MeasureGpu = value;
    }

    /// <summary>Waits for the display before showing a frame. A change takes effect on the next frame.</summary>
    public bool Vsync
    {
        get => _renderer.Vsync;
        set => _renderer.Vsync = value;
    }

    /// <summary>How many texels across each sun shadow map is. A change rebuilds the maps before the next frame.</summary>
    public int ShadowResolution
    {
        get => _renderer.ShadowResolution;
        set => _renderer.ShadowResolution = value;
    }

    /// <summary>The most of the sun's cascades drawn. See <see cref="Renderer.MaxShadowCascades"/>.</summary>
    public int MaxShadowCascades
    {
        get => _renderer.MaxShadowCascades;
        set => _renderer.MaxShadowCascades = value;
    }

    /// <summary>How long models keep their finer versions as they move away. See <see cref="Renderer.MeshDetailScale"/>.</summary>
    public float MeshDetail
    {
        get => _renderer.MeshDetailScale;
        set => _renderer.MeshDetailScale = value;
    }

    /// <summary>How large the scene is drawn against the window. A change rebuilds the targets before the next frame.</summary>
    public float RenderScale
    {
        get => _renderer.RenderScale;
        set => _renderer.RenderScale = value;
    }

    /// <summary>What anti-aliases the scene, and upscales it with FSR. A change rebuilds the targets before the next frame.</summary>
    public Upscaler Upscaler
    {
        get => _renderer.Upscaler;
        set => _renderer.Upscaler = value;
    }

    /// <summary>How many light probes follow the camera and are updated a frame. A change starts every probe again on the next frame.</summary>
    public ProbeQuality ProbeQuality
    {
        get => _renderer.ProbeQuality;
        set => _renderer.ProbeQuality = value;
    }

    /// <summary>How many pixels trace their screen-space reflection each frame. A change rebuilds the targets on the next frame.</summary>
    public ReflectionResolution ReflectionResolution
    {
        get => _renderer.ReflectionResolution;
        set => _renderer.ReflectionResolution = value;
    }

    /// <summary>How many pixels work out their ambient occlusion each frame. A change rebuilds the targets on the next frame.</summary>
    public OcclusionResolution OcclusionResolution
    {
        get => _renderer.OcclusionResolution;
        set => _renderer.OcclusionResolution = value;
    }

    /// <summary>Whether the light probes near the camera are all up to date. See <see cref="Renderer.ProbesSettled"/>.</summary>
    public bool ProbesSettled => _renderer.ProbesSettled;

    /// <summary>The effects left out of every frame from now on.</summary>
    public RenderEffects Off { get; set; }

    /// <summary>What every frame shows: the lit scene, or a look inside the renderer. See <see cref="Rendition.DebugView"/>.</summary>
    public DebugView DebugView { get; set; }

    /// <summary>Whether the GPU can trace rays. See <see cref="Renderer.CanTrace"/>.</summary>
    public bool CanTrace => _renderer.CanTrace;

    /// <summary>Swaps in shaders that were compiled again. See <see cref="Renderer.ReloadShaders"/>.</summary>
    public int ReloadShaders(IReadOnlySet<string> names) => _renderer.ReloadShaders(names);

    /// <summary>Why <see cref="CanTrace"/> is false, or null when it is true.</summary>
    public string? NoTraceReason => _renderer.NoTraceReason;

    /// <summary>The GPU the scene is drawn with. See <see cref="Renderer.Gpu"/>.</summary>
    public GpuInfo Gpu => _renderer.Gpu;

    public ReadOnlySpan<GpuPassTime> GpuTimes => _renderer.GpuTimes;

    public double WaitMilliseconds => _renderer.WaitMilliseconds;

    public (long Camera, long Shadows) DrawnTriangles => _renderer.DrawnTriangles;

    public void Render(View view, Content content, int width, int height, float time)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        PrepareDecals(view, content);
        var frame = CreateFrame(view, width, height, time);
        CaptureReflectionProbes(view, frame, _draws.AsSpan(0, count));
        _renderer.Render(frame, _draws.AsSpan(0, count));
        SayWhyNotBaking(view);
    }

    public byte[] Capture(View view, Content content, int width, int height, float time, out int capturedWidth, out int capturedHeight)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        PrepareDecals(view, content);
        var frame = CreateFrame(view, width, height, time);
        CaptureReflectionProbes(view, frame, _draws.AsSpan(0, count));
        return _renderer.Capture(frame, _draws.AsSpan(0, count), out capturedWidth, out capturedHeight);
    }

    /// <summary>
    /// Bakes the view's baked space from the same frame <see cref="Render"/> would draw, and returns the
    /// <c>.lightbake</c> file's bytes. See <see cref="Renderer.Bake"/>.
    /// </summary>
    public byte[] Bake(View view, Content content, int width, int height, float time, BakeSettings settings)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        PrepareDecals(view, content);
        var frame = CreateFrame(view, width, height, time);
        return _renderer.Bake(frame, _draws.AsSpan(0, count), settings);
    }

    /// <summary>
    /// Bakes chunk <paramref name="chunk"/> of the view from the same frame <see cref="Render"/> would draw, and returns
    /// the <c>.lightbake</c> file's bytes. See <see cref="Renderer.BakeChunk"/>.
    /// </summary>
    public byte[] BakeChunk(View view, Content content, int width, int height, float time, int chunk, BakeSettings settings)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        PrepareDecals(view, content);
        var frame = CreateFrame(view, width, height, time);
        return _renderer.BakeChunk(frame, _draws.AsSpan(0, count), chunk, settings);
    }

    /// <summary>What the last frame's baked areas came to. See <see cref="Renderer.LastBakeFrame"/>.</summary>
    public BakeFrame? LastBakeFrame => _renderer.LastBakeFrame;

    /// <summary>The bakes in the content last drawn with, which the renderer checks each frame's baked areas against.</summary>
    internal LightBakeStore Bakes { get; }

    /// <summary>
    /// The chunk bakes made on this machine, read when the cooked content has no bake of that name. Null, the default,
    /// reads cooked content only. Read and written where the bake files are, never in a frame's own work.
    /// </summary>
    internal BakeCache? BakeCache { get; set; }

    /// <summary>
    /// How long a bake that arrives on screen takes to fade in. 0, from the start, so shots, recordings and profiles
    /// show each bake whole; play in a window sets it. See <see cref="Renderer.BakeFadeSeconds"/>.
    /// </summary>
    public float BakeFadeSeconds
    {
        get => _renderer.BakeFadeSeconds;
        set => _renderer.BakeFadeSeconds = value;
    }

    /// <summary>What the renderer bakes by itself. See <see cref="Renderer.AutoBake"/>.</summary>
    internal AutoBake? AutoBake => _renderer.AutoBake;

    /// <summary>How the background bake is getting on. See <see cref="Renderer.BakeProgress"/>.</summary>
    public BakeProgress? BakeProgress => _renderer.BakeProgress;

    /// <summary>
    /// Bakes what <paramref name="plan"/> says in the background, or nothing when it is null. A space's bake is written
    /// into the plan's folder and a chunk's into <see cref="BakeCache"/>; see <see cref="BakeSaver"/>. Setting it stops
    /// the bake that runs and waits for the GPU the first time, so call it between frames.
    /// </summary>
    internal void BakeOnArrival(AutoBakePlan? plan)
    {
        if (plan is null)
        {
            _renderer.AutoBake = null;
            return;
        }

        // One saver while the folder and cache stay, so bakes are kept in the order they finished across changes.
        if (_saver is null || _saver.SpaceFolder != plan.SpaceFolder || _saver.Cache != BakeCache)
        {
            _saver?.Wait(TimeSpan.FromSeconds(10));
            _saver = new BakeSaver(plan.SpaceFolder, BakeCache, _written.Written, Console.Out, Console.Error);
        }

        _renderer.AutoBake = new AutoBake(plan.Budget, plan.Spaces, plan.Qualities, _saver.Done);
    }

    /// <summary>
    /// Set when the game would bake from source but the GPU can't trace, so the first area that wants a bake says once,
    /// on the error stream, why it isn't baked.
    /// </summary>
    internal bool SaysWhyNotBaking
    {
        get => _notBaking.On;
        set => _notBaking.On = value;
    }

    // The chunk files written to the cache on the thread pool, for the render thread to forget the bakes it holds.
    private readonly WrittenBakes _written = new();
    private readonly NotBakingNotice _notBaking = new(Console.Error);
    private BakeSaver? _saver;

    // Says, once, why the first area the last frame wanted a bake for isn't baked.
    private void SayWhyNotBaking(View view) => _notBaking.Check(
        LastBakeFrame, view.BakedSpace, view.PlacedBakedChunks, Off.HasFlag(RenderEffects.BakedLighting), NoTraceReason);

    // A bake is cooked as data under its own name, so anything else of that name isn't one. Failing that, the cache
    // of bakes made on this machine may have it.
    private byte[]? ReadBake(string name) =>
        _files is { } files && files.KindOf(name) == ContentKind.Data
            ? files.Read(name)
            : BakeCache?.Read(name);

    /// <summary>The reflection probes to keep: the view's, or none while reflections are off.</summary>
    internal static ReadOnlySpan<ReflectionProbeRequest> WantedReflectionProbes(View view, RenderEffects off) =>
        off.HasFlag(RenderEffects.Reflections) ? [] : view.ReflectionProbes;

    /// <summary>
    /// Photographs each of the view's reflection probes the first frame it's placed, and again when
    /// it moves or the lighting changes. Moving the camera isn't a lighting change. A probe that's gone takes the others with it, and they're
    /// photographed again in their new slots.
    /// </summary>
    private void CaptureReflectionProbes(View view, in FrameView frame, ReadOnlySpan<DrawItem> draws)
    {
        var wanted = WantedReflectionProbes(view, Off);
        if (_reflectionProbes.Forget(wanted))
        {
            _renderer.ClearReflectionProbes();
        }

        if (wanted.IsEmpty)
        {
            return;
        }

        int lighting = _renderer.ReflectionLightingHash(frame);
        foreach (var request in wanted)
        {
            if (!_reflectionProbes.ShouldCapture(request, lighting))
            {
                continue;
            }

            var probe = new ReflectionProbe
            {
                Position = request.Position,
                BoxMin = request.Min,
                BoxMax = request.Max,
                BlendDistance = request.Blend,
            };

            // The probe stands in the open, so it sees every draw, and every draw casts its shadow.
            // The schedule hears of the capture only once it worked, so a throw leaves the two in step.
            int slot = _reflectionProbes.SlotOf(request.Key);
            if (slot >= 0)
            {
                _renderer.RecaptureReflectionProbe(new ReflectionProbeHandle(slot), probe, frame, draws, draws);
            }
            else
            {
                _renderer.CaptureReflectionProbe(probe, frame, draws, draws);
            }

            _reflectionProbes.Captured(request, lighting);
        }
    }

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
            string name = Name(model, content, "generated mesh");
            costs.Add(new ModelCost(name, count, ShownTriangles(Parts(model, content), mask)));
        }

        return costs;
    }

    public void Dispose()
    {
        _renderer.Dispose();

        // A bake still being written would leave a temp file in the sources if the process ended under it.
        _saver?.Wait(TimeSpan.FromSeconds(10));
    }

    private static Material CreateMaterial(Vector3 color) => new()
    {
        BaseColor = new Vector4(color, 1f),
        Roughness = DefaultRoughness,
    };

    /// <summary>
    /// Reads a named texture and builds its mips, or takes the ones it was cooked with when it's
    /// compressed. Runs on the background thread. A cooked texture's kind, normal-map flag and
    /// format come from its header, as its cook settings set them.
    /// </summary>
    internal static TextureData LoadTexture(TextureItem item)
    {
        if (item.Generated is { } generated)
        {
            return TextureData.FromPixels(generated.Size, generated.Size, item.Kind, generated.Pixels, item.NormalMap);
        }

        return CookedTexture.Read(item.Files.Read(item.Name!), item.Name!).ToTextureData();
    }

    /// <summary>
    /// Reads an interface image and builds its mips. Runs on the background thread. A compressed
    /// one is decoded first, because an image's mips weigh each pixel by its alpha, so a clear
    /// pixel lends no colour to its neighbours, which the cooked mips don't.
    /// </summary>
    internal static TextureData LoadImage(GameImage image)
    {
        if (image.Pixels is { } pixels)
        {
            return new TextureData
            {
                Width = image.Width,
                Height = image.Height,
                Kind = TextureKind.Color,
                Mips = ImageMips.Build(image.Width, image.Height, pixels),
            };
        }

        var cooked = CookedTexture.Read(image.Cooked!, image.Path);
        return new TextureData
        {
            Width = cooked.Width,
            Height = cooked.Height,
            Kind = TextureKind.Color,
            Mips = ImageMips.Build(cooked.Width, cooked.Height, cooked.DecodePixels()),
        };
    }

    /// <summary>Reads a cooked model as a stream, so it's never held whole beside what it builds. Runs on the background thread.</summary>
    internal static ModelData LoadModel(string name, IContentFiles files)
    {
        using var stream = files.Open(name);
        return ModelData.Read(stream, name, hash =>
        {
            using var texture = files.OpenObject(hash);
            return TextureData.Read(texture, $"{name}: texture {hash}");
        });
    }

    /// <summary>
    /// Reads a cooked model and, when its materials name surface shaders, those shaders and the
    /// cooked textures they read, by name. Runs on the background thread. <paramref name="readShader"/>
    /// reads a shader's SPIR-V by name, from the <c>shaders</c> folder beside the program unless a test gives another.
    /// </summary>
    internal static object PrepareModel(string name, IContentFiles files, Func<string, byte[]>? readShader = null)
    {
        var model = LoadModel(name, files);
        var shaders = model.MaterialShaders.OfType<ModelMaterialShader>().ToArray();
        if (shaders.Length == 0)
        {
            return model;
        }

        var spirv = new Dictionary<string, byte[]>();
        var textures = new Dictionary<string, TextureData>();
        foreach (var shader in shaders)
        {
            if (!spirv.ContainsKey(shader.Name))
            {
                spirv[shader.Name] = (readShader ?? LoadShader)(shader.Name);
            }

            foreach (string texture in shader.Textures)
            {
                if (!textures.ContainsKey(texture))
                {
                    ContentNames.Require(files, texture, ContentKind.Texture);
                    textures[texture] = LoadTexture(new TextureItem(texture, null, files, TextureKind.Color, false));
                }
            }
        }

        return new PreparedModel(model, spirv, textures);
    }

    /// <summary>
    /// Uploads a model whose materials use surface shaders. A shader is made once for each name and
    /// kept for the renderer's life, as shaders named through <see cref="Content.Shader"/> are. The
    /// textures are the model's own, since destroying a model frees every texture its materials name.
    /// </summary>
    private LoadedModel UploadModel(PreparedModel prepared)
    {
        foreach (var (name, spirv) in prepared.Shaders)
        {
            if (!_modelShaders.ContainsKey(name))
            {
                _modelShaders[name] = _renderer.CreateSurfaceShader(spirv, name + ".frag");
            }
        }

        var textures = prepared.Textures.ToDictionary(pair => pair.Key, pair => _renderer.CreateTexture(pair.Value));
        var model = prepared.Model;
        var parts = _renderer.CreateModel(model, (index, material) =>
        {
            if (model.ShaderOf(index) is not { } shader)
            {
                return material;
            }

            material.Shader = _modelShaders[shader.Name];
            for (int i = 0; i < shader.Parameters.Length; i++)
            {
                material.ShaderParameters[i] = shader.Parameters[i];
            }

            for (int i = 0; i < shader.Textures.Length; i++)
            {
                material.ShaderTextures[i] = textures[shader.Textures[i]];
            }

            return material;
        });
        return new LoadedModel(parts, null, null, model.Skeleton?.Count ?? 0);
    }

    internal static byte[] LoadShader(string name)
    {
        // Shaders.targets keeps the source extension, so Shaders/terrain.frag becomes shaders/terrain.frag.spv.
        string path = Path.Combine(AppContext.BaseDirectory, "shaders", name + ".frag.spv");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Could not find the shader {name}: expected {path}. Shaders/{name}.frag compiles to it when the project imports src/Shaders.targets.",
                path);
        }

        return File.ReadAllBytes(path);
    }

    private void Prepare(Content content, View view)
    {
        // The bakes read so far came from other files, or from these before they were read again, so they're read again.
        if (!ReferenceEquals(_files, content.Files) || _filesReloads != content.Reloads)
        {
            Bakes.Forget();
            _filesReloads = content.Reloads;
        }

        // A chunk baked in the background is in the cache now, so the bake held for its file is read again.
        _written.ForgetIn(Bakes);

        // Set before anything new starts preparing, since models are read through it on background threads.
        _files = content.Files;
        _items.Update(content);

        // Decal materials point at the textures being freed, so they go too, and are made again when next used.
        if (content.PendingUnloads.Length > 0)
        {
            foreach (var material in _decalMaterials.Values)
            {
                _renderer.DestroyMaterial(material.Handle);
            }

            _decalMaterials.Clear();
        }

        foreach (int index in content.PendingUnloads)
        {
            if (_items.Take(index, Unloaded.Instance, content) is { } loaded)
            {
                Destroy(loaded);
            }
        }

        content.ClearPendingUnloads();

        // Surfaces pick up the color of the water or sky around them. Baking is slow, so
        // it only happens when the colors change.
        var bands = SurroundingColors(view);
        if (bands != _bakedBands)
        {
            _renderer.SetEnvironment(new EnvironmentDescription
            {
                Ceiling = bands.Ceiling,
                Horizon = bands.Horizon,
                Floor = bands.Floor,
            });
            _bakedBands = bands;
        }

        // Only a character that hasn't been drawn before brings this here.
        // Games with no interface never get here, and never load the fonts.
        if (view.Ui is { Quads.IsEmpty: false } ui && ui.Atlas.Version != _uploadedAtlas)
        {
            _renderer.SetOverlayAtlas(new TextureData
            {
                Width = GlyphAtlas.Size,
                Height = GlyphAtlas.Size,
                Kind = TextureKind.Linear,
                Mips = [ui.Atlas.Pixels],
            });
            _uploadedAtlas = ui.Atlas.Version;
        }

        if (view.Ui is { } shown)
        {
            _content = content;
            shown.BindImages(_textureOf);
        }
    }

    /// <summary>The part of loading that needs no GPU, so it runs on a background thread.</summary>
    private object PrepareItem(object item) => item switch
    {
        string name => PrepareModel(name, _files!),
        ShaderItem shader => new PreparedShader(shader.Name, LoadShader(shader.Name)),
        TextureItem texture => LoadTexture(texture),
        TextureArrayItem array => Array.ConvertAll(array.Layers, LoadTexture),
        GameImage image => LoadImage(image),
        GeneratedMesh mesh => new PreparedMesh(
            mesh,
            mesh.OwnedTexture is { } owned ? TextureData.FromPixels(owned.Size, owned.Size, TextureKind.Color, owned.Pixels) : null),
        UnloadedItem unloaded => unloaded, // Let go before the window saw it.
        _ => throw new InvalidOperationException($"Content can't load a {item.GetType().Name}."),
    };

    private Loaded Upload(object prepared, Content content) => prepared switch
    {
        ModelData model => new LoadedModel(_renderer.CreateModel(model), null, null, model.Skeleton?.Count ?? 0),
        PreparedModel model => UploadModel(model),
        PreparedShader shader => new LoadedShader(_renderer.CreateSurfaceShader(shader.Spirv, shader.Name + ".frag")),
        TextureData texture => new LoadedTexture(_renderer.CreateTexture(texture)),
        TextureData[] layers => new LoadedTextureArray(_renderer.CreateTextureArray(layers)),
        PreparedMesh(var mesh, var owned) => UploadMesh(mesh, owned, content),
        UnloadedItem => Unloaded.Instance,
        _ => throw new InvalidOperationException($"Content can't upload a {prepared.GetType().Name}."),
    };

    /// <summary>Each generated mesh gets its own material, built from what its surface named.</summary>
    private LoadedModel UploadMesh(GeneratedMesh mesh, TextureData? owned, Content content)
    {
        var surface = mesh.Surface;
        TextureHandle? ownedTexture = owned is null ? null : _renderer.CreateTexture(owned);
        var material = MaterialOf(surface) with
        {
            BaseColorTexture = ownedTexture ?? Texture(surface.ColorTexture, content),
            NormalTexture = Texture(surface.NormalTexture, content),
            SurfaceTexture = Texture(surface.SurfaceTexture, content),
            HeightTexture = Texture(surface.HeightTexture, content),
            HeightDepth = surface.HeightDepth,
            DetailColorTexture = Texture(surface.DetailColorTexture, content),
            DetailNormalTexture = Texture(surface.DetailNormalTexture, content),
            DetailScale = surface.DetailScale,
            Subsurface = surface.Subsurface,
            Shader = surface.Shader is { } shader ? Require<LoadedShader>(shader.Index, content).Handle : null,
        };
        for (int i = 0; i < surface.Parameters.Length; i++)
        {
            material.ShaderParameters[i] = surface.Parameters[i];
        }

        for (int i = 0; i < surface.ShaderTextures.Length; i++)
        {
            material.ShaderTextures[i] = Texture(surface.ShaderTextures[i], content);
        }

        for (int i = 0; i < surface.ShaderArrays.Length; i++)
        {
            material.ShaderArrays[i] = Require<LoadedTextureArray>(surface.ShaderArrays[i].Index, content).Handle;
        }

        var handle = _renderer.CreateMaterial(material);
        var part = _renderer.CreateDrawItem(mesh.Vertices, mesh.Indices, handle, mesh.Simplify);
        return new LoadedModel([part], handle, ownedTexture);
    }

    /// <summary>The view's decals, each with its surface's material, or none while decals are off.</summary>
    private void PrepareDecals(View view, Content content)
    {
        _decalCount = 0;
        if (Off.HasFlag(RenderEffects.Decals))
        {
            return;
        }

        var placed = view.DrawnDecals;
        if (_decals.Length < placed.Length)
        {
            _decals = new DecalDraw[Math.Max(placed.Length, _decals.Length * 2)];
        }

        _decalFrame++;
        foreach (ref readonly var decal in placed)
        {
            _decals[_decalCount++] = new DecalDraw(decal.Box, DecalMaterial(decal.Material, content), decal.Blend);
        }

        ForgetUnusedDecalMaterials();
    }

    /// <summary>
    /// Past <see cref="MaxDecalMaterials"/>, lets go of every decal material this frame didn't use. The renderer
    /// destroys them once the frames still in flight are done with them.
    /// </summary>
    private void ForgetUnusedDecalMaterials()
    {
        if (_decalMaterials.Count <= MaxDecalMaterials)
        {
            return;
        }

        _unusedDecalSurfaces.Clear();
        foreach (var (surface, material) in _decalMaterials)
        {
            if (material.Frame != _decalFrame)
            {
                _unusedDecalSurfaces.Add(surface);
            }
        }

        foreach (var surface in _unusedDecalSurfaces)
        {
            _renderer.DestroyMaterial(_decalMaterials[surface].Handle);
            _decalMaterials.Remove(surface);
        }
    }

    /// <summary>
    /// The material a decal lays on: the surface's colour, roughness and opacity, and its colour, normal and
    /// surface textures. A surface used before gets the material made then.
    /// </summary>
    private MaterialHandle DecalMaterial(Surface surface, Content content)
    {
        if (_decalMaterials.TryGetValue(surface, out var known))
        {
            _decalMaterials[surface] = (known.Handle, _decalFrame);
            return known.Handle;
        }

        var material = MaterialOf(surface) with
        {
            BaseColorTexture = Texture(surface.ColorTexture, content),
            NormalTexture = Texture(surface.NormalTexture, content),
            SurfaceTexture = Texture(surface.SurfaceTexture, content),
            Flags = MaterialFlags.None,
        };
        var handle = _renderer.CreateMaterial(material);
        _decalMaterials.Add(surface, (handle, _decalFrame));
        return handle;
    }

    /// <summary>A surface's own values as a material, before its textures and shader are found. Below full opacity it's glass.</summary>
    internal static Material MaterialOf(Surface surface) => new()
    {
        BaseColor = new Vector4(surface.Color, Math.Clamp(surface.Opacity, 0f, 1f)),
        Roughness = surface.Roughness,
        Metallic = surface.Metallic,
        Flags = FlagsOf(surface),
        Ior = surface.Ior,
        Translucency = surface.Translucency,
        Wind = surface.Wind,
    };

    /// <summary>A generated surface's material switches: glass below full opacity, foliage drawn from both sides.</summary>
    internal static MaterialFlags FlagsOf(Surface surface) =>
        (surface.Opacity < 1f ? MaterialFlags.Transparent : 0) | (surface.DetailMask ? MaterialFlags.DetailMask : 0)
        | (surface.WorldVariation ? MaterialFlags.WorldVariation : 0) | (surface.Dithered ? MaterialFlags.Dithered : 0)
        | (surface.Puddles ? MaterialFlags.Puddles : 0) | (surface.Foliage ? MaterialFlags.Foliage | MaterialFlags.TwoSided : 0);

    private TextureHandle? Texture(TextureRef? texture, Content content) =>
        texture is { } named ? Require<LoadedTexture>(named.Index, content).Handle : null;

    private T Require<T>(int index, Content content)
        where T : Loaded =>
        _items.Get(index, content) as T
            ?? throw new InvalidOperationException($"Item {index} was unloaded, so nothing can use it.");

    private DrawItem[] Parts(int index, Content content) => Model(index, content).Parts;

    /// <summary>What a model is called in reports: its path, the built-in shape's name, or what it is.</summary>
    private static string Name(int index, Content content, string generated) => index switch
    {
        0 => "cube",
        < 0 => Shapes.BuiltInNames[-index - 1],
        _ => content.Items[index - 1] is string path ? path : generated,
    };

    /// <summary>A model by its ref's index. The built-in shapes other than the cube sit below zero, outside the content.</summary>
    private LoadedModel Model(int index, Content content) => index < 0 ? _shapes[-index - 1] : Require<LoadedModel>(index, content);

    /// <summary>Frees what an item made. A model from a file owns its materials and textures; a generated mesh owns one of each.</summary>
    private void Destroy(Loaded loaded)
    {
        switch (loaded)
        {
            case LoadedModel { Material: { } material } generated:
                _renderer.DestroyMesh(generated.Parts[0].Mesh);
                _renderer.DestroyMaterial(material);
                if (generated.OwnedTexture is { } owned)
                {
                    _renderer.DestroyTexture(owned);
                }

                break;
            case LoadedModel model:
                _renderer.DestroyModel(model.Parts);
                break;
            case LoadedTexture texture:
                _renderer.DestroyTexture(texture.Handle);
                break;
            case Unloaded:
                break;
            default:
                throw new InvalidOperationException($"Content can't unload a {loaded.GetType().Name}.");
        }
    }

    private int PrepareDraws(View view, Content content)
    {
        int count = 0;
        _boneCount = 0;
        foreach (ref readonly var draw in view.Drawn)
        {
            var model = Model(draw.Model.Index, content);
            int first = count;
            count = AddShown(model.Parts, draw.Meshes, ref _draws, count);
            if (count == first)
            {
                continue;
            }

            // Skinned once per draw, however many of its parts are shown.
            var bones = draw.Pose is { } pose ? Skin(pose, draw.PoseAhead, model, draw.Model.Index, content) : default;
            var partBounds = draw.Static ? BoundsOf(model.Parts) : default;
            Dress(_draws.AsSpan(first, count - first), in draw, bones, partBounds, content, view.BakedSpace, view.PlacedBakedChunks, _bakeScratch);
        }

        return count;
    }

    /// <summary>The sphere around each of a model's parts, in the model's units, kept in a buffer reused each draw.</summary>
    private ReadOnlySpan<BakeSphere> BoundsOf(DrawItem[] parts)
    {
        if (_partBounds.Length < parts.Length)
        {
            _partBounds = new BakeSphere[Math.Max(parts.Length, _partBounds.Length * 2)];
        }

        for (int p = 0; p < parts.Length; p++)
        {
            _partBounds[p] = _renderer.BoundsOf(parts[p].Detail);
        }

        return _partBounds.AsSpan(0, parts.Length);
    }

    /// <summary>
    /// Gives the parts of one draw that <see cref="AddShown"/> copied what the draw says about them. A static draw's
    /// parts get bake ids, from <paramref name="partBounds"/>, which holds every part of the model; others keep zero.
    /// Throws for a static draw of a mesh made in code without <c>isStatic</c>, which has no lightmap UVs.
    /// </summary>
    internal static void Dress(
        Span<DrawItem> shown,
        in ModelDraw draw,
        BoneRange bones,
        ReadOnlySpan<BakeSphere> partBounds,
        Content content,
        BakedSpace? space,
        ReadOnlySpan<BakedChunk> chunks,
        BakeIdentity.Scratch scratch)
    {
        var tint = draw.Color ?? (draw.Model.IsBuiltIn ? CubeColor : null);
        for (int i = 0; i < shown.Length; i++)
        {
            ref var item = ref shown[i];
            item.Transform = draw.Transform;
            item.Swim = draw.Swim;
            item.Tint = tint;
            item.Bones = bones;
            item.Ghost = draw.Ghost;
            item.Key = draw.Key;
        }

        if (!draw.Static)
        {
            return;
        }

        if (!content.MadeStatic(draw.Model))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Model {draw.Model.Index} is a mesh made in code and drawn static, but it wasn't made static, so it has no lightmap UVs. Pass isStatic: true to Content.Mesh."));
        }

        if (partBounds.Length > BakeIds.MaxParts)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Model {draw.Model.Index} ({Name(draw.Model.Index, content, "a mesh made in code")}) is drawn static with {partBounds.Length} parts, but a static model can have at most {BakeIds.MaxParts}. Draw it without isStatic, or split it."));
        }

        BakeIdentity.Assign(shown, draw.Meshes, partBounds, content.IdentityOf(draw.Model), tint, draw.Transform, space, chunks, scratch);
    }

    /// <summary>
    /// Copies the parts a mask shows onto the end of <paramref name="draws"/>, from
    /// <paramref name="count"/> on, growing it when needed, and gives the new count. A null mask
    /// shows every part. Throws if the mask was made for a model with another part count.
    /// </summary>
    internal static int AddShown(DrawItem[] parts, MeshMask? mask, ref DrawItem[] draws, int count)
    {
        int kept = KeptCount(parts.Length, mask);
        if (count + kept > draws.Length)
        {
            Array.Resize(ref draws, (count + kept) * 2);
        }

        for (int p = 0; p < parts.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                draws[count++] = parts[p];
            }
        }

        return count;
    }

    /// <summary>The triangles a draw with this mask shows. Throws if the mask was made for a model with another part count.</summary>
    internal static long ShownTriangles(DrawItem[] parts, MeshMask? mask)
    {
        KeptCount(parts.Length, mask);
        long indices = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            if (mask is null || mask.Shows(p))
            {
                indices += parts[p].IndexCount;
            }
        }

        return indices / 3;
    }

    /// <summary>How many of a model's parts a draw shows. A null mask shows them all. Throws if the mask was made for a model with another part count.</summary>
    internal static int KeptCount(int partCount, MeshMask? mask)
    {
        if (mask is null)
        {
            return partCount;
        }

        if (mask.Count != partCount)
        {
            throw new InvalidOperationException(
                $"The mesh mask was made for a model drawn in {mask.Count} parts, but the model being drawn has {partCount}. Make it from the same cooked file.");
        }

        return mask.Kept;
    }

    /// <summary>
    /// Samples a pose into this frame's bone matrices. Once the frame's bones are full,
    /// the rest of the models stand in their bind pose rather than fail.
    /// </summary>
    private BoneRange Skin(AnimationPose pose, float ahead, LoadedModel model, int index, Content content)
    {
        int joints = pose.Rig.Skeleton.Count;
        if (model.JointCount == 0)
        {
            string name = Name(index, content, "a generated mesh");
            throw new InvalidOperationException($"Model {index} ({name}) has no skeleton, so it can't be drawn with a pose.");
        }

        if (joints != model.JointCount)
        {
            throw new InvalidOperationException(
                $"The pose's rig has {joints} joints, but model {index} has {model.JointCount}. Use the rig from the same cooked file.");
        }

        if (_boneCount + joints > Renderer.MaxBones)
        {
            return default;
        }

        if (_boneCount + joints > _bones.Length)
        {
            Array.Resize(ref _bones, Math.Min(Math.Max(_bones.Length * 2, _boneCount + joints), Renderer.MaxBones));
        }

        var local = _pose.AsSpan(0, joints);
        pose.Evaluate(ahead, local, _poseScratch.AsSpan(0, joints));
        pose.Rig.Skeleton.SkinMatrices(local, _bones.AsSpan(_boneCount, joints));
        var range = new BoneRange(_boneCount, joints);
        _boneCount += joints;
        return range;
    }

    /// <summary>Where the haze is measured from: the camera's focus when the sky asks for it, else the camera.</summary>
    internal static Vector3? HazeOrigin(SkySettings? sky, Camera shot) => sky is { HazeFromFocus: true } ? shot.Target : null;

    /// <summary>The fog the sky asks for, or clear air without a sky.</summary>
    internal static FogSettings FrameFog(SkySettings? sky) => sky?.Fog ?? default;

    /// <summary>Which sun casts the shadows: the one marked to, or else the first.</summary>
    internal static int ShadowSun(ReadOnlySpan<SunLight> suns)
    {
        for (int i = 0; i < suns.Length; i++)
        {
            if (suns[i].CastsShadows)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The sun's shadows as the view asks for them, round its camera's target, for a picture this wide over its height.</summary>
    internal static ShadowSettings FrameShadows(View view, float aspect)
    {
        var shot = view.Shot;
        return new ShadowSettings
        {
            Center = shot.Target,
            Radius = shot.ShadowRadius(view.ShadowRadius, aspect),
            Cascades = view.ShadowCascades,
            LightAngle = view.SunSize,
            ContactLength = view.ShadowContact,
        };
    }

    private FrameView CreateFrame(View view, int width, int height, float time)
    {
        var suns = view.Suns;
        int lightCount = Math.Min(suns.Length, MaxLights);
        int shadowSun = ShadowSun(suns);
        for (int i = 0; i < lightCount; i++)
        {
            _lights[i] = new DirectionalLight
            {
                Direction = suns[i].Direction,
                Radiance = suns[i].Color,
                CastsShadow = i == shadowSun,
                Mode = suns[i].Mode,
            };
        }

        var shot = view.Shot;
        var sky = view.SkySettings;
        float aspect = width / (float)Math.Max(height, 1);
        return new FrameView
        {
            Width = width,
            Height = height,
            View = Matrix4x4.CreateLookAt(shot.Position, shot.Target, shot.Up),
            Projection = shot.Matrix(aspect),
            CameraPosition = shot.Eye,
            Time = time,
            DebugView = DebugView,
            Lights = _lights.AsSpan(0, lightCount),
            Bones = _bones.AsSpan(0, _boneCount),
            Shadows = FrameShadows(view, aspect),
            Occlusion = view.Occlusion,
            LightProbes = view.LightProbeVolume,
            BakedSpace = view.BakedSpace,
            BakedChunks = view.PlacedBakedChunks,
            EnvironmentIntensity = sky is null ? 0.8f : sky.Value.Brightness > 0f ? sky.Value.Brightness : 1f,
            Sky = FrameSky(view),
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            ShowEnvironment = sky is not null,
            BackgroundFadeStart = sky?.HazeStart ?? 0f,
            BackgroundFadeEnd = sky?.HazeEnd ?? 0f,
            BackgroundFadeOrigin = HazeOrigin(sky, shot),
            BackgroundFadeColor = sky?.HazeColor ?? Vector3.Zero,
            Fog = FrameFog(sky),
            Post = view.Post,
            Water = view.WaterSettings,
            Puddles = view.PuddleSettings,
            Wind = view.WindSettings,
            Overlay = view.Ui is { } ui ? ui.Quads : [],
            Particles = view.DrawnParticles,
            Decals = _decals.AsSpan(0, _decalCount),
            Transition = view.Transition,
            Off = Off,
            LocalLights = view.DrawnLights,
        };
    }

    private sealed record PreparedMesh(GeneratedMesh Mesh, TextureData? OwnedTexture);

    /// <summary>A surface shader's compiled code, and its name so live shader reload can read the file again.</summary>
    private sealed record PreparedShader(string Name, byte[] Spirv);

    /// <summary>A model whose materials name surface shaders, with the shaders and textures they read.</summary>
    internal sealed record PreparedModel(ModelData Model, Dictionary<string, byte[]> Shaders, Dictionary<string, TextureData> Textures);

    private abstract record Loaded;

    /// <summary>
    /// A model's parts. Material and OwnedTexture are set for a generated mesh, which made
    /// them itself. JointCount is 0 for a model with no skeleton.
    /// </summary>
    private sealed record LoadedModel(DrawItem[] Parts, MaterialHandle? Material, TextureHandle? OwnedTexture, int JointCount = 0) : Loaded;

    private sealed record LoadedShader(SurfaceShaderHandle Handle) : Loaded;

    private sealed record LoadedTexture(TextureHandle Handle) : Loaded;

    private sealed record LoadedTextureArray(TextureArrayHandle Handle) : Loaded;

    /// <summary>Stands where an item used to be, so using it again fails clearly.</summary>
    private sealed record Unloaded : Loaded
    {
        public static Unloaded Instance { get; } = new();
    }
}
