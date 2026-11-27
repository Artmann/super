using System.Numerics;
using Super;
using Super.Ui;

namespace StorybookExample;

/// <summary>
/// A warm, textured theme whose cards, popups and buttons are drawn from art. The art is painted here in code, so the
/// storybook needs no image files: a game would name its own with <c>content.UiImages.Image("ui/card.png")</c>.
/// </summary>
internal static class Textured
{
    private static readonly Vector3 Edge = Rgb(0x12, 0x0D, 0x08);
    private static readonly Vector3 Gold = Rgb(0xC8, 0x98, 0x48);
    private static readonly Vector3 DeepGold = Rgb(0x8A, 0x62, 0x2A);
    private static readonly Vector3 Inner = Rgb(0x1A, 0x13, 0x0C);

    /// <summary>The theme, its art painted into this content.</summary>
    public static Theme Paint(Content content) => Theme.Dark with
    {
        Colors = new ThemeColors
        {
            Background = Color.Hex("#14100C"),
            Foreground = Color.Hex("#F3E9D2"),
            Surface = Color.Hex("#2A2017"),
            Raised = Color.Hex("#33271C"),
            Muted = Color.Hex("#3A2E22"),
            MutedForeground = Color.Hex("#B9A88A"),
            Hover = Color.Hex("#3F3225"),
            Primary = Color.Hex("#D9A441"),
            PrimaryForeground = Color.Hex("#1E150D"),
            Secondary = Color.Hex("#5A4733"),
            SecondaryForeground = Color.Hex("#F3E9D2"),
            Destructive = Color.Hex("#B5412F"),
            DestructiveForeground = Color.Hex("#F3E9D2"),
            Success = Color.Hex("#6BAF5B"),
            Warning = Color.Hex("#E0A23A"),
            Border = Color.Hex("#5A4630"),
            Input = Color.Hex("#6B5539"),
            Ring = Color.Hex("#D9A441"),
            Scrim = Color.Hex("#000000", 0.6f),
        },
        Panels = new ThemePanels
        {
            Card = new PanelArt(Image(content, 48, 48, Card), 16f),
            Popup = new PanelArt(Image(content, 64, 64, Popup), 24f),
            Button = new PanelArt(Image(content, 32, 32, Button), 10f) { Tinted = true },
        },
    };

    /// <summary>Leather in a gold rim with a dark edge, and a rivet in each corner.</summary>
    private static Vector4 Card(Vector2 at, Vector2 size)
    {
        float depth = -Inside(at, size, 7f);
        var colour = depth switch
        {
            < 1.5f => Edge,
            < 3f => Vector3.Lerp(Gold, DeepGold, at.Y / size.Y),
            < 4.5f => Inner,
            _ => Vector3.Lerp(Rgb(0x30, 0x25, 0x1A), Rgb(0x26, 0x1C, 0x13), at.Y / size.Y),
        };
        colour = Rivets(at, size, 9f, colour);
        return new Vector4(colour, Coverage(depth));
    }

    /// <summary>Stone in two gold rims, with a diamond at each corner.</summary>
    private static Vector4 Popup(Vector2 at, Vector2 size)
    {
        float depth = -Inside(at, size, 9f);
        var colour = depth switch
        {
            < 1.5f => Edge,
            < 3.5f => Vector3.Lerp(Gold, DeepGold, at.Y / size.Y),
            < 5.5f => Inner,
            < 6.5f => DeepGold,
            _ => Vector3.Lerp(Rgb(0x3A, 0x2D, 0x20), Rgb(0x2E, 0x23, 0x18), at.Y / size.Y),
        };
        var corner = Vector2.Min(at, size - at);
        if (MathF.Abs(corner.X - 12f) + MathF.Abs(corner.Y - 12f) < 4f)
        {
            colour = Gold;
        }

        return new Vector4(colour, Coverage(depth));
    }

    /// <summary>A pale bevel, light at the top and darker at the bottom, which the button's colour tints.</summary>
    private static Vector4 Button(Vector2 at, Vector2 size)
    {
        float depth = -Inside(at, size, 6f);
        float shade = depth switch
        {
            < 1.5f => 0.35f,
            < 3f => at.Y < size.Y / 2f ? 1f : 0.62f,
            _ => at.Y < 5f ? 0.98f : 0.86f,
        };
        return new Vector4(shade, shade, shade, Coverage(depth));
    }

    private static Vector3 Rivets(Vector2 at, Vector2 size, float from, Vector3 colour)
    {
        var corner = Vector2.Min(at, size - at);
        float distance = Vector2.Distance(corner, new Vector2(from));
        return distance switch
        {
            < 1.2f => Rgb(0xF6, 0xD9, 0x8E),
            < 2.4f => Gold,
            < 3f => Edge,
            _ => colour,
        };
    }

    /// <summary>How far a point is outside a rounded rectangle filling the image, negative inside.</summary>
    private static float Inside(Vector2 at, Vector2 size, float radius)
    {
        var half = size / 2f;
        var q = Vector2.Abs(at - half) - (half - new Vector2(radius));
        return Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0f) - radius;
    }

    /// <summary>How much of a pixel this far inside the edge is covered, for a smooth edge.</summary>
    private static float Coverage(float depth) => Math.Clamp(depth + 0.5f, 0f, 1f);

    private static Vector3 Rgb(int red, int green, int blue) => new(red / 255f, green / 255f, blue / 255f);

    /// <summary>Paints an image a pixel at a time, from each pixel's middle, in sRGB with straight alpha.</summary>
    private static GameImage Image(Content content, int width, int height, Func<Vector2, Vector2, Vector4> paint)
    {
        byte[] pixels = new byte[width * height * 4];
        var size = new Vector2(width, height);
        for (int i = 0; i < width * height; i++)
        {
            var colour = paint(new Vector2(i % width + 0.5f, i / width + 0.5f), size);
            pixels[i * 4] = (byte)(colour.X * 255f + 0.5f);
            pixels[i * 4 + 1] = (byte)(colour.Y * 255f + 0.5f);
            pixels[i * 4 + 2] = (byte)(colour.Z * 255f + 0.5f);
            pixels[i * 4 + 3] = (byte)(colour.W * 255f + 0.5f);
        }

        return content.UiImages.Image(width, height, pixels);
    }
}
