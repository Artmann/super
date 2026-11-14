using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>Where a joint is relative to its parent: position, rotation and scale.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JointPose(Vector3 position, Quaternion rotation, Vector3 scale)
{
    public Vector3 Position = position;
    public Quaternion Rotation = rotation;
    public Vector3 Scale = scale;

    public static JointPose Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The pose as a matrix: scale, then rotation, then position, for row vectors.</summary>
    public readonly Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>
    /// Blends between two poses. Rotations take the short way round, so a joint never
    /// spins the long way between two nearby frames.
    /// </summary>
    public static JointPose Lerp(in JointPose from, in JointPose to, float amount)
    {
        var target = to.Rotation;
        if (Quaternion.Dot(from.Rotation, target) < 0f)
        {
            target = -target;
        }

        return new JointPose(
            Vector3.Lerp(from.Position, to.Position, amount),
            Quaternion.Normalize(Quaternion.Lerp(from.Rotation, target, amount)),
            Vector3.Lerp(from.Scale, to.Scale, amount));
    }

    /// <summary>Takes a matrix apart. Shear is lost.</summary>
    public static JointPose FromMatrix(in Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var position))
        {
            throw new ArgumentException("The matrix can't be split into scale, rotation and position.", nameof(matrix));
        }

        return new JointPose(position, rotation, scale);
    }
}

/// <summary>A box lined up with a joint's own axes, from its least corner to its greatest.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct JointBounds(Vector3 Min, Vector3 Max)
{
    /// <summary>A box round nothing, which grows to the first point included.</summary>
    public static JointBounds Empty { get; } = new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Centre => IsEmpty ? Vector3.Zero : (Min + Max) / 2f;

    /// <summary>The box grown to take in a point.</summary>
    public JointBounds Including(Vector3 point) => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>One of the box's eight corners: bits 1, 2 and 4 of <paramref name="index"/> pick the greatest x, y and z.</summary>
    public Vector3 Corner(int index) => new(
        (index & 1) == 0 ? Min.X : Max.X,
        (index & 2) == 0 ? Min.Y : Max.Y,
        (index & 4) == 0 ? Min.Z : Max.Z);
}

/// <summary>
/// The joints of a rigged model, parents before children. Each joint's rest pose is
/// relative to its parent, and its inverse bind matrix takes a mesh vertex into the
/// joint's own space. The root transform is applied after skinning, and carries the
/// scale and turn the importer gives a model.
/// </summary>
public sealed class Skeleton
{
    /// <summary>The most joints a model can have, since a vertex names them by byte.</summary>
    public const int MaxJoints = 256;

    private readonly Dictionary<string, int> _indexOf;

    public Skeleton(string[] names, int[] parents, JointPose[] rest, Matrix4x4[] inverseBind, Matrix4x4 rootTransform, JointBounds[]? bounds = null)
    {
        if (names.Length != parents.Length || names.Length != rest.Length || names.Length != inverseBind.Length)
        {
            throw new ArgumentException("Every joint needs a name, a parent, a rest pose and an inverse bind matrix.");
        }

        if (bounds is not null && bounds.Length != names.Length)
        {
            throw new ArgumentException($"The bounds need one entry per joint, {names.Length}, not {bounds.Length}.", nameof(bounds));
        }

        if (names.Length > MaxJoints)
        {
            throw new ArgumentException($"A skeleton can have at most {MaxJoints} joints, not {names.Length}.", nameof(names));
        }

        for (int i = 0; i < parents.Length; i++)
        {
            if (parents[i] >= i)
            {
                throw new ArgumentException($"Joint {i} ({names[i]}) comes before its parent {parents[i]}. Parents must come first.", nameof(parents));
            }
        }

        Names = names;
        Parents = parents;
        Rest = rest;
        InverseBind = inverseBind;
        RootTransform = rootTransform;
        Bounds = bounds;
        _indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            _indexOf.TryAdd(names[i], i);
        }
    }

    public string[] Names { get; }

    /// <summary>Each joint's parent, or -1 for a root. Parents always come before their children.</summary>
    public int[] Parents { get; }

    public JointPose[] Rest { get; }

    public Matrix4x4[] InverseBind { get; }

    public Matrix4x4 RootTransform { get; }

    public int Count => Names.Length;

    /// <summary>
    /// For each joint, the box round the vertices that follow it most, in the joint's own
    /// space, so a ragdoll's shapes can fit the mesh. Null when the skeleton was made without
    /// a mesh; a joint no vertex follows most has an empty box.
    /// </summary>
    public JointBounds[]? Bounds { get; }

    /// <summary>
    /// The same skeleton with <see cref="Bounds"/> measured from these vertices, each counted
    /// for the joint that moves it most.
    /// </summary>
    public Skeleton WithBounds(ReadOnlySpan<Vertex> vertices)
    {
        var bounds = new JointBounds[Count];
        bounds.AsSpan().Fill(JointBounds.Empty);
        foreach (ref readonly var vertex in vertices)
        {
            int joint = -1;
            uint heaviest = 0;
            for (int i = 0; i < 4; i++)
            {
                uint weight = (vertex.Weights >> (8 * i)) & 0xFF;
                if (weight > heaviest)
                {
                    heaviest = weight;
                    joint = (int)((vertex.Joints >> (8 * i)) & 0xFF);
                }
            }

            if (joint < 0 || joint >= Count)
            {
                continue;
            }

            bounds[joint] = bounds[joint].Including(Vector3.Transform(vertex.Position, InverseBind[joint]));
        }

        return new Skeleton(Names, Parents, Rest, InverseBind, RootTransform, bounds);
    }

    /// <summary>The joint with this name, or -1.</summary>
    public int IndexOf(string name) => _indexOf.GetValueOrDefault(name, -1);

    /// <summary>Sets true for a joint and every joint under it.</summary>
    public void MarkDescendants(int joint, Span<bool> marks)
    {
        if (marks.Length != Count)
        {
            throw new ArgumentException($"The marks need one entry per joint, {Count}, not {marks.Length}.", nameof(marks));
        }

        marks[joint] = true;
        for (int i = joint + 1; i < Count; i++)
        {
            if (Parents[i] >= 0 && marks[Parents[i]])
            {
                marks[i] = true;
            }
        }
    }

    /// <summary>
    /// Turns local poses into the matrices that move each vertex from the mesh into the
    /// posed model, one per joint, ready for the vertex shader.
    /// </summary>
    public void SkinMatrices(ReadOnlySpan<JointPose> locals, Span<Matrix4x4> skin)
    {
        if (locals.Length != Count || skin.Length != Count)
        {
            throw new ArgumentException($"The skeleton has {Count} joints, but got {locals.Length} poses and {skin.Length} matrices.");
        }

        // The skin span holds the globals until each joint's children have read it,
        // which they have once it's the joint's own turn to be finished.
        for (int i = 0; i < Count; i++)
        {
            var global = locals[i].Matrix;
            if (Parents[i] >= 0)
            {
                global *= skin[Parents[i]];
            }

            skin[i] = global;
        }

        for (int i = Count - 1; i >= 0; i--)
        {
            skin[i] = InverseBind[i] * skin[i] * RootTransform;
        }
    }
}

