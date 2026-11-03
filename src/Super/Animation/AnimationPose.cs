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
/// A game can also set joints itself, such as legs placed on the ground by IK; those
/// go over whatever the layers give.
/// </summary>
public sealed class AnimationPose
{
    private PoseLayer[] _layers = new PoseLayer[6];
    private JointPose[]? _joints;
    private bool[]? _isSet;

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

    /// <summary>Hands every joint set by the game back to the layers.</summary>
    public void ClearJoints()
    {
        if (_isSet is not null)
        {
            Array.Clear(_isSet);
        }

        HasSetJoints = false;
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
    /// skeleton's rest pose, so a pose with no layers is the model as modelled.
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
    }
}
