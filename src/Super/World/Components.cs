using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// What an entity looks like. With a <see cref="Transform"/>, the world
/// draws it, keyed by the entity's ID. A color tints the model and keeps its textures. A mesh
/// mask from <see cref="ContentModels.Meshes"/> shows only some of its meshes; null shows them all. A fade
/// fades it out with distance from the camera, winning over its surfaces' own.
/// </summary>
public record struct Model(ModelRef Value, Vector3? Color = null, MeshMask? Meshes = null, DistanceFade? Fade = null);

/// <summary>
/// Where an entity was at the last tick, which drawing blends from so it moves smoothly. Every entity with a
/// <see cref="Transform"/> gets one, and Super copies the transform into it before each Update. Until the first copy
/// there's nothing to blend from, so a new entity's first frame shows it exactly where it is.
/// </summary>
internal struct LastTick
{
    internal Transform Previous;
    internal float PreviousPhase;
    internal bool HasPrevious;
}

/// <summary>
/// Attaches an entity to another. Its own <see cref="Transform"/> then counts from the
/// parent's, so a lamp on a cart is an entity with a Parent and a small offset. Name a
/// <paramref name="Bone"/> of an <see cref="Animated"/> parent's rig, such as "hand_l", to
/// count from that joint instead, following it between ticks as the model is drawn.
/// Destroying the parent destroys the child too.
/// </summary>
/// <param name="Value">The entity to count from.</param>
/// <param name="Bone">A joint of the parent's rig to count from, or null for the parent itself.</param>
public record struct Parent(Entity Value, string? Bone = null);

/// <summary>
/// Ties an entity to a state, so it's destroyed on leaving that state. Anything without
/// this carries through. Its children go with it, so only the entity at the top needs this.
/// </summary>
public record struct ScopedTo<TState>(TState State)
    where TState : struct, Enum;

/// <summary>
/// Animates the entity with an <see cref="Animator"/>. The game loop moves the animator
/// on every tick, so systems only say what the character is doing, and
/// the world draws its pose. Without a <see cref="Model"/>, it
/// draws the rig's own model; with one, the model must have been cooked with a skeleton
/// and the animator's rig must be its own.
/// </summary>
public record struct Animated(Animator Value);

/// <summary>
/// Bends the model in a wave from head to tail as it's drawn. The head must face +z.
/// Amplitude and BodyLength are in the model's own units, and Phase, in radians, should
/// rise steadily in Update. The phase blends between ticks, as the transform does.
/// </summary>
public record struct Swim(float Amplitude, float BodyLength, float Phase);

/// <summary>
/// Marks an entity's <see cref="Model"/> as one that never moves, so baked lighting can hold it.
/// The world draws it static, and throws if it also swims or is animated.
/// </summary>
public readonly record struct Static;

/// <summary>
/// Leaves an entity, and every entity whose <see cref="Parent"/> chain leads to it, out of the frame and the audio:
/// its model, lights and emitters aren't drawn and its sound fades out, as if those components were gone, until the
/// Hidden is taken away. The entity keeps its components and its place in queries.
/// </summary>
public readonly record struct Hidden;
