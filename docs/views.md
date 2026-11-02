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

Dust, smoke and spray are soft, see-through puffs. Describe the burst once as a
`ParticleEffect`, then draw it each frame with how long ago it started. Your `Update`
saves the tick when the tree falls; the view does the rest:

```csharp
static readonly ParticleEffect Dust = new()
{
    Count = 40,
    Lifetime = 1.6f,
    EmitSeconds = 0.15f,
    Spawn = ParticleSpawn.Line(),
    Outward = 2.2f,                        // billow out sideways off the line
    Velocity = new Vector3(0f, 0.6f, 0f),  // and up a little
    Drag = 2.5f,
    Gravity = new Vector3(0f, 0.25f, 0f),  // warm dust drifts upward
    StartSize = 0.5f,
    EndSize = 1.8f,
    StartColor = new Vector3(0.55f, 0.42f, 0.28f),
    EndColor = new Vector3(0.5f, 0.45f, 0.38f),
    Opacity = 0.6f,
};

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
`effect.Duration` the call costs nothing.

There are three ways to place a burst:

| Call | Use it for |
| --- | --- |
| `view.Particles(key, effect, position, age)` | A puff at one spot |
| `view.Particles(key, effect, transform, age)` | A burst turned, tilted or stretched |
| `view.Particles(key, effect, from, to, age)` | Dust along a line, such as where a tree lands. A one-metre `Line` spawn covers it end to end |

Spawn shapes and speeds are in the effect's own space and follow the transform;
gravity, sizes and colours stay in the world, so dust still rises and keeps its size
however the burst is turned.

| Setting | What it does | Default |
| --- | --- | --- |
| `Count` | Particles in the burst, 1 to 32,768 | 24 |
| `Lifetime`, `LifetimeSpread` | Seconds each lives, and how much that varies as a fraction | 1.5, 0.3 |
| `EmitSeconds` | Spreads births over this long; 0 is one burst | 0 |
| `Spawn` | `ParticleSpawn.Point`, `Line(length)`, `Box(size)` or `Disc(radius)` | Point |
| `Velocity` | A speed every particle starts with, in metres per second | none |
| `Outward` | Speed out sideways from the middle of the shape | 0 |
| `Scatter` | Most speed in a random direction | 0 |
| `Drag` | How fast the air slows them, per second | 0 |
| `Gravity` | A pull in the world; a positive y rises | none |
| `StartSize`, `EndSize`, `SizeSpread` | Width in metres, growing fast then slowly | 0.3, 0.8, 0.3 |
| `StartColor`, `EndColor` | Linear colour like a model's, lit by the sun and sky | grey |
| `Opacity`, `FadeIn`, `FadeOut` | How solid at most, and the parts of life spent fading | 0.5, 0.1, 0.6 |
| `Spin` | Fastest turn either way, in radians per second | 0.5 |
| `Seed` | Changes the pattern for every key | 0 |

Setting something out of range throws with the reason. Effects are records, so
`Dust with { Count = 12, Spawn = ParticleSpawn.Disc(0.3f) }` makes a variation.

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
