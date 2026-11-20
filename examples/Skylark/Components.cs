using System.Numerics;
using Super;

namespace SkylarkExample;

/// <summary>The bird the player hops around as.</summary>
/// <param name="Home">Where it goes back to after a fall.</param>
public record struct Hero(Vector3 Home);

/// <summary>A platform that glides between two places, there and back.</summary>
public struct Lift(TweenValue<Vector3> path)
{
    /// <summary>Where it is along its way. A field, so a query can update the tween itself and not a copy.</summary>
    public TweenValue<Vector3> Path = path;
}

/// <summary>A gem to collect, hovering around a height.</summary>
public record struct Gem(float Height);

/// <summary>The beacon that ends a level, and the flame that lights once every gem is found.</summary>
/// <param name="Reached">The bird got to the lit beacon, so the game should move on.</param>
public record struct Goal(Entity Flame, bool Reached = false);

/// <summary>A crate to push about, and where it goes back to after falling off.</summary>
public record struct Crate(Vector3 Home);
