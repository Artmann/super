using System.Numerics;
using System.Runtime.CompilerServices;
using Rendition;

namespace Super;

/// <summary>
/// One character's animation: which base state it's in, how far through its cycle,
/// and which actions are playing over it. Comes from
/// <see cref="AnimationController{TState, TAction}.Start"/>. The game loop moves it on
/// after every Update, so a game only says what the character is doing, and draws it
/// with <see cref="View.Model(int, Animator, System.Numerics.Matrix4x4, System.Numerics.Vector3?)"/>.
/// </summary>
public sealed class Animator
{
    private readonly AnimationControllerBase _controller;
    private ActionPlay[] _actions;

    private int _state;
    private float _phase;
    private float _parameter;
    private float _speed = 1f;

    private bool _fading;
    private int _previousState;
    private float _previousPhase;
    private float _previousParameter;
    private float _fadeSeconds;

    // Each joint in the model for this tick, worked out when first asked for.
    private Matrix4x4[]? _joints;
    private JointPose[]? _locals;
    private JointPose[]? _scratch;
    private bool _jointsFresh;

    internal Animator(AnimationControllerBase controller, int state)
    {
        _controller = controller;
        _state = state;
        _actions = new ActionPlay[controller.ActionCount];
        Pose = new AnimationPose(controller.Rig);
        controller.GetState(state);
        Rebuild();
        controller.Rig.Runner?.Add(this);
    }

    public Rig Rig => _controller.Rig;

    /// <summary>What to draw. <see cref="View.Model(int, Animator, System.Numerics.Matrix4x4, System.Numerics.Vector3?)"/> uses it for you.</summary>
    public AnimationPose Pose { get; }

    /// <summary>
    /// How fast everything plays: 1 as made, 2 twice as fast, and 0 holds the character
    /// still, such as while the game is paused.
    /// </summary>
    public float Speed
    {
        get => _speed;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The speed must be a number of at least zero.");
            }

