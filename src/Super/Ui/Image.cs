using System.Numerics;
using Rendition;

namespace Super.Ui;

/// <summary>
/// A picture named by <see cref="Content.Image(string)"/>, or painted in code and handed to
/// <see cref="Content.Image(int, int, byte[])"/>. Its size is known at once, so layout never waits for it.
/// </summary>
public sealed class GameImage
{
    internal GameImage(string path, int width, int height, byte[] cooked, int index)
    {
        Path = path;
        Width = width;
        Height = height;
        Cooked = cooked;
        Index = index;
    }

    internal GameImage(int width, int height, byte[] pixels, int index)
    {
        Path = "painted";
        Width = width;
        Height = height;
        Pixels = pixels;
        Index = index;
    }

    /// <summary>The name of the cooked texture, such as <c>icons/axe.png</c>, or <c>painted</c> for one made in code.</summary>
    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The cooked texture, header and pixels, which the window builds mips from in the background.</summary>
    internal byte[]? Cooked { get; }

    /// <summary>A painted image's pixels: rows from the top, four bytes each, in sRGB with straight alpha.</summary>
    internal byte[]? Pixels { get; }

    /// <summary>
    /// Its pixels: rows from the top, four bytes each (red, green, blue, alpha), in sRGB with
    /// straight alpha. A cooked image is read afresh each time, so keep what's returned rather
    /// than asking every frame; it's for painting new images from, not for drawing. An image
    /// cooked compressed, as images are unless <c>compressTextures</c> is off, gives its decoded
    /// pixels, which are close to the file's but not the same.
    /// </summary>
    public byte[] ReadPixels() => Pixels is { } painted
        ? (byte[])painted.Clone()
        : CookedTexture.Read(Cooked!, Path).DecodePixels();

    /// <summary>Its place among everything <see cref="Content"/> has named.</summary>
    internal int Index { get; }
}

/// <summary>How an image fills a frame of a different shape.</summary>
public enum ImageFit
{
    /// <summary>Fills the frame, changing the image's shape if it has to.</summary>
    Stretch,

    /// <summary>All of the image shows, as large as fits, centered.</summary>
    Contain,

    /// <summary>The frame is filled and the image keeps its shape, so its edges may be cut off.</summary>
    Cover,
}

/// <summary>
/// Draws an image at its natural size. Inside a <see cref="UiExtensions.Frame"/> it takes
/// the frame's size instead, and if only one length is set it keeps its shape.
/// <paramref name="Tint"/> multiplies its colors.
/// </summary>
public sealed record Image(GameImage Source, ImageFit Fit = ImageFit.Stretch, Color? Tint = null) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        if (FrameOf(node) is not { } frame)
        {
            return new UiSize(Source.Width, Source.Height);
        }

        float? width = frame.Width is null ? null : Finite(proposal.Width);
        float? height = frame.Height is null ? null : Finite(proposal.Height);
        float aspect = Source.Width / (float)Source.Height;
        return (width, height) switch
        {
            ({ } w, { } h) => new UiSize(w, h),
            ({ } w, null) => new UiSize(w, w / aspect),
            (null, { } h) => new UiSize(h * aspect, h),
            _ => new UiSize(Source.Width, Source.Height),
        };
    }

    internal override bool Blocks => true;

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiTree tree)
    {
        var (rectangle, uvMin, uvMax) = Fitted(node.Frame);
        tree.AddImage(rectangle, uvMin, uvMax, Tint ?? Color.White, Source);
    }

    /// <summary>The frame that decides this image's size, through anything that only wraps it.</summary>
    private static FrameModifier? FrameOf(UiNode node)
    {
        for (var parent = node.Parent; parent?.View is Modifier modifier; parent = parent.Parent)
        {
            if (modifier is FrameModifier frame)
            {
                return frame;
            }
        }

        return null;
    }

    private static float? Finite(float? length) => length is { } value && float.IsFinite(value) ? value : null;

    private (UiRect Rectangle, Vector2 UvMin, Vector2 UvMax) Fitted(UiRect frame)
    {
        float imageAspect = Source.Width / (float)Source.Height;
        float frameAspect = frame.Width / Math.Max(frame.Height, 1f);
        if (Fit == ImageFit.Contain)
        {
            var size = imageAspect > frameAspect
                ? new UiSize(frame.Width, frame.Width / imageAspect)
                : new UiSize(frame.Height * imageAspect, frame.Height);
            return (frame.Align(size, Alignment.Center), Vector2.Zero, Vector2.One);
        }

        if (Fit == ImageFit.Cover)
        {
            var visible = imageAspect > frameAspect
                ? new Vector2(frameAspect / imageAspect, 1f)
                : new Vector2(1f, imageAspect / frameAspect);
            var start = (Vector2.One - visible) * 0.5f;
            return (frame, start, start + visible);
        }

        return (frame, Vector2.Zero, Vector2.One);
    }
}
