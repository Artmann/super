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
    private Bands _bakedBands;
    private int _uploadedAtlas;
    private Content? _content;
    private IContentFiles? _files;

    public SceneRenderer(RendererOptions options)
    {
        _renderer = new Renderer(options);
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

    private readonly record struct Bands(Vector3 Ceiling, Vector3 Horizon, Vector3 Floor);

    // Water wins over the sky, which wins over the studio.
    private static Bands Surroundings(View view)
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

    /// <summary>The effects left out of every frame from now on.</summary>
    public RenderEffects Off { get; set; }

    public ReadOnlySpan<GpuPassTime> GpuTimes => _renderer.GpuTimes;

    public double WaitMilliseconds => _renderer.WaitMilliseconds;

    public (long Camera, long Shadows) DrawnTriangles => _renderer.DrawnTriangles;

    public void Render(View view, Content content, int width, int height, float time)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        _renderer.Render(CreateFrame(view, width, height, time), _draws.AsSpan(0, count));
    }

    public byte[] Capture(View view, Content content, int width, int height, float time, out int capturedWidth, out int capturedHeight)
    {
        Prepare(content, view);
        int count = PrepareDraws(view, content);
        return _renderer.Capture(CreateFrame(view, width, height, time), _draws.AsSpan(0, count), out capturedWidth, out capturedHeight);
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

    public void Dispose() => _renderer.Dispose();

    private static Material CreateMaterial(Vector3 color) => new()
    {
        BaseColor = new Vector4(color, 1f),
        Roughness = DefaultRoughness,
    };

    /// <summary>
    /// Reads a named texture and builds its mips. Runs on the background thread. A cooked
    /// texture's kind and normal-map flag come from its header, as its cook settings set them.
    /// </summary>
    internal static TextureData LoadTexture(TextureItem item)
    {
        if (item.Generated is { } generated)
        {
            return TextureData.FromPixels(generated.Size, generated.Size, item.Kind, generated.Pixels, item.NormalMap);
        }

        var cooked = CookedTexture.Read(item.Files.Read(item.Name!), item.Name!);
        return TextureData.FromPixels(cooked.Width, cooked.Height, cooked.Kind, cooked.Pixels, cooked.NormalMap);
    }

    /// <summary>Reads an interface image and builds its mips. Runs on the background thread.</summary>
    private static TextureData LoadImage(GameImage image)
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
            Mips = ImageMips.Build(cooked.Width, cooked.Height, cooked.Pixels),
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

    private static byte[] LoadShader(string name)
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
        // Set before anything new starts preparing, since models are read through it on background threads.
        _files = content.Files;
        _items.Update(content);
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
        var bands = Surroundings(view);
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
        string name => LoadModel(name, _files!),
        ShaderItem shader => LoadShader(shader.Name),
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
        byte[] spirv => new LoadedShader(_renderer.CreateSurfaceShader(spirv)),
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
            Flags = surface.DetailMask ? MaterialFlags.DetailMask : 0,
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

    /// <summary>A surface's own values as a material, before its textures and shader are found. Below full opacity it's glass.</summary>
    internal static Material MaterialOf(Surface surface) => new()
    {
        BaseColor = new Vector4(surface.Color, Math.Clamp(surface.Opacity, 0f, 1f)),
        Roughness = surface.Roughness,
        Metallic = surface.Metallic,
        Flags = surface.Opacity < 1f ? MaterialFlags.Transparent : MaterialFlags.None,
        Ior = surface.Ior,
    };

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
            for (int i = first; i < count; i++)
            {
                ref var item = ref _draws[i];
                item.Transform = draw.Transform;
                item.Swim = draw.Swim;
                item.Tint = draw.Color ?? (draw.Model.IsBuiltIn ? CubeColor : null);
                item.Bones = bones;
                item.Ghost = draw.Ghost;
            }
        }

        return count;
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
            Lights = _lights.AsSpan(0, lightCount),
            Bones = _bones.AsSpan(0, _boneCount),
            Shadows = new ShadowSettings
            {
                Center = shot.Target,
                Radius = shot.ShadowRadius(view.ShadowRadius, aspect),
                Cascades = view.ShadowCascades,
                LightAngle = view.SunSize,
            },
            EnvironmentIntensity = sky is null ? 0.8f : sky.Value.Brightness > 0f ? sky.Value.Brightness : 1f,
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            ShowEnvironment = sky is not null,
            BackgroundFadeStart = sky?.HazeStart ?? 0f,
            BackgroundFadeEnd = sky?.HazeEnd ?? 0f,
            BackgroundFadeOrigin = HazeOrigin(sky, shot),
            BackgroundFadeColor = sky?.HazeColor ?? Vector3.Zero,
            Post = view.Post,
            Water = view.WaterSettings,
            Overlay = view.Ui is { } ui ? ui.Quads : [],
            Particles = view.DrawnParticles,
            Transition = view.Transition,
            Off = Off,
            LocalLights = view.DrawnLights,
        };
    }

    private sealed record PreparedMesh(GeneratedMesh Mesh, TextureData? OwnedTexture);

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
