using System.Numerics;
using Rendition;

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

    /// <summary>The parts of each model, by <see cref="ModelRef.Index"/>.</summary>
    private readonly List<DrawItem[]> _models = [];

    private DrawItem[] _draws = new DrawItem[64];
    private Vector3 _bakedWater;

    public SceneRenderer(RendererOptions options)
    {
        _renderer = new Renderer(options);
        var cube = _renderer.CreateMesh(Cube.Vertices, Cube.Indices);
        _defaultMaterial = _renderer.CreateMaterial(CreateMaterial(new Vector3(0.7f)));
        _models.Add(
        [
            new DrawItem { Mesh = cube, IndexCount = (uint)Cube.Indices.Length, Material = _defaultMaterial },
        ]);
        _renderer.SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.45f, 0.5f, 0.6f),
            Horizon = new Vector3(0.5f, 0.5f, 0.52f),
            Floor = new Vector3(0.25f, 0.24f, 0.23f),
        });
    }

    public int ValidationErrors => _renderer.ValidationErrors;

    public void Render(View view, Content content, int width, int height, float time)
    {
        Prepare(content, view);
        int count = PrepareDraws(view);
        _renderer.Render(CreateFrame(view, width, height, time), _draws.AsSpan(0, count));
    }

    public byte[] Capture(View view, Content content, int width, int height, float time, out int capturedWidth, out int capturedHeight)
    {
        Prepare(content, view);
        int count = PrepareDraws(view);
        return _renderer.Capture(CreateFrame(view, width, height, time), _draws.AsSpan(0, count), out capturedWidth, out capturedHeight);
    }

    public void Dispose() => _renderer.Dispose();

    private static Material CreateMaterial(Vector3 color) => new()
    {
        BaseColor = new Vector4(color, 1f),
        Roughness = DefaultRoughness,
    };

    private static string FindCooked(string path)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, CookedFolder, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {path} in {CookedFolder}. Assets aren't in git, so it may need cooking first.", path);
    }

    private static byte[][] BuildMips(GeneratedTexture texture)
    {
        var mips = new List<byte[]> { texture.Pixels };
        for (int size = texture.Size; size > 1; size /= 2)
        {
            byte[] source = mips[^1];
            int half = size / 2;
            byte[] mip = new byte[half * half * 4];
            for (int y = 0; y < half; y++)
            {
                for (int x = 0; x < half; x++)
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int top = (y * 2 * size + x * 2) * 4 + channel;
                        int bottom = top + size * 4;
                        int sum = source[top] + source[top + 4] + source[bottom] + source[bottom + 4];
                        mip[(y * half + x) * 4 + channel] = (byte)(sum / 4);
                    }
                }
            }

            mips.Add(mip);
        }

        return mips.ToArray();
    }

    private void Prepare(Content content, View view)
    {
        while (_models.Count <= content.Items.Count)
        {
            _models.Add(Load(content.Items[_models.Count - 1]));
        }

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
    }

    private DrawItem[] Load(object item)
    {
        if (item is string path)
        {
            return _renderer.CreateModel(ModelData.Read(FindCooked(path)));
        }

        var mesh = (GeneratedMesh)item;
        var texture = _renderer.CreateTexture(new TextureData
        {
            Width = mesh.Texture.Size,
            Height = mesh.Texture.Size,
            Kind = TextureKind.Color,
            Mips = BuildMips(mesh.Texture),
        });
        var material = _renderer.CreateMaterial(new Material
        {
            BaseColor = Vector4.One,
            Roughness = mesh.Roughness,
            BaseColorTexture = texture,
        });
        return
        [
            new DrawItem
            {
                Mesh = _renderer.CreateMesh(mesh.Vertices, mesh.Indices),
                IndexCount = (uint)mesh.Indices.Length,
                Material = material,
            },
        ];
    }

    private int PrepareDraws(View view)
    {
        int count = 0;
        foreach (ref readonly var draw in view.Drawn)
        {
            var parts = _models[draw.Model.Index];
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

        return new FrameView
        {
            Width = width,
            Height = height,
            View = Matrix4x4.CreateLookAt(view.CameraPosition, view.CameraTarget, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(
                view.CameraFieldOfView, width / (float)Math.Max(height, 1), 0.1f),
            CameraPosition = view.CameraPosition,
            Time = time,
            Lights = _lights.AsSpan(0, lightCount),
            Shadows = new ShadowSettings { Center = view.CameraTarget, Radius = view.ShadowRadius, LightAngle = 0.12f },
            EnvironmentIntensity = 0.8f,
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            Post = view.Post,
            Water = view.WaterSettings,
        };
    }

    private readonly record struct ColoredMaterial(MaterialHandle Handle, Vector3 Color);
}
