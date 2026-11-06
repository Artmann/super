# Physics

`Super.Physics` gives entities solid shapes. Things fall, stack, bounce and get pushed;
characters walk into walls and up stairs; trigger areas notice who comes and goes; and
rays and shape casts find what's in the way. It's built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics), through JoltPhysicsSharp.

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
radius; a collider that can't be built throws on the next step, naming its entity.

**Layers.** A collider is on one or more of 16 layers, as bits, `Collider.DefaultLayer`
(bit 0) unless you say otherwise. Queries name the layers they look at, so a click can
look at enemies only, or skip the walls, and bodies name the layers they bump into.
`collider.OnLayers(bits)` gives the same shape on other layers.

## Bodies

A collider on its own is **kinematic**: it goes where its Transform says, and pushes
anything dynamic out of the way, like a door or a moving platform. Add a `Body` to say
otherwise:

```csharp
world.Add(floor, Collider.Box(new Vector3(40f, 1f, 40f)));
world.Add(floor, Body.Static);

world.Add(crate, Collider.Box(Vector3.One));
world.Add(crate, Body.Dynamic(mass: 20f, friction: 0.6f));
```

| Body | What moves it | Use it for |
| --- | --- | --- |
| No `Body`, or `Body.Kinematic` | Your code, through its Transform. It sweeps to the new place, pushing dynamic bodies | Doors, lifts, platforms, a character's own body |
| `Body.Static` | Nothing. Moving it teleports it | Floors, walls and scenery; the cheapest kind |
| `Body.Dynamic(...)` | The simulation: gravity, collisions and your pushes. Physics writes its Transform | Crates, balls, rubble, things that are thrown |

`Body.Dynamic` takes:

| Setting | Default | What it does |
| --- | --- | --- |
| `mass` | 1 | Kilograms. Inertia follows from the shape |
| `friction` | 0.5 | How rough it is, from 0 (ice) up. Two bodies use the square root of their two multiplied |
| `bounciness` | 0 | How much speed a bounce keeps, from 0 to 1. Two bodies use the larger |
| `drag`, `angularDrag` | 0.05 | How much moving and turning fade each second |
| `gravityScale` | 1 | 0 floats, 2 falls twice as hard |
| `lockRotation` | false | Never turns, such as a box-shaped player that must stay upright |
| `continuous` | false | Sweeps along its path so a small, fast body can't pass through a thin wall. Costs more |
| `collidesWith` | every layer | The layers it bumps into. Both bodies must say yes to collide |

`body.CollidingWith(bits)` and `body.WithSurface(friction, bounciness)` change one part of
a body, static and kinematic ones too.

**Moving dynamic bodies.** Physics owns a dynamic body's Transform, so it can't have a
`Parent`. Setting its Transform yourself teleports it, keeping its speed. Otherwise, push
it:

| Call | What it does |
| --- | --- |
| `physics.Push(entity, impulse)` | A sudden push in newton-seconds: the velocity changes by impulse over mass. Kicks, hits, explosions |
| `physics.Push(entity, impulse, point)` | The same, at a point in the world, so it spins when the point is off centre |
| `physics.AddForce(entity, force)` | A steady push in newtons through the next step. Call it every tick for thrusters or wind |
| `physics.AddTorque(entity, torque)` | A twist through the next step |
| `physics.SetVelocity(entity, velocity)` | Sets it outright, in metres per second |
| `physics.SetAngularVelocity(entity, spin)` | Sets the spin, as an axis whose length is radians per second |
| `physics.Velocity(entity)`, `physics.AngularVelocity(entity)` | How fast any body moves and turns |

Only dynamic bodies can be pushed; pushing anything else throws, naming the entity.

**Gravity.** `physics.Gravity` pulls every dynamic body, `Physics.EarthGravity`
(9.81 m/s² down) to start.

## Characters

A `Character` walks with `physics.Move`. It slides along walls, climbs steps, keeps to the
ground going down slopes and stairs, can't walk up slopes that are too steep, and pushes
crates. Its shape is its collider, usually a capsule on its feet:

```csharp
world.Add(player, Transform.At(start));
world.Add(player, Collider.Capsule(height: 1.8f, radius: 0.4f));
world.Add(player, new Character(MaxSlope: 45f, StepHeight: 0.35f));
```

`Move` walks it at once, writes where it ends up to its Transform, and says how it went.
Gravity and jumping are yours, so they feel the way your game wants. Keep the vertical
speed from one tick to the next:

```csharp
Vector3 velocity;
bool grounded;

public void Update(GameTime time)
{
    var walk = actions.Vector(Control.Move) * 5f;
    velocity.Y = grounded ? 0f : velocity.Y - (20f * time.DeltaSeconds);
    if (grounded && actions.Pressed(Control.Jump))
    {
        velocity.Y = 6f;
    }

    var move = physics.Move(player, new Vector3(walk.X, velocity.Y, walk.Y), time.DeltaSeconds);
    velocity = move.Velocity;
    grounded = move.Grounded;
}
```

