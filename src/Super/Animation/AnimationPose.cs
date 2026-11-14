using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// One clip's part in a pose. Layers apply in order: each moves the pose so far towards
/// its clip by <see cref="Amount"/>, scaled per joint by the mask, if there is one.
/// Time is at the last tick; the display adds how far it is into the next tick times
/// <see cref="Speed"/>, and wraps it when the layer loops.
/// </summary>
public readonly record struct PoseLayer(int Clip, float Time, float Speed, float Amount, bool Loops, BoneMask? Mask);

/// <summary>
/// Which clips a model is playing, at what time and how strongly. An <see cref="Animator"/>
/// keeps one up to date; a view hands it to the renderer, which samples the clips and
/// skins the model. Its layers hold no joint data, so a headless game can look at it.
/// A game can also set joints itself, which go over whatever the layers give; turn joints
/// on top of the layers, such as a body knocked back by a blow; and reach limbs to goals
/// by IK, such as a hand bringing a sword onto what it strikes.
/// </summary>
public sealed class AnimationPose
{
    private PoseLayer[] _layers = new PoseLayer[6];
    private JointPose[]? _joints;
    private bool[]? _isSet;
    private Quaternion[]? _turns;
    private bool[]? _isTurned;
    private bool _hasTurns;
    private readonly List<Goal> _reaches = [];

    private readonly HeldJoints _held = new();

    public AnimationPose(Rig rig)
    {
        Rig = rig;
    }

    public Rig Rig { get; }

    public int LayerCount { get; private set; }

    public ReadOnlySpan<PoseLayer> Layers => _layers.AsSpan(0, LayerCount);

    /// <summary>Whether any joint is set by <see cref="SetJoint(int, in JointPose)"/>.</summary>
    public bool HasSetJoints { get; private set; }

    /// <summary>Takes away the layers. Joints set by the game stay until <see cref="ClearJoints"/>.</summary>
    public void Clear() => LayerCount = 0;

    /// <summary>
    /// Sets a joint's local pose, relative to its parent, over whatever the layers give.
    /// It stays until it's set again or cleared, and isn't moved ahead between ticks, so
    /// set it in Draw from values already blended with <c>view.Blend</c>.
    /// </summary>
    public void SetJoint(int joint, in JointPose local)
    {
        int count = Rig.Skeleton.Count;
        if ((uint)joint >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(joint), joint, $"The rig has {count} joints, numbered from 0.");
        }

