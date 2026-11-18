using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// What a query, a trigger check or a character's move may meet: colliders on some layers, alive, and not a trigger.
/// Shared, so each sets them just before it asks Jolt.
/// </summary>
internal sealed class QueryFilters(World world, Dictionary<uint, Entity> owners) : IDisposable
{
    public QueryLayerFilter Layers { get; } = new();

    public LiveBodyFilter Alive { get; } = new(world, owners);

    /// <summary>Lets the next query see only <paramref name="layers"/>, and skip no one.</summary>
    public void Only(uint layers)
    {
        if (layers == 0 || (layers & ~LayerBits.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(layers), layers, "Layers are bits 0 to 15, and a query needs at least one.");
        }

        Layers.Layers = layers;
        Alive.Skip = null;
    }

    public void Dispose()
    {
        Alive.Dispose();
        Layers.Dispose();
    }

    /// <summary>Lets a query see only colliders on some layers.</summary>
    public sealed class QueryLayerFilter : ObjectLayerFilter
    {
        public uint Layers { get; set; } = LayerBits.All;

        protected override bool ShouldCollide(ObjectLayer layer) => (ObjectLayerPairFilterMask.GetGroup(in layer) & Layers) != 0;
    }

    /// <summary>
    /// Skips triggers, one entity a query is made for (<see cref="Skip"/>), and the bodies of entities destroyed, or
    /// stripped of their collider, since the last sync.
    /// </summary>
    public sealed class LiveBodyFilter(World world, Dictionary<uint, Entity> owners) : BodyFilter
    {
        public Entity? Skip { get; set; }

        protected override bool ShouldCollide(BodyID bodyID) =>
            owners.TryGetValue(bodyID.ID, out var entity)
            && entity != Skip
            && world.IsAlive(entity)
            && (world.Has<Collider>(entity) || world.Has<Ragdoll>(entity))
            && !world.Has<Trigger>(entity);

        protected override bool ShouldCollideLocked(JoltPhysicsSharp.Body body) => true;
    }
}
