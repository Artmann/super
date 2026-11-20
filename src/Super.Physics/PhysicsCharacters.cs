using System.Numerics;
using JoltPhysicsSharp;

namespace Super.Physics;

/// <summary>Walks entities with a <see cref="Character"/>. Reach it through <c>physics.Characters</c>.</summary>
public sealed class PhysicsCharacters
{
    private readonly DisposalGuard _guard;
    private readonly World _world;
    private readonly PhysicsSystem _system;
    private readonly Dictionary<uint, Entity> _owners;
    private readonly QueryFilters _filters;
    private readonly Dictionary<Entity, Walker> _characters = [];
    private readonly List<Entity> _gone = [];

    internal PhysicsCharacters(DisposalGuard guard, World world, BodySync bodies, QueryFilters filters)
    {
        _guard = guard;
        _world = world;
        _system = bodies.Simulation;
        _owners = bodies.Owners;
        _filters = filters;
    }

    /// <summary>
    /// Walks a <see cref="Character"/> at <paramref name="velocity"/> for <paramref name="seconds"/>, at once, and
    /// writes where it ends up to its Transform. It slides along walls, climbs steps up to its
    /// <see cref="Character.StepHeight"/>, keeps to the ground going down, can't walk up slopes steeper than its
    /// <see cref="Character.MaxSlope"/>, and pushes dynamic bodies. Gravity is yours to add: carry the returned
    /// <see cref="CharacterMove.Velocity"/>'s Y from tick to tick. Setting its Transform yourself teleports it.
    /// </summary>
    public CharacterMove Move(Entity entity, Vector3 velocity, float seconds)
    {
        _guard.ThrowIfDisposed();
        CheckMove(entity, velocity, seconds);
        var walker = Walking(entity);
        ref var transform = ref _world.Get<Transform>(entity);
        var character = walker.Virtual;
        if (transform.Position != walker.Position)
        {
            character.Position = transform.Position;
        }

        character.Rotation = Quaternion.Normalize(transform.Rotation);
        character.LinearVelocity = velocity;
        if (seconds > 0f)
        {
            Walk(entity, walker, seconds);
        }

        var position = character.Position;
        transform.Position = position;
        walker.Position = position;
        return Result(character, position);
    }

    /// <summary>Lets go of the characters whose entity, or whose Character, is gone.</summary>
    internal void Drop()
    {
        _gone.Clear();
        foreach (var (entity, _) in _characters)
        {
            if (!_world.IsAlive(entity) || !_world.Has<Character>(entity))
            {
                _gone.Add(entity);
            }
        }

        foreach (var entity in _gone)
        {
            _characters[entity].Dispose();
            _characters.Remove(entity);
        }
    }

    /// <summary>Lets go of every character, when the physics world is disposed.</summary>
    internal void Release()
    {
        foreach (var (_, walker) in _characters)
        {
            walker.Dispose();
        }

        _characters.Clear();
    }

