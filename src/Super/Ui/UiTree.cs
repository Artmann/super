using System.Numerics;
using Rendition;

namespace Super.Ui;

/// <summary>
/// A piece of text on screen and where it landed, with its colour, size in pixels and
/// weight. The colour is the one it's drawn in, faded by any <c>.Opacity</c> around it.
/// </summary>
public readonly record struct ShownText(string Text, UiRect Frame, Color Color, float Size, bool Bold, GameFont? Font = null);

/// <summary>
/// Keeps an interface on screen: it reconciles each new description, lays out what
/// changed, and writes down the rectangles to draw. None of it touches a GPU, so an
/// interface can be tested with no window.
/// </summary>
public sealed class UiTree
{
    private readonly UiDrawList _draw = new();

    // The drag preview and the tooltip, which float above everything.
    private readonly UiNode?[] _overlays = new UiNode?[2];
    private UiNode? _root;
    private (int Width, int Height) _screen;
    private bool _layoutWanted;

    public IReadOnlyList<ShownText> ShownText => _draw.ShownText;

    internal GlyphAtlas Atlas => field ??= new GlyphAtlas { Scale = Scale };

    internal ReadOnlySpan<OverlayQuad> Quads => _draw.Quads;

    /// <summary>
    /// How many pixels one unit of the interface covers. At 2, text, padding and frames are
    /// all twice as big, so an interface made for a desktop window can be read in a video
    /// watched on a phone. Text is drawn at the bigger size, so it stays sharp. Layout,
    /// <see cref="FrameOf"/> and <see cref="ShownText"/> are all in interface units, which
    /// are pixels at the default of 1. The mouse is turned into interface units for you.
    /// </summary>
    public float Scale
    {
        get => _draw.Scale;
        set
        {
            if (!(value > 0f) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The interface scale must be a number above zero.");
            }

            if (value != _draw.Scale)
            {
                _draw.Scale = value;
                Atlas.Scale = value;
                _layoutWanted = true;
            }
        }
    }

    /// <summary>
    /// Shows this description on a screen of this size in pixels. The interface lays itself
    /// out in that size divided by <see cref="Scale"/>. Pass null to show nothing.
    /// </summary>
    public void Update(UiView? view, int width, int height)
    {
        bool changed = Mount(ref _root, view) | (width, height) != _screen | _layoutWanted;
        _screen = (width, height);
        if (changed)
        {
            _layoutWanted = false;
            Layout();
        }
    }

    /// <summary>
    /// Where the view given this id with <see cref="UiExtensions.Id"/> was laid out last,
    /// in interface pixels. A box's margin isn't part of it, so this is the area that
    /// shows and takes clicks. It also looks in the tooltip and drag preview while they
    /// show. It throws if no view or more than one has the id.
    /// </summary>
    public UiRect FrameOf(object id)
    {
        var found = new List<UiNode>();
        var ids = new List<object>();
        if (_root is not null)
        {
            Collect(_root, id, found, ids);
        }

        // The drag preview and the tooltip, as the last frame showed them.
        foreach (var overlay in _overlays)
        {
            if (overlay is not null)
            {
                Collect(overlay, id, found, ids);
            }
        }

        return found.Count switch
        {
            1 => PrimitiveView.ShownArea(found[0]),
            0 => throw new InvalidOperationException(ids.Count == 0
                ? $"No view on screen has the id '{id}', and none has an id."
                : $"No view on screen has the id '{id}'. These ids are: {string.Join(", ", ids)}."),
            _ => throw new InvalidOperationException($"{found.Count} views on screen have the id '{id}', so it doesn't say which one."),
        };
    }

    private static void Collect(UiNode node, object id, List<UiNode> found, List<object> ids)
    {
        if (node.View.Identity is { } identity)
        {
            if (Equals(identity, id))
            {
                found.Add(node);
            }

            if (!ids.Contains(identity))
            {
                ids.Add(identity);
            }
        }

        foreach (var child in node.Children)
        {
            if (child is not null)
            {
                Collect(child, id, found, ids);
            }
        }
    }

    /// <summary>What the mouse touches at this point, in the layout last shown.</summary>
    internal UiNode? Hit(float x, float y) => _root?.Hit(x, y);

    /// <summary>
    /// Draws what floats above everything and belongs to no place in the tree, each time
    /// replacing what was there. The mouse can't touch these and no scroll area clips them.
    /// </summary>
    internal void ShowOverlays(UiView? dragged, UiView? tooltip, Vector2 cursor)
    {
        _draw.TrimToTree();
        if (Overlay(0, dragged) is { } preview)
        {
            preview.Place(UiOverlaySpots.Centered(preview.Measure(default), cursor));
            preview.Emit(_draw);
        }

        if (Overlay(1, tooltip) is { } tip)
        {
            var screen = new UiSize(_screen.Width / Scale, _screen.Height / Scale);
            tip.Place(UiOverlaySpots.Beside(tip.Measure(default), cursor, screen));
            tip.Emit(_draw);
        }
    }

    /// <summary>
    /// Draws each debug label over its place in the world as <paramref name="camera"/> sees it, centred just above it
    /// on a dark backing, and leaves out those behind the camera. Over the overlays, and gone the next frame with them.
    /// </summary>
    internal void ShowDebugLabels(ReadOnlySpan<DebugLabel> labels, in Camera camera) =>
        DebugLabels.Draw(labels, camera, (_draw, Atlas, Scale), new Vector2(_screen.Width, _screen.Height));

    /// <summary>Asks for layout on the next update, for what changes without a new description.</summary>
    internal void RequestLayout() => _layoutWanted = true;

    /// <summary>Writes the texture of each image quad, which only the renderer knows.</summary>
    internal void BindImages(Func<GameImage, int> textureIndex) => _draw.BindImages(textureIndex);

    /// <summary>
    /// Brings the node in a slot up to date with a view: the same node when the view's type
    /// is the same, a new one when it isn't, and none for no view. Returns whether anything changed.
    /// </summary>
    private bool Mount(ref UiNode? node, UiView? view)
    {
        if (view is null)
        {
            bool had = node is not null;
            node?.Unmount();
            node = null;
            return had;
        }

        if (node is not null && node.View.GetType() == view.GetType())
        {
            return node.Update(view, TextStyle.Default);
        }

        node?.Unmount();
        node = new UiNode(this, null, view);
        node.Update(view, TextStyle.Default);
        return true;
    }

    private UiNode? Overlay(int slot, UiView? view)
    {
        Mount(ref _overlays[slot], view);
        var node = _overlays[slot];
        node?.ForgetLayout();
        return node;
    }

    private void Layout()
    {
        _draw.Clear();
        if (_root is null)
        {
            return;
        }

        _root.ForgetLayout();
        float wide = _screen.Width / Scale;
        float high = _screen.Height / Scale;
        var screen = new UiRect(0f, 0f, wide, high);
        _root.Place(screen.Align(_root.Measure(new Proposal(wide, high)), Alignment.Center));
        _root.Emit(_draw);
        _draw.EndTree();
    }
}
