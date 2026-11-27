namespace Super.Ui;

/// <summary>How a <see cref="Toggle"/> looks.</summary>
public enum ToggleStyle
{
    /// <summary>A sliding switch after the label, which fills the row, as in a settings list.</summary>
    Switch,

    /// <summary>A box with a tick before the label.</summary>
    Checkbox,
}

/// <summary>
/// Something on or off, such as a setting. It shows <paramref name="IsOn"/>, and a click anywhere on it,
/// label included, calls <paramref name="OnChange"/> with the other value on the next tick, which the
/// game keeps and passes back. With an empty label it's only the switch or the box.
/// </summary>
public sealed record Toggle(string Label, bool IsOn, Action<bool>? OnChange = null, bool Disabled = false) : UiView
{
    public ToggleStyle Style { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var theme = environment.Theme;
            var metrics = environment.Metrics;
            bool off = Disabled || environment.Disabled;
            var change = OnChange;
            bool next = !IsOn;

            // How far the switch's knob is across, and its track filled in. The box ticks at once. Asked on every
            // run, whatever the style, so the hooks keep their order if the style changes.
            float knob = UseAnimation(IsOn ? 1f : 0f, AnimationExtensions.Quick);
            float on = Style == ToggleStyle.Switch ? knob : 0f;
            var toggle = new Pressable(
                _ => Row(theme, metrics, on).Opacity(off ? 0.5f : 1f),
                change is null ? null : () => change(next),
                off);
            var role = Style == ToggleStyle.Checkbox ? UiRole.Checkbox : UiRole.Switch;
            return new SemanticsModifier(toggle, new UiSemantics(role, Label, Checked: IsOn) { Disabled = off });
        }
    }

    private UiView Row(Theme theme, ControlMetrics metrics, float on)
    {
        var mark = Style == ToggleStyle.Checkbox ? Box(theme, metrics) : Switch(theme, metrics, on);
        if (Label.Length == 0)
        {
            return mark;
        }

        var label = new Text(Label).FontSize(metrics.FontSize);
        return Style == ToggleStyle.Checkbox
            ? new HStack(Spacing: metrics.Gap) { mark, label }
            : new HStack(Spacing: metrics.Gap) { label, new Spacer(), mark };
    }

    private static UiView Switch(Theme theme, ControlMetrics metrics, float on)
    {
        float height = MathF.Round(metrics.FontSize * 1.43f);
        float width = MathF.Round(height * 1.8f);
        float knob = height - 4f;
        return new ZStack(Alignment.Leading)
        {
            new Capsule().Fill(theme.Colors.Input.Mix(theme.Colors.Primary, on)),
            new Circle()
                .Fill(theme.Colors.Background)
                .Frame(knob, knob)
                .CornerRadius(knob / 2f)
                .Shadow(ShadowSize.Small)
                .Padding(2f)
                .Offset(MathF.Round((width - height) * on * 4f) / 4f, 0f),
        }.Frame(width, height);
    }

    private UiView Box(Theme theme, ControlMetrics metrics)
    {
        float side = MathF.Round(metrics.FontSize * 8f / 7f);
        var shape = new RoundedRectangle(theme.RadiusSmall);
        if (!IsOn)
        {
            return shape.Stroke(theme.Colors.Input).Frame(side, side);
        }

        return new ZStack
        {
            shape.Fill(theme.Colors.Primary),
            new Icon(IconName.Check).FontSize(MathF.Round(side * 0.7f)).Foreground(theme.Colors.PrimaryForeground),
        }.Frame(side, side);
    }
}
