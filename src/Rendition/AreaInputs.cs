namespace Rendition;

/// <summary>
/// What a frame's baked areas are worked out from besides the view and its draws: each part's bounds, the hash of
/// the environment, and where each light has been, so a light that moves is known to be moving. The frame's bakes and
/// the bake jobs share it.
/// </summary>
internal sealed class AreaInputs(Func<DetailHandle, BakeSphere> bounds, Func<ulong> environmentHash)
{
    /// <summary>Follows each light from frame to frame, and says once they have all held still.</summary>
    public AnimatedLights Lights { get; } = new(Console.Error);

    /// <summary>Works out the view's baked areas with <paramref name="resolver"/>.</summary>
    public BakeFrame ResolveAreas(BakeResolver resolver, in FrameView view, ReadOnlySpan<DrawItem> draws) =>
        resolver.Resolve(view, draws, bounds, environmentHash(), Lights);
}
