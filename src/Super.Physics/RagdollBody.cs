using System.Numerics;
using Rendition;

namespace Super.Physics;

/// <summary>The shape a <see cref="RagdollPart"/> is given, fitted to the vertices its joints move.</summary>
public enum RagdollShape
{
    /// <summary>A capsule along the part's longest side, for arms, legs and necks.</summary>
    Capsule,

    /// <summary>A box round the part, for a pelvis, chest, hand or foot.</summary>
    Box,

    /// <summary>A ball, for a head.</summary>
    Ball,
}

/// <summary>Which way a part's joint bends.</summary>
public enum RagdollBend
{
    /// <summary>Any way, within a cone of <see cref="RagdollPart.Swing"/>, and twisting by up to <see cref="RagdollPart.Twist"/>: a shoulder or hip.</summary>
    Any,

    /// <summary>One way only, towards the character's front, by up to <see cref="RagdollPart.Swing"/>: an elbow.</summary>
    Forward,

    /// <summary>One way only, towards the character's back, by up to <see cref="RagdollPart.Swing"/>: a knee.</summary>
    Back,
}

/// <summary>
/// One body of a ragdoll: a joint, the joints under it that no other part takes, and the vertices they move most. The
/// joint to its parent part bends as <see cref="Bend"/> and <see cref="Swing"/> say, measured from the model's rest
/// pose. A joint's name can leave out a prefix such as <c>mixamorig:</c>.
/// </summary>
/// <param name="Joint">The joint the part starts at, such as <c>LeftUpLeg</c>.</param>
/// <param name="Mass">Its share of the ragdoll's mass. Shares are scaled to add up to the whole.</param>
public sealed record RagdollPart(string Joint, float Mass)
{
    public RagdollShape Shape { get; init; } = RagdollShape.Capsule;

    /// <summary>How far the joint swings from rest, in degrees: the half angle of its cone, or for a hinge how far it bends.</summary>
    public float Swing { get; init; } = 30f;

    /// <summary>How far the part twists about its own length either way, in degrees. A hinge doesn't twist.</summary>
    public float Twist { get; init; } = 20f;

    public RagdollBend Bend { get; init; } = RagdollBend.Any;

    /// <summary>Tilts the middle of the joint's cone towards the character's front, in degrees, so a hip swings further forward than back.</summary>
    public float Lean { get; init; }

    /// <summary>Scales the width the part is fitted to, such as below 1 so arms pass close to the body.</summary>
    public float Thickness { get; init; } = 1f;
}

/// <summary>
/// The bodies and joints of a ragdoll for one <see cref="Super.Rig"/>, fitted to its mesh. Make one for a humanoid with
/// <see cref="Humanoid"/>, or list the parts yourself; share it between every character with the rig, and give each a
/// <see cref="Ragdoll"/>. The rig must be cooked with its mesh, which gives each joint the size of what it moves.
/// </summary>
public sealed class RagdollBody
{
    // Capsules and balls stand a little inside the mesh, so limbs that touch at rest, such as thighs, don't start pushed apart.
    private const float Inset = 0.85f;

    public RagdollBody(Rig rig, IReadOnlyList<RagdollPart> parts, float mass = 70f)
    {
        var skeleton = rig.Skeleton;
        if (parts.Count == 0)
        {
            throw new ArgumentException("A ragdoll needs at least one part.", nameof(parts));
        }

        if (!(mass > 0f) || !float.IsFinite(mass))
        {
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "A ragdoll's mass must be above 0.");
        }

        if (skeleton.Bounds is null)
        {
            throw new ArgumentException("The rig's skeleton has no bounds, so the parts can't be fitted to the mesh. Read the rig from a model cooked with its mesh.", nameof(rig));
        }

        foreach (var part in parts)
        {
            Check(part);
        }

