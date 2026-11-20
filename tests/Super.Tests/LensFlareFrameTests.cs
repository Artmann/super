using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

// Which lights flare and where their elements go, worked out on the CPU each frame. How much of each light is seen is
// the GPU's, so it's checked in LensFlareGpuTests.
public class LensFlareFrameTests
{
    private const float SceneHeight = 720f;

    private static readonly Vector3 White = Vector3.One;

    [Fact]
    public void A_sun_straight_ahead_flares_from_the_middle_with_its_own_colour()
    {
        var view = Looking(sun: -Vector3.UnitZ, radiance: new Vector3(4f, 3f, 2f));
        Span<FlareSource> sources = stackalloc FlareSource[LensFlareSettings.MaxSources];

        int count = LensFlareFrame.Sources(view, Vector4.Zero, SceneHeight, sources);

        Assert.Equal(1, count);
        Assert.Equal(0.5f, sources[0].Uv.X, 4);
        Assert.Equal(0.5f, sources[0].Uv.Y, 4);
        Assert.Equal(0f, sources[0].Depth);
        Assert.Equal(new Vector3(4f, 3f, 2f), sources[0].Color);
    }

    [Fact]
    public void A_sun_behind_the_camera_does_not_flare()
    {
        var view = Looking(sun: Vector3.UnitZ, radiance: White);

        Assert.Equal(0, LensFlareFrame.Sources(view, Vector4.Zero, SceneHeight, stackalloc FlareSource[4]));
    }

    [Fact]
    public void A_sun_off_the_side_of_the_screen_does_not_flare()
    {
        // 60 degrees to the side, past the 45 degree half width of a square 90 degree view.
        var view = Looking(sun: new Vector3(MathF.Sin(MathF.PI / 3f), 0f, -MathF.Cos(MathF.PI / 3f)), radiance: White);

        Assert.Equal(0, LensFlareFrame.Sources(view, Vector4.Zero, SceneHeight, stackalloc FlareSource[4]));
    }

    [Fact]
    public void The_moon_does_not_flare()
    {
        var view = Looking(sun: -Vector3.UnitZ, radiance: White);
        view.Environment = new EnvironmentSettings { Sky = new AnalyticSky { Moon = -Vector3.UnitZ } };

        Assert.Equal(0, LensFlareFrame.Sources(view, Vector4.Zero, SceneHeight, stackalloc FlareSource[4]));
    }

    [Theory]
    [InlineData(0.5f, 0.5f, 1f)]
    [InlineData(0.04f, 0.5f, 0.5f)]
    [InlineData(0.5f, 0.96f, 0.5f)]
    [InlineData(0f, 0.5f, 0f)]
    [InlineData(-0.2f, 0.5f, 0f)]
    [InlineData(0.08f, 0.08f, 1f)]
    public void A_light_fades_out_over_the_last_part_of_the_screen_towards_each_edge(float x, float y, float expected)
    {
        Assert.Equal(expected, LensFlareFrame.Fade(new Vector2(x, y)), 4);
    }

    [Fact]
    public void Fog_between_the_camera_and_a_light_dims_its_flare()
    {
        // A level ray 10 metres through fog of 0.1 a metre with no falloff loses e^-1 of its light.
        var view = Looking(sun: Vector3.Zero, radiance: Vector3.Zero, lights: [new LensFlareLight(new Vector3(0f, 0f, -10f), White)]);
        Span<FlareSource> sources = stackalloc FlareSource[4];

        int count = LensFlareFrame.Sources(view, new Vector4(0.1f, 0f, 0f, 0f), SceneHeight, sources);

        Assert.Equal(1, count);
        Assert.Equal(0.36788f, sources[0].Color.X, 4);
    }

    [Fact]
    public void Fog_dims_the_suns_flare_over_10_kilometres()
    {
        // The sun is taken to shine through 10 km of the fog: level fog of 0.0001 a metre loses e^-1 of its light.
        var view = Looking(sun: -Vector3.UnitZ, radiance: White);
        Span<FlareSource> sources = stackalloc FlareSource[4];

        int count = LensFlareFrame.Sources(view, new Vector4(0.0001f, 0f, 0f, 0f), SceneHeight, sources);

        Assert.Equal(1, count);
        Assert.Equal(0.36788f, sources[0].Color.X, 4);
    }

