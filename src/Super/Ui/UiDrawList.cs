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

    /// <summary>Draws a filled rectangle, rounded and bordered if asked to be.</summary>
    public void AddBox(UiRect frame, Color? fill, float radius, float borderWidth, Color border)
    {
        var uv = UiGlyphSheet.SolidUv;
        var color = fill ?? default;
        Add(frame, new OverlayQuad
        {
            UvMin = uv,
            UvMax = uv,
            Color = Nesting.Fade(color),
            BorderColor = Nesting.Fade(border),
            BorderWidth = borderWidth,
            CornerRadius = radius,
        });
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

    public void AddText(string text, UiRect frame, in TextStyle style)
    {
        if (!Nesting.Hides(frame))
        {
            _shownText.Add(new ShownText(text, frame, style.Color with { A = style.Color.A * Nesting.Opacity }, style.Size, style.Bold, style.Font));
        }
    }

    private void Add(UiRect frame, OverlayQuad quad)
    {
        // Rows out of sight are still laid out, but they cost the renderer nothing.
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
        quad.CornerRadius *= Scale;
        quad.BorderWidth *= Scale;
        if (Nesting.Clip is { } active)
        {
            quad.Clip = new Vector4(active.Rect.X, active.Rect.Y, active.Rect.Width, active.Rect.Height) * Scale;
            quad.ClipRadius = active.Radius * Scale;
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
}
