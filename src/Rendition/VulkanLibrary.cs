using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Where the renderer loads Vulkan from. Windows and Linux have a Vulkan loader of their own, which the GPU's driver
/// installs. A Mac has none. There the renderer uses a loader installed with Homebrew or the LunarG SDK when it finds
/// one, since only a loader brings the validation layer, and otherwise the MoltenVK library that ships beside the game.
/// MoltenVK runs Vulkan on top of Metal.
/// </summary>
public static unsafe class VulkanLibrary
{
    private const string Loader = "libvulkan.1.dylib";
    private const string MoltenVk = "libMoltenVK.dylib";

    private static readonly Lazy<string?> s_path = new(Find);
    private static readonly Lazy<nint> s_getInstanceProcAddr = new(LoadGetInstanceProcAddr);

    /// <summary>
    /// The file Vulkan is loaded from on a Mac: a Vulkan loader, or MoltenVK on its own. Null elsewhere, where the
    /// system's loader is found by name.
    /// </summary>
    public static string? Path => s_path.Value;

    /// <summary>
    /// Why a renderer asked to validate can't, on a Mac running MoltenVK without a loader. Null when there is a
    /// loader, which may still lack the layer.
    /// </summary>
    internal static string? NoLayersReason => Path is { } path && System.IO.Path.GetFileName(path) == MoltenVk
        ? "MoltenVK is running without a Vulkan loader, which the validation layer needs. Install them with "
            + "`brew install vulkan-loader vulkan-validationlayers`."
        : null;

    /// <summary>Makes the API the renderer calls Vulkan through, from <see cref="Path"/> where there is one.</summary>
    internal static Vk CreateApi() => Path is { } path ? new Vk(new DefaultNativeContext(path)) : Vk.GetApi();

    /// <summary>
    /// Points GLFW at the library the renderer uses, so a window's surface is made by the same Vulkan. Call it before
    /// glfwInit with GLFW's glfwInitVulkanLoader, looked up in the GLFW library itself. GLFW looks for a loader on its
    /// own otherwise, and on a Mac it finds none outside the system's folders. Does nothing off a Mac.
    /// </summary>
    public static void ShareWithGlfw(nint glfwInitVulkanLoader)
    {
        if (Path is null || glfwInitVulkanLoader == 0)
        {
            return;
        }

        ((delegate* unmanaged<nint, void>)glfwInitVulkanLoader)(s_getInstanceProcAddr.Value);
    }

    private static nint LoadGetInstanceProcAddr()
    {
        nint library = NativeLibrary.Load(Path!);
        return NativeLibrary.GetExport(library, "vkGetInstanceProcAddr");
    }

    private static string? Find()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        foreach (string folder in LoaderFolders())
        {
            string loader = System.IO.Path.Combine(folder, Loader);
            if (File.Exists(loader))
            {
                AddLayers(folder);
                return loader;
            }
        }

        // Rendition's build copies it beside the game, and the Super package does the same through runtimes/osx/native.
        string[] bundled =
        [
            System.IO.Path.Combine(AppContext.BaseDirectory, MoltenVk),
            System.IO.Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", MoltenVk),
        ];
        return bundled.FirstOrDefault(File.Exists)
            ?? throw new DllNotFoundException(
                $"Found no Vulkan on this Mac: no {Loader} in {string.Join(", ", LoaderFolders())}, and no {MoltenVk} "
                + $"in {AppContext.BaseDirectory}. The game's build should copy MoltenVK there.");
    }

    /// <summary>The LunarG SDK's folder first, as its own setup asks, then Homebrew's on Apple silicon and on Intel.</summary>
    private static IEnumerable<string> LoaderFolders()
    {
        if (Environment.GetEnvironmentVariable("VULKAN_SDK") is { Length: > 0 } sdk)
        {
            yield return System.IO.Path.Combine(sdk, "lib");
        }

        yield return "/opt/homebrew/lib";
        yield return "/usr/local/lib";
    }

    /// <summary>
    /// A loader installed this way only looks for layers in the system's folders, so the validation layer installed
    /// beside it isn't found without VK_LAYER_PATH. This adds the layers for the loader. The loader reads the native
    /// environment, which <see cref="Environment.SetEnvironmentVariable(string, string)"/> doesn't change on a Mac.
    /// </summary>
    private static void AddLayers(string loaderFolder)
    {
        string layers = System.IO.Path.GetFullPath(System.IO.Path.Combine(loaderFolder, "..", "share", "vulkan", "explicit_layer.d"));
        if (!Directory.Exists(layers))
        {
            return;
        }

        string folder = LayerManifests.WithFullPaths(layers, loaderFolder, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rendition-vulkan-layers"));
        string? added = Environment.GetEnvironmentVariable("VK_ADD_LAYER_PATH");
        SetEnv("VK_ADD_LAYER_PATH", added is { Length: > 0 } ? $"{added}:{folder}" : folder, 1);
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}
