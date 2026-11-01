using System.Numerics;

namespace Super.Tests;

public class CameraRigTests
{
    private const float Tick = 1f / 30f;

    private static void AssertNear(Vector3 expected, Vector3 actual, float within = 0.001f)
    {
        Assert.True(Vector3.Distance(expected, actual) < within, $"Expected {expected}, got {actual}");
    }

    [Fact]
    public void A_boom_sits_behind_the_pivot_at_its_length()
    {
        var boom = new BoomCamera { Yaw = 0f, Pitch = 0f, Length = 6f, Pivot = new Vector3(0f, 1.5f, 0f) };
        boom.Update(new Vector3(2f, 0f, 0f), Tick);
        AssertNear(new Vector3(2f, 1.5f, 6f), boom.Current.Position);
        AssertNear(new Vector3(2f, 1.5f, 0f), boom.Current.Target);
    }

    [Fact]
    public void A_shoulder_slides_the_view_sideways()
    {
        var boom = new BoomCamera { Pitch = 0f, Pivot = Vector3.Zero, Length = 6f, Shoulder = 1f };
        boom.Update(Vector3.Zero, Tick);
        AssertNear(new Vector3(1f, 0f, 6f), boom.Current.Position);
        AssertNear(new Vector3(1f, 0f, 0f), boom.Current.Target);
    }

    [Fact]
    public void The_arm_runs_from_the_pivot_to_the_camera()
    {
        var boom = new BoomCamera { Yaw = 1f, Pitch = 0.4f, Length = 6f };
        boom.Update(Vector3.One, Tick);
        var arm = boom.Arm(Vector3.One);
        AssertNear(boom.Current.Target, arm.Origin);
        AssertNear(boom.Current.Position, arm.Origin + arm.Direction * 6f);
    }

    [Fact]
    public void A_wall_pulls_the_boom_in_at_once_and_leaves_a_margin()
    {
        var boom = new BoomCamera { Pitch = 0f, Pivot = Vector3.Zero, Length = 6f, Margin = 0.5f };
        boom.Update(Vector3.Zero, Tick);
        boom.Update(Vector3.Zero, Tick, blockedAt: 2f);
        Assert.Equal(1.5f, boom.Current.Position.Z, 3);
    }

    [Fact]
    public void A_boom_eases_back_out_once_the_wall_is_gone()
    {
        var boom = new BoomCamera { Pitch = 0f, Pivot = Vector3.Zero, Length = 6f, Margin = 0f, ReturnLag = 0.3f };
        boom.Update(Vector3.Zero, Tick);
        boom.Update(Vector3.Zero, Tick, blockedAt: 2f);
        boom.Update(Vector3.Zero, Tick);
        Assert.InRange(boom.Current.Position.Z, 2.01f, 3f);

        for (int i = 0; i < 300; i++)
        {
            boom.Update(Vector3.Zero, Tick);
        }

        Assert.Equal(6f, boom.Current.Position.Z, 2);
    }

    [Fact]
    public void A_wall_right_up_against_the_pivot_never_turns_the_boom_inside_out()
    {
        var boom = new BoomCamera { Pitch = 0f, Pivot = Vector3.Zero, Length = 6f, Margin = 0.5f, MinLength = 0.25f };
        boom.Update(Vector3.Zero, Tick, blockedAt: 0f);
        Assert.Equal(0.25f, boom.Current.Position.Z, 3);
    }

    [Fact]
    public void A_wall_past_the_end_of_the_boom_changes_nothing()
    {
        var boom = new BoomCamera { Pitch = 0f, Pivot = Vector3.Zero, Length = 6f };
        boom.Update(Vector3.Zero, Tick, blockedAt: 50f);
        Assert.Equal(6f, boom.Current.Position.Z, 3);
    }

    [Fact]
    public void Zooming_a_boom_keeps_inside_its_limits()
    {
        var boom = new BoomCamera { Length = 6f, MinLength = 2f, MaxLength = 8f };
        boom.Zoom(100f);
        Assert.Equal(2f, boom.Length);
        boom.Zoom(-100f);
        Assert.Equal(8f, boom.Length);
    }

    [Fact]
    public void A_stick_walks_where_a_third_person_camera_faces()
    {
        var boom = new BoomCamera { Yaw = MathF.PI / 2f };
        AssertNear(-Vector3.UnitX, boom.Walk(Vector2.UnitY));
        AssertNear(-Vector3.UnitZ, boom.Walk(Vector2.UnitX));
    }

    [Fact]
    public void A_jump_skips_the_blend()
    {
        var boom = new BoomCamera();
        boom.Update(Vector3.Zero, Tick);
        boom.Jump();
        boom.Update(new Vector3(100f, 0f, 0f), Tick);
        Assert.Equal(boom.Current, boom.At(0f));
    }

    [Fact]
    public void First_person_sits_at_eye_height_above_the_feet()
    {
        var eyes = new FirstPersonCamera { EyeHeight = 1.7f };
        eyes.Update(new Vector3(1f, 0f, 2f));
        AssertNear(new Vector3(1f, 1.7f, 2f), eyes.Current.Position);
    }

    [Fact]
    public void First_person_starts_looking_along_minus_z()
    {
        var eyes = new FirstPersonCamera();
        eyes.Update(Vector3.Zero);
        var look = Vector3.Normalize(eyes.Current.Target - eyes.Current.Position);
        AssertNear(-Vector3.UnitZ, look);
    }

    [Fact]
    public void Moving_the_mouse_right_and_down_looks_right_and_down()
    {
        var eyes = new FirstPersonCamera();
        eyes.Turn(new Vector2(0.2f, 0.3f));
        eyes.Update(Vector3.Zero);
        var look = eyes.Current.Target - eyes.Current.Position;
        Assert.True(look.X > 0f);
        Assert.True(look.Y < 0f);
    }

