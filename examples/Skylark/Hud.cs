using Super;
using Super.Ui;

namespace SkylarkExample;

/// <summary>The level, the gems and the clock in the top-left corner, and a hint along the bottom.</summary>
public sealed record Hud(string Level, int Gems, int Total, float Seconds) : UiView
{
    private static readonly Color Panel = Color.Hex("#10141C", 0.7f);

    public override UiView Body => new VStack(HorizontalAlignment.Leading)
    {
        new VStack(HorizontalAlignment.Leading, Spacing: 4f)
        {
            new Text(Level).Bold(),
            new Text($"Gems {Gems}/{Total}"),
            new Text(Clock.Show(Seconds)),
        }
        .Padding(12f)
        .Background(Panel)
        .FixedSize()
        .Id("hud"),
        new Spacer(),
        new Text(Gems < Total ? "Find every gem to light the beacon" : "The beacon is lit. Go to it!")
            .Padding(8f)
            .Background(Panel)
            .IgnoresPointer(),
    }
    .Padding(16f)
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
}

/// <summary>A big line and a small one in the middle of the screen, for the title and the end of a run.</summary>
public sealed record Banner(string Title, string Hint) : UiView
{
    public override UiView Body => new VStack(Spacing: 8f)
    {
        new Text(Title).Bold().FontSize(40f),
        new Text(Hint),
    }
    .Padding(24f)
    .Background(Color.Hex("#10141C", 0.7f))
    .FixedSize()
    .IgnoresPointer()
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity);
}

/// <summary>Seconds as minutes and seconds, such as 1:07.</summary>
public static class Clock
{
    public static string Show(float seconds)
    {
        int whole = (int)seconds;
        return $"{whole / 60}:{whole % 60:00}";
    }
}
