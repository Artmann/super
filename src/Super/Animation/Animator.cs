using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// One character's animation: which base state it's in, how far through its cycle, and
/// which actions are playing over it. Comes from
/// <see cref="AnimationController{TState, TAction}.Start"/>. The game loop moves it on
/// after every Update, so a game only says what the character is doing, and draws it
/// with <see cref="View.Model(int, Animator, System.Numerics.Matrix4x4, in ModelOptions)"/>.
/// </summary>
public sealed class Animator
{
    private readonly AnimationControllerBase _controller;
    private readonly EventTracker _events;
    private AnimatorClock _clock;

    internal Animator(AnimationControllerBase controller, int state)
    {
        _controller = controller;
        _clock = new AnimatorClock { StateNumber = state, Speed = 1f };
        _events = new EventTracker(controller);
        Moves = new AnimatorActions(controller.ActionType, controller.ActionCount, controller.Timing, _events.SweepAction, Rebuild);
        Pose = new AnimationPose(controller.Rig);
        JointCache = new PoseJointCache(controller.Rig.Skeleton, (locals, scratch) => Pose.Evaluate(0f, locals, scratch, 1f));
        controller.GetState(state);
        Rebuild();
        controller.Rig.Runner?.Add(this);
    }

    public Rig Rig => _controller.Rig;

    /// <summary>What to draw. <see cref="View.Model(int, Animator, System.Numerics.Matrix4x4, in ModelOptions)"/> uses it for you.</summary>
    public AnimationPose Pose { get; }

    /// <summary>The one-shot actions playing over the state: play, stop and ask about them here.</summary>
    public AnimatorActions Moves { get; }

    /// <summary>
    /// How fast everything plays: 1 as made, 2 twice as fast, and 0 holds the character
    /// still, such as while the game is paused.
    /// </summary>
    public float Speed
    {
        get => _clock.Speed;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The speed must be a number of at least zero.");
            }

