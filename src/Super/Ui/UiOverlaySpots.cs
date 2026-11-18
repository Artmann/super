using System.Numerics;

namespace Super.Ui;

/// <summary>Where the overlays go around the cursor, in interface units.</summary>
internal static class UiOverlaySpots
{
    private const float TooltipOffsetX = 14f;
    private const float TooltipOffsetY = 18f;

    /// <summary>Centred on the cursor, as a drag preview is.</summary>
    public static UiRect Centered(UiSize size, Vector2 cursor) =>
        new(cursor.X - size.Width / 2f, cursor.Y - size.Height / 2f, size.Width, size.Height);

    /// <summary>Beside the cursor, flipped to its other side at the screen's edges.</summary>
    public static UiRect Beside(UiSize size, Vector2 cursor, UiSize screen)
    {
        float x = cursor.X + TooltipOffsetX;
        float y = cursor.Y + TooltipOffsetY;
        if (x + size.Width > screen.Width)
        {
            x = cursor.X - TooltipOffsetX - size.Width;
        }

        if (y + size.Height > screen.Height)
        {
            y = cursor.Y - TooltipOffsetY - size.Height;
        }

        return new UiRect(Math.Max(x, 0f), Math.Max(y, 0f), size.Width, size.Height);
    }
}
