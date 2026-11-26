namespace Super.Ui;

/// <summary>
/// What the editing keys do in a focused text field. Ctrl, or Command on a Mac, moves by words and reaches the
/// clipboard, and Shift stretches the selection.
/// </summary>
internal static class UiTextKeys
{
    /// <summary>The keys that do the same in a text field as anywhere: Tab, up and down move on, Enter submits, Escape lets go.</summary>
    public static bool LeavesField(Key key) => key is Key.Tab or Key.Up or Key.Down or Key.Enter or Key.KeyPadEnter or Key.Escape;

    /// <summary>
    /// Edits the text for this key. A text field takes every key but the modifiers and the function keys, so
    /// typing a name doesn't also walk the player around. Returns whether it took the key.
    /// </summary>
    public static bool Press(Key key, UiTextInput text, KeyFeed keys, List<Action> callbacks)
    {
        if (key is >= Key.LeftShift and <= Key.RightSuper or >= Key.F1 and <= Key.F25)
        {
            return false;
        }

        var edit = text.Edit;
        bool command = keys.Command;
        var next = Moved(key, edit, command, keys.Shift) ?? Clipped(key, edit, command, text.Secure, keys.Clipboard);
        if (next is { } changed)
        {
            text.Apply(changed, callbacks);
        }

        return true;
    }

    private static UiTextEdit? Moved(Key key, UiTextEdit edit, bool word, bool extend) => key switch
    {
        Key.Left => edit.Move(-1, word, extend),
        Key.Right => edit.Move(1, word, extend),
        Key.Home => edit.MoveTo(0, extend),
        Key.End => edit.MoveTo(edit.Text.Length, extend),
        Key.Backspace => edit.Backspace(word),
        Key.Delete => edit.Delete(word),
        _ => null,
    };

    /// <summary>Select all and the clipboard. A secure field won't give its text away.</summary>
    private static UiTextEdit? Clipped(Key key, UiTextEdit edit, bool command, bool secure, GameClipboard clipboard)
    {
        if (!command)
        {
            return null;
        }

        bool gives = !secure && edit.HasSelection;
        switch (key)
        {
            case Key.A:
                return edit.SelectAll();
            case Key.C when gives:
                clipboard.Text = edit.Selected;
                return null;
            case Key.X when gives:
                clipboard.Text = edit.Selected;
                return edit.Insert("");
            case Key.V:
                return edit.Insert(OneLine(clipboard.Text));
            default:
                return null;
        }
    }

    /// <summary>A field holds one line, so pasted line breaks become spaces and other control characters go.</summary>
    private static string OneLine(string text) =>
        string.Concat(text.ReplaceLineEndings(" ").Where(character => !char.IsControl(character)));
}