    [Fact]
    public void First_person_stops_short_of_looking_straight_up()
    {
        var eyes = new FirstPersonCamera();
        eyes.Turn(new Vector2(0f, -100f));
        Assert.Equal(1.5f, eyes.Pitch);
    }

    [Fact]
    public void Walking_stays_level_however_far_up_you_look()
    {
        var eyes = new FirstPersonCamera { Yaw = MathF.PI / 2f, Pitch = 1f };
        AssertNear(-Vector3.UnitX, eyes.Walk(Vector2.UnitY));
        AssertNear(-Vector3.UnitZ, eyes.Walk(Vector2.UnitX));
    }

    [Fact]
    public void Top_down_looks_straight_down_at_its_center()
    {
        var top = new TopDownCamera { Size = 30f, Lag = 0f };
        top.Update(new Vector3(4f, 0f, 5f), Tick);
        var ground = top.Current.Ray(new Vector2(640f, 360f), new Vector2(1280f, 720f)).Ground();
        Assert.True(top.Current.IsOrthographic);
        Assert.Equal(30f, top.Current.Size);
        AssertNear(new Vector3(4f, 0f, 5f), ground!.Value);
        AssertNear(-Vector3.UnitY, Vector3.Normalize(top.Current.Target - top.Current.Position));
    }

    [Fact]
    public void Panning_moves_the_top_down_view_the_way_the_screen_faces()
    {
        var top = new TopDownCamera { Lag = 0f };
        top.Update(Vector3.Zero, Tick);
        top.Pan(new Vector2(3f, 2f));
        top.Update(Tick);
        AssertNear(new Vector3(3f, 0f, -2f), top.Center);
        AssertNear(new Vector3(3f, 0f, -2f), top.Current.Target);
    }

    [Fact]
    public void Zooming_in_shows_less_and_keeps_inside_its_limits()
    {
        var top = new TopDownCamera { Size = 20f, MinSize = 5f, MaxSize = 40f };
        top.Zoom(1f);
        Assert.True(top.Size < 20f);
        top.Zoom(100f);
        Assert.Equal(5f, top.Size);
        top.Zoom(-100f);
        Assert.Equal(40f, top.Size);
    }

    [Fact]
    public void An_overhead_camera_eases_towards_its_center()
    {
        var top = new TopDownCamera { Lag = 0.2f };
        top.Update(Vector3.Zero, Tick);
        top.Update(new Vector3(10f, 0f, 0f), Tick);
        Assert.InRange(top.Current.Target.X, 0.1f, 5f);
    }

    [Fact]
    public void Isometric_uses_the_true_angle()
    {
        var iso = new IsometricCamera { Lag = 0f };
        iso.Update(Vector3.Zero, Tick);
        var look = Vector3.Normalize(iso.Current.Target - iso.Current.Position);
        Assert.True(iso.Current.IsOrthographic);
        Assert.Equal(-MathF.Atan(1f / MathF.Sqrt(2f)), MathF.Asin(look.Y), 3);
        Assert.Equal(MathF.Abs(look.X), MathF.Abs(look.Z), 3);
    }

    [Fact]
    public void Isometric_turns_in_quarters_and_eases_there()
    {
        var iso = new IsometricCamera { Lag = 0.2f };
        iso.Update(Vector3.Zero, Tick);
        var before = iso.Current.Position;
        iso.Turn(1);
        iso.Update(Tick);
        Assert.NotEqual(before, iso.Current.Position);

        for (int i = 0; i < 300; i++)
        {
            iso.Update(Tick);
        }

        AssertNear(new Vector3(before.Z, before.Y, -before.X), iso.Current.Position, 0.01f);
    }

    [Fact]
    public void Panning_an_isometric_view_follows_its_turn()
    {
        var iso = new IsometricCamera { Yaw = 0f, Lag = 0f };
        iso.Pan(Vector2.UnitY);
        AssertNear(-Vector3.UnitZ, iso.Center);

        // Turned a quarter, up the screen runs along -X instead.
        var turned = new IsometricCamera { Yaw = MathF.PI / 2f, Lag = 0f };
        turned.Pan(Vector2.UnitY);
        AssertNear(-Vector3.UnitX, turned.Center);
    }

    [Fact]
    public void Clicking_on_an_isometric_view_finds_the_ground_under_the_pixel()
    {
        var iso = new IsometricCamera { Size = 20f, Lag = 0f };
        iso.Update(new Vector3(3f, 0f, -2f), Tick);
        var screen = new Vector2(1280f, 720f);
        var middle = iso.Current.Ray(screen / 2f, screen).Ground();
        var above = iso.Current.Ray(new Vector2(640f, 0f), screen).Ground();
        AssertNear(new Vector3(3f, 0f, -2f), middle!.Value);
        Assert.True(Vector3.Distance(middle.Value, above!.Value) > 10f, "A tilted view sees further up the screen");
    }

    [Fact]
    public void The_rigs_blend_between_ticks()
    {
        var top = new TopDownCamera { Lag = 0f };
        top.Update(Vector3.Zero, Tick);
        top.Update(new Vector3(10f, 0f, 0f), Tick);
        Assert.Equal(5f, top.At(0.5f).Target.X, 3);
    }

    [Fact]
    public void Rigs_allocate_nothing()
    {
        var boom = new BoomCamera();
        var eyes = new FirstPersonCamera();
        var iso = new IsometricCamera();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            boom.Turn(Vector2.One * 0.01f);
            boom.Update(Vector3.One, Tick, blockedAt: 2f);
            eyes.Update(Vector3.One);
            iso.Update(Vector3.One, Tick);
            _ = boom.At(0.5f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
