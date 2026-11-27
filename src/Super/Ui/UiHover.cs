using System.Numerics;

namespace Super.Ui;

/// <summary>
/// How things under the cursor look: the press state a clickable view is shown in, the
/// scroll bar that's lit, and the tooltip, which shows after the cursor rests a moment.
/// </summary>
internal sealed class UiHover(UiTree tree, int ticksPerSecond)
{
    private const float TooltipDelaySeconds = 0.5f;

    private readonly int _tooltipTicks = (int)MathF.Ceiling(TooltipDelaySeconds * ticksPerSecond);

    private UiNode? _look;
    private PressState _lookState;
    private UiNode? _litBar;

    private UiNode? _tip;
    private int _tipAge;
    private bool _tipHidden;

    /// <summary>Counts how long the cursor has rested on the tooltip's view.</summary>
    public void Tick() => _tipAge++;

    /// <summary>A press or a scroll puts a tooltip away until the cursor reaches something else.</summary>
    public void HideTip() => _tipHidden = true;

    /// <summary>What the tooltip shows now, if anything. None shows while a button is held.</summary>
    public UiView? Tooltip(bool pressing) =>
        _tip is not null && !_tipHidden && !pressing && _tipAge >= _tooltipTicks ? ((TooltipModifier)_tip.View).Content : null;

    /// <summary>Shows a clickable view hovered, and pressed if it is, and the last one plain. Returns whether that changed.</summary>
    public bool ShowPress(UiNode? look, bool pressed)
    {
        var state = look is null ? default : new PressState(true, pressed);
        if (look == _look && state == _lookState)
        {
            return false;
        }

        if (_look is { Mounted: true, View: ClickModifier { Look: { } old } })
        {
            old.Value = default;
        }

        if (look?.View is ClickModifier { Look: { } shown } clickable)
        {
            shown.Value = state;

            // The pointer coming onto something new it can press. A finger is pressing already, so it's heard then.
            if (look != _look && !pressed && !clickable.Disabled && !look.Environment.Disabled)
            {
                tree.Cues.Raise(look, UiCue.Hover);
            }
        }

        _look = look;
        _lookState = state;
        return true;
    }

    /// <summary>
    /// Lights the bar whose thumb is held, or else the bar under the cursor while the left
    /// button is up. Returns whether that changed.
    /// </summary>
    public bool RefreshBar(UiNode? heldBar, UiNode? hit, Vector2 position, bool leftHeld)
    {
        var lit = heldBar;
        if (lit is null && !leftHeld && hit?.View is ScrollView bar && bar.BarOf(hit).Contains(position.X, position.Y))
        {
            lit = hit;
        }

        if (lit == _litBar)
        {
            return false;
        }

        _litBar?.Scroll.BarLit = false;
        lit?.Scroll.BarLit = true;
        _litBar = lit;
        tree.RequestLayout();
        return true;
    }

    /// <summary>Starts the tooltip's wait over when the cursor reaches a different one.</summary>
    public void FollowTip(UiNode? hit)
    {
        var tip = UiPointerSearch.Find<TooltipModifier>(hit);
        if (tip != _tip)
        {
            _tip = tip;
            _tipAge = 0;
            _tipHidden = false;
        }
    }
}