| `Character` setting | Default | What it does |
| --- | --- | --- |
| `MaxSlope` | 50 | The steepest ground it stands on and walks up, in degrees |
| `StepHeight` | 0.3 | The highest step it climbs, and how far down it stays on the ground, in metres |
| `CollidesWith` | every layer | The layers that block it |
| `Mass` | 70 | How hard it pushes dynamic bodies, in kilograms |

| `CharacterMove` | What it says |
| --- | --- |
| `Position` | Where it ended up, as written to its Transform |
| `Velocity` | How fast it really moved, after walls and slopes |
| `Footing` | `Ground`, `SteepGround` (it slides), `Unsupported` (on an edge) or `InAir` |
| `Grounded` | `Footing` is `Ground` |
| `GroundNormal`, `Ground` | Which way the ground faces, and its entity |

A character can't have a `Parent` or a dynamic `Body`. Setting its Transform teleports it.
Its collider is also a kinematic body, so rays and triggers find it and falling crates
land on it.

## Triggers

Add a `Trigger` to a collider to make it an area that notices what comes in and goes out,
rather than a solid shape. Nothing bumps into it, and queries pass through it:

```csharp
world.Add(goal, Collider.Box(new Vector3(4f, 3f, 4f)));
world.Add(goal, new Trigger(Detects: Players));

public void Update(GameTime time)
{
    foreach (var entered in physics.Entered)
    {
        if (entered.Trigger == goal)
        {
            Win(entered.Other);
        }
    }
}
```

| Call | What it gives |
| --- | --- |
| `physics.Entered` | Who came into a trigger in the last step, as `(Trigger, Other)` pairs |
| `physics.Exited` | Who left one in the last step |
| `physics.Inside(trigger)` | Who's in it now |

Each lists in trigger order, then entity order, so a seeded run repeats. Triggers notice
colliders on their `Detects` layers (every layer by default), but never other triggers.
An entity destroyed while inside leaves, so check `world.IsAlive` before using it; a
trigger that's removed reports no one leaving. Triggers are checked after each step, so a
character moved in Update is noticed on the next tick.

## Queries

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
| `ShapeCast(shape, from, direction, reach, rotation, layers)` | Sweeps a collider shape, a thick ray, and gives the first hit with its point and surface normal, or `null` |
| `ShapeCastAll(shape, from, direction, hits, reach, rotation, layers)` | Every collider the sweep meets, nearest first |
| `Overlap(shape, position, hits, rotation, layers)` | Every entity whose collider touches the shape there, in entity order |
| `Sync()` | Moves the colliders to where their entities are now |
| `Count` | How many colliders there are, as of the last sync |

A shape cast answers questions a ray can't: will a ball fit down this corridor, where
does a falling crate land, how far can the [camera boom](cameras.md#third-person) reach.
An overlap finds what an explosion reaches, or whether there's room to stand up. The
shape's offset counts from `from` or `position`, as on an entity, and `rotation` turns
it. A ray or shape that starts inside a collider hits it at distance 0. Hits at the same
distance come in entity order. Queries pass through triggers.

**When things move.** Before each `Update` the game loop steps the world by
`time.DeltaSeconds`: colliders move to where their entities are, dynamic bodies move on
and their Transforms are written, then triggers are checked. So `Update` sees the world as
it was last drawn. Entities moved during `Update` keep their old place for queries until
the next tick, unless you call `physics.Sync()`, which makes them jump there rather than
push. An entity destroyed, or a `Collider` taken away, is skipped by queries at once and
dropped at the next step. When `time.Scale` is 0 nothing moves.

A step longer than 1/60 s is split, so a game at 30 ticks a second simulates two steps
per tick. With a `Physics` you make yourself, such as in a test, call
`physics.Step(seconds)` to move it on.

**Repeating.** Jolt gives the same answers for the same calls with the same build on the
same machine, and Super keeps to that by sorting hits and events rather than trusting
Jolt's order. A pile of 60 dropped boxes lands the same way every run of a seed.

## Limits

- No mesh, convex-hull or height-field colliders; build a shape from balls, boxes and
  capsules.
- No joints, ragdolls or vehicles yet.
- No contact events between solid bodies, such as for a sound when a crate lands. Use a
  trigger, or watch a body's velocity.
- A collider's transform can't be sheared; a parent scaled unevenly under a turned child
  throws.
- Results repeat on one machine and build, not across machines.
- Up to 65,536 colliders.
- Games running in parallel, such as tests, take turns to step, because Jolt's native
  library shares one scratch allocator between physics worlds.
