# Physics

`Super.Physics` lets rays find entities by their shape: clicking a character's body
rather than the ground they stand on, or asking what a line of sight meets first. Give an
entity a `Collider`, and ask `Physics` what a ray hits. It's built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics), through JoltPhysicsSharp, and it
answers queries only for now: nothing falls, pushes or bounces yet.

It's an optional package, since it brings a native library (`joltc.dll`, copied beside
the game by the build). Reference `Super.Physics` and register it:

```csharp
services.AddSuperPhysics();   // Also registers the ECS if it isn't yet.
```

## Colliders

A `Collider` is an ECS component. It needs a `Transform`, and it moves, turns and scales
with it, following `Parent` too:

```csharp
var knight = world.Create();
world.Add(knight, Transform.At(position));
world.Add(knight, Collider.Capsule(height: 1.8f, radius: 0.4f, layers: Enemies));
```

| Shape | Made with | Where it sits |
| --- | --- | --- |
| Ball | `Collider.Ball(radius, offset)` | Centred on the offset |
| Box | `Collider.Box(size, offset)` | Its full size, centred on the offset, turned with the entity |
| Capsule | `Collider.Capsule(height, radius, offset)` | Upright, standing on the offset and reaching `height` up, end to end. It suits a character whose origin is at their feet |

The offset is in the entity's own space. A capsule's height must be at least twice its
radius; a collider that can't be built throws on the next sync, naming its entity.

**Layers.** A collider is on one or more of 16 layers, as bits, `Collider.DefaultLayer`
(bit 0) unless you say otherwise. Queries name the layers they look at, so a click can
look at enemies only, or skip the walls. `collider.OnLayers(bits)` gives the same shape on
other layers.

## Rays

```csharp
public void Update(GameTime time)
{
    var ray = input.MouseRay(camera);
    if (physics.Raycast(ray, layers: Enemies) is { } hit)
    {
        Target = hit.Entity;     // hit.Distance and hit.Point say where.
    }
}
```

| Call | What it does |
| --- | --- |
| `Raycast(ray, reach, layers)` | The nearest hit within `reach` metres (1000 by default) on those layers, or `null` |
| `RaycastAll(ray, hits, reach, layers)` | Every collider the ray meets, nearest first and once each, into a list you keep |
| `Sync()` | Moves the colliders to where their entities are now |
| `Count` | How many colliders there are, as of the last sync |

A ray that starts inside a collider hits it at distance 0. Hits at the same distance come
in entity order, so a seeded run repeats.

**When colliders move.** The game loop syncs every collider before each `Update`, so a
query in `Update` sees the world as it was last drawn. Entities moved during `Update` keep
their old place until the next tick, unless you call `physics.Sync()` yourself. An
entity destroyed, or a `Collider` taken away, is skipped at once and dropped at the
next sync.

**Repeating.** Jolt gives the same answers for the same calls with the same build on the
same machine. Super keeps to that by sorting hits rather than trusting Jolt's order.

## Limits

- Queries only: there are no forces, gravity, contacts or character controllers yet.
- No mesh or convex-hull colliders; build a shape from a ball, box or capsule.
- No shape casts or overlap tests, only rays.
- A collider's transform can't be sheared; a parent scaled unevenly under a turned child
  throws.
- Up to 65,536 colliders.
