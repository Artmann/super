using System.Numerics;

namespace Super.Ui;

/// <summary>What a view placed over the world does when its point isn't on screen.</summary>
public enum OffScreen
{
    /// <summary>It goes: past the edge with its point, or out of sight when the point is behind the camera.</summary>
    Hide,

    /// <summary>It clings to the edge of the screen nearest its point, with an arrow pointing the way.</summary>
    Edge,
}

/// <summary>Views placed over points in the world, from <see cref="WorldExtensions.AtWorld"/>.</summary>
public static class WorldExtensions
{
    /// <summary>
    /// Places the view over a point in the world, for a health bar over a head or a name plate over a door. The
    /// <paramref name="anchor"/> is the part of the view on the point: the default, bottom, sits it on top. It takes
    /// no room where it's laid out, so put it in a full-screen layer, not in a scroll view or under an offset.
    /// When the point is off screen it hides, or clings to the edge with an arrow with <see cref="OffScreen.Edge"/>.
    /// The interface lays out again each frame the camera moves while one is shown.
    /// </summary>
    public static UiView AtWorld(this UiView view, Vector3 point, Alignment anchor = Alignment.Bottom, OffScreen offScreen = OffScreen.Hide) =>
        new AtWorldModifier(view, point, anchor, offScreen);
}

/// <summary>The camera the interface's world views are placed with, held by the <see cref="UiTree"/>.</summary>
internal sealed class UiWorld(UiTree tree, UiDrawList draw)
{
    /// <summary>The camera of the frame, or null before the game has shown one.</summary>
    public Camera? Camera { get; private set; }

    /// <summary>The size of the screen in pixels.</summary>
    public Vector2 Screen { get; set; }

    /// <summary>How many views the last layout placed over the world.</summary>
    public int Anchored { get; set; }

    /// <summary>How much the last layout's <see cref="Backdrop"/>s ask for the picture behind to blur, the most of them.</summary>
    public float Blur { get; set; }

    /// <summary>Takes the frame's camera, and lays out again when it moved and views follow it.</summary>
    public void Look(in Camera camera)
    {
        if (Anchored > 0 && Camera != camera)
        {
            tree.RequestLayout();
        }

        Camera = camera;
    }

    /// <summary>
    /// Where a point shows on the interface, or null when it's hidden or there's no camera yet. A view that clings
    /// stays <paramref name="inset"/> in from the edges.
    /// </summary>
    public WorldSpot? Spot(Vector3 point, OffScreen offScreen, float inset) =>
        Camera is { } camera ? WorldSpots.Find(camera, point, Screen / tree.Scale, offScreen, inset) : null;

    /// <summary>
    /// Puts what the world asks of the interface and the interface of the world on the frame, once it's laid out. Each
    /// debug label is drawn over its place in the world as the frame's camera sees it, centred just above it on a
    /// dark backing, leaving out those behind the camera, over the overlays and gone the next frame with them. And
    /// the picture behind is blurred as much as a <see cref="Backdrop"/> asks, or as the game's own effects do if more.
    /// </summary>
    public void Present(View view)
    {
        if (Camera is { } camera)
        {
            DebugLabels.Draw(view.Debug.Labels, camera, (draw, tree.Atlas, tree.Scale), Screen);
        }

        var look = view.Effects.Look;
        if (Blur > look.Post.Screen.Blur)
        {
            look.Screen = look.Screen with { Blur = Blur };
        }
    }
}

/// <summary>Where a world view goes, in interface units, and which way its point is when it clings to the edge.</summary>
internal readonly record struct WorldSpot(Vector2 At, Vector2? Toward);

