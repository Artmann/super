namespace Super.Ui;

/// <summary>
/// A line of text the player types into, such as a name or a chat message. It shows <paramref name="Text"/>,
/// or <paramref name="Prompt"/> in a muted colour while that's empty, and calls <paramref name="OnChange"/>
/// with the new text on the next tick after each edit, which the game keeps and passes back. A click or Tab
/// focuses it; then it takes typing, the arrows with Shift and Ctrl, Home and End, Backspace and Delete,
/// Ctrl+A, C, X and V, and Enter, which calls <see cref="OnSubmit"/>. Escape lets go. It fills the width it's
/// offered, and the prompt names it for tests.
/// </summary>
public sealed record TextField(string Prompt, string Text, Action<string>? OnChange = null, bool Disabled = false) : UiView
{
    /// <summary>What Enter does, with the text as it is.</summary>
    public Action<string>? OnSubmit { get; init; }

    /// <summary>Whether it shows dots instead of the text and won't copy it, for a <see cref="SecureField"/>.</summary>
    internal bool Secure { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var theme = environment.Theme;
            var metrics = environment.Metrics;
            bool off = Disabled || environment.Disabled;
            var focused = UseState(false);
            var input = new UiTextInput(UseState(default(TextFieldMemory)), Text, OnChange, OnSubmit, Secure);
            var edit = input.Edit;
            string shown = Secure ? new string('•', edit.Text.Length) : edit.Text;
            var box = new TextInputView(shown, Prompt, edit.Caret, edit.Anchor, focused.Value && !off)
                .FontSize(metrics.FontSize)
                .Padding(MathF.Round(metrics.Padding * 0.75f), 0f)
                .Frame(maxWidth: UiExtensions.Infinity, minHeight: metrics.Height, alignment: Alignment.Leading)
                .Background(theme.Colors.Background)
                .Border(1f, theme.Colors.Input)
                .CornerRadius(theme.Radius)
                .Opacity(off ? 0.5f : 1f);
            var meaning = new SemanticsModifier(box, new UiSemantics(UiRole.TextField, Prompt, shown) { Disabled = off });
            return new FocusModifier(meaning, new FocusKeys { Text = input, Focused = focused, Disabled = off });
        }
    }
}

/// <summary>
/// A <see cref="TextField"/> for a password: it shows a dot for each character, and won't copy or cut. Tests
/// read the dots as its value, so check what the game kept instead.
/// </summary>
public sealed record SecureField(string Prompt, string Text, Action<string>? OnChange = null, bool Disabled = false) : UiView
{
    /// <summary>What Enter does, with the text as it is.</summary>
    public Action<string>? OnSubmit { get; init; }

    public override UiView Body => new TextField(Prompt, Text, OnChange, Disabled) { OnSubmit = OnSubmit, Secure = true };
}

/// <summary>
/// What a text field remembers between descriptions: the caret and selection, and an edit the game hasn't seen yet.
/// <see cref="Sent"/> counts the edits handed to the game, so a field described before the last one knows its text is old.
/// </summary>
internal readonly record struct TextFieldMemory(string? Draft, bool Delivered, int Sent, int Caret, int Anchor);

/// <summary>
/// A text field's editing, as focus drives it. Edits build on each other within a tick, before the game has
/// passed any of them back, and each reaches the game on the next tick.
/// </summary>
internal sealed class UiTextInput(State<TextFieldMemory> memory, string text, Action<string>? change, Action<string>? submit, bool secure)
{
    private readonly int _born = memory.Value.Sent;

    public bool Secure => secure;

    /// <summary>
    /// The text as the player last left it: an edit still on its way to the game, or one the game was handed after
    /// this field was described, or else what the game passed in.
    /// </summary>
    public UiTextEdit Edit
    {
        get
        {
            var held = memory.Value;
            bool draft = held.Draft is not null && (!held.Delivered || held.Sent > _born);
            return new UiTextEdit(text, held.Caret, held.Anchor).On(draft ? held.Draft! : text);
        }
    }

