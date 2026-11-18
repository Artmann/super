using System.Numerics;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

/// <summary>
/// A hidden 256 by 256 surface (see <see cref="ITestSurface"/>) and a <see cref="Rendition.Renderer"/> on it with
/// validation turned on. One is shared by every GPU test, which run one at a time.
/// </summary>
public sealed class GpuFixture : IDisposable
{
    private const int Size = 256;

    private readonly ITestSurface _surface;
    private readonly int _width;
    private readonly int _height;

    public Renderer Renderer { get; }

    /// <summary>The Vulkan messages the validation layers have reported so far.</summary>
    public int ValidationErrors => Renderer.ValidationErrors;

    /// <summary>A plain grey material: opaque, with no textures.</summary>
    public MaterialHandle Matte { get; }

    internal VulkanContext Context => Renderer.Context;

    public GpuFixture()
        : this(disableRayTracing: false)
    {
    }

    /// <summary>
    /// A fixture of its own, alongside the shared one. Pass true to make its device without ray queries, or to
    /// make a renderer that can swap in shaders compiled again.
    /// </summary>
    internal GpuFixture(bool disableRayTracing, bool liveShaders = false)
    {
        _surface = ITestSurface.Create(Size);
        _width = _surface.Width;
        _height = _surface.Height;
        try
        {
            Renderer = new Renderer(new RendererOptions
            {
                InstanceExtensions = _surface.InstanceExtensions,
                CreateSurface = _surface.CreateSurface,
                Width = _width,
                Height = _height,
                EnableValidation = true,
                DisableRayTracing = disableRayTracing,
                LiveShaders = liveShaders,
            });
            // Most tests draw an area live and then with its bake at one fixed time, which a fade would keep live, and
            // the renderer is shared, so a fade would tie each test to the ones before it. BakeFadeGpuTests turn it on.
            Renderer.BakeFadeSeconds = 0f;
            Matte = Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.8f });
        }
        catch
        {
            _surface.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renders one frame of the draws given, or of nothing, which also runs every copy waiting to ride a frame, such as
    /// a new mesh's. The effects in <paramref name="off"/> are switched off for the frame.
    /// </summary>
    public void RenderOneFrame(DrawItem[]? draws = null, DebugView debugView = DebugView.None, RenderEffects off = RenderEffects.None)
    {
        var view = View(debugView);
        view.Off = off;
        Renderer.Render(view, draws ?? []);
    }

    /// <summary>A new white metal, nearly smooth, that mirrors what's round it. The caller destroys it.</summary>
    public MaterialHandle CreateMirror() => Renderer.CreateMaterial(new Material
    {
        BaseColor = new Vector4(1f, 1f, 1f, 1f), Metallic = 1f, Roughness = 0.05f,
    });

    /// <summary>Renders one frame of nothing, as <see cref="RenderOneFrame"/> does, naming a baked space and chunks.</summary>
    public void RenderOneFrame(BakedSpace? bakedSpace, BakedChunk[] bakedChunks)
    {
        var view = View(DebugView.None);
        view.BakedSpace = bakedSpace;
        view.BakedChunks = bakedChunks;
        Renderer.Render(view, []);
    }

    /// <summary>Renders one frame of nothing, as <see cref="RenderOneFrame"/> does, under the sky and environment strength given.</summary>
    public void RenderOneFrame(AnalyticSky sky, float environmentIntensity)
    {
        var view = View(DebugView.None);
        view.Sky = sky;
        view.EnvironmentIntensity = environmentIntensity;
        Renderer.Render(view, []);
    }

    /// <summary>Renders one frame of the draws given, as <see cref="RenderOneFrame"/> does, and reads its pixels back.</summary>
    public byte[] CaptureOneFrame(DrawItem[]? draws = null) =>
        Renderer.Capture(View(DebugView.None), draws ?? [], out _, out _);

    /// <summary>The view every frame here is drawn from: the window's size, looking at the origin from 3 metres.</summary>
    internal FrameView View(DebugView debugView)
    {
        return new FrameView
        {
            Width = _width,
            Height = _height,
            View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, _width / (float)_height, 0.1f),
            CameraPosition = new Vector3(0, 0, 3),
            DebugView = debugView,
        };
    }

    public void Dispose()
    {
        Renderer.Dispose();
        _surface.Dispose();
    }
}

/// <summary>A GPU and its window are process-wide, so tests that use them run one at a time, on one shared renderer.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuCollection : ICollectionFixture<GpuFixture>
{
    public const string Name = "GPU";
}
