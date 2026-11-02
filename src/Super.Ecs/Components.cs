using System.Numerics;

namespace Super.Ecs;

/// <summary>
/// What an entity looks like. With a <see cref="Transform"/>, <see cref="ViewExtensions.Models"/>
/// draws it, keyed by the entity's ID. A color tints the model and keeps its textures.
/// </summary>
public record struct Model(ModelRef Value, Vector3? Color = null);

/// <summary>
/// Makes an entity move smoothly between ticks. Super remembers its transform before each
/// Update, and <see cref="ViewExtensions.Models"/> blends from there, so you never keep a
/// previous position yourself. An entity's first frame shows it exactly where it is.
/// </summary>
public struct Smoothed
{
    internal Transform Previous;
    internal float PreviousPhase;
    internal bool HasPrevious;
}

/// <summary>
/// Attaches an entity to another. Its own <see cref="Transform"/> then counts from the
/// parent's, so an axe in a hand is an entity with a Parent and a small offset.
/// </summary>
public record struct Parent(Entity Value);

/// <summary>
/// Ties an entity to a state, so it's destroyed on leaving that state. Anything without
/// this carries through. A parent's children aren't included, so tag them too.
/// </summary>
public record struct ScopedTo<TState>(TState State)
    where TState : struct, Enum;

/// <summary>
/// Animates the entity with an <see cref="Animator"/>. The game loop moves the animator
/// on every tick, so systems only say what the character is doing, and
/// <see cref="ViewExtensions.Models"/> draws its pose. Without a <see cref="Model"/>, it
/// draws the rig's own model; with one, the model must have been cooked with a skeleton
/// and the animator's rig must be its own.
/// </summary>
public record struct Animated(Animator Value);

/// <summary>
/// Bends the model in a wave from head to tail as it's drawn. The head must face +z.
/// Amplitude and BodyLength are in the model's own units, and Phase, in radians, should
/// rise steadily in Update. With <see cref="Smoothed"/>, the phase blends between ticks too.
/// </summary>
public record struct Swim(float Amplitude, float BodyLength, float Phase);
