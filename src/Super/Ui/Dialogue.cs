namespace Super.Ui;

/// <summary>An answer the player can give in a <see cref="DialogueBox"/>, and what picking it does.</summary>
public sealed record DialogueChoice(string Text, Action Pick);

/// <summary>
/// A line of dialogue: who says it, an optional portrait, and the text, which types itself out. The first press of
/// Enter, Space, the gamepad's South button or a click shows the rest of the line at once, and the next runs
/// <see cref="OnNext"/>. Once the line is out, its <see cref="Choices"/> show under it instead, and picking one runs
/// its action. Give a new <paramref name="Line"/> and it types out from the start. Inside <c>.Animations(false)</c>,
/// or with <see cref="CharactersPerSecond"/> at 0, each line shows whole.
/// </summary>
public sealed record DialogueBox(string Speaker, string Line) : UiView
{
    /// <summary>A picture of who's speaking, beside the text, such as an <see cref="Image"/>.</summary>
    public UiView? Portrait { get; init; }

    /// <summary>The answers, which show once the line is out. With none, a press moves on.</summary>
    public IReadOnlyList<DialogueChoice> Choices { get; init; } = [];

    /// <summary>What the press after the line is out does, such as showing the next line.</summary>
    public Action? OnNext { get; init; }

    /// <summary>
    /// What skipping the whole conversation does. When it's set, a button over the box skips when held, so a press
    /// meant for the line doesn't skip by accident.
    /// </summary>
    public Action? OnSkip { get; init; }

    /// <summary>How fast the line types itself out. 0 shows it whole.</summary>
    public float CharactersPerSecond { get; init; } = 45f;

    /// <summary>The colour of the speaker's name, the theme's primary when it isn't set.</summary>
    public Color? SpeakerColor { get; init; }

    // The widest a box goes, and the side of a portrait.
    private const float MaxWidth = 760f;
    private const float PortraitSide = 96f;

    // True for the box itself under the skip button, keyed by the line so a new line starts typing again from nothing.
    private bool IsCard { get; init; }

    public override UiView Body => IsCard ? Card() : new VStack(HorizontalAlignment.Trailing, 8f)
    {
        OnSkip is { } skip ? new HoldButton("Hold to skip", skip, 0.8f) : null,
        (this with { IsCard = true }).Id(Line),
    };

    /// <summary>The box, with the line as far as it has typed, and a press that shows the rest or moves on.</summary>
    private UiView Card()
    {
        var skipped = UseState(false);
        float seconds = CharactersPerSecond > 0f ? Line.Length / CharactersPerSecond : 0f;
        float typing = UseAnimation(1f, seconds, Easing.Linear, from: 0f);
        int shown = skipped.Value ? Line.Length : (int)MathF.Round(typing * Line.Length);
        bool typed = shown >= Line.Length;
        if (typed && Choices.Count > 0)
        {
            return Panel(shown, Answers(Choices), null);
        }

        var onNext = OnNext;
        void Advance()
        {
            if (!typed)
            {
                skipped.Value = true;
            }
            else
            {
                onNext?.Invoke();
            }
        }

        return Panel(shown, null, typed ? new NextMark().Padding(10f) : null).Focusable(Advance).DefaultFocus().OnClick(Advance);
    }

    /// <summary>The panel: the portrait, the speaker, the typed line, the answers under it, and a mark in the corner.</summary>
    private UiView Panel(int shown, UiView? answers, UiView? corner)
    {
        var theme = Environment.Theme;
        var words = new VStack(HorizontalAlignment.Leading, 6f)
        {
            new Text(Speaker).TextStyle(TextStyle.Headline).Foreground(SpeakerColor ?? theme.Colors.Primary),
            new TypedText(Line, shown),
            answers,
        };
        var row = new HStack(VerticalAlignment.Top, 16f)
        {
            Portrait?.Frame(PortraitSide, PortraitSide).CornerRadius(theme.Radius),
            words,
            new Spacer(),
        };
        var inside = new ZStack(Alignment.BottomTrailing) { row.Padding(20f, 16f), corner };
        return ThemePanel.On(inside.Frame(maxWidth: MaxWidth), theme.Panels.Popup, theme.Colors.Raised, box => box
            .Background(theme.Colors.Raised)
            .Border(1f, theme.Colors.Border)
            .CornerRadius(theme.RadiusLarge)
            .Shadow(theme.Shadows.Medium));
    }

    private static UiView Answers(IReadOnlyList<DialogueChoice> choices)
    {
        var list = new VStack(HorizontalAlignment.Leading, 4f);
        for (int i = 0; i < choices.Count; i++)
        {
            var button = new Button(choices[i].Text, choices[i].Pick) { Style = ButtonStyle.Menu, FullWidth = true };
            list.Add(i == 0 ? button.DefaultFocus() : button);
        }

        return list.FocusWraps().Padding(6f, 0f, 0f, 0f);
    }
}

/// <summary>
/// A paragraph laid out whole, with only its first <paramref name="Shown"/> characters drawn, so words don't jump to
/// the next line as they type out. Only what's drawn counts as shown.
/// </summary>
internal sealed record TypedText(string Content, int Shown) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        node.Tree.Atlas.Measure(Content, node.Environment.Text, proposal.Width);

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        int shown = Math.Clamp(Shown, 0, Content.Length);
        draw.AddText(Content[..shown], node.Frame, node.Environment.Text);
        node.Tree.Atlas.Draw(draw, Content, node.Environment.Text, node.Frame, shown);
    }
}

/// <summary>The arrow in a dialogue box's corner that says a press moves on, bobbing gently, and still inside <c>.Animations(false)</c>.</summary>
internal sealed record NextMark : UiView
{
    public override UiView Body
    {
        get
        {
            double time = UseTime();
            float bob = MathF.Sin((float)(time * Math.Tau * 1.2)) * 2f;
            return new Icon(IconName.ChevronDown).Foreground(Tone.Muted).Offset(0f, bob);
        }
    }
}
