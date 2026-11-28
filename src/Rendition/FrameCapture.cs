namespace Rendition;

/// <summary>
/// A frame's picture being read back, from <see cref="Renderer.RequestCapture"/>. The next frame drawn copies it, and it
/// is ready once a later frame has waited for the GPU to finish that one, so nothing waits for it in between.
/// </summary>
public sealed class FrameCapture
{
    private byte[]? _pixels;

    internal FrameCapture()
    {
    }

    /// <summary>Whether the picture has been read back.</summary>
    public bool IsReady => _pixels is not null;

    /// <summary>How many pixels across the picture is, once it is ready.</summary>
    public int Width { get; private set; }

    /// <summary>How many pixels down the picture is, once it is ready.</summary>
    public int Height { get; private set; }

    /// <summary>The picture as RGBA bytes, row by row from the top, every pixel opaque. Throws until it is ready.</summary>
    public byte[] Pixels => _pixels ?? throw new InvalidOperationException("The capture isn't ready yet: draw more frames.");

    /// <summary>The frame slot whose frame copied the picture, or -1 until a frame has.</summary>
    internal int Frame { get; set; } = -1;

    internal void Complete(byte[] pixels, int width, int height)
    {
        (Width, Height) = (width, height);
        _pixels = pixels;
    }
}
