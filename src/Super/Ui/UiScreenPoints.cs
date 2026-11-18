using System.Numerics;

namespace Super.Ui;

/// <summary>Turns places in an interface into screen pixels, for scripts that move the mouse.</summary>
internal static class UiScreenPoints
{
    /// <summary>The middle of the view with this id, in screen pixels, for moving the mouse there.</summary>
    public static Vector2 ScreenMiddle(this UiTree tree, object id)
    {
        var frame = tree.FrameOf(id);
        return new Vector2(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f) * tree.Scale;
    }
}
