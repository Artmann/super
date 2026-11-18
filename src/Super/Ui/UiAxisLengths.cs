namespace Super.Ui;

/// <summary>Sizes seen along an axis: the main length runs along it, and the cross length across it.</summary>
internal static class UiAxisLengths
{
    public static float Main(this Axis axis, UiSize size) => axis == Axis.Vertical ? size.Height : size.Width;

    public static float Cross(this Axis axis, UiSize size) => axis == Axis.Vertical ? size.Width : size.Height;

    public static UiSize SizeOf(this Axis axis, float main, float cross) =>
        axis == Axis.Vertical ? new UiSize(cross, main) : new UiSize(main, cross);
}
