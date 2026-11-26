using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Cards, sections and forms, labeled content, disclosure groups, tab views and navigation stacks.</summary>
public sealed class ContainerTests
{
    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    // A row the screen leaves out.
    private static readonly bool Hidden = false;

    private static UiView Corner(UiView view, float width = 400f) =>
        view.Frame(width: width)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static Vector2 Middle(UiRect frame) => new(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);

    private static bool Shows(HeadlessRun<PointerGame> run, string text) => run.ShownText.Any(shown => shown.Text == text);

    private static UiRect FrameOf(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text).Frame;

    private static UiElement Element(HeadlessRun<PointerGame> run, UiRole role, string label) =>
        run.Elements.Single(element => element.Role == role && element.Label == label);

    private static void ClickOn(HeadlessRun<PointerGame> run, UiRole role, string label) => Click(run, Middle(Element(run, role, label).Frame));

    [Fact]
    public void A_cards_views_sit_inside_its_inset()
    {
        using var run = Run(_ => new Card { new Text("Gold") }.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading));

        var gold = FrameOf(run, "Gold");

        Assert.Equal(16f, gold.X);
        Assert.Equal(16f, gold.Y);
    }

    [Fact]
    public void Labeled_content_puts_its_value_at_the_trailing_edge()
    {
        using var run = Run(_ => Corner(new LabeledContent("Score", "1200"), 300f));

        Assert.Equal(0f, FrameOf(run, "Score").X);
        Assert.Equal(300f, FrameOf(run, "1200").Right, 0.5f);
    }

    [Fact]
    public void A_section_puts_one_line_between_rows_and_none_above_the_first()
    {
        using var run = Run(_ => Corner(new Section { new Text("Music"), new Text("Voices"), new Text("Effects") }));

        var music = FrameOf(run, "Music");
        var voices = FrameOf(run, "Voices");
        var effects = FrameOf(run, "Effects");

        // Each row is 10 in from the top and bottom of its slot, and a line a pixel high splits the slots.
        Assert.Equal(10f, music.Y);
        Assert.Equal(music.Bottom + 10f + 1f + 10f, voices.Y, 0.01f);
        Assert.Equal(voices.Bottom + 10f + 1f + 10f, effects.Y, 0.01f);
    }

    [Fact]
    public void A_missing_row_in_a_section_leaves_no_extra_line()
    {
        using var run = Run(_ => Corner(new Section { new Text("Music"), Hidden ? new Text("Voices") : null, new Text("Effects") }));

        Assert.Equal(FrameOf(run, "Music").Bottom + 21f, FrameOf(run, "Effects").Y, 0.01f);
    }

    [Fact]
    public void A_sections_header_and_footer_go_above_and_below_its_rows()
    {
        using var run = Run(_ => Corner(new Section("Audio", "Changes apply at once.") { new Text("Music") }));

        Assert.True(FrameOf(run, "Audio").Bottom <= FrameOf(run, "Music").Y);
        Assert.True(FrameOf(run, "Changes apply at once.").Y >= FrameOf(run, "Music").Bottom);
    }

    [Fact]
    public void A_form_spaces_its_sections_apart()
    {
        using var run = Run(_ => Corner(new Form { new Section { new Text("Music") }, new Section { new Text("Voices") } }));

        // The first panel ends 10 under Music, and the second starts 24 later with Voices 10 inside it.
        Assert.Equal(FrameOf(run, "Music").Bottom + 10f + 24f + 10f, FrameOf(run, "Voices").Y, 0.01f);
    }

    [Fact]
    public void A_disclosure_group_starts_closed_and_a_click_on_its_heading_opens_and_closes_it()
    {
        using var run = Run(_ => Corner(new DisclosureGroup("Advanced") { new Text("Shadow bias") }));
        Assert.False(Shows(run, "Shadow bias"));
        Assert.Equal(false, Element(run, UiRole.Disclosure, "Advanced").Checked);

        ClickOn(run, UiRole.Disclosure, "Advanced");
        Assert.True(Shows(run, "Shadow bias"));
        Assert.Equal(true, Element(run, UiRole.Disclosure, "Advanced").Checked);

        ClickOn(run, UiRole.Disclosure, "Advanced");
        Assert.False(Shows(run, "Shadow bias"));
    }

    [Fact]
    public void A_disclosure_group_the_game_controls_only_asks_to_open()
    {
        var asked = new List<bool>();
        using var run = Run(_ => Corner(new DisclosureGroup("Advanced", false, asked.Add) { new Text("Shadow bias") }));

        ClickOn(run, UiRole.Disclosure, "Advanced");

        Assert.Equal([true], asked);
        Assert.False(Shows(run, "Shadow bias"));
    }

    [Fact]
    public void Enter_opens_a_focused_disclosure_group()
    {
        using var run = Run(_ => Corner(new DisclosureGroup("Advanced") { new Text("Shadow bias") }));
        Press(run, Key.Tab);

        Press(run, Key.Enter);

        Assert.True(Shows(run, "Shadow bias"));
    }

    private static TabView Tabs(int? selection = null, Action<int>? change = null) => new(selection, change)
    {
        new Tab("Video", new Text("Resolution")),
        new Tab("Audio", new Text("Music volume")),
        new Tab("Controls", new Text("Invert look")),
    };

    [Fact]
    public void A_tab_view_shows_its_first_tab_and_a_click_on_another_shows_that_one()
    {
        using var run = Run(_ => Corner(Tabs()));
        Assert.True(Shows(run, "Resolution"));
        Assert.Equal(true, Element(run, UiRole.Tab, "Video").Checked);

        ClickOn(run, UiRole.Tab, "Audio");

        Assert.True(Shows(run, "Music volume"));
        Assert.False(Shows(run, "Resolution"));
        Assert.Equal(true, Element(run, UiRole.Tab, "Audio").Checked);
        Assert.Equal(false, Element(run, UiRole.Tab, "Video").Checked);
    }

    [Fact]
    public void A_tab_view_the_game_controls_shows_its_selection_and_reports_clicks()
    {
        var asked = new List<int>();
        using var run = Run(_ => Corner(Tabs(2, asked.Add)));

        ClickOn(run, UiRole.Tab, "Audio");

        Assert.Equal([1], asked);
        Assert.True(Shows(run, "Invert look"));
    }

    [Fact]
    public void The_bumpers_switch_tabs_round_the_ends_and_the_game_doesnt_see_them()
    {
        using var run = Run(_ => Corner(Tabs()));
        run.Game.Watch = input => input.Pressed(GamepadButton.LeftBumper) || input.Pressed(GamepadButton.RightBumper);

        Press(run, GamepadButton.LeftBumper);
        Assert.True(Shows(run, "Invert look"));

        Press(run, GamepadButton.RightBumper);
        Assert.True(Shows(run, "Resolution"));

        Press(run, GamepadButton.RightBumper);
        Assert.True(Shows(run, "Music volume"));
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void Right_on_the_focused_tab_bar_shows_the_next_tab()
    {
        using var run = Run(_ => Corner(Tabs()));
        Press(run, Key.Tab);

        Press(run, Key.Right);

        Assert.True(Shows(run, "Music volume"));
    }

    [Fact]
    public void The_bumpers_leave_tabs_alone_under_a_sheet()
    {
        using var run = Run(_ => Corner(Tabs()).Sheet(true, () => { }, new Text("Paused")));

        Press(run, GamepadButton.RightBumper);

        Assert.True(Shows(run, "Resolution"));
    }

    private enum Page
    {
        Video,
        Advanced,
    }

    private static UiView Destination(Page page) => new Text($"{page} page");

    private static UiView Settings(NavigationPath<Page> path) =>
        Corner(new NavigationStack<Page>(path, "Settings", new Text("Root page"), Destination), 500f).Frame(height: 400f);

    [Fact]
    public void A_navigation_stack_shows_the_page_pushed_with_its_title_and_a_way_back()
    {
        var path = new NavigationPath<Page>();
        using var run = Run(_ => Settings(path));
        Assert.True(Shows(run, "Root page"));
        Assert.DoesNotContain(run.Elements, element => element.Role == UiRole.Button);

        path.Push(Page.Video);
        run.Ticks(1);

        Assert.True(Shows(run, "Video page"));
        Assert.False(Shows(run, "Root page"));
        var stack = run.Elements.Single(element => element.Role == UiRole.Navigation);
        Assert.Equal(("Video", "1"), (stack.Label, stack.Value));
        Assert.Single(run.Elements, element => element is { Role: UiRole.Button, Label: "Settings" });
    }

    [Fact]
    public void The_back_button_is_named_after_the_page_under_and_goes_back_to_it()
    {
        var path = new NavigationPath<Page>();
        path.Push(Page.Video);
        path.Push(Page.Advanced);
        using var run = Run(_ => Settings(path));

        ClickOn(run, UiRole.Button, "Video");

        Assert.Equal([Page.Video], path.Pages);
        Assert.True(Shows(run, "Video page"));
    }

    [Fact]
    public void Escape_goes_back_a_page_and_at_the_root_the_game_has_it()
    {
        var path = new NavigationPath<Page>();
        path.Push(Page.Video);
        using var run = Run(_ => Settings(path));
        run.Game.Watch = input => input.Pressed(Key.Escape);

        Press(run, Key.Escape);
        Assert.Empty(path.Pages);
        Assert.Equal(0, run.Game.Reads);

        Press(run, Key.Escape);
        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void The_gamepads_East_button_goes_back_a_page()
    {
        var path = new NavigationPath<Page>();
        path.Push(Page.Video);
        using var run = Run(_ => Settings(path));

        Press(run, GamepadButton.East);

        Assert.Empty(path.Pages);
        Assert.True(Shows(run, "Root page"));
    }

    [Fact]
    public void Escape_in_a_stack_on_a_sheet_goes_back_before_it_closes_the_sheet()
    {
        var path = new NavigationPath<Page>();
        path.Push(Page.Video);
        bool closed = false;
        using var run = Run(_ => new Text("Game").Sheet(true, () => closed = true, new NavigationStack<Page>(path, "Settings", new Text("Root page"), Destination)));

        Press(run, Key.Escape);
        Assert.Empty(path.Pages);
        Assert.False(closed);

        Press(run, Key.Escape);
        Assert.True(closed);
    }

    [Fact]
    public void Escape_closes_a_menu_open_over_a_stack_without_going_back()
    {
        var path = new NavigationPath<Page>();
        path.Push(Page.Video);
        using var run = Run(_ => new VStack
        {
            new Menu("Options", [new MenuItem("Reset", () => { })]).Id("menu"),
            Settings(path),
        });
        ClickOn(run, UiRole.Menu, "Options");
        Assert.Contains(run.Elements, element => element.Role == UiRole.MenuItem);

        Press(run, Key.Escape);

        Assert.DoesNotContain(run.Elements, element => element.Role == UiRole.MenuItem);
        Assert.Equal([Page.Video], path.Pages);
    }

    [Fact]
    public void Back_at_the_root_does_nothing_and_says_so()
    {
        var path = new NavigationPath<Page>();

        Assert.False(path.Back());
        path.Push(Page.Video);
        path.Push(Page.Advanced);
        Assert.True(path.Back());
        Assert.Equal([Page.Video], path.Pages);

        path.BackToRoot();
        Assert.Empty(path.Pages);
    }
}
