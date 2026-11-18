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
/// A game can also set and turn joints itself through <see cref="Joints"/>, which go over
/// whatever the layers give, such as a body knocked back by a blow, and reach limbs to goals
/// by IK, such as a hand bringing a sword onto what it strikes.
/// </summary>
public sealed class AnimationPose
{
    private readonly HeldJoints _held = new();
    private PoseLayer[] _layers = new PoseLayer[6];
    private int _version;

    public AnimationPose(Rig rig)
    {
        Rig = rig;
        Joints = new PoseJoints(rig.Skeleton);
    }

    public Rig Rig { get; }

    public int LayerCount { get; private set; }

    public ReadOnlySpan<PoseLayer> Layers => _layers.AsSpan(0, LayerCount);

    /// <summary>
    /// The joints the game sets, turns and reaches over the layers. They stay until
    /// <see cref="PoseJoints.Clear"/>; taking the layers away leaves them.
    /// </summary>
    public PoseJoints Joints { get; }

    /// <summary>
    /// Where the model was last drawn with this pose, in the world, so its clips' sounds play
    /// where it stands. Null until it's drawn.
    /// </summary>
    internal Matrix4x4? Drawn { get; set; }

    /// <summary>
    /// Changes whenever the game sets, turns or reaches a joint, or physics takes or lets go of
    /// joints, so whoever works the joints out knows to again.
    /// </summary>
    internal int Version => _version + Joints.Version;

    /// <summary>
    /// Whether physics holds any joints, such as while a ragdoll is simulated or blending back to the clips.
    /// </summary>
    public bool IsPhysical => _held.IsActive;

    /// <summary>Takes away the layers. Joints set by the game stay until <see cref="PoseJoints.Clear"/>.</summary>
    public void Clear() => LayerCount = 0;

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

        Joints.Reach(limb.Joints, goal, weight, point);
    }

    /// <summary>Adds a layer. Layers with no amount are left out, since they'd change nothing.</summary>
    public void Add(in PoseLayer layer)
    {
        if (layer.Amount <= 0f)
        {
            return;
        }

        Rig.ClipIndex.Check(new ClipRef(layer.Clip));
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
        _version++;
    }

    /// <summary>Hands every joint physics held back to the layers.</summary>
    internal void ClearPhysical()
    {
        if (_held.Clear())
        {
            _version++;
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
        Joints.Apply(pose);
        if (physical is { } amount && _held.IsActive)
        {
            _held.Apply(skeleton.Parents, amount, pose);
        }
    }

    /// <summary>
    /// How far the layers carry the character over this much time, once the rig has root motion, mixed as the
    /// layers mix the root joint: each later layer takes its share from those before. Zero without root motion.
    /// </summary>
    internal Vector3 TravelOver(float seconds)
    {
        if (Rig.RootTravel is not { } travel)
        {
            return Vector3.Zero;
        }

        var sum = Vector3.Zero;
        float left = 1f;
        var layers = Layers;
        for (int i = layers.Length - 1; i >= 0 && left > 0f; i--)
        {
            var layer = layers[i];
            float share = Math.Clamp(layer.Amount * (layer.Mask?.Weights[travel.CarrierJoint] ?? 1f), 0f, 1f);
            if (share <= 0f)
            {
                continue;
            }

            sum += left * share * travel.Over(layer.Clip, layer.Time, layer.Speed, layer.Loops, seconds);
            left *= 1f - share;
        }

        return sum;
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
}
