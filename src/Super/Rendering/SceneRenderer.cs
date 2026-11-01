using System.Numerics;
using Rendition;
using StbImageSharp;
using Super.Profiling;
using Super.Ui;

namespace Super;

/// <summary>Turns what a <see cref="View"/> wrote down into Rendition draws.</summary>
internal sealed class SceneRenderer : IDisposable
{
    private const int MaxLights = 8;
    private const float DefaultRoughness = 0.4f;
    private const string CookedFolder = "assets/cooked";

    private readonly Renderer _renderer;
    private readonly MaterialHandle _defaultMaterial;
    private readonly DirectionalLight[] _lights = new DirectionalLight[MaxLights];
    private readonly Dictionary<int, ColoredMaterial> _coloredMaterials = [];

    /// <summary>Everything the content named, by the index its ref carries.</summary>
    private readonly ContentLoader<Loaded> _items;

    private DrawItem[] _draws = new DrawItem[64];
    private Vector3 _bakedWater;
    private int _uploadedAtlas;

    public SceneRenderer(RendererOptions options)
    {
        _renderer = new Renderer(options);
        _defaultMaterial = _renderer.CreateMaterial(CreateMaterial(new Vector3(0.7f)));
        _items = new ContentLoader<Loaded>(
            new LoadedModel([_renderer.CreateDrawItem(Cube.Vertices, Cube.Indices, _defaultMaterial)], null, null),
            PrepareItem,
            Upload);
        _renderer.SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.45f, 0.5f, 0.6f),
            Horizon = new Vector3(0.5f, 0.5f, 0.52f),
            Floor = new Vector3(0.25f, 0.24f, 0.23f),
        });
    }

    public int ValidationErrors => _renderer.ValidationErrors;

    public bool MeasureGpu
    {
        set => _renderer.MeasureGpu = value;
    }

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

    /// <summary>Adds up what the view drew, model by model.</summary>
    internal List<ModelCost> CountModels(View view, Content content)
    {
        var copies = new Dictionary<int, int>();
        foreach (ref readonly var draw in view.Drawn)
        {
            copies[draw.Model.Index] = copies.GetValueOrDefault(draw.Model.Index) + 1;
        }

        var costs = new List<ModelCost>();
        foreach (var (model, count) in copies)
        {
            string name = model > 0 && content.Items[model - 1] is string path ? path : model > 0 ? "generated mesh" : "cube";
            costs.Add(new ModelCost(name, count, Parts(model, content).Sum(part => (long)part.IndexCount) / 3));
        }

        return costs;
    }

    public void Dispose() => _renderer.Dispose();

    private static Material CreateMaterial(Vector3 color) => new()
    {
        BaseColor = new Vector4(color, 1f),
        Roughness = DefaultRoughness,
    };

    private static string FindCooked(string path)
    {
        foreach (string folder in ContentFolder.Upward())
        {
            string candidate = Path.Combine(folder, CookedFolder, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {path} in {CookedFolder}. Assets aren't in git, so it may need cooking first.", path);
    }

    /// <summary>Decodes a named texture and builds its mips. Runs on the background thread.</summary>
    internal static TextureData LoadTexture(TextureItem item)
    {
        if (item.Generated is { } generated)
        {
            return TextureData.FromPixels(generated.Size, generated.Size, item.Kind, generated.Pixels, item.NormalMap);
        }

        var image = ImageResult.FromMemory(item.Files.Read(item.Path!), ColorComponents.RedGreenBlueAlpha);
        return TextureData.FromPixels(image.Width, image.Height, item.Kind, image.Data, item.NormalMap);
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
        _items.Update(content);
        foreach (int index in content.PendingUnloads)
        {
            Destroy(_items.Get(index, content));
            _items.Replace(index, Unloaded.Instance);
        }

        content.ClearPendingUnloads();

        // Surfaces pick up the color of the water around them.
        var water = view.WaterSettings;
        if (water.Absorption != Vector3.Zero && water.ShallowColor != _bakedWater)
        {
            _renderer.SetEnvironment(new EnvironmentDescription
            {
                Ceiling = water.ShallowColor,
                Horizon = Vector3.Lerp(water.ShallowColor, water.DeepColor, 0.5f),
                Floor = water.DeepColor,
            });
            _bakedWater = water.ShallowColor;
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
    }

    /// <summary>The part of loading that needs no GPU, so it runs on a background thread.</summary>
    private static object PrepareItem(object item) => item switch
    {
        string path => ModelData.Read(FindCooked(path)),
        ShaderItem shader => LoadShader(shader.Name),
        TextureItem texture => LoadTexture(texture),
        TextureArrayItem array => Array.ConvertAll(array.Layers, LoadTexture),
        GeneratedMesh mesh => new PreparedMesh(
            mesh,
            mesh.OwnedTexture is { } owned ? TextureData.FromPixels(owned.Size, owned.Size, TextureKind.Color, owned.Pixels) : null),
        _ => throw new InvalidOperationException($"Content can't load a {item.GetType().Name}."),
    };

    private Loaded Upload(object prepared, Content content) => prepared switch
    {
        ModelData model => new LoadedModel(_renderer.CreateModel(model), null, null),
        byte[] spirv => new LoadedShader(_renderer.CreateSurfaceShader(spirv)),
        TextureData texture => new LoadedTexture(_renderer.CreateTexture(texture)),
        TextureData[] layers => new LoadedTextureArray(_renderer.CreateTextureArray(layers)),
        PreparedMesh(var mesh, var owned) => UploadMesh(mesh, owned, content),
        _ => throw new InvalidOperationException($"Content can't upload a {prepared.GetType().Name}."),
    };

    /// <summary>Each generated mesh gets its own material, built from what its surface named.</summary>
    private LoadedModel UploadMesh(GeneratedMesh mesh, TextureData? owned, Content content)
    {
        var surface = mesh.Surface;
        TextureHandle? ownedTexture = owned is null ? null : _renderer.CreateTexture(owned);
        var material = new Material
        {
            BaseColor = new Vector4(surface.Color, 1f),
            Roughness = surface.Roughness,
            Metallic = surface.Metallic,
            BaseColorTexture = ownedTexture ?? Texture(surface.ColorTexture, content),
            NormalTexture = Texture(surface.NormalTexture, content),
            SurfaceTexture = Texture(surface.SurfaceTexture, content),
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

    private TextureHandle? Texture(TextureRef? texture, Content content) =>
        texture is { } named ? Require<LoadedTexture>(named.Index, content).Handle : null;

    private T Require<T>(int index, Content content)
        where T : Loaded =>
        _items.Get(index, content) as T
            ?? throw new InvalidOperationException($"Item {index} was unloaded, so nothing can use it.");

    private DrawItem[] Parts(int index, Content content) => Require<LoadedModel>(index, content).Parts;

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
        }
    }

    private int PrepareDraws(View view, Content content)
    {
        int count = 0;
        foreach (ref readonly var draw in view.Drawn)
        {
            var parts = Parts(draw.Model.Index, content);
            if (count + parts.Length > _draws.Length)
            {
                Array.Resize(ref _draws, (count + parts.Length) * 2);
            }

            foreach (ref readonly var part in parts.AsSpan())
            {
                ref var item = ref _draws[count++];
                item = part;
                item.Transform = draw.Transform;
                item.Swim = draw.Swim;
                if (draw.Color is { } color)
                {
                    item.Material = MaterialFor(draw.Key, color);
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Each key that asks for a color gets one material and recolors it in place. The
    /// renderer has a fixed number of materials, so one per color would run out.
    /// </summary>
    private MaterialHandle MaterialFor(int key, Vector3 color)
    {
        if (!_coloredMaterials.TryGetValue(key, out var colored))
        {
            colored = new ColoredMaterial(_renderer.CreateMaterial(CreateMaterial(color)), color);
            _coloredMaterials.Add(key, colored);
        }
        else if (colored.Color != color)
        {
            _renderer.UpdateMaterial(colored.Handle, CreateMaterial(color));
            _coloredMaterials[key] = colored with { Color = color };
        }

        return colored.Handle;
    }

    private FrameView CreateFrame(View view, int width, int height, float time)
    {
        var suns = view.Suns;
        int lightCount = Math.Min(suns.Length, MaxLights);
        for (int i = 0; i < lightCount; i++)
        {
            _lights[i] = new DirectionalLight
            {
                Direction = suns[i].Direction,
                Radiance = suns[i].Color,
                CastsShadow = i == 0,
            };
        }

        var shot = view.Shot;
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
            Shadows = new ShadowSettings
            {
                Center = shot.Target,
                Radius = shot.ShadowRadius(view.ShadowRadius, aspect),
                Cascades = view.ShadowCascades,
                LightAngle = 0.12f,
            },
            EnvironmentIntensity = 0.8f,
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            Post = view.Post,
            Water = view.WaterSettings,
            Overlay = view.Ui is { } ui ? ui.Quads : [],
        };
    }

    private readonly record struct ColoredMaterial(MaterialHandle Handle, Vector3 Color);

    private sealed record PreparedMesh(GeneratedMesh Mesh, TextureData? OwnedTexture);

    private abstract record Loaded;

    /// <summary>A model's parts. Material and OwnedTexture are set for a generated mesh, which made them itself.</summary>
    private sealed record LoadedModel(DrawItem[] Parts, MaterialHandle? Material, TextureHandle? OwnedTexture) : Loaded;

    private sealed record LoadedShader(SurfaceShaderHandle Handle) : Loaded;

    private sealed record LoadedTexture(TextureHandle Handle) : Loaded;

    private sealed record LoadedTextureArray(TextureArrayHandle Handle) : Loaded;

    /// <summary>Stands where an item used to be, so using it again fails clearly.</summary>
    private sealed record Unloaded : Loaded
    {
        public static Unloaded Instance { get; } = new();
    }
}
