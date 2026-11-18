using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A sampled image with a full mip chain and one or more layers. Its bytes arrive through
/// a staging buffer, either at once with <see cref="Upload"/> or on a later command buffer
/// through <see cref="TextureUploads.CreateStaging"/> and <see cref="TextureUploads.RecordUpload"/>.
/// </summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private readonly GpuAllocation _allocation;

    public readonly VulkanContext Context;
    public readonly Image Image;
    public readonly ImageView View;
    public readonly int Width;
    public readonly int Height;
    public readonly uint MipCount;
    public readonly uint Layers;
    public readonly TextureFormat Format;

    /// <summary>A single 2D texture, filled right away. Waits for the GPU.</summary>
    public GpuTexture(VulkanContext context, TextureData data)
        : this(context, new TextureShape(data.Width, data.Height, data.Kind, (uint)data.Mips.Length, Format: data.Format))
    {
        Upload([data]);
    }

    /// <summary>A 2D array of same-sized layers, filled right away. Waits for the GPU.</summary>
    public GpuTexture(VulkanContext context, ReadOnlySpan<TextureData> layers)
        : this(context, new TextureShape(
            layers[0].Width, layers[0].Height, layers[0].Kind, (uint)layers[0].Mips.Length, (uint)layers.Length, layers[0].Format) { Array = true })
    {
        Upload(layers);
    }

    /// <summary>A cube of six same-sized square faces in Vulkan's order, filled right away. Waits for the GPU.</summary>
    public static GpuTexture Cube(VulkanContext context, ReadOnlySpan<TextureData> faces)
    {
        if (faces.Length != 6)
        {
            throw new ArgumentException($"A cube has 6 faces, but {faces.Length} were given.", nameof(faces));
        }

        var texture = new GpuTexture(context, new TextureShape(
            faces[0].Width, faces[0].Height, faces[0].Kind, (uint)faces[0].Mips.Length, 6, faces[0].Format) { Cube = true });
        texture.Upload(faces);
        return texture;
    }

    /// <summary>An empty texture. Fill it with <see cref="TextureUploads.RecordUpload"/>.</summary>
    public GpuTexture(VulkanContext context, in TextureShape shape)
    {
        Context = context;
        Width = shape.Width;
        Height = shape.Height;
        MipCount = shape.MipCount;
        Layers = shape.Layers;
        Format = shape.Format;
        var api = context.Api;

        var imageInfo = shape.CreateInfo;
        api.CreateImage(context.Device, &imageInfo, null, out Image).Check();

        api.GetImageMemoryRequirements(context.Device, Image, out var requirements);
        _allocation = context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(context.Device, Image, _allocation.Memory, _allocation.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = shape.ViewType,
            Format = shape.VulkanFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, shape.MipCount, 0, shape.Layers),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();
    }

    public void Dispose()
    {
        Context.Api.DestroyImageView(Context.Device, View, null);
        Context.Api.DestroyImage(Context.Device, Image, null);
        Context.Memory.Free(_allocation);
    }

    /// <summary>
    /// How many rows a band of a single-mip texture must hold, so each band but the last starts and ends on a block:
    /// 4 for a block-compressed format, 1 otherwise.
    /// </summary>
    public int RowUnit => Format is TextureFormat.Rgba8 or TextureFormat.Rgba16f ? 1 : 4;

    /// <summary>The bytes of the first mip's rows from the top down to <paramref name="rows"/>, a whole number of <see cref="RowUnit"/>s or the full height.</summary>
    public int RowsSize(int rows) => TextureData.LevelSize(Format, Width, rows);

    /// <summary>The bytes of one layer's mip <paramref name="level"/>.</summary>
    public int MipSize(int level) => TextureData.LevelSize(Format, Math.Max(Width >> level, 1), Math.Max(Height >> level, 1));

    private void Upload(ReadOnlySpan<TextureData> layers)
    {
        using var staging = this.CreateStaging(layers);
        Context.SubmitAndWait(cmd => this.RecordUpload(cmd, staging));
    }
}
