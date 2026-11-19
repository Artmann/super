using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>Every entity's ragdoll, built, moved and taken down to match its <see cref="Ragdoll"/>.</summary>
/// <param name="floor">Finds the ground: the height a ray down from a point hits within a reach, on some layers, or null.</param>
internal sealed class RagdollSet(World world, PhysicsSystem system, Dictionary<uint, Entity> owners, Func<Vector3, float, uint, float?> floor)
{
    private readonly Dictionary<Entity, LiveRagdoll> _live = [];
    private readonly List<Entity> _entities = [];
    private readonly List<Entity> _gone = [];

    /// <summary>Whether any ragdoll has bodies, so the world needs stepping.</summary>
    public bool AnyActive()
    {
        foreach (var (_, live) in _live)
        {
            if (live.Parts.Active)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Simulates <paramref name="seconds"/> in <paramref name="steps"/> steps. While any ragdoll's muscles pull, they
    /// steer each step, so the steps run one at a time.
    /// </summary>
    public PhysicsUpdateError Update(float seconds, int steps, JobSystem jobs)
    {
        if (!AnyPowered())
        {
            return system.Update(seconds, steps, jobs);
        }

        var error = PhysicsUpdateError.None;
        for (int i = 0; i < steps && error == PhysicsUpdateError.None; i++)
        {
            Drive();
            error = system.Update(seconds / steps, 1, jobs);
        }

        return error;
    }

    /// <summary>Whether any ragdoll's muscles pull, so the steps run one at a time.</summary>
    private bool AnyPowered()
    {
        foreach (var (_, live) in _live)
        {
            if (live.Powered)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entity's ragdoll while it has bodies, or null.</summary>
    public LiveRagdoll? Active(Entity entity) => _live.TryGetValue(entity, out var live) && live.Parts.Active ? live : null;

    /// <summary>The entity's ragdoll while it simulates at least one part, or null.</summary>
    public LiveRagdoll? Simulating(Entity entity) => _live.TryGetValue(entity, out var live) && live.Parts.Simulating ? live : null;

    /// <summary>
    /// Builds, moves and takes down ragdolls to match each entity's <see cref="Ragdoll"/>. Kinematic parts head for
    /// where the animation has them, reaching it over <paramref name="seconds"/>; with 0 they jump there.
    /// </summary>
    public void Sync(float seconds)
    {
        _entities.Clear();
        foreach (var row in world.Query<Ragdoll, Transform>())
        {
            _entities.Add(row.Entity);
        }

        DropGone();
        _entities.Sort(EntityOrder.Comparison);
        foreach (var entity in _entities)
        {
            Sync(entity, world.Get<Ragdoll>(entity), seconds);
        }
    }

    /// <summary>Pulls each powered ragdoll's joints towards the animation's pose, before one simulation step.</summary>
    private void Drive()
    {
        foreach (var (_, live) in _live)
        {
            if (live.Powered)
            {
                RagdollMuscles.Drive(system.BodyInterface, live);
            }
        }
    }

    /// <summary>After a step, moves each simulated ragdoll's entity with its pelvis and hands its pose to the animator.</summary>
    public void Write(float seconds)
    {
        foreach (var (entity, live) in _live)
        {
            if (live.Parts.Simulating)
            {
                live.Write(ref world.Get<Transform>(entity), seconds);
            }
        }
    }

    public void Dispose()
    {
        foreach (var (_, live) in _live)
        {
            live.Parts.Destroy();
        }

        _live.Clear();
    }

    /// <summary>Takes down the ragdolls whose entity, <see cref="Ragdoll"/> or Transform is gone.</summary>
    private void DropGone()
    {
        _gone.Clear();
        foreach (var (entity, _) in _live)
        {
            if (!world.IsAlive(entity) || !world.Has<Ragdoll>(entity) || !world.Has<Transform>(entity))
            {
                _gone.Add(entity);
            }
        }

        foreach (var entity in _gone)
        {
            var live = _live[entity];
            live.Parts.Destroy();
            live.Pose.Clear(live.Animator);
            _live.Remove(entity);
        }
    }

    private void Sync(Entity entity, Ragdoll ragdoll, float seconds)
    {
        var animator = RagdollChecks.Check(world, entity, ragdoll);
        var transform = world.Get<Transform>(entity);
        var live = LiveFor(entity, ragdoll.Body, animator);
        float scale = ragdoll.Body.Fitting.RootScale * transform.Scale.X;
        if (live.Parts.Active && live.Scale != scale)
        {
            // A new size needs new shapes. The parts start again from the animation.
            live.Parts.Destroy();
        }

        live.Scale = scale;
        live.Targets.Aim(live.Body, live.Animator, transform, seconds);
        if (ragdoll.Mode == RagdollMode.Off && live.Parts.Active)
        {
            RagdollStanding.TurnOff(world, entity, live, ragdoll.BlendSeconds, floor);
        }
        else if (ragdoll.Mode == RagdollMode.Off)
        {
            live.Pose.Fade(live.Animator, ragdoll.BlendSeconds, seconds);
        }
        else
        {
            if (!live.Parts.Active)
            {
                live.Pose.StopFading();
                RagdollBuilder.Build(system, entity, live, ragdoll, owners);
            }

            live.Follow(ragdoll, seconds);
        }

        live.Mode = ragdoll.Mode;
        live.Strength = Math.Clamp(ragdoll.Strength, 0f, 1f);
    }

    /// <summary>The entity's ragdoll, made again when its body or animator changed.</summary>
    private LiveRagdoll LiveFor(Entity entity, RagdollBody body, Animator animator)
    {
        if (_live.TryGetValue(entity, out var live) && live.Body == body && live.Animator == animator)
        {
            return live;
        }

        if (live is not null)
        {
            live.Parts.Destroy();
            live.Pose.Clear(live.Animator);
        }

        live = new LiveRagdoll(system, body, animator, owners);
        _live[entity] = live;
        return live;
    }
}
