namespace Super.Physics;

/// <summary>How a <see cref="Body"/> moves.</summary>
public enum BodyKind
{
    /// <summary>Moved only by code, through its <see cref="Super.Ecs.Transform"/>. Pushes dynamic bodies out of its way.</summary>
    Kinematic,

    /// <summary>Never meant to move, such as walls and floors. The cheapest kind; moving it teleports it.</summary>
    Static,

    /// <summary>Moved by the simulation: it falls, collides, bounces and is pushed. Physics writes its Transform.</summary>
    Dynamic,
}

/// <summary>
/// How an entity's <see cref="Collider"/> takes part in the simulation. A collider with no Body is kinematic: it
/// follows its Transform and pushes dynamic bodies. Make one with <see cref="Static"/>, <see cref="Kinematic"/> or
/// <see cref="Dynamic"/>. A dynamic body's Transform is written by physics after each step, so it can't have a
/// <see cref="Super.Ecs.Parent"/>; setting its Transform yourself teleports it, keeping its velocity.
/// </summary>
public readonly record struct Body
{
    /// <summary>A kinematic body, the same as <see cref="Kinematic"/>; without this, <c>new Body()</c> would collide with nothing.</summary>
    public Body()
        : this(BodyKind.Kinematic, 0f, 0.5f, 0f, 0f, 0f, 0f, false, false, Physics.AllLayers)
    {
    }

    private Body(
        BodyKind kind,
        float mass,
        float friction,
        float bounciness,
        float drag,
        float angularDrag,
        float gravityScale,
        bool lockRotation,
        bool continuous,
        uint collidesWith)
    {
        Kind = kind;
        Mass = mass;
        Friction = friction;
        Bounciness = bounciness;
        Drag = drag;
        AngularDrag = angularDrag;
        GravityScale = gravityScale;
        LockRotation = lockRotation;
        Continuous = continuous;
        CollidesWith = collidesWith;
    }

    /// <summary>A kinematic body that collides with every layer.</summary>
    public static Body Kinematic { get; } = new();

    /// <summary>A static body that collides with every layer.</summary>
    public static Body Static { get; } = new(BodyKind.Static, 0f, 0.5f, 0f, 0f, 0f, 0f, false, false, Physics.AllLayers);

    /// <summary>How much the angular velocity fades each second, from 0 up.</summary>
    public float AngularDrag { get; }

    /// <summary>
    /// How much of its speed it keeps when it bounces, from 0 (none) to 1 (all). Two bodies meeting use the larger of
    /// their two.
    /// </summary>
    public float Bounciness { get; }

    /// <summary>The layers whose colliders it bumps into, as bits. Both sides must say yes for two bodies to collide.</summary>
    public uint CollidesWith { get; }

    /// <summary>
    /// Whether it sweeps along its path each step, so a fast, small body can't pass through a thin wall. It costs
    /// more, so keep it for bullets and the like.
    /// </summary>
    public bool Continuous { get; }

    /// <summary>How much the velocity fades each second, from 0 up, like air resistance.</summary>
    public float Drag { get; }

    /// <summary>How rough it is, from 0 (ice) up. Two bodies meeting use the square root of their two multiplied.</summary>
    public float Friction { get; }

    /// <summary>How strongly gravity pulls it: 1 as normal, 0 to float.</summary>
    public float GravityScale { get; }

    public BodyKind Kind { get; }

    /// <summary>Whether it never turns, such as for a box-shaped player that should stay upright.</summary>
    public bool LockRotation { get; }

    /// <summary>A dynamic body's mass in kilograms. Its inertia follows its shape.</summary>
    public float Mass { get; }

    /// <summary>A body moved by the simulation.</summary>
    public static Body Dynamic(
        float mass = 1f,
        float friction = 0.5f,
        float bounciness = 0f,
        float drag = 0.05f,
        float angularDrag = 0.05f,
        float gravityScale = 1f,
        bool lockRotation = false,
        bool continuous = false,
        uint collidesWith = Physics.AllLayers) =>
        new(BodyKind.Dynamic, mass, friction, bounciness, drag, angularDrag, gravityScale, lockRotation, continuous, collidesWith);

    /// <summary>The same body, bumping into other layers.</summary>
    public Body CollidingWith(uint layers) =>
        new(Kind, Mass, Friction, Bounciness, Drag, AngularDrag, GravityScale, LockRotation, Continuous, layers);

    /// <summary>The same body, as rough and bouncy as given.</summary>
    public Body WithSurface(float friction, float bounciness) =>
        new(Kind, Mass, friction, bounciness, Drag, AngularDrag, GravityScale, LockRotation, Continuous, CollidesWith);
}
