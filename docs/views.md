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

A rigged model takes a pose from an `Animator`, which the game keeps as state and moves
on in `Update`. The pose keeps moving between ticks, so it's as smooth as the frame
rate:

```csharp
view.Model(FoxKey, _model, transform, _animator.Pose);
```

[Animation](animation.md) covers importing rigs, states, actions and blends.

## Planned

Attaching models to bones and particles aren't built yet. This is the intended shape:

```csharp
// Planned: not in Super yet.
var hand = view.Bone(colonist.Id, "hand_r");
view.Model(colonist.AxeId, axe, hand);

if (tree.FellAt is { } fellAt)
{
    view.Particles(tree.Id, dust, tree.Position, age: view.SecondsSince(fellAt));
}
```

Particles will be worked out from when they started rather than fired and forgotten, so
pausing and loading stay correct. `view.SecondsSince` already works for anything that
plays out from a moment in time.
