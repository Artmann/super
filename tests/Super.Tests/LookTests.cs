using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

// How a look is laid over the game's own settings, blended and picked by name. How the retro pixels look is the GPU's,
// so it's checked in RetroGpuTests.
public class LookTests
{
    private static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    [Fact]
    public void The_natural_look_leaves_the_games_finish_as_it_is()
    {
        var game = new PostSettings
        {
            Exposure = 0.5f,
            Contrast = 0.2f,
            Saturation = -0.3f,
            Vignette = 0.4f,
            Sharpen = 0.3f,
            Grain = 0.02f,
            Lift = new Vector3(0.01f, 0.02f, 0.03f),
            Tonemapper = Tonemapper.Aces,
            Grade = new ColorGrade { HueShift = 12f, SplitBalance = 0.2f, Lut = new ColorLut(4), LutBlend = 0.7f },
        };

        Assert.Equal(game, LookLayer.Finish(game, Looks.Natural));
    }

    [Fact]
    public void A_looks_finish_adds_to_the_games_and_stays_in_range()
    {
        var game = new PostSettings { Contrast = 0.1f, Vignette = 0.8f, Saturation = -0.8f, Sharpen = 0.3f, Tonemapper = Tonemapper.Aces };
        var look = new ArtLook { Finish = new PostSettings { Contrast = 0.2f, Vignette = 0.5f, Saturation = -1f, Sharpen = -0.4f } };

        var finish = LookLayer.Finish(game, look);

        Assert.Equal(0.3f, finish.Contrast, 5);
        Assert.Equal(1f, finish.Vignette);
        Assert.Equal(-1f, finish.Saturation);
        Assert.Equal(0f, finish.Sharpen);
        Assert.Equal(Tonemapper.Aces, finish.Tonemapper);
    }

    [Fact]
    public void A_looks_colour_table_replaces_the_games_and_without_one_the_games_stays()
    {
        var game = new PostSettings { Grade = new ColorGrade { Lut = new ColorLut(1), LutBlend = 0.3f } };
        var withTable = new ArtLook { Finish = new PostSettings { Grade = new ColorGrade { Lut = new ColorLut(2), LutBlend = 0.9f } } };

        var replaced = LookLayer.Finish(game, withTable).Grade;
        var kept = LookLayer.Finish(game, Looks.Noir).Grade;

        Assert.Equal(new ColorLut(2), replaced.Lut);
        Assert.Equal(0.9f, replaced.LutBlend);
        Assert.Equal(new ColorLut(1), kept.Lut);
        Assert.Equal(0.3f, kept.LutBlend);
    }

    [Fact]
    public void A_looks_paint_leaves_the_games_when_it_paints_nothing_and_replaces_it_at_full_amount()
    {
        var game = new PaintSettings { Amount = 0.5f, BrushSize = 4f };

        Assert.Equal(game, LookLayer.Paint(game, Looks.Noir));
        Assert.Equal(Looks.Painterly.Paint, LookLayer.Paint(game, Looks.Painterly));
    }

    [Fact]
    public void A_painting_look_easing_in_grows_from_the_games_paint()
    {
        var game = new PaintSettings { Amount = 0.5f, BrushSize = 4f };
        var look = new ArtLook { Paint = new PaintSettings { Amount = 1f, BrushSize = 10f, Sharpness = 0.7f } };

        var start = LookLayer.Paint(game, ArtLook.Lerp(Looks.Natural, look, 0.01f));
        var half = LookLayer.Paint(game, ArtLook.Lerp(Looks.Natural, look, 0.5f));
        var alone = LookLayer.Paint(default, ArtLook.Lerp(Looks.Natural, look, 0.3f));

        // Just started, it is nearly the game's: 0.99 of 0.5 and 0.01 of 0.01, and 0.99 of 4 and 0.01 of 10.
        Assert.Equal(0.495f, start.Amount, 3);
        Assert.Equal(4.06f, start.BrushSize, 3);
        // Halfway, the look's paint is half there and weighs half: 0.5 of 0.5 and 0.5 of 0.5, and a brush of 7.
        Assert.Equal(0.5f, half.Amount, 5);
        Assert.Equal(7f, half.BrushSize, 5);
        Assert.Equal(look.Paint with { Amount = 0.3f }, alone);
    }

    [Fact]
    public void A_warmer_sun_keeps_its_brightness_and_each_stop_doubles_it()
    {
        var white = Vector3.One;

        var warm = LookLayer.Sun(white, new LookLight { SunWarmth = 1f });
        var cold = LookLayer.Sun(white, new LookLight { SunWarmth = -1f });
        var brighter = LookLayer.Sun(new Vector3(1f, 2f, 3f), new LookLight { Sun = 1f });

        Assert.Equal(1f, Vector3.Dot(warm, Luma), 4);
        Assert.Equal(1f, Vector3.Dot(cold, Luma), 4);
        Assert.True(warm.X > warm.Z, $"A warm sun should be redder than it is blue, but it is {warm}.");
        Assert.True(cold.Z > cold.X, $"A cold sun should be bluer than it is red, but it is {cold}.");
        Assert.Equal(new Vector3(2f, 4f, 6f), brighter);
    }

