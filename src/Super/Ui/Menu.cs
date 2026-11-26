using System.Numerics;

namespace Super.Ui;

/// <summary>
/// One row in a <see cref="Menu"/> or a context menu: what it says, and what picking it does. Picking it
/// closes the menu and runs <paramref name="Action"/> on the tick. Use <see cref="Divider"/> for a line
/// between groups of rows.
/// </summary>
public sealed record MenuItem(string Label, Action? Action = null)
{
    /// <summary>A line between groups of rows. It can't be picked.</summary>
    public static MenuItem Divider { get; } = new(string.Empty) { IsDivider = true };

    public IconName? Icon { get; init; }

    /// <summary>A key to show at the trailing edge, such as "Ctrl+S". It's only shown: the game handles the key itself.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Whether it destroys something, in the theme's destructive colour.</summary>
    public bool Destructive { get; init; }

    /// <summary>Whether it's faded and can't be picked.</summary>
    public bool Disabled { get; init; }

    /// <summary>Whether it shows a check, as the picked one in a picker's menu does.</summary>
    public bool Checked { get; init; }

    internal bool IsDivider { get; private init; }
}

/// <summary>
/// A button that opens a list of <paramref name="Items"/> under it, or above it when there's no room
/// below. Picking an item closes the list and runs its action on the tick. A click outside, Escape or the
/// gamepad's East button close it, and the arrows and the d-pad move through it.
/// </summary>
public sealed record Menu(string Label, IReadOnlyList<MenuItem> Items) : UiView
{
    public IconName? Icon { get; init; }

    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var open = UseState(false);
            bool off = Disabled || Environment.Disabled;
            void Close() => open.Value = false;
            var trigger = new MenuTrigger(Label, Icon, () => open.Value = !open.Value, off);
            var meaning = new SemanticsModifier(trigger, new UiSemantics(UiRole.Menu, Label, Checked: open.Value) { Disabled = off });
            return new PopupModifier(meaning, open.Value && !off ? MenuPanel.Layer(Items, Close) : null);
        }
    }
}

/// <summary>The face of a button that opens a menu: its label, and a chevron at the trailing edge.</summary>
internal sealed record MenuTrigger(string Label, IconName? Icon, Action Toggle, bool Off) : UiView
{
    public override UiView Body => new Pressable(Look, Toggle, Off);

    private UiView Look(PressState look)
    {
        var theme = Environment.Theme;
        var metrics = Environment.Metrics;
        UiView text = Icon is { } name ? new Label(Label, name) : new Text(Label);
        return new HStack(Spacing: metrics.Gap)
            {
                text.FontSize(metrics.FontSize).Weight(FontWeight.Medium).LineLimit(1),
                new Icon(IconName.ChevronDown).FontSize(metrics.FontSize).Foreground(theme.Colors.MutedForeground),
            }
            .Foreground(theme.Colors.Foreground)
            .Padding(metrics.Padding, 0f)
            .Frame(minHeight: metrics.Height)
            .Background(look.Pressed ? theme.Colors.Hover.Mix(theme.Colors.Foreground, 0.06f) : look.Hovered ? theme.Colors.Hover : default)
            .Border(1f, theme.Colors.Input)
            .CornerRadius(theme.Radius)
            .Opacity(Off ? 0.5f : 1f);
    }
}

/// <summary>The rows of an open menu, one under the other in a raised card. The first row that can be picked has focus.</summary>
internal sealed record MenuPanel(IReadOnlyList<MenuItem> Items, Action Close) : UiView
{
    /// <summary>A menu that opens under the view it hangs off, and closes on a click outside.</summary>
    public static LayerView Layer(IReadOnlyList<MenuItem> items, Action close) =>
        new(new PopupCard(new MenuPanel(items, close), 4f), LayerPlace.Below, close) { Light = true };

    public override UiView Body
    {
        get
        {
            bool checks = Items.Any(item => item.Checked);
            // Focus starts on the checked row, as on the picked one in a picker's menu, or else on the first.
            var start = Items.FirstOrDefault(item => item is { Checked: true, Disabled: false, IsDivider: false })
                ?? Items.FirstOrDefault(item => item is { Disabled: false, IsDivider: false });
            var rows = new VStack(HorizontalAlignment.Leading, 0f);
            foreach (var item in Items)
            {
                if (item.IsDivider)
                {
                    rows.Add(new Divider().Padding(0f, 4f));
                    continue;
                }

                var row = Row(item, checks);
                rows.Add(ReferenceEquals(item, start) ? row.DefaultFocus() : row);
            }

            return rows.Frame(minWidth: 160f);
        }
    }

    private UiView Row(MenuItem item, bool checks)
    {
        var close = Close;
        var action = item.Action;
        var pressable = new Pressable(look => Look(item, checks, look), () =>
        {
            close();
            action?.Invoke();
        }, item.Disabled);
        return new SemanticsModifier(pressable, new UiSemantics(UiRole.MenuItem, item.Label, Checked: item.Checked) { Disabled = item.Disabled });
    }

    private UiView Look(MenuItem item, bool checks, PressState look)
    {
        var theme = Environment.Theme;
        var metrics = Environment.Metrics;
        var text = item.Destructive ? theme.Colors.Destructive : theme.Colors.Foreground;
        float side = metrics.FontSize;
        var row = new HStack(Spacing: metrics.Gap);
        if (checks)
        {
            row.Add(item.Checked ? new Icon(IconName.Check).Frame(side, side) : new Spacer().Frame(side, side));
        }

        UiView label = item.Icon is { } icon ? new Label(item.Label, icon) : new Text(item.Label);
        row.Add(label.LineLimit(1));
        row.Add(new Spacer(MinLength: 16f));
        if (item.Shortcut is { } shortcut)
        {
            row.Add(new Text(shortcut).Foreground(theme.Colors.MutedForeground).LineLimit(1));
        }

        return row
            .FontSize(metrics.FontSize)
            .Foreground(text)
            .Padding(8f, 0f)
            .Frame(maxWidth: UiExtensions.Infinity, minHeight: MathF.Round(metrics.Height * 0.85f), alignment: Alignment.Leading)
            .Background(look.Hovered || look.Pressed ? theme.Colors.Hover : default)
            .CornerRadius(theme.RadiusSmall)
            .Opacity(item.Disabled ? 0.5f : 1f);
    }
}

/// <summary>A view with a menu that opens where it's right-clicked or held.</summary>
internal sealed record ContextMenuView(UiView Child, IReadOnlyList<MenuItem> Items) : UiView
{
    public override UiView Body
    {
        get
        {
            var at = UseState<Vector2?>(null);
            void Close() => at.Value = null;
            var target = new ContextModifier(Child, point => at.Value = point);
            return new PopupModifier(target, at.Value is { } point && !Environment.Disabled
                ? new LayerView(new PopupCard(new MenuPanel(Items, Close), 4f), LayerPlace.At, Close) { Point = point, Light = true }
                : null);
        }
    }
}

/// <summary>Opens a context menu at a point, when the view is right-clicked or a finger holds still on it.</summary>
internal sealed record ContextModifier(UiView Child, Action<Vector2> Open) : PointerModifier(Child);
