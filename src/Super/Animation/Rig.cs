using System.Numerics;
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
    public Rig(Skeleton skeleton, IReadOnlyList<AnimationClip> clips)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            if (clips[i].JointCount != skeleton.Count)
            {
                throw new ArgumentException(
                    $"Clip {clips[i].Name} has {clips[i].JointCount} joints, but the skeleton has {skeleton.Count}.", nameof(clips));
            }
        }

        Skeleton = skeleton;
        Clips = clips;
        ClipIndex = new RigClipIndex(clips);
        EventTables = new ClipEventTable[clips.Count];
        for (int i = 0; i < clips.Count; i++)
        {
            EventTables[i] = new ClipEventTable();
            foreach (var clipEvent in clips[i].Events)
            {
                EventTables[i].Add(clipEvent, Skeleton, sounds: null);
            }
        }
    }

    public Skeleton Skeleton { get; }

    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>
    /// The model the rig was read from, when it came from <see cref="Content.Rig"/>, so
    /// an animator can be drawn without naming its model again.
    /// </summary>
    public ModelRef? Model { get; internal set; }

    /// <summary>
    /// Who moves this rig's animators on each tick. Rigs from content have the game loop; others are moved by hand.
    /// Setting it names the events' sounds in its content, so a sound content hasn't got stops here.
    /// </summary>
    internal AnimatorRunner? Runner
    {
        get;
        set
        {
            field = value;
            if (value is not null)
            {
                foreach (var table in EventTables)
                {
                    table.NameSounds(value.Sounds);
                }
            }
        }
    }

    /// <summary>The clip names, for a game that wants to list them.</summary>
    public IEnumerable<string> ClipNames => ClipIndex.Names;

    /// <summary>The clips by name, and the check that a clip is one of them.</summary>
    internal RigClipIndex ClipIndex { get; }

    /// <summary>Each clip's events, by the clip's number.</summary>
    internal ClipEventTable[] EventTables { get; }

    /// <summary>A clip by the name it had in the model file or the importer's settings.</summary>
    public ClipRef Clip(string name) => ClipIndex.Find(name);

    public bool HasClip(string name) => ClipIndex.Has(name);

    /// <summary>
    /// Marks a moment in a clip, in seconds, such as a foot landing. An animator playing the
    /// clip reports it in <see cref="Animator.Events"/> on the tick it passes it, every time
    /// round a loop. Name a <paramref name="joint"/> to be told where it is then, such as the foot,
    /// and a <paramref name="sound"/> to play there then, such as a footstep.
    /// </summary>
    public void Event(ClipRef clip, string name, float at, string? joint = null, ClipSound? sound = null) => Event(clip, name, at, at, joint, sound);

    /// <summary>
    /// Marks a window in a clip, from one time to another in seconds, such as when a sword's
    /// hitbox is live. An animator reports when it begins and ends, and
    /// <see cref="Animator.IsActive"/> says whether it's open now. A <paramref name="sound"/> plays as it begins.
    /// </summary>
    public void Event(ClipRef clip, string name, float from, float to, string? joint = null, ClipSound? sound = null)
    {
        ClipIndex.Check(clip);
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("An event needs a name.", nameof(name));
        }

        float duration = Clips[clip.Index].Duration;
        if (!(from >= 0f) || !(to <= duration) || !(from <= to))
        {
            throw new ArgumentOutOfRangeException(
                nameof(from), $"Event {name} runs from {from} to {to} seconds, but clip {Clips[clip.Index].Name} lasts {duration}, and the start can't come after the end.");
        }

        EventTables[clip.Index].Add(new ClipEvent(name, from, to, joint) { Sound = sound }, Skeleton, Runner?.Sounds);
    }

    /// <summary>The moments and windows marked in a clip, from its cooked file and from <see cref="Event(ClipRef, string, float, string?, ClipSound?)"/>.</summary>
    public IReadOnlyList<ClipEvent> Events(ClipRef clip)
    {
        ClipIndex.Check(clip);
        return EventTables[clip.Index].Events;
    }

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
    /// An arm or a leg, for <see cref="AnimationPose.Reach(Limb, Vector3, float, Vector3)"/>: the shoulder,
    /// elbow and wrist, or hip, knee and ankle. Each must be under the one before. The
    /// <paramref name="tip"/> is what's carried to the goal, such as the joint a sword hangs
    /// from; it's the end itself when left out, and must be the end or under it.
    /// </summary>
    public Limb Limb(string upper, string lower, string end, string? tip = null) => Super.Limb.Find(this, upper, lower, end, tip);

    /// <summary>
    /// Where a joint is in the model at a moment of a clip, played alone: the hand at the
    /// height of a swing, say, for working out where a blow will land before it's played.
    /// It works the whole skeleton out, so it's for planning rather than every frame.
    /// </summary>
    public Matrix4x4 Joint(ClipRef clip, float seconds, string joint)
    {
        ClipIndex.Check(clip);
        int index = Skeleton.IndexOf(joint);
        if (index < 0)
        {
            throw new ArgumentException($"The rig has no joint called {joint}. It has: {string.Join(", ", Skeleton.Names)}.", nameof(joint));
        }

        var locals = new JointPose[Skeleton.Count];
        Clips[clip.Index].Sample(seconds, locals);
        var globals = new Matrix4x4[index + 1];
        for (int i = 0; i <= index; i++)
        {
            globals[i] = Skeleton.Parents[i] >= 0 ? locals[i].Matrix * globals[Skeleton.Parents[i]] : locals[i].Matrix;
        }

        return globals[index] * Skeleton.RootTransform;
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
            ClipIndex.Check(points[i].Clip);
            if (i > 0 && !(points[i].At > points[i - 1].At))
            {
                throw new ArgumentException($"Blend points must rise, but {points[i].At} comes after {points[i - 1].At}.", nameof(points));
            }

            sorted[i] = (points[i].Clip.Index, points[i].At);
        }

        return new Motion(this, sorted);
    }

    /// <summary>The joint whose travel across the ground moves the character, once <see cref="RootMotion"/> has picked one.</summary>
    public string? RootMotionJoint { get; private set; }

    /// <summary>The travel taken out of the clips once <see cref="RootMotion"/> has picked a joint, or null.</summary>
    internal RigTravel? RootTravel { get; private set; }

    /// <summary>
    /// Takes the travel across the ground out of every clip and hands it to the game
    /// instead, so a dodge or a roll moves the character and not only its mesh. Name the
    /// joint that carries the body, such as the hips. Its height stays in the clips, so a
    /// jump still leaves the ground, and so do its turns, apart from in the clips named in
    /// <paramref name="turning"/>, such as a turn on the spot: their turns come out too, so
    /// the character ends facing the new way instead of snapping back. Each animator then
    /// reports the travel in <see cref="Animator.RootMotion"/> and the turn in
    /// <see cref="Animator.RootTurn"/>, for the game to add to where the character is and
    /// faces. Asking again for the same joint and clips does nothing.
    /// </summary>
    public void RootMotion(string joint, params ReadOnlySpan<ClipRef> turning)
    {
        var turns = new HashSet<int>();
        foreach (var clip in turning)
        {
            if ((uint)clip.Index >= (uint)Clips.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(turning), clip.Index, $"The rig has {Clips.Count} clips, so it has no clip {clip.Index} to turn with.");
            }

            turns.Add(clip.Index);
        }

        if (RootTravel is { } taken)
        {
            if (RootMotionJoint == joint && taken.Turning.SetEquals(turns))
            {
                return;
            }

            throw new InvalidOperationException(RootMotionJoint == joint
                ? $"The rig already takes its root motion from {joint} with other clips turning, so name every turning clip in one call."
                : $"The rig already takes its root motion from {RootMotionJoint}, so it can't take it from {joint} too.");
        }

        int index = Skeleton.IndexOf(joint);
        if (index < 0)
        {
            throw new ArgumentException($"The rig has no joint called {joint}. It has: {string.Join(", ", Skeleton.Names)}.", nameof(joint));
        }

        RootTravel = new RigTravel(Skeleton, Clips, index, turns);
        RootMotionJoint = joint;
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

    /// <summary>How long one cycle takes at speed 1: the clips' durations mixed as they are at this parameter.</summary>
    internal float CycleSeconds(float parameter)
    {
        var (first, second, towards) = Pick(parameter);
        return (1f - towards) * Rig.Clips[first].Duration + towards * Rig.Clips[second].Duration;
    }
}