            _clock.Speed = value;
            Rebuild();
        }
    }

    /// <summary>
    /// Picks the clip in a blended state, such as the speed for a walk-to-run blend.
    /// Outside the blend's range the nearest clip plays. States with one clip ignore it.
    /// </summary>
    public float Parameter
    {
        get => _clock.Parameter;
        set
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The parameter must be a number.");
            }

            _clock.Parameter = value;
        }
    }

    /// <summary>
    /// How far the clips carried the character over the ground on the last tick, in the
    /// model's space, once the rig has <see cref="Rig.RootMotion"/>. Turn it by the
    /// character's own rotation and scale, then add it to where the character is.
    /// </summary>
    public Vector3 RootMotion { get; private set; }

    /// <summary>
    /// What the clips passed on the last tick: moments, and windows opening and closing, in
    /// the order they happened. Read it in Update; it holds until the next tick and allocates
    /// nothing. A loop passes its moments every time round, and a long tick reports every one
    /// it skipped. Cross-fading states count while their share is at least
    /// <see cref="AnimationControllerBase.EventThreshold"/>, and a blended state's events come
    /// from its heavier clip, so a walk and a run in step don't both step.
    /// </summary>
    public ReadOnlySpan<AnimationEvent> Events => _events.Events;

    /// <summary>How far through the state's cycle, from 0 to 1.</summary>
    public float Phase => _clock.Phase;

    /// <summary>Whether a change of state is still blending from the old clip.</summary>
    public bool IsCrossFading => _clock.FadeOut is not null;

    /// <summary>Each joint in the model this tick, worked out when first asked for.</summary>
    internal PoseJointCache JointCache { get; }

    /// <summary>
    /// Where a joint is in the model this tick, such as the hand an arrow leaves from.
    /// Multiply by the character's own transform to place it in the world.
    /// </summary>
    public Matrix4x4 Joint(string name)
    {
        int joint = Rig.Skeleton.IndexOf(name);
        if (joint < 0)
        {
            throw new ArgumentException($"The rig has no joint called {name}. It has: {string.Join(", ", Rig.Skeleton.Names)}.", nameof(name));
        }

        return JointCache.InModel(joint, Pose.Version);
    }

    /// <summary>Whether a window with this name is open now in any clip that counts, such as a hitbox mid-swing.</summary>
    public bool IsActive(string name) => _events.IsActive(name);

    /// <summary>Whether the animator is in this state.</summary>
    public bool IsIn<TState>(TState state) where TState : struct, Enum => _clock.StateNumber == _controller.StateIndex(state);

    /// <summary>
    /// Changes the base state. The new clip blends in over the controller's cross-fade
    /// time, keeping the cycle's phase so walk and run stay in step. Setting the same
    /// state does nothing.
    /// </summary>
    public void Set<TState>(TState state) where TState : struct, Enum
    {
        int index = _controller.StateIndex(state);
        if (index == _clock.StateNumber)
        {
            return;
        }

        _controller.GetState(index);
        _clock.FadeOut = _controller.CrossFadeSeconds > 0f ? new StateFadeOut(_clock.StateNumber, _clock.Parameter, _clock.Phase, Seconds: 0f) : null;
        _clock.StateNumber = index;
        Rebuild();
    }

    /// <summary>
    /// Moves everything on by this much game time, scaled by <see cref="Speed"/>. The game
    /// loop calls it once a tick for animators from content, with what plays its clips' sounds
    /// play on; tests call it by hand.
    /// </summary>
    internal void Advance(float seconds, ClipSoundPlayer? play = null)
    {
        if (!(seconds >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Time can't go backwards.");
        }

        RootMotion = Pose.TravelOver(seconds);
        seconds *= _clock.Speed;
        _events.SweepStates(_clock.StateNumber, _clock.Parameter, _clock.Phase, _clock.FadeOut, seconds);
        _clock.Phase = _controller.GetState(_clock.StateNumber).Advance(_clock.Parameter, _clock.Phase, seconds);
        if (_clock.FadeOut is { } outgoing)
        {
            float faded = outgoing.Seconds + seconds;
            _clock.FadeOut = faded >= _controller.CrossFadeSeconds
                ? null
                : outgoing with { Phase = _controller.GetState(outgoing.From).Advance(outgoing.Parameter, outgoing.Phase, seconds), Seconds = faded };
        }

        Moves.Advance(seconds);
        Rebuild();
        _events.Finish(this, play);
    }

    /// <summary>Lays the pose out again: the state, or the old state with the new one blending in over it, then the actions.</summary>
    private void Rebuild()
    {
        JointCache.Invalidate();
        Pose.Clear();
        var state = _controller.GetState(_clock.StateNumber);
        if (_clock.FadeOut is { } outgoing)
        {
            float f = Math.Clamp(outgoing.Seconds / _controller.CrossFadeSeconds, 0f, 1f);
            var (first, second, w) = state.Motion.Pick(_clock.Parameter);
            _controller.GetState(outgoing.From).AddTo(Pose, outgoing.Parameter, outgoing.Phase, 1f, _clock.Speed);
            // Lerping into the previous pose by f, towards a mix of (first, second) at w,
            // takes two layers: first by f(1-w)/(1-fw), then second by fw.
            Pose.Add(state.ClipLayer(first, _clock.Parameter, _clock.Phase, f * (1f - w) / (1f - f * w), _clock.Speed));
            Pose.Add(state.ClipLayer(second, _clock.Parameter, _clock.Phase, f * w, _clock.Speed));
        }
        else
        {
            state.AddTo(Pose, _clock.Parameter, _clock.Phase, 1f, _clock.Speed);
        }

        var plays = Moves.Plays;
        for (int i = 0; i < plays.Length; i++)
        {
            if (plays[i].Playing)
            {
                var action = _controller.GetAction(i);
                Pose.Add(action.Layer(plays[i].Time, Rig.Clips[action.Clip].Duration, _clock.Speed));
            }
        }
    }
}
