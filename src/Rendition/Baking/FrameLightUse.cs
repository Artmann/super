namespace Rendition;

/// <summary>
/// How a <see cref="BakeFrame"/> uses each light: its mode this frame, the Baked lights a fresh bake takes over, those
/// kept dimmed while their bake fades in, and the Mixed chunk lights that found no shadow mask slot. The resolver and
/// the renderer fill it; the frame's light members read it.
/// </summary>
internal sealed class FrameLightUse
{
    private readonly Dictionary<LightKey, LightMode> _modes = [];
    private readonly List<int> _droppedLocalLights = [];
    private readonly List<int> _droppedSuns = [];
    private readonly List<(int Index, float Fade)> _fadingLocalLights = [];
    private readonly List<(int Index, float Fade)> _fadingSuns = [];

    /// <summary>The indices, ascending, of the point and spot lights over a bake that has faded all the way in.</summary>
    public IReadOnlyList<int> DroppedLocalLights => _droppedLocalLights;

    /// <summary>The indices, ascending, of the suns over a space bake that has faded all the way in.</summary>
    public IReadOnlyList<int> DroppedSuns => _droppedSuns;

    /// <summary>
    /// The Baked lights over a fresh bake that is still fading in, with how far it has faded: the frame keeps them, their
    /// radiance scaled by 1 minus that, instead of dropping them, so the area never goes darker than either end.
    /// </summary>
    public IReadOnlyList<(int Index, float Fade)> FadingLocalLights => _fadingLocalLights;

    /// <summary>The suns kept dimmed the same way.</summary>
    public IReadOnlyList<(int Index, float Fade)> FadingSuns => _fadingSuns;

    /// <summary>The keys of the Mixed chunk lights that found no shadow mask slot, which the renderer fills.</summary>
    public List<int> Unslotted { get; } = [];

    /// <summary>The light's mode this frame, <see cref="LightMode.Realtime"/> for a light the frame doesn't have.</summary>
    public LightMode ModeOf(LightKey key) => _modes.TryGetValue(key, out var mode) ? mode : LightMode.Realtime;

    public void SetMode(LightKey key, LightMode mode) => _modes[key] = mode;

    /// <summary>A Baked light over a fresh bake: dropped once the bake is all there, kept dimmed while it fades in.</summary>
    public void DropLocalLight(int index, float fade) => Drop(index, fade, _droppedLocalLights, _fadingLocalLights);

    /// <summary>A Baked sun over a fresh space bake, as <see cref="DropLocalLight"/>.</summary>
    public void DropSun(int index, float fade) => Drop(index, fade, _droppedSuns, _fadingSuns);

    /// <summary>Empties it for the next frame, keeping what it allocated.</summary>
    public void Clear()
    {
        _modes.Clear();
        _droppedLocalLights.Clear();
        _droppedSuns.Clear();
        _fadingLocalLights.Clear();
        _fadingSuns.Clear();
        Unslotted.Clear();
    }

    private static void Drop(int index, float fade, List<int> dropped, List<(int Index, float Fade)> fading)
    {
        if (fade >= 1f)
        {
            dropped.Add(index);
        }
        else
        {
            fading.Add((index, fade));
        }
    }
}
