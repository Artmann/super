using System.Numerics;
using Rendition;

namespace Super.Tests;

public class GhostHiddenTests
{
    private readonly World _world = new();
    private readonly View _view = new();

    private Entity Spawn(Vector3 at) => _world.Spawn(Transform.At(at), new Model(ModelRef.Cube));

    private void Describe()
    {
        _view.Begin(0f);
        WorldFrame.Describe(_view, _world);
    }

    [Fact]
    public void A_ghost_entity_draws_as_a_ghost()
    {
        var ghost = new Ghost(new Vector3(0f, 1f, 0f), 0.5f);
        var preview = Spawn(Vector3.Zero);
        _world.Add(preview, ghost);

        Describe();

        var drawn = Assert.Single(_view.Drawn.ToArray());
        Assert.Equal(ghost, drawn.Ghost);
        Assert.Equal(preview.Id, drawn.Key);
    }

    [Fact]
    public void A_ghost_with_a_bad_opacity_throws_with_the_view_message()
    {
        _world.Add(Spawn(Vector3.Zero), new Ghost(Vector3.One, 0f));

        var error = Assert.Throws<ArgumentOutOfRangeException>(Describe);

        Assert.StartsWith("A ghost's opacity must be more than 0 and at most 1.", error.Message);
    }

    [Fact]
    public void A_hidden_entity_is_not_drawn()
    {
        var shown = Spawn(Vector3.Zero);
        _world.Add(Spawn(Vector3.One), new Hidden());

        Describe();

        Assert.Equal(shown.Id, Assert.Single(_view.Drawn.ToArray()).Key);
    }

    [Fact]
    public void Showing_a_hidden_entity_draws_it_again()
    {
        var entity = Spawn(Vector3.Zero);
        _world.Add(entity, new Hidden());
        Describe();
        Assert.Empty(_view.Drawn.ToArray());

        _world.Remove<Hidden>(entity);
        Describe();

        Assert.Single(_view.Drawn.ToArray());
    }

    [Fact]
    public void A_hidden_parent_hides_its_children_lights_and_emitters()
    {
        var parent = Spawn(Vector3.Zero);
        var child = _world.Spawn(Transform.At(Vector3.UnitX), new Parent(parent));
        var grandchild = _world.Spawn(Transform.At(Vector3.UnitX), new Parent(child), new Model(ModelRef.Cube));
        _world.Spawn(Transform.Identity, new Parent(grandchild), new PointLight(default, Vector3.One, 4f));
        _world.Add(parent, new Hidden());

        Describe();

        Assert.Empty(_view.Drawn.ToArray());
        Assert.Empty(_view.Lights.DrawnLights.ToArray());
    }

    [Fact]
    public void Hiding_the_parent_of_a_static_entity_draws_it_again()
    {
        var parent = _world.Spawn(Transform.Identity);
        _world.Spawn(Transform.Identity, new Parent(parent), new Model(ModelRef.Cube), new Static());
        Describe();
        Assert.Single(_view.Drawn.ToArray());

        _world.Add(parent, new Hidden());
        Describe();

        Assert.Empty(_view.Drawn.ToArray());
    }
}
