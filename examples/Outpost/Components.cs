using Super;

namespace OutpostExample;

/// <summary>An enemy on the road: what kind, how much health it has left, and how far it has walked.</summary>
/// <param name="Offset">How far to the side of the road's middle it walks, so a line of enemies doesn't look like a train.</param>
public record struct Enemy(EnemyKind Kind, int Health, float Distance, float Offset);

/// <summary>A tower on a tile, and the seconds until it can shoot again.</summary>
public record struct Tower(int X, int Z, float Reload);

/// <summary>A shot in the air, chasing the enemy it was fired at.</summary>
public record struct Shot(Entity Target);
