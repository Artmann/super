using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

namespace Rendition.Gpu.Tests;

/// <summary>
/// A hidden 256 by 256 window and a <see cref="Rendition.Renderer"/> on it with validation turned on, made the way
/// the game's window makes them. One is shared by every GPU test, which run one at a time.
/// </summary>
public sealed unsafe class GpuFixture : IDisposable
{
    private const int Size = 256;

    // GLFW is process-wide, and terminating it closes every window, so it ends with the last fixture.
    private static readonly Lock GlfwLock = new();
    private static int _glfwUsers;

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
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

    /// <summary>A fixture of its own, alongside the shared one. Pass true to make its device without ray queries.</summary>
    internal GpuFixture(bool disableRayTracing)
    {
        _glfw = Glfw.GetApi();
        lock (GlfwLock)
        {
            if (!_glfw.Init())
            {
                throw new InvalidOperationException("Could not initialize GLFW.");
            }

            _glfwUsers++;
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        _glfw.WindowHint(WindowHintBool.Visible, false);
        _window = _glfw.CreateWindow(Size, Size, "Rendition GPU tests", null, null);
        if (_window is null)
        {
            ReleaseGlfw();
            throw new InvalidOperationException("Could not create a window.");
        }

        try
        {
            _glfw.GetFramebufferSize(_window, out _width, out _height);
            Renderer = new Renderer(new RendererOptions
            {
                InstanceExtensions = InstanceExtensions(),
                CreateSurface = CreateSurface,
                Width = _width,
                Height = _height,
                EnableValidation = true,
                DisableRayTracing = disableRayTracing,
            });
            Matte = Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.8f });
        }
        catch
        {
            _glfw.DestroyWindow(_window);
            ReleaseGlfw();
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
        _glfw.DestroyWindow(_window);
        ReleaseGlfw();
    }

    private void ReleaseGlfw()
    {
        lock (GlfwLock)
        {
            _glfwUsers--;
            if (_glfwUsers == 0)
            {
                _glfw.Terminate();
            }
        }
    }

    private ulong CreateSurface(nint instance)
    {
        VkNonDispatchableHandle surface;
        int result = _glfw.CreateWindowSurface(new VkHandle(instance), _window, null, &surface);
        return result == 0
            ? surface.Handle
            : throw new InvalidOperationException($"Could not create a window surface: {result}");
    }

    private string[] InstanceExtensions()
    {
        byte** names = _glfw.GetRequiredInstanceExtensions(out uint count);
        var extensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        return extensions;
    }
}

/// <summary>A GPU and its window are process-wide, so tests that use them run one at a time, on one shared renderer.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuCollection : ICollectionFixture<GpuFixture>
{
    public const string Name = "GPU";
}
