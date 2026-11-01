using System.Numerics;
using Rendition;

namespace Super.Ui;

/// <summary>A picture named by <see cref="Content.Image"/>. Its size is known at once, so layout never waits for it.</summary>
public sealed class GameImage
{
    internal GameImage(string path, int width, int height, byte[] file, int index)
    {
        Path = path;
        Width = width;
        Height = height;
        File = file;
        Index = index;
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The file as it was read, which the window decodes in the background.</summary>
    internal byte[] File { get; }

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
