using System.Numerics;
using Rendition;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Images drawn as 9-slices, and themes that draw their cards, popups and buttons from them.</summary>
public sealed class PanelTests
{
    private const int ArtTexture = 4321;

    /// <summary>A 30 by 30 painted image, its slices 10 pixels in on every side.</summary>
    private static GameImage Art() => new Content(new MemoryContent()).UiImages.Image(30, 30, new byte[30 * 30 * 4]);

    /// <summary>The quads drawn from images, in the order they were drawn.</summary>
    private static OverlayQuad[] ArtQuads(UiTree tree)
    {
        tree.BindImages(_ => ArtTexture);
        return [.. tree.Quads.ToArray().Where(quad => quad.TextureIndex == ArtTexture)];
    }

    private static OverlayQuad[] Sliced(UiView view, int width = 800, int height = 600)
    {
        var tree = new UiTree();
        tree.Update(view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading), width, height);
        return ArtQuads(tree);
    }

    [Fact]
    public void A_sliced_image_keeps_its_corners_and_stretches_its_edges_and_middle()
    {
        var pieces = Sliced(new Image(Art()) { Slices = 10f }.Frame(width: 200f, height: 100f));

        Assert.Equal(9, pieces.Length);
        Assert.Equal((new Vector2(0f, 0f), new Vector2(10f, 10f)), (pieces[0].Position, pieces[0].Size));
        Assert.Equal((new Vector2(10f, 0f), new Vector2(180f, 10f)), (pieces[1].Position, pieces[1].Size));
        Assert.Equal((new Vector2(10f, 10f), new Vector2(180f, 80f)), (pieces[4].Position, pieces[4].Size));
        Assert.Equal((new Vector2(190f, 90f), new Vector2(10f, 10f)), (pieces[8].Position, pieces[8].Size));

        // The corner shows the image's top-left third, and the middle its middle third.
        Assert.Equal(new Vector2(0f, 0f), pieces[0].UvMin);
        Assert.Equal(new Vector2(1f / 3f, 1f / 3f), pieces[0].UvMax);
        Assert.Equal(new Vector2(1f / 3f, 1f / 3f), pieces[4].UvMin);
        Assert.Equal(2f / 3f, pieces[4].UvMax.X, 0.0001f);
        Assert.Equal(2f / 3f, pieces[4].UvMax.Y, 0.0001f);
    }

    [Fact]
    public void A_sliced_image_fills_the_room_it_is_offered()
    {
        var pieces = Sliced(new Image(Art()) { Slices = 10f }, 300, 120);

        Assert.Equal(new Vector2(290f, 110f), pieces[^1].Position);
    }

    [Fact]
    public void Slice_scale_draws_the_corners_bigger()
    {
        var pieces = Sliced(new Image(Art()) { Slices = 10f, SliceScale = 2f }.Frame(width: 200f, height: 100f));

        Assert.Equal(new Vector2(20f, 20f), pieces[0].Size);
        Assert.Equal(new Vector2(160f, 60f), pieces[4].Size);
    }

    [Fact]
    public void Corners_too_big_for_the_frame_shrink_together_and_the_middle_goes()
    {
        var pieces = Sliced(new Image(Art()) { Slices = new ImageSlices(10f, 30f, 10f, 10f) }.Frame(width: 20f, height: 100f));

        // 10 and 30 across 20 shrink to 5 and 15, and there's no middle column left between them.
        Assert.Equal(6, pieces.Length);
        Assert.Equal(new Vector2(5f, 10f), pieces[0].Size);
        Assert.Equal((new Vector2(5f, 0f), new Vector2(15f, 10f)), (pieces[1].Position, pieces[1].Size));
    }

    private static Theme Drawn(GameImage art, bool tinted = false)
    {
        var panel = new PanelArt(art, 10f) { Tinted = tinted };
        return Theme.Dark with { Panels = new ThemePanels { Card = panel, Popup = panel, Button = panel } };
    }

    [Fact]
    public void A_card_in_a_theme_with_card_art_is_drawn_on_it_round_its_content()
    {
        var tree = new UiTree();
        tree.Update(new Card { new Text("Inventory") }.Theme(Drawn(Art())), 800, 600);
        var pieces = ArtQuads(tree);
        var text = Assert.Single(tree.ShownText);

        Assert.Equal(9, pieces.Length);
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), pieces[4].Color);
        Assert.Equal(text.Frame.X - 16f, pieces[0].Position.X, 0.01f);
        Assert.Equal(text.Frame.Y - 16f, pieces[0].Position.Y, 0.01f);
        Assert.DoesNotContain(tree.Quads.ToArray(), quad => quad.Kind == OverlayKind.Shadow);
    }

    [Fact]
    public void Tinted_art_takes_the_colour_the_box_would_have()
    {
        var tree = new UiTree();
        tree.Update(new Card { new Text("Inventory") }.Theme(Drawn(Art(), tinted: true)), 800, 600);

        var surface = Theme.Dark.Colors.Surface;
        Assert.All(ArtQuads(tree), quad => Assert.Equal(new Vector4(surface.R, surface.G, surface.B, surface.A), quad.Color));
    }

    [Fact]
    public void A_theme_without_panels_draws_cards_as_boxes()
    {
        var tree = new UiTree();
        tree.Update(new Card { new Text("Inventory") }, 800, 600);

        Assert.Empty(ArtQuads(tree));
        Assert.Contains(tree.Quads.ToArray(), quad => quad.Kind == OverlayKind.Shadow);
    }

    [Fact]
    public void A_filled_button_is_drawn_on_button_art_in_its_style_colour_when_tinted()
    {
        var tree = new UiTree();
        tree.Update(new Button("Play") { Style = ButtonStyle.Destructive }.Theme(Drawn(Art(), tinted: true)), 800, 600);

        var pieces = ArtQuads(tree);
        var red = Theme.Dark.Colors.Destructive;
        Assert.Equal(9, pieces.Length);
        Assert.Equal(new Vector4(red.R, red.G, red.B, red.A), pieces[4].Color);
        Assert.Equal(Theme.Dark.Regular.Height, pieces[0].Size.Y + pieces[3].Size.Y + pieces[6].Size.Y, 0.01f);
    }

    [Fact]
    public void An_outline_button_keeps_its_box_in_a_theme_with_button_art()
    {
        var tree = new UiTree();
        tree.Update(new Button("Back") { Style = ButtonStyle.Outline }.Theme(Drawn(Art())), 800, 600);

        Assert.Empty(ArtQuads(tree));
    }

    [Fact]
    public void A_dialog_is_drawn_on_popup_art()
    {
        var tree = new UiTree();
        var view = new Text("Under").Alert(true, () => { }, new Dialog("Quit?", "Your progress is saved.")).Theme(Drawn(Art()));
        tree.Update(view, 800, 600);
        tree.Update(view, 800, 600);

        var title = tree.ShownText.Single(shown => shown.Text == "Quit?");
        var pieces = ArtQuads(tree);
        Assert.NotEmpty(pieces);
        Assert.True(pieces[0].Position.X < title.Frame.X && pieces[0].Position.Y < title.Frame.Y);
    }
}
