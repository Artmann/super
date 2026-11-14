using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class RagdollTests
{
    private const float Tick = 1f / 60f;

    private static readonly RagdollBody Body = RagdollBody.Humanoid(TestHumanoid.Rig);

    private static (World World, Physics Physics) Make(bool floor = true)
    {
        var world = new World();
        if (floor)
        {
            var ground = world.Create();
            world.Add(ground, Transform.At(new Vector3(0f, -0.5f, 0f)));
            world.Add(ground, Collider.Box(new Vector3(40f, 1f, 40f)));
            world.Add(ground, Super.Physics.Body.Static);
        }

        return (world, new Physics(world));
    }

    /// <summary>Ticks as the game loop does: physics, then the animators.</summary>
    private static void Run(Physics physics, Animator animator, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            physics.Step(Tick);
            animator.Advance(Tick);
        }
    }

    /// <summary>Where a joint is in the world, as the character is drawn.</summary>
    private static Vector3 JointAt(World world, Entity entity, Animator animator, string joint) =>
        (animator.Joint(joint) * world.Get<Transform>(entity).Matrix).Translation;

    private static List<RagdollShapeView> Shapes(Physics physics, Entity entity)
    {
        List<RagdollShapeView> shapes = [];
        physics.RagdollShapes(entity, shapes);
        return shapes;
    }

    /// <summary>The way a part's bone points, from its joint towards the next.</summary>
    private static Vector3 Bone(Physics physics, Entity entity, string joint)
    {
        var shape = Shapes(physics, entity).Single(shape => shape.Joint == joint);
        return Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, shape.Transform));
    }

    private static float Degrees(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_limp_body_falls_and_lies_on_the_floor()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 2f, 0f)), TestHumanoid.Limp(Body));

        Run(physics, animator, 240);

        var hips = JointAt(world, person, animator, "Hips");
        var head = JointAt(world, person, animator, "Head");
        Assert.True(hips.Y is > 0f and < 0.35f, $"the hips are {hips.Y} m up");
        Assert.True(head.Y is > 0f and < 0.35f, $"the head is {head.Y} m up");
        Assert.True(physics.Velocity(person).Length() < 0.2f, $"it still moves at {physics.Velocity(person)}");
    }

    [Theory]
    [InlineData(RagdollMode.Off)]
    [InlineData(RagdollMode.Kinematic)]
    public void Switching_on_keeps_the_speed_the_animation_moved_at(RagdollMode from)
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), new Ragdoll(Body) { Mode = from });

        for (int i = 0; i < 5; i++)
        {
            world.Get<Transform>(person).Position += new Vector3(4f * Tick, 0f, 0f);
            Run(physics, animator, 1);
        }

        world.Get<Transform>(person).Position += new Vector3(4f * Tick, 0f, 0f);
        world.Get<Ragdoll>(person) = world.Get<Ragdoll>(person) with { Mode = RagdollMode.Simulated };
        Run(physics, animator, 1);

        var velocity = physics.Velocity(person);
        Assert.Equal(4f, velocity.X, 0.1f);
        Assert.Equal(-9.81f * Tick, velocity.Y, 0.05f);
    }

    [Fact]
    public void The_entity_follows_the_pelvis()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), TestHumanoid.Limp(Body));

        physics.SetVelocity(person, new Vector3(3f, 0f, 0f));
        Run(physics, animator, 30);

        // Half a second at 3 m/s, while the hips are drawn where their body is.
        var position = world.Get<Transform>(person).Position;
        Assert.Equal(1.5f, position.X, 0.05f);
        var hipsBody = Shapes(physics, person).Single(shape => shape.Joint == "Hips").Transform.Translation;
        var hipsDrawn = JointAt(world, person, animator, "Hips");
        Assert.True(Vector3.Distance(hipsDrawn, hipsBody) < 0.15f, $"the hips are drawn at {hipsDrawn} but their body is at {hipsBody}");
    }

    [Fact]
    public void A_push_through_the_middle_moves_the_whole_body_by_its_mass()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var (person, _) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), TestHumanoid.Limp(Body));

        physics.Push(person, new Vector3(0f, 0f, 140f));

        // 140 newton-seconds on 70 kilograms.
        Assert.Equal(2f, physics.Velocity(person).Z, 0.01f);
    }

    [Fact]
    public void A_push_on_one_part_moves_that_part()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), TestHumanoid.Limp(Body));
        physics.Sync();
        var before = Bone(physics, person, "LeftForeArm");

        physics.PushPart(person, "LeftHand", new Vector3(0f, 0f, 2f));
        Run(physics, animator, 10);

        // The hand swings the forearm forward; the rest of the body hardly moves.
        var after = Bone(physics, person, "LeftForeArm");
        Assert.True(after.Z > before.Z + 0.2f, $"the forearm points {after}");
        Assert.True(MathF.Abs(physics.Velocity(person).Z) < 0.2f, $"the body moves at {physics.Velocity(person)}");
    }

    [Fact]
    public void A_part_that_joins_the_simulation_starts_where_it_is()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var ragdoll = TestHumanoid.Limp(Body) with { Mask = TestHumanoid.Rig.Mask("LeftArm") };
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), ragdoll);
        Run(physics, animator, 10);

        world.Get<Ragdoll>(person) = ragdoll with { Mask = TestHumanoid.Rig.Mask("LeftArm", "RightArm") };
        Run(physics, animator, 1);

        // Right after the step the pose is still the last step's, which the right arm has no part in.
        var skeleton = TestHumanoid.Rig.Skeleton;
        var start = new Rendition.JointPose[skeleton.Count];
        var end = new Rendition.JointPose[skeleton.Count];
        animator.Pose.Evaluate(0f, start, new Rendition.JointPose[skeleton.Count]);
        animator.Pose.Evaluate(Tick, end, new Rendition.JointPose[skeleton.Count]);
        int arm = Array.IndexOf(skeleton.Names, "RightArm");
        Assert.True(arm >= 0, "the rig has no RightArm");
        var turn = start[arm].Rotation;
        Assert.True(float.IsFinite(turn.X) && float.IsFinite(turn.W) && MathF.Abs(turn.Length() - 1f) < 1e-3f, $"the right arm starts turned by {turn}");
        Assert.True(MathF.Abs(Quaternion.Dot(turn, end[arm].Rotation)) > 0.999f, $"the right arm jumps from {turn} to {end[arm].Rotation}");
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData("LeftUpLeg", "LeftLeg", -1f, true)]
    [InlineData("LeftUpLeg", "LeftLeg", 1f, false)]
    [InlineData("LeftArm", "LeftForeArm", 1f, true)]
    [InlineData("LeftArm", "LeftForeArm", -1f, false)]
    public void Knees_bend_back_and_elbows_forward(string upper, string lower, float way, bool bends)
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;

        // The body and the upper part stay where the animation holds them; only the lower part swings free.
        var ragdoll = TestHumanoid.Limp(Body) with { Mask = TestHumanoid.Rig.Mask(lower) };
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), ragdoll);
        physics.Sync();

        string end = lower == "LeftLeg" ? "LeftFoot" : "LeftHand";
        for (int i = 0; i < 60; i++)
        {
            // Hold the upper part still and keep pushing the end of the limb one way, front or back.
            physics.PushPart(person, end, new Vector3(0f, 0f, 1.5f * way));
            Run(physics, animator, 1);
        }

        float angle = Degrees(Bone(physics, person, upper), Bone(physics, person, lower));
        if (bends)
        {
            Assert.True(angle > 45f, $"the joint bends {angle} degrees");
        }
        else
        {
            Assert.True(angle < 15f, $"the joint bends {angle} degrees the wrong way");
        }
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData("LeftUpLeg", "LeftFoot", "RightUpLeg")]
    [InlineData("LeftLeg", "LeftFoot", "RightLeg")]
    public void A_leg_pushed_into_the_other_stops_against_it(string swung, string pushed, string held)
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;

        // The other leg stays where the animation holds it; this one is pushed across into it, over and over.
        var ragdoll = TestHumanoid.Limp(Body) with { Mask = TestHumanoid.Rig.Mask(swung) };
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), ragdoll);
        physics.Sync();
        float closest = float.MaxValue;
        float radius = 0f;
        for (int i = 0; i < 60; i++)
        {
            physics.PushPart(person, pushed, new Vector3(-6f, 0f, 0f));
            Run(physics, animator, 1);
            var shapes = Shapes(physics, person);
            var still = shapes.Single(shape => shape.Joint == held);
            var moving = shapes.Single(shape => shape.Joint == HeldsTwin(held));
            closest = MathF.Min(closest, Gap(still, moving));
            radius = still.Radius;
        }

        // Pushed this hard, colliding capsules sink in a few centimetres. Passing through takes one past the other's
        // surface by more than its own radius, towards the other's middle.
        Assert.True(closest > -radius, $"the {HeldsTwin(held)} capsule sank {-closest} m into the {held} one, deeper than its {radius} m radius");
    }

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.8f, true)]
    public void Parts_that_overlap_at_rest_dont_collide(float thighThickness, bool keptApart)
    {
        // The thighs' joints are 0.2 m apart and each capsule is 0.064 m round, so 1.8 times as thick they overlap.
        var parts = Body.Parts.Select(part => part.Joint.EndsWith("UpLeg", StringComparison.Ordinal) ? part with { Thickness = thighThickness } : part).ToList();
        var body = new RagdollBody(TestHumanoid.Rig, parts);
        int left = body.PartOf("LeftUpLeg");
        int right = body.PartOf("RightUpLeg");

        Assert.Equal(keptApart, body.Touching.Contains((Math.Max(left, right), Math.Min(left, right))));
        Assert.Contains((left, body.PartOf("Hips")), body.Touching);
    }

    private static string HeldsTwin(string held) => held.Replace("Right", "Left", StringComparison.Ordinal);

    /// <summary>How far apart two capsules' surfaces are, in metres, or how deep they overlap when negative.</summary>
    private static float Gap(RagdollShapeView a, RagdollShapeView b)
    {
        Assert.Equal(RagdollShape.Capsule, a.Shape);
        Assert.Equal(RagdollShape.Capsule, b.Shape);

        // Close enough for a test: the nearest of many points along each capsule's middle.
        float nearest = float.MaxValue;
        for (int i = 0; i <= 50; i++)
        {
            var p = Vector3.Transform(new Vector3(0f, a.HalfSize.Y * ((i / 25f) - 1f), 0f), a.Transform);
            for (int j = 0; j <= 50; j++)
            {
                var q = Vector3.Transform(new Vector3(0f, b.HalfSize.Y * ((j / 25f) - 1f), 0f), b.Transform);
                nearest = MathF.Min(nearest, Vector3.Distance(p, q));
            }
        }

        return nearest - a.Radius - b.Radius;
    }

    [Fact]
    public void Parts_outside_the_mask_follow_the_animation()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var ragdoll = TestHumanoid.Limp(Body) with { Mask = TestHumanoid.Rig.Mask("LeftArm") };
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), ragdoll);

        Run(physics, animator, 60);

        Assert.Equal(new Vector3(0f, 10f, 0f), world.Get<Transform>(person).Position);
        var simulated = Shapes(physics, person).Where(shape => shape.Simulated).Select(shape => shape.Joint).Order();
        Assert.Equal(["LeftArm", "LeftForeArm", "LeftHand"], simulated);

        // The arm hangs; the other stays out as the animation holds it.
        Assert.True(JointAt(world, person, animator, "LeftHand").Y < 11f, "the left hand didn't fall");
        Assert.Equal(11.45f, JointAt(world, person, animator, "RightHand").Y, 0.01f);
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData(1f, true)]
    [InlineData(0f, false)]
    public void Strength_holds_the_animation_pose_against_gravity(float strength, bool held)
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var ragdoll = TestHumanoid.Limp(Body) with { Mask = TestHumanoid.Rig.Mask("LeftArm"), Strength = strength };
        var (person, animator) = TestHumanoid.Place(world, Transform.At(new Vector3(0f, 10f, 0f)), ragdoll);

        Run(physics, animator, 120);

        // The T-pose holds the hand out level with the shoulder, 11.45 m up.
        float drop = 11.45f - JointAt(world, person, animator, "LeftHand").Y;
        if (held)
        {
            Assert.True(drop < 0.15f, $"the hand dropped {drop} m");
        }
        else
        {
            Assert.True(drop > 0.4f, $"the hand dropped only {drop} m");
        }
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData(true)]
    [InlineData(false)]
    public void A_body_lying_down_knows_whether_it_faces_up(bool faceUp)
    {
        var (world, physics) = Make();
        using var _ = physics;

        // Lying along x, front to the sky or the floor.
        var lying = Transform.Facing(new Vector3(0f, 0.3f, 0f), faceUp ? Vector3.UnitY : -Vector3.UnitY, Vector3.UnitX, Vector3.One);
        var (person, animator) = TestHumanoid.Place(world, lying, TestHumanoid.Limp(Body));

        Run(physics, animator, 120);

        Assert.Equal(faceUp, physics.IsFaceUp(person));
    }

    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData(true)]
    [InlineData(false)]
    public void Turning_off_stands_the_entity_under_the_pelvis_facing_the_way_it_gets_up(bool faceUp)
    {
        var (world, physics) = Make();
        using var _ = physics;
        var lying = Transform.Facing(new Vector3(2f, 0.3f, 1f), faceUp ? Vector3.UnitY : -Vector3.UnitY, Vector3.UnitX, Vector3.One);
        var (person, animator) = TestHumanoid.Place(world, lying, TestHumanoid.Limp(Body));
        Run(physics, animator, 120);
        var hips = JointAt(world, person, animator, "Hips");

        world.Get<Ragdoll>(person) = world.Get<Ragdoll>(person) with { Mode = RagdollMode.Off };
        Run(physics, animator, 1);

        var transform = world.Get<Transform>(person);
        Assert.Equal(0f, transform.Position.Y, 0.01f);
        Assert.Equal(hips.X, transform.Position.X, 0.01f);
        Assert.Equal(hips.Z, transform.Position.Z, 0.01f);

        // The head lies towards +x. Face up, a body gets up facing its feet; face down, it pushes up towards its head.
        var forward = Vector3.Transform(Vector3.UnitZ, transform.Rotation);
        Assert.Equal(faceUp ? -1f : 1f, forward.X, 0.15f);

        // The pose it lay in is still drawn there, and blends away into the clips.
        Assert.True(Vector3.Distance(hips, JointAt(world, person, animator, "Hips")) < 0.01f, "the hips jumped when the ragdoll turned off");
        Assert.True(animator.Pose.IsPhysical);
        Run(physics, animator, 30);
        Assert.False(animator.Pose.IsPhysical);
        Assert.Empty(Shapes(physics, person));
    }

    [Fact]
    public void Turning_off_while_a_get_up_starts_lays_the_clip_s_pelvis_where_the_ragdoll_s_lies()
    {
        var (world, physics) = Make();
        using var _ = physics;
        var lying = Transform.Facing(new Vector3(2f, 0.3f, 1f), Vector3.UnitY, Vector3.UnitX, Vector3.One);
        var (person, animator) = TestHumanoid.Place(world, lying, TestHumanoid.Limp(Body));
        Run(physics, animator, 120);
        var hips = JointAt(world, person, animator, "Hips");

        world.Get<Ragdoll>(person) = world.Get<Ragdoll>(person) with { Mode = RagdollMode.Off };
        animator.Play(TestHumanoid.Deed.GetUp);
        Run(physics, animator, 1);

        // The clip lies with its head towards -z and the ragdoll's head lies towards +x, so the entity faces -x.
        var forward = Vector3.Transform(Vector3.UnitZ, world.Get<Transform>(person).Rotation);
        Assert.Equal(-1f, forward.X, 0.15f);

        // Once the held pose has blended away, the clip's hips are drawn where the ragdoll's lay, not 0.5 m off.
        Run(physics, animator, 30);
        Assert.False(animator.Pose.IsPhysical);
        var drawn = JointAt(world, person, animator, "Hips");
        Assert.True(new Vector2(drawn.X - hips.X, drawn.Z - hips.Z).Length() < 0.1f, $"the clip's hips are at {drawn}, the ragdoll's were at {hips}");
    }

    [Fact]
    public void Removing_the_ragdoll_takes_its_bodies_away()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var (person, animator) = TestHumanoid.Place(world, Transform.At(Vector3.Zero), new Ragdoll(Body) { Mode = RagdollMode.Kinematic });
        var down = new Super.Ray(new Vector3(0f, 5f, 0f), -Vector3.UnitY);
        physics.Sync();
        Assert.Equal(person, physics.Raycast(down)?.Entity);

        world.Remove<Ragdoll>(person);
        physics.Sync();

        Assert.Null(physics.Raycast(down));
        Assert.False(animator.Pose.IsPhysical);
    }

    [Fact]
    public void A_ragdoll_needs_an_animator()
    {
        var (world, physics) = Make(floor: false);
        using var _ = physics;
        var person = world.Create();
        world.Add(person, Transform.Identity);
        world.Add(person, TestHumanoid.Limp(Body));

        var problem = Assert.Throws<InvalidOperationException>(physics.Sync);

        Assert.Contains("isn't Animated", problem.Message);
    }

    [Fact]
    public void A_rig_without_bounds_cant_be_fitted()
    {
        var skeleton = TestHumanoid.Rig.Skeleton;
        var bare = new Rig(new Rendition.Skeleton(skeleton.Names, skeleton.Parents, skeleton.Rest, skeleton.InverseBind, skeleton.RootTransform), [.. TestHumanoid.Rig.Clips]);

        var problem = Assert.Throws<ArgumentException>(() => RagdollBody.Humanoid(bare));

        Assert.Contains("no bounds", problem.Message);
    }
}