    private static void CheckMove(Entity entity, Vector3 velocity, float seconds)
    {
        if (!Numbers.IsZeroOrMore(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A move must last a finite number of seconds, 0 or more.");
        }

        if (!Numbers.IsFinite(velocity))
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), velocity, $"Entity {entity.Id} can't move at {velocity}; the velocity must be finite.");
        }
    }

    /// <summary>Lets Jolt walk the character for <paramref name="seconds"/>, past everything but itself.</summary>
    private void Walk(Entity entity, Walker walker, float seconds)
    {
        var step = walker.Settings.StepHeight;
        var settings = new ExtendedUpdateSettings
        {
            StickToFloorStepDown = new Vector3(0f, -step, 0f),
            WalkStairsStepUp = new Vector3(0f, step, 0f),
        };
        var layer = JoltSystem.Layer(walker.Collider.Layers, walker.Settings.CollidesWith);
        _filters.Alive.Skip = entity;
        try
        {
            lock (JoltSystem.Scratch)
            {
                walker.Virtual.ExtendedUpdate(seconds, settings, in layer, _system, _filters.Alive, null!);
            }
        }
        finally
        {
            _filters.Alive.Skip = null;
        }
    }

    /// <summary>Where the character ended up, how fast, and what it stands on.</summary>
    private CharacterMove Result(CharacterVirtual character, Vector3 position)
    {
        var footing = character.GroundState switch
        {
            GroundState.OnGround => Footing.Ground,
            GroundState.OnSteepGround => Footing.SteepGround,
            GroundState.NotSupported => Footing.Unsupported,
            _ => Footing.InAir,
        };
        Entity? ground = footing != Footing.InAir && _owners.TryGetValue(character.GroundBodyId, out var under) ? under : null;
        var normal = footing == Footing.InAir ? Vector3.Zero : character.GroundNormal;
        var carried = character.GroundVelocity;
        return new CharacterMove(position, character.LinearVelocity, footing, normal, ground, carried);
    }

    /// <summary>The entity's Jolt character, made or rebuilt to match its components.</summary>
    private Walker Walking(Entity entity)
    {
        Require(entity);
        var collider = _world.Get<Collider>(entity);
        var settings = _world.Get<Character>(entity);
        var transform = _world.Get<Transform>(entity);
        if (_characters.TryGetValue(entity, out var walker)
            && walker.Collider == collider
            && walker.Scale == transform.Scale
            && walker.Settings == settings)
        {
            return walker;
        }

        ColliderShapes.Check($"Entity {entity.Id}'s Collider", collider);
        CheckCharacter(entity, settings);
        return Build(entity, walker);
    }

    /// <summary>Throws unless the entity can walk: it has a Character, a Collider and a Transform, no Parent and no dynamic Body.</summary>
    private void Require(Entity entity)
    {
        if (!_world.IsAlive(entity) || !_world.Has<Character>(entity) || !_world.Has<Collider>(entity) || !_world.Has<Transform>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} can't Move: it needs a Character, a Collider and a Transform.");
        }

        if (_world.Has<Parent>(entity))
        {
            throw new InvalidOperationException($"Entity {entity.Id} can't Move: it has a Parent, and a character walks in the world.");
        }

        if (_world.Has<Body>(entity) && _world.Get<Body>(entity).Kind == BodyKind.Dynamic)
        {
            throw new InvalidOperationException($"Entity {entity.Id} has a dynamic Body and a Character; a character is moved by Physics.Characters.Move, not simulated.");
        }
    }

    /// <summary>Makes the entity's Jolt character from its components, carrying on from <paramref name="old"/>'s place and speed.</summary>
    private Walker Build(Entity entity, Walker? old)
    {
        var collider = _world.Get<Collider>(entity);
        var settings = _world.Get<Character>(entity);
        var transform = _world.Get<Transform>(entity);
        var position = transform.Position;
        var velocity = Vector3.Zero;
        if (old is not null)
        {
            position = old.Virtual.Position;
            velocity = old.Virtual.LinearVelocity;
            old.Dispose();
        }

        using var shape = ColliderShapes.Make(collider, transform.Scale);
        var rotation = Quaternion.Normalize(transform.Rotation);
        var virtualSettings = new CharacterVirtualSettings
        {
            Shape = shape,
            MaxSlopeAngle = settings.MaxSlope * (MathF.PI / 180f),
            Mass = settings.Mass,
            Up = Vector3.UnitY,
        };
        var character = new CharacterVirtual(virtualSettings, in position, in rotation, (ulong)entity.Id, _system)
        {
            LinearVelocity = velocity,
        };
        var walker = new Walker { Virtual = character, Collider = collider, Scale = transform.Scale, Settings = settings, Position = position };
        _characters[entity] = walker;
        return walker;
    }

    private static void CheckCharacter(Entity entity, Character settings)
    {
        if (!(settings.MaxSlope > 0f && settings.MaxSlope < 90f))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.MaxSlope, $"Entity {entity.Id}'s Character has a max slope of {settings.MaxSlope}°; it must be above 0 and below 90.");
        }

        if (!Numbers.IsZeroOrMore(settings.StepHeight))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.StepHeight, $"Entity {entity.Id}'s Character has a step height of {settings.StepHeight}; it must be 0 or more.");
        }

        if (!Numbers.IsAboveZero(settings.Mass))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.Mass, $"Entity {entity.Id}'s Character has mass {settings.Mass}; it must be above 0.");
        }

        if (settings.CollidesWith == 0 || (settings.CollidesWith & ~LayerBits.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.CollidesWith, $"Entity {entity.Id}'s Character collides with layers {settings.CollidesWith}; layers are bits 0 to 15, and it needs at least one.");
        }
    }

    private sealed class Walker : IDisposable
    {
        public required CharacterVirtual Virtual { get; init; }

        public required Collider Collider { get; init; }

        public required Vector3 Scale { get; init; }

        public required Character Settings { get; init; }

        public Vector3 Position { get; set; }

        public void Dispose() => Virtual.Dispose();
    }
}
