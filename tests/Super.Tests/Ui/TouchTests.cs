using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

public sealed class TouchTests
{
    private const float Close = 0.01f;
    private const int RowHeight = 40;

    // Headless runs at 30 ticks a second, so half a second is 15 ticks.
    private const int LongPressTicks = 15;

    private static readonly Color Solid = new(0.2f, 0.2f, 0.2f);
    private static readonly Vector2 OnBox = new(40f, 40f);
    private static readonly Vector2 Away = new(600f, 600f);

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen) => PointerGame.Show(screen);

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView Target(PointerGame game, string name = "click") =>
        Corner(new Text(name).Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add(name)));

    /// <summary>A list 100 tall of rows that each log their name when clicked.</summary>
    private static UiView Rows(PointerGame game, int count = 10)
    {
        var scroll = new ScrollView();
        for (int i = 0; i < count; i++)
        {
            string name = $"row {i}";
            scroll.Add(new Text(name).Frame(90f, RowHeight, alignment: Alignment.TopLeading).OnClick(() => game.Log.Add(name)));
        }

        return Corner(scroll.Frame(100f, 100f));
    }

    private static float Y(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text).Frame.Y;

    /// <summary>How far the list of rows has scrolled, from whichever row shows.</summary>
    private static float Offset(HeadlessRun<PointerGame> run)
    {
        var row = run.ShownText.First(shown => shown.Text.StartsWith("row ", StringComparison.Ordinal));
        return int.Parse(row.Text[4..], System.Globalization.CultureInfo.InvariantCulture) * RowHeight - row.Frame.Y;
    }

    private static void Down(HeadlessRun<PointerGame> run, Vector2 at, int finger = 0)
    {
        run.Input.TouchDown(at, finger);
        run.Ticks(1);
    }

    private static void Move(HeadlessRun<PointerGame> run, Vector2 to, int finger = 0)
    {
        run.Input.TouchMove(to, finger);
        run.Ticks(1);
    }

    private static void Up(HeadlessRun<PointerGame> run, int finger = 0)
    {
        run.Input.TouchUp(finger);
        run.Ticks(1);
    }

    [Fact]
    public void A_tap_on_a_view_clicks_it_once()
    {
        using var run = Run(game => Target(game));

        run.Input.Tap(OnBox);
        run.Ticks(2);

        Assert.Equal(["click"], run.Game.Log);
    }

    [Fact]
    public void A_finger_that_lifts_off_the_view_is_no_tap()
    {
        using var run = Run(game => Target(game));

        Down(run, OnBox);
        Move(run, Away);
        Up(run);
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_finger_the_system_takes_away_is_no_tap()
    {
        using var run = Run(game => Target(game));

        Down(run, OnBox);
        run.Input.TouchCancel();
        run.Ticks(2);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_drag_on_a_list_moves_it_with_the_finger_and_clicks_nothing()
    {
        using var run = Run(game => Rows(game));

        Down(run, new Vector2(40f, 90f));
        Move(run, new Vector2(40f, 30f));

        Assert.Equal(20f, Y(run, "row 2"), Close);

        // Held still for a tick, so it lets go without flinging.
        run.Ticks(1);
        Up(run);
        run.Ticks(1);
        Assert.Equal(20f, Y(run, "row 2"), Close);
        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_short_wobble_on_a_list_row_still_taps_it()
    {
        using var run = Run(game => Rows(game));

        Down(run, new Vector2(40f, 50f));
        Move(run, new Vector2(43f, 46f));
        Up(run);
        run.Ticks(1);

        Assert.Equal(["row 1"], run.Game.Log);
        Assert.Equal(0f, Y(run, "row 0"), Close);
    }

    [Fact]
    public void A_swipe_flings_the_list_on_after_the_finger_lifts_and_it_slows_to_a_stop()
    {
        using var run = Run(game => Rows(game, count: 40));

        Down(run, new Vector2(40f, 90f));
        Move(run, new Vector2(40f, 70f));
        Move(run, new Vector2(40f, 50f));
        Up(run);
        float lifted = Offset(run);

        run.Ticks(1);
        float first = Offset(run) - lifted;
        run.Ticks(1);
        float second = Offset(run) - lifted - first;
        Assert.True(first > 0f, $"The list should go on moving after the finger lifts, but moved {first}.");
        Assert.True(second > 0f && second < first, $"The fling should slow, but moved {first} and then {second}.");

        run.Ticks(60);
        float settled = Offset(run);
        run.Ticks(5);
        Assert.Equal(settled, Offset(run), Close);
        Assert.True(settled < 40 * RowHeight - 100f, $"A gentle swipe shouldn't reach the end, but went to {settled}.");
    }

    [Fact]
    public void A_finger_on_a_flinging_list_stops_it()
    {
        using var run = Run(game => Rows(game, count: 40));
        Down(run, new Vector2(40f, 90f));
        Move(run, new Vector2(40f, 50f));
        Up(run);

        Down(run, new Vector2(40f, 50f));
        float caught = Offset(run);
        run.Ticks(3);

        Assert.Equal(caught, Offset(run), Close);
    }

    [Fact]
    public void A_sideways_drag_in_a_list_leaves_it_where_it_is()
    {
        using var run = Run(game => Rows(game));

        Down(run, new Vector2(20f, 50f));
        Move(run, new Vector2(80f, 46f));
        Up(run);

        Assert.Equal(0f, Y(run, "row 0"), Close);
    }

    [Fact]
    public void A_held_finger_long_presses_once_and_its_lift_is_no_tap()
    {
        using var run = Run(game => Corner(
            new Text("hold").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("click")).OnLongPress(() => game.Log.Add("long press"))));

        Down(run, OnBox);
        run.Ticks(LongPressTicks - 2);
        Assert.Empty(run.Game.Log);

        run.Ticks(LongPressTicks);
        Up(run);
        run.Ticks(1);

        Assert.Equal(["long press"], run.Game.Log);
    }

    [Fact]
    public void A_long_press_on_a_turned_off_view_does_nothing()
    {
        using var run = Run(game => Corner(
            new Text("hold").Frame(100f, 100f).Background(Solid).OnLongPress(() => game.Log.Add("long press")).Disabled()));

        Down(run, OnBox);
        run.Ticks(LongPressTicks * 2);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_slider_follows_the_finger()
    {
        var slides = new List<float>();
        using var run = Run(_ => Corner(new Rectangle().Fill(Solid).Frame(100f, 20f).OnSlide(slides.Add)));

        Down(run, new Vector2(25f, 10f));
        Move(run, new Vector2(75f, 10f));
        Up(run);

        Assert.Equal([0.25f, 0.75f], slides);
    }

    [Fact]
    public void A_tap_on_a_text_field_focuses_it()
    {
        using var run = Run(_ => Corner(new TextField("Name", "", _ => { }).Frame(200f).Id("name")));

        run.Input.Tap(run.FrameOf("name") is var frame ? new Vector2(frame.X + 20f, frame.Y + frame.Height / 2) : default);
        run.Ticks(2);

        Assert.True(run.Element("name").Focused);
    }

    [Fact]
    public void The_game_sees_fingers_off_the_interface_with_their_ids_and_motion_but_not_those_on_it()
    {
        var seen = new List<Touch>();
        using var run = Run(game => Target(game));
        run.Game.Watch = input =>
        {
            seen.AddRange(input.Touches);
            return false;
        };

        Down(run, OnBox, finger: 1);
        Down(run, new Vector2(300f, 300f), finger: 2);
        Move(run, new Vector2(310f, 305f), finger: 2);
        Up(run, finger: 2);
        Up(run, finger: 1);
        run.Ticks(1);

        Assert.Equal(
            [
                new Touch(2, new Vector2(300f, 300f), Vector2.Zero, Pressed: true, Released: false),
                new Touch(2, new Vector2(310f, 305f), new Vector2(10f, 5f), Pressed: false, Released: false),
                new Touch(2, new Vector2(310f, 305f), Vector2.Zero, Pressed: false, Released: true),
            ],
            seen);
    }

    [Fact]
    public void A_finger_that_comes_down_and_lifts_between_ticks_is_seen_pressed_and_released_once()
    {
        var seen = new List<Touch>();
        using var run = Run(_ => Corner(new Text("nothing here")));
        run.Game.Watch = input =>
        {
            seen.AddRange(input.Touches);
            return false;
        };

        run.Input.Tap(Away);
        run.Ticks(3);

        Assert.Equal([new Touch(0, Away, Vector2.Zero, Pressed: true, Released: true)], seen);
    }

    [Fact]
    public void A_second_finger_on_the_interface_does_nothing_while_the_first_holds_it()
    {
        using var run = Run(game => Rows(game));

        Down(run, new Vector2(40f, 10f), finger: 0);
        Down(run, new Vector2(40f, 50f), finger: 1);
        Up(run, finger: 1);
        Up(run, finger: 0);
        run.Ticks(1);

        Assert.Equal(["row 0"], run.Game.Log);
    }
}
