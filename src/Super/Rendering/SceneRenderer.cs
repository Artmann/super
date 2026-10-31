using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Turns what a <see cref="View"/> wrote down into Rendition draws.</summary>
internal sealed class SceneRenderer : IDisposable
{
    private const int MaxLights = 8;
    private const float DefaultRoughness = 0.4f;

    private readonly Renderer _renderer;
    private readonly MeshHandle _cube;
    private readonly MaterialHandle _defaultMaterial;
    private readonly DirectionalLight[] _lights = new DirectionalLight[MaxLights];
    private readonly Dictionary<int, ColoredMaterial> _coloredMaterials = [];
    private DrawItem[] _draws = new DrawItem[64];

    public SceneRenderer(RendererOptions options)
    {
        _renderer = new Renderer(options);
        _cube = _renderer.CreateMesh(Cube.Vertices, Cube.Indices);
        _defaultMaterial = _renderer.CreateMaterial(CreateMaterial(new Vector3(0.7f)));
        _renderer.SetEnvironment(new EnvironmentDescription
        {
            Ceiling = new Vector3(0.45f, 0.5f, 0.6f),
            Horizon = new Vector3(0.5f, 0.5f, 0.52f),
            Floor = new Vector3(0.25f, 0.24f, 0.23f),
        });
    }

    public int ValidationErrors => _renderer.ValidationErrors;

    public void Render(View view, int width, int height, float time)
    {
        int count = PrepareDraws(view);
        _renderer.Render(CreateFrame(view, width, height, time), _draws.AsSpan(0, count));
    }

    public byte[] Capture(View view, int width, int height, float time, out int capturedWidth, out int capturedHeight)
    {
        int count = PrepareDraws(view);
        return _renderer.Capture(CreateFrame(view, width, height, time), _draws.AsSpan(0, count), out capturedWidth, out capturedHeight);
    }

    public void Dispose() => _renderer.Dispose();

    private static Material CreateMaterial(Vector3 color) => new()
    {
        BaseColor = new Vector4(color, 1f),
        Roughness = DefaultRoughness,
    };

    private int PrepareDraws(View view)
    {
        var drawn = view.Drawn;
        if (drawn.Length > _draws.Length)
        {
            Array.Resize(ref _draws, drawn.Length * 2);
        }

        for (int i = 0; i < drawn.Length; i++)
        {
            ref readonly var draw = ref drawn[i];
            _draws[i] = new DrawItem
            {
                Mesh = _cube,
                IndexCount = (uint)Cube.Indices.Length,
                Material = draw.Color is { } color ? MaterialFor(draw.Key, color) : _defaultMaterial,
                Transform = draw.Transform,
            };
        }

        return drawn.Length;
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
            Shadows = new ShadowSettings { Center = view.CameraTarget, Radius = 8f, LightAngle = 0.12f },
            EnvironmentIntensity = 0.8f,
            BackgroundRadiance = new Vector3(0.15f, 0.16f, 0.18f),
            Post = new PostSettings { Vignette = 0.3f, Sharpen = 0.4f },
        };
    }

    private readonly record struct ColoredMaterial(MaterialHandle Handle, Vector3 Color);
}
