using System.Numerics;

namespace Rendition;

/// <summary>
/// The point and spot lights the light probes were last updated for, by key, so a frame can tell the scheduler which
/// probes a light that appeared, went out or changed reaches.
/// </summary>
internal sealed class ProbeLightTracker
{
    private readonly Dictionary<int, LocalLight> _lights = [];
    private readonly HashSet<int> _seen = [];
    private readonly List<int> _gone = [];

    /// <summary>Forgets every light, as for probes that start again uncaptured.</summary>
    public void Clear() => _lights.Clear();

    /// <summary>
    /// How far a light's colour may drift from the colour the probes were last marked for before they are marked
    /// again, as a share of the brighter of the two. A flame that flickers by a tenth or so every frame would otherwise
    /// keep every probe round it busy for good, for a change the soft bounced light barely shows.
    /// </summary>
    public const float ColorDrift = 1f / 3f;

    /// <summary>
    /// Marks the probes near each point and spot light that appeared, went out, or changed its place,
    /// range, cone or shadow since the probes were last marked for it, both where it was and where it is now, or whose
    /// colour drifted by more than <see cref="ColorDrift"/>.
    /// </summary>
    public void Follow(ReadOnlySpan<LocalLight> lights, ProbeScheduler scheduler)
    {
        _seen.Clear();
        foreach (ref readonly var light in lights)
        {
            _seen.Add(light.Key);
            if (_lights.TryGetValue(light.Key, out var old))
            {
                if (Same(old, light))
                {
                    continue;
                }

                // A light that only flickers covers the same probes as before, so they are marked once, below.
                if (old.Position != light.Position || old.Range != light.Range)
                {
                    scheduler.LightChanged(old.Position, old.Range);
                }
            }

            scheduler.LightChanged(light.Position, light.Range);
            _lights[light.Key] = light;
        }

        if (_lights.Count != _seen.Count)
        {
            ForgetGone(scheduler);
        }
    }

    // Marks the probes near each light that wasn't seen this frame, and forgets the light.
    private void ForgetGone(ProbeScheduler scheduler)
    {
        _gone.Clear();
        foreach (var (key, old) in _lights)
        {
            if (!_seen.Contains(key))
            {
                scheduler.LightChanged(old.Position, old.Range);
                _gone.Add(key);
            }
        }

        foreach (int key in _gone)
        {
            _lights.Remove(key);
        }
    }

    private static bool Same(in LocalLight a, in LocalLight b) =>
        a.Position == b.Position
        && a.Range == b.Range
        && CloseColor(a.Radiance, b.Radiance)
        && a.Radius == b.Radius
        && a.Direction == b.Direction
        && a.InnerAngle == b.InnerAngle
        && a.OuterAngle == b.OuterAngle
        && a.CastsShadow == b.CastsShadow;

    // Whether two colours are within the drift of each other, channel by channel, against the brighter one's
    // brightest channel. A light that goes out or comes on is never close, since nothing is within a share of black.
    internal static bool CloseColor(Vector3 a, Vector3 b)
    {
        if (a == b)
        {
            return true;
        }

        float bright = MathF.Max(MaxChannel(a), MaxChannel(b));
        var gap = Vector3.Abs(a - b);
        return MaxChannel(gap) <= ColorDrift * bright;
    }

    private static float MaxChannel(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
}
