using System.Numerics;
using Rendition;

namespace Super.Physics;

/// <summary>
/// A ragdoll fitted to its rig's mesh: each part's box, mass and joint axes, and which way the character faces, all
/// worked out once from the rest pose.
/// </summary>
internal sealed class RagdollFitting
{
    private readonly RagdollTree _tree;

    public RagdollFitting(Skeleton skeleton, RagdollTree tree, IReadOnlyList<RagdollPart> parts, float mass)
    {
        _tree = tree;
        var rest = RestGlobals(skeleton);
        Matrix4x4.Decompose(skeleton.RootTransform, out var rootScale, out var rootTurn, out _);
        if (MathF.Abs(rootScale.X - rootScale.Y) > 1e-3f * rootScale.X || MathF.Abs(rootScale.X - rootScale.Z) > 1e-3f * rootScale.X)
        {
            throw new ArgumentException($"The rig's root transform scales unevenly, by {rootScale}, so its parts can't be fitted.", "rig");
        }

        RootScale = rootScale.X;
        var fromModel = Quaternion.Inverse(rootTurn);
        Forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, fromModel));
        Up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, fromModel));

        int count = parts.Count;
        Fits = new JointBounds[count];
        Masses = new float[count];
        Inertias = new float[count];
        Frames = new JointFrame[count];
        float shares = parts.Sum(part => part.Mass);
        for (int i = 0; i < count; i++)
        {
            Fits[i] = Fit(skeleton, rest, parts[i], i);
            Masses[i] = mass * parts[i].Mass / shares;
            var size = Fits[i].Size;
            float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            float shortest = MathF.Min(size.X, MathF.Min(size.Y, size.Z));
            Inertias[i] = Masses[i] * ((longest * longest) + (shortest * shortest)) / 12f;
        }

        for (int i = 1; i < count; i++)
        {
            Frames[i] = Frame(skeleton, rest, parts[i], i);
        }

        var intoPelvis = Quaternion.Inverse(Turn(rest[tree.Joints[0]]));
        PelvisForward = Vector3.Transform(Forward, intoPelvis);
        PelvisUp = Vector3.Transform(Up, intoPelvis);
        PelvisHeight = Vector3.Dot(rest[tree.Joints[0]].Translation, Up) - Lowest(rest);
    }

    /// <summary>Each part's box, in its joint's own space and the skeleton's units.</summary>
    public JointBounds[] Fits { get; }

    /// <summary>Each part's mass, in kilograms.</summary>
    public float[] Masses { get; }

    /// <summary>Roughly how hard each part is to turn, in kilograms times the skeleton's units squared.</summary>
    public float[] Inertias { get; }

    /// <summary>Each part's joint to its parent: the axes in the parent's and the part's own joint space.</summary>
    public JointFrame[] Frames { get; }

    /// <summary>The character's front and up, in the skeleton's space.</summary>
    public Vector3 Forward { get; }

    public Vector3 Up { get; }

    /// <summary>The character's front and up in the first part's joint space, to tell a body lying face up from face down.</summary>
    public Vector3 PelvisForward { get; }

    public Vector3 PelvisUp { get; }

    /// <summary>How high the first part's joint stands above the lowest vertex at rest, in the skeleton's units.</summary>
    public float PelvisHeight { get; }

    /// <summary>How much the rig's root transform scales the skeleton, such as from centimetres to metres.</summary>
    public float RootScale { get; }

    /// <summary>Each joint's place in the skeleton at rest.</summary>
    internal static Matrix4x4[] RestGlobals(Skeleton skeleton)
    {
        var globals = new Matrix4x4[skeleton.Count];
        for (int i = 0; i < skeleton.Count; i++)
        {
            globals[i] = skeleton.Parents[i] >= 0 ? skeleton.Rest[i].Matrix * globals[skeleton.Parents[i]] : skeleton.Rest[i].Matrix;
        }

        return globals;
    }

    /// <summary>The box round every vertex the part's joints move, in the part's joint space.</summary>
    private JointBounds Fit(Skeleton skeleton, Matrix4x4[] rest, RagdollPart settings, int part)
    {
        Matrix4x4.Invert(rest[_tree.Joints[part]], out var intoPart);
        var box = JointBounds.Empty;
        for (int j = 0; j < skeleton.Count; j++)
        {
            var bounds = skeleton.Bounds![j];
            if (_tree.PartOfJoint[j] != part || bounds.IsEmpty)
            {
                continue;
            }

            var toPart = rest[j] * intoPart;
            for (int corner = 0; corner < 8; corner++)
            {
                box = box.Including(Vector3.Transform(bounds.Corner(corner), toPart));
            }
        }

        if (box.IsEmpty)
        {
            throw new ArgumentException($"No vertex follows part {settings.Joint} or the joints under it most, so it has no size. Join it to a neighbouring part instead.", nameof(part));
        }

        return box;
    }

    /// <summary>How far below the first part's joint the lowest corner of any part reaches at rest, along up.</summary>
    private float Lowest(Matrix4x4[] rest)
    {
        float lowest = float.MaxValue;
        for (int i = 0; i < Fits.Length; i++)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                lowest = MathF.Min(lowest, Vector3.Dot(Vector3.Transform(Fits[i].Corner(corner), rest[_tree.Joints[i]]), Up));
            }
        }

        return lowest;
    }

    /// <summary>
    /// The axes of a part's joint at rest: the twist axis runs along the part, or for a hinge across it, and the
    /// parent's copy leans forward by <see cref="RagdollPart.Lean"/>. Each is kept in its own joint's space, so the
    /// limits hold from the rest pose whatever pose the ragdoll starts in.
    /// </summary>
    private JointFrame Frame(Skeleton skeleton, Matrix4x4[] rest, RagdollPart settings, int part)
    {
        // A hinge's other two ways are held this close to still, in radians.
        const float HingeSlack = 0.05f;

        var along = Along(skeleton, rest, part);
        var (twist, plane) = Axes(along, settings.Bend);
        var (parentTwist, parentPlane) = Leaned(twist, plane, along, settings);
        var intoParent = Quaternion.Inverse(Turn(rest[_tree.Joints[_tree.ParentParts[part]]]));
        var intoChild = Quaternion.Inverse(Turn(rest[_tree.Joints[part]]));
        bool hinge = settings.Bend != RagdollBend.Any;
        float swing = settings.Swing * MathF.PI / 180f;
        float twistLimit = settings.Twist * MathF.PI / 180f;
        return new JointFrame(
            Vector3.Transform(parentTwist, intoParent),
            Vector3.Transform(parentPlane, intoParent),
            Vector3.Transform(twist, intoChild),
            Vector3.Transform(plane, intoChild),
            hinge ? HingeSlack : swing,
            hinge ? -HingeSlack : -twistLimit,
            hinge ? swing : twistLimit);
    }

    /// <summary>A joint's twist axis and the plane its cone is measured in, for a part running <paramref name="along"/>.</summary>
    private (Vector3 Twist, Vector3 Plane) Axes(Vector3 along, RagdollBend bend)
    {
        if (bend == RagdollBend.Any)
        {
            var across = MathF.Abs(Vector3.Dot(along, Forward)) < 0.9f ? Forward : Up;
            return (along, Vector3.Normalize(Vector3.Cross(along, across)));
        }

        // A hinge turns about the axis across the part and the way it bends.
        var towards = bend == RagdollBend.Forward ? Forward : -Forward;
        var axis = Vector3.Cross(along, towards);
        if (axis.LengthSquared() < 1e-6f)
        {
            axis = Vector3.Cross(along, Up);
        }

        return (Vector3.Normalize(axis), along);
    }

    /// <summary>The parent's copy of a joint's axes, leaned forward by <see cref="RagdollPart.Lean"/>.</summary>
    private (Vector3 Twist, Vector3 Plane) Leaned(Vector3 twist, Vector3 plane, Vector3 along, RagdollPart settings)
    {
        if (settings.Lean == 0f || settings.Bend != RagdollBend.Any)
        {
            return (twist, plane);
        }

        var leanAxis = Vector3.Cross(along, Forward);
        if (!(leanAxis.LengthSquared() > 1e-6f))
        {
            return (twist, plane);
        }

        var lean = Quaternion.CreateFromAxisAngle(Vector3.Normalize(leanAxis), settings.Lean * MathF.PI / 180f);
        return (Vector3.Transform(twist, lean), Vector3.Transform(plane, lean));
    }

    /// <summary>
    /// Which way a part runs from its joint at rest, in the skeleton's space: towards its only child joint, or, with
    /// none or several, towards the middle of what it moves.
    /// </summary>
    private Vector3 Along(Skeleton skeleton, Matrix4x4[] rest, int part)
    {
        int joint = _tree.Joints[part];
        int child = -1;
        int children = 0;
        for (int j = joint + 1; j < skeleton.Count; j++)
        {
            if (skeleton.Parents[j] == joint)
            {
                child = j;
                children++;
            }
        }

        var from = rest[joint].Translation;
        var to = children == 1 ? rest[child].Translation : Vector3.Transform(Fits[part].Centre, rest[joint]);
        var along = to - from;
        return along.LengthSquared() > 1e-10f ? Vector3.Normalize(along) : Up;
    }

    private static Quaternion Turn(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out _, out var turn, out _);
        return turn;
    }
}

/// <summary>A ragdoll joint's axes, in the parent part's and the child part's own joint space, and its limits in radians.</summary>
internal readonly record struct JointFrame(Vector3 Twist1, Vector3 Plane1, Vector3 Twist2, Vector3 Plane2, float Cone, float TwistMin, float TwistMax);
