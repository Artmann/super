namespace Super.Ui;

/// <summary>One page of a <see cref="TabView"/>: the label on its tab, and what it shows when it's picked.</summary>
public sealed record Tab(string Label, UiView Content) : UiView
{
    public IconName? Icon { get; init; }

    public override UiView Body => Content;
}

/// <summary>
/// Pages a player switches between with a bar of tabs along the top, as on a settings or inventory screen.
/// Add <see cref="Tab"/>s with collection syntax. It shows the tab at <paramref name="Selection"/> and calls
/// <paramref name="OnChange"/> with the index of the one clicked, which the game keeps and passes back; leave
/// Selection null and it keeps the picked tab itself, starting with the first. The tab bar is one focus stop
/// whose left and right switch tabs, and the gamepad's bumpers switch them from anywhere on the screen.
/// </summary>
public sealed record TabView(int? Selection = null, Action<int>? OnChange = null) : ViewList
{
    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var own = UseState(0);
            var tabs = Items.OfType<Tab>().ToArray();
            if (tabs.Length == 0)
            {
                return new VStack(Spacing: 0f);
            }

            int picked = Math.Clamp(Selection ?? own.Value, 0, tabs.Length - 1);
            var change = OnChange;
            Action<int> pick = index =>
            {
                own.Value = index;
                change?.Invoke(index);
            };
            Action<int> step = by => pick((picked + by + tabs.Length) % tabs.Length);
            var keys = new FocusKeys { Adjust = by => pick(Math.Clamp(picked + by, 0, tabs.Length - 1)), Activate = () => step(1) };
            var bar = new FocusModifier(Bar(environment, tabs, picked, pick), keys);
            var page = tabs[picked].Content.Id(tabs[picked].Label);
            var view = new VStack(HorizontalAlignment.Leading, 0f)
            {
                bar,
                new Divider(),
                page.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading),
            };
            return new SignUpModifier(view, null, step);
        }
    }

    private static HStack Bar(UiEnvironment environment, Tab[] tabs, int picked, Action<int> pick)
    {
        var bar = new HStack(Spacing: 4f);
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            var tab = tabs[i];
            bool shown = i == picked;
            var pressable = new Pressable(look => Face(environment, tab, shown, look), () => pick(index));
            bar.Add(new SemanticsModifier(pressable, new UiSemantics(UiRole.Tab, tab.Label, Checked: shown)));
        }

        bar.Add(new Spacer());
        return bar;
    }

    /// <summary>A tab's label, bright with a line under it when it's the one shown.</summary>
    private static UiView Face(UiEnvironment environment, Tab tab, bool shown, PressState look)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        var colors = theme.Colors;
        var label = new HStack(Spacing: metrics.Gap)
        {
            tab.Icon is { } icon ? new Icon(icon).FontSize(metrics.FontSize) : null,
            new Text(tab.Label).FontSize(metrics.FontSize).Weight(FontWeight.Medium).LineLimit(1),
        }.Foreground(shown || look.Hovered ? colors.Foreground : colors.MutedForeground);
        return label
            .Padding(metrics.Padding, MathF.Round(metrics.Gap * 1.5f))
            .Overlay(new Rectangle().Fill(shown ? colors.Primary : default).Frame(height: 2f), Alignment.Bottom);
    }
}

/// <summary>
/// Signs a navigation stack's way back, or a tab view's switch, up with the tree when it's placed, so Escape,
/// the gamepad's East button and its bumpers reach it.
/// </summary>
internal sealed record SignUpModifier(UiView Child, Action? Back, Action<int>? Tabs) : Modifier(Child)
{
    internal override void Place(UiNode node, UiRect frame)
    {
        if (Back is { } back)
        {
            node.Tree.Layers.CanGoBack(node, back);
        }

        if (Tabs is { } tabs)
        {
            node.Tree.Layers.SwitchesTabs(node, tabs);
        }

        Inner(node).Place(frame);
    }
}

/// <summary>The gamepad's bumpers, which switch tabs in the innermost tab view on screen.</summary>
internal static class TabKeys
{
    private const int Left = (int)GamepadButton.LeftBumper;
    private const int Right = (int)GamepadButton.RightBumper;

    public static void Bumpers(ButtonSet buttons, UiLayers layers, List<Action> callbacks)
    {
        if (layers.SwitchTab is not { } switchTab)
        {
            return;
        }

        if (buttons.WentDown(Left))
        {
            buttons.Take(Left);
            callbacks.Add(Switch(switchTab, -1));
        }

        if (buttons.WentDown(Right))
        {
            buttons.Take(Right);
            callbacks.Add(Switch(switchTab, 1));
        }
    }

    // A method of its own, so the closure is made only when a bumper went down. One that captured in
    // Bumpers would be made each time it runs, every tick.
    private static Action Switch(Action<int> switchTab, int by) => () => switchTab(by);
}
