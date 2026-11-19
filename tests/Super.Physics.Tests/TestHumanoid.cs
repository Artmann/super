using System.Numerics;
using Rendition;

namespace Super.Physics.Tests;

/// <summary>
/// A person 1.8 m tall standing in a T-pose, built by hand as Mixamo builds one: metres, facing +z, its left at +x,
/// and each bone's +y pointing along it to the next joint. Every joint is boxed by what it would move.
/// </summary>
internal static class TestHumanoid
{
    internal enum Stance
    {
        Still,
    }

    internal enum Deed
    {
        None,
        GetUp,
    }

    private static readonly Lazy<Rig> Shared = new(Build);

    private static readonly Lazy<Rig> SharedCentimetres = new(() => InCentimetres(Shared.Value));

    public static Rig Rig => Shared.Value;

    /// <summary>
    /// The same person in centimetres under a root joint that scales by 0.01, as a rig exported from FBX often comes,
    /// its root transform leaving it as it is.
    /// </summary>
    public static Rig Centimetres => SharedCentimetres.Value;

    /// <summary>A ragdolled person standing at a place, its animator holding the T-pose.</summary>
    public static (Entity Entity, Animator Animator) Place(World world, Transform transform, Ragdoll ragdoll)
    {
        var rig = ragdoll.Body.Rig;
        var animator = new AnimationController<Stance, Deed>(rig).State(Stance.Still, rig.Clip("still"))
            .Action(Deed.GetUp, rig.Clip("lying"), fadeSeconds: 0f)
            .Start(Stance.Still);
        var entity = world.Create();
        world.Add(entity, transform);
        world.Add(entity, new Animated(animator));
        world.Add(entity, ragdoll);
        return (entity, animator);
    }

    /// <summary>A ragdoll of the person that simulates every part, with no muscle.</summary>
    public static Ragdoll Limp(RagdollBody body) => new(body) { Mode = RagdollMode.Simulated };

    private static Rig Build()
    {
        List<(string Name, int Parent, Vector3 At, Vector3 Along, float Length, float Width, float Depth)> joints = [];

        int Add(string name, int parent, Vector3 at, Vector3 along, float length, float width, float depth)
        {
            joints.Add((name, parent, at, Vector3.Normalize(along), length, width, depth));
            return joints.Count - 1;
        }

        var up = Vector3.UnitY;
        var down = -Vector3.UnitY;
        int hips = Add("Hips", -1, new(0f, 1f, 0f), up, 0.1f, 0.34f, 0.2f);
        int spine = Add("Spine", hips, new(0f, 1.1f, 0f), up, 0.1f, 0.3f, 0.18f);
        int spine1 = Add("Spine1", spine, new(0f, 1.2f, 0f), up, 0.1f, 0.32f, 0.18f);
        int spine2 = Add("Spine2", spine1, new(0f, 1.3f, 0f), up, 0.2f, 0.36f, 0.2f);
        int neck = Add("Neck", spine2, new(0f, 1.5f, 0f), up, 0f, 0f, 0f);
        Add("Head", neck, new(0f, 1.58f, 0f), up, 0.22f, 0.18f, 0.2f);
        foreach (var (side, x) in (ReadOnlySpan<(string, float)>)[("Left", 1f), ("Right", -1f)])
        {
            var outwards = new Vector3(x, 0f, 0f);
            int shoulder = Add($"{side}Shoulder", spine2, new(0.05f * x, 1.45f, 0f), outwards, 0.13f, 0.1f, 0.1f);
            int arm = Add($"{side}Arm", shoulder, new(0.18f * x, 1.45f, 0f), outwards, 0.28f, 0.1f, 0.1f);
            int forearm = Add($"{side}ForeArm", arm, new(0.46f * x, 1.45f, 0f), outwards, 0.26f, 0.08f, 0.08f);
            Add($"{side}Hand", forearm, new(0.72f * x, 1.45f, 0f), outwards, 0.18f, 0.09f, 0.04f);
            int upLeg = Add($"{side}UpLeg", hips, new(0.1f * x, 0.95f, 0f), down, 0.43f, 0.15f, 0.15f);
            int leg = Add($"{side}Leg", upLeg, new(0.1f * x, 0.52f, 0f), down, 0.42f, 0.11f, 0.11f);
            int foot = Add($"{side}Foot", leg, new(0.1f * x, 0.1f, 0f), new(0f, -0.4f, 1f), 0.15f, 0.09f, 0.08f);
            Add($"{side}ToeBase", foot, new(0.1f * x, 0.03f, 0.15f), Vector3.UnitZ, 0.06f, 0.09f, 0.04f);
        }

        int count = joints.Count;
        var globals = new Matrix4x4[count];
        var rest = new JointPose[count];
        var bounds = new JointBounds[count];
        for (int i = 0; i < count; i++)
        {
            var (_, parent, at, along, length, width, depth) = joints[i];
            globals[i] = Matrix4x4.CreateFromQuaternion(Towards(along)) * Matrix4x4.CreateTranslation(at);
            Matrix4x4.Invert(parent >= 0 ? globals[parent] : Matrix4x4.Identity, out var intoParent);
            rest[i] = JointPose.FromMatrix(globals[i] * intoParent);
            bounds[i] = length > 0f
                ? new JointBounds(new Vector3(-width / 2f, 0f, -depth / 2f), new Vector3(width / 2f, length, depth / 2f))
                : JointBounds.Empty;
        }

        // The hips' box reaches down to the tops of the thighs.
        bounds[hips] = new JointBounds(new Vector3(-0.17f, -0.1f, -0.1f), new Vector3(0.17f, 0.1f, 0.1f));
        var inverseBind = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            Matrix4x4.Invert(globals[i], out inverseBind[i]);
        }

