using System.Numerics;
using Super.Ecs;

namespace Super.Physics.Tests;

public class AllocationTests
{
    private const float Tick = 1f / 60f;

    private static Entity Place(World world, Vector3 position, Collider collider)
    {
        var entity = world.Create();
        world.Add(entity, Transform.At(position));
        world.Add(entity, collider);
        return entity;
    }

    [Fact]
    public void Stepping_and_querying_a_settled_world_allocates_nothing()
    {
        var world = new World();
        using var physics = new Physics(world);
        var floor = Place(world, new Vector3(0f, -0.5f, 0f), Collider.Box(new Vector3(40f, 1f, 40f)));
        world.Add(floor, Body.Static);
        for (int i = 0; i < 20; i++)
        {
            var box = Place(world, new Vector3(i - 10f, 1f + (i * 0.3f), 0f), Collider.Box(Vector3.One));
            world.Add(box, Body.Dynamic());
        }

        var area = Place(world, Vector3.Zero, Collider.Box(new Vector3(6f)));
        world.Add(area, new Trigger());
        var walker = Place(world, new Vector3(0f, 0f, 5f), Collider.Capsule(1.8f, 0.4f));
        world.Add(walker, new Character());
        var rays = new List<RayHit>();
        var casts = new List<ShapeHit>();
        var overlaps = new List<Entity>();
        var ray = new Ray(new Vector3(0f, 1f, 10f), -Vector3.UnitZ);

        void Frame()
        {
            physics.Step(Tick);
            physics.Move(walker, new Vector3(1f, -1f, 0f), Tick);
            physics.Raycast(ray);
            physics.RaycastAll(ray, rays);
            physics.ShapeCast(Collider.Ball(0.3f), ray.Origin, ray.Direction);
            physics.ShapeCastAll(Collider.Ball(0.3f), ray.Origin, ray.Direction, casts);
            physics.Overlap(Collider.Ball(2f), Vector3.Zero, overlaps);
        }

        // Warm up first, so lists have grown and JIT and lazy setup are done.
        for (int i = 0; i < 120; i++)
        {
            Frame();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
        {
            Frame();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