    [Fact]
    public void A_looks_sky_fog_and_flare_change_the_games()
    {
        var light = new LookLight { Sky = 1f, Fog = 0.01f, Flare = -1f };

        Assert.Equal(4f, LookLayer.Sky(2f, light));
        Assert.Equal(0.012f, LookLayer.Fog(new FogSettings(0.002f), light).Density, 6);
        Assert.Equal(0f, LookLayer.Fog(new FogSettings(0.002f), new LookLight { Fog = -1f }).Density);
        Assert.Equal(0.25f, LookLayer.Flare(new LensFlareSettings { Intensity = 0.5f }, light).Intensity!.Value, 6);
        Assert.Equal(0.5f, LookLayer.Flare(default, light).Intensity!.Value, 6);
    }

    [Fact]
    public void A_looks_clouds_add_cover_and_a_clear_sky_gets_fair_weather_ones()
    {
        var cloudier = new LookLight { Clouds = 0.4f };

        Assert.Null(LookLayer.Clouds(null, cloudier));
        Assert.Equal(0.78f, LookLayer.Clouds(CloudSettings.Fair, cloudier)!.Value.Coverage, 5);
        Assert.Equal(1f, LookLayer.Clouds(CloudSettings.Fair, new LookLight { Clouds = 2f })!.Value.Coverage);

        var clear = LookLayer.Clouds(new CloudSettings(), cloudier)!.Value;
        Assert.Equal(CloudSettings.Fair with { Coverage = 0.4f }, clear);
    }

    [Fact]
    public void Clouds_easing_into_a_clear_sky_grow_their_cirrus_from_nothing()
    {
        var look = new ArtLook { Light = new LookLight { Clouds = 0.76f } };

        var start = LookLayer.Clouds(CloudSettings.Clear, ArtLook.Lerp(Looks.Natural, look, 0.001f).Light)!.Value;
        var quarter = LookLayer.Clouds(CloudSettings.Clear, ArtLook.Lerp(Looks.Natural, look, 0.25f).Light)!.Value;
        var full = LookLayer.Clouds(CloudSettings.Clear, look.Light)!.Value;

        Assert.True(start.Cirrus < 0.001f, $"Just as the look eases in, the clear sky already has cirrus {start.Cirrus}.");
        // A quarter of the way the cover is 0.19, half fair weather's 0.38, so the cirrus is half its 0.35.
        Assert.Equal(0.19f, quarter.Coverage, 5);
        Assert.Equal(0.175f, quarter.Cirrus, 5);
        Assert.Equal(0.35f, full.Cirrus, 5);
    }

    [Fact]
    public void A_blend_is_exact_at_its_ends_and_changes_name_halfway()
    {
        Assert.Equal(Looks.Noir, ArtLook.Lerp(Looks.Noir, Looks.Candy, 0f));
        Assert.Equal(Looks.Candy, ArtLook.Lerp(Looks.Noir, Looks.Candy, 1f));
        Assert.Equal("Noir", ArtLook.Lerp(Looks.Noir, Looks.Candy, 0.49f).Name);
        Assert.Equal("Candy", ArtLook.Lerp(Looks.Noir, Looks.Candy, 0.5f).Name);

        var half = ArtLook.Lerp(Looks.Natural, Looks.Noir, 0.5f);
        Assert.Equal(-0.5f, half.Finish.Saturation, 5);
        Assert.Equal(0.15f, half.Light.Sun, 5);
    }

    [Fact]
    public void Paint_only_one_side_has_grows_in_with_that_sides_brush()
    {
        var brush = new PaintSettings { Amount = 1f, BrushSize = 8f, Sharpness = 0.4f };

        var half = LookMix.Paint(default, brush, 0.5f);

        Assert.Equal(brush with { Amount = 0.5f }, half);
    }

    [Fact]
    public void Retro_pixels_only_one_side_has_grow_from_one_pixel_and_every_shade()
    {
        var psx = new RetroSettings { PixelSize = 4f, Levels = 32, Dither = 1f };

        var start = LookMix.Retro(default, psx, 0f);
        var half = LookMix.Retro(default, psx, 0.5f);

        // From none, 1 pixel and all 256 levels, halfway to 4 pixels and 32 levels.
        Assert.Equal(Vector4.Zero, start.Pack(RenderEffects.None));
        Assert.Equal(2.5f, half.PixelSize);
        Assert.Equal(144, half.Levels);
        Assert.Equal(1f, half.Dither);
    }

