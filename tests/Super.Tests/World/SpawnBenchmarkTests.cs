using System.Numerics;
using Rendition;
using Xunit.Abstractions;

namespace Super.Tests;

/// <summary>Spawning and destroying stay cheap enough to do by the thousand every frame.</summary>
[Collection(TimingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class SpawnBenchmarkTests(ITestOutputHelper output)
{
    private const int PerFrame = 1000;

    [Fact]
    public void Spawning_all_at_once_is_much_faster_than_adding_one_at_a_time()
    {
        var spawned = new World();
        var added = new World();
        var spawnedEntities = new Entity[PerFrame];
        var addedEntities = new Entity[PerFrame];

        var (spawn, add) = Timing.Medians(() =>
        {
            for (int i = 0; i < PerFrame; i++)
            {
                spawnedEntities[i] = spawned.Spawn(Transform.Identity, new Model(ModelRef.Cube), new Velocity(1f), new Health(3));
            }

            DestroyAll(spawned, spawnedEntities);
        }, () =>
        {
            for (int i = 0; i < PerFrame; i++)
            {
                var entity = added.Create();
                added.Add(entity, Transform.Identity);
                added.Add(entity, new Model(ModelRef.Cube));
                added.Add(entity, new Velocity(1f));
                added.Add(entity, new Health(3));
                addedEntities[i] = entity;
            }

            DestroyAll(added, addedEntities);
        }, warmUp: 1000);

        output.WriteLine($"Spawn: {spawn:F3} ms, Create and Add: {add:F3} ms, for {PerFrame} entities.");
        Assert.True(spawn <= 0.5 * add, $"Spawning took {spawn:F3} ms against {add:F3} ms adding one at a time.");
    }

    [Fact]
    public void Spawning_and_destroying_allocates_nothing_once_warm()
    {
        var world = new World();
        var entities = new Entity[PerFrame];
        void Frame()
        {
            for (int i = 0; i < PerFrame; i++)
            {
                entities[i] = world.Spawn(Transform.At(new Vector3(i, 0f, 0f)), new Model(ModelRef.Cube));
            }

            DestroyAll(world, entities);
        }

        for (int i = 0; i < 20; i++)
        {
            Frame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Destroying_costs_the_same_however_many_parented_entities_the_world_holds()
    {
        var bare = new World();
        var crowded = new World();
        for (int i = 0; i < 100; i++)
        {
            var parent = crowded.Spawn(Transform.Identity);
            for (int j = 0; j < 200; j++)
            {
                crowded.Spawn(Transform.Identity, new Parent(parent));
            }
        }

        var bareEntities = new Entity[PerFrame];
        var crowdedEntities = new Entity[PerFrame];
        var (alone, among) = Timing.Medians(() => SpawnAndDestroy(bare, bareEntities), () => SpawnAndDestroy(crowded, crowdedEntities));

        output.WriteLine($"Among no parented entities: {alone:F3} ms, among 20,000: {among:F3} ms, for {PerFrame} entities.");
        Assert.True(among <= 3 * alone, $"Destroying among 20,000 parented entities took {among:F3} ms against {alone:F3} ms.");
    }

    private static void SpawnAndDestroy(World world, Entity[] entities)
    {
        for (int i = 0; i < PerFrame; i++)
        {
            entities[i] = world.Spawn(Transform.Identity, new Model(ModelRef.Cube));
        }

        DestroyAll(world, entities);
    }

    private static void DestroyAll(World world, Entity[] entities)
    {
        foreach (var entity in entities)
        {
            world.Destroy(entity);
        }
    }

    private readonly record struct Velocity(float X);

    private readonly record struct Health(int Value);
}
