using System.Numerics;
using JoltPhysicsSharp;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Reads and pushes entities' ragdolls. Reach it through <c>physics.Ragdolls</c>.</summary>
public sealed class PhysicsRagdolls
{
    private readonly DisposalGuard _guard;
    private readonly World _world;
    private readonly PhysicsSystem _system;
    private readonly Action _sync;

    internal PhysicsRagdolls(DisposalGuard guard, World world, PhysicsSystem system, RagdollSet set, Action sync)
    {
        _guard = guard;
        _world = world;
        _system = system;
        Set = set;
        _sync = sync;
    }

    /// <summary>Every entity's live ragdoll.</summary>
    internal RagdollSet Set { get; }

    /// <summary>
    /// Whether an entity's ragdoll lies face up, by which way its pelvis faces, so the game can pick a get-up clip
    /// before turning it off. False when it has no simulated pelvis.
    /// </summary>
    public bool IsFaceUp(Entity entity)
    {
        _guard.ThrowIfDisposed();
        return Set.Active(entity)?.Parts.IsFaceUp() ?? false;
    }

    /// <summary>
    /// Lists the shapes of an entity's ragdoll where they are now, to draw them while tuning a <see cref="RagdollBody"/>.
    /// Empty while its <see cref="Ragdoll"/> is off.
    /// </summary>
    public void Shapes(Entity entity, List<RagdollShapeView> shapes)
    {
        _guard.ThrowIfDisposed();
        shapes.Clear();
        Set.Active(entity)?.Shapes(shapes);
    }

    /// <summary>Pushes one part of a ragdoll, such as the head a ball hits, at a point in the world or through its middle.</summary>
    public void PushPart(Entity entity, string joint, Vector3 impulse, Vector3? point = null)
    {
        Numbers.RequireFinite(impulse, nameof(impulse));
        var live = Simulated(entity) ?? throw new InvalidOperationException($"Entity {entity.Id} has no simulated Ragdoll; switch its Mode to Simulated first.");
        int part = live.Body.PartOf(joint);
        if (part < 0 || !live.Parts.Dynamic[part])
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s ragdoll doesn't simulate joint {joint}, so it can't be pushed.");
        }

        if (point is { } at)
        {
            Numbers.RequireFinite(at, nameof(point));
            _system.BodyInterface.AddImpulse(in live.Parts.Ids[part], in impulse, in at);
        }
        else
        {
            _system.BodyInterface.AddImpulse(in live.Parts.Ids[part], in impulse);
        }
    }

    /// <summary>
    /// The entity's simulated ragdoll, syncing first if it was only just switched to <see cref="RagdollMode.Simulated"/>,
    /// or null if it has none.
    /// </summary>
    internal LiveRagdoll? Simulated(Entity entity)
    {
        _guard.ThrowIfDisposed();
        if (Set.Simulating(entity) is null && _world.IsAlive(entity) && _world.Has<Ragdoll>(entity) && _world.Get<Ragdoll>(entity).Mode == RagdollMode.Simulated)
        {
            _sync();
        }

        return Set.Simulating(entity);
    }
}
