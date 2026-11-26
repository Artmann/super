using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The editing a text field does, as plain values with no screen.</summary>
public sealed class UiTextEditTests
{
    [Fact]
    public void A_new_edit_has_the_caret_at_the_end_and_nothing_selected()
    {
        var edit = new UiTextEdit("Ada");

        Assert.Equal(3, edit.Caret);
        Assert.False(edit.HasSelection);
    }

    [Fact]
    public void Insert_replaces_the_selection_and_puts_the_caret_after()
    {
        var edit = new UiTextEdit("Hello world", 6, 11);

        var next = edit.Insert("there");

        Assert.Equal(new UiTextEdit("Hello there", 11, 11), next);
    }

    [Fact]
    public void A_selection_made_backwards_reads_the_same()
    {
        var edit = new UiTextEdit("Hello world", 5, 0);

        Assert.Equal("Hello", edit.Selected);
        Assert.Equal(new UiTextEdit(" world", 0, 0), edit.Backspace(false));
    }

    [Theory]
    [InlineData(11, 6)]
    [InlineData(6, 0)]
    [InlineData(8, 6)]
    [InlineData(0, 0)]
    public void Ctrl_Left_goes_to_the_start_of_the_word_before(int from, int to)
    {
        var edit = new UiTextEdit("Hello world", from, from);

        Assert.Equal(to, edit.Move(-1, word: true, extend: false).Caret);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 11)]
    [InlineData(7, 11)]
    [InlineData(11, 11)]
    public void Ctrl_Right_goes_to_the_end_of_the_word_after(int from, int to)
    {
        var edit = new UiTextEdit("Hello world", from, from);

        Assert.Equal(to, edit.Move(1, word: true, extend: false).Caret);
    }

    [Fact]
    public void An_arrow_without_Shift_collapses_a_selection_to_its_side()
    {
        var edit = new UiTextEdit("Hello world", 8, 2);

        Assert.Equal(new UiTextEdit("Hello world", 2, 2), edit.Move(-1, word: false, extend: false));
        Assert.Equal(new UiTextEdit("Hello world", 8, 8), edit.Move(1, word: false, extend: false));
    }

    [Fact]
    public void Shift_keeps_the_anchor_while_the_caret_moves()
    {
        var edit = new UiTextEdit("Hello", 3, 3);

        var next = edit.Move(-1, word: false, extend: true).Move(-1, word: false, extend: true);

        Assert.Equal(new UiTextEdit("Hello", 1, 3), next);
        Assert.Equal("el", next.Selected);
    }

    [Fact]
    public void Ctrl_Backspace_takes_the_word_and_the_spaces_before_the_caret()
    {
        var edit = new UiTextEdit("one two  ", 9, 9);

        Assert.Equal(new UiTextEdit("one ", 4, 4), edit.Backspace(word: true));
    }

    [Fact]
    public void Ctrl_Delete_takes_the_spaces_and_the_word_after_the_caret()
    {
        var edit = new UiTextEdit("one  two three", 3, 3);

        Assert.Equal(new UiTextEdit("one three", 3, 3), edit.Delete(word: true));
    }

    [Fact]
    public void Backspace_and_delete_at_the_ends_change_nothing()
    {
        var edit = new UiTextEdit("Ada");

        Assert.Equal(edit, edit.Delete(word: false));
        Assert.Equal(new UiTextEdit("Ada", 0, 0).Backspace(word: false), new UiTextEdit("Ada", 0, 0));
    }

    [Fact]
    public void An_emoji_is_one_character_to_the_arrows_backspace_and_delete()
    {
        // "a😀b": the emoji is a surrogate pair at 1 and 2.
        var edit = new UiTextEdit("a\U0001F600b", 3, 3);

        Assert.Equal(1, edit.Move(-1, word: false, extend: false).Caret);
        Assert.Equal(3, new UiTextEdit("a\U0001F600b", 1, 1).Move(1, word: false, extend: false).Caret);
        Assert.Equal("ab", edit.Backspace(word: false).Text);
        Assert.Equal("ab", new UiTextEdit("a\U0001F600b", 1, 1).Delete(word: false).Text);
    }

    [Fact]
    public void A_caret_put_inside_an_emoji_moves_to_its_start()
    {
        var edit = new UiTextEdit("a\U0001F600b");

        Assert.Equal(1, edit.MoveTo(2, extend: false).Caret);
    }

    [Fact]
    public void Select_all_puts_the_caret_at_the_end()
    {
        var edit = new UiTextEdit("Ada", 1, 1).SelectAll();

        Assert.Equal(new UiTextEdit("Ada", 3, 0), edit);
        Assert.Equal("Ada", edit.Selected);
    }

    [Fact]
    public void The_same_edit_on_shorter_text_keeps_the_caret_inside_it()
    {
        var edit = new UiTextEdit("Ada Lovelace", 12, 4);

        Assert.Equal(new UiTextEdit("Ada", 3, 3), edit.On("Ada"));
    }
}
