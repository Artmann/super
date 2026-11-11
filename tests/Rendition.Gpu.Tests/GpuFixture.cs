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

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;

    public Renderer Renderer { get; }

    /// <summary>The Vulkan messages the validation layers have reported so far.</summary>
    public int ValidationErrors => Renderer.ValidationErrors;

    internal VulkanContext Context => Renderer.Context;

    public GpuFixture()
    {
        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
        {
            throw new InvalidOperationException("Could not initialize GLFW.");
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        _glfw.WindowHint(WindowHintBool.Visible, false);
        _window = _glfw.CreateWindow(Size, Size, "Rendition GPU tests", null, null);
        if (_window is null)
        {
            _glfw.Terminate();
            throw new InvalidOperationException("Could not create a window.");
        }

        try
        {
            _glfw.GetFramebufferSize(_window, out int width, out int height);
            Renderer = new Renderer(new RendererOptions
            {
                InstanceExtensions = InstanceExtensions(),
                CreateSurface = CreateSurface,
                Width = width,
                Height = height,
                EnableValidation = true,
            });
        }
        catch
        {
            _glfw.DestroyWindow(_window);
            _glfw.Terminate();
            throw;
        }
    }

    public void Dispose()
    {
        Renderer.Dispose();
        _glfw.DestroyWindow(_window);
        _glfw.Terminate();
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
