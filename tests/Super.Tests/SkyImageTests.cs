using System.Globalization;
using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public sealed class SkyImageTests
{
    [Fact]
    public void A_sky_reads_its_suns_direction_and_naming_it_again_gives_the_same_one()
    {
        // A dim sky with one blazing texel, in column 32 and row 8 of 64 by 32. Its centre is 47.8 degrees from
        // straight up and 2.8 degrees to the right of -Z: (0.0364, 0.6716, -0.7401).
        var files = new MemoryContent().Add("skies/sun.hdr",
            TestHdr.Make(64, 32, (x, y) => x == 32 && y == 8 ? new Vector3(1000f) : new Vector3(0.1f)));
        var content = new Content(files);

        var sky = content.Sky("skies/sun.hdr");

        Assert.Equal("skies/sun.hdr", sky.Name);
        Assert.True(sky.HasSun);
        Assert.True(Vector3.Distance(new Vector3(0.0364f, 0.6716f, -0.7401f), sky.SunDirection) < 1e-3f, $"The sun is towards {sky.SunDirection}.");
        Assert.Same(sky, content.Sky("skies/sun.hdr"));
    }

    [Fact]
    public void A_sky_without_a_sun_says_so()
    {
        var content = new Content(new MemoryContent().Add("skies/grey.hdr", TestHdr.Make(64, 32, new Vector3(0.5f))));

        var sky = content.Sky("skies/grey.hdr");

        Assert.False(sky.HasSun);
        Assert.Equal(Vector3.Zero, sky.SunDirection);
    }

    [Fact]
    public void A_sky_name_that_is_not_cooked_stops_at_once()
    {
        var content = new Content(new MemoryContent());

        var problem = Assert.Throws<ContentException>(() => content.Sky("skies/meadow.hdr"));

        Assert.Equal("skies/meadow.hdr isn't in the cooked content. Check the name, including its case and extension.", problem.Message);
    }

    [Fact]
    public void The_sun_turns_with_the_sky_counterclockwise_seen_from_above()
    {
        var content = new Content(new MemoryContent().Add("skies/sun.hdr",
            TestHdr.Make(64, 32, (x, y) => x == 32 && y == 8 ? new Vector3(1000f) : new Vector3(0.1f))));
        var sky = content.Sky("skies/sun.hdr");

        // A quarter turn takes -Z to -X: (0.0364, 0.6716, -0.7401) goes to (-0.7401, 0.6716, -0.0364).
        var turned = sky.SunDirectionAt(MathF.PI / 2f);

        Assert.True(Vector3.Distance(new Vector3(-0.7401f, 0.6716f, -0.0364f), turned) < 1e-3f, $"The turned sun is towards {turned}.");
    }

    [Fact]
    public void The_image_its_rotation_and_exposure_reach_the_frame()
    {
        var content = new Content(new MemoryContent().Add("skies/grey.hdr", TestHdr.Make(64, 32, new Vector3(0.5f))));
        var image = content.Sky("skies/grey.hdr");
        var view = new View();
        view.Sky(new SkySettings { Image = image, Rotation = 0.75f, Exposure = -1.5f });

        var sky = SceneRenderer.FrameSky(view, RenderEffects.None, named => new SkyImageHandle(named == image ? 7 : -1));

        Assert.Equal(new SkyImageHandle(7), sky.Image);
        Assert.Equal(0.75f, sky.Rotation);
        Assert.Equal(-1.5f, sky.Exposure);
    }

    [Theory]
    [InlineData(float.NaN, 0f, "NaN and 0")]
    [InlineData(0f, 20.5f, "0 and 20.5")]
    [InlineData(0f, float.NegativeInfinity, "0 and -Infinity")]
    public void A_rotation_that_is_not_a_number_or_an_exposure_past_twenty_stops_is_refused(float rotation, float exposure, string shown)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                view.Sky(new SkySettings { Rotation = rotation, Exposure = exposure }));

            Assert.Equal("settings", error.ParamName);
            Assert.StartsWith(
                "The sky image's rotation must be a number and its exposure from -20 to 20 stops, but they are " + shown + ".",
                error.Message);
            Assert.Null(view.SkySettings);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
