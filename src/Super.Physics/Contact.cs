using System.Numerics;
using Super.Ecs;

namespace Super.Physics;

/// <summary>Two entities that started touching. From <see cref="Physics.Contacts"/>.</summary>
/// <param name="Entity">One of them, the one that comes first by id.</param>
/// <param name="Other">The other.</param>
/// <param name="Point">Where they touch, in the world.</param>
/// <param name="Normal">The way out of <paramref name="Entity"/> and into <paramref name="Other"/>, one unit long.</param>
/// <param name="Speed">How fast they came together along the normal, in metres per second.</param>
public readonly record struct Contact(Entity Entity, Entity Other, Vector3 Point, Vector3 Normal, float Speed);
