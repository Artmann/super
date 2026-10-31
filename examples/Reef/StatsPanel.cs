using Super;
using Super.Ui;

namespace Reef;

/// <summary>A label on the left and a value on the right.</summary>
internal sealed record StatRow(string Label, string Value) : UiView
{
    public override UiView Body => new HStack(Spacing: 24f)
    {
        new Text(Label).Foreground(UiColor.Hex(0x9FD3E0)),
        new Spacer(),
        new Text(Value).MonospacedDigit(),
    };
}

/// <summary>How the game is running. Stats only change twice a second, and so does this.</summary>
internal sealed record StatsPanel(FrameStats Stats, int Fish) : UiView
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    public override UiView Body => new VStack(HorizontalAlignment.Leading, Spacing: 4f)
    {
        new Text("Reef").Bold().FontSize(16f),
        new StatRow("Frame rate", $"{Stats.FramesPerSecond:0} fps"),
        new StatRow("Frame time", $"{Stats.FrameMilliseconds:0.0} ms"),
        new StatRow("Allocated", $"{Stats.AllocatedBytesPerFrame:N0} B per frame"),
        new StatRow("Heap", $"{Stats.HeapBytes / BytesPerMegabyte:0.0} MB"),
        new StatRow("Collections", $"{Stats.Collections}"),
        new StatRow("Fish", $"{Fish:N0}"),
    }
    .Padding(12f)
    .Background(UiColor.Hex(0x06202C, 0.72f))
    .FixedSize();
}
