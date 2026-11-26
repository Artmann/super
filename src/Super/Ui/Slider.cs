namespace Super.Ui;

/// <summary>
/// Picks a number between <paramref name="Min"/> and <paramref name="Max"/> by dragging a thumb along a
/// track, or by pressing anywhere on the track to jump there. It shows <paramref name="Value"/>, and calls
/// <paramref name="OnChange"/> with the new value on the next tick while it's dragged, which the game keeps
/// and passes back. It fills the width it's offered.
/// </summary>
public sealed record Slider(float Value, Action<float>? OnChange = null, float Min = 0f, float Max = 1f) : UiView
{
    /// <summary>What the value moves in, such as 1 for whole numbers. Zero, the default, moves smoothly.</summary>
    public float Step { get; init; }

    /// <summary>What the slider is called, such as "Music volume". Tests find it by this; it isn't drawn.</summary>
    public string Label { get; init; } = "";

    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var colors = environment.Theme.Colors;
            var metrics = environment.Metrics;
            bool off = Disabled || environment.Disabled;
            float thumb = MathF.Round(metrics.FontSize * 8f / 7f);
            float fraction = Max > Min ? Math.Clamp((Value - Min) / (Max - Min), 0f, 1f) : 0f;
            var bar = new MeterBar(fraction, colors.Primary, MathF.Round(thumb * 0.375f), thumb)
                .Frame(maxWidth: UiExtensions.Infinity, minHeight: metrics.Height);
            var slider = new SlideModifier(bar, Slid, thumb / 2f, off).Opacity(off ? 0.5f : 1f);
            var meaning = new SemanticsModifier(slider, new UiSemantics(UiRole.Slider, Label, UiSemantics.Number(Value)) { Disabled = off });
            return new FocusModifier(meaning, new FocusKeys { Adjust = Nudged, Disabled = off });
        }
    }

    /// <summary>What left and right move it by: a step, or a twentieth of the range when it moves smoothly.</summary>
    private void Nudged(int by)
    {
        float step = Step > 0f ? Step : (Max - Min) / 20f;
        float value = Math.Clamp(Value + by * step, Math.Min(Min, Max), Math.Max(Min, Max));
        if (value != Value)
        {
            OnChange?.Invoke(value);
        }
    }

    /// <summary>The value at this fraction of the track, on a step if there is one.</summary>
    internal float ValueAt(float fraction)
    {
        float value = Min + fraction * (Max - Min);
        if (Step > 0f)
        {
            value = Min + MathF.Round((value - Min) / Step) * Step;
        }

        return Math.Clamp(value, Math.Min(Min, Max), Math.Max(Min, Max));
    }

    private void Slid(float fraction)
    {
        float value = ValueAt(fraction);
        if (value != Value)
        {
            OnChange?.Invoke(value);
        }
    }
}

/// <summary>
/// A rounded track filled part of the way in a colour, with a round thumb at the end of the fill if
/// <paramref name="Thumb"/> is above zero. It fills the width it's offered. Sliders, progress bars and
/// gauges are drawn with it.
/// </summary>
internal sealed record MeterBar(float Fraction, Paint Fill, float Thickness, float Thumb = 0f) : PrimitiveView
{
    // How wide a bar is when nothing says.
    private const float IdealWidth = 160f;

    internal override IReadOnlyList<UiView?> Children => [];

    internal override bool Blocks => true;

    internal override UiSize Measure(UiNode node, Proposal proposal) => new(proposal.Width ?? IdealWidth, Math.Max(Thickness, Thumb));

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        var colors = node.Environment.Theme.Colors;
        var frame = node.Frame;
        float f = Math.Clamp(Fraction, 0f, 1f);
        var track = new UiRect(frame.X, frame.Y + (frame.Height - Thickness) / 2f, frame.Width, Thickness);
        draw.AddBox(track, colors.Muted, Thickness / 2f);

        // The thumb's middle runs from half a thumb in at one end to half a thumb in at the other.
        float end = Thumb > 0f ? Thumb / 2f + f * (frame.Width - Thumb) : f * frame.Width;
        if (end > 0f)
        {
            draw.AddBox(track with { Width = end }, Fill, Thickness / 2f);
        }

        if (Thumb > 0f)
        {
            var knob = new UiRect(frame.X + end - Thumb / 2f, frame.Y + (frame.Height - Thumb) / 2f, Thumb, Thumb);
            draw.AddShadow(knob, Thumb / 2f, node.Environment.Theme.Shadows.Small);
            draw.AddEllipse(knob, colors.Background, new UiStroke(1.5f, Fill.From));
        }
    }
}
