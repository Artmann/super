using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Rendition.Gpu.Tests;

/// <summary>
/// What the GPU tests' renderer draws to. A hidden GLFW window, made the way the game's window is, or on a Mac a
/// surface with no window at all: macOS only lets the main thread make windows, and xunit makes fixtures on a pool
/// thread, where GLFW would wait forever.
/// </summary>
internal interface ITestSurface : IDisposable
{
    int Width { get; }
    int Height { get; }
    IReadOnlyList<string> InstanceExtensions { get; }
    ulong CreateSurface(nint instance);

    static ITestSurface Create(int size) => OperatingSystem.IsMacOS() ? new HeadlessSurface(size) : new GlfwSurface(size);
}

/// <summary>A hidden GLFW window.</summary>
internal sealed unsafe class GlfwSurface : ITestSurface
{
    // GLFW is process-wide, and terminating it closes every window, so it ends with the last window.
    private static readonly Lock GlfwLock = new();
    private static int _glfwUsers;

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;

    public GlfwSurface(int size)
    {
        _glfw = Glfw.GetApi();
        lock (GlfwLock)
        {
            _glfw.Context.TryGetProcAddress("glfwInitVulkanLoader", out nint initVulkanLoader);
            VulkanLibrary.ShareWithGlfw(initVulkanLoader);
            if (!_glfw.Init())
            {
                throw new InvalidOperationException("Could not initialize GLFW.");
            }

            _glfwUsers++;
        }

        _glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        _glfw.WindowHint(WindowHintBool.Visible, false);
        _window = _glfw.CreateWindow(size, size, "Rendition GPU tests", null, null);
        if (_window is null)
        {
            Release();
            throw new InvalidOperationException("Could not create a window.");
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        Width = width;
        Height = height;
        byte** names = _glfw.GetRequiredInstanceExtensions(out uint count);
        var extensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        InstanceExtensions = extensions;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<string> InstanceExtensions { get; }

    public ulong CreateSurface(nint instance)
    {
        VkNonDispatchableHandle surface;
        int result = _glfw.CreateWindowSurface(new VkHandle(instance), _window, null, &surface);
        return result == 0
            ? surface.Handle
            : throw new InvalidOperationException($"Could not create a window surface: {result}");
    }

    public void Dispose()
    {
        _glfw.DestroyWindow(_window);
        Release();
    }

    private void Release()
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
}

/// <summary>
/// A surface no window shows, from VK_EXT_headless_surface. Its swapchain takes the size the renderer asks for, and
/// presenting shows nothing, which suits tests that read their frames back.
/// </summary>
internal sealed unsafe class HeadlessSurface(int size) : ITestSurface
{
    private readonly Vk _api = VulkanLibrary.CreateApi();

    public int Width => size;
    public int Height => size;
    public IReadOnlyList<string> InstanceExtensions { get; } = ["VK_KHR_surface", ExtHeadlessSurface.ExtensionName];

    public ulong CreateSurface(nint instance)
    {
        var handle = new Instance(instance);
        if (!_api.TryGetInstanceExtension(handle, out ExtHeadlessSurface headless))
        {
            throw new InvalidOperationException($"{ExtHeadlessSurface.ExtensionName} is not available.");
        }

        var info = new HeadlessSurfaceCreateInfoEXT { SType = StructureType.HeadlessSurfaceCreateInfoExt };
        var result = headless.CreateHeadlessSurface(handle, &info, null, out var surface);
        return result == Result.Success
            ? surface.Handle
            : throw new InvalidOperationException($"Could not create a headless surface: {result}");
    }

    public void Dispose() => _api.Dispose();
}
