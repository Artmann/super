using Super;
using Super.Ui;

namespace Gallery;

/// <summary>
/// What two keys change during play, to show the reflection probes catching up without stopping the game: L puts the
/// chapel's lantern out and lights it again, and C dims the sky. The probes the change reaches take new pictures over
/// the next few frames.
/// </summary>
public sealed class LiveChanges
{
    /// <summary>How bright the sky is with C pressed, against 1 for a clear day.</summary>
    private const float DimSkyBrightness = 0.3f;

    /// <summary>
    /// Key presses for profiling the probes catching up. A profile from a shot's tick warms up for 60 ticks first, and
    /// each press comes 15 ticks after that, 30 frames into the measured ones. "dim" dims the sky in the colour room
    /// (tick 1065), which photographs every probe again. "lantern" puts the chapel's lantern out (tick 2415), which
    /// photographs the probes it reached again.
    /// </summary>
    public static IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
    {
        ["dim"] = new InputScript().Wait(1065 + 75).Press(Key.C),
        ["lantern"] = new InputScript().Wait(2415 + 75).Press(Key.L),
    };

    public bool LanternLit { get; private set; } = true;

    public bool SkyDimmed { get; private set; }

    public float SkyBrightness => SkyDimmed ? DimSkyBrightness : 1f;

    public void Update(Input input)
    {
        if (input.Pressed(Key.L))
        {
            LanternLit = !LanternLit;
        }

        if (input.Pressed(Key.C))
        {
            SkyDimmed = !SkyDimmed;
        }
    }

    /// <summary>
    /// Adds a line for each key, bright while its change is on and <paramref name="muted"/> while it's off. The words stay
    /// the same, so switching makes no new letters for the text atlas, which would wait for the GPU.
    /// </summary>
    public void AddLines(VStack lines, Color muted)
    {
        var lantern = new Text("L  Lantern lit");
        lines.Add(LanternLit ? lantern : lantern.Foreground(muted));
        var dim = new Text("C  Dim sky");
        lines.Add(SkyDimmed ? dim : dim.Foreground(muted));
    }
}
