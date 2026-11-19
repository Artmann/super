namespace Super.Tests;

public class HierarchyTests
{
    private enum Screen { Menu, House }

    private readonly World _world = new();

    private Entity Under(Entity parent) => _world.Spawn(Transform.Identity, new Parent(parent));

    [Fact]
    public void Destroying_a_parent_destroys_its_children_and_theirs()
    {
        var parent = _world.Spawn(Transform.Identity);
        var child = Under(parent);
        var grandchild = Under(child);

        _world.Destroy(parent);

        Assert.False(_world.IsAlive(parent));
        Assert.False(_world.IsAlive(child));
        Assert.False(_world.IsAlive(grandchild));
        Assert.Equal(0, _world.Count);
    }

    [Fact]
    public void Destroying_a_child_leaves_its_parent()
    {
        var parent = _world.Spawn(Transform.Identity);
        var child = Under(parent);

        _world.Destroy(child);

        Assert.True(_world.IsAlive(parent));
        Assert.False(_world.IsAlive(child));
    }

    [Fact]
    public void A_destroy_made_in_a_query_takes_the_children_when_the_query_ends()
    {
        var parent = _world.Spawn(Transform.Identity, new Health(1));
        var child = Under(parent);
        bool aliveInside = false;

        foreach (var row in _world.Query<Health>())
        {
            _world.Destroy(row.Entity);
            aliveInside = _world.IsAlive(child);
        }

        Assert.True(aliveInside);
        Assert.False(_world.IsAlive(child));
    }

    [Fact]
    public void A_child_moved_to_another_parent_stays()
    {
        var old = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var child = Under(old);

        _world.Get<Parent>(child) = new Parent(other);
        _world.Destroy(old);

        Assert.True(_world.IsAlive(child));
    }

    [Fact]
    public void A_child_moved_by_reference_goes_with_its_new_parent()
    {
        var old = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var child = Under(old);

        _world.Get<Parent>(child) = new Parent(other);
        _world.Destroy(other);

        Assert.False(_world.IsAlive(child));
        Assert.True(_world.IsAlive(old));
    }

    [Fact]
    public void A_child_moved_in_a_query_goes_with_its_new_parent()
    {
        var old = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var child = Under(old);

        foreach (var row in _world.Query<Parent>())
        {
            row.First = new Parent(other);
        }

        _world.Destroy(old);
        Assert.True(_world.IsAlive(child));
        _world.Destroy(other);
        Assert.False(_world.IsAlive(child));
    }

    [Fact]
    public void A_child_moved_with_Add_goes_with_its_new_parent()
    {
        var old = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var child = Under(old);

        _world.Add(child, new Parent(other));
        _world.Destroy(old);
        Assert.True(_world.IsAlive(child));
        _world.Destroy(other);
        Assert.False(_world.IsAlive(child));
    }

    [Fact]
    public void A_child_whose_parent_is_removed_stays()
    {
        var parent = _world.Spawn(Transform.Identity);
        var child = Under(parent);

        _world.Remove<Parent>(child);
        _world.Destroy(parent);

        Assert.True(_world.IsAlive(child));
    }

    [Fact]
    public void A_child_spawned_in_a_query_goes_with_its_parent()
    {
        var parent = _world.Spawn(Transform.Identity, new Health(1));
        Entity child = default;

        foreach (var row in _world.Query<Health>())
        {
            child = Under(row.Entity);
        }

        _world.Destroy(parent);

        Assert.False(_world.IsAlive(child));
    }

    [Fact]
    public void Siblings_left_after_some_go_are_still_destroyed_with_their_parent()
    {
        var parent = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var first = Under(parent);
        var second = Under(parent);
        var third = Under(parent);
        var fourth = Under(parent);

        // The last child takes the first's place, so moving it next finds it there.
        _world.Destroy(first);
        _world.Add(fourth, new Parent(other));
        _world.Destroy(parent);

        Assert.False(_world.IsAlive(second));
        Assert.False(_world.IsAlive(third));
        Assert.True(_world.IsAlive(fourth));
    }

    [Fact]
    public void A_child_a_handler_moves_while_its_siblings_go_stays()
    {
        var parent = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var first = _world.Spawn(new Parent(parent), new Health(1));
        var second = Under(parent);
        _world.OnRemoved<Health>(_ => _world.Add(second, new Parent(other)));

        _world.Destroy(parent);

        Assert.False(_world.IsAlive(first));
        Assert.True(_world.IsAlive(second));
    }

    [Fact]
    public void A_cycle_of_parents_is_destroyed_without_looping()
    {
        var a = _world.Spawn(Transform.Identity);
        var b = Under(a);
        _world.Add(a, new Parent(b));

        _world.Destroy(a);

        Assert.False(_world.IsAlive(a));
        Assert.False(_world.IsAlive(b));
    }

    [Fact]
    public void Removed_handlers_run_for_children()
    {
        var removed = new List<int>();
        _world.OnRemoved<Transform>(entity => removed.Add(entity.Id));
        var parent = _world.Spawn(Transform.Identity);
        var child = Under(parent);

        _world.Destroy(parent);

        Assert.Equal(new[] { parent.Id, child.Id }.Order(), removed.Order());
    }

    [Fact]
    public void Children_lists_only_direct_children()
    {
        var parent = _world.Spawn(Transform.Identity);
        var first = Under(parent);
        var second = _world.Spawn(new Parent(parent));
        var grandchild = Under(first);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), _world.Children(parent).Select(child => child.Id).Order());
        Assert.Equal([grandchild], _world.Children(first));
        Assert.Empty(_world.Children(grandchild));
    }

    [Fact]
    public void Children_follow_a_parent_changed_by_reference()
    {
        var old = _world.Spawn(Transform.Identity);
        var other = _world.Spawn(Transform.Identity);
        var child = Under(old);
        Assert.Equal([child], _world.Children(old));

        _world.Get<Parent>(child) = new Parent(other);

        Assert.Empty(_world.Children(old));
        Assert.Equal([child], _world.Children(other));
    }

    [Fact]
    public void Leaving_a_state_takes_the_children_of_scoped_entities()
    {
        var house = _world.Spawn(Transform.Identity, new ScopedTo<Screen>(Screen.House));
        var door = Under(house);
        var menu = _world.Spawn(Transform.Identity, new ScopedTo<Screen>(Screen.Menu));

        ScopedEntities.DestroyScopedTo(_world, Screen.House);

        Assert.False(_world.IsAlive(door));
        Assert.True(_world.IsAlive(menu));
    }

    private readonly record struct Health(int Value);
}
