using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Rendition.Vulkan;

/// <summary>
/// The validation layer's messages while it is on: each warning and error is written to the console and counted.
/// </summary>
internal sealed unsafe class VulkanValidation : IDisposable
{
    public const string Layer = "VK_LAYER_KHRONOS_validation";

    private readonly Instance _instance;
    private readonly ExtDebugUtils _debugUtils;
    private readonly DebugUtilsMessengerEXT _messenger;

    // Kept so the delegate the layer calls lives as long as the messenger.
    private readonly DebugUtilsMessengerCallbackFunctionEXT _callback;
    private int _errors;
    private int _fsrLumaHistoryNoted;

    public VulkanValidation(Vk api, Instance instance)
    {
        _instance = instance;
        _callback = OnDebugMessage;
        api.TryGetInstanceExtension(instance, out _debugUtils);
        var info = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_callback),
        };
        _debugUtils!.CreateDebugUtilsMessenger(instance, &info, null, out _messenger).Check();
    }

    /// <summary>How many messages the layer has raised, leaving out the one FSR message that isn't ours.</summary>
    public int Errors => _errors;

    /// <summary>Whether the validation layer is installed.</summary>
    public static bool IsLayerAvailable(Vk api)
    {
        uint count = 0;
        api.EnumerateInstanceLayerProperties(&count, null).Check();
        var layers = new LayerProperties[count];
        fixed (LayerProperties* ptr = layers)
        {
            api.EnumerateInstanceLayerProperties(&count, ptr).Check();
            for (int i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)ptr[i].LayerName) == Layer)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose() => _debugUtils.DestroyDebugUtilsMessenger(_instance, _messenger, null);

    private uint OnDebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string message = Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "";
        if (IsFsrLumaHistoryFormat(message))
        {
            if (Interlocked.Exchange(ref _fsrLumaHistoryNoted, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[vulkan] note: FSR's own shader declares its luma history Rgba8 while FSR makes that image RGBA16F. "
                    + "It's inside amd_fidelityfx_vk.dll, so it isn't counted.");
            }

            return Vk.False;
        }

        Interlocked.Increment(ref _errors);
        Console.Error.WriteLine($"[vulkan] {severity}: {message}");
        return Vk.False;
    }

    /// <summary>
    /// The one message the prebuilt FidelityFX 1.1.4 shaders raise on every FSR frame: their storage image for
    /// luma history says Rgba8, but the SDK creates that image, which nothing of ours touches, as RGBA16F.
    /// That is a real bug in AMD's shader, undefined behaviour inside FSR, which we can't fix without rebuilding
    /// it; see "Upscaling" in docs/design.md for why we accept it. Matched on the variable and both formats, so any
    /// other format message still counts.
    /// </summary>
    internal static bool IsFsrLumaHistoryFormat(string message) =>
        message.Contains("variable \"rw_luma_history\"", StringComparison.Ordinal)
        && message.Contains("Format operand Rgba8", StringComparison.Ordinal)
        && message.Contains("VK_FORMAT_R16G16B16A16_SFLOAT", StringComparison.Ordinal);
}