/// <summary>
/// A motion for a skeleton, sampled at a steady rate into one pose per joint per frame,
/// so playing it is a single interpolation. The last frame sits at <see cref="Duration"/>.
/// </summary>
public sealed class AnimationClip
{
    public AnimationClip(string name, float frameRate, int jointCount, JointPose[] frames)
    {
        if (!(frameRate > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), frameRate, "The frame rate must be more than zero.");
        }

        if (jointCount <= 0 || frames.Length < jointCount || frames.Length % jointCount != 0)
        {
            throw new ArgumentException($"A clip needs whole frames of {jointCount} joints, but has {frames.Length} poses.", nameof(frames));
        }

        Name = name;
        FrameRate = frameRate;
        JointCount = jointCount;
        Frames = frames;
    }

    public string Name { get; }

    public float FrameRate { get; }

    public int JointCount { get; }

    /// <summary>Frame after frame, each with one pose per joint.</summary>
    public JointPose[] Frames { get; }

    public int FrameCount => Frames.Length / JointCount;

    /// <summary>Seconds from the first frame to the last.</summary>
    public float Duration => (FrameCount - 1) / FrameRate;

    /// <summary>The pose at a moment, held at the ends. Looping is the caller's, by wrapping the time.</summary>
    public void Sample(float seconds, Span<JointPose> pose)
    {
        if (pose.Length != JointCount)
        {
            throw new ArgumentException($"The clip has {JointCount} joints, not {pose.Length}.", nameof(pose));
        }

        float frame = Math.Clamp(seconds * FrameRate, 0f, FrameCount - 1);
        int first = Math.Min((int)frame, FrameCount - 2);
        if (first < 0)
        {
            Frames.AsSpan(0, JointCount).CopyTo(pose);
            return;
        }

        float amount = frame - first;
        var a = Frames.AsSpan(first * JointCount, JointCount);
        var b = Frames.AsSpan((first + 1) * JointCount, JointCount);
        for (int i = 0; i < JointCount; i++)
        {
            pose[i] = JointPose.Lerp(in a[i], in b[i], amount);
        }
    }
}

/// <summary>Mixes poses joint by joint.</summary>
public static class PoseBlend
{
    /// <summary>
    /// Moves <paramref name="into"/> towards <paramref name="from"/> by <paramref name="amount"/>,
    /// scaled per joint by the mask, if there is one. An amount of 1 with no mask copies.
    /// </summary>
    public static void Blend(Span<JointPose> into, ReadOnlySpan<JointPose> from, float amount, ReadOnlySpan<float> mask = default)
    {
        if (into.Length != from.Length || (!mask.IsEmpty && mask.Length != into.Length))
        {
            throw new ArgumentException($"Poses of {into.Length}, {from.Length} and a mask of {mask.Length} joints don't match.");
        }

        for (int i = 0; i < into.Length; i++)
        {
            float share = mask.IsEmpty ? amount : amount * mask[i];
            if (share <= 0f)
            {
                continue;
            }

            into[i] = share >= 1f ? from[i] : JointPose.Lerp(in into[i], in from[i], share);
        }
    }
}
