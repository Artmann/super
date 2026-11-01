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
    private readonly List<(int Quad, GameImage Image)> _imageQuads = [];
    private readonly List<Clip?> _clips = [];
    private readonly List<float> _opacities = [];
    private GlyphAtlas? _atlas;
    private UiNode? _root;
    private int _width;
    private int _height;
    private Clip? _clip;
    private float _opacity = 1f;
    private bool _layoutWanted;
#if !DEBUG
    private bool _reportedTooManyQuads;
#endif

    public IReadOnlyList<ShownText> ShownText => _shownText;

    internal GlyphAtlas Atlas => _atlas ??= new GlyphAtlas();

    internal ReadOnlySpan<OverlayQuad> Quads => CollectionsMarshal.AsSpan(_quads);

    /// <summary>Shows this description at this size. Pass null to show nothing.</summary>
    public void Update(UiView? view, int width, int height)
    {
        bool changed = Reconcile(view) | width != _width | height != _height | _layoutWanted;
        _width = width;
        _height = height;
        if (changed)
        {
            _layoutWanted = false;
            Layout();
        }
    }

    /// <summary>Asks for layout on the next update, for what changes without a new description.</summary>
    internal void RequestLayout() => _layoutWanted = true;

    /// <summary>Draws a filled rectangle, rounded and bordered if asked to be.</summary>
    internal void AddBox(UiRect frame, Color? fill, float radius, float borderWidth, Color border)
    {
        var uv = Atlas.SolidUv;
        var color = fill ?? default;
        Add(frame, new OverlayQuad
        {
            UvMin = uv,
            UvMax = uv,
            Color = Faded(color),
            BorderColor = Faded(border),
            BorderWidth = borderWidth,
            CornerRadius = radius,
        });
    }

    internal void AddQuad(UiRect frame, Vector2 uvMin, Vector2 uvMax, Color color)
    {
        Add(frame, new OverlayQuad { UvMin = uvMin, UvMax = uvMax, Color = Faded(color) });
    }

    /// <summary>Draws part of an image. The renderer says which texture that is at <see cref="BindImages"/>.</summary>
    internal void AddImage(UiRect frame, Vector2 uvMin, Vector2 uvMax, Color tint, GameImage image)
    {
        int quad = _quads.Count;
        AddQuad(frame, uvMin, uvMax, tint);
        if (_quads.Count > quad)
        {
            _imageQuads.Add((quad, image));
        }
    }

    /// <summary>Writes the texture of each image quad, which only the renderer knows.</summary>
    internal void BindImages(Func<GameImage, int> textureIndex)
    {
        var quads = CollectionsMarshal.AsSpan(_quads);
        foreach (var (quad, image) in _imageQuads)
        {
            quads[quad].TextureIndex = textureIndex(image);
        }
    }

    internal void AddText(string text, UiRect frame)
    {
        if (_clip is not { } clip || clip.Rect.Overlaps(frame))
        {
            _shownText.Add(new ShownText(text, frame));
        }
    }

    /// <summary>
    /// Everything until the matching <see cref="PopClip"/> stays inside this rectangle.
    /// A quad has room for one clip, so nested ones intersect and keep the innermost
    /// radius that's set.
    /// </summary>
    internal void PushClip(UiRect rectangle, float radius)
    {
        _clips.Add(_clip);
        _clip = _clip is { } outer
            ? new Clip(outer.Rect.Intersect(rectangle), radius > 0f ? radius : outer.Radius)
            : new Clip(rectangle, radius);
    }

    internal void PopClip()
    {
        _clip = _clips[^1];
        _clips.RemoveAt(_clips.Count - 1);
    }

    /// <summary>Multiplies everything until the matching <see cref="PopOpacity"/>.</summary>
    internal void PushOpacity(float opacity)
    {
        _opacities.Add(_opacity);
        _opacity *= opacity;
    }

    internal void PopOpacity()
    {
        _opacity = _opacities[^1];
        _opacities.RemoveAt(_opacities.Count - 1);
    }

    private Vector4 Faded(Color color) => new(color.R, color.G, color.B, color.A * _opacity);

    private void Add(UiRect frame, OverlayQuad quad)
    {
        // A long list costs what's on screen, not what's in it.
        if (_clip is { } clip && !clip.Rect.Overlaps(frame))
        {
            return;
        }

        if (_quads.Count >= OverlayQuad.MaxPerFrame)
        {
            ReportTooManyQuads();
            return;
        }

        quad.Position = new Vector2(frame.X, frame.Y);
        quad.Size = new Vector2(frame.Width, frame.Height);
        if (_clip is { } active)
        {
            quad.Clip = new Vector4(active.Rect.X, active.Rect.Y, active.Rect.Width, active.Rect.Height);
            quad.ClipRadius = active.Radius;
        }

        _quads.Add(quad);
    }

    private void ReportTooManyQuads()
    {
        string message = $"This interface draws more than {OverlayQuad.MaxPerFrame} quads, and each character of text is one. The rest won't show.";
#if DEBUG
        throw new InvalidOperationException(message);
#else
        if (!_reportedTooManyQuads)
        {
            _reportedTooManyQuads = true;
            Console.Error.WriteLine(message);
        }
#endif
    }

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
        _imageQuads.Clear();
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

    private readonly record struct Clip(UiRect Rect, float Radius);
}
