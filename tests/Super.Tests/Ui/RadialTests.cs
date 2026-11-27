using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>Radial menus, aimed with the mouse or the stick and picked with a click, Enter or South.</summary>
public sealed class RadialTests
{
    private static HeadlessRun<PointerGame> Wheel(List<string> picked, params UiView[] after) => PointerGame.Show(_ =>
    {
        var items = new[] { "Up", "Right", "Down", "Left" }.Select(name => new RadialItem(name, () => picked.Add(name))).ToList();
        var stack = new VStack { new RadialMenu("Weapons", items) };
        foreach (var view in after)
        {
            stack.Add(view);
        }

        return FocusTests.Corner(stack);
    });

    private static UiElement Menu(HeadlessRun<PointerGame> run) => Assert.Single(run.Elements, element => element.Label == "Weapons");

    private static Vector2 Middle(HeadlessRun<PointerGame> run)
    {
        var frame = Menu(run).Frame;
        return new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);
    }

    private static void Hover(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.MoveMouse(at);
        run.Ticks(1);
    }

    [Theory]
    [InlineData(0f, -110f, "Up")]
    [InlineData(110f, 0f, "Right")]
    [InlineData(0f, 110f, "Down")]
    [InlineData(-110f, 0f, "Left")]
    // Nearer the top than the right, so still the top slice.
    [InlineData(40f, -100f, "Up")]
    public void The_mouse_aims_at_the_slice_it_is_over_and_a_click_picks_it(float x, float y, string expected)
    {
        var picked = new List<string>();
        using var run = Wheel(picked);
        var at = Middle(run) + new Vector2(x, y);

        Hover(run, at);
        Assert.Equal(expected, Menu(run).Value);

        Click(run, at);
        Assert.Equal([expected], picked);
    }

    [Fact]
    public void The_hole_in_the_middle_aims_at_nothing()
    {
        var picked = new List<string>();
        using var run = Wheel(picked);
        Hover(run, Middle(run) + new Vector2(110f, 0f));

        Click(run, Middle(run));

        Assert.Null(Menu(run).Value);
        Assert.Empty(picked);
    }

    [Fact]
    public void The_stick_aims_keeps_its_aim_when_let_go_and_South_picks()
    {
        var picked = new List<string>();
        using var run = Wheel(picked, new Button("Below").Id("below"));
        run.Input.ConnectGamepad("Xbox Controller");
        run.Ticks(1);
        Assert.True(Menu(run).Focused);

        // Back on the stick is down the screen.
        run.Input.Stick(GamepadStick.Left, new Vector2(0f, -1f));
        run.Ticks(10);
        Assert.Equal("Down", Menu(run).Value);
        run.Input.Stick(GamepadStick.Left, Vector2.Zero);
        run.Ticks(1);

        Press(run, GamepadButton.South);

        Assert.Equal(["Down"], picked);
        Assert.True(Menu(run).Focused);
    }

    [Fact]
    public void Left_and_right_step_round_the_wheel()
    {
        var picked = new List<string>();
        using var run = Wheel(picked);
        run.Ticks(1);
        // As anywhere focus was given, the first arrow only shows where it is.
        Press(run, Key.Left);
        Assert.Null(Menu(run).Value);

        Press(run, Key.Left);
        Assert.Equal("Left", Menu(run).Value);
        Press(run, Key.Right);
        Assert.Equal("Up", Menu(run).Value);
        Press(run, Key.Right);

        Press(run, Key.Enter);
        Assert.Equal(["Right"], picked);
    }
}
