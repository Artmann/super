using System.Numerics;

namespace Super.Tests;

public class ViewTests
{
    [Fact]
    public void Begin_forgets_the_last_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.Model(1, ModelRef.Cube, Matrix4x4.Identity);
        view.Sun(Vector3.UnitY, Vector3.One);

        view.Begin(0f);

        Assert.Equal(0, view.Drawn.Length);
        Assert.Equal(0, view.Suns.Length);
    }

    [Fact]
    public void Sky_is_kept_until_the_next_frame()
    {
        var view = new View();
        view.Begin(0f);
        var sky = new SkySettings { Zenith = new Vector3(0.2f, 0.4f, 0.9f), Horizon = Vector3.One, HazeStart = 10f, HazeEnd = 40f };

        view.Sky(sky);
        var kept = view.SkySettings;
        view.Begin(0f);

        Assert.Equal(sky, kept);
        Assert.Null(view.SkySettings);
    }

    [Fact]
    public void Sky_with_negative_brightness_is_explained()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Sky(new SkySettings { Brightness = -1f }));

        Assert.Contains("brightness can't be negative", error.Message);
    }

    [Theory]
    [InlineData(-5f, 10f)]
    [InlineData(30f, 10f)]
    public void Sky_with_haze_that_ends_before_it_starts_is_explained(float start, float end)
    {
        var view = new View();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Sky(new SkySettings { HazeStart = start, HazeEnd = end }));

        Assert.Contains($"runs from {start} to {end} metres", error.Message);
    }

    [Fact]
    public void Model_records_what_it_was_given()
    {
        var view = new View();
        var transform = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var red = new Vector3(1f, 0f, 0f);
        view.Begin(0f);

        view.Model(7, ModelRef.Cube, transform, red);

        Assert.Equal(new ModelDraw(7, ModelRef.Cube, transform, red), view.Drawn[0]);
    }

    [Fact]
    public void Model_with_a_pose_records_how_far_past_the_tick_the_display_is()
    {
        var view = new View();
        var pose = new AnimationPose(Animation.TestRig.Rig());
        view.Begin(0.5f, tick: 3, tickSeconds: 0.1f);

        view.Model(7, ModelRef.Cube, Matrix4x4.Identity, pose);

        Assert.Same(pose, view.Drawn[0].Pose);
        Assert.Equal(0.05f, view.Drawn[0].PoseAhead, 0.0001f);
    }

    [Fact]
    public void A_pose_moves_on_between_ticks_by_the_scaled_time()
    {
        var view = new View();
        var pose = new AnimationPose(Animation.TestRig.Rig());
        view.Begin(0.5f, tick: 3, tickSeconds: 0.1f, timeScale: 0.25f);

        view.Model(7, ModelRef.Cube, Matrix4x4.Identity, pose);

        Assert.Equal(0.0125f, view.Drawn[0].PoseAhead, 0.0001f);
    }

    [Fact]
    public void Model_with_an_animator_draws_its_rigs_model_in_its_pose()
    {
        var view = new View();
        var rig = Animation.TestRig.Rig();
        rig.Model = new ModelRef(7);
        var animator = new AnimationController<Still, Still>(rig).State(Still.Only, rig.Clip("idle")).Start(Still.Only);
        var red = new Vector3(1f, 0f, 0f);
        view.Begin(0f);

        view.Model(3, animator, Matrix4x4.Identity, red);

        Assert.Equal(new ModelRef(7), view.Drawn[0].Model);
        Assert.Same(animator.Pose, view.Drawn[0].Pose);
        Assert.Equal(red, view.Drawn[0].Color);
    }

    [Fact]
    public void Model_with_an_animator_whose_rig_has_no_model_is_explained()
    {
        var view = new View();
        var rig = Animation.TestRig.Rig();
        var animator = new AnimationController<Still, Still>(rig).State(Still.Only, rig.Clip("idle")).Start(Still.Only);
        view.Begin(0f);

        var problem = Assert.Throws<InvalidOperationException>(() => view.Model(3, animator, Matrix4x4.Identity));

        Assert.Equal("The animator's rig wasn't read with content.Rig, so it has no model. Pass the model and the animator's Pose instead.", problem.Message);
    }

    private enum Still
    {
        Only,
    }

    [Fact]
    public void Model_without_a_pose_has_none()
    {
        var view = new View();
        view.Begin(0.5f, tick: 3, tickSeconds: 0.1f);

        view.Model(7, ModelRef.Cube, Matrix4x4.Identity);

        Assert.Null(view.Drawn[0].Pose);
        Assert.Equal(0f, view.Drawn[0].PoseAhead);
    }

    [Fact]
    public void Sun_directions_become_one_unit_long()
    {
        var view = new View();
        view.Begin(0f);

        view.Sun(new Vector3(0f, 5f, 0f), Vector3.One);

        Assert.Equal(Vector3.UnitY, view.Suns[0].Direction);
    }

    [Fact]
    public void Smooth_blends_between_ticks()
    {
        var view = new View();
        view.Begin(0.25f);

        var smooth = view.Smooth(Matrix4x4.CreateTranslation(0f, 0f, 0f), Matrix4x4.CreateTranslation(4f, 0f, 0f));

        Assert.Equal(1f, smooth.Translation.X, 0.0001f);
    }
}
