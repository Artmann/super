using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;

namespace Super.Ui;

/// <summary>
/// What an interface draws in a frame: the quads the renderer gets, and the text that
/// shows, which tests read. Views add to it in interface units, and it writes pixels.
/// The tree's own drawing comes first, and the overlays above it can be redrawn alone.
/// </summary>
internal sealed class UiDrawList
{
    private readonly List<OverlayQuad> _quads = [];
    private readonly List<ShownText> _shownText = [];
    private readonly List<(int Quad, GameImage Picture)> _imageQuads = [];
    private (int Quads, int Images, int Texts) _treeEnd;
#if !DEBUG
    private bool _reportedTooManyQuads;
#endif

    /// <summary>How many pixels one interface unit covers.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>The clips and opacities that wrap what's drawn now.</summary>
    public UiDrawNesting Nesting { get; } = new();

    public ReadOnlySpan<OverlayQuad> Quads => CollectionsMarshal.AsSpan(_quads);

    public IReadOnlyList<ShownText> ShownText => _shownText;

    /// <summary>Forgets everything, before the tree draws again.</summary>
    public void Clear()
    {
        _quads.Clear();
        _imageQuads.Clear();
        _shownText.Clear();
        _treeEnd = default;
    }

    /// <summary>Marks where the tree's drawing ends and the overlays begin.</summary>
    public void EndTree() => _treeEnd = (_quads.Count, _imageQuads.Count, _shownText.Count);

    /// <summary>Takes away the overlays, keeping what the tree drew.</summary>
    public void TrimToTree()
    {
        if (_quads.Count > _treeEnd.Quads)
        {
            _quads.RemoveRange(_treeEnd.Quads, _quads.Count - _treeEnd.Quads);
        }

        _imageQuads.RemoveRange(_treeEnd.Images, _imageQuads.Count - _treeEnd.Images);
        _shownText.RemoveRange(_treeEnd.Texts, _shownText.Count - _treeEnd.Texts);
    }

    public void AddQuad(UiRect frame, Vector2 uvMin, Vector2 uvMax, Color color)
    {
        Add(frame, new OverlayQuad { UvMin = uvMin, UvMax = uvMax, Color = Nesting.Fade(color) });
    }

    /// <summary>Draws part of an image. The renderer says which texture that is at <see cref="BindImages"/>.</summary>
    public void AddImage(UiRect frame, Vector2 uvMin, Vector2 uvMax, Color tint, GameImage image)
    {
        int quad = _quads.Count;
        AddQuad(frame, uvMin, uvMax, tint);
        if (_quads.Count > quad)
        {
            _imageQuads.Add((quad, image));
        }
    }

    /// <summary>Writes the texture of each image quad, which only the renderer knows.</summary>
    public void BindImages(Func<GameImage, int> textureIndex)
    {
        var quads = CollectionsMarshal.AsSpan(_quads);
        foreach (var (quad, image) in _imageQuads)
        {
            quads[quad].TextureIndex = textureIndex(image);
        }
    }

    public void AddText(string text, UiRect frame, in TextLook style)
    {
        frame = Nesting.Place(frame);
        if (!Nesting.Hides(frame))
        {
            _shownText.Add(new ShownText(text, frame, style.Color with { A = style.Color.A * Nesting.Opacity }, style.Size * Nesting.Zoom, style.Bold, style.Font, style.Weight, style.Italic));
        }
    }

    /// <summary>Adds a quad laid out in interface units, unless it's out of sight, faded already by the caller.</summary>
    public void Add(UiRect frame, OverlayQuad quad)
    {
        // Rows out of sight are still laid out, but they cost the renderer nothing.
        frame = Nesting.Place(frame);
        if (Nesting.Hides(frame))
        {
            return;
        }

        if (_quads.Count >= OverlayQuad.MaxPerFrame)
        {
            ReportTooManyQuads();
            return;
        }

        // Laid out in interface units, drawn in pixels.
        quad.Position = new Vector2(frame.X, frame.Y) * Scale;
        quad.Size = new Vector2(frame.Width, frame.Height) * Scale;
        ToPixels(ref quad);
        if (Nesting.Clip is { } active)
        {
            quad.Clip = new Vector4(active.Rect.X, active.Rect.Y, active.Rect.Width, active.Rect.Height) * Scale;
            quad.ClipRadii = active.Radius.ToVector() * Scale;
        }

        _quads.Add(quad);
    }

    /// <summary>Turns a quad's lengths and places into pixels. Lengths shrink with a scaled view too, and a shadow's rectangles are places, which move with it.</summary>
    private void ToPixels(ref OverlayQuad quad)
    {
        float length = Scale * Nesting.Zoom;
        quad.Radii *= length;
        quad.BorderWidth *= length;
        quad.Thickness *= length;
        if (quad.Kind == OverlayKind.Shadow)
        {
            quad.Params = Pixels(quad.Params);
            quad.GradientPoints = Pixels(quad.GradientPoints);
        }
        else
        {
            quad.GradientPoints *= length;
        }

        // An arc's are angles. Everything else's are lengths.
        if (quad.Kind is not (OverlayKind.Arc or OverlayKind.Shadow))
        {
            quad.Params *= length;
        }
    }

    /// <summary>A rectangle laid out in interface units, as x, y, width and height, where it's drawn in pixels.</summary>
    private Vector4 Pixels(Vector4 rectangle)
    {
        var drawn = Nesting.Place(new UiRect(rectangle.X, rectangle.Y, rectangle.Z, rectangle.W));
        return new Vector4(drawn.X, drawn.Y, drawn.Width, drawn.Height) * Scale;
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
}
