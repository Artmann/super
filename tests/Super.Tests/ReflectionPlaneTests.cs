using Rendition;

namespace Super.Tests;

/// <summary>
/// Where the planar reflection's plane comes from: the view's own plane, a MirrorPlane component or the puddles, and
/// which of them wins, and what turns it off.
/// </summary>
public class ReflectionPlaneTests
{
    private static FrameView Frame(float? plane, float? puddles) => new()
    {
        Weather = new WeatherSettings { Puddles = puddles is { } level ? new PuddleSettings(level) : null },
        Extras = new FrameExtras { ReflectionPlane = plane is { } height ? new ReflectionPlane(height) : null },
    };

    [Fact]
    public void A_frame_with_no_plane_and_no_puddles_mirrors_nothing()
    {
        Assert.Null(PlanarStage.PlaneHeight(Frame(null, null), RenderEffects.None));
    }

    [Fact]
    public void Puddles_alone_mirror_at_their_level()
    {
        Assert.Equal(-0.045f, PlanarStage.PlaneHeight(Frame(null, -0.045f), RenderEffects.None));
    }

    [Fact]
    public void The_views_own_plane_wins_over_the_puddles()
    {
        Assert.Equal(1.5f, PlanarStage.PlaneHeight(Frame(1.5f, -0.045f), RenderEffects.None));
    }

    [Fact]
    public void Puddles_switched_off_set_no_plane_but_an_own_plane_stays()
    {
        Assert.Null(PlanarStage.PlaneHeight(Frame(null, -0.045f), RenderEffects.Puddles));
        Assert.Equal(1.5f, PlanarStage.PlaneHeight(Frame(1.5f, -0.045f), RenderEffects.Puddles));
    }

    [Fact]
    public void Planar_reflections_switched_off_mirror_nothing_even_with_a_plane()
    {
        Assert.Null(PlanarStage.PlaneHeight(Frame(1.5f, -0.045f), RenderEffects.PlanarReflections));
    }

    [Fact]
    public void The_plane_reaches_the_view_and_a_new_frame_starts_without_one()
    {
        var view = new View();
        view.Begin(0f);
        Assert.Null(view.Surroundings.ReflectionPlaneSettings);

        view.Surroundings.ReflectionPlane(0.25f);
        Assert.Equal(new ReflectionPlane(0.25f), view.Surroundings.ReflectionPlaneSettings);

        view.Begin(0f);
        Assert.Null(view.Surroundings.ReflectionPlaneSettings);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_height_that_is_not_a_finite_number_is_refused(float height)
    {
        var view = new View();
        view.Begin(0f);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => view.Surroundings.ReflectionPlane(height));

        Assert.StartsWith("The reflection plane's height must be a number of metres.", error.Message);
        Assert.Equal("height", error.ParamName);
    }

    [Fact]
    public void The_newest_mirror_plane_component_sets_the_frames_plane()
    {
        var world = new World();
        var view = new View();
        world.Spawn(new MirrorPlane(0.1f));
        var pool = world.Spawn(new MirrorPlane(2f));

        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(new ReflectionPlane(2f), view.Surroundings.ReflectionPlaneSettings);

        world.Destroy(pool);
        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(new ReflectionPlane(0.1f), view.Surroundings.ReflectionPlaneSettings);
    }
}
