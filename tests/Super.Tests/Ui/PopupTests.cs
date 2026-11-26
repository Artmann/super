using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Menus, popovers, sheets, alerts, confirmation dialogs, context menus and toasts.</summary>
public sealed class PopupTests
{
    // Headless runs at 30 ticks a second.
    private const int TicksPerSecond = 30;

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static Vector2 Middle(UiRect frame) => new(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);

    private static void ClickAt(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.Click(MouseButton.Left, at: at * run.UiScale);
        run.Ticks(2);
    }

    private static void Click(HeadlessRun<PointerGame> run, UiElement element) => ClickAt(run, Middle(element.Frame));

    private static UiElement Item(HeadlessRun<PointerGame> run, string label) =>
        run.Elements.Single(element => element.Role == UiRole.MenuItem && element.Label == label);

    private static string[] Items(HeadlessRun<PointerGame> run) =>
        run.Elements.Where(element => element.Role == UiRole.MenuItem).Select(element => element.Label).ToArray();

    private static object? FocusedId(HeadlessRun<PointerGame> run) => run.Elements.SingleOrDefault(element => element.Focused)?.Id;

    private static string? FocusedLabel(HeadlessRun<PointerGame> run) => run.Elements.SingleOrDefault(element => element.Focused)?.Label;

    /// <summary>A menu of New, Save, a turned-off Load and Quit, logging what's picked, beside a button.</summary>
    private static UiView GameMenu(PointerGame game) => Corner(new VStack(HorizontalAlignment.Leading, 8f)
    {
        new Menu("Game",
        [
            new MenuItem("New", () => game.Log.Add("new")),
            new MenuItem("Save", () => game.Log.Add("save")) { Shortcut = "Ctrl+S" },
            new MenuItem("Load", () => game.Log.Add("load")) { Disabled = true },
            MenuItem.Divider,
            new MenuItem("Quit", () => game.Log.Add("quit")) { Destructive = true },
        ]).Id("menu"),
        new Button("Under", () => game.Log.Add("under")).Id("under"),
    }.Padding(20f));

    [Fact]
    public void A_menu_shows_its_items_only_once_clicked()
    {
        using var run = Run(GameMenu);
        Assert.Empty(Items(run));
        Assert.Equal(false, run.Element("menu").Checked);

        Click(run, run.Element("menu"));

        Assert.Equal(["New", "Save", "Load", "Quit"], Items(run));
        Assert.Equal(true, run.Element("menu").Checked);
        Assert.True(Item(run, "Load").Disabled);
    }

    [Fact]
    public void A_menu_opens_under_its_button()
    {
        using var run = Run(GameMenu);
        var button = run.Element("menu").Frame;

        Click(run, run.Element("menu"));

        var first = Item(run, "New").Frame;
        Assert.True(first.Y > button.Bottom, $"The first item should be under the button, which ends at {button.Bottom}, but is at {first.Y}.");
        Assert.InRange(first.X, button.X - 1f, button.X + 12f);
    }

    [Fact]
    public void Clicking_an_item_runs_it_and_closes_the_menu()
    {
        using var run = Run(GameMenu);
        Click(run, run.Element("menu"));

        Click(run, Item(run, "Save"));

        Assert.Equal(["save"], run.Game.Log);
        Assert.Empty(Items(run));
    }

    [Fact]
    public void A_turned_off_item_does_nothing_when_clicked()
    {
        using var run = Run(GameMenu);
        Click(run, run.Element("menu"));

        Click(run, Item(run, "Load"));

        Assert.Empty(run.Game.Log);
        Assert.Contains("Load", Items(run));
    }

    [Fact]
    public void A_click_outside_a_menu_closes_it_without_pressing_what_was_under_it()
    {
        using var run = Run(GameMenu);
        var under = Middle(run.Element("under").Frame);
        Click(run, run.Element("menu"));
        Assert.NotEmpty(Items(run));

        // Far from the menu, which hangs from the button above, not over the one under it at its leading edge.
        ClickAt(run, new Vector2(600f, 500f));
        Assert.Empty(Items(run));

        ClickAt(run, under);
        Assert.Equal(["under"], run.Game.Log);
    }

    [Fact]
    public void The_arrows_move_through_a_menu_past_turned_off_items_and_Enter_picks()
    {
        using var run = Run(GameMenu);
        Press(run, Key.Tab);
        Assert.Equal("menu", FocusedId(run));

        Press(run, Key.Enter);
        Assert.Equal("New", FocusedLabel(run));

        Press(run, Key.Down);
        Assert.Equal("Save", FocusedLabel(run));
        Press(run, Key.Down);
        Assert.Equal("Quit", FocusedLabel(run));

        Press(run, Key.Enter);
        Assert.Equal(["quit"], run.Game.Log);
        Assert.Empty(Items(run));
    }

