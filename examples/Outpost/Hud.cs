using Super;
using Super.Ui;

namespace OutpostExample;

/// <summary>Gold, lives and the wave in the top-left corner, and a hint along the bottom.</summary>
public sealed record Hud(int Gold, int Lives, int Wave, int Waves) : UiView
{
    private static readonly Color Panel = Color.Hex("#10141C", 0.7f);

    public override UiView Body => new VStack(HorizontalAlignment.Leading)
    {
        new VStack(HorizontalAlignment.Leading, Spacing: 4f)
        {
            new Text($"Gold {Gold}").Bold(),
            new Text($"Lives {Lives}"),
            new Text($"Wave {Wave}/{Waves}"),
        }
        .Padding(12f)
        .Background(Panel)
        .FixedSize()
        .Id("hud"),
        new Spacer(),
        new Text($"Click grass to build a tower ({Towers.Price} gold)")
            .Padding(8f)
            .Background(Panel)
            .IgnoresPointer(),
    }
    .Padding(16f)
    .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
}

/// <summary>A big line and a small one in the middle of the screen, for the title and the end of a game.</summary>
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
