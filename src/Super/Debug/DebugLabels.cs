using System.Numerics;
using Super.Ui;

namespace Super;

/// <summary>Draws <see cref="ViewDebug"/>'s labels into the interface's draw list, for <see cref="UiWorld.Present"/>.</summary>
internal static class DebugLabels
{
    /// <summary>What a label sits on, so it reads over anything.</summary>
    private static readonly Color Backing = new(0f, 0f, 0f, 0.6f);

    /// <summary>
    /// Draws each label over its place in the world as <paramref name="camera"/> sees it, centred just above it on a
    /// dark backing, and leaves out those behind the camera. <paramref name="screen"/> is in pixels, and the interface's
    /// scale turns them into its points.
    /// </summary>
    public static void Draw(
        ReadOnlySpan<DebugLabel> labels, in Camera camera, (UiDrawList List, GlyphAtlas Atlas, float Scale) ui, Vector2 screen)
    {
        if (labels.IsEmpty || screen.X <= 0f || screen.Y <= 0f)
        {
            return;
        }

        foreach (var label in labels)
        {
            if (camera.Pixel(label.Position, screen) is not { } pixel)
            {
                continue;
            }

            var style = TextLook.Default with { Size = 13f, Color = label.Color };
            var size = ui.Atlas.Measure(label.Text, style, null);
            var at = pixel / ui.Scale;
            var frame = new UiRect(MathF.Round(at.X - size.Width / 2f), MathF.Round(at.Y - size.Height - 6f), size.Width, size.Height);
            ui.List.AddBox(new UiRect(frame.X - 4f, frame.Y - 2f, frame.Width + 8f, frame.Height + 4f), Backing, 3f);
            ui.Atlas.Draw(ui.List, label.Text, style, frame);
            ui.List.AddText(label.Text, frame, style);
        }
    }
}