        var skeleton = new Skeleton([.. joints.Select(joint => joint.Name)], [.. joints.Select(joint => joint.Parent)], rest, inverseBind, Matrix4x4.Identity, bounds);
        // Lying face up with its head towards -z, as a get-up clip starts, its hips off to one side of the entity.
        var lying = (JointPose[])rest.Clone();
        lying[hips] = JointPose.FromMatrix(Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(LyingHips));
        return new Rig(skeleton, [new AnimationClip("still", 1f, count, [.. rest, .. rest]), new AnimationClip("lying", 1f, count, [.. lying, .. lying])]);
    }

    /// <summary>A metre rig moved into centimetres under a new first joint, "Root", that scales it back down.</summary>
    private static Rig InCentimetres(Rig rig)
    {
        var skeleton = rig.Skeleton;
        int count = skeleton.Count + 1;
        var root = new JointPose(Vector3.Zero, Quaternion.Identity, new Vector3(0.01f));
        JointPose Larger(JointPose pose) => new(pose.Position * 100f, pose.Rotation, pose.Scale);
        JointPose[] Moved(JointPose[] poses, int joints)
        {
            var moved = new JointPose[poses.Length / joints * (joints + 1)];
            for (int frame = 0; frame < poses.Length / joints; frame++)
            {
                moved[frame * (joints + 1)] = root;
                for (int j = 0; j < joints; j++)
                {
                    moved[(frame * (joints + 1)) + j + 1] = Larger(poses[(frame * joints) + j]);
                }
            }

            return moved;
        }

        var rest = Moved(skeleton.Rest, skeleton.Count);
        int[] parents = [-1, .. skeleton.Parents.Select(parent => parent + 1)];
        var globals = new Matrix4x4[count];
        var inverseBind = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            globals[i] = parents[i] >= 0 ? rest[i].Matrix * globals[parents[i]] : rest[i].Matrix;
            Matrix4x4.Invert(globals[i], out inverseBind[i]);
        }

        JointBounds[] bounds =
        [
            JointBounds.Empty,
            .. skeleton.Bounds!.Select(box => box.IsEmpty ? box : new JointBounds(box.Min * 100f, box.Max * 100f)),
        ];
        var scaled = new Skeleton(["Root", .. skeleton.Names], parents, rest, inverseBind, skeleton.RootTransform, bounds);
        return new Rig(scaled, [.. rig.Clips.Select(clip => new AnimationClip(clip.Name, clip.FrameRate, count, Moved(clip.Frames, skeleton.Count)))]);
    }

    /// <summary>Where the "lying" clip puts the hips, in the entity's space.</summary>
    public static readonly Vector3 LyingHips = new(0.4f, 0.1f, 0.3f);

    /// <summary>The turn that points +y along a direction, without spinning round it more than it must.</summary>
    private static Quaternion Towards(Vector3 along)
    {
        float dot = Vector3.Dot(Vector3.UnitY, along);
        if (dot < -0.9999f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        }

        var axis = Vector3.Cross(Vector3.UnitY, along);
        return axis.LengthSquared() < 1e-12f
            ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(dot, -1f, 1f)));
    }
}
