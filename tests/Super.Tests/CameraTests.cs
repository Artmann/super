using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class CameraTests
{
    private static readonly Vector2 Screen = new(1280f, 720f);

    /// <summary>Where the renderer's own matrices put a point, in pixels. Its y already points down.</summary>
    private static Vector2 Project(in Camera camera, Vector3 point)
    {
        var view = Matrix4x4.CreateLookAt(camera.Position, camera.Target, camera.Up);
        var projection = Projection.PerspectiveInfiniteReversedZ(camera.FieldOfView, Screen.X / Screen.Y, 0.1f);
        var clip = Vector4.Transform(new Vector4(point, 1f), view * projection);
        return new Vector2((clip.X / clip.W + 1f) * 0.5f * Screen.X, (clip.Y / clip.W + 1f) * 0.5f * Screen.Y);
    }

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

    [Theory]
    [InlineData(0f, 5f, 5f)]
    [InlineData(3f, 2f, -4f)]
    [InlineData(0f, 10f, 0f)]
    [InlineData(0f, -10f, 0f)]
    public void A_ray_runs_through_what_is_drawn_at_that_pixel(float x, float y, float z)
    {
        var camera = new Camera(new Vector3(x, y, z), Vector3.Zero);
        var point = new Vector3(0.7f, 0.4f, -0.9f);
        var ray = camera.Ray(Project(camera, point), Screen);
        AssertNear(point, ray.Origin + ray.Direction * Vector3.Distance(ray.Origin, point));
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
                Clicked = input.MouseRay(Above).Ground();
            }
        }

        public void Draw(View view)
        {
            view.Camera(Above);
            Peek?.Invoke();
        }

        public Action? Peek { get; set; }

        public void ReadRay() => input.MouseRay(Above);
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
        Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
    }

    [Fact]
    public void The_view_takes_a_whole_camera()
    {
        var view = new View();
        view.Begin(0f);
        view.Camera(new Camera(Vector3.UnitY, Vector3.UnitX, 1f));
        Assert.Equal(new Camera(Vector3.UnitY, Vector3.UnitX, 1f), view.Shot);
    }
}
