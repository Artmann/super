using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class LensFlareTests
{
    private static readonly Vector3 White = Vector3.One;

    [Fact]
    public void Lens_flare_settings_reach_the_frame_as_set()
    {
        var view = new View();
        var elements = new[] { new LensFlareElement(LensFlareShape.Ring, 1.5f, 0.3f, White) };

        view.Lights.LensFlare(new LensFlareSettings { Intensity = 2f, Elements = elements });

        var flare = view.Lights.LensFlareSettings;
        Assert.Equal(2f, flare.Intensity);
        Assert.Equal(elements, flare.Elements.ToArray());
    }

    [Fact]
    public void Flaring_lights_follow_the_settings_own()
    {
        var view = new View();
        var own = new LensFlareLight(new Vector3(1f, 2f, 3f), White);
        view.Lights.FlareLight(new Vector3(4f, 5f, 6f), 2f * White, 0.5f);
        view.Lights.LensFlare(new LensFlareSettings { Lights = new[] { own } });

        var lights = view.Lights.LensFlareSettings.Lights.ToArray();

        Assert.Equal([own, new LensFlareLight(new Vector3(4f, 5f, 6f), 2f * White, 0.5f)], lights);
    }

    [Fact]
    public void A_new_frame_forgets_the_lens_flare_and_its_lights()
    {
        var view = new View();
        view.Lights.LensFlare(new LensFlareSettings { Intensity = 3f });
        view.Lights.FlareLight(Vector3.Zero, White);

        view.Begin(0f);

        Assert.Null(view.Lights.LensFlareSettings.Intensity);
        Assert.True(view.Lights.LensFlareSettings.Lights.IsEmpty);
    }

    [Fact]
    public void A_fourth_flaring_light_is_refused()
    {
        var view = new View();
        view.Lights.LensFlare(new LensFlareSettings { Lights = new[] { new LensFlareLight(Vector3.Zero, White) } });
        view.Lights.FlareLight(Vector3.Zero, White);
        view.Lights.FlareLight(Vector3.Zero, White);

        var error = Assert.Throws<ArgumentException>(() => view.Lights.FlareLight(Vector3.Zero, White));

        Assert.Equal("position", error.ParamName);
        Assert.StartsWith("At most 3 lights flare besides the sun, but this frame names 4.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_that_bring_too_many_lights_with_those_named_are_refused()
    {
        var view = new View();
        view.Lights.FlareLight(Vector3.Zero, White);
        view.Lights.FlareLight(Vector3.Zero, White);
        var two = new[] { new LensFlareLight(Vector3.Zero, White), new LensFlareLight(Vector3.Zero, White) };

        var error = Assert.Throws<ArgumentException>(() => view.Lights.LensFlare(new LensFlareSettings { Lights = two }));

        Assert.Equal("settings", error.ParamName);
        Assert.StartsWith("At most 3 lights flare besides the sun, but this frame names 4.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_elements_are_refused()
    {
        var elements = Enumerable.Repeat(new LensFlareElement(LensFlareShape.Ghost, 1f, 0.1f, White), 17).ToArray();

        var error = Assert.Throws<ArgumentException>(() => new View().Lights.LensFlare(new LensFlareSettings { Elements = elements }));

        Assert.StartsWith("A lens flare has at most 16 elements, but this one has 17.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("intensity", "The lens flare's intensity must be 0 or more, with 1 as made, but it is -0.5.")]
    [InlineData("size", "A lens flare element's size must be above 0 screen heights, but it is -0.5.")]
    [InlineData("position", "A lens flare element's position must be a number, 0 on the light and 1 in the middle of the screen, but it is NaN.")]
    [InlineData("colour", "A lens flare element's colour must be 0 or more in red, green and blue, but it is <1, -0.5, 1>.")]
    [InlineData("sides", "A lens flare polygon has 3 sides or more, or 0 for six, but this one has 2.")]
    [InlineData("shape", "A lens flare element's shape must be a LensFlareShape, but it is 9.")]
    [InlineData("light colour", "A flaring light's colour must be 0 or more in red, green and blue, but it is <1, -0.5, 1>.")]
    [InlineData("light radius", "A flaring light's radius must be 0 or more metres, but it is -0.5.")]
    [InlineData("light position", "A flaring light must be at a place in the world, but it is at <NaN, 0, 0>.")]
    public void A_setting_that_cant_be_drawn_is_refused_saying_why(string what, string message)
    {
        var ghost = new LensFlareElement(LensFlareShape.Ghost, 1f, 0.1f, White);
        var light = new LensFlareLight(Vector3.Zero, White);
        var settings = what switch
        {
            "intensity" => new LensFlareSettings { Intensity = -0.5f },
            "size" => new LensFlareSettings { Elements = new[] { ghost with { Size = -0.5f } } },
            "position" => new LensFlareSettings { Elements = new[] { ghost with { Position = float.NaN } } },
            "colour" => new LensFlareSettings { Elements = new[] { ghost with { Color = new Vector3(1f, -0.5f, 1f) } } },
            "sides" => new LensFlareSettings { Elements = new[] { ghost with { Shape = LensFlareShape.Polygon, Sides = 2 } } },
            "shape" => new LensFlareSettings { Elements = new[] { ghost with { Shape = (LensFlareShape)9 } } },
            "light colour" => new LensFlareSettings { Lights = new[] { light with { Color = new Vector3(1f, -0.5f, 1f) } } },
            "light radius" => new LensFlareSettings { Lights = new[] { light with { Radius = -0.5f } } },
            _ => new LensFlareSettings { Lights = new[] { light with { Position = new Vector3(float.NaN, 0f, 0f) } } },
        };

        // Under Swedish, so a number written with the culture's decimal comma shows.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().Lights.LensFlare(settings));

            Assert.Equal("settings", error.ParamName);
            Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_flaring_light_with_a_negative_radius_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().Lights.FlareLight(Vector3.Zero, White, -1f));

        Assert.StartsWith("A flaring light's radius must be 0 or more metres, but it is -1.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lens_flare_component_sets_the_frames_lens_flare()
    {
        var world = new World();
        var view = new View();
        var entity = world.Spawn(new LensFlareSettings { Intensity = 0.25f });

        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(0.25f, view.Lights.LensFlareSettings.Intensity);

        world.Get<LensFlareSettings>(entity).Intensity = 0.75f;
        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(0.75f, view.Lights.LensFlareSettings.Intensity);
    }
}
