using System.Numerics;
using Rendition;

namespace Super.Tests;

public class StaticCacheTests
{
    private const float Tolerance = 0.0001f;

    private readonly World _world = new();
    private readonly View _view = new();

    private Entity SpawnStatic(Vector3 at) => _world.Spawn(Transform.At(at), new Model(ModelRef.Cube), new Static());

    private ModelDraw[] Draw(float blend = 0f)
    {
        _view.Begin(blend);
        WorldFrame.Describe(_view, _world);
        return _view.Drawn.ToArray();
    }

    [Fact]
    public void A_static_entity_moved_in_place_stays_until_Changed()
    {
        var wall = SpawnStatic(Vector3.Zero);
        Draw();

        _world.Get<Transform>(wall).Position = new Vector3(5f, 0f, 0f);
        Assert.Equal(0f, Draw().Single().Transform.Translation.X, Tolerance);

        _world.Changed(wall);
        Assert.Equal(5f, Draw().Single().Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void Adding_a_new_value_to_a_static_entity_counts_as_a_change()
    {
        var wall = SpawnStatic(Vector3.Zero);
        Draw();

        _world.Add(wall, Transform.At(new Vector3(3f, 0f, 0f)));

        Assert.Equal(3f, Draw().Single().Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void Changed_on_an_entity_that_isnt_static_does_nothing_to_the_static_ones()
    {
        var wall = SpawnStatic(Vector3.Zero);
        var crate = _world.Spawn(Transform.At(Vector3.Zero), new Model(ModelRef.Cube));
        Draw();

        _world.Get<Transform>(wall).Position = new Vector3(5f, 0f, 0f);
        _world.Changed(crate);

        Assert.Equal(0f, Draw().Single(draw => draw.Key == wall.Id).Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void A_static_entity_spawned_later_appears()
    {
        SpawnStatic(Vector3.Zero);
        Draw();

        SpawnStatic(new Vector3(2f, 0f, 0f));

        Assert.Equal(2, Draw().Length);
    }

    [Fact]
    public void A_destroyed_static_entity_goes()
    {
        var wall = SpawnStatic(Vector3.Zero);
        Draw();

        _world.Destroy(wall);

        Assert.Empty(Draw());
    }

    [Fact]
    public void Removing_Static_lets_it_move()
    {
        var wall = SpawnStatic(Vector3.Zero);
        Draw();

        _world.Remove<Static>(wall);
        LastTicks.Remember(_world);
        _world.Get<Transform>(wall).Position = new Vector3(4f, 0f, 0f);

        var drawn = Draw(0.25f).Single();
        Assert.False(drawn.Static);
        Assert.Equal(1f, drawn.Transform.Translation.X, Tolerance);
    }

    [Fact]
    public void Moving_entities_still_move_beside_cached_static_ones()
    {
        SpawnStatic(Vector3.Zero);
        var crate = _world.Spawn(Transform.At(Vector3.Zero), new Model(ModelRef.Cube));
        Draw();

        _world.Get<Transform>(crate).Position = new Vector3(2f, 0f, 0f);

        Assert.Equal(2f, Draw().Single(draw => draw.Key == crate.Id).Transform.Translation.X, Tolerance);
    }
}
