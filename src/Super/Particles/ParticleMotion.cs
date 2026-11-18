using System.Numerics;

namespace Super;

/// <summary>
/// The nodes for how an emitter's particles move: the speeds they start with and the forces
/// on them, which add up, then motions on top of the flight, which apply in the order they
/// were added.
/// </summary>
public static class ParticleMotion
{
    /// <summary>A speed every particle starts with, in metres per second.</summary>
    public static ParticleEmitter Velocity(this ParticleEmitter emitter, Vector3 velocity) =>
        emitter.Then(new VelocityNode(ParticleChecks.Finite(velocity, nameof(velocity))));

    /// <summary>Flings particles out from the middle of the shape at about this many metres per second, from half to one and a half times it.</summary>
    public static ParticleEmitter Outward(this ParticleEmitter emitter, float speed) =>
        emitter.Then(new OutwardNode(ParticleChecks.NotNegative(speed, nameof(speed))));

    /// <summary>Adds up to this many metres per second in a random direction.</summary>
    public static ParticleEmitter Scatter(this ParticleEmitter emitter, float speed) =>
        emitter.Then(new ScatterNode(ParticleChecks.NotNegative(speed, nameof(speed))));

    /// <summary>Shoots particles within a cone round a direction, this many radians either side of it, at about this speed.</summary>
    public static ParticleEmitter Cone(this ParticleEmitter emitter, Vector3 direction, float angle, float speed)
    {
        if (!(direction.LengthSquared() > 1e-8f) || !float.IsFinite(direction.LengthSquared()))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "A cone needs a direction to point in.");
        }

        if (!(angle >= 0f && angle <= MathF.PI))
        {
            throw new ArgumentOutOfRangeException(nameof(angle), angle, "A cone's angle must be from 0 to pi radians.");
        }

        return emitter.Then(new ConeNode(Vector3.Normalize(direction), angle, ParticleChecks.NotNegative(speed, nameof(speed))));
    }

    /// <summary>
    /// Slows particles down, per second. At 2, a particle has lost about 86% of its speed
    /// after one second. It also caps how fast gravity can move them.
    /// </summary>
    public static ParticleEmitter Drag(this ParticleEmitter emitter, float perSecond) =>
        emitter.Then(new DragNode(ParticleChecks.NotNegative(perSecond, nameof(perSecond))));

    /// <summary>A pull in metres per second, per second, in the world. A positive y rises, as warm air does.</summary>
    public static ParticleEmitter Gravity(this ParticleEmitter emitter, Vector3 pull) =>
        emitter.Then(new GravityNode(ParticleChecks.Finite(pull, nameof(pull))));

    /// <summary>Air moving this fast in the world. It needs <see cref="Drag"/> to catch particles; the more drag, the sooner they move with it.</summary>
    public static ParticleEmitter Wind(this ParticleEmitter emitter, Vector3 velocity) =>
        emitter.Then(new WindNode(ParticleChecks.Finite(velocity, nameof(velocity))));

    /// <summary>A smooth, wandering sway of up to about this many metres, changing about this many times a second.</summary>
    public static ParticleEmitter Wobble(this ParticleEmitter emitter, float metres, float perSecond = 1f) =>
        emitter.Then(new WobbleNode(ParticleChecks.NotNegative(metres, nameof(metres)), ParticleChecks.NotNegative(perSecond, nameof(perSecond))));

    /// <summary>Turns particles round the effect's upright axis through its middle, this many times a second. Negative turns the other way.</summary>
    public static ParticleEmitter Swirl(this ParticleEmitter emitter, float turnsPerSecond) =>
        emitter.Then(new SwirlNode(ParticleChecks.FiniteValue(turnsPerSecond, nameof(turnsPerSecond))));
}
