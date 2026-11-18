using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The joints a game sets, turns and reaches itself, over whatever a pose's layers give: a
/// knee bent to the ground, a spine leaned by a blow, a hand brought onto what it strikes.
/// Each stays until it's changed or <see cref="Clear"/> hands the joints back to the layers,
/// and none is moved ahead between ticks, so change them in Draw from values blended with
/// <c>view.Blend</c>. From <see cref="AnimationPose.Joints"/>.
/// </summary>
public sealed class PoseJoints
{
    private readonly Skeleton _skeleton;
    private readonly List<PoseReach> _reaches = [];
    private JointPose[]? _set;
    private bool[]? _isSet;
    private Quaternion[]? _turns;
    private bool[]? _isTurned;
    private bool _hasTurns;

    internal PoseJoints(Skeleton skeleton)
    {
        _skeleton = skeleton;
    }

    /// <summary>Whether any joint is set by <see cref="Set(int, in JointPose)"/>.</summary>
    public bool AnySet { get; private set; }

    /// <summary>Changes whenever a joint is set, turned or reached, so whoever works the joints out knows to again.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Sets a joint's local pose, relative to its parent, over whatever the layers give.
    /// It stays until it's set again or cleared.
    /// </summary>
    public void Set(int joint, in JointPose local)
    {
        int count = _skeleton.Count;
        if ((uint)joint >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(joint), joint, $"The rig has {count} joints, numbered from 0.");
        }

        _set ??= new JointPose[count];
        _isSet ??= new bool[count];
        _set[joint] = local;
        _isSet[joint] = true;
        AnySet = true;
        Version++;
    }

    /// <summary>Sets a joint by name. An unknown name throws, listing the joints the rig has.</summary>
    public void Set(string joint, in JointPose local) => Set(Index(joint), local);

    /// <summary>
    /// Turns a joint on top of whatever the layers give, by a rotation in the model's space,
    /// about the joint itself. Everything under it goes with it, so a turn of the spine
    /// leans the chest, arms and head together. It stays until it's turned again or the
    /// joints are cleared; <see cref="Quaternion.Identity"/> takes it away.
    /// </summary>
    public void Turn(int joint, Quaternion turn)
    {
        int count = _skeleton.Count;
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
    public void Turn(string joint, Quaternion turn) => Turn(Index(joint), turn);

    /// <summary>The number of a joint, for setting it quickly every frame. An unknown name throws, listing the joints.</summary>
    public int Index(string joint)
    {
        int index = _skeleton.IndexOf(joint);
        if (index < 0)
        {
            throw new ArgumentException($"The rig has no joint called {joint}. It has: {string.Join(", ", _skeleton.Names)}.", nameof(joint));
        }

        return index;
    }

    /// <summary>Hands every joint the game set, turned or reached back to the layers.</summary>
    public void Clear()
    {
        if (_isSet is not null)
        {
            Array.Clear(_isSet);
        }

        if (_isTurned is not null)
        {
            Array.Clear(_isTurned);
        }

        AnySet = false;
        _hasTurns = false;
        _reaches.Clear();
        Version++;
    }

    /// <summary>
    /// Bends a limb towards a goal in the model's space, taking away any reach the limb had.
    /// A weight of 0 leaves it with none. The pose checks the limb and the numbers.
    /// </summary>
    internal void Reach(LimbJoints limb, Vector3 goal, float weight, Vector3 point)
    {
        _reaches.RemoveAll(reach => reach.Bones == limb);
        if (weight > 0f)
        {
            _reaches.Add(new PoseReach(limb, goal, weight, point));
        }

        Version++;
    }

    /// <summary>Puts the set joints over the layers' pose, then the turns on, then the limbs' reaches.</summary>
    internal void Apply(Span<JointPose> pose)
    {
        if (AnySet)
        {
            for (int i = 0; i < pose.Length; i++)
            {
                if (_isSet![i])
                {
                    pose[i] = _set![i];
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
        int count = _skeleton.Count;
        Span<Matrix4x4> globals = count <= 128 ? stackalloc Matrix4x4[count] : new Matrix4x4[count];
        Span<Quaternion> turned = count <= 128 ? stackalloc Quaternion[count] : new Quaternion[count];
        var space = new SkeletonSpace(_skeleton.Parents, pose, globals, turned);
        Matrix4x4.Decompose(_skeleton.RootTransform, out _, out var rootTurn, out _);
        Matrix4x4.Invert(_skeleton.RootTransform, out var fromModel);

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
            space.Reach(reach.Bones, reach.Point, reach.Weight, Vector3.Transform(reach.Target, fromModel));
        }
    }

    private readonly record struct PoseReach(LimbJoints Bones, Vector3 Target, float Weight, Vector3 Point);
}
