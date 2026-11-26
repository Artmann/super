using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// The screen effects a game drives from play. These read the picture of a plain sky looking towards the horizon, pale
// near the ground and deep blue above, with every other effect off so only the screen effects change it. The window
// may hold more pixels than asked for, such as on a screen scaled up, so places are read as parts of the picture.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ScreenEffectsGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 128;

    private const RenderEffects ScreenSwitches =
        RenderEffects.ChromaticAberration | RenderEffects.SpeedBlur | RenderEffects.Flashes | RenderEffects.ScreenEffects;

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.8f));

    // How many pixels across and down the last picture read back has.
    private int _wide = Size;

    [Fact]
    public void Colours_set_with_every_amount_at_zero_leave_the_picture_within_one_step()
    {
        int errors = gpu.ValidationErrors;
        var paper = gpu.Renderer.Assets.CreateTexture(TextureData.FromPixels(1, 1, TextureKind.Color, [0, 0, 0, 255]));
        var idle = new ScreenEffects
        {
            SpeedBlurCenter = new Vector2(0.1f, 0.9f),
            FlashColor = new Vector3(1f, 0f, 0f),
            FadeColor = new Vector3(0f, 1f, 0f),
            EdgeTint = new EdgeTint(new Vector3(1f, 0f, 0f), 0f),
            SecondEdgeTint = new EdgeTint(new Vector3(0f, 0f, 1f), 0f),
            Canvas = paper,
            CanvasTileSize = 3f,
        };

        var plain = Render(default);
        var zero = Render(idle);

        float most = MostBlockDifference(plain, zero);
        output.WriteLine($"Screen effects at zero moved a 4 by 4 square's mean by at most {most:F2} steps.");
        Assert.True(most <= 1f, $"Screen effects at zero moved a 4 by 4 square's mean by {most:F2} steps.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_flash_brightens_towards_its_colour_and_a_fade_covers_with_its_own()
    {
        int errors = gpu.ValidationErrors;
        var plain = Middle(Render(default));
        var flashed = Middle(Render(new ScreenEffects { Flash = 0.5f }));
        var faded = Middle(Render(new ScreenEffects { Fade = 0.5f }));
        var red = Middle(Render(new ScreenEffects { Fade = 1f, FadeColor = new Vector3(1f, 0f, 0f) }));
        output.WriteLine($"Plain {TestPixels.Show(plain)}, flashed {TestPixels.Show(flashed)}, faded {TestPixels.Show(faded)}, red {TestPixels.Show(red)}.");

        // Worked in linear light: half a white flash takes each channel c to 1 - (1 - c) / 2, half a fade to black to c / 2.
        var linear = ToLinear(plain);
        TestPixels.AssertNear(ToScreen(Vector3.One - ((Vector3.One - linear) * 0.5f)), flashed, 3f, "half a white flash");
        TestPixels.AssertNear(ToScreen(linear * 0.5f), faded, 3f, "half a fade to black");
        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), red, 2f, "a full fade to red");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Desaturating_fully_greys_the_sky()
    {
        int errors = gpu.ValidationErrors;
        var plain = Middle(Render(default));
        var grey = Middle(Render(new ScreenEffects { Desaturate = 1f }));
        output.WriteLine($"Plain {TestPixels.Show(plain)}, desaturated {TestPixels.Show(grey)}.");

        Assert.True(plain.Z > plain.X + 20f, $"The sky is {TestPixels.Show(plain)}, not blue enough to check against.");
        Assert.True(MathF.Abs(grey.X - grey.Z) < 2f && MathF.Abs(grey.Y - grey.Z) < 2f, $"Desaturated the sky is {TestPixels.Show(grey)}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Edge_tints_colour_the_edges_one_over_the_other_and_leave_the_middle()
    {
        int errors = gpu.ValidationErrors;
        var plainPicture = Render(default);
        var redPicture = Render(new ScreenEffects { EdgeTint = new EdgeTint(new Vector3(1f, 0f, 0f), 1f) });
        var bothPicture = Render(new ScreenEffects
        {
            EdgeTint = new EdgeTint(new Vector3(1f, 0f, 0f), 1f),
            SecondEdgeTint = new EdgeTint(new Vector3(0f, 1f, 0f), 0.5f),
        });
        var redCorner = Corner(redPicture);
        var bothCorner = Corner(bothPicture);
        output.WriteLine($"Corners: plain {TestPixels.Show(Corner(plainPicture))}, red {TestPixels.Show(redCorner)}, both {TestPixels.Show(bothCorner)}.");

        // An edge tint shows half as much again at the very corners as at the edges' middles, so half of green there
        // covers three quarters of the red under it: (0.25, 0.75, 0) in linear light.
        TestPixels.AssertNear(new Vector3(255f, 0f, 0f), redCorner, 3f, "a full red edge's corner");
        TestPixels.AssertNear(ToScreen(new Vector3(0.25f, 0.75f, 0f)), bothCorner, 3f, "half a green edge over a full red one");
        TestPixels.AssertNear(Middle(plainPicture), Middle(bothPicture), 1f, "the middle under two edge tints");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Chromatic_aberration_moves_red_and_blue_at_the_edges_and_keeps_green_and_the_middle()
    {
        int errors = gpu.ValidationErrors;

        // The sky is too smooth to show a shift of a pixel or two, so a wall stands at the left of the picture, where
        // its edge is far enough from the middle to fringe.
        using var wall = Wall();
        var plain = Render(default, draws: wall.Draws);
        var fringed = Render(new ScreenEffects { ChromaticAberration = 1f }, draws: wall.Draws);

        float red = MostBlockDifference(plain, fringed, channel: 0);
        float green = MostBlockDifference(plain, fringed, channel: 1);
        float blue = MostBlockDifference(plain, fringed, channel: 2);
        output.WriteLine($"Fringes moved a square's red by {red:F2}, green by {green:F2} and blue by {blue:F2} steps.");

        Assert.True(red > 8f, $"Fringes moved a square's red by only {red:F2} steps.");
        Assert.True(blue > 8f, $"Fringes moved a square's blue by only {blue:F2} steps.");
        // Green is read in place: what's left is the wall's edge stepping a little between frames.
        Assert.True(green <= 3f, $"Fringes moved a square's green by {green:F2} steps.");
        TestPixels.AssertNear(Middle(plain), Middle(fringed), 1f, "the middle under fringes");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Speed_blur_smears_towards_its_centre_and_leaves_the_centre()
    {
        int errors = gpu.ValidationErrors;
        var plain = Render(default);
        var blurred = Render(new ScreenEffects { SpeedBlur = 1f });
        var fromTop = Render(new ScreenEffects { SpeedBlur = 1f, SpeedBlurCenter = new Vector2(0.5f, 0f) });

        var plainTop = At(plain, 0.5f, 0.03f);
        var plainBottom = At(plain, 0.5f, 0.97f);
        var blurredTop = At(blurred, 0.5f, 0.03f);
        var blurredBottom = At(blurred, 0.5f, 0.97f);
        var fromTopTop = At(fromTop, 0.5f, 0.03f);
        var fromTopBottom = At(fromTop, 0.5f, 0.97f);
        output.WriteLine($"Top plain {TestPixels.Show(plainTop)}, blurred {TestPixels.Show(blurredTop)}, from the top {TestPixels.Show(fromTopTop)}; "
            + $"bottom plain {TestPixels.Show(plainBottom)}, blurred {TestPixels.Show(blurredBottom)}, from the top {TestPixels.Show(fromTopBottom)}.");

        // A smear towards the middle mixes some of the way to the middle's colour in, so the top comes nearer to it.
        var middle = Middle(plain);
        Assert.True(Vector3.Distance(blurredTop, middle) < Vector3.Distance(plainTop, middle) - 2f,
            $"Blur from the middle took the top from {TestPixels.Show(plainTop)} to {TestPixels.Show(blurredTop)}, not towards {TestPixels.Show(middle)}.");
        TestPixels.AssertNear(middle, Middle(blurred), 1f, "the middle under a blur from the middle");

        // From the top, the top stays and the bottom smears up the picture instead of towards its middle.
        TestPixels.AssertNear(plainTop, fromTopTop, 1f, "the top under a blur from the top");
        Assert.True(Vector3.Distance(fromTopBottom, plainBottom) > 3f, $"Blur from the top left the bottom at {TestPixels.Show(fromTopBottom)}.");
        Assert.True(Vector3.Distance(fromTopBottom, blurredBottom) > 3f,
            $"Blur from the top and from the middle both left the bottom at {TestPixels.Show(fromTopBottom)}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_canvas_darkens_where_it_is_dark_by_its_strength_and_tiles_by_its_size()
    {
        int errors = gpu.ValidationErrors;

        // Two white columns then two black, so at the texture's own size each pixel lands on one texel and at a wider
        // tile the middle of each half sits between two texels of the same colour.
        var stripes = gpu.Renderer.Assets.CreateTexture(TextureData.FromPixels(4, 1, TextureKind.Color,
            [255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255]));
        var plain = Render(default);
        var full = Render(new ScreenEffects { Canvas = stripes, CanvasStrength = 1f });
        var half = Render(new ScreenEffects { Canvas = stripes, CanvasStrength = 0.5f });
        var wide = Render(new ScreenEffects { Canvas = stripes, CanvasStrength = 1f, CanvasTileSize = 64f });

        int x = _wide / 2 / 4 * 4;
        int y = _wide / 2;
        output.WriteLine($"Plain {TestPixels.Show(Pixel(plain, x, y))}; full {TestPixels.Show(Pixel(full, x, y))} {TestPixels.Show(Pixel(full, x + 2, y))}; "
            + $"half {TestPixels.Show(Pixel(half, x + 2, y))}; wide {TestPixels.Show(Pixel(wide, 16, y))} {TestPixels.Show(Pixel(wide, 48, y))}.");

        TestPixels.AssertNear(Pixel(plain, x, y), Pixel(full, x, y), 2f, "a white column of the paper");
        Assert.True(TestPixels.Brightness(Pixel(full, x + 2, y)) < 2f, $"A black column at full strength is {TestPixels.Show(Pixel(full, x + 2, y))}.");
        TestPixels.AssertNear(ToScreen(ToLinear(Pixel(plain, x + 2, y)) * 0.5f), Pixel(half, x + 2, y), 3f, "a black column at half strength");

        // 64 pixels a tile: the first 32 are white and the next 32 black.
        TestPixels.AssertNear(Pixel(plain, 16, y), Pixel(wide, 16, y), 2f, "the white half of a 64 pixel tile");
        Assert.True(TestPixels.Brightness(Pixel(wide, 48, y)) < 2f, $"The black half of a 64 pixel tile is {TestPixels.Show(Pixel(wide, 48, y))}.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Switches_off_take_the_screen_effects_away_and_the_group_keeps_the_fade()
    {
        int errors = gpu.ValidationErrors;
        var busy = new ScreenEffects
        {
            ChromaticAberration = 1f,
            SpeedBlur = 1f,
            Flash = 0.5f,
            EdgeTint = new EdgeTint(new Vector3(1f, 0f, 0f), 1f),
            Desaturate = 1f,
        };

        // The wall's edge shows fringes left on, which the smooth sky would hide.
        using var wall = Wall();
        var plain = Render(default, draws: wall.Draws);
        float group = MostBlockDifference(plain, Render(busy, RenderEffects.ScreenEffects, wall.Draws));

        // The tint and the loss of colour would cover the wall's fringes, so the own switches are checked without them.
        var moving = new ScreenEffects { ChromaticAberration = 1f, SpeedBlur = 1f, Flash = 0.5f };
        float each = MostBlockDifference(
            plain, Render(moving, RenderEffects.ChromaticAberration | RenderEffects.SpeedBlur | RenderEffects.Flashes, wall.Draws));
        var fadeKept = Middle(Render(busy with { Fade = 1f, FadeColor = new Vector3(0f, 0f, 1f) }, RenderEffects.ScreenEffects));
        output.WriteLine($"Group off moved a square by {group:F2}; the three own switches by {each:F2}; the kept fade {TestPixels.Show(fadeKept)}.");

        // Up to 3 steps is the wall's edge stepping a little between frames.
        Assert.True(group <= 3f, $"With the group switch off the screen effects still moved a square by {group:F2} steps.");
        Assert.True(each <= 3f, $"With their own switches off the fringes, blur and flash still moved a square by {each:F2} steps.");
        TestPixels.AssertNear(new Vector3(0f, 0f, 255f), fadeKept, 2f, "a full blue fade with the group switch off");
        Assert.Equal(errors, gpu.ValidationErrors);
    }


    [Fact]
    public void Changing_the_screen_effects_never_bakes_the_grade_again()
    {
        var grade = gpu.Renderer.FrameParts.FrameStages.PostStage.Grade;
        Render(default);

        int before = grade.Bakes;
        Render(new ScreenEffects { Flash = 0.3f, Fade = 0.2f, Desaturate = 0.5f });
        Render(new ScreenEffects { ChromaticAberration = 0.6f, SpeedBlur = 0.4f, EdgeTint = new EdgeTint(Vector3.UnitX, 0.5f) });

        Assert.Equal(before, grade.Bakes);
    }

    private Vector3 At(byte[] pixels, float across, float down) => TestPixels.Around(pixels, _wide, across, down);

    private Vector3 Middle(byte[] pixels) => At(pixels, 0.5f, 0.5f);

    private Vector3 Corner(byte[] pixels) => At(pixels, 0f, 0f);

    private Vector3 Pixel(byte[] pixels, int x, int y)
    {
        int at = (y * _wide + x) * 4;
        return new Vector3(pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    // A screen colour of 0 to 255 in the sRGB curve, as linear light from 0 to 1, and back.
    private static Vector3 ToLinear(Vector3 screen) => new(Linear(screen.X / 255f), Linear(screen.Y / 255f), Linear(screen.Z / 255f));

    private static Vector3 ToScreen(Vector3 linear) => new Vector3(Encoded(linear.X), Encoded(linear.Y), Encoded(linear.Z)) * 255f;

    private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float Encoded(float c) => c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1f / 2.4f)) - 0.055f;

    // A couple of frames first, then the one read back, with every effect but the screen effects off.
    private byte[] Render(in ScreenEffects screen, RenderEffects off = RenderEffects.None, DrawItem[]? draws = null)
    {
        var view = TestSky.View(Size, new Vector3(0f, 0.3f, -1f), Sun) with
        {
            Picture = new PictureSettings { Post = new PostSettings { Screen = screen } },
            Off = off | (RenderEffects.All & ~ScreenSwitches),
        };
        return TestSky.Capture(gpu, view, draws ?? [], 2, out _wide);
    }

    // The most the mean of any 4 by 4 square of the channels differs between two pictures, in steps of 255.
    private float MostBlockDifference(byte[] a, byte[] b, int channel = -1)
    {
        Assert.Equal(a.Length, b.Length);
        float most = 0f;
        for (int top = 0; top + 4 <= _wide; top += 4)
        {
            for (int left = 0; left + 4 <= _wide; left += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    if (channel >= 0 && c != channel)
                    {
                        continue;
                    }

                    int difference = 0;
                    for (int y = top; y < top + 4; y++)
                    {
                        for (int x = left; x < left + 4; x++)
                        {
                            int at = (y * _wide + x) * 4 + c;
                            difference += a[at] - b[at];
                        }
                    }

                    most = MathF.Max(most, MathF.Abs(difference / 16f));
                }
            }
        }

        return most;
    }

    // A grey wall covering the left tenth of the picture, for a sharp edge where effects that move pixels show.
    private TestWall Wall() => new(gpu, Matrix4x4.CreateScale(8f, 40f, 0.1f) * Matrix4x4.CreateTranslation(-6.3f, 0f, -5f));
}
