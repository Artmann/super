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
    readonly ModelRef box = content.Model("props/chest.glb");
    readonly ModelRef lid = content.Model("props/chest-lid.glb");

    public void Draw(in Box data, View view)
    {
        float open = view.Animate(data.Id, data.IsOpen ? 1f : 0f, seconds: 0.4f);
        view.Model(data.Id, box, data.Transform);
        view.Model(data.Id + 1, lid, Matrix4x4.CreateRotationX(-open * 1.8f) * data.Transform);
    }
}
```

`IsOpen` is game state. `open` is view memory, which Super keeps for you under the key
you pass. The first time Super sees a key, the value goes straight to its target. So
when a player loads a save, open boxes are simply open, with no animation. When
`IsOpen` changes during play, the lid swings.

A view only describes how things look, so sounds don't go in `Draw`. The creak belongs
where `IsOpen` changes, in `Update`, which runs once per tick whether or not the box is
on screen, and which a test can check without drawing:

```csharp
public void Open(ref Box box, Audio audio)
{
    box.IsOpen = true;
    audio.Play(creak, at: box.Transform.Translation);
}
```

(`Changed` is view memory, which only lasts while you keep drawing, so a creak
tied to it would be skipped for a box that is off screen.) For a sound that plays for as
long as something exists, such as a humming machine, put a [`Sound`](ecs.md#sounds-from-entities)
component on its entity instead. See [Audio](audio.md).

## What a view can draw

| Call | What it does |
| --- | --- |
| `view.Camera(position, lookAt, fieldOfView)` | A fixed camera. See [Cameras](cameras.md) for the rest |
| `view.Camera(camera)` | A `Camera` value the game keeps |
| `view.Sun(direction, color, shadows)` | A directional light. `color` is linear and can go past 1. The first sun casts shadows unless `shadows: true` picks another |
| `view.PointLight(key, position, color, range, shadows, radius, volumetric)` | A light shining every way, like a candle or a torch. See [Point and spot lights](rendering.md#point-and-spot-lights) |
| `view.SpotLight(key, position, direction, color, range, innerAngle, outerAngle, shadows, radius, volumetric)` | A light shining in a cone, like a lantern's beam |
| `view.Model(key, model, transform, color)` | A model. `color`, if given, tints it and keeps its textures. `ModelRef.Cube`, `Sphere`, `Capsule`, `Cylinder`, `Cone`, `Plane`, `Quad` and `Torus` are [built in](rendering.md#built-in-shapes) |
| `view.Model(key, model, transform, meshes)` | Only the meshes a `MeshMask` shows, such as a body without its helmet. See [Showing some meshes](#showing-some-meshes) |
| `view.Decal(key, box, material, blend)` | Grime, damp or a stain projected onto whatever is inside a box. Up to 256 a frame. See [Decals](#decals) |
| `view.Ghost(key, model, transform, color, opacity)` | A model in one colour that glows a little, as a [preview of where something will go](rendering.md#ghosts) |
| `view.Shadows(radius, cascades, sunSize, contact)` | How far shadows reach, how soft they are, and how far contact shadows look where things meet the ground. See [Rendering](rendering.md#shadows) |
| `view.AmbientOcclusion(radius, strength)` | How far corners and creases reach out to darken the soft light, and how dark. See [Rendering](rendering.md#ambient-occlusion) |
| `view.LightProbes(min, max, spacing)` | A box of light probes that gives what's inside light bounced off the walls and ground. See [Rendering](rendering.md#bounced-light) |
| `view.ReflectionProbe(key, position, boxMin, boxMax, blend)` | A photograph of the scene from one spot, which shiny surfaces and glass in its box reflect. Up to 4 a frame. See [Rendering](rendering.md#reflections) |
| `view.Water(settings)` | Puts the camera underwater. See [Rendering](rendering.md#water) |
| `view.Puddles(level, band)` | Lets water stand below `level` on materials that ask for puddles, damp for `band` metres above. See [Rendering](rendering.md#puddles) |
| `view.Wind(direction, strength, gustiness)` | A breeze that sways grass and leaves on materials that ask for wind, leaning grass 1 m tall `strength` metres downwind, with gusts from 0 for steady to 1. See [Rendering](rendering.md#wind) |
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

`Animate` is for looks. A move that is part of the game, such as a building rising before
it works, is a [tween](tweens.md) kept in the game's state.

View memory is never part of the simulation, so it can't change how a seed plays out.

## Keys

A key names something across frames, like a `key` in React. Super uses it for view
memory, and particles use it as their seed. A test can find a model draw by its key in
`run.Drawn`. Use a stable ID from your game, and the same one every frame.

The renderer doesn't read model keys, so two models that share a key draw fine. Keys
don't need to be unique across a game, and there's no limit on how many you use. A
grid of tiles can pass the tile's index, and a building and its ghost can both pass the
building's ID.

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

To draw a pose on a model without an animator, pass the model and the pose. Both forms
take a mesh mask too, [see below](#showing-some-meshes):

```csharp
view.Model(FoxKey, foxModel, transform, pose);
view.Model(FoxKey, _animator, transform, meshes);
view.Model(FoxKey, foxModel, transform, pose, meshes);
```

[Animation](animation.md) covers importing rigs, states, actions and blends.

## Showing some meshes

A model cooked from a file with several meshes can be drawn in part: one body, the
helmet off, the sword in the other hand. Make a `MeshMask` once with `Content.Meshes`,
naming the meshes to show, and keep it. A static model has mesh names only with
`"keepMeshes": true` in its sidecar, see [Models](rendering.md#showing-some-meshes-of-a-model).
Each draw then shows only those meshes:

```csharp
MeshMask knight = content.Meshes(armoured, "body", "legs");
view.Model(KnightKey, armoured, transform, knight);
```

With entities, give the `Model` component the mask, and `view.Models(world)` draws with it:

```csharp
world.Add(knight, new Model(armoured, Meshes: knightMeshes));
```

A mask belongs to the model it was made for. Drawing it with another model throws, and
the message says which model the mask was made for. A mask never changes, so a running
game draws with it without allocating.

- Meshes the mask leaves out are not drawn, and cost no triangles. The profiler counts
  each mask's copies apart, by the triangles they show.
- A draw with a pose or an animator still skins the whole skeleton once, however few
  meshes it shows.
- A ghost takes a mask too, as `view.Ghost(key, model, transform, meshes, color, opacity)`.
- `run.Drawn` entries carry the mask as `Meshes`, or null for a draw that shows every
  mesh. See [Testing](testing.md).

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
| | `Color(color)`, `Color(start, end)` | Linear colour like a model's, lit by the sun, the sky and point and spot lights |
| | `Gradient((0f, a), (0.3f, b), (1f, c))` | Colours at points through the life, blending between them |
| | `Opacity(amount)` | How solid the middle is at most, from 0 to 1. Overlapping puffs add up |
| | `Fade(in, out)` | The parts of the life, from 0 to 1, spent fading in and out |
| | `Flicker(amount, perSecond)` | Dips the opacity, each particle at its own beat |
| | `Spin(radiansPerSecond)` | Turns each at its own speed, up to that fast either way |
| | `Glow(strength)` | Shines with its own colour times this, like a flame or a spark. Past 1 it blooms |

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

Particles are lit by the sun, the sky and point and spot lights, shaded by the sun's
shadows, sit in the fog, fade into haze and melt softly into the ground and anything they touch. A
glowing particle gives off its own light on top, but doesn't light what's round it: put
a `view.PointLight` at a fire for that. They cast no shadow, and underwater
they fade with distance but miss the shafts of light. They're sorted with
[glass](rendering.md#glass), so a puff in front of a pane covers it and one behind is
seen through it. `Cached` keeps only
models, so draw particles outside it. A frame holds at most 32,768 particles; more
are dropped. The `Dust` example drops trees in a clearing.

## Decals

A decal lays a material onto whatever is already drawn inside a box: damp rising up a
wall, a puddle stain on a floor, rust run down from a bolt. The box's matrix places, turns
and sizes it, and the material is projected along the box's own −Y, so its +Y should look
out of the surface. The material's U runs along the box's X and its V runs against its Z,
each across the whole box, from −0.5 to 0.5 in the box's own units. Its normal map reads
as it does on a mesh, with green pointing the way V falls, towards the box's +Z, and a
mirrored box reads it the same way. A material graph is the usual material, loaded on its
own with `content.Surface`:

```csharp
Surface damp = content.Surface("materials/grime.material.cs");

