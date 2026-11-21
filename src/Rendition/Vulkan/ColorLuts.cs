using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The artists' colour tables <see cref="RendererLuts.Create"/> made, kept until destroyed or until the renderer
/// goes, and the neutral table the grade reads when the picture has none. A destroyed table's slot is used again by the
/// next one made.
/// </summary>
internal sealed class ColorLuts(VulkanContext context) : IDisposable
{
    // A destroyed table leaves null until a new one takes its slot.
    private readonly List<CellVolume?> _luts = [];
    private readonly Stack<int> _free = [];

    // Two cells a side are enough for a table that changes nothing.
    private readonly CellVolume _neutral = new(context, (2, 2, 2), ColorLutData.Neutral(2).Cells);

    /// <summary>How many tables are made and not destroyed, for tests that check they are freed.</summary>
    public int Live => _luts.Count - _free.Count;

    /// <summary>Uploads an artist's table, for <see cref="ColorGrade.Lut"/>. Waits for the GPU and stays until destroyed.</summary>
    public ColorLut Create(ColorLutData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Check();
        var volume = new CellVolume(context, (data.Size, data.Size, data.Size), data.Cells);
        if (_free.TryPop(out int index))
        {
            _luts[index] = volume;
            return new ColorLut(index);
        }

        _luts.Add(volume);
        return new ColorLut(_luts.Count - 1);
    }

    /// <summary>
    /// Lets go of <paramref name="lut"/>. Its image goes into <paramref name="retired"/>, to be freed once the frames
    /// that might still read it are done, and its slot is free at once. Throws when it isn't live.
    /// </summary>
    public void Destroy(ColorLut lut, RetiredSlot retired)
    {
        var volume = Existing(lut, nameof(lut));
        _luts[lut.Index] = null;
        _free.Push(lut.Index);
        retired.FreedOthers.Add(volume);
    }

    /// <summary>
    /// The view of <paramref name="lut"/>, or of the neutral table for none. Throws when the table wasn't made by
    /// <see cref="Create"/> or was destroyed.
    /// </summary>
    public ImageView View(ColorLut? lut) => lut is { } table ? Existing(table, nameof(lut)).View : _neutral.View;

    public void Dispose()
    {
        _neutral.Dispose();
        foreach (var lut in _luts)
        {
            lut?.Dispose();
        }
    }

    private CellVolume Existing(ColorLut lut, string parameter)
    {
        if (lut.Index < 0 || lut.Index >= _luts.Count)
        {
            throw new ArgumentException($"Colour table {lut.Index} was not made by this renderer.", parameter);
        }

        return _luts[lut.Index] ?? throw new ArgumentException($"Colour table {lut.Index} was destroyed.", parameter);
    }
}
