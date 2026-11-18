using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The volumetric fog's volumes: the two the inject pass lights in turn, each reading the other as history, and the
/// one the integrate pass sums them into. Until they're made, a single texel that lets everything through stands in
/// for the sum.
/// </summary>
internal sealed class FogGrid : IDisposable
{
    private readonly VulkanContext _context;
    private readonly FogCells _empty;
    private FogCells[]? _injected;
    private FogCells? _integrated;

    public FogGrid(VulkanContext context)
    {
        _context = context;

        // Nothing in front, and everything behind gets through.
        _empty = new FogCells(context, (1, 1, 1), new ClearColorValue(0f, 0f, 0f, 1f));
    }

    /// <summary>The cells across, down and deep of the volumes made last; meaningless without <see cref="HasTargets"/>.</summary>
    public (int X, int Y, int Z) Size { get; private set; }

    /// <summary>Whether the volumes have been made.</summary>
    public bool HasTargets => _integrated is not null;

    /// <summary>The summed volume, or until it's made a texel that lets everything through.</summary>
    public ImageView Result => (_integrated ?? _empty).View;

    /// <summary>The two volumes the inject pass lights in turn; null until they're made.</summary>
    public FogCells[]? Injected => _injected;

    /// <summary>Makes the volumes at a size, replacing any there were.</summary>
    public void Make((int X, int Y, int Z) size)
    {
        Release();
        Size = size;
        var black = new ClearColorValue(0f, 0f, 0f, 0f);
        _injected = [new FogCells(_context, size, black), new FogCells(_context, size, black)];
        _integrated = new FogCells(_context, size, new ClearColorValue(0f, 0f, 0f, 1f));
    }

    /// <summary>Frees the volumes, leaving the stand-in texel.</summary>
    public void Release()
    {
        if (_injected is not null)
        {
            _injected[0].Dispose();
            _injected[1].Dispose();
            _injected = null;
        }

        if (_integrated is not null)
        {
            _integrated.Dispose();
            _integrated = null;
        }
    }

    public void Dispose()
    {
        Release();
        _empty.Dispose();
    }
}
