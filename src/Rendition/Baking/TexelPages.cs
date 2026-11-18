using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The <see cref="Baker"/>'s pages: each one's position, geometric normal, shading normal and validity, made as a
/// layout first needs them, and which bake holds them. They hold one bake's texels at a time.
/// </summary>
internal sealed unsafe class TexelPages(VulkanContext context) : IDisposable
{
    private readonly List<PageTargets> _pages = [];
    private (object Owner, string Name)? _claimedBy;

    /// <summary>The pages made so far.</summary>
    public int Count => _pages.Count;

    /// <summary>Whether a bake holds the pages, so another can't set up yet.</summary>
    public bool Claimed => _claimedBy is not null;

    public PageTargets this[int page] => _pages[page];

    /// <summary>Makes pages until there are <paramref name="count"/>.</summary>
    public void Ensure(int count)
    {
        while (_pages.Count < count)
        {
            _pages.Add(new PageTargets(context));
        }
    }

    /// <summary>
    /// Takes the pages for the bake of <paramref name="name"/> until <see cref="Release"/>. The pages hold one bake's
    /// texels at a time, so a second bake can't set up while the first still reads them.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another bake holds the pages.</exception>
    public void Claim(object owner, string name)
    {
        if (_claimedBy is { } held && !ReferenceEquals(held.Owner, owner))
        {
            throw new InvalidOperationException(
                $"The baker's pages hold the bake of \"{held.Name}\", so the bake of \"{name}\" can't set up its texels until that one is done.");
        }

        _claimedBy = (owner, name);
    }

    /// <summary>Gives the pages back, if <paramref name="owner"/> holds them.</summary>
    public void Release(object owner)
    {
        if (_claimedBy is { } held && ReferenceEquals(held.Owner, owner))
        {
            _claimedBy = null;
        }
    }

    /// <summary>
    /// The texels of a region of a page, <see cref="BakeGpu.TexelFloats"/> floats each, row by row. Waits for the GPU;
    /// for tests.
    /// </summary>
    public float[] ReadTexels(int page, int x, int y, int width, int height)
    {
        CheckRegion(page, x, y, width, height);
        var targets = _pages[page];
        var readBack = new TexelReadBack((ulong)width * (ulong)height);
        var region = (x, y, width, height);
        using var buffer = GpuBuffer.CreateHostVisible(context, readBack.Bytes, BufferUsageFlags.TransferDstBit);

        context.SubmitAndWait(cmd =>
        {
            var api = context.Api;
            api.MemoryBarrier(cmd, (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit),
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));
            BakeGpu.Copy(api, cmd, targets.Position, buffer, BakeGpu.Region(readBack.PositionAt, region));
            BakeGpu.Copy(api, cmd, targets.GeometricNormal, buffer, BakeGpu.Region(readBack.GeometricAt, region));
            BakeGpu.Copy(api, cmd, targets.ShadingNormal, buffer, BakeGpu.Region(readBack.ShadingAt, region));
            BakeGpu.Copy(api, cmd, targets.Validity, buffer, BakeGpu.Region(readBack.ValidityAt, region));
        });

        return readBack.Unpack((byte*)buffer.Mapped);
    }

    private void CheckRegion(int page, int x, int y, int width, int height)
    {
        if (page < 0 || page >= _pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, $"There are {_pages.Count} pages, so page {page} doesn't exist.");
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > LightmapLayout.PageSize || y + height > LightmapLayout.PageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"The region at ({x}, {y}) of {width} by {height} texels isn't inside the page.");
        }
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Dispose();
        }
    }

    // Where a texel readback's four images sit in its buffer, for count texels, and how they turn into texels.
    private readonly struct TexelReadBack
    {
        public TexelReadBack(ulong count)
        {
            Count = count;
            GeometricAt = BakeGpu.Align(PositionAt + count * 16);
            ShadingAt = BakeGpu.Align(GeometricAt + count * 8);
            ValidityAt = BakeGpu.Align(ShadingAt + count * 8);
        }

        public ulong Count { get; }

        public ulong PositionAt => 0;

        public ulong GeometricAt { get; }

        public ulong ShadingAt { get; }

        public ulong ValidityAt { get; }

        public ulong Bytes => ValidityAt + Count;

        // Each texel's TexelFloats floats, row by row, from the mapped buffer.
        public float[] Unpack(byte* bytes)
        {
            const int floats = BakeGpu.TexelFloats;
            var texels = new float[Count * floats];
            var positions = (float*)(bytes + PositionAt);
            var geometric = (Half*)(bytes + GeometricAt);
            var shading = (Half*)(bytes + ShadingAt);
            var validity = bytes + ValidityAt;
            for (ulong i = 0; i < Count; i++)
            {
                ulong at = i * floats;
                for (ulong c = 0; c < 4; c++)
                {
                    texels[at + c] = positions[i * 4 + c];
                }

                for (ulong c = 0; c < 3; c++)
                {
                    texels[at + 4 + c] = (float)geometric[i * 4 + c];
                    texels[at + 7 + c] = (float)shading[i * 4 + c];
                }

                texels[at + 10] = validity[i] / 255f;
            }

            return texels;
        }
    }
}

/// <summary>A page's position, geometric normal, shading normal and validity.</summary>
internal sealed class PageTargets : IDisposable
{
    public PageTargets(VulkanContext context)
    {
        var extent = new Extent2D(LightmapLayout.PageSize, LightmapLayout.PageSize);
        const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit
            | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        Position = new RenderTarget(context, BakeGpu.PositionFormat, extent, usage, ImageAspectFlags.ColorBit);
        GeometricNormal = new RenderTarget(context, BakeGpu.NormalFormat, extent, usage, ImageAspectFlags.ColorBit);
        ShadingNormal = new RenderTarget(context, BakeGpu.NormalFormat, extent, usage, ImageAspectFlags.ColorBit);
        Validity = new RenderTarget(context, BakeGpu.ValidityFormat, extent,
            ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        Colors = [Position, GeometricNormal, ShadingNormal];
    }

    /// <summary>World position, w the number (from 1) of the rectangle covering the texel and 0 elsewhere.</summary>
    public RenderTarget Position { get; }

    /// <summary>The normal of the triangle itself, unit length.</summary>
    public RenderTarget GeometricNormal { get; }

    /// <summary>The vertex normals blended, unit length.</summary>
    public RenderTarget ShadingNormal { get; }

    /// <summary>1 for a covered texel outside walls, 0 otherwise.</summary>
    public RenderTarget Validity { get; }

    /// <summary>The three drawn into, in the fragment shader's order.</summary>
    public RenderTarget[] Colors { get; }

    public void Dispose()
    {
        Position.Dispose();
        GeometricNormal.Dispose();
        ShadingNormal.Dispose();
        Validity.Dispose();
    }
}
