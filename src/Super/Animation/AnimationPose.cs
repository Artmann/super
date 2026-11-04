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
    /// go over the layers, then turns go on, then limbs reach.
    /// </summary>
    public void Evaluate(float ahead, Span<JointPose> pose, Span<JointPose> scratch)
    {
        var skeleton = Rig.Skeleton;
        if (pose.Length != skeleton.Count || scratch.Length != skeleton.Count)
        {
            throw new ArgumentException($"The rig has {skeleton.Count} joints, but got {pose.Length} and {scratch.Length}.");
        }

        skeleton.Rest.AsSpan().CopyTo(pose);
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
        Matrix4x4.Decompose(skeleton.RootTransform, out _, out var rootTurn, out _);
        Matrix4x4.Invert(skeleton.RootTransform, out var fromModel);

        // Parents come first, so each turn is in place before its children are worked out.
        for (int i = 0; i < count; i++)
        {
            if (_hasTurns && _isTurned![i])
            {
                TurnJoint(pose, turned, i, Within(_turns![i], rootTurn));
            }

            Place(pose, globals, turned, i);
        }

        foreach (var reach in _reaches)
        {
            ReachLimb(pose, globals, turned, reach, Vector3.Transform(reach.Target, fromModel));
        }
    }

    private void ReachLimb(Span<JointPose> pose, Span<Matrix4x4> globals, Span<Quaternion> turned, Goal reach, Vector3 goal)
    {
        var limb = reach.Limb;
        var upper = globals[limb.Upper].Translation;
        var lower = globals[limb.Lower].Translation;
        var end = globals[limb.End].Translation;
        var endTurn = turned[limb.End];

        // What the tip holds stays where it is relative to the end, which keeps its turn, so
        // the end's goal is the goal less that.
        var held = Vector3.Transform(reach.Point, globals[limb.Tip]) - end;
        var wrist = goal - held;

        float upperLength = Vector3.Distance(upper, lower);
        float lowerLength = Vector3.Distance(lower, end);
        var toGoal = wrist - upper;
        float span = toGoal.Length();
        if (upperLength < 1e-6f || lowerLength < 1e-6f || span < 1e-6f)
        {
            return;
        }

        var along = toGoal / span;
        span = Math.Clamp(span, MathF.Abs(upperLength - lowerLength) * 1.001f, (upperLength + lowerLength) * 0.999f);

        // The bend keeps to the side the layers bend it: the lower joint's way out from the line
        // between the upper joint and the goal.
        var bend = lower - upper - (Vector3.Dot(lower - upper, along) * along);
        if (bend.LengthSquared() < 1e-10f)
        {
            bend = Vector3.Cross(along, MathF.Abs(along.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }

        bend = Vector3.Normalize(bend);
        float cos = Math.Clamp(((upperLength * upperLength) + (span * span) - (lowerLength * lowerLength)) / (2f * upperLength * span), -1f, 1f);
        var elbow = upper + (upperLength * ((cos * along) + (MathF.Sqrt(1f - (cos * cos)) * bend)));

        var first = Quaternion.Slerp(Quaternion.Identity, Between(lower - upper, elbow - upper), reach.Weight);
        TurnJoint(pose, turned, limb.Upper, first);
        Refresh(pose, globals, turned, limb.Upper);

        var bent = globals[limb.Lower].Translation;
        var second = Quaternion.Slerp(Quaternion.Identity, Between(globals[limb.End].Translation - bent, upper + (span * along) - bent), reach.Weight);
        TurnJoint(pose, turned, limb.Lower, second);
        Refresh(pose, globals, turned, limb.Lower);

        // The end turns back to how the layers had it.
        TurnJoint(pose, turned, limb.End, endTurn * Quaternion.Inverse(turned[limb.End]));
        Refresh(pose, globals, turned, limb.End);
    }

    /// <summary>Turns a joint about itself by a rotation in the skeleton's space, its parent's place already known.</summary>
    private void TurnJoint(Span<JointPose> pose, Span<Quaternion> turned, int joint, Quaternion turn)
    {
        int parent = Rig.Skeleton.Parents[joint];
        var local = parent >= 0 ? Within(turn, turned[parent]) : turn;
        pose[joint].Rotation = Quaternion.Normalize(local * pose[joint].Rotation);
    }

    /// <summary>Works out a joint's place and rotation in the skeleton from its parent's.</summary>
    private void Place(Span<JointPose> pose, Span<Matrix4x4> globals, Span<Quaternion> turned, int joint)
    {
        int parent = Rig.Skeleton.Parents[joint];
        globals[joint] = parent >= 0 ? pose[joint].Matrix * globals[parent] : pose[joint].Matrix;
        turned[joint] = parent >= 0 ? Quaternion.Normalize(turned[parent] * pose[joint].Rotation) : pose[joint].Rotation;
    }

    /// <summary>Works out a joint and everything under it again, after it's turned.</summary>
    private void Refresh(Span<JointPose> pose, Span<Matrix4x4> globals, Span<Quaternion> turned, int joint)
    {
        var parents = Rig.Skeleton.Parents;
        Span<bool> under = parents.Length <= 256 ? stackalloc bool[parents.Length] : new bool[parents.Length];
        under[joint] = true;
        Place(pose, globals, turned, joint);
        for (int i = joint + 1; i < parents.Length; i++)
        {
            if (parents[i] >= 0 && under[parents[i]])
            {
                under[i] = true;
                Place(pose, globals, turned, i);
            }
        }
    }

    /// <summary>A rotation given outside a frame, such as the model's, as the same rotation inside it.</summary>
    private static Quaternion Within(Quaternion turn, Quaternion frame) => Quaternion.Inverse(frame) * turn * frame;

    /// <summary>The shortest rotation that takes one direction to another.</summary>
    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float dot = Vector3.Dot(from, to);
        if (dot > 0.99999f)
        {
            return Quaternion.Identity;
        }

        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-10f)
        {
            axis = Vector3.Cross(from, MathF.Abs(from.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }

        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(dot, -1f, 1f)));
    }

    private readonly record struct Goal(Limb Limb, Vector3 Target, float Weight, Vector3 Point);
}
