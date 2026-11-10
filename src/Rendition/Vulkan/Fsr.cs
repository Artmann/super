using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// AMD FidelityFX Super Resolution 3.1, through the FidelityFX API in its prebuilt Vulkan library,
/// amd_fidelityfx_vk.dll, which ships in third_party/fidelityfx. One context upscales from one render size to one
/// display size; a new size needs a new context. The structs here mirror ffx_api.h, ffx_api_types.h,
/// ffx_api_vk.h and ffx_upscale.h from FidelityFX SDK 1.1.4.
/// </summary>
internal sealed unsafe class Fsr : IDisposable
{
    /// <summary>The library's name, without its extension.</summary>
    public const string LibraryName = "amd_fidelityfx_vk";

    private const ulong BackendVulkanType = 0x3;
    private const ulong UpscaleContextType = 0x00010000;
    private const ulong UpscaleDispatchType = 0x00010001;
    private const ulong ProviderVersionType = 6;
    private const uint Ok = 0;

    private const uint HighDynamicRange = 1 << 0;
    private const uint DepthInverted = 1 << 3;
    private const uint DepthInfinite = 1 << 4;
    private const uint DebugChecking = 1 << 7;

    private const uint Texture2D = 2;
    private const uint FormatR16G16B16A16Float = 4;
    private const uint FormatR16G16Float = 18;
    private const uint FormatR32Float = 28;
    private const uint UsageReadOnly = 0;
    private const uint UsageUnorderedAccess = 1 << 1;
    private const uint UsageDepthTarget = 1 << 2;
    private const uint StateUnorderedAccess = 1 << 1;
    private const uint StatePixelComputeRead = (1 << 2) | (1 << 3);

    private static readonly Lazy<bool> Loaded = new(() =>
        NativeLibrary.TryLoad(LibraryName, typeof(Fsr).Assembly, DllImportSearchPath.AssemblyDirectory, out _));

    private nint _context;

    /// <summary>Whether the library is next to the game and loads. Only the first call tries.</summary>
    public static bool IsAvailable => Loaded.Value;

    /// <summary>The size the scene is drawn at.</summary>
    public Extent2D RenderSize { get; }

    /// <summary>The size FSR rebuilds the picture at.</summary>
    public Extent2D DisplaySize { get; }

    /// <summary>The name and version of FSR the library picked, such as "FSR 3.1.4".</summary>
    public string Version { get; }

    /// <summary>
    /// Makes a context that upscales from <paramref name="renderSize"/> to <paramref name="displaySize"/> on the
    /// context's device. With <paramref name="debugChecking"/> FSR checks what it's given each frame and writes
    /// what it finds to the error stream. Throws when FSR can't make it.
    /// </summary>
    public Fsr(VulkanContext context, Extent2D renderSize, Extent2D displaySize, bool debugChecking)
    {
        RenderSize = renderSize;
        DisplaySize = displaySize;

        var getDeviceProcAddr = context.Api.GetInstanceProcAddr(context.Instance, "vkGetDeviceProcAddr");
        var backend = new BackendVulkanDescription
        {
            Header = new ApiHeader { Type = BackendVulkanType },
            Device = context.Device.Handle,
            PhysicalDevice = context.PhysicalDevice.Handle,
            GetDeviceProcAddr = (nint)getDeviceProcAddr.Handle,
        };
        var description = new UpscaleContextDescription
        {
            Header = new ApiHeader { Type = UpscaleContextType, Next = &backend.Header },
            // The scene is lit in linear HDR, depth runs from 1 near to 0 at an infinite far plane (reversed-Z),
            // and motion vectors leave the jitter out.
            Flags = HighDynamicRange | DepthInverted | DepthInfinite | (debugChecking ? DebugChecking : 0),
            MaxRenderSize = new Dimensions(renderSize.Width, renderSize.Height),
            MaxUpscaleSize = new Dimensions(displaySize.Width, displaySize.Height),
            Message = (nint)(delegate* unmanaged[Cdecl]<uint, char*, void>)&OnMessage,
        };

        nint created = 0;
        uint result = ffxCreateContext(&created, &description.Header, null);
        if (result != Ok)
        {
            throw new InvalidOperationException($"FSR couldn't make its context (FidelityFX error {result}).");
        }

        _context = created;
        Version = QueryVersion();
    }

