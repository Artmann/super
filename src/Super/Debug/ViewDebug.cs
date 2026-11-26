using System.Runtime.InteropServices;
using Rendition;

namespace Super;

/// <summary>
/// The debug shapes of one frame, drawn from Draw with <see cref="View.Debug"/> and gone the next frame. What
/// <see cref="DebugDraw"/> keeps is copied in too, so a test sees every debug shape on screen in <see cref="Lines"/>
/// and <see cref="Labels"/>. Which channels show is <see cref="DebugDraw"/>'s to say; a view of its own shows only the
/// shapes with no channel.
/// </summary>
public sealed class ViewDebug : DebugCanvas
{
    private readonly List<DebugLine> _lines = [];
    private readonly List<DebugLabel> _labels = [];
    private readonly List<LineDraw> _draws = [];

    internal ViewDebug()
    {
    }

    /// <summary>Every debug line on screen this frame, the kept ones after the frame's own.</summary>
    public ReadOnlySpan<DebugLine> Lines => CollectionsMarshal.AsSpan(_lines);

    /// <summary>Every debug label on screen this frame, the kept ones after the frame's own.</summary>
    public ReadOnlySpan<DebugLabel> Labels => CollectionsMarshal.AsSpan(_labels);

    /// <summary>The lines as the renderer draws them.</summary>
    internal ReadOnlySpan<LineDraw> Draws => CollectionsMarshal.AsSpan(_draws);

    /// <summary>Which channels show, or null for only the shapes with no channel.</summary>
    internal DebugDraw? Channels { get; set; }

    /// <summary>How many lines on screen this frame are in a channel, case aside, or have none when it's null.</summary>
    public int Count(string? channel)
    {
        int count = 0;
        foreach (var line in Lines)
        {
            if (string.Equals(line.Look.Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    internal void Clear()
    {
        _lines.Clear();
        _labels.Clear();
        _draws.Clear();
    }

    private protected override bool Showing(string? channel) => channel is null || Channels?.Shows(channel) == true;

    private protected override void Add(in DebugLine line)
    {
        _lines.Add(line);
        var color = line.Color;
        _draws.Add(new LineDraw
        {
            From = line.From,
            Width = line.Look.Width,
            To = line.To,
            Hidden = line.Look.OnTop ? 1f : DebugLook.HiddenShare,
            Color = new(color.R, color.G, color.B, color.A),
        });
    }

    private protected override void Add(in DebugLabel label) => _labels.Add(label);

    /// <summary>Adds a kept line, checking its channel again, since it may have been hidden since it was drawn.</summary>
    internal void AddKept(in DebugLine line)
    {
        if (Showing(line.Look.Channel))
        {
            Add(line);
        }
    }

    /// <summary>Adds a kept label, checking its channel again.</summary>
    internal void AddKept(in DebugLabel label)
    {
        if (Showing(label.Channel))
        {
            Add(label);
        }
    }
}
