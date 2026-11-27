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
/// a label; a circular one shows the percentage inside the ring. Made with only a label, it doesn't know how
/// far along it is and keeps moving instead: a ring spins, or a piece of the bar sweeps across.
/// </summary>
public sealed record ProgressView(string Label, float Value, float Total = 1f) : UiView
{
    // How long a spinning ring takes to go round, and a sweeping piece of bar to cross, in seconds.
    private const float Turn = 1.1f;
    private const float Sweep = 1.4f;

    /// <summary>A progress view that doesn't know how far along it is, which keeps moving until it's gone.</summary>
    public ProgressView(string Label = "")
        : this(Label, float.NaN)
    {
    }

    public MeterStyle Style { get; init; }

    /// <summary>Whether it doesn't know how far along it is, and spins.</summary>
    public bool Indeterminate => float.IsNaN(Value);

    /// <summary>How much is done, from 0 to 1.</summary>
    internal float Fraction => Total > 0f && !Indeterminate ? Math.Clamp(Value / Total, 0f, 1f) : 0f;

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var colors = environment.Theme.Colors;
            var metrics = environment.Metrics;
            if (Indeterminate)
            {
                return new SemanticsModifier(Moving(colors, metrics, UseTime()), new UiSemantics(UiRole.ProgressBar, Label));
            }

            string percent = MathF.Round(Fraction * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            UiView shown = Style == MeterStyle.Circular
                ? new MeterRing(Fraction, colors.Primary, metrics.Height + 8f, new Text(percent).FontSize(MathF.Round(metrics.FontSize * 0.8f)).MonospacedDigit())
                : Meters.Bar(Label, percent, new MeterBar(Fraction, colors.Primary, 8f), metrics);
            return new SemanticsModifier(shown, new UiSemantics(UiRole.ProgressBar, Label, UiSemantics.Number(Value)));
        }
    }

    /// <summary>
    /// A quarter of a ring going round, beside the label if there is one, or a third of a bar sweeping across
    /// under it. Still, inside <c>.Animations(false)</c>, it rests where it starts.
    /// </summary>
    private UiView Moving(ThemeColors colors, ControlMetrics metrics, double now)
    {
        if (Style == MeterStyle.Circular)
        {
            float side = metrics.Height - 8f;
            float start = (float)(now / Turn % 1.0);
            var ring = new ZStack
            {
                new Arc(0f, 1f, 2.5f).Fill(colors.Muted),
                new Arc(start, 0.25f, 2.5f).Fill(colors.Primary),
            }.Frame(side, side);
            return Label.Length == 0
                ? ring
                : new HStack(Spacing: metrics.Gap) { ring, new Text(Label).FontSize(metrics.FontSize).Foreground(Tone.Muted) };
        }

        // The piece runs from off the start to off the end, so it enters and leaves rather than bouncing. It
        // starts halfway, so at time 0, where it rests with motion off, it sits in the middle of the bar.
        float across = (float)((now / Sweep + 0.5) % 1.0) * 1.3f - 0.3f;
        var bar = new MeterBar(Math.Min(across + 0.3f, 1f), colors.Primary, 8f) { From = Math.Max(across, 0f) };
        return Meters.Bar(Label, "", bar, metrics);
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
