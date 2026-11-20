using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>The settings the grade's table is baked from, the artist's table, easing between looks and --neutral-lut.</summary>
public class GradeTableTests
{
    [Fact]
    public void The_colour_matrix_of_default_settings_changes_nothing()
    {
        var matrix = PostGrade.ColorMatrix(new PostSettings());

        AssertNear(Matrix4x4.Identity, matrix);
    }

    // A third of a turn round the grey axis moves each channel to the next, exactly.
    [Fact]
    public void A_hue_shift_of_120_degrees_turns_red_into_green_and_green_into_blue()
    {
        var turn = PostGrade.HueRotation(120f);

        AssertNear(new Vector3(0f, 1f, 0f), Vector3.Transform(new Vector3(1f, 0f, 0f), turn));
        AssertNear(new Vector3(0f, 0f, 1f), Vector3.Transform(new Vector3(0f, 1f, 0f), turn));
    }

    [Fact]
    public void A_small_hue_shift_moves_red_towards_yellow_and_leaves_grey_grey()
    {
        var turn = PostGrade.HueRotation(20f);

        var red = Vector3.Transform(new Vector3(1f, 0f, 0f), turn);
        Assert.True(red.Y > red.Z, $"red turned to {red}");
        AssertNear(new Vector3(0.4f), Vector3.Transform(new Vector3(0.4f), turn));
    }

    [Fact]
    public void The_channel_mixer_adds_each_channels_share_on_top_of_its_own()
    {
        var post = new PostSettings { Grade = new() { RedFrom = new Vector3(0f, 0.5f, 0f), BlueFrom = new Vector3(-1f, 0f, 0f) } };

        // Red 0.2 + half of green 0.4 = 0.4; blue 0.6 - all of red 0.2 = 0.4; green is untouched.
        var mixed = Vector3.Transform(new Vector3(0.2f, 0.4f, 0.6f), PostGrade.ColorMatrix(post));

        AssertNear(new Vector3(0.4f, 0.4f, 0.4f), mixed);
    }

    [Fact]
    public void The_tones_are_only_worked_out_when_one_of_them_is_set()
    {
        Assert.Equal(0f, PostGrade.Tone(new PostSettings()).Look.Y);
        Assert.Equal(1f, PostGrade.Tone(new PostSettings { Grade = new() { Midtones = new Vector3(0f, 0f, 0.01f) } }).Look.Y);
        Assert.Equal(1f, PostGrade.Tone(new PostSettings { Grade = new() { SplitHighlights = new Vector3(0.05f, 0f, 0f) } }).Look.Y);
    }

    [Fact]
    public void The_split_balance_is_passed_on_and_held_to_its_range()
    {
        Assert.Equal(0.25f, PostGrade.Tone(new PostSettings { Grade = new() { SplitBalance = 0.25f } }).Look.X);
        Assert.Equal(-1f, PostGrade.Tone(new PostSettings { Grade = new() { SplitBalance = -3f } }).Look.X);
    }

    [Fact]
    public void The_curve_carries_the_tables_blend_only_while_a_table_is_named()
    {
        Assert.Equal(0f, PostGrade.Curve(new PostSettings { Grade = new() { LutBlend = 0.5f } }).W);
        Assert.Equal(0.5f, PostGrade.Curve(new PostSettings { Grade = new() { Lut = new ColorLut(1), LutBlend = 0.5f } }).W);
        Assert.Equal(1f, PostGrade.Curve(new PostSettings { Grade = new() { Lut = new ColorLut(1), LutBlend = 4f } }).W);
    }

    [Fact]
    public void The_curve_numbers_the_neutral_tonemapper_2()
    {
        Assert.Equal(2f, PostGrade.Curve(new PostSettings { Tonemapper = Tonemapper.Neutral }).X);
    }

    [Fact]
    public void Switching_the_colour_table_off_leaves_it_out_and_keeps_the_grade()
    {
        var post = new PostSettings { Grade = new() { Lut = new ColorLut(3), LutBlend = 1f, HueShift = 10f } };

        var without = PostGrade.Without(post, RenderEffects.ColorLut);

        Assert.Null(without.Grade.Lut);
        Assert.Equal(0f, without.Grade.LutBlend);
        Assert.Equal(10f, without.Grade.HueShift);
    }