    [Fact]
    public void Tab_stays_inside_an_open_menu()
    {
        using var run = Run(GameMenu);
        Press(run, Key.Tab);
        Press(run, Key.Enter);

        var seen = new List<string?>();
        for (int i = 0; i < 4; i++)
        {
            Press(run, Key.Tab);
            seen.Add(FocusedLabel(run));
        }

        Assert.Equal(["Save", "Quit", "New", "Save"], seen);
    }

    [Fact]
    public void Escape_closes_a_menu_and_gives_focus_back_to_its_button()
    {
        using var run = Run(GameMenu);
        Press(run, Key.Tab);
        Press(run, Key.Enter);
        Press(run, Key.Down);

        Press(run, Key.Escape);

        Assert.Empty(Items(run));
        Assert.Equal("menu", FocusedId(run));
        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void The_gamepads_East_button_closes_a_menu_too()
    {
        using var run = Run(GameMenu);
        Press(run, Key.Tab);
        Press(run, Key.Enter);

        Press(run, GamepadButton.East);

        Assert.Empty(Items(run));
        Assert.Equal("menu", FocusedId(run));
    }

    [Fact]
    public void A_turned_off_menu_doesnt_open()
    {
        using var run = Run(_ => Corner(new Menu("Locked", [new MenuItem("Hidden")]) { Disabled = true }.Id("locked")));

        Click(run, run.Element("locked"));

        Assert.True(run.Element("locked").Disabled);
        Assert.Empty(Items(run));
    }

    [Fact]
    public void A_picker_as_a_menu_checks_the_picked_option_and_picks_the_one_clicked()
    {
        string picked = "medium";
        PickerOption<string>[] options = [new("Easy", "easy"), new("Medium", "medium"), new("Hard", "hard")];
        using var run = Run(_ => Corner(new Picker<string>("Difficulty", picked, options, value => picked = value) { Style = PickerStyle.Menu }.Id("difficulty")));
        Assert.Equal("Medium", run.Element("difficulty").Value);

        Click(run, run.Element("difficulty"));
        Assert.Equal([false, true, false], run.Elements.Where(element => element.Role == UiRole.MenuItem).Select(element => element.Checked));
        Assert.Equal("Medium", FocusedLabel(run));

        Click(run, Item(run, "Hard"));

        Assert.Equal("hard", picked);
        Assert.Equal("Hard", run.Element("difficulty").Value);
        Assert.Empty(Items(run));
    }

    /// <summary>A button that opens a popover with a button of its own, and a button beside it, clear of the popover under it.</summary>
    private static UiView WithPopover(PointerGame game, Func<bool> open, Action<bool> set) => Corner(new HStack(Spacing: 200f)
    {
        new Button("Info", () => set(!open()))
            .Popover(open(), () => set(false), new Button("Inside", () => game.Log.Add("inside")).Id("inside"))
            .Id("info"),
        new Button("Beside", () => game.Log.Add("beside")).Id("beside"),
    }.Padding(20f));

    [Fact]
    public void A_click_inside_a_popover_reaches_it_and_one_outside_closes_it()
    {
        bool open = false;
        using var run = Run(game => WithPopover(game, () => open, value => open = value));
        Click(run, run.Element("info"));
        Assert.True(open);

        Click(run, run.Element("inside"));
        Assert.Equal(["inside"], run.Game.Log);
        Assert.True(open);

        Click(run, run.Element("beside"));
        Assert.False(open);
        Assert.Equal(["inside"], run.Game.Log);
    }

    /// <summary>A save that asks before it's deleted, logging the answer.</summary>
    private static UiView WithAlert(PointerGame game, Func<bool> open, Action<bool> set, Dialog? alert = null) => Corner(
        new Button("Delete", () => set(true))
            .Alert(open(), () => set(false), alert ?? new Dialog("Delete this save?", "You can't undo this.")
            {
                Buttons =
                [
                    new DialogButton("Cancel", () => game.Log.Add("cancel"), DialogRole.Cancel),
                    new DialogButton("Delete", () => game.Log.Add("delete"), DialogRole.Destructive),
                ],
            })
            .Id("delete")
            .Padding(20f));

    [Fact]
    public void An_alert_says_what_it_asks_and_its_buttons_answer_and_close_it()
    {
        bool open = true;
        using var run = Run(game => WithAlert(game, () => open, value => open = value));
        var dialog = run.Elements.Single(element => element.Role == UiRole.Dialog);
        Assert.Equal("Delete this save?", dialog.Label);
        Assert.Equal("You can't undo this.", dialog.Value);

        Click(run, run.Elements.Single(element => element.Role == UiRole.Button && element.Label == "Delete" && element.Id is null));

        Assert.Equal(["delete"], run.Game.Log);
        Assert.False(open);
    }

    [Fact]
    public void An_alert_ignores_a_click_outside_it()
    {
        bool open = true;
        using var run = Run(game => WithAlert(game, () => open, value => open = value));

        ClickAt(run, new Vector2(5f, 5f));

        Assert.True(open);
        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void An_alert_opens_with_focus_on_cancel_when_its_other_answer_destroys_something()
    {
        bool open = false;
        using var run = Run(game => WithAlert(game, () => open, value => open = value));
        Press(run, Key.Tab);
        Press(run, Key.Enter);
        Assert.True(open);

        Assert.Equal("Cancel", FocusedLabel(run));
    }

    [Fact]
    public void Escape_presses_an_alerts_cancel_button_and_focus_goes_back()
    {
        bool open = false;
        using var run = Run(game => WithAlert(game, () => open, value => open = value));
        Press(run, Key.Tab);
        Press(run, Key.Enter);

        Press(run, Key.Escape);

        Assert.Equal(["cancel"], run.Game.Log);
        Assert.False(open);
        Assert.Equal("delete", FocusedId(run));
    }

    [Fact]
    public void An_alert_with_no_buttons_has_an_OK_that_the_gamepads_East_button_presses()
    {
        bool open = true;
        using var run = Run(game => WithAlert(game, () => open, value => open = value, new Dialog("Controller connected")));
        Assert.Single(run.Elements, element => element.Role == UiRole.Button && element.Label == "OK");

        Press(run, GamepadButton.East);

        Assert.False(open);
    }

    [Fact]
    public void A_click_outside_a_confirmation_dialog_presses_its_cancel_button()
    {
        bool open = true;
        var log = new List<string>();
        var dialog = new Dialog("Quit?")
        {
            Buttons =
            [
                new DialogButton("Save and quit", () => log.Add("save")),
                new DialogButton("Cancel", () => log.Add("cancel"), DialogRole.Cancel),
            ],
        };
        using var run = Run(_ => Corner(new Button("Quit", () => open = true).ConfirmationDialog(open, () => open = false, dialog)));

        ClickAt(run, new Vector2(5f, 5f));

        Assert.Equal(["cancel"], log);
        Assert.False(open);
    }

    [Fact]
    public void A_confirmation_dialog_stacks_its_buttons_at_the_bottom_with_cancel_last()
    {
        var dialog = new Dialog("Quit?")
        {
            Buttons =
            [
                new DialogButton("Cancel", null, DialogRole.Cancel),
                new DialogButton("Save and quit"),
                new DialogButton("Quit without saving", null, DialogRole.Destructive),
            ],
        };
        using var run = Run(_ => Corner(new Text("Game").ConfirmationDialog(true, () => { }, dialog)));
        var buttons = run.Elements.Where(element => element.Role == UiRole.Button).OrderBy(element => element.Frame.Y).ToArray();

        Assert.Equal(["Save and quit", "Quit without saving", "Cancel"], buttons.Select(button => button.Label));
        Assert.True(buttons[^1].Frame.Bottom > run.ScreenSize.Y / run.UiScale - 80f, $"Cancel should be near the bottom of the screen, but ends at {buttons[^1].Frame.Bottom}.");
    }

    /// <summary>A sheet with two fields and Done, over a page with a button of its own.</summary>
    private static UiView WithSheet(PointerGame game, Func<bool> open, Action<bool> set) => Corner(new VStack(HorizontalAlignment.Leading, 8f)
    {
        new Button("Page", () => game.Log.Add("page")).Id("page"),
        new Button("New character", () => set(true))
            .Sheet(open(), () => set(false), new VStack
            {
                new TextField("Name", "", _ => { }).Id("name"),
                new TextField("Title", "", _ => { }).Id("title"),
                new Button("Done", () => set(false)).DefaultFocus().Id("done"),
            })
            .Id("new"),
    }.Padding(20f));

    [Fact]
    public void A_sheet_opens_on_its_default_focus_and_Tab_stays_inside_it()
    {
        bool open = false;
        using var run = Run(game => WithSheet(game, () => open, value => open = value));
        Press(run, Key.Tab);
        Press(run, Key.Tab);
        Assert.Equal("new", FocusedId(run));
        Press(run, Key.Enter);
        Assert.Equal("done", FocusedId(run));

        var seen = new List<object?>();
        for (int i = 0; i < 4; i++)
        {
            Press(run, Key.Tab);
            seen.Add(FocusedId(run));
        }

        Assert.Equal(["name", "title", "done", "name"], seen);
    }

    [Fact]
    public void A_click_on_a_sheets_scrim_closes_it_and_doesnt_press_the_page()
    {
        bool open = true;
        using var run = Run(game => WithSheet(game, () => open, value => open = value));
        var page = run.Element("page").Frame;

        ClickAt(run, Middle(page));

        Assert.False(open);
        Assert.Empty(run.Game.Log);
    }

    private static UiView Sword(PointerGame game) => Corner(new Text("Iron sword")
        .Frame(200f, 60f)
        .ContextMenu(
        [
            new MenuItem("Equip", () => game.Log.Add("equip")),
            new MenuItem("Drop", () => game.Log.Add("drop")),
        ])
        .Id("sword")
        .Padding(20f));

    [Fact]
    public void A_right_click_opens_a_context_menu_where_it_was()
    {
        using var run = Run(Sword);
        var at = new Vector2(150f, 50f);
        run.Input.MoveMouse(at * run.UiScale);
        run.Ticks(1);
        Assert.Empty(Items(run));

        run.Input.Press(MouseButton.Right);
        run.Ticks(2);

        var first = Item(run, "Equip").Frame;
        Assert.InRange(first.X, at.X, at.X + 12f);
        Assert.InRange(first.Y, at.Y, at.Y + 12f);

        Click(run, Item(run, "Drop"));
        Assert.Equal(["drop"], run.Game.Log);
        Assert.Empty(Items(run));
    }

    [Fact]
    public void A_left_click_doesnt_open_a_context_menu()
    {
        using var run = Run(Sword);

        ClickAt(run, new Vector2(150f, 50f));

        Assert.Empty(Items(run));
    }

    [Fact]
    public void Holding_a_finger_on_a_view_opens_its_context_menu()
    {
        using var run = Run(Sword);
        run.Input.TouchDown(new Vector2(150f, 50f) * run.UiScale);
        run.Ticks(TicksPerSecond / 4);
        Assert.Empty(Items(run));

        run.Ticks(TicksPerSecond / 2);
        Assert.Equal(["Equip", "Drop"], Items(run));

        // Lifting the finger that opened it doesn't pick what's under it.
        run.Input.TouchUp();
        run.Ticks(2);
        Assert.Equal(["Equip", "Drop"], Items(run));
        Assert.Empty(run.Game.Log);
    }

    private static UiView WithToasts(PointerGame game, Toasts toasts) =>
        Corner(new Button("Play", () => game.Log.Add("play")).Id("play").Padding(20f)).Toasts(toasts);

    private static string[] ShownToasts(HeadlessRun<PointerGame> run) =>
        run.Elements.Where(element => element.Role == UiRole.Toast).Select(element => element.Label).ToArray();

    [Fact]
    public void A_toast_shows_in_the_bottom_corner_until_its_time_is_up()
    {
        var toasts = new Toasts();
        using var run = Run(game => WithToasts(game, toasts));

        toasts.Show(new Toast("Game saved", "Slot 1"));
        run.Ticks(1);
        var toast = run.Elements.Single(element => element.Role == UiRole.Toast);
        Assert.Equal("Slot 1", toast.Value);
        var screen = run.ScreenSize / run.UiScale;
        Assert.True(toast.Frame.Right > screen.X - 40f && toast.Frame.Bottom > screen.Y - 40f, $"The toast should be in the bottom right corner of {screen}, but is at {toast.Frame}.");

        run.Ticks(TicksPerSecond * 4 - 10);
        Assert.Equal(["Game saved"], ShownToasts(run));

        run.Ticks(20);
        Assert.Empty(ShownToasts(run));
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public void A_click_takes_a_toast_away_and_the_pointer_still_reaches_the_page_round_it()
    {
        var toasts = new Toasts();
        using var run = Run(game => WithToasts(game, toasts));
        toasts.Show("Game saved");
        toasts.Show("Quest updated");
        run.Ticks(1);

        Click(run, run.Element("play"));
        Assert.Equal(["play"], run.Game.Log);
        Assert.Equal(["Game saved", "Quest updated"], ShownToasts(run));

        Click(run, run.Elements.Single(element => element.Role == UiRole.Toast && element.Label == "Game saved"));
        Assert.Equal(["Quest updated"], ShownToasts(run));
    }

    [Fact]
    public void Showing_more_toasts_than_fit_lets_the_oldest_go()
    {
        var toasts = new Toasts { MaxShown = 2 };

        toasts.Show("One");
        toasts.Show("Two");
        toasts.Show("Three");

        Assert.Equal(["Two", "Three"], toasts.Shown.Select(toast => toast.Title));
    }

    [Fact]
    public void Dismissing_a_toast_takes_only_the_first_one_equal_to_it()
    {
        var toasts = new Toasts();
        toasts.Show("Saved");
        toasts.Show("Saved");

        toasts.Dismiss(new Toast("Saved"));

        Assert.Single(toasts.Shown);
    }

    [Fact]
    public void A_toast_needs_some_time_to_show()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Toasts().Show("Saved", 0f));

        Assert.Equal("seconds", error.ParamName);
        Assert.StartsWith("seconds ('0') must be a non-negative and non-zero value.", error.Message);
    }
}
