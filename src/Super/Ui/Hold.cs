namespace Super.Ui;

/// <summary>
/// A button that has to be held, for what shouldn't happen by accident, such as quitting or skipping a scene. A ring
/// fills while the mouse, a finger, Enter, Space or the gamepad's South button holds it, and once it's full
/// <paramref name="OnHold"/> runs on the next tick, with the confirm sound. Letting go before then empties it again and
/// does nothing. Holding on after it's full doesn't run it again.
/// </summary>
public sealed record HoldButton(string Label, Action? OnHold = null, float Seconds = 1f) : UiView
{
    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var theme = environment.Theme;
            var metrics = environment.Metrics;
            var held = UseState(0f);
            var look = UseState(default(PressState));
            bool off = Disabled || environment.Disabled;
            // Quick while it fills, so it keeps up with the hold, and slower as it drains.
            float shown = UseAnimation(held.Value, held.Value > 0f ? 0.05f : 0.25f);
            var face = new HStack(Spacing: metrics.Gap)
            {
                Ring(shown, theme.Colors, metrics.FontSize * 1.25f),
                new Text(Label).FontSize(metrics.FontSize).Weight(FontWeight.Medium).Foreground(theme.Colors.SecondaryForeground).LineLimit(1),
            }
                .Padding(metrics.Padding, 0f)
                .Frame(minHeight: metrics.Height)
                .Background(Button.Shade(theme.Colors.Secondary, theme.Colors, look.Value))
                .CornerRadius(theme.Radius)
                .Opacity(off ? 0.5f : 1f);
            var hold = new HoldModifier(new ClickModifier(face, null, look, off), OnHold, Seconds, held, off);
            var meaning = new SemanticsModifier(hold, new UiSemantics(UiRole.Button, Label, UiSemantics.Number(held.Value)) { Disabled = off });

            // Enter and South start the hold, which the hold itself finishes, so pressing them does nothing more.
            return new FocusModifier(meaning, new FocusKeys { Activate = static () => { }, Disabled = off });
        }
    }

    private static UiView Ring(float fraction, ThemeColors colors, float side) => new ZStack
    {
        new Arc(0f, 1f, 2.5f).Fill(colors.SecondaryForeground with { A = 0.25f }),
        fraction > 0f ? new Arc(0f, fraction, 2.5f).Fill(colors.Primary) : null,
    }.Frame(side, side);
}

/// <summary>What a hold fills: <paramref name="Progress"/>, from 0 to 1, which <see cref="UiHolds"/> moves a tick at a time.</summary>
internal sealed record HoldModifier(UiView Child, Action? Done, float Seconds, State<float> Progress, bool Disabled) : Modifier(Child);

/// <summary>
/// The keys focus has taken, which the game sees no part of until they're let go, and the view the last one
/// activated, so a hold button knows it's still being held.
/// </summary>
internal sealed class UiTakenKeys(GamepadInput pad)
{
    private readonly HashSet<Key> _keys = [];
    private UiNode? _activated;

    public void Add(Key key) => _keys.Add(key);

    public bool Remove(Key key) => _keys.Remove(key);

    public bool Contains(Key key) => _keys.Contains(key);

    /// <summary>Notes the view a key or South just activated.</summary>
    public void Activated(UiNode node) => _activated = node;

    /// <summary>
    /// The hold button in <paramref name="focused"/>, while the key or South button that activated it is still held, which
    /// is how a <see cref="HoldButton"/> is held without the mouse.
    /// </summary>
    public UiNode? Holding(UiNode? focused) =>
        _activated is { } node && node == focused
        && (_keys.Contains(Key.Enter) || _keys.Contains(Key.KeyPadEnter) || _keys.Contains(Key.Space) || pad.Buttons.Physical((int)GamepadButton.South))
            ? UiFocusStops.Below<HoldModifier>(node)
            : null;
}

/// <summary>
/// Counts how long the view under a hold has been held, a tick at a time so a replay holds the same, and runs it
/// once it's held long enough. One hold at a time: whatever holds it, the mouse, a finger or a key.
/// </summary>
internal sealed class UiHolds(UiTree tree, List<Action> callbacks, int ticksPerSecond)
{
    private UiNode? _node;
    private int _ticks;

    /// <summary>Once a tick: what's held down this tick, or null when nothing is.</summary>
    public void Tick(UiNode? held)
    {
        var node = UiPointerSearch.Find<HoldModifier>(held) is { View: HoldModifier { Disabled: false }, Environment.Disabled: false } found ? found : null;
        if (node != _node)
        {
            if (_node is { View: HoldModifier old })
            {
                old.Progress.Value = 0f;
            }

            _node = node;
            _ticks = 0;
        }

        if (node is not { View: HoldModifier hold })
        {
            return;
        }

        int needed = Math.Max(1, (int)MathF.Round(hold.Seconds * ticksPerSecond));
        if (_ticks >= needed)
        {
            return;
        }

        _ticks++;
        hold.Progress.Value = _ticks / (float)needed;
        if (_ticks == needed)
        {
            if (hold.Done is { } done)
            {
                callbacks.Add(done);
            }

            tree.Cues.Raise(node, UiCue.Confirm);
        }
    }
}