    [Fact]
    public void Switching_grading_off_clears_every_new_setting_and_keeps_the_table()
    {
        var grade = new ColorGrade
        {
            HueShift = 10f,
            RedFrom = Vector3.One,
            GreenFrom = Vector3.One,
            BlueFrom = Vector3.One,
            Shadows = Vector3.One,
            Midtones = Vector3.One,
            Highlights = Vector3.One,
            SplitShadows = Vector3.One,
            SplitHighlights = Vector3.One,
            SplitBalance = 0.5f,
            Lut = new ColorLut(3),
            LutBlend = 1f,
        };
        var post = new PostSettings { Grade = grade };

        var without = PostGrade.Without(post, RenderEffects.ColorGrading);

        AssertNear(Matrix4x4.Identity, PostGrade.ColorMatrix(without));
        var tone = PostGrade.Tone(without);
        Assert.Equal(0f, tone.Look.Y);
        Assert.Equal(0f, without.Grade.SplitBalance);
        Assert.Equal(new ColorLut(3), without.Grade.Lut);
    }

    [Fact]
    public void A_table_with_no_blend_is_no_table()
    {
        var without = PostGrade.Without(new PostSettings { Grade = new() { Lut = new ColorLut(3) } }, RenderEffects.None);

        Assert.Null(without.Grade.Lut);
    }

    [Fact]
    public void Lerp_goes_evenly_from_one_look_to_the_other()
    {
        var from = new PostSettings { Contrast = 0.2f, Lift = new Vector3(0.1f, 0f, 0f), Grade = new() { HueShift = -10f } };
        var to = new PostSettings { Contrast = 0.6f, Lift = new Vector3(0.3f, 0f, 0f), Grade = new() { HueShift = 30f } };

        var quarter = PostSettings.Lerp(from, to, 0.25f);

        Assert.Equal(0.3f, quarter.Contrast, 5);
        Assert.Equal(0.15f, quarter.Lift.X, 5);
        Assert.Equal(0f, quarter.Grade.HueShift, 5);
    }

    [Fact]
    public void Lerp_keeps_the_ends_exactly_and_holds_t_to_them()
    {
        var from = new PostSettings { Contrast = 0.1f, AdaptationSpeed = 0f };
        var to = new PostSettings { Contrast = 0.7f, AdaptationSpeed = 4f };

        Assert.Equal(from, PostSettings.Lerp(from, to, 0f));
        Assert.Equal(from, PostSettings.Lerp(from, to, -2f));
        Assert.Equal(to, PostSettings.Lerp(from, to, 1f));
        Assert.Equal(to, PostSettings.Lerp(from, to, 5f));
    }

    [Fact]
    public void Lerp_switches_choices_at_the_halfway_point()
    {
        var from = new PostSettings { Tonemapper = Tonemapper.Agx };
        var to = new PostSettings { Tonemapper = Tonemapper.Aces, AutoExposure = true };

        Assert.Equal(Tonemapper.Agx, PostSettings.Lerp(from, to, 0.49f).Tonemapper);
        Assert.False(PostSettings.Lerp(from, to, 0.49f).AutoExposure);
        Assert.Equal(Tonemapper.Aces, PostSettings.Lerp(from, to, 0.5f).Tonemapper);
        Assert.True(PostSettings.Lerp(from, to, 0.5f).AutoExposure);
    }

    // An adaptation speed of 0 stands for the default 2 stops a second, so halfway to 4 is 3, not 2.
    [Fact]
    public void Lerp_eases_a_default_adaptation_speed_from_what_it_stands_for()
    {
        var eased = PostSettings.Lerp(new PostSettings(), new PostSettings { AdaptationSpeed = 4f }, 0.5f);

        Assert.Equal(3f, eased.AdaptationSpeed, 5);
    }

    [Fact]
    public void Lerp_crosses_two_tables_through_none()
    {
        var from = new PostSettings { Grade = new() { Lut = new ColorLut(1), LutBlend = 1f } };
        var to = new PostSettings { Grade = new() { Lut = new ColorLut(2), LutBlend = 0.8f } };

        var early = PostSettings.Lerp(from, to, 0.25f);
        var late = PostSettings.Lerp(from, to, 0.75f);

        Assert.Equal(new ColorLut(1), early.Grade.Lut);
        Assert.Equal(0.5f, early.Grade.LutBlend, 5);
        Assert.Equal(new ColorLut(2), late.Grade.Lut);
        Assert.Equal(0.4f, late.Grade.LutBlend, 5);
    }

    [Fact]
    public void Lerp_fades_the_same_table_by_its_blend()
    {
        var from = new PostSettings { Grade = new() { Lut = new ColorLut(1), LutBlend = 0f } };
        var to = new PostSettings { Grade = new() { Lut = new ColorLut(1), LutBlend = 1f } };

        var eased = PostSettings.Lerp(from, to, 0.25f);

        Assert.Equal(new ColorLut(1), eased.Grade.Lut);
        Assert.Equal(0.25f, eased.Grade.LutBlend, 5);
    }

