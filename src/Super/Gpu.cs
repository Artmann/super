using Rendition;

namespace Super;

/// <summary>
/// The GPU the game is drawn with, for an options menu to show and to pick settings from. Ask for it in a
/// constructor. The window fills it in once it has made its device, so it is known from the first
/// <see cref="IGame.Update"/>, not in <see cref="IGame.Load"/>, and stays <see cref="GpuKind.Unknown"/> in tests
/// without a window.
/// </summary>
public sealed class Gpu
{
    /// <summary>The name the driver gives it, such as "NVIDIA GeForce RTX 4060 Ti", or empty while it isn't known.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Whether it is a card of its own, built into the processor or virtual.</summary>
    public GpuKind Kind { get; private set; }

    /// <summary>The size of its own memory, in bytes. On an integrated GPU this is a share of the computer's memory.</summary>
    public long MemoryBytes { get; private set; }

    /// <summary>Whether it traces rays, which the light probes and the baker use when they can.</summary>
    public bool CanTrace { get; private set; }

    /// <summary>The quality <see cref="Graphics.Recommend"/> picks for this GPU.</summary>
    public GraphicsQuality RecommendedQuality => Graphics.Recommend(Info);

    /// <summary>Everything above as one value.</summary>
    public GpuInfo Info => new(Name, Kind, MemoryBytes, CanTrace);

    internal void Fill(GpuInfo info)
    {
        (Name, Kind, MemoryBytes, CanTrace) = (info.Name, info.Kind, info.MemoryBytes, info.CanTrace);
    }
}
