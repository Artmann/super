using System.Globalization;

namespace Rendition;

/// <summary>
/// Names a light from one frame to the next. A sun's <see cref="Key"/> is its index in the frame, and a point or spot
/// light's is its <see cref="LocalLight.Key"/>.
/// </summary>
public readonly record struct LightKey(bool Sun, int Key);

/// <summary>
/// Finds the lights that are moving, so a bake leaves them out until they hold still. A light is animated from the
/// frame its bake hash first differs from the last frame's, and stays so until a second after it last changed.
/// See "Moving lights" in docs/rendering.md.
/// </summary>
public sealed class AnimatedLights(TextWriter log)
{
    /// <summary>How long a light must hold still, in seconds, before it returns to the bake.</summary>
    public const float SettleSeconds = 1f;

    private sealed class State
    {
        public ulong Hash;
        public float ChangedAt;
        public bool Animated;
        public bool Logged;
        public bool Spot;
        public int Frame;
    }

    private readonly Dictionary<LightKey, State> states = [];
    private readonly List<LightKey> gone = [];
    private int frame;
    private bool hasTime;
    private float lastTime;

    /// <summary>
    /// Called once per frame with every Mixed or Baked light's key, its bake hash, and the frame's game time. Every
    /// light is taken to be a point light when it is named in the log; use the overload with a spot flag to say otherwise.
    /// </summary>
    public void Update(ReadOnlySpan<(LightKey Key, ulong Hash)> lights, float time)
    {
        Begin(time);
        foreach (var (key, hash) in lights)
        {
            Observe(key, hash, false, time);
        }

        End(time);
    }

    /// <summary>The same as <see cref="Update(ReadOnlySpan{ValueTuple{LightKey, ulong}}, float)"/>, naming each local light as a spot or a point light in the log.</summary>
    public void Update(ReadOnlySpan<(LightKey Key, ulong Hash, bool Spot)> lights, float time)
    {
        Begin(time);
        foreach (var (key, hash, spot) in lights)
        {
            Observe(key, hash, spot, time);
        }

        End(time);
    }

    /// <summary>True while the light is moving, or has not yet held still for <see cref="SettleSeconds"/>.</summary>
    public bool IsAnimated(LightKey key)
    {
        return states.TryGetValue(key, out var state) && state.Animated;
    }

    private void Begin(float time)
    {

        if (hasTime && time < lastTime)
        {
            states.Clear();
        }

        hasTime = true;
        lastTime = time;
        frame++;
    }

    private void Observe(LightKey key, ulong hash, bool spot, float time)
    {
        if (!states.TryGetValue(key, out var state))
        {
            states[key] = new State { Hash = hash, Spot = spot, Frame = frame };
            return;
        }

        if (state.Frame == frame)
        {
            Animate(key, state, time, shared: true);
            return;
        }

        state.Frame = frame;
        if (state.Hash != hash)
        {
            state.Hash = hash;
            Animate(key, state, time, shared: false);
        }
    }

    private void Animate(LightKey key, State state, float time, bool shared)
    {
        state.ChangedAt = time;
        state.Animated = true;
        if (state.Logged)
        {
            return;
        }

        state.Logged = true;
        var name = Name(key, state.Spot);
        var seconds = SettleSeconds.ToString(CultureInfo.InvariantCulture);
        var unit = SettleSeconds == 1f ? "second" : "seconds";
        if (shared)
        {
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Two or more lights share key {key.Key}, so they look like one light that moves every frame. They are kept out of the bake and lit live, and none returns to it until they stop sharing and hold still for {seconds} {unit}."));
        }
        else
        {
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"The {name} is moving, so it is kept out of the bake and lit live. It returns to the bake once it holds still for {seconds} {unit}."));
        }
    }

    private static string Name(LightKey key, bool spot)
    {
        var kind = key.Sun ? "sun" : spot ? "spot light" : "point light";
        return string.Create(CultureInfo.InvariantCulture, $"{kind} {key.Key}");
    }

    private void End(float time)
    {
        gone.Clear();
        foreach (var (key, state) in states)
        {
            if (state.Frame != frame)
            {
                gone.Add(key);
            }
            else if (state.Animated && time - state.ChangedAt >= SettleSeconds)
            {
                state.Animated = false;
            }
        }

        foreach (var key in gone)
        {
            states.Remove(key);
        }
    }
}