// A metre wide, half a metre high, on a wall that faces +Z. Its +Y looks out of the wall,
// and its +Z points down, so V is 0 at the bottom edge.
Matrix4x4 box = new(
    1f, 0f, 0f, 0f,
    0f, 0f, 0.2f, 0f,
    0f, -0.5f, 0f, 0f,
    2f, 0.6f, -3.98f, 1f);

view.Decal(DampKey, box, damp);
```

The material's opacity fades the decal: a graph's `Opacity`, or the alpha of a colour
texture. Where it shows, the decal blends its colour over the surface, makes it as rough
as its own roughness, and lays its bumps over the surface's own, so a brick wall's edges
and mortar still show through the damp. Lighting, shadows, ambient occlusion and
reflections all see the result. It also fades where the surface turns away from the box's
+Y, so a box over a corner stains the wall and not the floor below it, and over the last
fifth of the box's depth at either end.

`DecalBlend` sets how much of each part goes on, each from 0 to 1:

| Field | Default | What it does |
| --- | --- | --- |
| `Color` | 1 | How much of the decal's colour covers the surface's |
| `Normal` | 1 | How much of its bumps are laid on. 0 keeps the surface's bumps alone |
| `Roughness` | 1 | How far the surface's roughness moves to the decal's. A wet stain is smoother, a dusty one rougher |
| `AngleFade` | 0.5 | How far the surface can turn from the box's +Y before the decal fades. It's gone where the cosine of the angle is below this, and whole halfway from there to facing +Y. At 0.5 it fades from about 41° and is gone at 60°. At 0 it fades from 60° and is gone only where the surface is side-on |

```csharp
// Darkens the bricks but keeps them their own roughness.
view.Decal(SootKey, box, soot, new DecalBlend { Roughness = 0f });
```

A surface's `Color` tints the decal's colour, so `damp with { Color = new Vector3(0.6f) }`
lays the same stain darker. Each different surface gets a material of its own the first
time a decal uses it. Make them once in `Load` rather than a new one each frame: past 512,
the ones the frame didn't use are let go and made again when next used.

A view refuses, with a message naming what's wrong:

- more than 256 decals in a frame;
- a key used twice in a frame;
- a box squashed flat on any axis, or holding a number that isn't finite, since it can't be
  inverted;
- a `DecalBlend` field outside 0 to 1;
- a surface with a [surface shader](rendering.md#surface-shaders), since a decal draws only
  textures;
- a surface naming a texture its content doesn't have.

[`Graphics.Decals`](rendering.md#graphics-settings) turns them all off, and
`Graphics.Fastest()` does too. The Courtyard lays its damp, the dirty water under its sill
and the rust under its anchor plates as decals. D turns them off and on there.

![The foot of the Courtyard's brick wall under the window, before and after. On the left the damp is a dithered quad on the wall and shows only faintly. On the right it is a decal that darkens the bricks and the mortar above the plinth and fades upward in streaks, while the bricks' edges still show through](media/decals.webp)

Decals have some limits:

- Where two decals overlap, both colours show, but the later one's bumps and roughness
  replace the earlier one's.
- A decal can't change how metallic a surface is or make it glow.
- A decal's material uses its colour, normal and surface textures, and its colour,
  roughness and opacity. Its detail textures, height map and world variation are ignored.
- On strongly bumped surfaces, the fade by angle follows the bumps, so near the limit it
  fades in specks.
- Probes see decals, but moving or adding one doesn't photograph them again.

## Planned

Attaching models to bones isn't built yet. Until then, `animator.Joint(name)` times the
character's transform places a model on a bone by hand; see
[Animation](animation.md#joints). This is the intended shape:

```csharp
// Planned: not in Super yet.
var hand = view.Bone(colonist.Id, "hand_r");
view.Model(colonist.AxeId, axe, hand);
```
