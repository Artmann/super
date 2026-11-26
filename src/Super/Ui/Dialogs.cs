namespace Super.Ui;

/// <summary>What a <see cref="DialogButton"/> does, which sets its look and what Escape presses.</summary>
public enum DialogRole
{
    /// <summary>An ordinary answer. The last one has focus when the dialog opens, so Enter picks it.</summary>
    Default,

    /// <summary>Backs out. Escape, the gamepad's East button and a click outside press it.</summary>
    Cancel,

    /// <summary>Destroys something, in the theme's destructive colour.</summary>
    Destructive,
}

/// <summary>One answer in a <see cref="Dialog"/>, and what it does.</summary>
public sealed record DialogButton(string Label, Action? Action = null, DialogRole Role = DialogRole.Default);

/// <summary>
/// What an alert or a confirmation dialog says: a title, a message under it, and the buttons that answer it.
/// Show one with <see cref="PopupExtensions.Alert"/> or <see cref="PopupExtensions.ConfirmationDialog"/>.
/// </summary>
public sealed record Dialog(string Title, string? Message = null)
{
    /// <summary>The answers, in order. With none, it has an OK that only closes it.</summary>
    public IReadOnlyList<DialogButton> Buttons { get; init; } = [];

    internal IReadOnlyList<DialogButton> Shown => Buttons.Count == 0 ? [new DialogButton("OK")] : Buttons;

    /// <summary>What backing out does: the cancel button, or the only button, and then closing. Null when neither is there.</summary>
    internal Action? Cancel(Action dismiss)
    {
        var shown = Shown;
        var cancel = shown.FirstOrDefault(button => button.Role == DialogRole.Cancel) ?? (shown.Count == 1 ? shown[0] : null);
        return cancel is null ? null : Press(cancel, dismiss);
    }

    internal static Action Press(DialogButton button, Action dismiss) => () =>
    {
        button.Action?.Invoke();
        dismiss();
    };
}

/// <summary>
/// The card of an alert, with its buttons in a row at the trailing edge, or of a confirmation dialog, with
/// them one under the other and cancel at the bottom.
/// </summary>
internal sealed record DialogCard(Dialog Dialog, Action Dismiss, bool Stacked) : UiView
{
    public override UiView Body
    {
        get
        {
            var buttons = Dialog.Shown;
            var main = buttons.LastOrDefault(button => button.Role == DialogRole.Default) ?? buttons.FirstOrDefault(button => button.Role == DialogRole.Cancel) ?? buttons[0];
            var card = Stacked ? Column(buttons, main) : Row(buttons, main);
            var meaning = new SemanticsModifier(card, new UiSemantics(UiRole.Dialog, Dialog.Title, Dialog.Message));
            return new PopupCard(meaning, Stacked ? 16f : 24f, MinWidth: 320f, MaxWidth: 420f);
        }
    }

    private UiView Row(IReadOnlyList<DialogButton> buttons, DialogButton main)
    {
        var row = new HStack(Spacing: 8f) { new Spacer() };
        foreach (var button in buttons.Where(button => button.Role == DialogRole.Cancel).Concat(buttons.Where(button => button.Role != DialogRole.Cancel)))
        {
            row.Add(Answer(button, main, fullWidth: false));
        }

        return new VStack(HorizontalAlignment.Leading, 8f)
        {
            new Text(Dialog.Title).TextStyle(TextStyle.Title3),
            Dialog.Message is { } message ? new Text(message).Foreground(Tone.Muted) : null,
            row.Padding(16f, 0f, 0f, 0f),
        };
    }

    private UiView Column(IReadOnlyList<DialogButton> buttons, DialogButton main)
    {
        var column = new VStack(Spacing: 8f)
        {
            new Text(Dialog.Title).TextStyle(TextStyle.Headline).TextAlign(TextAlignment.Center),
            Dialog.Message is { } message ? new Text(message).Foreground(Tone.Muted).TextAlign(TextAlignment.Center) : null,
        };
        foreach (var button in buttons.Where(button => button.Role != DialogRole.Cancel))
        {
            column.Add(Answer(button, main, fullWidth: true));
        }

        if (buttons.FirstOrDefault(button => button.Role == DialogRole.Cancel) is { } cancel)
        {
            column.Add(new Divider());
            column.Add(Answer(cancel, main, fullWidth: true));
        }

        return column;
    }

    private UiView Answer(DialogButton button, DialogButton main, bool fullWidth)
    {
        var style = button.Role switch
        {
            DialogRole.Cancel => ButtonStyle.Outline,
            DialogRole.Destructive => ButtonStyle.Destructive,
            _ => Stacked ? ButtonStyle.Secondary : ButtonStyle.Default,
        };
        UiView shown = new Button(button.Label, Dialog.Press(button, Dismiss)) { Style = style, FullWidth = fullWidth };
        return ReferenceEquals(button, main) ? shown.DefaultFocus() : shown;
    }
}
