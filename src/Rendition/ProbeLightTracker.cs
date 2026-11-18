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
    /// Marks the probes near each point and spot light that appeared, went out, or changed its place,
    /// range, colour, cone or shadow since last frame, both where it was and where it is now.
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
        && a.Radiance == b.Radiance
        && a.Radius == b.Radius
        && a.Direction == b.Direction
        && a.InnerAngle == b.InnerAngle
        && a.OuterAngle == b.OuterAngle
        && a.CastsShadow == b.CastsShadow;
}