    [Fact]
    public void A_named_light_flares_with_the_depth_of_its_near_side()
    {
        var near = Looking(sun: Vector3.Zero, radiance: Vector3.Zero, lights: [new LensFlareLight(new Vector3(0f, 0f, -10f), White, 0f)]);
        var wide = Looking(sun: Vector3.Zero, radiance: Vector3.Zero, lights: [new LensFlareLight(new Vector3(0f, 0f, -10f), White, 2f)]);
        Span<FlareSource> sources = stackalloc FlareSource[4];

        LensFlareFrame.Sources(near, Vector4.Zero, SceneHeight, sources);
        float centre = sources[0].Depth;
        LensFlareFrame.Sources(wide, Vector4.Zero, SceneHeight, sources);
        float front = sources[0].Depth;

        // Reversed depth with an infinite far plane and a near plane of 0.1 m: depth is 0.1 over the distance.
        Assert.Equal(0.01f, centre, 5);
        Assert.Equal(0.1f / 8f, front, 5);
    }

    [Fact]
    public void A_named_light_behind_the_camera_or_round_it_does_not_flare()
    {
        var behind = Looking(sun: Vector3.Zero, radiance: Vector3.Zero, lights: [new LensFlareLight(new Vector3(0f, 0f, 5f), White)]);
        var round = Looking(sun: Vector3.Zero, radiance: Vector3.Zero, lights: [new LensFlareLight(new Vector3(0f, 0f, -0.5f), White, 1f)]);

        Assert.Equal(0, LensFlareFrame.Sources(behind, Vector4.Zero, SceneHeight, stackalloc FlareSource[4]));
        Assert.Equal(0, LensFlareFrame.Sources(round, Vector4.Zero, SceneHeight, stackalloc FlareSource[4]));
    }

    [Fact]
    public void The_sun_comes_first_and_named_lights_follow_while_they_fit()
    {
        var lights = new LensFlareLight[] { new(new Vector3(1f, 0f, -10f), White), new(new Vector3(-1f, 0f, -10f), 2f * White) };
        var view = Looking(sun: -Vector3.UnitZ, radiance: 3f * White, lights: lights);
        Span<FlareSource> sources = stackalloc FlareSource[2];

        int count = LensFlareFrame.Sources(view, Vector4.Zero, SceneHeight, sources);

        Assert.Equal(2, count);
        Assert.Equal(0f, sources[0].Depth);
        Assert.Equal(3f, sources[0].Color.X);
        Assert.Equal(1f, sources[1].Color.X);
        Assert.True(sources[1].Uv.X > 0.5f, $"A light to the right sits at {sources[1].Uv.X} across.");
    }

    [Fact]
    public void Elements_lie_on_the_line_from_the_light_through_the_middle()
    {
        var sources = new[] { new FlareSource(new Vector2(0.2f, 0.3f), 0f, 0.01f, White) };
        var settings = new LensFlareSettings
        {
            Elements = new[]
            {
                new LensFlareElement(LensFlareShape.Ghost, 0f, 0.1f, White),
                new LensFlareElement(LensFlareShape.Ghost, 1f, 0.1f, White),
                new LensFlareElement(LensFlareShape.Ghost, 2f, 0.1f, White),
            },
        };
        Span<FlareQuad> quads = stackalloc FlareQuad[LensFlareFrame.MaxQuads];

        int count = LensFlareFrame.Quads(sources, settings, 1f, quads);

        Assert.Equal(3, count);
        AssertNear(new Vector2(0.2f, 0.3f), quads[0].Center);
        AssertNear(new Vector2(0.5f, 0.5f), quads[1].Center);
        AssertNear(new Vector2(0.8f, 0.7f), quads[2].Center);
    }

