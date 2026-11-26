namespace Super.Ui;

/// <summary>
/// Text being edited: the text, where the caret is, and where the selection started. With no
/// selection the anchor is at the caret. Every edit gives a new one, so editing is a function a
/// test can check without a screen. Positions count UTF-16 characters, and never land inside a pair.
/// </summary>
internal readonly record struct UiTextEdit(string Text, int Caret, int Anchor)
{
    public UiTextEdit(string text)
        : this(text, text.Length, text.Length)
    {
    }

    public int SelectionStart => Math.Min(Caret, Anchor);

    public int SelectionEnd => Math.Max(Caret, Anchor);

    public bool HasSelection => Caret != Anchor;

    public string Selected => Text[SelectionStart..SelectionEnd];

    /// <summary>The same edit on text the game may have changed, with the caret and selection kept inside it.</summary>
    public UiTextEdit On(string text) => new(text, Math.Clamp(Caret, 0, text.Length), Math.Clamp(Anchor, 0, text.Length));

    /// <summary>Puts this text where the selection is, or at the caret, and the caret after it.</summary>
    public UiTextEdit Insert(string text)
    {
        string next = string.Concat(Text.AsSpan(0, SelectionStart), text, Text.AsSpan(SelectionEnd));
        int caret = SelectionStart + text.Length;
        return new UiTextEdit(next, caret, caret);
    }

    /// <summary>Takes out the selection, or what's before the caret: a character, or a word with <paramref name="word"/>.</summary>
    public UiTextEdit Backspace(bool word) => HasSelection ? Insert("") : Cut(word ? WordBefore(Caret) : CharacterBefore(Caret), Caret);

    /// <summary>Takes out the selection, or what's after the caret: a character, or a word with <paramref name="word"/>.</summary>
    public UiTextEdit Delete(bool word) => HasSelection ? Insert("") : Cut(Caret, word ? WordAfter(Caret) : CharacterAfter(Caret));

    /// <summary>
    /// Moves the caret one character or word towards <paramref name="direction"/>, -1 or 1. Without
    /// <paramref name="extend"/> a selection collapses to its end on that side instead.
    /// </summary>
    public UiTextEdit Move(int direction, bool word, bool extend)
    {
        if (HasSelection && !extend)
        {
            int end = direction < 0 ? SelectionStart : SelectionEnd;
            return new UiTextEdit(Text, end, end);
        }

        int caret = (direction < 0, word) switch
        {
            (true, true) => WordBefore(Caret),
            (true, false) => CharacterBefore(Caret),
            (false, true) => WordAfter(Caret),
            _ => CharacterAfter(Caret),
        };
        return MoveTo(caret, extend);
    }

    /// <summary>Puts the caret here, keeping the anchor where it was with <paramref name="extend"/>.</summary>
    public UiTextEdit MoveTo(int caret, bool extend)
    {
        caret = Math.Clamp(caret, 0, Text.Length);
        if (caret > 0 && caret < Text.Length && char.IsLowSurrogate(Text[caret]))
        {
            caret--;
        }

        return new UiTextEdit(Text, caret, extend ? Anchor : caret);
    }

    public UiTextEdit SelectAll() => new(Text, Text.Length, 0);

    private UiTextEdit Cut(int from, int to) => new(Text.Remove(from, to - from), from, from);

    private int CharacterBefore(int at) => at >= 2 && char.IsLowSurrogate(Text[at - 1]) && char.IsHighSurrogate(Text[at - 2]) ? at - 2 : Math.Max(at - 1, 0);

    private int CharacterAfter(int at) => at + 1 < Text.Length && char.IsHighSurrogate(Text[at]) && char.IsLowSurrogate(Text[at + 1]) ? at + 2 : Math.Min(at + 1, Text.Length);

    /// <summary>The start of the word before, past any spaces first, as Ctrl+Left goes.</summary>
    private int WordBefore(int at)
    {
        while (at > 0 && char.IsWhiteSpace(Text[at - 1]))
        {
            at--;
        }

        while (at > 0 && !char.IsWhiteSpace(Text[at - 1]))
        {
            at--;
        }

        return at;
    }

    /// <summary>The end of the word after, past any spaces first, as Ctrl+Right goes.</summary>
    private int WordAfter(int at)
    {
        while (at < Text.Length && char.IsWhiteSpace(Text[at]))
        {
            at++;
        }

        while (at < Text.Length && !char.IsWhiteSpace(Text[at]))
        {
            at++;
        }

        return at;
    }
}
