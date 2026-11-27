using System.Numerics;
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

/// <summary>A bar over each enemy that has been hit, green when healthy and red when nearly dead.</summary>
public sealed record HealthBars(List<(Vector3 Head, float Left)> Bars) : UiView
{
    private const float Width = 36f;
    private static readonly Color Healthy = Color.Hex("#6CCB5F");
    private static readonly Color Dying = Color.Hex("#E2513C");

    public override UiView Body
    {
        get
        {
            var bars = new ZStack();
            foreach (var (head, left) in Bars)
            {
                bars.Add(new ZStack(Alignment.Leading)
                {
                    new Capsule().Fill(Color.Hex("#10141C", 0.8f)).Frame(width: Width + 4f, height: 8f),
                    new Capsule().Fill(Dying.Mix(Healthy, left)).Frame(width: Width * left, height: 4f).Padding(2f),
                }
                .Id("health")
                .AtWorld(head));
            }

            return bars.IgnoresPointer();
        }
    }
}
