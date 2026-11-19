using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>
/// Simulates and queries the entities that have a <see cref="Collider"/>. Before each Update the game loop calls
/// <see cref="Step"/>: colliders move to where their entities are, dynamic <see cref="Body"/>s fall and collide and
/// their Transforms are written, and <see cref="Trigger"/> areas list who came and went. Its parts do the rest:
/// <see cref="Bodies"/> pushes and reads bodies, <see cref="Characters"/> walks a <see cref="Character"/> at once,
/// <see cref="Queries"/> casts rays and shapes, <see cref="Triggers"/> says who is in each area and
/// <see cref="Ragdolls"/> reads and pushes ragdolls. Queries in Update see the world as of the last sync. Built on Jolt
/// Physics, which gives the same answers for the same calls in the same build.
/// </summary>
public sealed class Physics : ITickHook, IDisposable
{
    /// <summary>Every layer, for queries that skip none.</summary>
    public const uint AllLayers = LayerBits.All;

    /// <summary>How far a ray or shape cast reaches when no distance is given, in metres.</summary>
    public const float DefaultReach = PhysicsQueries.DefaultReach;

    /// <summary>Earth's gravity, which <see cref="Gravity"/> starts as, in metres per second squared.</summary>
    public static readonly Vector3 EarthGravity = new(0f, -9.81f, 0f);

    // A step longer than this is split, so a slow tick rate doesn't let things tunnel or jitter.
    private const float LongestStep = 1f / 60f;

    private readonly GameTime? _time;
    private readonly DisposalGuard _guard;
    private readonly JoltSystem _jolt;
    private readonly BodySync _tracked;
    private readonly ContactSet _contacts;

    /// <summary>A world that steps only when <see cref="Step"/> is called, such as in a test.</summary>
    public Physics(World world)
        : this(world, null)
    {
    }

    /// <summary>A world the game loop steps by <paramref name="time"/>'s <see cref="GameTime.DeltaSeconds"/> before each Update.</summary>
    public Physics(World world, GameTime? time)
    {
        _time = time;
        _guard = new DisposalGuard(this);
        _jolt = new JoltSystem();
        var system = _jolt.System;
        system.Gravity = EarthGravity;
        _tracked = new BodySync(world, system);
        _contacts = new ContactSet(_tracked.Owners);
        system.OnContactAdded += _contacts.Added;
        var filters = new QueryFilters(world, _tracked.Owners);
        var parts = new QueryParts(new QueryShapeCache(), filters, new ShapeOverlaps(system, _tracked.Owners, filters));
        Queries = new PhysicsQueries(_guard, system, _tracked.Owners, parts);
        var ragdolls = new RagdollSet(world, system, _tracked.Owners, (from, reach, layers) => Queries.Raycast(new Super.Ray(from, -Vector3.UnitY), reach, layers)?.Point.Y);
        Ragdolls = new PhysicsRagdolls(_guard, world, system, ragdolls, Sync);
        Bodies = new PhysicsBodies(_guard, world, _tracked, Ragdolls, Sync);
        Characters = new PhysicsCharacters(_guard, world, _tracked, filters);
        Triggers = new PhysicsTriggers(world, _tracked, parts);
    }

    /// <summary>How many colliders there are, as of the last sync, triggers and characters included.</summary>
    public int Count => _tracked.Count;

    /// <summary>The pull on every dynamic body, in metres per second squared. <see cref="EarthGravity"/> to start.</summary>
    public Vector3 Gravity
    {
        get => _jolt.System.Gravity;
        set
        {
            if (!Numbers.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Gravity must be finite.");
            }

            _jolt.System.Gravity = value;
        }
    }

    /// <summary>Reads how fast bodies move, and pushes, spins and speeds up dynamic ones.</summary>
    public PhysicsBodies Bodies { get; }

    /// <summary>Walks entities with a <see cref="Character"/>.</summary>
    public PhysicsCharacters Characters { get; }

    /// <summary>Casts rays and shapes, and finds what overlaps a shape.</summary>
    public PhysicsQueries Queries { get; }

    /// <summary>Who is in each <see cref="Trigger"/> area, and who came and went in the last step.</summary>
    public PhysicsTriggers Triggers { get; }

    /// <summary>Reads and pushes entities' ragdolls.</summary>
    public PhysicsRagdolls Ragdolls { get; }

    /// <summary>
    /// What started touching in the last step, one contact for each pair of entities, in entity order: a crate landing,
    /// a ball hitting a wall, a ragdoll's limb hitting the floor. Things that keep touching aren't listed again. Use
    /// <see cref="Contact.Speed"/> to tell a tap from a crash, such as for how loud a thud is. Triggers and a
    /// ragdoll's parts touching each other don't count. Read it in Update; the next step replaces it.
    /// </summary>
    public IReadOnlyList<Contact> Contacts => _contacts.List;

    void ITickHook.BeforeUpdate()
    {
        if (_time is null)
        {
            Sync();
            return;
        }

        Step(_time.DeltaSeconds);
    }

    /// <summary>
    /// Moves the world on by <paramref name="seconds"/>: syncs the colliders, pushing dynamic bodies out of the way of
    /// kinematic ones that moved, simulates, writes each dynamic body's Transform, and finds what entered and left
    /// each trigger. The game loop does this before each Update with the tick's <see cref="GameTime.DeltaSeconds"/>;
    /// call it yourself only for a Physics you made. 0 simulates nothing but still syncs and checks triggers.
    /// </summary>
    public void Step(float seconds)
    {
        _guard.ThrowIfDisposed();
        if (!Numbers.IsZeroOrMore(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A step must be a finite number of seconds, 0 or more.");
        }

        Sync(seconds);
        var ragdolls = Ragdolls.Set;
        if (seconds > 0f && (_tracked.AnyMoving() || ragdolls.AnyActive()))
        {
            var jobs = _jolt.Jobs;
            int steps = Math.Max(1, (int)MathF.Ceiling((seconds / LongestStep) - 0.001f));
            PhysicsUpdateError error;
            lock (JoltSystem.Scratch)
            {
                error = ragdolls.Update(seconds, steps, jobs);
            }

            if (error != PhysicsUpdateError.None)
            {
                throw new InvalidOperationException($"The physics step ran out of room ({error}); there are too many bodies touching at once.");
            }

            _tracked.WriteDynamics();
            ragdolls.Write(seconds);
        }

        _contacts.Gather();
        Triggers.Find();
    }

    /// <summary>
    /// Moves every collider to where its entity is now, adds new ones and drops those whose entity or
    /// <see cref="Collider"/> is gone. <see cref="Step"/> does this first. Call it in Update to make queries see what
    /// you moved this tick; colliders moved this way jump rather than push.
    /// </summary>
    public void Sync()
    {
        _guard.ThrowIfDisposed();
        Sync(0f);
    }

    public void Dispose()
    {
        if (_guard.IsDisposed)
        {
            return;
        }

        _guard.IsDisposed = true;
        Characters.Release();
        Ragdolls.Set.Dispose();
        _tracked.Dispose();
        Queries.Release();
        _jolt.System.OnContactAdded -= _contacts.Added;
        _jolt.Dispose();
    }

    /// <summary>Syncs; with <paramref name="seconds"/> above 0, moved kinematic bodies sweep there over that time.</summary>
    private void Sync(float seconds)
    {
        _tracked.Sync(seconds);
        Characters.Drop();
        Ragdolls.Set.Sync(seconds);
        _tracked.SyncJoints();
    }
}
