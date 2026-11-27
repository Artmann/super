using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class ScreenEffectsTests
{
    private static readonly Vector3 Red = new(1f, 0f, 0f);
    private static readonly Vector3 Green = new(0f, 1f, 0f);

    private static ScreenEffects Busy() => new()
    {
        ChromaticAberration = 0.4f,
        SpeedBlur = 0.5f,
        SpeedBlurCenter = new Vector2(0.25f, 0.75f),
        Flash = 0.6f,
        FlashColor = Red,
        Fade = 0.3f,
        FadeColor = Green,
        EdgeTint = new EdgeTint(Red, 0.7f),
        SecondEdgeTint = new EdgeTint(Green, 0.2f),
        Desaturate = 0.8f,
        Blur = 0.6f,
        Canvas = new TextureHandle(5),
        CanvasStrength = 0.9f,
        CanvasTileSize = 256f,
    };

    [Fact]
    public void Lerp_ends_are_the_effects_themselves()
    {
        var from = Busy();
        var to = new ScreenEffects { Flash = 1f };

        Assert.Equal(from, ScreenEffects.Lerp(from, to, 0f));
        Assert.Equal(to, ScreenEffects.Lerp(from, to, 1f));
        Assert.Equal(from, ScreenEffects.Lerp(from, to, -3f));
        Assert.Equal(to, ScreenEffects.Lerp(from, to, 7f));
    }

    [Fact]
    public void Lerp_moves_amounts_evenly()
    {
        var halfway = ScreenEffects.Lerp(default, Busy(), 0.5f);

        Assert.Equal(0.2f, halfway.ChromaticAberration, 5);
        Assert.Equal(0.25f, halfway.SpeedBlur, 5);
        Assert.Equal(0.3f, halfway.Flash, 5);
        Assert.Equal(0.15f, halfway.Fade, 5);
        Assert.Equal(0.35f, halfway.EdgeTint.Strength, 5);
        Assert.Equal(0.4f, halfway.Desaturate, 5);
        Assert.Equal(0.3f, halfway.Blur, 5);
    }

    [Fact]
    public void A_colour_fading_in_from_none_keeps_its_own_colour_rather_than_passing_through_black()
    {
        var halfway = ScreenEffects.Lerp(default, Busy(), 0.5f);

        Assert.Equal(Red, halfway.FlashColor);
        Assert.Equal(Green, halfway.FadeColor);
        Assert.Equal(Red, halfway.EdgeTint.Color);
        Assert.Equal(new Vector2(0.25f, 0.75f), halfway.SpeedBlurCenter);
    }

    [Fact]
    public void A_colour_fading_out_to_none_keeps_its_own_colour()
    {
        var halfway = ScreenEffects.Lerp(Busy(), default, 0.5f);

        Assert.Equal(Red, halfway.FlashColor);
        Assert.Equal(Green, halfway.SecondEdgeTint.Color);
    }

    [Fact]
    public void Two_shown_colours_blend()
    {
        var red = new ScreenEffects { Flash = 1f, FlashColor = Red };
        var green = new ScreenEffects { Flash = 1f, FlashColor = Green };

        Assert.Equal(new Vector3(0.25f, 0.75f, 0f), ScreenEffects.Lerp(red, green, 0.75f).FlashColor);
    }

    [Fact]
    public void Two_different_canvases_cross_through_none()
    {
        var paper = new ScreenEffects { Canvas = new TextureHandle(1), CanvasStrength = 0.8f, CanvasTileSize = 100f };
        var cloth = new ScreenEffects { Canvas = new TextureHandle(2), CanvasStrength = 0.4f, CanvasTileSize = 300f };

        var quarter = ScreenEffects.Lerp(paper, cloth, 0.25f);
        var threeQuarters = ScreenEffects.Lerp(paper, cloth, 0.75f);

        Assert.Equal(new TextureHandle(1), quarter.Canvas);
        Assert.Equal(0.4f, quarter.CanvasStrength, 5);
        Assert.Equal(100f, quarter.CanvasTileSize);
        Assert.Equal(new TextureHandle(2), threeQuarters.Canvas);
        Assert.Equal(0.2f, threeQuarters.CanvasStrength, 5);
        Assert.Equal(300f, threeQuarters.CanvasTileSize);
    }

    [Fact]
    public void The_same_canvas_blends_its_strength_and_tile()
    {
        var faint = new ScreenEffects { Canvas = new TextureHandle(1), CanvasStrength = 0.2f, CanvasTileSize = 100f };
        var strong = faint with { CanvasStrength = 0.6f, CanvasTileSize = 300f };

        var halfway = ScreenEffects.Lerp(faint, strong, 0.5f);

        Assert.Equal(new TextureHandle(1), halfway.Canvas);
        Assert.Equal(0.4f, halfway.CanvasStrength, 5);
        Assert.Equal(200f, halfway.CanvasTileSize, 5);
    }

    [Fact]
    public void Edge_tint_lerp_blends_strength_and_keeps_the_shown_colour()
    {
        var halfway = EdgeTint.Lerp(new EdgeTint(Green, 0f), new EdgeTint(Red, 0.8f), 0.5f);

        Assert.Equal(new EdgeTint(Red, 0.4f), halfway);
    }

    [Fact]
    public void Post_settings_lerp_blends_the_screen_effects()
    {
        var calm = new PostSettings();
        var hurt = new PostSettings { Screen = new ScreenEffects { EdgeTint = new EdgeTint(Red, 1f) } };

        var halfway = PostSettings.Lerp(calm, hurt, 0.5f);

        Assert.Equal(new EdgeTint(Red, 0.5f), halfway.Screen.EdgeTint);
    }

    [Fact]
    public void Over_lets_each_effect_the_top_sets_win_and_keeps_the_rest()
    {
        var under = Busy();
        var top = new ScreenEffects { Flash = 1f, FlashColor = Green, SecondEdgeTint = new EdgeTint(Red, 0.5f) };

        var layered = ScreenEffects.Over(top, under);

        Assert.Equal(
            under with { Flash = 1f, FlashColor = Green, SecondEdgeTint = new EdgeTint(Red, 0.5f) },
            layered);
    }

    [Fact]
    public void Over_takes_the_tops_speed_blur_centre_and_fade_colour_with_their_amounts()
    {
        var top = new ScreenEffects { SpeedBlur = 0.1f, Fade = 0.2f };

        var layered = ScreenEffects.Over(top, Busy());

        Assert.Equal(0.1f, layered.SpeedBlur);
        Assert.Null(layered.SpeedBlurCenter);
        Assert.Equal(0.2f, layered.Fade);
        Assert.Equal(Vector3.Zero, layered.FadeColor);
    }

    [Fact]
    public void Over_keeps_the_paper_under_a_canvas_with_no_strength()
    {
        var top = new ScreenEffects { Canvas = new TextureHandle(9) };

        Assert.Equal(new TextureHandle(5), ScreenEffects.Over(top, Busy()).Canvas);
    }

    [Fact]
    public void Nothing_off_keeps_every_screen_effect()
    {
        var post = new PostSettings { Screen = Busy() };

        Assert.Equal(Busy(), PostGrade.Without(post, RenderEffects.None).Screen);
    }

    [Fact]
    public void Each_screen_switch_clears_only_its_own_effect()
    {
        var post = new PostSettings { Screen = Busy() };

        Assert.Equal(Busy() with { ChromaticAberration = 0f }, PostGrade.Without(post, RenderEffects.ChromaticAberration).Screen);
        Assert.Equal(Busy() with { SpeedBlur = 0f }, PostGrade.Without(post, RenderEffects.SpeedBlur).Screen);
        Assert.Equal(Busy() with { Flash = 0f }, PostGrade.Without(post, RenderEffects.Flashes).Screen);
    }

    [Fact]
    public void The_screen_effects_switch_clears_all_but_the_fade()
    {
        var post = new PostSettings { Screen = Busy() };

        var screen = PostGrade.Without(post, RenderEffects.ScreenEffects).Screen;

        Assert.Equal(new ScreenEffects { Fade = 0.3f, FadeColor = Green }, screen);
    }

    [Fact]
    public void The_screen_block_holds_every_amount_from_0_to_1()
    {
        var wild = new ScreenEffects
        {
            ChromaticAberration = 3f,
            SpeedBlur = -1f,
            Flash = 2f,
            FlashColor = new Vector3(4f, -1f, 0.5f),
            Desaturate = 9f,
            EdgeTint = new EdgeTint(Red, 5f),
            Canvas = new TextureHandle(3),
            CanvasStrength = 2f,
            CanvasTileSize = -10f,
            Blur = 4f,
        };

        var block = PostGrade.Screen(wild, 3);
        var under = PostGrade.Screen(new ScreenEffects { Blur = -2f }, -1);

        Assert.Equal(1f, block.ScreenRetro.W);
        Assert.Equal(0f, under.ScreenRetro.W);

        Assert.Equal(new Vector4(1f, 0f, 0.5f, 0.5f), block.ScreenLens);
        Assert.Equal(new Vector4(1f, 0f, 0.5f, 1f), block.ScreenFlash);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), block.EdgeTint);
        Assert.Equal(new Vector4(1f, 1f, 0f, 3f), block.ScreenCanvas);
    }

    [Fact]
    public void The_screen_block_lights_a_flash_with_no_colour_white()
    {
        var block = PostGrade.Screen(new ScreenEffects { Flash = 0.5f }, -1);

        Assert.Equal(new Vector4(1f, 1f, 1f, 0.5f), block.ScreenFlash);
    }

    [Fact]
    public void The_screen_block_puts_the_speed_blur_centre_where_it_is_asked()
    {
        var block = PostGrade.Screen(new ScreenEffects { SpeedBlur = 0.5f, SpeedBlurCenter = new Vector2(0.2f, 0.9f) }, -1);

        Assert.Equal(new Vector4(0f, 0.5f, 0.2f, 0.9f), block.ScreenLens);
    }

    [Fact]
    public void A_canvas_with_no_texture_or_no_strength_shows_none()
    {
        var paper = new ScreenEffects { Canvas = new TextureHandle(3), CanvasStrength = 0.5f, CanvasTileSize = 64f };

        Assert.Equal(new Vector4(0f, 0f, 64f, -1f), PostGrade.Screen(paper, -1).ScreenCanvas);
        Assert.Equal(new Vector4(0f, 0f, 64f, -1f), PostGrade.Screen(paper with { CanvasStrength = 0f }, 3).ScreenCanvas);
        Assert.Equal(new Vector4(0f, 0.5f, 64f, 3f), PostGrade.Screen(paper, 3).ScreenCanvas);
    }

    [Fact]
    public void No_screen_effects_make_a_block_that_changes_nothing()
    {
        var block = PostGrade.Screen(default, -1);

        Assert.Equal(new Vector4(0f, 0f, 0.5f, 0.5f), block.ScreenLens);
        Assert.Equal(0f, block.ScreenFlash.W);
        Assert.Equal(0f, block.ScreenFade.W);
        Assert.Equal(0f, block.EdgeTint.W);
        Assert.Equal(0f, block.SecondEdgeTint.W);
        Assert.Equal(new Vector4(0f, 0f, 0f, -1f), block.ScreenCanvas);
    }

    [Fact]
    public void The_screen_block_follows_the_reflection_sun_matrix_as_in_common_glsl()
    {
        // std140: after the mat4 reflectionSunMatrix come six vec4s: the lens, the flash, the fade, the two edge
        // tints and the canvas.
        int after = PostGradeTests.FrameOffset("ReflectionSunMatrix") + 64;
        Assert.Equal(after, PostGradeTests.FrameOffset("ScreenLens"));
        Assert.Equal(after + 16, PostGradeTests.FrameOffset("ScreenFlash"));
        Assert.Equal(after + 32, PostGradeTests.FrameOffset("ScreenFade"));
        Assert.Equal(after + 48, PostGradeTests.FrameOffset("EdgeTint"));
        Assert.Equal(after + 64, PostGradeTests.FrameOffset("SecondEdgeTint"));
        Assert.Equal(after + 80, PostGradeTests.FrameOffset("ScreenCanvas"));
    }

    [Fact]
    public void Screen_effects_from_play_lie_over_the_finish_whichever_is_set_first()
    {
        var paper = new ScreenEffects { Canvas = new TextureHandle(4), CanvasStrength = 0.5f };
        var hit = new ScreenEffects { Flash = 0.8f };
        var first = new View();
        var second = new View();

        first.Effects.Finish(new PostSettings { Screen = paper });
        first.Effects.Screen(hit);
        second.Effects.Screen(hit);
        second.Effects.Finish(new PostSettings { Screen = paper });

        var both = paper with { Flash = 0.8f };
        Assert.Equal(both, first.Effects.Post.Screen);
        Assert.Equal(both, second.Effects.Post.Screen);
    }

    [Fact]
    public void A_new_frame_clears_the_screen_effects()
    {
        var view = new View();
        view.Effects.Screen(new ScreenEffects { Flash = 0.8f });

        view.Begin(0f);

        Assert.Equal(default, view.Effects.Post.Screen);
    }

    [Theory]
    [InlineData("chromatic aberration")]
    [InlineData("speed blur")]
    [InlineData("flash")]
    [InlineData("fade")]
    [InlineData("edge tint's strength")]
    [InlineData("second edge tint's strength")]
    [InlineData("desaturation")]
    [InlineData("blur")]
    [InlineData("canvas strength")]
    public void An_amount_past_1_is_refused_with_its_name(string name)
    {
        var effects = name switch
        {
            "chromatic aberration" => new ScreenEffects { ChromaticAberration = 1.5f },
            "speed blur" => new ScreenEffects { SpeedBlur = 1.5f },
            "flash" => new ScreenEffects { Flash = 1.5f },
            "fade" => new ScreenEffects { Fade = 1.5f },
            "edge tint's strength" => new ScreenEffects { EdgeTint = new EdgeTint(Red, 1.5f) },
            "second edge tint's strength" => new ScreenEffects { SecondEdgeTint = new EdgeTint(Red, 1.5f) },
            "desaturation" => new ScreenEffects { Desaturate = 1.5f },
            "blur" => new ScreenEffects { Blur = 1.5f },
            _ => new ScreenEffects { CanvasStrength = 1.5f },
        };

        var error = InSwedish(() => Assert.Throws<ArgumentOutOfRangeException>(() => new View().Effects.Screen(effects)));

        Assert.Equal("effects", error.ParamName);
        Assert.StartsWith($"The {name} must be from 0, none, to 1, but it is 1.5.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_amount_in_the_finish_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new View().Effects.Finish(new PostSettings { Screen = new ScreenEffects { Flash = -0.5f } }));

        Assert.Equal("settings", error.ParamName);
        Assert.StartsWith("The flash must be from 0, none, to 1, but it is -0.5.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_canvas_tile_is_refused()
    {
        var error = InSwedish(() => Assert.Throws<ArgumentOutOfRangeException>(
            () => new View().Effects.Screen(new ScreenEffects { CanvasTileSize = -2.5f })));

        Assert.StartsWith(
            "The canvas tile size must be a number of pixels, or 0 for the texture's own size, but it is -2.5.",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_speed_blur_centre_that_is_not_a_number_is_refused()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new View().Effects.Screen(new ScreenEffects { SpeedBlurCenter = new Vector2(float.NaN, 0.5f) }));

        Assert.StartsWith("The speed blur's centre must be a place on the screen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_that_is_not_a_number_is_refused_with_its_name()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new View().Effects.Screen(new ScreenEffects { SecondEdgeTint = new EdgeTint(new Vector3(float.PositiveInfinity), 0.5f) }));

        Assert.StartsWith("The second edge tint colour must be numbers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_effects_component_sets_the_frames_screen_over_the_finish()
    {
        var world = new World();
        var view = new View();
        world.Spawn(new Finish(new PostSettings { Screen = new ScreenEffects { Desaturate = 0.5f } }));
        var player = world.Spawn(new ScreenEffects { EdgeTint = new EdgeTint(Red, 0.6f) });

        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(new ScreenEffects { Desaturate = 0.5f, EdgeTint = new EdgeTint(Red, 0.6f) }, view.Effects.Post.Screen);

        world.Get<ScreenEffects>(player).Flash = 0.25f;
        view.Begin(0f);
        WorldFrame.Describe(view, world);
        Assert.Equal(0.25f, view.Effects.Post.Screen.Flash);
    }

    private static T InSwedish<T>(Func<T> act)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
