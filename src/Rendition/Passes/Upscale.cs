using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// The parts of upscaling that need no GPU: how the camera's sub-pixel shift moves from frame to frame, and
/// which upscaler a frame really gets. See "Upscaling" in docs/design.md.
/// </summary>
internal static class Upscale
{
    /// <summary>
    /// How many different shifts the camera goes through before it repeats, for a scene drawn at
    /// <paramref name="scale"/> of the window's size each way: 8 at the window's size, and more as the scene
    /// gets smaller, so every window pixel is still landed on. It's FSR's rule, ceil(8 · (1 / scale)²).
    /// </summary>
    public static int JitterPhaseCount(float scale)
    {
        double ratio = 1d / scale;
        // A whisker off, so a ratio such as 2 that floats carry as 2.0000001 doesn't round up a whole phase.
        return Math.Max((int)Math.Ceiling(8d * ratio * ratio - 1e-4), 1);
    }

    /// <summary>
    /// The camera's shift for <paramref name="frame"/>, in fractions of a scene pixel from -0.5 to 0.5, with x to
    /// the right and y down. It's the Halton (2, 3) sequence from its first point, starting again every
    /// <paramref name="phases"/> frames: the same points FSR's own jitter query gives.
    /// </summary>
    public static Vector2 Jitter(int frame, int phases)
    {
        int index = (int)((uint)frame % (uint)phases) + 1;
        return new Vector2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f);
    }

    /// <summary>
    /// The upscaler a frame really gets when <paramref name="wanted"/> was asked for. FSR needs its library, and
    /// without it the frame falls back to TAA and says so on <paramref name="log"/>. The renderer asks once each
    /// time the wish changes, so the line is written once.
    /// </summary>
    public static Upscaler Choose(Upscaler wanted, bool libraryLoaded, TextWriter log)
    {
        if (wanted != Upscaler.Fsr || libraryLoaded)
        {
            return wanted;
        }

        log.WriteLine($"FSR upscaling was asked for, but {Vulkan.Fsr.LibraryName} could not be loaded, so anti-aliasing uses TAA instead.");
        return Upscaler.Taa;
    }

    private static float Halton(int index, int radix)
    {
        float result = 0f;
        float fraction = 1f;
        for (; index > 0; index /= radix)
        {
            fraction /= radix;
            result += fraction * (index % radix);
        }

        return result;
    }
}
