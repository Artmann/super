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

    /// <summary>
    /// Lays the pose out again for where the clock is: the state, or the old state with the new one blending in over
    /// it, then the actions playing. The joints worked out from the old layers are thrown away.
    /// </summary>
    public readonly void LayOut(AnimationControllerBase controller, AnimationPose pose, AnimatorJoints joints, ReadOnlySpan<ActionPlay> plays)
    {
        joints.Invalidate();
        pose.Clear();
        var state = controller.GetState(StateNumber);
        if (FadeOut is { } outgoing)
        {
            float f = Math.Clamp(outgoing.Seconds / controller.CrossFadeSeconds, 0f, 1f);
            var (first, second, w) = state.Motion.Pick(Parameter);
            controller.GetState(outgoing.From).AddTo(pose, outgoing.Parameter, outgoing.Phase, 1f, Speed);
            // Lerping into the previous pose by f, towards a mix of (first, second) at w,
            // takes two layers: first by f(1-w)/(1-fw), then second by fw.
            pose.Add(state.ClipLayer(first, Parameter, Phase, f * (1f - w) / (1f - f * w), Speed));
            pose.Add(state.ClipLayer(second, Parameter, Phase, f * w, Speed));
        }
        else
        {
            state.AddTo(pose, Parameter, Phase, 1f, Speed);
        }

        for (int i = 0; i < plays.Length; i++)
        {
            if (plays[i].Playing)
            {
                var action = controller.GetAction(i);
                pose.Add(action.Layer(plays[i].Time, controller.Rig.Clips[action.Clip].Duration, Speed));
            }
        }
    }
}

/// <summary>The state blending out: where its cycle is, and how long it has been fading.</summary>
internal readonly record struct StateFadeOut(int From, float Parameter, float Phase, float Seconds);