    /// <summary>
    /// Records FSR's passes into <paramref name="cmd"/>. Every input must be ready to sample from compute
    /// shaders, depth too, and <see cref="FsrInputs.Output"/> must be in the general layout.
    /// </summary>
    public void Dispatch(CommandBuffer cmd, in FsrInputs inputs)
    {
        var render = new Dimensions(RenderSize.Width, RenderSize.Height);
        var display = new Dimensions(DisplaySize.Width, DisplaySize.Height);
        var dispatch = new UpscaleDispatchDescription
        {
            Header = new ApiHeader { Type = UpscaleDispatchType },
            CommandList = cmd.Handle,
            Color = Texture(inputs.Color, FormatR16G16B16A16Float, render, UsageReadOnly, StatePixelComputeRead),
            Depth = Texture(inputs.Depth, FormatR32Float, render, UsageDepthTarget, StatePixelComputeRead),
            MotionVectors = Texture(inputs.Motion, FormatR16G16Float, render, UsageReadOnly, StatePixelComputeRead),
            Exposure = Texture(inputs.Exposure, FormatR32Float, new Dimensions(1, 1), UsageReadOnly, StatePixelComputeRead),
            Output = Texture(inputs.Output, FormatR16G16B16A16Float, display, UsageUnorderedAccess, StateUnorderedAccess),
            JitterOffset = new FloatCoords(inputs.Jitter.X, inputs.Jitter.Y),
            // Motion is already in render pixels, from now back to then, which is what FSR reads.
            MotionVectorScale = new FloatCoords(1f, 1f),
            RenderSize = render,
            UpscaleSize = display,
            FrameTimeDelta = inputs.FrameMilliseconds,
            PreExposure = 1f,
            Reset = inputs.Reset ? (byte)1 : (byte)0,
            // With inverted, infinite depth FSR wants the far plane in CameraNear, as the largest float, and the
            // camera's near plane in CameraFar: the names follow depth 0 and depth 1, not the camera.
            CameraNear = float.MaxValue,
            CameraFar = inputs.CameraNear,
            CameraFovAngleVertical = inputs.VerticalFov,
            ViewSpaceToMetersFactor = 1f,
        };

        nint context = _context;
        uint result = ffxDispatch(&context, &dispatch.Header);
        if (result != Ok)
        {
            throw new InvalidOperationException($"FSR couldn't record its passes (FidelityFX error {result}).");
        }
    }

    public void Dispose()
    {
        if (_context == 0)
        {
            return;
        }

        nint context = _context;
        ffxDestroyContext(&context, null);
        _context = 0;
    }

    private string QueryVersion()
    {
        var query = new ProviderVersionQuery { Header = new ApiHeader { Type = ProviderVersionType } };
        nint context = _context;
        return ffxQuery(&context, &query.Header) == Ok && query.VersionName != null
            ? Marshal.PtrToStringUTF8((nint)query.VersionName) ?? "FSR"
            : "FSR";
    }

    private static Resource Texture(Image image, uint format, Dimensions size, uint usage, uint state) => new()
    {
        Handle = image.Handle,
        Description = new ResourceDescription
        {
            Type = Texture2D,
            Format = format,
            Width = size.Width,
            Height = size.Height,
            Depth = 1,
            MipCount = 1,
            Usage = usage,
        },
        State = state,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMessage(uint type, char* message)
    {
        string kind = type == 0 ? "error" : "warning";
        Console.Error.WriteLine($"[fsr] {kind}: {new string(message)}");
    }

    [DllImport(LibraryName)]
    private static extern uint ffxCreateContext(nint* context, ApiHeader* description, void* allocationCallbacks);

    [DllImport(LibraryName)]
    private static extern uint ffxDestroyContext(nint* context, void* allocationCallbacks);

    [DllImport(LibraryName)]
    private static extern uint ffxQuery(nint* context, ApiHeader* description);

    [DllImport(LibraryName)]
    private static extern uint ffxDispatch(nint* context, ApiHeader* description);

    [StructLayout(LayoutKind.Sequential)]
    private struct ApiHeader
    {
        public ulong Type;
        public ApiHeader* Next;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BackendVulkanDescription
    {
        public ApiHeader Header;
        public nint Device;
        public nint PhysicalDevice;
        public nint GetDeviceProcAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Dimensions(uint Width, uint Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FloatCoords(float X, float Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleContextDescription
    {
        public ApiHeader Header;
        public uint Flags;
        public Dimensions MaxRenderSize;
        public Dimensions MaxUpscaleSize;
        public nint Message;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceDescription
    {
        public uint Type;
        public uint Format;
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint MipCount;
        public uint Flags;
        public uint Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Resource
    {
        public ulong Handle;
        public ResourceDescription Description;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleDispatchDescription
    {
        public ApiHeader Header;
        public nint CommandList;
        public Resource Color;
        public Resource Depth;
        public Resource MotionVectors;
        public Resource Exposure;
        public Resource Reactive;
        public Resource TransparencyAndComposition;
        public Resource Output;
        public FloatCoords JitterOffset;
        public FloatCoords MotionVectorScale;
        public Dimensions RenderSize;
        public Dimensions UpscaleSize;
        public byte EnableSharpening;
        public float Sharpness;
        public float FrameTimeDelta;
        public float PreExposure;
        public byte Reset;
        public float CameraNear;
        public float CameraFar;
        public float CameraFovAngleVertical;
        public float ViewSpaceToMetersFactor;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderVersionQuery
    {
        public ApiHeader Header;
        public ulong VersionId;
        public byte* VersionName;
    }
}

/// <summary>What one FSR dispatch reads and writes. Every image but the output is at the render size.</summary>
internal readonly record struct FsrInputs(
    Image Color,
    Image Depth,
    Image Motion,
    Image Exposure,
    Image Output,
    System.Numerics.Vector2 Jitter,
    float FrameMilliseconds,
    bool Reset,
    float CameraNear,
    float VerticalFov);
