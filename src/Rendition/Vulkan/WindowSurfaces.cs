using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Rendition.Vulkan;

/// <summary>The Vulkan side of a <see cref="RendererSurface"/>: the instance extensions it needs, and its VkSurfaceKHR.</summary>
internal static unsafe class WindowSurfaces
{
    /// <summary>The instance extensions the surface needs.</summary>
    public static IReadOnlyList<string> Extensions(RendererSurface surface)
    {
        if (surface.Kind == RendererSurfaceKind.Headless)
        {
            return [KhrSurface.ExtensionName, ExtHeadlessSurface.ExtensionName];
        }

        byte** names = Glfw.GetApi().GetRequiredInstanceExtensions(out uint count);
        if (names is null)
        {
            throw new InvalidOperationException("GLFW can't make a Vulkan surface here: it found no Vulkan loader.");
        }

        var extensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        return extensions;
    }

    /// <summary>Makes the surface for <paramref name="instance"/>, which was made with its <see cref="Extensions"/>.</summary>
    public static SurfaceKHR Create(Vk api, Instance instance, RendererSurface surface) =>
        surface.Kind == RendererSurfaceKind.Headless ? CreateHeadless(api, instance) : CreateGlfw(instance, surface.GlfwWindow);

    private static SurfaceKHR CreateGlfw(Instance instance, nint window)
    {
        VkNonDispatchableHandle surface;
        int result = Glfw.GetApi().CreateWindowSurface(new VkHandle(instance.Handle), (WindowHandle*)window, null, &surface);
        return result == 0
            ? new SurfaceKHR(surface.Handle)
            : throw new InvalidOperationException($"Could not create a window surface: {result}");
    }

    private static SurfaceKHR CreateHeadless(Vk api, Instance instance)
    {
        if (!api.TryGetInstanceExtension(instance, out ExtHeadlessSurface headless))
        {
            throw new InvalidOperationException($"{ExtHeadlessSurface.ExtensionName} is not available.");
        }

        var info = new HeadlessSurfaceCreateInfoEXT { SType = StructureType.HeadlessSurfaceCreateInfoExt };
        var result = headless.CreateHeadlessSurface(instance, &info, null, out var surface);
        return result == Result.Success
            ? surface
            : throw new InvalidOperationException($"Could not create a headless surface: {result}");
    }
}