        Rig = rig;
        Parts = parts;
        Mass = mass;
        PartTree = new RagdollTree(skeleton, parts);
        Fitting = new RagdollFitting(skeleton, PartTree, parts, mass);
        var rest = RagdollFitting.RestGlobals(skeleton);
        Touching = RagdollTouching.Pairs(PartTree.ParentParts, [.. Enumerable.Range(0, parts.Count).Select(part => RagdollTouching.Covering(ShapeOf(part), rest[PartTree.Joints[part]]))]);
    }

    public Rig Rig { get; }

    public IReadOnlyList<RagdollPart> Parts { get; }

    /// <summary>The whole ragdoll's mass, in kilograms.</summary>
    public float Mass { get; }

    /// <summary>How rough the parts are, from 0 (ice) up.</summary>
    public float Friction { get; init; } = 0.7f;

    /// <summary>How much of their speed the parts keep when they hit something, from 0 to 1.</summary>
    public float Bounciness { get; init; } = 0.1f;

    /// <summary>The layers the parts are on, as bits.</summary>
    public uint Layers { get; init; } = 1;

    /// <summary>The layers whose colliders the parts bump into, as bits.</summary>
    public uint CollidesWith { get; init; } = LayerBits.All;

    /// <summary>
    /// Whether the parts sweep through each step rather than jump, so a body thrown fast doesn't pass through a thin
    /// wall. On by default, since limbs are thin and ragdolls are often thrown.
    /// </summary>
    public bool Continuous { get; init; } = true;

    /// <summary>Which joint each part starts at, and which part is its parent.</summary>
    internal RagdollTree PartTree { get; }

    /// <summary>Each part's size, mass and joint axes, fitted to the mesh.</summary>
    internal RagdollFitting Fitting { get; }

    /// <summary>The pairs of parts that don't collide: each with its parent, and any two that overlap at rest.</summary>
    internal IReadOnlyList<(int A, int B)> Touching { get; }

    /// <summary>
    /// The ragdoll for a humanoid rig with the usual joint names, such as Mixamo's: pelvis, two spine parts, head, and
    /// upper arm, forearm, hand, thigh, shin and foot each side, with joints that bend as a body does. Names may have
    /// a prefix such as <c>mixamorig:</c>.
    /// </summary>
    public static RagdollBody Humanoid(Rig rig, float mass = 70f) => new(rig, HumanoidParts(), mass);

    /// <summary>
    /// The parts <see cref="Humanoid"/> uses, to make a humanoid with other settings, such as
    /// <c>new RagdollBody(rig, RagdollBody.HumanoidParts()) { Bounciness = 0.6f }</c>, or to change a part.
    /// </summary>
    public static List<RagdollPart> HumanoidParts()
    {
        List<RagdollPart> parts =
        [
            new("Hips", 0.14f) { Shape = RagdollShape.Box },
            new("Spine", 0.14f) { Shape = RagdollShape.Box, Swing = 25f, Twist = 20f },
            new("Spine2", 0.17f) { Shape = RagdollShape.Box, Swing = 25f, Twist = 20f },
            new("Head", 0.08f) { Shape = RagdollShape.Ball, Swing = 45f, Twist = 60f },
        ];
        foreach (string side in (string[])["Left", "Right"])
        {
            parts.Add(new($"{side}Arm", 0.028f) { Swing = 85f, Twist = 60f, Thickness = 0.85f });
            parts.Add(new($"{side}ForeArm", 0.016f) { Bend = RagdollBend.Forward, Swing = 140f, Thickness = 0.85f });
            parts.Add(new($"{side}Hand", 0.006f) { Shape = RagdollShape.Box, Swing = 60f, Twist = 30f });
            parts.Add(new($"{side}UpLeg", 0.1f) { Swing = 50f, Twist = 30f, Lean = 35f });
            parts.Add(new($"{side}Leg", 0.045f) { Bend = RagdollBend.Back, Swing = 140f });
            parts.Add(new($"{side}Foot", 0.015f) { Shape = RagdollShape.Box, Swing = 30f, Twist = 10f });
        }

        return parts;
    }

    /// <summary>The part a joint belongs to, by name, or -1 for one above the ragdoll.</summary>
    public int PartOf(string joint) => PartTree.PartOfJoint[RagdollTree.Find(Rig.Skeleton, joint)];

    /// <summary>A part's shape in its joint's space: centre, rotation that lays a capsule along its longest side, and size.</summary>
    internal (Vector3 Centre, Quaternion Turn, RagdollShape Shape, Vector3 HalfSize, float Radius) ShapeOf(int part)
    {
        var fit = Fitting.Fits[part];
        var half = fit.Size / 2f;
        float thickness = Parts[part].Thickness;
        switch (Parts[part].Shape)
        {
            case RagdollShape.Box:
                return (fit.Centre, Quaternion.Identity, RagdollShape.Box, half * new Vector3(Inset), 0f);
            case RagdollShape.Ball:
                return (fit.Centre, Quaternion.Identity, RagdollShape.Ball, half, Inset * thickness * (half.X + half.Y + half.Z) / 3f);
        }

        // A capsule runs along y, so it's turned to lie along the longest side.
        Quaternion turn;
        float length;
        float radius;
        if (half.Y >= half.X && half.Y >= half.Z)
        {
            (turn, length, radius) = (Quaternion.Identity, half.Y, (half.X + half.Z) / 2f);
        }
        else if (half.X >= half.Z)
        {
            (turn, length, radius) = (Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f), half.X, (half.Y + half.Z) / 2f);
        }
        else
        {
            (turn, length, radius) = (Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f), half.Z, (half.X + half.Y) / 2f);
        }

        radius *= Inset * thickness;
        return (fit.Centre, turn, RagdollShape.Capsule, new Vector3(0f, MathF.Max(length - radius, 0f), 0f), radius);
    }

    private static void Check(RagdollPart part)
    {
        if (!(part.Mass > 0f) || !float.IsFinite(part.Mass))
        {
            throw new ArgumentOutOfRangeException(nameof(part), part.Mass, $"Part {part.Joint} has mass {part.Mass}; it must be above 0.");
        }

        if (!(part.Swing >= 0f && part.Swing <= 180f) || !(part.Twist >= 0f && part.Twist <= 180f))
        {
            throw new ArgumentOutOfRangeException(nameof(part), part.Swing, $"Part {part.Joint} swings {part.Swing} and twists {part.Twist} degrees; each must be from 0 to 180.");
        }

        if (!(part.Thickness > 0f) || !float.IsFinite(part.Thickness) || !float.IsFinite(part.Lean))
        {
            throw new ArgumentOutOfRangeException(nameof(part), part.Thickness, $"Part {part.Joint} has thickness {part.Thickness} and lean {part.Lean}; the thickness must be above 0.");
        }
    }
}
