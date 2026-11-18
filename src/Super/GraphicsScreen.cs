using Rendition;

namespace Super;

/// <summary>How frames reach the screen: how fast they come, how large the scene is drawn and what smooths its edges.</summary>
public sealed class GraphicsScreen
{
    /// <summary>
    /// Waits for the display before showing each frame, so the picture never tears and the frame rate
    /// stops at the display's, often 60. Off, the default, frames come as fast as the computer can draw
    /// them, and fast movement may tear. <c>--vsync</c> turns it on. Only play in a window follows it:
    /// recordings and profiles always wait for the display, and screenshots never do.
    /// </summary>
    public bool Vsync { get; set; }

    /// <summary>
    /// The most frames drawn in a second, or 0 for no limit. A limit saves power and keeps a
    /// laptop cool, and with vsync off it's how to stop at a steady rate. Only play in a window
    /// is limited; screenshots, recordings and profiles run at their own pace.
    /// </summary>
    public int MaxFramesPerSecond
    {
        get;
        set => field = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame limit can't be negative. Use 0 for no limit.");
    }

    /// <summary>
    /// How large the scene is drawn against the window, from 0.5 to 2 each way; 1 draws it at the window's
    /// size. At 2 it's drawn with four times the pixels and shrunk to fit, which smooths thin things such as
    /// grass and railings, at about four times the cost of lighting and effects. At 0.5 it's drawn with a
    /// quarter of the pixels and stretched, which is much faster and softer. The interface and screenshots
    /// stay at the window's size. <c>--render-scale</c> sets it from the command line. A change while the
    /// game runs rebuilds the scene's targets before the next frame.
    /// </summary>
    public float RenderScale
    {
        get;
        set => field = Renderer.CheckRenderScale(value);
    } = 1f;

    /// <summary>
    /// What smooths the scene's edges when <see cref="AntiAliasing"/> is on. <see cref="Upscaler.Taa"/> blends
    /// each frame with the last. <see cref="Upscaler.Fsr"/> hands the frame to AMD FSR, which rebuilds it at the
    /// window's size from the scene and its own history, so a <see cref="RenderScale"/> below 1 comes back
    /// sharper than a stretch. FSR needs amd_fidelityfx_vk.dll next to the game and is used only at a render
    /// scale of 1 or less; otherwise TAA is used. <c>--upscaler</c> sets it from the command line, and
    /// <see cref="Graphics.Fastest"/> uses TAA.
    /// </summary>
    public Upscaler Upscaler { get; set; } = Upscaler.Taa;

    /// <summary>Smoothing jagged edges over several frames. Off, edges show their pixels, and nothing blurs as the camera moves.</summary>
    public bool AntiAliasing { get; set; } = true;

    /// <summary>Takes every setting from <paramref name="other"/>.</summary>
    internal void CopyFrom(GraphicsScreen other)
    {
        Vsync = other.Vsync;
        MaxFramesPerSecond = other.MaxFramesPerSecond;
        RenderScale = other.RenderScale;
        Upscaler = other.Upscaler;
        AntiAliasing = other.AntiAliasing;
    }
}
