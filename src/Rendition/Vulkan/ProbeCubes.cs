using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The reflection probes' cube map array, RGBA16F with one mip per roughness step. It starts with room
/// for <see cref="FirstCapacity"/> probes and doubles when asked to, up to
/// <see cref="ReflectionProbeOrder.MaxProbes"/>, keeping every cube in its place.
/// </summary>
internal sealed unsafe class ProbeCubes : IDisposable
{
    public const uint FaceSize = 128;

    /// <summary>How many probes the array holds before it first grows.</summary>
    public const int FirstCapacity = 8;

    /// <summary>Same roughness steps as the studio. See REFLECTION_LODS in Shaders/brdf.glsl.</summary>
    public const int Mips = 6;

    private const ImageUsageFlags Usage =
        ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

    private readonly VulkanContext _context;
    private readonly ImageView[] _levels = new ImageView[ReflectionProbeOrder.MaxProbes * Mips];
    private Image _image;
    private GpuAllocation _memory;

    /// <summary>Every cube, for sampling. A new view each time the array grows.</summary>
    public ImageView View { get; private set; }

    /// <summary>How many cubes the array holds.</summary>
    public int Capacity { get; private set; }

    /// <summary>Makes the array, every cube ready to sample from the start, captured or not.</summary>
    public ProbeCubes(VulkanContext context)
    {
        _context = context;
        Capacity = FirstCapacity;
        (_image, _memory) = CreateImage(Capacity);
        CreateViews();

        var api = context.Api;
        var image = _image;
        uint layers = (uint)Capacity * 6;
        context.SubmitAndWait(cmd => api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal) { MipCount = Mips, LayerCount = layers },
            (PipelineStageFlags2.None, AccessFlags2.None),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit)));
    }

    /// <summary>One cube's mip as six layers, for the blur to write.</summary>
    public ImageView Level(int cube, int mip) => _levels[cube * Mips + mip];

    public Image Image => _image;

    /// <summary>
    /// Doubles the array, copying every cube across, so each probe keeps its place. The frames in flight
    /// still read the old array, so this waits for the GPU first, and the caller points them at
    /// <see cref="View"/> after.
    /// </summary>
    public void Grow()
    {
        _context.WaitIdle();

        int capacity = Math.Min(Capacity * 2, ReflectionProbeOrder.MaxProbes);
        var (image, memory) = CreateImage(capacity);
        var old = _image;
        uint oldLayers = (uint)Capacity * 6;
        _context.SubmitAndWait(cmd => RecordCopy(cmd, old, image, oldLayers, (uint)capacity * 6));

        Destroy();
        (_image, _memory) = (image, memory);
        Capacity = capacity;
        CreateViews();
    }

    // Copies every mip of the old cubes into the front of the new array, and leaves the whole new array ready to sample.
    private void RecordCopy(CommandBuffer cmd, Image old, Image image, uint oldLayers, uint layers)
    {
        var api = _context.Api;
        api.Transition(cmd, new ImageTransition(old, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal) { MipCount = Mips, LayerCount = oldLayers },
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal) { MipCount = Mips, LayerCount = layers },
            (PipelineStageFlags2.None, AccessFlags2.None),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));

        var regions = stackalloc ImageCopy[Mips];
        for (int mip = 0; mip < Mips; mip++)
        {
            uint size = FaceSize >> mip;
            regions[mip] = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, 0, oldLayers),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, 0, oldLayers),
                Extent = new Extent3D(size, size, 1),
            };
        }

        api.CmdCopyImage(cmd, old, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal, Mips, regions);
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal) { MipCount = Mips, LayerCount = layers },
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    private void CreateViews()
    {
        var api = _context.Api;
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.TypeCubeArray,
            Format = GBuffer.HdrFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Mips, 0, (uint)Capacity * 6),
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();
        View = view;

        viewInfo.ViewType = ImageViewType.Type2DArray;
        for (int cube = 0; cube < Capacity; cube++)
        {
            for (int mip = 0; mip < Mips; mip++)
            {
                viewInfo.SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, (uint)mip, 1, (uint)cube * 6, 6);
                api.CreateImageView(_context.Device, &viewInfo, null, out _levels[cube * Mips + mip]).Check();
            }
        }
    }

    private (Image Image, GpuAllocation Memory) CreateImage(int capacity)
    {
        var api = _context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = GBuffer.HdrFormat,
            Extent = new Extent3D(FaceSize, FaceSize, 1),
            MipLevels = Mips,
            ArrayLayers = (uint)capacity * 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = Usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(_context.Device, &imageInfo, null, out var image).Check();

        api.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var memory = _context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(_context.Device, image, memory.Memory, memory.Offset).Check();
        return (image, memory);
    }

    private void Destroy()
    {
        var api = _context.Api;
        for (int i = 0; i < Capacity * Mips; i++)
        {
            api.DestroyImageView(_context.Device, _levels[i], null);
        }

        api.DestroyImageView(_context.Device, View, null);
        api.DestroyImage(_context.Device, _image, null);
        _context.Memory.Free(_memory);
    }

    public void Dispose() => Destroy();
}
