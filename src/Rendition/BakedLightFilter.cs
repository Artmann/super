namespace Rendition;

/// <summary>
/// The frame's lights without those a fresh bake takes over. A Baked light whose bake is still fading in stays, dimmed
/// as the bake comes, so the two always add up. Reuses its arrays, so a frame allocates nothing once they have grown.
/// </summary>
internal sealed class BakedLightFilter
{
    // A Baked light while its bake fades in, its radiance scaled by what the bake doesn't give yet.
    private static readonly Func<DirectionalLight, float, DirectionalLight> DimSun = static (sun, left) =>
    {
        sun.Radiance *= left;
        return sun;
    };

    private static readonly Func<LocalLight, float, LocalLight> DimLocalLight = static (light, left) =>
    {
        light.Radiance *= left;
        return light;
    };

    private DirectionalLight[] _keptSuns = [];
    private LocalLight[] _keptLights = [];

    /// <summary>
    /// True when <paramref name="bakes"/> takes some of the view's lights over: then <paramref name="lit"/> is the view
    /// without them.
    /// </summary>
    public bool TryWithout(in FrameView view, BakeFrame bakes, out FrameView lit)
    {
        lit = default;
        if (bakes.DroppedSuns.Count == 0 && bakes.DroppedLocalLights.Count == 0
            && bakes.FadingSuns.Count == 0 && bakes.FadingLocalLights.Count == 0)
        {
            return false;
        }

        lit = view;
        lit.Lights = BakeFreshness.Without(view.Lights, bakes.DroppedSuns, bakes.FadingSuns, DimSun, ref _keptSuns);
        lit.LocalLights = BakeFreshness.Without(
            view.LocalLights, bakes.DroppedLocalLights, bakes.FadingLocalLights, DimLocalLight, ref _keptLights);
        return true;
    }
}
