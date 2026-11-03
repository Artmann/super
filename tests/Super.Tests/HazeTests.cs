using System.Numerics;
using Rendition.Passes;

namespace Super.Tests;

public class HazeTests
{
    private static readonly Vector3 Eye = new(300f, 400f, 300f);

    [Fact]
    public void Without_an_origin_the_haze_is_measured_from_the_camera()
    {
        var (origin, _) = Haze.Pack(Eye, null, Vector3.Zero);

        Assert.Equal(Eye, new Vector3(origin.X, origin.Y, origin.Z));
        Assert.Equal(0f, origin.W);
    }

    [Fact]
    public void With_an_origin_the_haze_is_measured_across_the_ground_from_it()
    {
        var (origin, _) = Haze.Pack(Eye, new Vector3(5f, 0f, -3f), Vector3.Zero);

        Assert.Equal(new Vector4(5f, 0f, -3f, 1f), origin);
    }

    [Fact]
    public void Without_a_colour_things_fade_into_what_is_behind_them()
    {
        var (_, color) = Haze.Pack(Eye, null, Vector3.Zero);

        Assert.Equal(0f, color.W);
    }

    [Fact]
    public void With_a_colour_things_fade_into_that_colour()
    {
        var (_, color) = Haze.Pack(Eye, null, new Vector3(0.8f, 0.7f, 0.9f));

        Assert.Equal(new Vector4(0.8f, 0.7f, 0.9f, 1f), color);
    }

    [Fact]
    public void A_sky_hazing_from_the_focus_measures_from_where_the_camera_looks()
    {
        var shot = new Camera(Eye, new Vector3(10f, 0f, 4f));

        Assert.Equal(new Vector3(10f, 0f, 4f), SceneRenderer.HazeOrigin(new SkySettings { HazeFromFocus = true }, shot));
        Assert.Null(SceneRenderer.HazeOrigin(new SkySettings(), shot));
        Assert.Null(SceneRenderer.HazeOrigin(null, shot));
    }
}