    // Halfway from no lens to an open one would be the widest f-number of all, so the lens switches instead.
    [Fact]
    public void Lerp_opens_a_lens_from_none_at_the_halfway_point()
    {
        var from = new PostSettings();
        var to = new PostSettings { Aperture = 2f, FocusDistance = 5f };

        Assert.Equal(0f, PostSettings.Lerp(from, to, 0.4f).Aperture);
        Assert.Equal(2f, PostSettings.Lerp(from, to, 0.6f).Aperture);
    }

    [Theory]
    [InlineData(1.5f, 0f, 0f, "The colour table's blend must be from 0, none, to 1, all of it, but it is 1.5.")]
    [InlineData(-0.25f, 0f, 0f, "The colour table's blend must be from 0, none, to 1, all of it, but it is -0.25.")]
    [InlineData(0f, 1.5f, 0f, "The split toning balance must be from -1 to 1, with 0 for the middle, but it is 1.5.")]
    [InlineData(0f, float.NaN, 0f, "The split toning balance must be from -1 to 1, with 0 for the middle, but it is NaN.")]
    [InlineData(0f, 0f, float.PositiveInfinity, "The hue shift must be a number of degrees, but it is Infinity.")]
    public void A_blend_balance_or_hue_shift_out_of_range_is_refused(float blend, float balance, float hue, string message)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var view = new View();

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => view.Effects.Finish(new PostSettings { Grade = new() { LutBlend = blend, SplitBalance = balance, HueShift = hue } }));

            Assert.StartsWith(message, error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_view_keeps_a_full_blend_and_balance_at_the_ends_of_their_ranges()
    {
        var view = new View();

        view.Effects.Finish(new PostSettings { Grade = new() { LutBlend = 1f, SplitBalance = -1f, HueShift = 400f } });

        Assert.Equal(1f, view.Effects.Post.Grade.LutBlend);
        Assert.Equal(-1f, view.Effects.Post.Grade.SplitBalance);
        Assert.Equal(400f, view.Effects.Post.Grade.HueShift);
    }

    [Fact]
    public void The_neutral_strip_lays_red_across_green_down_and_blue_square_by_square()
    {
        var stamped = NeutralLutStrip.Stamped(new byte[1100 * 40 * 4], 1100, 40);

        // x = 33 is the second square's second column: red step 1, blue step 1. y = 2 is green step 2.
        int at = ((2 * 1100) + 33) * 4;
        Assert.Equal([8, 16, 8, 255], stamped[at..(at + 4)]);

        // The last pixel of the strip is white, and the picture past it is left alone.
        int last = ((31 * 1100) + 1023) * 4;
        Assert.Equal([255, 255, 255, 255], stamped[last..(last + 4)]);
        Assert.Equal([0, 0, 0, 0], stamped[(last + 4)..(last + 8)]);
    }

    [Fact]
    public void The_neutral_strip_leaves_the_picture_it_was_given_alone()
    {
        var pixels = new byte[1024 * 32 * 4];

        NeutralLutStrip.Stamped(pixels, 1024, 32);

        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void The_neutral_strip_refuses_a_picture_too_small_for_it()
    {
        var error = Assert.Throws<ArgumentException>(() => NeutralLutStrip.Stamped(new byte[800 * 600 * 4], 800, 600));

        Assert.Equal(
            "The neutral colour table is 1024 by 32 pixels, so it doesn't fit a 800 by 600 picture. Take it at least 1024 pixels wide, such as with --size 1280x720.",
            error.Message);
    }

    [Fact]
    public void Neutral_lut_needs_a_screenshot()
    {
        var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--neutral-lut"]));

        Assert.Equal("--neutral-lut needs --screenshot, since it stamps the neutral colour table on the picture saved.", error.Message);
    }

    [Fact]
    public void Neutral_lut_needs_a_picture_wide_enough_for_the_strip()
    {
        var error = Assert.Throws<ArgumentException>(
            () => GameArguments.Parse(["--neutral-lut", "--screenshot", "grade.png", "--size", "800x600"]));

        Assert.Equal("--neutral-lut needs a picture at least 1024 pixels wide to hold the colour table, but --size asks for 800.", error.Message);
    }

    [Fact]
    public void Neutral_lut_turns_the_colour_table_in_use_off()
    {
        var arguments = GameArguments.Parse(["--neutral-lut", "--screenshot", "grade.png"]);
        var graphics = new Graphics();

        Game.ApplyGraphicsArguments(graphics, arguments, null);

        Assert.True(arguments.NeutralLut);
        Assert.False(graphics.Picture.ColorLut);
        Assert.True(graphics.Picture.ColorGrading);
    }

    private static void AssertNear(Matrix4x4 expected, Matrix4x4 actual) => PostGradeTests.AssertNear(expected, actual);

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");
    }
}
