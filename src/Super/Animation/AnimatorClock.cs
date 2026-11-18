namespace Super;

/// <summary>
/// Where an animator's base state is: which state, how far through its cycle, the blend's
/// parameter, how fast everything plays, and the state fading out after a change, if any.
/// </summary>
internal struct AnimatorClock
{
    /// <summary>The base state's number in the controller.</summary>
    public int StateNumber;
    public float Phase;
    public float Parameter;
    public float Speed;

    // The state a change left, while the new one blends in over it, or null once it has. See "Lifecycle state in
    // engine code" in docs/design.md.
    public StateFadeOut? FadeOut;
}

/// <summary>The state blending out: where its cycle is, and how long it has been fading.</summary>
internal readonly record struct StateFadeOut(int From, float Parameter, float Phase, float Seconds);
