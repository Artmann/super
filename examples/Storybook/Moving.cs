using System.Globalization;
using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The animation story: controls that move, views that ease in, a number of your own, and motion turned off.</summary>
internal static class Moving
{
    public static UiView Animation() => new Live<bool>(true, (on, set) => Page(
        new Specimen(
            "Motion",
            "Everything on this page moves unless it's turned off here, as a player who wants less motion would. .Animations(false) does it for all inside.",
            new Toggle("Animations", on, set).Id("animations").Frame(width: 320f)),
        new Specimen(
            "Controls that move",
            "A switch's knob slides across, and a slider's thumb eases to where the track was pressed. What they say they're set to changes at once.",
            new Live<(bool Music, float Volume)>((true, 0.3f), (state, change) => new VStack(HorizontalAlignment.Leading, 14f)
            {
                new Toggle("Music", state.Music, music => change(state with { Music = music })).Id("moving-music"),
                new Slider(state.Volume, volume => change(state with { Volume = volume })) { Label = "Volume" }.Id("moving-volume"),
            }.Frame(width: 320f))),
        new Specimen(
            "Waiting",
            "A progress view without a value spins, or sweeps along its bar, for as long as it shows.",
            Row(
                40f,
                new ProgressView("Connecting") { Style = MeterStyle.Circular },
                new ProgressView().Frame(width: 280f))),
        new Specimen(
            "Transitions",
            ".Transition(...) eases a view in when it appears. It takes its room at once, so nothing round it jumps.",
            new Live<int>(0, (shown, show) => new VStack(HorizontalAlignment.Leading, 16f)
            {
                new Button("Show them again", () => show(shown + 1)) { Style = ButtonStyle.Outline }.Id("show-again"),
                Row(
                    16f,
                    Arriving("Fade", Entrance.Fade, shown),
                    Arriving("Rise", Entrance.Rise, shown),
                    Arriving("Drop", Entrance.Drop, shown)),
            })),
        new Specimen(
            "A number of your own",
            "UseAnimation eases any number toward its target. This health bar drains smoothly when it's hit and fills again when healed.",
            new HealthDemo())).Animations(on));

    /// <summary>A tile that eases in again each time the count goes up, since a new id makes it a new view.</summary>
    private static UiView Arriving(string name, Entrance entrance, int shown) =>
        new Text(name)
            .Padding(24f, 14f)
            .Background(new RoundedRectangle(8f).Foreground(Tone.Primary).Opacity(0.25f))
            .Transition(entrance, 0.4f)
            .Id($"{name}-{shown}");
}

/// <summary>A health bar whose fill eases to the health it's given, with buttons to hurt and heal.</summary>
internal sealed record HealthDemo : UiView
{
    private const float Most = 100f;

    public override UiView Body
    {
        get
        {
            var health = UseState(Most);
            float shown = UseAnimation(health.Value / Most, 0.4f);
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new HStack
                {
                    new Text("Health"),
                    new Spacer(),
                    new Text(health.Value.ToString("0", CultureInfo.InvariantCulture)).MonospacedDigit().Foreground(Tone.Muted).Id("health"),
                },
                new ZStack(Alignment.Leading)
                {
                    new Capsule().Fill(Color.Hex("#EF4444", 0.2f)),
                    new Capsule().Fill(Color.Hex("#EF4444")).Frame(width: 320f * shown),
                }.Frame(320f, 10f),
                new HStack(Spacing: 8f)
                {
                    new Button("Take a hit", () => health.Value = Math.Max(health.Value - 30f, 0f)) { Style = ButtonStyle.Destructive }.Id("hit"),
                    new Button("Heal", () => health.Value = Most) { Style = ButtonStyle.Secondary }.Id("heal"),
                },
            }.Frame(width: 320f);
        }
    }
}