    [Fact]
    public void An_element_is_round_on_a_wide_screen_and_a_streak_is_thin()
    {
        var sources = new[] { new FlareSource(new Vector2(0.5f), 0f, 0.01f, White) };
        var settings = new LensFlareSettings
        {
            Elements = new[]
            {
                new LensFlareElement(LensFlareShape.Ring, 0f, 0.2f, White),
                new LensFlareElement(LensFlareShape.Streak, 0f, 1f, White),
            },
        };
        Span<FlareQuad> quads = stackalloc FlareQuad[LensFlareFrame.MaxQuads];

        LensFlareFrame.Quads(sources, settings, 2f, quads);

        AssertNear(new Vector2(0.05f, 0.1f), quads[0].HalfSize);
        Assert.Equal(0.25f, quads[1].HalfSize.X, 5);
        Assert.True(quads[1].HalfSize.Y < 0.05f, $"A streak half a screen wide is {quads[1].HalfSize.Y} high.");
    }

    [Fact]
    public void Each_quad_takes_its_lights_colour_the_intensity_and_which_light_it_is()
    {
        var sources = new[]
        {
            new FlareSource(new Vector2(0.3f), 0f, 0.01f, new Vector3(2f, 1f, 0.5f)),
            new FlareSource(new Vector2(0.6f), 0.1f, 0.01f, White),
        };
        var settings = new LensFlareSettings
        {
            Intensity = 0.5f,
            Elements = new[] { new LensFlareElement(LensFlareShape.Ghost, 0.5f, 0.1f, new Vector3(0.2f, 0.4f, 1f)) },
        };
        Span<FlareQuad> quads = stackalloc FlareQuad[LensFlareFrame.MaxQuads];

        int count = LensFlareFrame.Quads(sources, settings, 1f, quads);

        Assert.Equal(2, count);
        Assert.Equal(new Vector4(0.2f, 0.2f, 0.25f, 0f), quads[0].Color);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.5f, 1f), quads[1].Color);
    }

    [Fact]
    public void An_intensity_of_0_draws_nothing()
    {
        var sources = new[] { new FlareSource(new Vector2(0.3f), 0f, 0.01f, White) };

        Assert.Equal(0, LensFlareFrame.Quads(sources, new LensFlareSettings { Intensity = 0f }, 1f, new FlareQuad[LensFlareFrame.MaxQuads]));
    }

    [Fact]
    public void No_elements_given_draws_the_default_set()
    {
        var sources = new[] { new FlareSource(new Vector2(0.3f), 0f, 0.01f, White) };
        var quads = new FlareQuad[LensFlareFrame.MaxQuads];

        int count = LensFlareFrame.Quads(sources, default, 1f, quads);

        var defaults = LensFlareSettings.DefaultElements.Span;
        Assert.Equal(defaults.Length, count);
        Assert.Equal((float)defaults[0].Shape, quads[0].Shape.X);
        Assert.Equal((float)defaults[^1].Shape, quads[count - 1].Shape.X);
    }

    [Fact]
    public void A_polygon_with_no_sides_given_has_six()
    {
        var sources = new[] { new FlareSource(new Vector2(0.3f), 0f, 0.01f, White) };
        var settings = new LensFlareSettings
        {
            Elements = new[]
            {
                new LensFlareElement(LensFlareShape.Polygon, 0.5f, 0.1f, White),
                new LensFlareElement(LensFlareShape.Polygon, 0.5f, 0.1f, White) { Sides = 5 },
            },
        };
        var quads = new FlareQuad[LensFlareFrame.MaxQuads];

        LensFlareFrame.Quads(sources, settings, 1f, quads);

        Assert.Equal(6f, quads[0].Shape.Y);
        Assert.Equal(5f, quads[1].Shape.Y);
    }

    // A square 90 degree view from the origin down -z, with one sun and the named lights given.
    private static FrameView Looking(Vector3 sun, Vector3 radiance, LensFlareLight[]? lights = null) => new()
    {
        Width = 720,
        Height = 720,
        View = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY),
        Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f),
        CameraPosition = Vector3.Zero,
        Lights = sun == Vector3.Zero ? Array.Empty<DirectionalLight>() : new[] { new DirectionalLight { Direction = sun, Radiance = radiance } },
        Picture = new PictureSettings { LensFlare = new LensFlareSettings { Lights = lights ?? [] } },
    };

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.True(Vector2.Distance(expected, actual) < 1e-4f, $"Expected {expected} but got {actual}.");
    }
}
