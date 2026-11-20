using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The artists' colour tables <see cref="Renderer.CreateColorLut"/> made, kept until the renderer goes, and the neutral
/// table the grade reads when the picture has none.
/// </summary>
internal sealed class ColorLuts(VulkanContext context) : IDisposable
{
    private readonly List<CellVolume> _luts = [];

    // Two cells a side are enough for a table that changes nothing.
    private readonly CellVolume _neutral = new(context, (2, 2, 2), ColorLutData.Neutral(2).Cells);

    /// <summary>Uploads an artist's table, for <see cref="ColorGrade.Lut"/>. Waits for the GPU and stays until disposed.</summary>
    public ColorLut Create(ColorLutData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Check();
        _luts.Add(new CellVolume(context, (data.Size, data.Size, data.Size), data.Cells));
        return new ColorLut(_luts.Count - 1);
    }

    /// <summary>
    /// The view of <paramref name="lut"/>, or of the neutral table for none. Throws when the table wasn't made by
    /// <see cref="Create"/>.
    /// </summary>
    public ImageView View(ColorLut? lut)
    {
        if (lut is not { } table)
        {
            return _neutral.View;
        }

        if (table.Index < 0 || table.Index >= _luts.Count)
        {
            throw new ArgumentException($"Colour table {table.Index} was not made by this renderer.", nameof(lut));
        }

        return _luts[table.Index].View;
    }

    public void Dispose()
    {
        _neutral.Dispose();
        foreach (var lut in _luts)
        {
            lut.Dispose();
        }
    }
}
