using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// What <see cref="LensFlareVisibility"/> keeps between frames on the CPU: where the last frame's lights were, so it
/// knows when to start the kept visibility afresh, and which of the four held frames is written next.
/// </summary>
internal sealed class LensFlareHistory
{
    /// <summary>The frames whose visibility is kept. Must match the ring in lens_flare_visibility.comp.</summary>
    private const int HeldFrames = 4;

    /// <summary>How far a light may move on screen in a frame, as a fraction of it, before its kept visibility is dropped, as on a cut.</summary>
    private const float JumpDistance = 0.1f;

    // Must match the flags in lens_flare_visibility.comp.
    private const int HoldFlag = 1;
    private const int ResetFlag = 2;
    private const int CloudsFlag = 4;

    private readonly Vector2[] _lastUvs = new Vector2[LensFlareSettings.MaxSources];

    // How many lights the last recorded frame looked at, 0 when it drew no flare.
    private int _lastCount;
    private uint _frame;
    private int _flags;

    /// <summary>
    /// Takes this frame's lights. <paramref name="hold"/> keeps the most of the last four frames, for anti-aliasing.
    /// The frames start afresh without a last picture, after a frame with no flare, when the number of lights changes
    /// or when one jumps across the screen. <paramref name="clouds"/> says whether the frame draws clouds, which the sun
    /// is then seen through.
    /// </summary>
    public void Take(ReadOnlySpan<FlareSource> sources, bool hold, bool historyReady, bool clouds)
    {
        bool reset = !historyReady || sources.Length != _lastCount;
        for (int i = 0; i < sources.Length && !reset; i++)
        {
            reset = Vector2.Distance(sources[i].Uv, _lastUvs[i]) > JumpDistance;
        }

        _flags = (hold ? HoldFlag : 0) | (reset ? ResetFlag : 0) | (clouds ? CloudsFlag : 0);
    }

    /// <summary>
    /// The held frame this frame writes and the flags <see cref="Take"/> worked out, for the frame being recorded with
    /// <paramref name="sources"/>, which the next frame is compared with.
    /// </summary>
    public (int Slot, int Flags) Record(ReadOnlySpan<FlareSource> sources)
    {
        _lastCount = sources.Length;
        for (int i = 0; i < sources.Length; i++)
        {
            _lastUvs[i] = sources[i].Uv;
        }

        return ((int)(_frame++ % HeldFrames), _flags);
    }

    /// <summary>Notes a frame that drew no flare, so the next one starts its frames afresh.</summary>
    public void Skip() => _lastCount = 0;
}
