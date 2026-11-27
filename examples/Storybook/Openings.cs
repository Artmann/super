using System.Globalization;
using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>
/// A title screen: the game's name, and "Press any button" breathing under it. Any key, click or pad button goes on
/// to the main menu, and the press that does it picks nothing there.
/// </summary>
internal sealed record TitleScreen : UiView
{
    public override UiView Body
    {
        get
        {
            var started = UseState(false);
            if (started.Value)
            {
                return new MainMenuScreen().Padding(32f).Transition(Entrance.Fade, 0.4f);
            }

            double time = UseTime();
            float breath = 0.55f + 0.45f * MathF.Cos((float)(time * Math.Tau / 2.4));
            return new VStack(Spacing: 0f)
            {
                new Spacer(),
                new Text("EMBERFALL").FontSize(84f).Weight(FontWeight.Bold).Foreground(Color.Hex("#F8FAFC")).Transition(Entrance.Drop, 0.8f),
                new Text("The last of the lamplighters").TextStyle(TextStyle.Title3).Foreground(Color.Hex("#C7D2FE")).Padding(4f, 0f, 0f, 0f),
                new Spacer(),
                new Text("Press any button").TextStyle(TextStyle.Headline).Foreground(Color.Hex("#F8FAFC")).Opacity(breath).Id("press-any"),
                new Text("© Lamplight Games").TextStyle(TextStyle.Caption).Foreground(Color.Hex("#A5B4FC")).Padding(28f, 0f, 20f, 0f),
            }
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity)
            .OnAnyPress(() => started.Value = true)
            .Theme(Theme.Dark);
        }
    }
}

/// <summary>
/// A loading screen: the chapter it's loading, a tip that changes every few seconds, a bar that fills and a spinner.
/// Once it's loaded it asks for any button, and a press loads it again from the start.
/// </summary>
internal sealed record LoadingScreen : UiView
{
    // How long the pretend load takes, and how long each tip shows.
    private const float LoadSeconds = 8f;
    private const float TipSeconds = 4f;

    private static readonly string[] Tips =
    [
        "Lamps you light stay lit. Light them on the way in and you'll have a road home.",
        "A held block takes less from your stamina than a dodge, but leaves you standing still.",
        "The miller's dog knows the way through the reeds. Feed it and follow.",
        "Oil burns longer than wax. Keep a flask for the deep places.",
    ];

    public override UiView Body
    {
        get
        {
            double now = UseTime();
            var began = UseState(now);
            float seconds = (float)(now - began.Value);
            float loaded = Math.Clamp(seconds / LoadSeconds, 0f, 1f);
            int tip = (int)(seconds / TipSeconds) % Tips.Length;
            UiView page = new VStack(HorizontalAlignment.Leading, 0f)
            {
                new Text("Chapter 4").TextStyle(TextStyle.Headline).Foreground(Color.Hex("#FBBF24")),
                new Text("The flooded mill").FontSize(44f).Weight(FontWeight.Bold).Foreground(Color.Hex("#F8FAFC")),
                new Spacer(),
                Tip(tip),
                Footer(loaded),
            }
            .Padding(40f, 36f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading)
            .Theme(Theme.Dark);
            return loaded >= 1f ? page.OnAnyPress(() => began.Value = now) : page;
        }
    }

    private static UiView Tip(int index) => new VStack(HorizontalAlignment.Leading, 6f)
    {
        new Label("Tip", new Icon(IconName.Info)).TextStyle(TextStyle.Footnote).Foreground(Color.Hex("#A5B4FC")),
        new Text(Tips[index]).TextStyle(TextStyle.Title3).Foreground(Color.Hex("#E0E7FF")).Transition(Entrance.Rise, 0.5f).Id($"tip-{index}"),
    }.Frame(width: 520f, alignment: Alignment.Leading).Padding(0f, 0f, 28f, 0f);

    private static UiView Footer(float loaded) => new HStack(VerticalAlignment.Center, 16f)
    {
        loaded >= 1f
            ? new Text("Press any button").TextStyle(TextStyle.Headline).Foreground(Color.Hex("#F8FAFC")).Transition(Entrance.Fade, 0.4f).Id("loaded")
            : new ProgressView { Style = MeterStyle.Circular }.Frame(28f, 28f),
        new Spacer(),
        new VStack(HorizontalAlignment.Trailing, 6f)
        {
            new Text(MathF.Round(loaded * 100f).ToString(CultureInfo.InvariantCulture) + "%").MonospacedDigit().Foreground(Color.Hex("#C7D2FE")).Id("loaded-percent"),
            new ZStack(Alignment.Leading)
            {
                new Capsule().Fill(Color.White with { A = 0.15f }),
                new Capsule().Fill(Color.Hex("#FBBF24")).Frame(width: 320f * loaded),
            }.Frame(320f, 6f),
        },
    };
}
