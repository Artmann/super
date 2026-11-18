using Rendition;

namespace Super;

/// <summary>The quality a GPU is guessed to suit, from its kind and memory. See <see cref="Graphics.Recommend"/>.</summary>
internal static class QualityGuess
{
    private const long Gigabyte = 1L << 30;

    public static GraphicsQuality For(GpuInfo gpu) => gpu.Kind switch
    {
        GpuKind.Integrated or GpuKind.Virtual => GraphicsQuality.Low,
        GpuKind.Discrete => gpu.MemoryBytes switch
        {
            < 3 * Gigabyte => GraphicsQuality.Low,
            < 6 * Gigabyte => GraphicsQuality.Medium,
            < 12 * Gigabyte => GraphicsQuality.High,
            _ => GraphicsQuality.Ultra,
        },
        _ => GraphicsQuality.High,
    };
}
