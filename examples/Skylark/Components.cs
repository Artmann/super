using System.Numerics;
using Super;

namespace SkylarkExample;

/// <summary>The bird the player hops around as.</summary>
/// <param name="Velocity">How fast it moves, kept from tick to tick so it can fall and jump.</param>
/// <param name="AirTime">Seconds since it last stood on something.</param>
/// <param name="Home">Where it goes back to after a fall.</param>
public record struct Hero(Vector3 Velocity, float AirTime, Vector3 Home, Entity? Ground = null);

/// <summary>A platform that glides between two places, there and back.</summary>
public struct Lift(TweenValue<Vector3> path)
{
    /// <summary>Where it is along its way. A field, so a query can update the tween itself and not a copy.</summary>
    public TweenValue<Vector3> Path = path;

    /// <summary>How fast it moved this tick, for whoever is standing on it.</summary>
    public Vector3 Velocity;
}

/// <summary>A gem to collect, hovering around a height.</summary>
public record struct Gem(float Height);

/// <summary>The beacon that ends a level, and the flame that lights once every gem is found.</summary>
public record struct Goal(Entity Flame);

/// <summary>A crate to push about, and where it goes back to after falling off.</summary>
public record struct Crate(Vector3 Home);
