using System.Numerics;

namespace Super.Tests;

public class CutsceneTests
{
    private const float Tolerance = 0.0001f;

    private static Camera At(float x) => new(new Vector3(x, 0f, 0f), new Vector3(x, 0f, -1f));

    [Theory]
    [InlineData(Easing.Linear, 0.25f, 0.25f)]
    [InlineData(Easing.In, 0.5f, 0.125f)]
    [InlineData(Easing.Out, 0.5f, 0.875f)]
    [InlineData(Easing.InOut, 0.25f, 0.0625f)]
    [InlineData(Easing.InOut, 0.75f, 0.9375f)]
    [InlineData(Easing.InOut, 1.5f, 1f)]
    [InlineData(Easing.Out, -1f, 0f)]
    public void Easing_shapes_a_move(Easing easing, float amount, float expected)
    {
        Assert.Equal(expected, Ease.Apply(easing, amount), Tolerance);
    }

    [Fact]
    public void Move_slides_between_two_cameras()
    {
        var shot = Shot.Move(2f, At(0f), At(10f), Easing.Linear);

        Assert.Equal(5f, shot.CameraAt(1f).Position.X, Tolerance);
    }

    [Fact]
    public void Move_eases_by_default()
    {
        var shot = Shot.Move(2f, At(0f), At(10f));

        // A quarter of the way in time is a sixteenth of the way along, eased in and out.
        Assert.Equal(0.625f, shot.CameraAt(0.5f).Position.X, Tolerance);
    }

    [Fact]
    public void Along_rides_a_rail_looking_ahead()
    {
        var rail = Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f)]);
        var shot = Shot.Along(2f, rail, easing: Easing.Linear);

        var halfway = shot.CameraAt(1f);

        Assert.Equal(5f, halfway.Position.X, 1e-3f);
        Assert.Equal(6f, halfway.Target.X, 1e-3f);
        Assert.Equal(10f, shot.CameraAt(2f).Position.X, 1e-3f);
    }

    [Fact]
    public void Along_looks_at_its_subject_when_it_has_one()
    {
        var rail = Spline.Through([Vector3.Zero, new Vector3(10f, 0f, 0f)]);
        var subject = new Vector3(3f, 0f, 5f);
        var shot = Shot.Along(2f, rail, () => subject, look: new Vector3(0f, 1f, 0f), easing: Easing.Linear);

        var halfway = shot.CameraAt(1f);

        Assert.Equal(new Vector3(3f, 1f, 5f), halfway.Target);
        Assert.Equal(5f, halfway.Position.X, 1e-3f);
    }

    [Fact]
    public void Track_rides_along_with_its_subject()
    {
        var subject = new Vector3(5f, 0f, 0f);
        var shot = Shot.Track(1f, () => subject, from: new Vector3(0f, 1f, 4f), to: new Vector3(0f, 1f, 2f), look: new Vector3(0f, 1f, 0f), easing: Easing.Linear);

        var halfway = shot.CameraAt(0.5f);
        subject = new Vector3(8f, 0f, 0f);
        var later = shot.CameraAt(0.5f);

        Assert.Equal(new Vector3(5f, 1f, 3f), halfway.Position);
        Assert.Equal(new Vector3(5f, 1f, 0f), halfway.Target);
        Assert.Equal(new Vector3(8f, 1f, 3f), later.Position);
    }

    [Fact]
    public void Orbit_circles_from_plus_z_towards_plus_x()
    {
        var shot = Shot.Orbit(1f, () => Vector3.Zero, radius: 2f, height: 1f, fromAngle: 0f, toAngle: MathF.PI / 2f, easing: Easing.Linear);

        var start = shot.CameraAt(0f).Position;
        var end = shot.CameraAt(1f).Position;

        Assert.Equal(0f, Vector3.Distance(new Vector3(0f, 1f, 2f), start), Tolerance);
        Assert.Equal(0f, Vector3.Distance(new Vector3(2f, 1f, 0f), end), Tolerance);
    }

    [Fact]
    public void Shots_play_one_after_another()
    {
        var scene = new Cutscene().Then(Shot.Hold(1f, At(1f))).Then(Shot.Hold(2f, At(2f)));

        Assert.Equal(3f, scene.Seconds);
        Assert.Equal((0, 0.5f), scene.ShotAt(0.5f));
        Assert.Equal((1, 0.5f), scene.ShotAt(1.5f));
        Assert.Equal(2f, scene.CameraAt(1.5f).Position.X);
        Assert.Equal((1, 2f), scene.ShotAt(9f));
    }

    [Fact]
    public void Moments_run_once_when_their_time_is_passed()
    {
        var ran = new List<string>();
        var scene = new Cutscene()
            .Then(Shot.Hold(1f, At(0f)))
            .At(0.5f, () => ran.Add("a"))
            .At(0.5f, () => ran.Add("b"))
            .At(0.8f, () => ran.Add("c"));

        scene.Cue(0.4f, 0.5f);
        scene.Cue(0.5f, 0.6f);

        Assert.Equal(["a", "b"], ran);
    }

    [Fact]
    public void Slow_motion_eases_in_holds_and_eases_out()
    {
        var scene = new Cutscene().Then(Shot.Hold(3f, At(0f))).Slow(0.5f, 1.5f, 0.25f, ramp: 0.25f);

        Assert.Equal(1f, scene.ScaleAt(0.4f));
        // A quarter up the ramp is eased to a sixteenth of the way from 1 to 0.25.
        Assert.Equal(0.953125f, scene.ScaleAt(0.5625f), Tolerance);
        Assert.Equal(0.625f, scene.ScaleAt(0.625f), Tolerance);
        Assert.Equal(0.25f, scene.ScaleAt(1f), Tolerance);
        Assert.Equal(0.625f, scene.ScaleAt(1.375f), Tolerance);
        Assert.Equal(1f, scene.ScaleAt(1.5f));
    }

    [Fact]
    public void A_shot_with_no_length_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => Shot.Hold(0f, At(0f)));

        Assert.StartsWith("A shot must last more than zero seconds.", problem.Message);
    }

    [Fact]
    public void Slow_motion_whose_ramps_overlap_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => new Cutscene().Slow(1f, 1.4f, 0.5f, ramp: 0.25f));

        Assert.StartsWith("The ramps at each end must fit inside the slow motion.", problem.Message);
    }

    [Fact]
    public void Slow_motion_that_ends_before_it_starts_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => new Cutscene().Slow(2f, 1f, 0.5f));

        Assert.StartsWith("Slow motion must end after it starts, at 2 seconds or later.", problem.Message);
    }

    [Fact]
    public void A_moment_before_the_start_is_refused()
    {
        var problem = Assert.Throws<ArgumentOutOfRangeException>(() => new Cutscene().At(-1f, () => { }));

        Assert.StartsWith("A moment must be at least zero seconds in.", problem.Message);
    }
}