    /// <summary>Makes this the edit, and owes the game the new text if it changed.</summary>
    public void Apply(UiTextEdit next, List<Action> callbacks)
    {
        var held = memory.Value;
        if (next.Text == Edit.Text)
        {
            memory.Value = held with { Caret = next.Caret, Anchor = next.Anchor };
            return;
        }

        memory.Value = held with { Draft = next.Text, Delivered = false, Caret = next.Caret, Anchor = next.Anchor };
        if (change is { } owed)
        {
            string sent = next.Text;
            callbacks.Add(() =>
            {
                // Only the last edit of a tick goes, carrying the ones before it.
                if (memory.Value is { Delivered: false } now && now.Draft == sent)
                {
                    owed(sent);
                    memory.Value = now with { Delivered = true, Sent = now.Sent + 1 };
                }
            });
        }
    }

    public void Submit(List<Action> callbacks)
    {
        if (submit is { } owed)
        {
            string done = Edit.Text;
            callbacks.Add(() => owed(done));
        }
    }
}

/// <summary>
/// The text in a text field, with the selection behind it and the caret, clipped to its frame and scrolled
/// so the caret stays in view. It's as tall as a line.
/// </summary>
internal sealed record TextInputView(string Shown, string Prompt, int Caret, int Anchor, bool Focused) : PrimitiveView
{
    private const float CaretWidth = 1.5f;

    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float line = node.Tree.Atlas.Measure("Ag", node.Environment.Text, null).Height;
        return new UiSize(proposal.Width ?? Width(node, Shown.Length > 0 ? Shown : Prompt) + CaretWidth, line);
    }

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        var frame = node.Frame;
        var style = node.Environment.Text;
        var colors = node.Environment.Theme.Colors;
        float offset = Scrolled(node);
        draw.Nesting.PushClip(frame, 0f);
        if (Focused && Caret != Anchor)
        {
            float from = Width(node, Shown[..Math.Min(Caret, Anchor)]) - offset;
            float to = Width(node, Shown[..Math.Max(Caret, Anchor)]) - offset;
            draw.AddBox(new UiRect(frame.X + from, frame.Y, to - from, frame.Height), colors.Primary with { A = 0.35f }, 0f);
        }

        string content = Shown.Length > 0 ? Shown : Prompt;
        var look = Shown.Length > 0 ? style : style with { Color = colors.MutedForeground };
        var at = new UiRect(frame.X - offset, frame.Y, Width(node, content) + CaretWidth, frame.Height);
        draw.AddText(content, at, look);
        node.Tree.Atlas.Draw(draw, content, look, at);
        if (Focused)
        {
            float x = frame.X + Width(node, Shown[..Caret]) - offset;
            draw.AddBox(new UiRect(x, frame.Y, CaretWidth, frame.Height), style.Color, 0f);
        }

        draw.Nesting.PopClip();
    }

    /// <summary>Where in the text a point this far across the frame falls, at the nearest gap between characters.</summary>
    public int IndexAt(UiNode node, float x)
    {
        float across = x - node.Frame.X + node.Scroll.Offset;
        float before = 0f;
        for (int i = 1; i <= Shown.Length; i++)
        {
            float width = Width(node, Shown[..i]);
            if (across < (before + width) / 2f)
            {
                return i - 1;
            }

            before = width;
        }

        return Shown.Length;
    }

    private static float Width(UiNode node, string text) =>
        text.Length == 0 ? 0f : node.Tree.Atlas.Measure(text, node.Environment.Text with { LineLimit = 1 }, null).Width;

    /// <summary>How far the text is scrolled: no more than it takes to keep the caret in view, and kept from the last frame.</summary>
    private float Scrolled(UiNode node)
    {
        float caret = Width(node, Shown[..Caret]);
        float room = Math.Max(node.Frame.Width - CaretWidth, 0f);
        float whole = Width(node, Shown);
        float offset = Math.Clamp(node.Scroll.Offset, caret - room, caret);
        offset = Math.Clamp(offset, 0f, Math.Max(whole - room, 0f));
        node.Scroll.Offset = offset;
        return offset;
    }
}