            _speed = value;
            Rebuild();
        }
    }

    /// <summary>
    /// Picks the clip in a blended state, such as the speed for a walk-to-run blend.
    /// Outside the blend's range the nearest clip plays. States with one clip ignore it.
    /// </summary>
    public float Parameter
    {
        get => _parameter;
        set
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The parameter must be a number.");
            }

            _parameter = value;
        }
    }

    /// <summary>
    /// How far the clips carried the character over the ground on the last tick, in the
    /// model's space, once the rig has <see cref="Rig.RootMotion"/>. Turn it by the
    /// character's own rotation and scale, then add it to where the character is.
    /// </summary>
    public Vector3 RootMotion { get; private set; }

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

        var skeleton = Rig.Skeleton;
        if (!_jointsFresh || _joints is null)
        {
            _joints ??= new Matrix4x4[skeleton.Count];
            _locals ??= new JointPose[skeleton.Count];
            _scratch ??= new JointPose[skeleton.Count];
            var locals = _locals;
            Pose.Evaluate(0f, locals, _scratch);
            for (int i = 0; i < skeleton.Count; i++)
            {
                _joints[i] = skeleton.Parents[i] >= 0 ? locals[i].Matrix * _joints[skeleton.Parents[i]] : locals[i].Matrix;
            }

            _jointsFresh = true;
        }

        return _joints[joint] * skeleton.RootTransform;
    }

    /// <summary>How far through the state's cycle, from 0 to 1.</summary>
    public float Phase => _phase;

    /// <summary>Whether a change of state is still blending from the old clip.</summary>
    public bool IsCrossFading => _fading;

    /// <summary>Whether the animator is in this state.</summary>
    public bool IsIn<TState>(TState state) where TState : struct, Enum
    {
        CheckState<TState>();
        return _state == Index(state);
    }

    /// <summary>
    /// Changes the base state. The new clip blends in over the controller's cross-fade
    /// time, keeping the cycle's phase so walk and run stay in step. Setting the same
    /// state does nothing.
    /// </summary>
    public void Set<TState>(TState state) where TState : struct, Enum
    {
        CheckState<TState>();
        int index = Index(state);
        if (index == _state)
        {
            return;
        }

        _controller.GetState(index);
        _previousState = _state;
        _previousPhase = _phase;
        _previousParameter = _parameter;
        _fading = _controller.CrossFadeSeconds > 0f;
        _fadeSeconds = 0f;
        _state = index;
        Rebuild();
    }

    /// <summary>Plays an action from its start, over whatever is playing. Playing it again restarts it.</summary>
    public void Play<TAction>(TAction action) where TAction : struct, Enum
    {
        CheckAction<TAction>();
        int index = Index(action);
        _controller.GetAction(index);
        if (index >= _actions.Length)
        {
            Array.Resize(ref _actions, index + 1);
        }

        _actions[index] = new ActionPlay(Playing: true, Time: 0f, PreviousTime: -1f);
        Rebuild();
    }

    /// <summary>Stops an action before its end.</summary>
    public void Stop<TAction>(TAction action) where TAction : struct, Enum
    {
        CheckAction<TAction>();
        int index = Index(action);
        if (index < _actions.Length)
        {
            _actions[index] = _actions[index] with { Playing = false };
        }

        Rebuild();
    }

    public bool IsPlaying<TAction>(TAction action) where TAction : struct, Enum
    {
        CheckAction<TAction>();
        int index = Index(action);
        return index < _actions.Length && _actions[index].Playing;
    }

    /// <summary>
    /// True on the one tick an action passed this moment of its clip, in seconds, so a
    /// shot fires when the hand squeezes, not when the clip starts. Passing 0 is true
    /// the tick the action starts.
    /// </summary>
    public bool Crossed<TAction>(TAction action, float seconds) where TAction : struct, Enum
    {
        CheckAction<TAction>();
        int index = Index(action);
        if (index >= _actions.Length)
        {
            return false;
        }

        var play = _actions[index];
        return play.PreviousTime < seconds && seconds <= play.Time;
    }

    /// <summary>
    /// Moves everything on by this much game time, scaled by <see cref="Speed"/>. The game
    /// loop calls it once a tick for animators from content; tests call it by hand.
    /// </summary>
    internal void Advance(float seconds)
    {
        if (!(seconds >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Time can't go backwards.");
        }

        RootMotion = Rig.RootJoint >= 0 ? TravelOver(seconds) : Vector3.Zero;
        seconds *= _speed;

        _phase = AdvancePhase(_controller.GetState(_state), _parameter, _phase, seconds);
        if (_fading)
        {
            _previousPhase = AdvancePhase(_controller.GetState(_previousState), _previousParameter, _previousPhase, seconds);
            _fadeSeconds += seconds;
            if (_fadeSeconds >= _controller.CrossFadeSeconds)
            {
                _fading = false;
            }
        }

        for (int i = 0; i < _actions.Length; i++)
        {
            ref var play = ref _actions[i];
            play.PreviousTime = play.Time;
            if (!play.Playing)
            {
                continue;
            }

            var action = _controller.GetAction(i);
            play.Time += seconds * action.Speed;
            if (play.Time >= Rig.Clips[action.Clip].Duration)
            {
                play.Playing = false;
            }
        }

        Rebuild();
    }

    /// <summary>
    /// How far the pose's layers carry the character over this much time, mixed as the
    /// layers mix the root joint: each later layer takes its share from those before.
    /// </summary>
    private Vector3 TravelOver(float seconds)
    {
        var travel = Vector3.Zero;
        float left = 1f;
        var layers = Pose.Layers;
        for (int i = layers.Length - 1; i >= 0 && left > 0f; i--)
        {
            var layer = layers[i];
            float share = Math.Clamp(layer.Amount * (layer.Mask?.Weights[Rig.RootJoint] ?? 1f), 0f, 1f);
            if (share <= 0f)
            {
                continue;
            }

            travel += left * share * LayerTravel(layer, seconds);
            left *= 1f - share;
        }

        return travel;
    }

    private Vector3 LayerTravel(in PoseLayer layer, float seconds)
    {
        float duration = Rig.Clips[layer.Clip].Duration;
        float from = layer.Time;
        float to = from + seconds * layer.Speed;
        if (!layer.Loops || duration <= 0f)
        {
            return Rig.Travel(layer.Clip, to) - Rig.Travel(layer.Clip, from);
        }

        // A loop carries on from where its last cycle ended, so each wrap adds a whole cycle.
        float cycles = MathF.Floor(to / duration);
        var lap = Rig.Travel(layer.Clip, duration);
        return Rig.Travel(layer.Clip, to - cycles * duration) + cycles * lap - Rig.Travel(layer.Clip, from);
    }

    private float AdvancePhase(StateDefinition state, float parameter, float phase, float seconds)
    {
        float duration = CycleSeconds(state, parameter);
        if (duration <= 0f)
        {
            return 0f;
        }

        phase += seconds * state.Speed / duration;
        return phase - MathF.Floor(phase);
    }

    /// <summary>How long one cycle of the state takes at speed 1: the clips' durations mixed as they are.</summary>
    private float CycleSeconds(StateDefinition state, float parameter)
    {
        var (first, second, towards) = state.Motion.Pick(parameter);
        return (1f - towards) * Rig.Clips[first].Duration + towards * Rig.Clips[second].Duration;
    }

    private void Rebuild()
    {
        _jointsFresh = false;
        Pose.Clear();
        if (_fading)
        {
            float f = Math.Clamp(_fadeSeconds / _controller.CrossFadeSeconds, 0f, 1f);
            var (first, second, w) = _controller.GetState(_state).Motion.Pick(_parameter);
            AddState(_previousState, _previousParameter, _previousPhase, 1f);
            // Lerping into the previous pose by f, towards a mix of (first, second) at w,
            // takes two layers: first by f(1-w)/(1-fw), then second by fw.
            AddStateClip(_controller.GetState(_state), _parameter, first, _phase, f * (1f - w) / (1f - f * w));
            AddStateClip(_controller.GetState(_state), _parameter, second, _phase, f * w);
        }
        else
        {
            AddState(_state, _parameter, _phase, 1f);
        }

        for (int i = 0; i < _actions.Length; i++)
        {
            var play = _actions[i];
            if (!play.Playing)
            {
                continue;
            }

            var action = _controller.GetAction(i);
            float duration = Rig.Clips[action.Clip].Duration;
            float weight = 1f;
            if (action.FadeSeconds > 0f)
            {
                weight = Math.Clamp(Math.Min(play.Time / action.FadeSeconds, (duration - play.Time) / action.FadeSeconds), 0f, 1f);
            }

            Pose.Add(new PoseLayer(action.Clip, play.Time, action.Speed * _speed, weight, Loops: false, action.Mask));
        }
    }

    /// <summary>The whole of a state as layers: its first clip at <paramref name="amount"/>, then its second by the blend.</summary>
    private void AddState(int stateIndex, float parameter, float phase, float amount)
    {
        var state = _controller.GetState(stateIndex);
        var (first, second, towards) = state.Motion.Pick(parameter);
        AddStateClip(state, parameter, first, phase, amount);
        AddStateClip(state, parameter, second, phase, towards);
    }

    private void AddStateClip(StateDefinition state, float parameter, int clip, float phase, float amount)
    {
        float clipDuration = Rig.Clips[clip].Duration;
        float cycle = CycleSeconds(state, parameter);
        float speed = cycle > 0f ? state.Speed * _speed * clipDuration / cycle : 0f;
        Pose.Add(new PoseLayer(clip, phase * clipDuration, speed, amount, Loops: true, Mask: null));
    }

    private void CheckState<TState>()
    {
        if (typeof(TState) != _controller.StateType)
        {
            throw new InvalidOperationException($"The animator's states are {_controller.StateType.Name}, not {typeof(TState).Name}.");
        }
    }

    private void CheckAction<TAction>()
    {
        if (typeof(TAction) != _controller.ActionType)
        {
            throw new InvalidOperationException($"The animator's actions are {_controller.ActionType.Name}, not {typeof(TAction).Name}.");
        }
    }

    private static int Index<T>(T value) where T : struct, Enum => Unsafe.As<T, int>(ref value);

    private record struct ActionPlay(bool Playing, float Time, float PreviousTime);
}
