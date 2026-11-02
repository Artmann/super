# Drawing with views

A view is a function that takes some data and says how it looks. If you've used SwiftUI
or React, this will feel familiar. You don't create scene objects and keep them in sync
with your game. Each frame, `Draw` describes what's visible.

```csharp
struct Box
{
    public int Id;
    public Matrix4x4 Transform;
    public bool IsOpen;
}

class BoxView(Content content)
{
    readonly ModelRef box = content.Model("props/chest.rmodel");
    readonly ModelRef lid = content.Model("props/chest-lid.rmodel");

    public void Draw(in Box data, View view)
    {
        float open = view.Animate(data.Id, data.IsOpen ? 1f : 0f, seconds: 0.4f);
        view.Model(data.Id, box, data.Transform);
        view.Model(data.Id + 1, lid, Matrix4x4.CreateRotationX(-open * 1.8f) * data.Transform);

        if (view.Changed(data.Id, data.IsOpen))
        {
            // Play a creak here, once audio exists.
        }
    }
}
```

`IsOpen` is game state. `open` is view memory, which Super keeps for you under the key
you pass. The first time Super sees a key, the value goes straight to its target. So
when a player loads a save, open boxes are simply open, with no animation. When
`IsOpen` changes during play, the lid swings.

## What a view can draw