    [Fact]
    public void Retro_settings_pack_as_shares_of_the_height_and_steps()
    {
        var psx = new RetroSettings { PixelSize = 4f, Levels = 32, Dither = 1f };

        Assert.Equal(new Vector4(4f / 1080f, 31f, 1f, 0f), psx.Pack(RenderEffects.None));
        Assert.Equal(new Vector4(32f / 1080f, 1f, 0.5f, 0f), new RetroSettings { PixelSize = 100f, Levels = 1, Dither = 0.5f }.Pack(RenderEffects.None));
        Assert.Equal(Vector4.Zero, new RetroSettings { PixelSize = 1f, Dither = 1f }.Pack(RenderEffects.None));
        Assert.Equal(Vector4.Zero, psx.Pack(RenderEffects.Retro));
    }

    [Theory]
    [InlineData("golden-hour")]
    [InlineData("Golden Hour")]
    [InlineData("GOLDEN_HOUR")]
    [InlineData("goldenhour")]
    public void A_look_is_found_by_name_ignoring_case_spaces_and_dashes(string name)
    {
        Assert.Equal(Looks.GoldenHour, Looks.Named(name));
    }

    [Fact]
    public void An_unknown_look_name_lists_the_looks()
    {
        var error = Assert.Throws<ArgumentException>(() => Looks.Named("sepia"));

        Assert.Equal(
            "There is no look called 'sepia'. The looks are natural, painterly, candy, golden-hour, noir, horror, vhs, psx. (Parameter 'name')",
            error.Message);
    }

    [Fact]
    public void Every_built_in_look_has_its_own_name_and_passes_the_checks()
    {
        var style = new ArtStyle(new GameTime(30));

        foreach (var look in Looks.All)
        {
            style.Use(look);
        }

        Assert.Equal(Looks.All.Count, Looks.All.Select(look => look.Name).Distinct().Count());
    }

    [Fact]
    public void The_look_argument_picks_a_look()
    {
        Assert.Equal(Looks.GoldenHour, GameArguments.Parse(["--look", "golden-hour"]).Look);
        Assert.Null(GameArguments.Parse([]).Look);
    }

    [Fact]
    public void An_unknown_look_argument_lists_the_names()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--look", "sepia"]));

        Assert.Equal("--look needs one of natural, painterly, candy, golden-hour, noir, horror, vhs, psx, not 'sepia'.", error.Message);
    }

    [Fact]
    public void A_look_without_a_blend_shows_on_the_next_frame()
    {
        var time = new GameTime(30);
        var style = new ArtStyle(time);
        var view = new View();

        style.Use(Looks.Psx);
        Draw(style, view, time, tick: 1);

        Assert.Equal(Looks.Psx, view.Effects.Look.Style);
        Assert.Equal(Looks.Psx, style.Current);
    }

    [Fact]
    public void A_blend_eases_over_its_seconds_and_a_new_one_starts_from_what_is_on_screen()
    {
        var time = new GameTime(30);
        var style = new ArtStyle(time);
        var view = new View();

        style.Use(Looks.Noir, seconds: 1f);
        // Half a second in, the eased share is also a half.
        Draw(style, view, time, tick: 16);
        Assert.Equal(-0.5f, view.Effects.Look.Style.Finish.Saturation, 4);

        // Turning back starts from the half-grey picture on screen, not from the full Noir it was heading for.
        style.Use(Looks.Natural, seconds: 1f);
        Draw(style, view, time, tick: 16);
        Assert.Equal(-0.5f, view.Effects.Look.Style.Finish.Saturation, 4);

        Draw(style, view, time, tick: 47);
        Assert.Equal(Looks.Natural, view.Effects.Look.Style);
    }

    [Fact]
    public void A_look_out_of_range_is_refused_with_its_number_written_the_same_in_any_language()
    {
        var style = new ArtStyle(new GameTime(30));
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            var dither = Assert.Throws<ArgumentOutOfRangeException>(
                () => style.Use(Looks.Psx with { Retro = Looks.Psx.Retro with { Dither = 1.5f } }));
            var levels = Assert.Throws<ArgumentOutOfRangeException>(
                () => style.Use(Looks.Psx with { Retro = Looks.Psx.Retro with { Levels = 1 } }));
            var seconds = Assert.Throws<ArgumentOutOfRangeException>(() => style.Use(Looks.Noir, seconds: -0.5f));
            var light = Assert.Throws<ArgumentOutOfRangeException>(
                () => style.Use(Looks.Noir with { Light = new LookLight { Sky = float.NaN } }));

            Assert.Equal("A look's dither must be from 0 to 1, but it is 1.5. (Parameter 'look')", dither.Message);
            Assert.Equal("A look's levels must be from 2 to 256, or 0 to keep every shade, but it is 1. (Parameter 'look')", levels.Message);
            Assert.Equal("A look's blend must take 0 seconds or more, but it is -0.5. (Parameter 'seconds')", seconds.Message);
            Assert.Equal("Every part of a look's light must be a finite number. (Parameter 'look')", light.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static void Draw(ArtStyle style, View view, GameTime time, long tick)
    {
        time.Tick = tick;
        view.Begin(0f, tick, time.TickSeconds);
        ((IDrawHook)style).AfterDraw(view);
    }
}
