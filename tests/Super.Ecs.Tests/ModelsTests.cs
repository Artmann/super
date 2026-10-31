using System.Numerics;
using Rendition;

namespace Super.Ecs.Tests;

public class ModelsTests
{
    private const float Tolerance = 0.0001f;

    private readonly World _world = new();
    private readonly View _view = new();

    private Entity Spawn(Transform transform, bool smoothed = false)
    {
        var entity = _world.Create();
        _world.Add(entity, transform);
        _world.Add(entity, new Model(ModelRef.Cube));
        if (smoothed)
        {
            _world.Add(entity, new Smoothed());
        }

        return entity;
    }

    private ReadOnlySpan<ModelDraw> Draw(float blend)
    {
        _view.Begin(blend);
        _view.Models(_world);
        return _view.Drawn;
    }

    [Fact]
    public void Draws_each_entity_with_a_transform_and_model_keyed_by_its_id()
    {
        var first = Spawn(Transform.At(new Vector3(1f, 0f, 0f)));
        var second = Spawn(Transform.At(new Vector3(2f, 0f, 0f)));
        _world.Add(second, new Model(ModelRef.Cube, new Vector3(1f, 0f, 0f)));
        _world.Add(_world.Create(), Transform.Identity); // No model, so it isn't drawn.

        var drawn = Draw(0f);

        Assert.Equal(2, drawn.Length);
        Assert.Equal(first.Id, drawn[0].Key);
        Assert.Equal(1f, drawn[0].Transform.Translation.X);
        Assert.Null(drawn[0].Color);
        Assert.Equal(second.Id, drawn[1].Key);
        Assert.Equal(new Vector3(1f, 0f, 0f), drawn[1].Color);
    }

    [Fact]
    public void A_world_with_no_transforms_draws_nothing()
    {
        _world.Create();

        Assert.Equal(0, Draw(0f).Length);
    }

    [Fact]
    public void A_smoothed_entity_starts_where_it_is()
    {
        Spawn(Transform.At(new Vector3(4f, 0f, 0f)), smoothed: true);

        Assert.Equal(4f, Draw(0.5f)[0].Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void A_smoothed_entity_blends_from_where_it_was_before_the_tick()
    {
        var entity = Spawn(Transform.At(Vector3.Zero), smoothed: true);

        _world.RememberTransforms();
        _world.Get<Transform>(entity).Position = new Vector3(4f, 0f, 0f);

        Assert.Equal(1f, Draw(0.25f)[0].Transform.Translation.X, Tolerance);
        Assert.Equal(4f, Draw(1f)[0].Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void An_unsmoothed_entity_jumps()
    {
        var entity = Spawn(Transform.At(Vector3.Zero));

        _world.RememberTransforms();
        _world.Get<Transform>(entity).Position = new Vector3(4f, 0f, 0f);

        Assert.Equal(4f, Draw(0.25f)[0].Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void A_child_follows_its_parent()
    {
        var parent = Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        var child = Spawn(Transform.At(new Vector3(0f, 1f, 0f)));
        _world.Add(child, new Parent(parent));

        var drawn = Draw(0f);

        var childDraw = drawn[0].Key == child.Id ? drawn[0] : drawn[1];
        Assert.Equal(new Vector3(10f, 1f, 0f), childDraw.Transform.Translation);
    }

    [Fact]
    public void A_child_turns_and_grows_with_its_parent()
    {
        var parent = Spawn(new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), new Vector3(2f)));
        var child = Spawn(Transform.At(new Vector3(0f, 0f, 1f)));
        _world.Add(child, new Parent(parent));

        var drawn = Draw(0f);

        var childDraw = drawn[0].Key == child.Id ? drawn[0] : drawn[1];
        var at = childDraw.Transform.Translation;
        Assert.Equal(2f, at.X, Tolerance);
        Assert.Equal(0f, at.Y, Tolerance);
        Assert.Equal(0f, at.Z, Tolerance);
    }

    [Fact]
    public void A_child_follows_a_smoothed_parent_between_ticks()
    {
        var parent = Spawn(Transform.At(Vector3.Zero), smoothed: true);
        var child = Spawn(Transform.Identity);
        _world.Add(child, new Parent(parent));

        _world.RememberTransforms();
        _world.Get<Transform>(parent).Position = new Vector3(4f, 0f, 0f);

        var drawn = Draw(0.5f);
        var childDraw = drawn[0].Key == child.Id ? drawn[0] : drawn[1];
        Assert.Equal(2f, childDraw.Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void A_dead_parent_leaves_the_child_where_it_is()
    {
        var parent = Spawn(Transform.At(new Vector3(10f, 0f, 0f)));
        var child = Spawn(Transform.At(new Vector3(0f, 1f, 0f)));
        _world.Add(child, new Parent(parent));
        _world.Destroy(parent);

        var drawn = Draw(0f);

        Assert.Equal(new Vector3(0f, 1f, 0f), Assert.Single(drawn.ToArray()).Transform.Translation);
    }

    [Fact]
    public void A_swimming_entity_passes_its_motion_and_blends_its_phase()
    {
        var entity = Spawn(Transform.Identity, smoothed: true);
        _world.Add(entity, new Swim(0.1f, 1f, 0f));

        _world.RememberTransforms();
        _world.Get<Swim>(entity).Phase = 2f;

        Assert.Equal(new SwimMotion(0.1f, 1f, 1f), Draw(0.5f)[0].Swim);
    }

    [Fact]
    public void Keys_stay_stable_as_entities_come_and_go()
    {
        var first = Spawn(Transform.Identity);
        var second = Spawn(Transform.Identity);
        _world.Destroy(first);

        var drawn = Draw(0f);

        Assert.Equal(second.Id, Assert.Single(drawn.ToArray()).Key);
    }
}