/// <summary>Finds where points in the world show on the interface.</summary>
internal static class WorldSpots
{
    /// <summary>
    /// The spot for a point on a screen of this size in interface units, or null when it's hidden. A view that
    /// clings stays <paramref name="inset"/> in from the edges.
    /// </summary>
    public static WorldSpot? Find(in Camera camera, Vector3 point, Vector2 screen, OffScreen offScreen, float inset)
    {
        // The camera's aspect is the screen's, so placing on the scaled screen lands in interface units.
        if (camera.Pixel(point, screen) is { } at)
        {
            bool inside = at.X >= 0f && at.Y >= 0f && at.X <= screen.X && at.Y <= screen.Y;
            return inside || offScreen == OffScreen.Hide ? new WorldSpot(at, null) : Cling(screen, at - screen / 2f, inset);
        }

        return offScreen == OffScreen.Hide ? null : Cling(screen, Behind(camera, point), inset);
    }

    /// <summary>Which way a point behind the camera is, across the screen: to the side it's on, and down.</summary>
    private static Vector2 Behind(in Camera camera, Vector3 point)
    {
        var forward = camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, camera.Up));
        var up = Vector3.Cross(right, forward);
        var away = point - camera.Position;
        var across = new Vector2(Vector3.Dot(away, right), -Vector3.Dot(away, up));
        return across.LengthSquared() > 1e-8f ? across : Vector2.UnitY;
    }

    /// <summary>The spot on the edge, inset, along a line from the middle of the screen.</summary>
    private static WorldSpot Cling(Vector2 screen, Vector2 direction, float inset)
    {
        var half = Vector2.Max(screen / 2f - new Vector2(inset), Vector2.Zero);
        float alongX = direction.X == 0f ? float.PositiveInfinity : half.X / MathF.Abs(direction.X);
        float alongY = direction.Y == 0f ? float.PositiveInfinity : half.Y / MathF.Abs(direction.Y);
        return new WorldSpot(screen / 2f + direction * MathF.Min(alongX, alongY), Vector2.Normalize(direction));
    }
}

/// <summary>Places a view over a point in the world, from <see cref="WorldExtensions.AtWorld"/>.</summary>
internal sealed record AtWorldModifier(UiView Child, Vector3 Point, Alignment Anchor, OffScreen OffScreen) : Modifier(Child)
{
    // The gap between a clinging view and its arrow, the arrow's width, and the room kept to the screen's edge.
    private const float ArrowGap = 6f;
    private const float ArrowWidth = 3f;
    private const float EdgeRoom = 8f;

    // How much of a turn the arrow covers.
    private const float ArrowSweep = 0.14f;

    // It takes no room: it's drawn over the world, not in the layout.
    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        Inner(node).Measure(default);
        return default;
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        node.Tree.World.Anchored++;
        var size = Inner(node).Measure(default);
        var spot = Spot(node, size) ?? new WorldSpot(new Vector2(frame.X, frame.Y), null);
        var anchor = spot.Toward is null ? Anchor : Alignment.Center;
        Inner(node).Place(new UiRect(spot.At.X, spot.At.Y, 0f, 0f).Align(size, anchor));
    }

    internal override bool Shows(UiNode node, int child) => Spot(node, Inner(node).Measure(default)) is not null;

    internal override void EmitAfter(UiNode node, UiDrawList draw)
    {
        if (Spot(node, Inner(node).Measure(default)) is not { Toward: { } toward })
        {
            return;
        }

        var shown = Inner(node).Frame;
        float radius = Reach(new UiSize(shown.Width, shown.Height)) - EdgeRoom;
        var middle = new Vector2(shown.X + shown.Width / 2f, shown.Y + shown.Height / 2f);
        float turn = MathF.Atan2(toward.X, -toward.Y) / MathF.Tau;
        var ring = new UiRect(middle.X - radius, middle.Y - radius, radius * 2f, radius * 2f);
        draw.AddArc(ring, (turn - ArrowSweep / 2f) * MathF.Tau, ArrowSweep * MathF.Tau, new UiStroke(ArrowWidth, node.Environment.Text.Color));
    }

    /// <summary>How far from its middle a clinging view and its arrow reach, with room to the edge.</summary>
    private static float Reach(UiSize size) => MathF.Max(size.Width, size.Height) / 2f + ArrowGap + ArrowWidth + EdgeRoom;

    private WorldSpot? Spot(UiNode node, UiSize size) => node.Tree.World.Spot(Point, OffScreen, Reach(size));
}
