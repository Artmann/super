using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>The clouds' targets at one picture size: the shadow map, the march, the picture and the two histories.</summary>
internal sealed class CloudTargets : IDisposable
{
    public CloudTargets(VulkanContext context, (int Width, int Height) size)
    {
        Size = size;
        MarchSize = ((size.Width + 1) / 2, (size.Height + 1) / 2);
        var none = new ClearColorValue(0f, 0f, 0f, 1f);
        Shadow = new CellVolume(context, (CloudModel.ShadowResolution, CloudModel.ShadowResolution, 1), new ClearColorValue(1f, 1f, 1f, 1f));
        March = new CellVolume(context, (MarchSize.Width, MarchSize.Height, 1), none);
        Picture = new CellVolume(context, (size.Width, size.Height, 1), none);
        Histories = [new CellVolume(context, (size.Width, size.Height, 1), none), new CellVolume(context, (size.Width, size.Height, 1), none)];
    }

    public (int Width, int Height) Size { get; }

    public (int Width, int Height) MarchSize { get; }

    public CellVolume Shadow { get; }

    public CellVolume March { get; }

    public CellVolume Picture { get; }

    public CellVolume[] Histories { get; }

    /// <summary>The picture's texels across and down for a scene this size: half of it, rounded up.</summary>
    public static (int Width, int Height) PictureSize(Extent2D scene) =>
        ((int)Math.Max((scene.Width + 1) / 2, 1), (int)Math.Max((scene.Height + 1) / 2, 1));

    public void Dispose()
    {
        Shadow.Dispose();
        March.Dispose();
        Picture.Dispose();
        Histories[0].Dispose();
        Histories[1].Dispose();
    }
}
