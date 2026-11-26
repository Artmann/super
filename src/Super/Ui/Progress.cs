using System.Globalization;

namespace Super.Ui;

/// <summary>How a <see cref="ProgressView"/> or a <see cref="Gauge"/> is drawn.</summary>
public enum MeterStyle
{
    /// <summary>A bar that fills the width it's offered.</summary>
    Linear,

    /// <summary>A ring with the number in the middle.</summary>
    Circular,
}

/// <summary>
/// How far along a task is, such as loading or crafting: <paramref name="Value"/> of
/// <paramref name="Total"/>. A linear one shows the label and the percentage above the bar, when there's
/// a label; a circular one shows the percentage inside the ring.
/// </summary>
public sealed record ProgressView(string Label, float Value, float Total = 1f) : UiView
{
    public MeterStyle Style { get; init; }

    /// <summary>How much is done, from 0 to 1.</summary>
    internal float Fraction => Total > 0f ? Math.Clamp(Value / Total, 0f, 1f) : 0f;

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var colors = environment.Theme.Colors;
            var metrics = environment.Metrics;
            string percent = MathF.Round(Fraction * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            UiView shown = Style == MeterStyle.Circular
                ? new MeterRing(Fraction, colors.Primary, metrics.Height + 8f, new Text(percent).FontSize(MathF.Round(metrics.FontSize * 0.8f)).MonospacedDigit())
                : Meters.Bar(Label, percent, new MeterBar(Fraction, colors.Primary, 8f), metrics);
            return new SemanticsModifier(shown, new UiSemantics(UiRole.ProgressBar, Label, UiSemantics.Number(Value)));
        }
    }
}

/// <summary>
/// A reading on a scale from <paramref name="Min"/> to <paramref name="Max"/>, such as health, speed or
/// a temperature. A circular one is a ring open at the bottom with the reading in the middle and the label
/// under it; a linear one is a bar with the label and reading above, and the ends of the scale below.
/// </summary>
public sealed record Gauge(string Label, float Value, float Min = 0f, float Max = 1f) : UiView
{
    // The ring is open at the bottom: it starts down to the left and turns three quarters.
    private const float RingStart = 0.625f;
    private const float RingSweep = 0.75f;

    public MeterStyle Style { get; init; } = MeterStyle.Circular;

    /// <summary>The colour of the filled part, instead of the theme's primary colour.</summary>
    public Color? Tint { get; init; }

    /// <summary>What the reading says, such as "72 bpm", instead of the bare number.</summary>
    public string? ValueLabel { get; init; }

    internal float Fraction => Max > Min ? Math.Clamp((Value - Min) / (Max - Min), 0f, 1f) : 0f;

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var metrics = environment.Metrics;
            var tint = Tint ?? environment.Theme.Colors.Primary;
            string reading = ValueLabel ?? UiSemantics.Number(Value);
            UiView shown = Style == MeterStyle.Circular
                ? Circular(tint, reading, metrics)
                : new VStack(HorizontalAlignment.Leading, 6f)
                {
                    Meters.Bar(Label, reading, new MeterBar(Fraction, tint, 6f), metrics),
                    new HStack
                    {
                        new Text(UiSemantics.Number(Min)),
                        new Spacer(),
                        new Text(UiSemantics.Number(Max)),
                    }.TextStyle(TextStyle.Caption).Foreground(Tone.Muted).MonospacedDigit(),
                };
            return new SemanticsModifier(shown, new UiSemantics(UiRole.Gauge, Label, UiSemantics.Number(Value)));
        }
    }

    private UiView Circular(Color tint, string reading, ControlMetrics metrics)
    {
        float side = metrics.Height * 2f;
        var middle = new VStack(Spacing: 0f)
        {
            new Text(reading).FontSize(MathF.Round(side * 0.24f)).Weight(FontWeight.SemiBold).MonospacedDigit().LineLimit(1),
            new Text(Label).FontSize(MathF.Round(side * 0.14f)).Foreground(Tone.Muted).LineLimit(1),
        };
        return new MeterRing(Fraction, tint, side, middle) { Start = RingStart, Sweep = RingSweep };
    }
}

/// <summary>
/// A ring this wide on the theme's muted colour, filled part of the way round from <see cref="Start"/>
/// through <see cref="Sweep"/>, both fractions of a turn, with a view in the middle.
/// </summary>
internal sealed record MeterRing(float Fraction, Color Fill, float Side, UiView Middle) : UiView
{
    public float Start { get; init; }

    public float Sweep { get; init; } = 1f;

    public override UiView Body
    {
        get
        {
            float thickness = MathF.Round(Side / 11f);
            return new ZStack
            {
                new Arc(Start, Sweep, thickness).Fill(Environment.Theme.Colors.Muted),
                Fraction > 0f ? new Arc(Start, Sweep * Fraction, thickness).Fill(Fill) : null,
                Middle.Padding(thickness),
            }.Frame(Side, Side);
        }
    }
}

/// <summary>The layout progress views and gauges share.</summary>
internal static class Meters
{
    /// <summary>A bar under a line with the label before and the reading after, or only the bar without a label.</summary>
    public static UiView Bar(string label, string reading, MeterBar bar, ControlMetrics metrics)
    {
        if (label.Length == 0)
        {
            return bar;
        }

        return new VStack(HorizontalAlignment.Leading, 8f)
        {
            new HStack
            {
                new Text(label).Weight(FontWeight.Medium).LineLimit(1),
                new Spacer(),
                new Text(reading).Foreground(Tone.Muted).MonospacedDigit().LineLimit(1),
            }.FontSize(metrics.FontSize),
            bar,
        };
    }
}
