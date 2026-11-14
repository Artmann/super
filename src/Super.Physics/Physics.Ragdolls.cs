using System.Numerics;
using Super.Ecs;

namespace Super.Physics;

public sealed partial class Physics
{
    private readonly RagdollSet _ragdolls;

    /// <summary>
    /// Whether an entity's ragdoll lies face up, by which way its pelvis faces, so the game can pick a get-up clip
    /// before turning it off. False when it has no simulated pelvis.
    /// </summary>
    public bool IsFaceUp(Entity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ragdolls.Active(entity)?.Parts.IsFaceUp() ?? false;
    }

    /// <summary>
    /// Lists the shapes of an entity's ragdoll where they are now, to draw them while tuning a <see cref="RagdollBody"/>.
    /// Empty while its <see cref="Ragdoll"/> is off.
    /// </summary>
    public void RagdollShapes(Entity entity, List<RagdollShapeView> shapes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        shapes.Clear();
        _ragdolls.Active(entity)?.Shapes(shapes);
    }

    /// <summary>Pushes one part of a ragdoll, such as the head a ball hits, at a point in the world or through its middle.</summary>
    public void PushPart(Entity entity, string joint, Vector3 impulse, Vector3? point = null)
    {
        CheckFinite(impulse, nameof(impulse));
        var live = RagdollFor(entity) ?? throw new InvalidOperationException($"Entity {entity.Id} has no simulated Ragdoll; switch its Mode to Simulated first.");
        int part = live.Body.PartOf(joint);
        if (part < 0 || !live.Parts.Dynamic[part])
        {
            throw new InvalidOperationException($"Entity {entity.Id}'s ragdoll doesn't simulate joint {joint}, so it can't be pushed.");
        }

        if (point is { } at)
        {
            CheckFinite(at, nameof(point));
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
    private LiveRagdoll? RagdollFor(Entity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ragdolls.Simulating(entity) is null && _world.IsAlive(entity) && _world.Has<Ragdoll>(entity) && _world.Get<Ragdoll>(entity).Mode == RagdollMode.Simulated)
        {
            Sync();
        }

        return _ragdolls.Simulating(entity);
    }
}