        _joints ??= new JointPose[count];
        _isSet ??= new bool[count];
        _joints[joint] = local;
        _isSet[joint] = true;
        HasSetJoints = true;
        Version++;
    }

    /// <summary>
    /// Changes whenever the game sets, turns or reaches a joint, so whoever works the joints
    /// out knows to again.
    /// </summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Turns a joint on top of whatever the layers give, by a rotation in the model's space,
    /// about the joint itself. Everything under it goes with it, so a turn of the spine
    /// leans the chest, arms and head together. It stays until it's turned again or the
    /// joints are cleared; <see cref="Quaternion.Identity"/> takes it away. Like a set joint,
    /// it isn't moved ahead between ticks, so turn it in Draw from values blended with
    /// <c>view.Blend</c>.
    /// </summary>
    public void Turn(int joint, Quaternion turn)
    {
        int count = Rig.Skeleton.Count;
        if ((uint)joint >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(joint), joint, $"The rig has {count} joints, numbered from 0.");
        }

        if (!float.IsFinite(turn.X + turn.Y + turn.Z + turn.W) || turn.LengthSquared() < 1e-6f)
        {
            throw new ArgumentException($"A turn must be a rotation, not {turn}.", nameof(turn));
        }

        _turns ??= new Quaternion[count];
        _isTurned ??= new bool[count];
        _turns[joint] = Quaternion.Normalize(turn);
        _isTurned[joint] = !turn.IsIdentity;
        _hasTurns = Array.IndexOf(_isTurned, true) >= 0;
        Version++;
    }

    /// <summary>Turns a joint by name. An unknown name throws, listing the joints the rig has.</summary>
    public void Turn(string joint, Quaternion turn) => Turn(JointIndex(joint), turn);

    /// <summary>
    /// Bends a limb so the point its tip holds reaches a goal in the model's space, by
    /// turning its upper and lower joints. The bend stays on the side the layers give, so an
    /// elbow never folds backwards, and the end keeps the way the layers turned it, so a hand
    /// brings its sword over without twisting it. The point is in the tip's own space, such
    /// as how far along a blade; leave it out for the tip itself. A goal out of reach
    /// stretches the limb towards it. The weight mixes from the layers' pose, at 0, to the
    /// goal, at 1; a weight of 0 takes the reach away. It stays until it's reached again or
    /// the joints are cleared, and like a set joint isn't moved ahead between ticks, but the
    /// layers under it are, so set it in Draw for a limb that follows the clip smoothly.
    /// </summary>
    public void Reach(Limb limb, Vector3 goal, float weight = 1f, Vector3 point = default)
    {
        if (limb.Rig != Rig)
        {
            throw new ArgumentException("The limb is for another rig.", nameof(limb));
        }

        if (!float.IsFinite(goal.X + goal.Y + goal.Z) || !float.IsFinite(point.X + point.Y + point.Z))
        {
            throw new ArgumentException($"A reach needs a goal and point that are numbers, not {goal} and {point}.", nameof(goal));
        }

        if (!(weight >= 0f && weight <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "A reach's weight runs from 0 to 1.");
        }

        _reaches.RemoveAll(reach => reach.Limb == limb);
        if (weight > 0f)
        {
            _reaches.Add(new Goal(limb, goal, weight, point));
        }

        Version++;
    }

    /// <summary>Sets a joint by name. An unknown name throws, listing the joints the rig has.</summary>
    public void SetJoint(string joint, in JointPose local) => SetJoint(JointIndex(joint), local);

    /// <summary>The number of a joint, for setting it quickly every frame. An unknown name throws, listing the joints.</summary>
    public int JointIndex(string joint)
    {
        int index = Rig.Skeleton.IndexOf(joint);
        if (index < 0)
        {
            throw new ArgumentException($"The rig has no joint called {joint}. It has: {string.Join(", ", Rig.Skeleton.Names)}.", nameof(joint));
        }

        return index;
    }

    /// <summary>Hands every joint the game set, turned or reached back to the layers.</summary>
    public void ClearJoints()
    {
        if (_isSet is not null)
        {
            Array.Clear(_isSet);
        }

        if (_isTurned is not null)
        {
            Array.Clear(_isTurned);
        }

        HasSetJoints = false;
        _hasTurns = false;
        _reaches.Clear();
        Version++;
    }

    /// <summary>Adds a layer. Layers with no amount are left out, since they'd change nothing.</summary>
    public void Add(in PoseLayer layer)
    {
        if (layer.Amount <= 0f)
        {
            return;
        }

        Rig.Check(new ClipRef(layer.Clip));
        if (layer.Mask is { } mask && mask.Rig != Rig)
        {
            throw new ArgumentException("The mask is for another rig.", nameof(layer));
        }

        if (LayerCount == _layers.Length)
        {
            Array.Resize(ref _layers, _layers.Length * 2);
        }

        _layers[LayerCount++] = layer;
    }

    /// <summary>
    /// Works out the joints' local poses at a moment: <paramref name="ahead"/> seconds
    /// after the last tick, for a display between ticks. The result starts from the
    /// skeleton's rest pose, so a pose with no layers is the model as modelled. Set joints
    /// go over the layers, then turns go on, then limbs reach, then the joints physics holds, such as a ragdoll's,
    /// go over all of that.
    /// </summary>
    public void Evaluate(float ahead, Span<JointPose> pose, Span<JointPose> scratch) =>
        Evaluate(ahead, pose, scratch, _held.Progress(ahead));

    /// <summary>
    /// Whether physics holds any joints, such as while a ragdoll is simulated or blending back to the clips.
    /// </summary>
    public bool IsPhysical => _held.IsActive;

    /// <summary>
    /// Hands joints to physics. Each is where it is in the skeleton's space, before the root transform, at the tick
    /// before and at this one; between ticks the pose moves from one to the other over
    /// <paramref name="tickSeconds"/>. A joint's weight mixes from the layers, at 0, to physics, at 1. Only joints'
    /// turns are taken, so bones keep their length, but for <paramref name="placed"/>, whose place is taken too.
    /// </summary>
    internal void SetPhysical(ReadOnlySpan<JointPose> from, ReadOnlySpan<JointPose> to, ReadOnlySpan<float> weights, int placed, float tickSeconds)
    {
        int count = Rig.Skeleton.Count;
        if (from.Length != count || to.Length != count || weights.Length != count)
        {
            throw new ArgumentException($"The rig has {count} joints, but got {from.Length}, {to.Length} and {weights.Length}.");
        }

        _held.Set(from, to, weights, placed, tickSeconds);
        Version++;
    }

    /// <summary>Hands every joint physics held back to the layers.</summary>
    internal void ClearPhysical()
    {
        if (_held.Clear())
        {
            Version++;
        }
    }

    /// <summary>
    /// Evaluates, with physics' joints <paramref name="physical"/> of the way from the tick before to the last: 1 for
    /// the pose as of the last tick, or null for what the layers, set joints, turns and reaches give without physics,
    /// which a ragdoll's muscles aim for.
    /// </summary>
    internal void Evaluate(float ahead, Span<JointPose> pose, Span<JointPose> scratch, float? physical)
    {
        var skeleton = Rig.Skeleton;
        if (pose.Length != skeleton.Count || scratch.Length != skeleton.Count)
        {
            throw new ArgumentException($"The rig has {skeleton.Count} joints, but got {pose.Length} and {scratch.Length}.");
        }

        Sample(ahead, pose, scratch);
        if (HasSetJoints)
        {
            for (int i = 0; i < pose.Length; i++)
            {
                if (_isSet![i])
                {
                    pose[i] = _joints![i];
                }
            }
        }

        if (_hasTurns || _reaches.Count > 0)
        {
            Solve(pose);
        }

        if (physical is { } amount && _held.IsActive)
        {
            _held.Apply(skeleton.Parents, amount, pose);
        }
    }

    /// <summary>The rest pose with each layer's clip blended over it in turn, <paramref name="ahead"/> seconds after the last tick.</summary>
    private void Sample(float ahead, Span<JointPose> pose, Span<JointPose> scratch)
    {
        Rig.Skeleton.Rest.AsSpan().CopyTo(pose);
        foreach (ref readonly var layer in Layers)
        {
            var clip = Rig.Clips[layer.Clip];
            float time = layer.Time + ahead * layer.Speed;
            if (layer.Loops && clip.Duration > 0f)
            {
                time = ((time % clip.Duration) + clip.Duration) % clip.Duration;
            }

            clip.Sample(time, scratch);
            PoseBlend.Blend(pose, scratch, layer.Amount, layer.Mask?.Weights ?? default(ReadOnlySpan<float>));
        }
    }

    /// <summary>
    /// Puts the turns and reaches on. The work is in the skeleton's own space, before the root
    /// transform, with each joint's place and rotation in it kept beside the local poses.
    /// </summary>
    private void Solve(Span<JointPose> pose)
    {
        var skeleton = Rig.Skeleton;
        int count = skeleton.Count;
        Span<Matrix4x4> globals = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
        Span<Quaternion> turned = count <= 128 ? stackalloc Quaternion[count] : new Quaternion[count];
        var space = new SkeletonSpace(skeleton.Parents, pose, globals, turned);
        Matrix4x4.Decompose(skeleton.RootTransform, out _, out var rootTurn, out _);
        Matrix4x4.Invert(skeleton.RootTransform, out var fromModel);

        // Parents come first, so each turn is in place before its children are worked out.
        for (int i = 0; i < count; i++)
        {
            if (_hasTurns && _isTurned![i])
            {
                space.Turn(i, SkeletonSpace.Within(_turns![i], rootTurn));
            }

            space.Place(i);
        }

        foreach (var reach in _reaches)
        {
            space.Reach(reach.Limb, reach.Point, reach.Weight, Vector3.Transform(reach.Target, fromModel));
        }
    }

    private readonly record struct Goal(Limb Limb, Vector3 Target, float Weight, Vector3 Point);
}
