using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;

namespace Super.Ui;

/// <summary>A piece of text on screen, and where it landed.</summary>
public readonly record struct ShownText(string Text, UiRect Frame);

/// <summary>
/// Keeps an interface on screen: it reconciles each new description, lays out what
/// changed, and writes down the rectangles to draw. None of it touches a GPU, so an
/// interface can be tested with no window.
/// </summary>
public sealed class UiTree
{
    private readonly List<OverlayQuad> _quads = [];
    private readonly List<ShownText> _shownText = [];
    private GlyphAtlas? _atlas;
    private UiNode? _root;
    private int _width;
    private int _height;

    public IReadOnlyList<ShownText> ShownText => _shownText;

    internal GlyphAtlas Atlas => _atlas ??= new GlyphAtlas();

    internal ReadOnlySpan<OverlayQuad> Quads => CollectionsMarshal.AsSpan(_quads);

    /// <summary>Shows this description at this size. Pass null to show nothing.</summary>
    public void Update(UiView? view, int width, int height)
    {
        bool changed = Reconcile(view) | width != _width | height != _height;
        _width = width;
        _height = height;
        if (changed)
        {
            Layout();
        }
    }

    internal void AddRectangle(UiRect frame, UiColor color)
    {
        var uv = Atlas.SolidUv;
        AddQuad(frame, uv, uv, color);
    }

    internal void AddQuad(UiRect frame, Vector2 uvMin, Vector2 uvMax, UiColor color)
    {
        _quads.Add(new OverlayQuad
        {
            Position = new Vector2(frame.X, frame.Y),
            Size = new Vector2(frame.Width, frame.Height),
            UvMin = uvMin,
            UvMax = uvMax,
            Color = new Vector4(color.R, color.G, color.B, color.A),
        });
    }

    internal void AddText(string text, UiRect frame) => _shownText.Add(new ShownText(text, frame));

    private bool Reconcile(UiView? view)
    {
        if (view is null)
        {
            bool had = _root is not null;
            _root?.Unmount();
            _root = null;
            return had;
        }

        if (_root is not null && _root.View.GetType() == view.GetType())
        {
            return _root.Update(view, TextStyle.Default);
        }

        _root?.Unmount();
        _root = new UiNode(this, null, view);
        _root.Update(view, TextStyle.Default);
        return true;
    }

    private void Layout()
    {
        _quads.Clear();
        _shownText.Clear();
        if (_root is null)
        {
            return;
        }

        _root.ForgetLayout();
        var screen = new UiRect(0f, 0f, _width, _height);
        _root.Place(screen.Align(_root.Measure(new Proposal(_width, _height)), Alignment.Center));
        _root.Emit();
    }
}
