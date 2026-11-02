using Rendition;

namespace Super;

/// <summary>Names one of a <see cref="Rig"/>'s clips.</summary>
public readonly record struct ClipRef(int Index);

/// <summary>
/// A rigged model's skeleton and clips, read from the front of its cooked file, so a
/// game has them on the main thread right away. Make masks and blends here, then give
/// them to an <see cref="AnimationController{TState, TAction}"/>.
/// </summary>
public sealed class Rig
{
    private readonly Dictionary<string, ClipRef> _clipsByName = new(StringComparer.Ordinal);

    public Rig(Skeleton skeleton, IReadOnlyList<AnimationClip> clips)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].JointCount != skeleton.Count)
            {
                throw new ArgumentException(
                    $"Clip {clips[i].Name} has {clips[i].JointCount} joints, but the skeleton has {skeleton.Count}.", nameof(clips));
            }

            _clipsByName.TryAdd(clips[i].Name, new ClipRef(i));
        }

        Skeleton = skeleton;
        Clips = clips;
    }

    public Skeleton Skeleton { get; }

    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>
    /// The model the rig was read from, when it came from <see cref="Content.Rig"/>, so
    /// an animator can be drawn without naming its model again.
    /// </summary>
    public ModelRef? Model { get; internal set; }

    /// <summary>Who moves this rig's animators on each tick. Rigs from content have the game loop; others are moved by hand.</summary>
    internal AnimatorRunner? Runner { get; set; }

    /// <summary>The clip names, for a game that wants to list them.</summary>
    public IEnumerable<string> ClipNames => Clips.Select(clip => clip.Name);

    /// <summary>A clip by the name it had in the model file or the importer's settings.</summary>
    public ClipRef Clip(string name)
    {
        if (!_clipsByName.TryGetValue(name, out var clip))
        {
            throw new ArgumentException($"The rig has no clip called {name}. It has: {string.Join(", ", ClipNames)}.", nameof(name));
        }

        return clip;
    }

    public bool HasClip(string name) => _clipsByName.ContainsKey(name);

    /// <summary>
    /// The joints with these names and every joint under them, so an action on the
    /// spine moves the arms and head but leaves the legs to the base state.
    /// </summary>
    public BoneMask Mask(params ReadOnlySpan<string> joints)
    {
        if (joints.IsEmpty)
        {
            throw new ArgumentException("A mask needs at least one joint.", nameof(joints));
        }

        var marks = new bool[Skeleton.Count];
        foreach (string name in joints)
        {
            int joint = Skeleton.IndexOf(name);
            if (joint < 0)
            {
                throw new ArgumentException($"The rig has no joint called {name}. It has: {string.Join(", ", Skeleton.Names)}.", nameof(joints));
            }

            Skeleton.MarkDescendants(joint, marks);
        }

        var weights = new float[marks.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = marks[i] ? 1f : 0f;
        }

        return new BoneMask(this, weights);
    }

    /// <summary>
    /// A motion that picks between clips by a number, such as speed: at 1.5 the walk,
    /// at 4 the run, and a mix between. The clips play in step, so feet keep landing
    /// together. Points must rise.
    /// </summary>
    public Motion Blend(params ReadOnlySpan<(ClipRef Clip, float At)> points)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException("A blend needs at least two clips.", nameof(points));
        }

        var sorted = new (int Clip, float At)[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Check(points[i].Clip);
            if (i > 0 && !(points[i].At > points[i - 1].At))
            {
                throw new ArgumentException($"Blend points must rise, but {points[i].At} comes after {points[i - 1].At}.", nameof(points));
            }

            sorted[i] = (points[i].Clip.Index, points[i].At);
        }

        return new Motion(this, sorted);
    }

    internal void Check(ClipRef clip)
    {
        if (clip.Index < 0 || clip.Index >= Clips.Count)
        {
            throw new ArgumentException($"Clip {clip.Index} doesn't exist. The rig has {Clips.Count} clips.", nameof(clip));
        }
    }
}

/// <summary>Which joints an action moves, as a weight per joint. From <see cref="Rig.Mask"/>.</summary>
public sealed class BoneMask
{
    internal BoneMask(Rig rig, float[] weights)
    {
        Rig = rig;
        Weights = weights;
    }

    internal Rig Rig { get; }

    internal float[] Weights { get; }
}

/// <summary>
/// What a state plays: one clip, or a blend of clips picked by a number. From
/// <see cref="Rig.Blend"/>, or a <see cref="ClipRef"/> on its own.
/// </summary>
public sealed class Motion
{
    internal Motion(Rig rig, (int Clip, float At)[] points)
    {
        Rig = rig;
        Points = points;
    }

    internal Rig Rig { get; }

    /// <summary>Clips and where each sits on the parameter, rising. One point is a plain clip.</summary>
    internal (int Clip, float At)[] Points { get; }

    /// <summary>
    /// The two clips around a parameter and how far towards the second, so a blend
    /// of walk at 1.5 and run at 4 gives (walk, run, 0.5) at 2.75.
    /// </summary>
    internal (int First, int Second, float Towards) Pick(float parameter)
    {
        if (Points.Length == 1 || parameter <= Points[0].At)
        {
            return (Points[0].Clip, Points[0].Clip, 0f);
        }

        for (int i = 1; i < Points.Length; i++)
        {
            if (parameter <= Points[i].At)
            {
                float towards = (parameter - Points[i - 1].At) / (Points[i].At - Points[i - 1].At);
                return (Points[i - 1].Clip, Points[i].Clip, towards);
            }
        }

        int last = Points[^1].Clip;
        return (last, last, 0f);
    }
}
