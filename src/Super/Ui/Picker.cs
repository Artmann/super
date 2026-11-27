namespace Super.Ui;

/// <summary>One choice in a <see cref="Picker{T}"/>: what it says, and the value it stands for.</summary>
public sealed record PickerOption<T>(string Label, T Value);

/// <summary>How a <see cref="Picker{T}"/> lays out its choices.</summary>
public enum PickerStyle
{
    /// <summary>Side by side in one bar, the picked one raised. It fills the width it's offered.</summary>
    Segmented,

    /// <summary>One under the other, each with a round button that's filled when it's picked.</summary>
    Radio,

    /// <summary>
    /// A button that shows the picked one and opens a menu of them all, with a check by the picked one. It
    /// takes the least room, so it suits a long list or a settings row.
    /// </summary>
    Menu,

    /// <summary>
    /// The picked one between two arrows, <c>◀ High ▶</c>, with a pip for each choice under it, as in a game's
    /// settings. The arrows and left and right go round from the last to the first. It fills the width it's offered.
    /// </summary>
    Arrows,
}

/// <summary>
/// One choice from several, such as a difficulty or a graphics preset. It shows <paramref name="Selection"/>
/// as picked, and a click on another calls <paramref name="OnChange"/> with that one's value on the next
/// tick, which the game keeps and passes back. <paramref name="Label"/> names it for tests; it isn't drawn,
/// so put a heading beside it as the screen needs.
/// </summary>
public sealed record Picker<T>(string Label, T Selection, IReadOnlyList<PickerOption<T>> Options, Action<T>? OnChange = null) : UiView
{
    public PickerStyle Style { get; init; }

    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var open = UseState(false);
            bool off = Disabled || environment.Disabled;
            var picked = Options.FirstOrDefault(IsPicked);
            UiView choices = Style switch
            {
                PickerStyle.Radio => Radio(environment, off),
                PickerStyle.Menu => Dropdown(open, picked, off),
                PickerStyle.Arrows => Arrows(environment, picked, off),
                _ => Segmented(environment, off),
            };
            var meaning = new SemanticsModifier(choices, new UiSemantics(UiRole.Picker, Label, picked?.Label) { Disabled = off });