| Call | What it does |
| --- | --- |
| `view.Camera(position, lookAt, fieldOfView)` | A fixed camera. See [Cameras](cameras.md) for the rest |
| `view.Camera(camera)` | A `Camera` value the game keeps |
| `view.Sun(direction, color)` | A directional light. `color` is linear and can go past 1 |
| `view.Model(key, model, transform, color)` | A model. `color`, if given, tints it and keeps its textures |
| `view.Ghost(key, model, transform, color, opacity)` | A model in one colour that glows a little, as a [preview of where something will go](rendering.md#ghosts) |
| `view.Shadows(radius, cascades)` | How far shadows reach. See [Rendering](rendering.md#shadows) |
| `view.Water(settings)` | Puts the camera underwater. See [Rendering](rendering.md#water) |
| `view.Finish(settings)` | Exposure, contrast, vignette and other [post-processing](rendering.md#the-look-of-the-picture) |
| `view.Models(world)` | Every entity with a model, from [`Super.Ecs`](ecs.md#drawing-entities) |

Worlds in Super are flat: a list of things you draw, not a tree of nodes. To attach one
thing to another, multiply their transforms:

```csharp
view.Model(colonist.AxeId, axe, handOffset * colonist.Transform);
```

## View memory

| Call | What it gives |
| --- | --- |
| `view.Animate(key, target, seconds)` | Eases from where the value is to the target over that many seconds |
| `view.Changed(key, value)` | True on the one frame a value differs from last frame |
| `view.SecondsSince(tick)` | Seconds since a tick, blended smoothly between ticks |
| `view.Blend` | How far this frame is between the last tick and the next, from 0 to 1 |

`Animate` follows game time, so the frame rate doesn't change it. `Animate` and
`Changed` work with any number of things, and a running game allocates nothing for them
once each key has been seen.

Memory lasts as long as you keep drawing. If a frame doesn't ask for a key, Super
forgets it, so a box that comes back into view appears finished instead of replaying.
Don't call `Animate` only while something is moving, or it will never move.

View memory is never part of the simulation, so it can't change how a seed plays out.

## Keys

A key names something across frames, like a `key` in React. Super uses it for view
memory, and to tell the renderer where an object was last frame, which keeps moving
things crisp under temporal anti-aliasing. Use a stable ID from your game, and the same
one every frame.

View memory also belongs to the line of code asking, like the order of hooks in React.
So a lid and a latch on one box can both use the box's ID, and a box and a tree with the
same ID can't get mixed up. Two calls from one line with one key share their memory, so
give each its own key.

## Smooth motion

Things that move in `Update` advance in steps, so the view blends between the last two
ticks to keep motion smooth at any frame rate. Keep the previous value in your state and
let `Smooth` blend them:

```csharp
public void Update(GameTime time)
{
    ball.PreviousTransform = ball.Transform;
    ball.Transform = Step(ball, time.TickSeconds);
}

public void Draw(View view) =>
    view.Model(ball.Id, ballModel, view.Smooth(ball.PreviousTransform, ball.Transform));
```

With the ECS, the `Smoothed` component does this for you; see
[Drawing entities](ecs.md#drawing-entities).

## Things that rarely change

A city might have 50,000 walls. Describing them all 60 times a second is wasteful, so
wrap them in `Cached`. It runs your function again only when the version changes. The
key says which chunk this is, and your `Update` adds one to the version when a wall
moves:

```csharp
view.Cached(chunk.Id, chunk.Version, chunk, static (chunk, view) =>
{
    foreach (ref readonly var wall in chunk.Walls)
    {
        view.Model(wall.Id, wall.Model, wall.Transform);
    }
});
```

`Cached` keeps models and nothing else, so lights, the camera and anything that animates
go outside it. It saves your code describing them, but the models still reach the
renderer each frame. Pass a `static` lambda and the data as an argument, so nothing is
captured and nothing allocates.

## State or view?

If gameplay cares, it's state. If only the eyes care, it's view.

| Example | Where it lives | Why |
| --- | --- | --- |
| Lid of a box | View | Gameplay only needs open or closed |
| Door you can't pass until it's half open | State: an `OpenAmount` your `Update` moves | Gameplay depends on it |
| Ball that can hit things | State: position and velocity | It carries on mid-bounce after a load |
| Dust from a falling tree | View | Nothing depends on it |

Views read state and never write it, so animation can't change how a game turns out,
and a seed always replays the same.

## Animated models

A rigged model takes a pose from an `Animator`. The game sets its state in `Update`,
and the game loop moves it on each tick. Give the view the animator, and it draws the
rig's model in its current pose. The pose keeps moving between ticks, so it's as
smooth as the frame rate:

```csharp
view.Model(FoxKey, _animator, transform);
```

[Animation](animation.md) covers importing rigs, states, actions and blends.

## Particles

Dust, smoke and spray are soft, see-through puffs. You build them from nodes, the way
you would in Unity's or Unreal's particle editors, but in code: start a
`ParticleEmitter` with how many particles and how long they live, then chain the
steps. Draw it each frame with how long ago it started. Your `Update` saves the tick
when the tree falls; the view does the rest:

```csharp
static readonly ParticleEmitter Dust = new ParticleEmitter(count: 40, lifetime: 1.6f)
    .Over(0.15f)                            // born over 0.15 s, not all at once
    .FromLine()                             // along a line one metre long
    .Outward(2.2f)                          // billow out sideways off the line
    .Velocity(new Vector3(0f, 0.6f, 0f))    // and up a little
    .Drag(2.5f)
    .Gravity(new Vector3(0f, 0.25f, 0f))    // warm dust drifts upward
    .Size(0.5f, 1.8f)
    .Color(new Vector3(0.55f, 0.42f, 0.28f), new Vector3(0.5f, 0.45f, 0.38f))
    .Opacity(0.6f)
    .Fade(0.1f, 0.6f);

if (tree.FellAt is { } fellAt)
{
    float age = view.SecondsSince(fellAt);
    view.Particles(tree.Id, Dust, tree.Foot, tree.Crown, age - FallSeconds);
}
```

Nothing is simulated from frame to frame. Every particle's place, size and colour are
worked out from the age and the key, so pausing, loading, slow motion and `--record`
all show exactly the same dust. Use a different key for each burst, such as the tree's
ID, or every tree throws up the same pattern. Before the age is 0 nothing is drawn, so
you can pass a negative age to start a burst later, and once the age passes
`Duration` the call costs nothing.

There are three ways to place a burst:

| Call | Use it for |
| --- | --- |
| `view.Particles(key, effect, position, age)` | A puff at one spot |
| `view.Particles(key, effect, transform, age)` | A burst turned, tilted or stretched |
| `view.Particles(key, effect, from, to, age)` | Dust along a line, such as where a tree lands. `FromLine()` covers it end to end |

Spawn shapes and speeds are in the emitter's own space and follow the transform;
gravity, wind, sizes and colours stay in the world, so dust still rises and keeps its
size however the burst is turned.

### Nodes

A new emitter bursts all its particles at once from a point, a metre wide, white and
solid. Each call adds a node and gives back a new emitter, so a shared one never
changes and `Dust.WithCount(12)` is a smaller variation. The timing, the shape, the
count, the lifetime and the seed have one setting each, so calling one again replaces
it. Everything else stacks: speeds and forces add up, motions apply in turn, and
sizes, colours and opacities multiply.

| Stage | Call | What it does |
| --- | --- | --- |
| Settings | `WithCount(n)`, `WithLifetime(seconds, spread)`, `Seed(n)` | Particles from 1 to 32,768; how long each lives and how much that varies, from 0 to 0.9; a different pattern for every key |
| Birth | `Burst()` | All born at once. The default |
| | `Over(seconds)` | Births spread evenly over that long |
| | `Pulses(times, every)` | Shared between bursts that far apart, like puffs from a chimney |
| Shape | `FromPoint()` | All from the middle. The default |
| | `FromLine(length)` | Along the x axis. Outward speed throws them sideways off it |
| | `FromBox(size)`, `FromDisc(radius)`, `FromSphere(radius)` | Anywhere inside, evenly. A disc lies flat, such as round the foot of a tree |
| Speed | `Velocity(v)` | A speed every particle starts with, in metres per second |
| | `Outward(speed)` | Out from the middle of the shape, at 50% to 150% of the speed |
| | `Scatter(speed)` | Up to that speed in any direction |
| | `Cone(direction, angle, speed)` | Within a cone that many radians either side of the direction, like a fountain |
| Force | `Drag(perSecond)` | Slows them. At 2, a particle has lost 86% of its speed after a second |
| | `Gravity(pull)` | A pull in the world, in metres per second per second. A positive y rises |
| | `Wind(velocity)` | Moving air. It needs drag to catch them; the more drag, the sooner they go with it |
| Motion | `Wobble(metres, perSecond)` | A smooth wandering sway, like eddies in the air |
| | `Swirl(turnsPerSecond)` | Turns them round the upright axis through the middle, like a dust devil |
| Look | `Size(start, end, easing, spread)` | Width in metres over the life. The default `Easing.Out` grows fast, then slowly |
| | `Color(color)`, `Color(start, end)` | Linear colour like a model's, lit by the sun and sky |
| | `Gradient((0f, a), (0.3f, b), (1f, c))` | Colours at points through the life, blending between them |
| | `Opacity(amount)` | How solid the middle is at most, from 0 to 1. Overlapping puffs add up |
| | `Fade(in, out)` | The parts of the life, from 0 to 1, spent fading in and out |
| | `Flicker(amount, perSecond)` | Dips the opacity, each particle at its own beat |
| | `Spin(radiansPerSecond)` | Turns each at its own speed, up to that fast either way |

A setting out of range throws with the reason, and `ToString()` lists the nodes in
order, which helps when a puff doesn't look as expected.

### Effects

A `ParticleEffect` puts several emitters on a timeline under one key. Each starts after
its delay, moved by its offset in the effect's own space, and each gets its own pattern
from the key. The `Dust` example draws a whole tree fall with one call, from the stump
to where the crown lands. On a from/to line the offset's x stretches with the line, so
-0.5 is the start and 0.5 the end:

```csharp
static readonly ParticleEffect TreeFall = new ParticleEffect()
    .Add(StumpPuff, offset: new Vector3(-0.5f, 0f, 0f))  // at the stump, straight away
    .Add(GroundDust, delay: FallSeconds);                // along the trunk when it lands

view.Particles(key, TreeFall, tree.Foot, tree.Foot + direction * TrunkLength, since);
```

An emitter can be drawn on its own wherever an effect can. To share steps between
emitters, write an extension method; the `Dust` example gives its stump puff and its
ground dust the same `Billows` look this way:

```csharp
public static ParticleEmitter Billows(this ParticleEmitter emitter, float opacity) => emitter
    .Drag(2.5f)
    .Gravity(new Vector3(0f, 0.25f, 0f))
    .Color(tan, grey)
    .Opacity(opacity)
    .Fade(0.1f, 0.6f);
```

Build emitters and effects once, in `static readonly` fields, and drawing them
allocates nothing.

### How they look

Particles are lit by the sun and sky, shaded by shadows, fade into haze and melt
softly into the ground and anything they touch. They cast no shadow, and underwater
they fade with distance but miss the shafts of light. Glass drawn in front of them
covers them correctly, but glass behind a puff also covers it. `Cached` keeps only
models, so draw particles outside it. A frame holds at most 32,768 particles; more
are dropped. The `Dust` example drops trees in a clearing.

## Planned

Attaching models to bones isn't built yet. Until then, `animator.Joint(name)` times the
character's transform places a model on a bone by hand; see
[Animation](animation.md#joints). This is the intended shape:

```csharp
// Planned: not in Super yet.
var hand = view.Bone(colonist.Id, "hand_r");
view.Model(colonist.AxeId, axe, hand);
```
