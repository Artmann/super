using System.Numerics;
using Super.Ecs;

namespace Super.Physics;

/// <summary>
/// Makes an entity walk with <see cref="Physics.Move"/>: it slides along walls, climbs steps, stays on the ground
/// going down slopes and stairs, and pushes dynamic bodies. Its shape is its <see cref="Collider"/>, usually a
/// capsule standing on its feet. It needs a Transform and no <see cref="Parent"/>, and can't also have a dynamic
/// <see cref="Body"/>.
/// </summary>
/// <param name="MaxSlope">The steepest ground it can stand and walk up, in degrees.</param>
/// <param name="StepHeight">The highest step it climbs without jumping, and how far down it keeps to the ground, in metres.</param>
/// <param name="CollidesWith">The layers whose colliders block it, as bits.</param>
/// <param name="Mass">How heavy it is when it pushes dynamic bodies, in kilograms.</param>
public readonly record struct Character(
    float MaxSlope = 50f,
    float StepHeight = 0.3f,
    uint CollidesWith = Physics.AllLayers,
    float Mass = 70f)
{
    /// <summary>A character with the defaults; without this, <c>new Character()</c> would be all zeros.</summary>
    public Character()
        : this(50f, 0.3f, Physics.AllLayers, 70f)
    {
    }
}

/// <summary>What a character stands on.</summary>
public enum Footing
{
    /// <summary>In the air, touching nothing below.</summary>
    InAir,

    /// <summary>On ground it can stand on.</summary>
    Ground,

    /// <summary>On a slope steeper than its <see cref="Character.MaxSlope"/>, so it slides down.</summary>
    SteepGround,

    /// <summary>Touching something below that doesn't hold it up, such as the edge of a ledge.</summary>
    Unsupported,
}

/// <summary>
/// Where a <see cref="Physics.Move"/> left a character. <see cref="Velocity"/> is what it really moved at, after
/// walls and slopes; keep its Y for gravity and jumps.
/// </summary>
public readonly record struct CharacterMove(
    Vector3 Position,
    Vector3 Velocity,
    Footing Footing,
    Vector3 GroundNormal,
    Entity? Ground)
{
    /// <summary>Whether it stands on ground it can walk on.</summary>
    public bool Grounded => Footing == Footing.Ground;
}
