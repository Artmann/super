using System.Numerics;
using Super.Tests.Animation;

namespace Super.Tests;

public class BoneParentTests
{
    private enum Stance { Move }

    private readonly World _world = new();
    private readonly View _view = new();

    public BoneParentTests()
    {
        _view.World = _world;
    }

    // The rig's Move clip slides the root 2 units a second along x, with the child joint one unit above it.
    private Entity Fighter(Vector3 at)
    {
        var rig = TestRig.Rig();
        var animator = new AnimationController<Stance, Stance>(rig).State(Stance.Move, rig.Clip("move")).Start(Stance.Move);
        return _world.Spawn(Transform.At(at), new Animated(animator), new Model(ModelRef.Cube));
    }

    private Entity On(Entity parent, string bone, Vector3 offset) =>
        _world.Spawn(Transform.At(offset), new Parent(parent, Bone: bone));

    [Fact]
    public void A_child_on_a_bone_follows_the_joint()
    {
        var fighter = Fighter(new Vector3(10f, 0f, 0f));
        var sword = On(fighter, TestRig.Child, new Vector3(0f, 0f, 1f));
        _view.Begin(0f);

        Assert.Equal(new Vector3(10f, 1f, 1f), _view.TransformOf(sword).Translation);
    }

    [Fact]
    public void A_child_on_a_bone_follows_the_pose_between_ticks()
    {
        var fighter = Fighter(Vector3.Zero);
        var sword = On(fighter, TestRig.Root, Vector3.Zero);

        // Half way to the next half-second tick is a quarter second past it, where the root has slid 0.5.
        _view.Begin(0.5f, tick: 1, tickSeconds: 0.5f);

        Assert.Equal(0.5f, _view.TransformOf(sword).Translation.X, 1e-5f);
    }

    [Fact]
    public void Physics_places_a_child_on_a_bone_as_of_the_tick()
    {
        var fighter = Fighter(Vector3.Zero);
        var sword = On(fighter, TestRig.Root, Vector3.Zero);

        Assert.Equal(Vector3.Zero, WorldFrame.WorldMatrix(_world, sword, 1f).Translation);
    }

    [Fact]
    public void A_bone_the_rig_lacks_throws_naming_it_and_the_entity()
    {
        var fighter = Fighter(Vector3.Zero);
        var sword = On(fighter, "hand_l", Vector3.Zero);
        _view.Begin(0f);

        var error = Assert.Throws<ArgumentException>(() => _view.TransformOf(sword));

        Assert.StartsWith(
            $"Entity {sword.Id}'s Parent names the bone hand_l, but entity {fighter.Id}'s rig has no joint called that. It has: root, child.",
            error.Message);
    }

    [Fact]
    public void A_bone_on_a_parent_that_isnt_animated_throws()
    {
        var crate = _world.Spawn(Transform.Identity);
        var lid = On(crate, "lid", Vector3.Zero);
        _view.Begin(0f);

        var error = Assert.Throws<InvalidOperationException>(() => _view.TransformOf(lid));

        Assert.Equal(
            $"Entity {lid.Id}'s Parent names the bone lid, but its parent, entity {crate.Id}, isn't Animated, so it has no bones.",
            error.Message);
    }

    [Fact]
    public void A_camera_on_a_bone_follows_the_pose_between_ticks()
    {
        var fighter = Fighter(Vector3.Zero);
        _world.Spawn(new Camera(Vector3.Zero, Vector3.UnitZ, 0.5f), new Parent(fighter, Bone: TestRig.Root));

        var camera = WorldCameras.FrameCamera(_world, blend: 0.5f, poseAhead: 0.25f);

        Assert.Equal(0.5f, camera!.Value.Position.X, 1e-5f);
    }

    [Fact]
    public void A_model_on_a_bone_is_drawn_at_the_joint()
    {
        var fighter = Fighter(Vector3.Zero);
        var sword = On(fighter, TestRig.Child, Vector3.Zero);
        _world.Add(sword, new Model(ModelRef.Cube));
        _view.Begin(0f);

        WorldFrame.Describe(_view, _world);

        Assert.Equal(new Vector3(0f, 1f, 0f), _view.Drawn.ToArray().Single(draw => draw.Key == sword.Id).Transform.Translation);
    }
}
