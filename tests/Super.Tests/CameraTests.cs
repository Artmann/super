using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class CameraTests
{
    private static readonly Vector2 Screen = new(1280f, 720f);

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 0.001f, $"Expected {expected}, got {actual}");
    }

    [Fact]
    public void The_middle_of_the_screen_looks_at_the_target()
    {
        var camera = new Camera(new Vector3(0f, 5f, 5f), Vector3.Zero);
        var ray = camera.Ray(Screen / 2f, Screen);
        AssertNear(camera.Position, ray.Origin);
        AssertNear(Vector3.Normalize(camera.Target - camera.Position), ray.Direction);
    }

    [Fact]
    public void The_target_shows_in_the_middle_of_the_screen()
    {
        var camera = new Camera(new Vector3(0f, 5f, 5f), Vector3.Zero);
        var pixel = camera.Pixel(Vector3.Zero, Screen)!.Value;
        Assert.Equal(640f, pixel.X, 0.01f);
        Assert.Equal(360f, pixel.Y, 0.01f);
    }

    [Fact]
    public void A_point_above_the_target_shows_above_the_middle()
    {
        // Looking down the z axis with a quarter turn of view: a point one up at one away shows half a screen up.
        var camera = new Camera(new Vector3(0f, 0f, 1f), Vector3.Zero, MathF.PI / 2f);
        var pixel = camera.Pixel(new Vector3(0f, 1f, 0f), Screen)!.Value;
        Assert.Equal(640f, pixel.X, 0.01f);
        Assert.Equal(0f, pixel.Y, 0.01f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_point_along_a_pixels_ray_shows_at_that_pixel(bool orthographic)
    {
        var camera = new Camera(new Vector3(3f, 4f, 6f), new Vector3(0f, 1f, 0f)) { Size = orthographic ? 8f : 0f };
        var asked = new Vector2(200f, 500f);
        var ray = camera.Ray(asked, Screen);
        var pixel = camera.Pixel(ray.Origin + ray.Direction * 7f, Screen)!.Value;
        Assert.Equal(asked.X, pixel.X, 0.05f);
        Assert.Equal(asked.Y, pixel.Y, 0.05f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_point_behind_the_camera_has_no_pixel(bool orthographic)
    {
        var camera = new Camera(new Vector3(0f, 0f, 5f), Vector3.Zero) { Size = orthographic ? 8f : 0f };
        Assert.Null(camera.Pixel(new Vector3(0f, 0f, 6f), Screen));
    }

    [Fact]
    public void A_ray_finds_the_ground()
    {
        var ray = new Ray(new Vector3(1f, 4f, 1f), -Vector3.UnitY);
        Assert.Equal(new Vector3(1f, 0f, 1f), ray.Ground());
        Assert.Equal(new Vector3(1f, 3f, 1f), ray.Ground(3f));
    }

    [Fact]
    public void A_ray_pointing_at_the_sky_finds_no_ground()
    {
        Assert.Null(new Ray(Vector3.UnitY, Vector3.UnitY).Ground());
        Assert.Null(new Ray(Vector3.UnitY, Vector3.UnitX).Ground());
        Assert.Null(new Ray(-Vector3.UnitY, Vector3.UnitX).Ground());
    }

    [Fact]
    public void A_ray_gives_how_far_away_a_ball_is()
    {
        var ray = new Ray(new Vector3(0f, 0f, 10f), -Vector3.UnitZ);
        Assert.Equal(9f, ray.HitsBall(Vector3.Zero, 1f));
        Assert.Null(ray.HitsBall(new Vector3(5f, 0f, 0f), 1f));
        Assert.Null(ray.HitsBall(new Vector3(0f, 0f, 20f), 1f));
    }

    [Fact]
    public void A_ray_starting_inside_a_ball_hits_it_at_once()
    {
        Assert.Equal(0f, new Ray(Vector3.Zero, Vector3.UnitZ).HitsBall(Vector3.Zero, 1f));
    }

    [Fact]
    public void A_ray_gives_how_far_away_a_box_is()
    {
        var ray = new Ray(new Vector3(0.5f, 0.5f, 10f), -Vector3.UnitZ);
        Assert.Equal(9f, ray.HitsBox(-Vector3.One, Vector3.One));
        Assert.Null(new Ray(new Vector3(5f, 0f, 10f), -Vector3.UnitZ).HitsBox(-Vector3.One, Vector3.One));
        Assert.Null(new Ray(new Vector3(0f, 0f, -10f), -Vector3.UnitZ).HitsBox(-Vector3.One, Vector3.One));
    }

    [Fact]
    public void A_ray_starting_inside_a_box_hits_it_at_once()
    {
        Assert.Equal(0f, new Ray(Vector3.Zero, Vector3.UnitX).HitsBox(-Vector3.One, Vector3.One));
    }

    [Fact]
    public void A_ray_lying_in_a_face_of_a_box_counts_as_a_miss()
    {
        Assert.Null(new Ray(new Vector3(1f, 0f, 5f), -Vector3.UnitZ).HitsBox(-Vector3.One, Vector3.One));
    }

    [Fact]
    public void Orbiting_keeps_the_distance_and_stays_off_the_pole()
    {
        var orbit = new OrbitCamera { Distance = 10f };
        orbit.Turn(new Vector2(1.2f, 50f));
        orbit.Update(new Vector3(1f, 0f, 0f));
        Assert.Equal(new Vector3(1f, 0f, 0f), orbit.Current.Target);
        Assert.Equal(10f, Vector3.Distance(orbit.Current.Position, orbit.Current.Target), 3);
        Assert.True(orbit.Pitch < MathF.PI / 2f);
    }

    [Fact]
    public void Zooming_stays_between_the_limits()
    {
        var orbit = new OrbitCamera { Distance = 10f, MinDistance = 2f, MaxDistance = 20f };
        orbit.Zoom(1f);
        Assert.True(orbit.Distance < 10f);
        orbit.Zoom(100f);
        Assert.Equal(2f, orbit.Distance);
        orbit.Zoom(-100f);
        Assert.Equal(20f, orbit.Distance);
    }

    [Fact]
    public void A_camera_blends_between_its_last_two_ticks()
    {
        var orbit = new OrbitCamera { Distance = 10f };
        orbit.Update(Vector3.Zero);
        orbit.Update(new Vector3(2f, 0f, 0f));
        Assert.Equal(new Vector3(1f, 0f, 0f), orbit.At(0.5f).Target);
    }

    [Fact]
    public void A_follow_camera_starts_in_place()
    {
        var follow = new FollowCamera { Offset = new Vector3(0f, 5f, 5f) };
        follow.Update(new Vector3(3f, 0f, 0f), 1f / 60f);
        Assert.Equal(new Vector3(3f, 0f, 0f), follow.Current.Target);
        Assert.Equal(new Vector3(3f, 5f, 5f), follow.Current.Position);
    }

    [Fact]
    public void A_follow_camera_lags_then_arrives()
    {
        var follow = new FollowCamera { Offset = Vector3.UnitZ, Lag = 0.2f };
        follow.Update(Vector3.Zero, 1f / 60f);
        follow.Update(new Vector3(10f, 0f, 0f), 1f / 60f);
        Assert.InRange(follow.Current.Target.X, 0.1f, 5f);
        for (int i = 0; i < 300; i++)
        {
            follow.Update(new Vector3(10f, 0f, 0f), 1f / 60f);
        }

        Assert.Equal(10f, follow.Current.Target.X, 2);
    }

    [Fact]
    public void A_follow_camera_lags_the_same_at_any_tick_rate()
    {
        var slow = new FollowCamera { Offset = Vector3.UnitZ };
        var fast = new FollowCamera { Offset = Vector3.UnitZ };
        slow.Update(Vector3.Zero, 0.1f);
        fast.Update(Vector3.Zero, 0.1f);
        slow.Update(Vector3.UnitX, 0.1f);
        fast.Update(Vector3.UnitX, 0.05f);
        fast.Update(Vector3.UnitX, 0.05f);
        Assert.Equal(slow.Current.Target.X, fast.Current.Target.X, 3);
    }

    [Fact]
    public void A_follow_camera_can_jump_when_its_target_teleports()
    {
        var follow = new FollowCamera { Offset = Vector3.UnitZ };
        follow.Update(Vector3.Zero, 1f / 60f);
        follow.Jump();
        follow.Update(new Vector3(50f, 0f, 0f), 1f / 60f);
        Assert.Equal(new Vector3(50f, 0f, 0f), follow.Current.Target);
        Assert.Equal(new Vector3(50f, 0f, 0f), follow.At(0f).Target);
    }

    private class Clicker(Input input) : IGame
    {
        public static readonly Camera Above = new(new Vector3(0f, 10f, 10f), Vector3.Zero);

        public Vector3? Clicked { get; private set; }

        public void Update(GameTime time)
        {
            if (input.Pressed(MouseButton.Left))
            {
                Clicked = input.Mouse.Ray(Above).Ground();
            }
        }

        public void Draw(View view)
        {
            view.Camera(Above);
            Peek?.Invoke();
        }

        public Action? Peek { get; set; }

        public void ReadRay() => input.Mouse.Ray(Above);
    }

    [Fact]
    public void Clicking_the_middle_of_the_screen_finds_what_the_camera_looks_at()
    {
        using var run = HeadlessGame.Create<Clicker>(1);
        run.Input.Click(MouseButton.Left, Screen / 2f);
        run.Ticks(1);
        AssertNear(Vector3.Zero, run.Game.Clicked!.Value);
    }

    [Fact]
    public void The_mouse_ray_can_only_be_read_in_update()
    {
        using var run = HeadlessGame.Create<Clicker>(1);
        run.Game.Peek = run.Game.ReadRay;
        var error = Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
        Assert.Equal("Input can only be read in Update. Draw and Ui describe state, so they can't look at input.", error.Message);
    }
}
