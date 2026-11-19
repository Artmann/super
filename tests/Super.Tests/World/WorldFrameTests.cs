using System.Numerics;
using Rendition;
using Super.Testing;

namespace Super.Tests;

public class WorldFrameTests
{
    private sealed class Cube(World world) : IGame
    {
        public Entity Entity { get; private set; }

        public void Load() => Entity = world.Spawn(Transform.Identity, new Model(ModelRef.Cube));

        public void Update(GameTime time)
        {
        }
    }

    private sealed class CubeAndHand(World world) : IGame
    {
        public void Load() => world.Spawn(Transform.Identity, new Model(ModelRef.Cube));

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => view.Model(-1, ModelRef.Cube, Matrix4x4.Identity);
    }

    private sealed class Carried(World world) : IGame
    {
        public Entity Child { get; private set; }

        public View? Seen { get; private set; }

        private Entity _parent;

        public void Load()
        {
            _parent = world.Spawn(Transform.At(Vector3.Zero));
            Child = world.Spawn(Transform.At(new Vector3(0f, 1f, 0f)), new Parent(_parent));
        }

        public void Update(GameTime time) => world.Get<Transform>(_parent).Position += new Vector3(2f, 0f, 0f);

        public void Draw(View view) => Seen = view;
    }

    [Fact]
    public void A_game_with_no_Draw_shows_its_entities()
    {
        using var run = HeadlessGame.Create<Cube>(seed: 1);
        run.Ticks(1);

        run.Frame(0f);

        Assert.Equal(run.Game.Entity.Id, Assert.Single(run.Drawn).Key);
    }

    [Fact]
    public void Draw_adds_to_what_the_world_drew()
    {
        using var run = HeadlessGame.Create<CubeAndHand>(seed: 1);
        run.Ticks(1);

        run.Frame(0f);

        Assert.Equal(2, run.Drawn.Count);
    }

    [Fact]
    public void TransformOf_follows_the_parent_between_ticks()
    {
        using var run = HeadlessGame.Create<Carried>(seed: 1);
        run.Ticks(2); // The parent's X was 2, is 4.

        run.Frame(0.25f);

        var at = run.Game.Seen!.TransformOf(run.Game.Child).Translation;
        Assert.Equal(2.5f, at.X, 4);
        Assert.Equal(1f, at.Y, 4);
        Assert.Equal(0f, at.Z, 4);
    }

    [Fact]
    public void TransformOf_an_entity_with_no_transform_says_so()
    {
        using var run = HeadlessGame.Create<Carried>(seed: 1);
        run.Frame(0f);
        var bare = run.World.Create();

        var error = Assert.Throws<InvalidOperationException>(() => run.Game.Seen!.TransformOf(bare));

        Assert.Equal($"Entity {bare.Id} has no Transform.", error.Message);
    }
}
