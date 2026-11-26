using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Typing into text and secure fields, and what the game sees meanwhile.</summary>
public sealed class TextFieldTests
{
    private sealed class Form
    {
        public string Name = "";
        public string Password = "";
        public readonly List<string> Changes = [];
        public readonly List<string> Submitted = [];
        public bool Off;
    }

    private static HeadlessRun<PointerGame> Run(Form form, bool secure = false)
    {
        var run = HeadlessGame.Create<PointerGame>(1);
        run.Game.Screen = _ => new VStack
        {
            secure
                ? new SecureField("Password", form.Password, text => form.Password = text).Id("password")
                : new TextField("Name", form.Name, text => { form.Name = text; form.Changes.Add(text); }, form.Off)
                {
                    OnSubmit = text => form.Submitted.Add(text),
                }.Id("name"),
            new Button("Next", () => { }).Id("next"),
        }.Frame(width: 300f).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);
        run.Ticks(1);
        return run;
    }

    private static void Press(HeadlessRun<PointerGame> run, Key key)
    {
        run.Input.Press(key);
        run.Ticks(2);
    }

    private static void Type(HeadlessRun<PointerGame> run, string text)
    {
        run.Input.Type(text);
        run.Ticks(2);
    }

    /// <summary>Holds Ctrl, or Command on a Mac, for one key.</summary>
    private static void Command(HeadlessRun<PointerGame> run, Key key)
    {
        var command = OperatingSystem.IsMacOS() ? Key.LeftSuper : Key.LeftControl;
        run.Input.Hold(command);
        run.Input.Press(key);
        run.Input.Release(command);
        run.Ticks(2);
    }

    private static void Focus(HeadlessRun<PointerGame> run, object id)
    {
        run.Input.Click(id);
        run.Ticks(2);
    }

    private static GameClipboard Clipboard(HeadlessRun<PointerGame> run) => run.Service<Input>().Keys.Clipboard;

    [Fact]
    public void A_field_describes_itself_by_its_prompt_and_text()
    {
        var form = new Form { Name = "Ada" };
        using var run = Run(form);

        var field = run.Element("name");

        Assert.Equal(UiRole.TextField, field.Role);
        Assert.Equal("Name", field.Label);
        Assert.Equal("Ada", field.Value);
        Assert.False(field.Focused);
    }

    [Fact]
    public void A_click_focuses_a_field_and_typing_reaches_OnChange()
    {
        var form = new Form();
        using var run = Run(form);

        Focus(run, "name");
        Assert.True(run.Element("name").Focused);
        Type(run, "Ada");

        Assert.Equal("Ada", form.Name);
        Assert.Equal("Ada", run.Element("name").Value);
    }

    [Fact]
    public void Letters_typed_inside_one_tick_reach_the_game_as_one_change()
    {
        var form = new Form();
        using var run = Run(form);
        Focus(run, "name");

        Type(run, "Grace");

        Assert.Equal(["Grace"], form.Changes);
    }

    [Fact]
    public void Control_characters_the_window_sends_with_Ctrl_arent_typed()
    {
        var form = new Form();
        using var run = Run(form);
        Focus(run, "name");

        // Windows sends Ctrl+A as U+0001 and Backspace as U+0008, next to their keys.
        Type(run, "A\u0001\bda");

        Assert.Equal("Ada", form.Name);
    }

    [Fact]
    public void Typing_into_a_field_doesnt_also_move_the_player()
    {
        var form = new Form();
        using var run = Run(form);
        Focus(run, "name");
        run.Game.Watch = input => input.Typed.Length > 0 || input.Pressed(Key.W) || input.Down(Key.W) || input.Pressed(Key.Space);

        run.Input.Press(Key.W);
        run.Input.Type("w");
        run.Input.Press(Key.Space);
        run.Input.Type(" ");
        run.Ticks(2);

        Assert.Equal("w ", form.Name);
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void Function_keys_and_modifiers_still_reach_the_game_from_a_field()
    {
        var form = new Form();
        using var run = Run(form);
        Focus(run, "name");
        run.Game.Watch = input => input.Pressed(Key.F5) || input.Pressed(Key.LeftShift);

        Press(run, Key.F5);
        Press(run, Key.LeftShift);

        Assert.Equal(2, run.Game.Reads);
    }

    [Fact]
    public void Tab_moves_from_a_field_to_the_next_control_with_the_text_kept()
    {
        var form = new Form();
        using var run = Run(form);
        Press(run, Key.Tab);
        Assert.True(run.Element("name").Focused);
        Type(run, "Ada");

        Press(run, Key.Tab);

        Assert.True(run.Element("next").Focused);
        Assert.Equal("Ada", form.Name);
    }

    [Fact]
    public void Backspace_takes_a_letter_and_with_Ctrl_a_word()
    {
        var form = new Form { Name = "Ada Lovelace" };
        using var run = Run(form);
        Press(run, Key.Tab);

        Press(run, Key.Backspace);
        Assert.Equal("Ada Lovelac", form.Name);
        Command(run, Key.Backspace);
        Assert.Equal("Ada ", form.Name);
    }

    [Fact]
    public void Delete_takes_the_letter_after_the_caret()
    {
        var form = new Form { Name = "Ada" };
        using var run = Run(form);
        Press(run, Key.Tab);

        Press(run, Key.Home);
        Press(run, Key.Delete);

        Assert.Equal("da", form.Name);
    }

    [Fact]
    public void Typing_over_a_selection_made_with_Shift_replaces_it()
    {
        var form = new Form { Name = "Hello" };
        using var run = Run(form);
        Press(run, Key.Tab);

        run.Input.Hold(Key.LeftShift);
        run.Input.Press(Key.Left);
        run.Input.Press(Key.Left);
        run.Input.Release(Key.LeftShift);
        run.Ticks(2);
        Type(run, "p!");

        Assert.Equal("Help!", form.Name);
    }

    [Fact]
    public void Ctrl_A_and_C_copy_the_whole_text_and_V_pastes_it_at_the_caret()
    {
        var form = new Form { Name = "Ada" };
        using var run = Run(form);
        Press(run, Key.Tab);

        Command(run, Key.A);
        Command(run, Key.C);
        Assert.Equal("Ada", Clipboard(run).Text);
        Press(run, Key.End);
        Command(run, Key.V);

        Assert.Equal("AdaAda", form.Name);
    }

    [Fact]
    public void Ctrl_X_cuts_the_selection_to_the_clipboard()
    {
        var form = new Form { Name = "Ada" };
        using var run = Run(form);
        Press(run, Key.Tab);

        Command(run, Key.A);
        Command(run, Key.X);

        Assert.Equal("Ada", Clipboard(run).Text);
        Assert.Equal("", form.Name);
    }

    [Fact]
    public void A_paste_becomes_one_line()
    {
        var form = new Form();
        using var run = Run(form);
        Press(run, Key.Tab);
        Clipboard(run).Text = "Ada\r\nLovelace\t!";

        Command(run, Key.V);

        Assert.Equal("Ada Lovelace!", form.Name);
    }

    [Fact]
    public void Enter_submits_the_text_and_keeps_focus()
    {
        var form = new Form();
        using var run = Run(form);
        Press(run, Key.Tab);
        Type(run, "Ada");

        Press(run, Key.Enter);

        Assert.Equal(["Ada"], form.Submitted);
        Assert.True(run.Element("name").Focused);
    }

    [Fact]
    public void Escape_lets_go_of_a_field_and_the_game_has_the_keys_back()
    {
        var form = new Form();
        using var run = Run(form);
        Press(run, Key.Tab);
        Press(run, Key.Escape);
        run.Game.Watch = input => input.Pressed(Key.W);

        Press(run, Key.W);

        Assert.False(run.Element("name").Focused);
        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void A_click_puts_the_caret_where_it_lands()
    {
        var form = new Form { Name = "Ada" };
        using var run = Run(form);
        var frame = run.FrameOf("name");

        // Left of the first letter, inside the field's padding.
        run.Input.Click(MouseButton.Left, new Vector2(frame.X + 2f, frame.Y + frame.Height / 2f) * run.UiScale);
        run.Ticks(2);
        Type(run, ">");

        Assert.Equal(">Ada", form.Name);
    }

    [Fact]
    public void A_turned_off_field_takes_no_focus()
    {
        var form = new Form { Off = true };
        using var run = Run(form);

        Focus(run, "name");
        Type(run, "Ada");

        Assert.False(run.Element("name").Focused);
        Assert.Equal("", form.Name);
    }

    [Fact]
    public void A_secure_field_shows_dots_and_gives_the_game_the_text()
    {
        var form = new Form();
        using var run = Run(form, secure: true);
        Focus(run, "password");

        Type(run, "hunter2");

        Assert.Equal("hunter2", form.Password);
        Assert.Equal("•••••••", run.Element("password").Value);
        Assert.DoesNotContain(run.ShownText, shown => shown.Text.Contains("hunter2"));
    }

    [Fact]
    public void A_secure_field_wont_copy_or_cut()
    {
        var form = new Form { Password = "hunter2" };
        using var run = Run(form, secure: true);
        Focus(run, "password");
        Clipboard(run).Text = "before";

        Command(run, Key.A);
        Command(run, Key.C);
        Command(run, Key.X);

        Assert.Equal("before", Clipboard(run).Text);
        Assert.Equal("hunter2", form.Password);
    }
}
