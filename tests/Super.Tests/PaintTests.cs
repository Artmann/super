using System.Globalization;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

// What the paint is told for a frame, and which settings a view refuses. How it looks is the GPU's, so it's checked in
// PaintGpuTests.
public class PaintTests
{
    private static PaintConstants? For(PaintSettings paint, int height = 1080, RenderEffects off = RenderEffects.None, DebugView debug = DebugView.None) =>
        PaintConstants.For(paint, off, debug, height);

    [Fact]
    public void The_default_paints_nothing()
    {
        Assert.Null(For(default));
    }

    [Fact]
    public void The_switch_or_a_debug_view_turns_the_paint_off()
    {
        var paint = new PaintSettings { Amount = 1f };

        Assert.NotNull(For(paint));
        Assert.Null(For(paint, off: RenderEffects.Paint));
        Assert.Null(For(paint, debug: DebugView.Trace));
    }

    [Fact]
    public void A_brush_covers_the_same_share_of_any_height()
    {
        var paint = new PaintSettings { Amount = 1f, BrushSize = 8f };

        // 8 pixels at 1080 is 4 half-size pixels; at 2160 the picture is twice as tall, and so is the brush.
        Assert.Equal(4f, For(paint, 1080)!.Value.Radius, 4);
        Assert.Equal(8f, For(paint, 2160)!.Value.Radius, 4);
        Assert.Equal(2f, For(paint, 540)!.Value.Radius, 4);
    }

    [Fact]
    public void A_brush_of_zero_is_the_default_brush()
    {
        var zero = For(new PaintSettings { Amount = 1f })!.Value;
        var given = For(new PaintSettings { Amount = 1f, BrushSize = PaintSettings.DefaultBrushSize })!.Value;

        Assert.Equal(given.Radius, zero.Radius);
        Assert.True(zero.Radius > 0f);
    }

    [Fact]
    public void A_brush_never_reaches_past_the_shared_tile()
    {
        // At 4K the largest brush would be 16 half-size pixels, but the tile only holds 11 on every side.
        var big = For(new PaintSettings { Amount = 1f, BrushSize = PaintSettings.MaxBrushSize }, 2160)!.Value;

        Assert.Equal(11f, big.Radius);
    }

    [Fact]
    public void Sharper_paint_weighs_sectors_harder()
    {
        float soft = For(new PaintSettings { Amount = 1f, Sharpness = 0f })!.Value.Hardness;
        float middle = For(new PaintSettings { Amount = 1f })!.Value.Hardness;
        float crisp = For(new PaintSettings { Amount = 1f, Sharpness = 1f })!.Value.Hardness;

        Assert.True(soft < middle && middle < crisp, $"{soft}, {middle}, {crisp}");
    }

    [Fact]
    public void Paint_without_a_far_distance_paints_the_whole_picture()
    {
        var whole = For(new PaintSettings { Amount = 0.5f, Near = 30f })!.Value;

        Assert.Equal(0.5f, whole.Amount);
        Assert.Equal(0f, whole.Near);
        Assert.Equal(0f, whole.Far);
    }

    [Fact]
    public void Distances_a_view_would_refuse_are_taken_as_the_nearest_it_wouldnt()
    {
        // Rendition used without Super doesn't check, so the pass keeps near short of far and the amount at most 1.
        var paint = For(new PaintSettings { Amount = 3f, Near = 200f, Far = 100f })!.Value;

        Assert.Equal(1f, paint.Amount);
        Assert.Equal(100f, paint.Far);
        Assert.True(paint.Near < paint.Far, $"near {paint.Near}");
        Assert.True(paint.Near > 99f, $"near {paint.Near}");
    }

    [Fact]
    public void A_view_hands_its_paint_to_the_frame_and_a_new_frame_forgets_it()
    {
        var view = new View();
        var paint = new PaintSettings { Amount = 0.75f, BrushSize = 4f, Sharpness = 0.25f, Near = 10f, Far = 50f };

        view.Effects.Paint(paint);
        Assert.Equal(paint, view.Effects.Look.Paint);

        view.Begin(0f);
        Assert.Equal(default, view.Effects.Look.Paint);
    }

    [Fact]
    public void Paint_settings_on_an_entity_paint_the_frame()
    {
        var world = new World();
        var view = new View();
        world.Spawn(new PaintSettings { Amount = 1f, Near = 40f, Far = 150f });

        view.Begin(0f);
        WorldFrame.Describe(view, world);

        Assert.Equal(1f, view.Effects.Look.Paint.Amount);
        Assert.Equal(40f, view.Effects.Look.Paint.Near);
        Assert.Equal(150f, view.Effects.Look.Paint.Far);
    }

    [Theory]
    [InlineData("amount", "The paint's amount must be from 0 to 1, with 0 for none, but it is 1.5.")]
    [InlineData("brush", "The paint's brush size must be from 0 to 16 pixels, with 0 for the default of 10, but it is 20.5.")]
    [InlineData("sharpness", "The paint's sharpness must be from 0 to 1, or null for the default of 0.5, but it is -0.5.")]
    [InlineData("near", "The paint's near distance must be 0 metres or more, but it is -1.5.")]
    [InlineData("far", "The paint's far distance must be 0 metres or more, with 0 to paint the whole picture, but it is NaN.")]
    [InlineData("order", "The paint's far distance, 10.5 metres, must be past its near distance, 20.5 metres.")]
    public void A_setting_that_cant_be_drawn_is_refused_saying_why(string what, string message)
    {
        var paint = what switch
        {
            "amount" => new PaintSettings { Amount = 1.5f },
            "brush" => new PaintSettings { Amount = 1f, BrushSize = 20.5f },
            "sharpness" => new PaintSettings { Amount = 1f, Sharpness = -0.5f },
            "near" => new PaintSettings { Amount = 1f, Near = -1.5f },
            "far" => new PaintSettings { Amount = 1f, Far = float.NaN },
            _ => new PaintSettings { Amount = 1f, Near = 20.5f, Far = 10.5f },
        };

        // Under Swedish, so a number written with the culture's decimal comma shows.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new View().Effects.Paint(paint));

            Assert.Equal("settings", error.ParamName);
            Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
