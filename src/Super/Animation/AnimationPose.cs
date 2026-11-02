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
/// skins the model. It holds no joint data itself, so a headless game can look at it.
/// </summary>
public sealed class AnimationPose
{
    private PoseLayer[] _layers = new PoseLayer[6];

    public AnimationPose(Rig rig)
    {
        Rig = rig;
    }

    public Rig Rig { get; }

    public int LayerCount { get; private set; }

    public ReadOnlySpan<PoseLayer> Layers => _layers.AsSpan(0, LayerCount);

    public void Clear() => LayerCount = 0;

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
    }
}
