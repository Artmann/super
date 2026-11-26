using System.Globalization;

namespace Super.Ui;

/// <summary>
/// A whole number with a minus and a plus button, for counts such as how many to craft or how many
/// players. Each press calls <paramref name="OnChange"/> with the value one <see cref="Step"/> along on
/// the next tick, and a button turns off at its end of the range. With a label, it fills the row with
/// the label before the buttons.
/// </summary>
public sealed record Stepper(string Label, int Value, Action<int>? OnChange = null, int Min = int.MinValue, int Max = int.MaxValue) : UiView
{
    public int Step { get; init; } = 1;

    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var metrics = environment.Metrics;
            bool off = Disabled || environment.Disabled;
            var buttons = new HStack(Spacing: MathF.Round(metrics.Gap / 2f))
            {
                StepButton(IconName.Minus, -Step, off || Value <= Min),
                new Text(Value.ToString(CultureInfo.InvariantCulture))
                    .FontSize(metrics.FontSize)
                    .Weight(FontWeight.Medium)
                    .MonospacedDigit()
                    .LineLimit(1)
                    .Frame(minWidth: metrics.Height)
                    .Opacity(off ? 0.5f : 1f),
                StepButton(IconName.Plus, Step, off || Value >= Max),
            };
            UiView stepper = Label.Length == 0
                ? buttons
                : new HStack(Spacing: metrics.Gap) { new Text(Label).FontSize(metrics.FontSize), new Spacer(), buttons };
            return new SemanticsModifier(
                stepper,
                new UiSemantics(UiRole.Stepper, Label, Value.ToString(CultureInfo.InvariantCulture)) { Disabled = off });
        }
    }

    private Button StepButton(IconName icon, int by, bool disabled)
    {
        var change = OnChange;
        int next = Next(by);
        return new Button("", change is null ? null : () => change(next), disabled) { Style = ButtonStyle.Outline, Icon = icon };
    }

    /// <summary>The value this far along, kept in the range and clear of overflow.</summary>
    private int Next(int by) => (int)Math.Clamp((long)Value + by, Min, Max);
}
