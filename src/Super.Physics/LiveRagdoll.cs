using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>One entity's ragdoll: its bodies while on, where the animation has its parts, and the pose it hands back.</summary>
internal sealed class LiveRagdoll
{
    private readonly PhysicsSystem _system;

    public LiveRagdoll(PhysicsSystem system, RagdollBody body, Animator animator, Dictionary<uint, Entity> owners)
    {
        _system = system;
        Body = body;
        Animator = animator;
        Parts = new RagdollParts(system, body, owners);
        Targets = new RagdollTargets(body);
        Pose = new RagdollHeldPose(body);
    }

    public RagdollBody Body { get; }

    public Animator Animator { get; }

    public RagdollParts Parts { get; }

    public RagdollTargets Targets { get; }

    public RagdollHeldPose Pose { get; }

    public RagdollMode Mode { get; set; }

    /// <summary>The muscles' strength, from 0 to 1.</summary>
    public float Strength { get; set; }

    /// <summary>The size of the parts against the rig's: its root's scale times the entity's.</summary>
    public float Scale { get; set; }

    /// <summary>Whether the muscles pull this step.</summary>
    public bool Powered => Parts.Active && Strength > 0f && Mode == RagdollMode.Simulated;

    /// <summary>Lists the shapes of the parts where they are now.</summary>
    public void Shapes(List<RagdollShapeView> shapes)
    {
        var bodies = _system.BodyInterface;
        for (int i = 0; i < Parts.Ids.Length; i++)
        {
            var (centre, turn, shape, half, radius) = Body.ShapeOf(i);
            var place = Matrix4x4.CreateFromQuaternion(turn)
                * Matrix4x4.CreateTranslation(centre * Scale)
                * Matrix4x4.CreateFromQuaternion(bodies.GetRotation(in Parts.Ids[i]))
                * Matrix4x4.CreateTranslation(bodies.GetPosition(in Parts.Ids[i]));
            shapes.Add(new RagdollShapeView(Body.Parts[i].Joint, shape, place, half * Scale, radius * Scale, Parts.Dynamic[i]));
        }
    }

    /// <summary>
    /// Makes each part dynamic or kinematic as the mode and mask now ask, and moves the kinematic ones to where the
    /// animation has them, over <paramref name="seconds"/>, or at once for 0.
    /// </summary>
    public void Follow(Ragdoll ragdoll, float seconds)
    {
        var bodies = _system.BodyInterface;
        for (int i = 0; i < Parts.Ids.Length; i++)
        {
            bool dynamic = ragdoll.Simulates(i);
            if (dynamic != Parts.Dynamic[i])
            {
                bodies.SetMotionType(in Parts.Ids[i], dynamic ? MotionType.Dynamic : MotionType.Kinematic, Activation.Activate);
                Parts.Dynamic[i] = dynamic;
            }

            if (dynamic)
            {
                continue;
            }

            if (seconds > 0f)
            {
                bodies.MoveKinematic(in Parts.Ids[i], in Targets.Positions[i], in Targets.Rotations[i], seconds);
            }
            else
            {
                bodies.SetPositionAndRotation(in Parts.Ids[i], in Targets.Positions[i], in Targets.Rotations[i], Activation.DontActivate);
            }
        }

        if (!Parts.Simulating)
        {
            Pose.Clear(Animator);
        }
    }

    /// <summary>
    /// After a step, moves the entity with the pelvis and hands the parts' places to the animator, which shows them
    /// between this tick and the last.
    /// </summary>
    public void Write(ref Transform transform, float seconds)
    {
        Parts.Read();
        if (Parts.Dynamic[0])
        {
            transform.Position += Parts.Positions[0] - Targets.Positions[0];
        }

        Pose.Step(Body, Parts, Scale, transform);
        Pose.Show(Animator, seconds);
    }
}
