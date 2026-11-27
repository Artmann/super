using Rendition;
using Super.Ui;

namespace Super;

/// <summary>Keeps the renderer's overlay in step with the view's interface: the glyph atlas, and the textures its images draw with.</summary>
internal sealed class OverlayUpkeep
{
    private readonly Renderer _renderer;

    /// <summary>Kept, since asking for a new one each frame would be garbage.</summary>
    private readonly Func<GameImage, int> _textureOf;

    private int _uploadedAtlas;
    private Content? _content;

    public OverlayUpkeep(Renderer renderer, UploadedItems items)
    {
        _renderer = renderer;
        _textureOf = image => OverlayQuad.For(items.Require<TextureOnGpu>(image.Index, image.Generation, _content!).Handle);
    }

    /// <summary>Uploads the glyph atlas when it gained characters, and points the interface's images at their textures.</summary>
    public void Update(View view, Content content)
    {
        // Only a character that hasn't been drawn before brings this here.
        // Games with no interface never get here, and never load the fonts.
        if (view.Effects.Ui is { Quads.IsEmpty: false } ui && ui.Atlas.Version != _uploadedAtlas)
        {
            _renderer.Assets.SetOverlayAtlas(new TextureData
            {
                Width = GlyphAtlas.Size,
                Height = GlyphAtlas.Size,
                Kind = TextureKind.Linear,
                Mips = [ui.Atlas.Pixels],
            });
            _uploadedAtlas = ui.Atlas.Version;
        }

        if (view.Effects.Ui is { } shown)
        {
            _content = content;
            shown.BindImages(_textureOf);
        }
    }
}