            // One stop for the whole picker: left and right pick the one beside, and Enter the next, round to
            // the first, or opens the menu. Arrows go round with left and right too, as their arrows do.
            Action activate = Style == PickerStyle.Menu ? () => open.Value = !open.Value : () => PickBeside(1, true);
            bool round = Style == PickerStyle.Arrows;
            var keys = new FocusKeys { Adjust = by => PickBeside(by, round), Activate = activate, Disabled = off };
            return new FocusModifier(meaning, keys);
        }
    }

    /// <summary>A button with the picked one's label, which opens a menu of every choice.</summary>
    private UiView Dropdown(State<bool> open, PickerOption<T>? picked, bool off)
    {
        var change = OnChange;
        var items = Options.Select(option =>
        {
            var value = option.Value;
            return new MenuItem(option.Label, change is null ? null : () => change(value)) { Checked = IsPicked(option) };
        }).ToArray();
        var trigger = new MenuTrigger(picked?.Label ?? Label, null, () => open.Value = !open.Value, off);
        return new PopupModifier(trigger, open.Value && !off ? MenuPanel.Layer(items, () => open.Value = false) : null);
    }

    private void PickBeside(int by, bool round)
    {
        if (Options.Count == 0)
        {
            return;
        }

        int at = Options.ToList().FindIndex(IsPicked);
        int next = round ? (at + by + Options.Count) % Options.Count : Math.Clamp(at + by, 0, Options.Count - 1);
        if (next != at)
        {
            OnChange?.Invoke(Options[next].Value);
        }
    }

    private bool IsPicked(PickerOption<T> option) => EqualityComparer<T>.Default.Equals(option.Value, Selection);

    private UiView Segmented(UiEnvironment environment, bool off)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        const float Inset = 3f;
        var bar = new HStack(Spacing: 0f);
        foreach (var option in Options)
        {
            bool picked = IsPicked(option);
            bar.Add(Choice(option, off, look => new Text(option.Label)
                .FontSize(metrics.FontSize)
                .Weight(FontWeight.Medium)
                .Foreground(picked || look.Hovered ? theme.Colors.Foreground : theme.Colors.MutedForeground)
                .LineLimit(1)
                .Padding(MathF.Round(metrics.Padding * 0.75f), 0f)
                .Frame(maxWidth: UiExtensions.Infinity, minHeight: metrics.Height - 2f * Inset)
                .Background(picked ? theme.Colors.Background : default)
                .CornerRadius(theme.Radius - 2f)
                .Shadow(picked ? theme.Shadows.Small : default)));
        }

        return bar.Padding(Inset).Background(theme.Colors.Muted).CornerRadius(theme.Radius).Opacity(off ? 0.5f : 1f);
    }

    private UiView Radio(UiEnvironment environment, bool off)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        float side = MathF.Round(metrics.FontSize * 8f / 7f);
        var list = new VStack(HorizontalAlignment.Leading, MathF.Round(metrics.Gap * 1.5f));
        foreach (var option in Options)
        {
            bool picked = IsPicked(option);
            list.Add(Choice(option, off, look => new HStack(Spacing: metrics.Gap)
            {
                new ZStack
                {
                    new Circle().Stroke(picked ? theme.Colors.Primary : theme.Colors.Input),
                    picked ? new Circle().Fill(theme.Colors.Primary).Frame(side / 2f, side / 2f) : null,
                }.Frame(side, side),
                new Text(option.Label).FontSize(metrics.FontSize),
            }));
        }

        return list.Opacity(off ? 0.5f : 1f);
    }

    /// <summary>The picked one's label between an arrow each way, and a pip for each choice under it.</summary>
    private UiView Arrows(UiEnvironment environment, PickerOption<T>? picked, bool off)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        var pips = new HStack(Spacing: MathF.Round(metrics.Gap * 0.5f));
        foreach (var option in Options)
        {
            var color = IsPicked(option) ? theme.Colors.Primary : theme.Colors.Muted;
            pips.Add(new Capsule().Fill(color).Frame(MathF.Round(metrics.FontSize), 3f));
        }

        var middle = new VStack(HorizontalAlignment.Center, MathF.Round(metrics.Gap * 0.5f))
        {
            new Text(picked?.Label ?? "").FontSize(metrics.FontSize).Weight(FontWeight.Medium).LineLimit(1),
            pips,
        };
        return new HStack(Spacing: metrics.Gap)
        {
            Arrow(IconName.ChevronLeft, -1, environment, off),
            middle.Frame(maxWidth: UiExtensions.Infinity),
            Arrow(IconName.ChevronRight, 1, environment, off),
        }.Frame(minHeight: metrics.Height).Opacity(off ? 0.5f : 1f);
    }

    /// <summary>An arrow that picks the one beside, going round at the ends.</summary>
    private UiView Arrow(IconName icon, int by, UiEnvironment environment, bool off)
    {
        var theme = environment.Theme;
        float side = environment.Metrics.Height;
        var pressable = new Pressable(look => new Icon(icon)
            .FontSize(environment.Metrics.FontSize * 1.25f)
            .Foreground(look.Hovered ? theme.Colors.Foreground : theme.Colors.MutedForeground)
            .Frame(side, side)
            .Background(look.Pressed ? theme.Colors.Muted : default)
            .CornerRadius(theme.Radius), Options.Count == 0 ? null : () => PickBeside(by, true), off);
        return new SemanticsModifier(pressable, new UiSemantics(UiRole.Button, by < 0 ? "Previous" : "Next") { Disabled = off });
    }

    /// <summary>One choice, which picks its value when clicked and says whether it's the one picked.</summary>
    private UiView Choice(PickerOption<T> option, bool off, Func<PressState, UiView> look)
    {
        var change = OnChange;
        var value = option.Value;
        var pressable = new Pressable(look, change is null ? null : () => change(value), off);
        return new SemanticsModifier(pressable, new UiSemantics(UiRole.Option, option.Label, Checked: IsPicked(option)) { Disabled = off });
    }
}
