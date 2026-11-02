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
