# Drawing by hand

Most of a game's picture is [entities in its world](ecs.md#drawing-entities): the camera,
the lights, the sky and every model draw themselves, so a game made of entities leaves
`Draw` out. This page is about the rest, drawn by hand in `Draw`: data that isn't an
entity, and what only one frame shows, such as a flash, a pulse or a burst of dust.

`Draw` takes a view, a function that takes some data and says how it looks. If you've
used SwiftUI or React, this will feel familiar. Each frame, `Draw` describes what it
adds, and Super remembers nothing of it into the next frame but the view memory you ask
for by key.

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
| `view.Camera(camera)` | Takes this frame from another camera than the world's [camera entity](cameras.md#a-camera-entity). Most games only need the entity |
| `view.Model(key, model, transform, color)` | A model. `color`, if given, tints it and keeps its textures. `ModelRef.Cube`, `Sphere`, `Capsule`, `Cylinder`, `Cone`, `Plane`, `Quad` and `Torus` are [built in](rendering.md#built-in-shapes) |
| `view.Model(key, model, transform, new ModelOptions { Meshes = meshes })` | Only the meshes a `MeshMask` shows, such as a body without its helmet. See [Showing some meshes](#showing-some-meshes) |
| `view.Model(key, model, transform, new ModelOptions { Color = color, Static = true })` | A model that never moves, whose lighting will be baked. See [Static draws](#static-draws) |
| `view.Model(key, model, transform, new ModelOptions { Fade = new DistanceFade(50, 60) })` | A model that fades out between 50 and 60 metres from the camera, such as grass. See [Fading with distance](rendering.md#fading-with-distance) |
| `view.Model(key, model, transform, new Ghost(color, opacity))` | A model in one colour that glows a little, as a [preview of where something will go](rendering.md#ghosts) |
| `view.TransformOf(entity)` | Where an entity is drawn this frame, so something drawn by hand can sit on it. The world draws its own [entities](ecs.md#drawing-entities) before `Draw` |

`ModelOptions` holds everything a model draw can take beyond a colour: `Color`, `Swim` for a
`SwimMotion` that bends a fish as it swims, `Meshes`, `Static`, `Fade` and `Grass`, which grows [grass](rendering.md#grass) over it. Leave out what you don't need.

The suns, point and spot lights, shadows, ambient occlusion, screen bounce, light probes,
baked lighting, sky, water, puddles, mirror plane, wind, finished look, outlines, decals, fog volumes, reflection probes and baked pieces are components in the
[world](ecs.md#drawing-entities), such as `world.Spawn(new Wind(direction, 0.35f))`, and the
calls below set them for one frame when drawing by hand. The view still takes
`view.Lights.Point` and `view.Lights.Spot` for a light that only one frame shows, such as
a muzzle flash. The rest of what a frame shows is set through parts of the view:

| Call | What it does |
| --- | --- |
| `view.Indirect.ScreenBounce(strength, radius)` | How much light surfaces on screen pass on to the surfaces near them, and how far it reaches. See [Rendering](rendering.md#light-bounced-across-the-screen) |
| `view.Indirect.LightProbes(min, max, spacing)` | A box of light probes of your own, finer than the ones the renderer keeps round the camera, whose probes win inside it. `--debug-view probes` shows its probes and those of the finest box round the camera, not the coarser ones. See [Rendering](rendering.md#bounced-light) |
| `view.Indirect.BakedLighting(name, min, max, probeSpacing, texelsPerMetre)` | Names the one place this frame whose lighting is baked, with lightmaps of `texelsPerMetre` texels a metre (20 unless given). `--bake` bakes it, and once its bake is fresh its surfaces are lit from their lightmap, and anything moving inside it from the bake's probes before the live ones. See [Baked lighting](rendering.md#baked-lighting) |
| `view.Indirect.BakedChunk(name, transform, min, max, texelsPerMetre, edgeBlend)` | Places a piece of the world that is baked once and reused wherever it stands, each copy relit by the frame's sky and suns. Up to 256 a frame. `edgeBlend`, 0.5 metres by default, blends the chunk's light into the live light over that far in from its four sides; 0 gives a hard edge. See [Baked lighting](rendering.md#baked-lighting) |
| `view.Indirect.ReflectionProbe(key, position, boxMin, boxMax, blend, priority, update, version)` | A photograph of the scene from one spot, which shiny surfaces and glass in its box reflect, taken during play without stopping the game. Up to 64 a frame; where boxes overlap, the higher priority, then the smaller box, goes first. `update` says when it's taken again: when the light changes (the default), when `version` changes, or always. See [Rendering](rendering.md#reflections) |
| `view.Surroundings.Water(settings)` | Puts the camera underwater. See [Rendering](rendering.md#water) |
| `view.Surroundings.Puddles(level, band)` | Lets water stand below `level` on materials that ask for puddles, damp for `band` metres above. See [Rendering](rendering.md#puddles) |
| `view.Surroundings.ReflectionPlane(height)` | A level mirror at `height`: smooth surfaces lying in it reflect the scene drawn from under it, sharp even for what the screen doesn't show. Puddles set one at their level by themselves. See [Rendering](rendering.md#planar-reflections) |
| `view.Surroundings.Wind(direction, strength, gustiness)` | A breeze that sways grass and leaves on materials that ask for wind, leaning grass 1 m tall `strength` metres downwind, with gusts from 0 for steady to 1. See [Rendering](rendering.md#wind) |
| `view.Effects.Particles(key, effect, position, age)` | A burst of particles, such as dust or smoke. See [Particles](#particles) |
| `view.Effects.Decal(key, box, material, blend)` | Grime, damp or a stain projected onto whatever is inside a box. Up to 256 a frame. See [Decals](rendering.md#decals) |
| `view.Effects.FogVolume(key, shape, transform, settings)` | A box, ellipsoid, cylinder or cone filled with fog of its own, such as mist in a room or a lamp's beam. Up to 64 a frame. See [Fog volumes](rendering.md#fog-volumes) |

`view.Indirect` holds the light that bounces, live, in probes or baked; `view.Surroundings`
the water, puddles, mirror plane and wind; and `view.Effects` particles, decals and fog volumes.

What you draw by hand is flat: a list of things, not a tree of nodes. To attach one
thing to another, multiply their transforms. In the world, a [`Parent`](ecs.md#drawing-entities)
does this for you:

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

Two models that share a key draw fine. Keys don't need to be unique across a game, and
there's no limit on how many you use. A grid of tiles can pass the tile's index, and a
building and its ghost can both pass the building's ID. The renderer uses a model's key
to find where it stood last frame, for [motion vectors](rendering.md#motion-vectors), so
a model that moves keeps a sharp edge if its key is its own. Models that share a key and
stand in different places look as they would without motion vectors: a little softer
while they move.

View memory also belongs to the line of code asking, like the order of hooks in React.
So a lid and a latch on one box can both use the box's ID, and a box and a tree with the
same ID can't get mixed up. Two calls from one line with one key share their memory, so
give each its own key.

Entities in the world are drawn with their entity's id as the key. Decals, fog volumes and reflection
probes must each have a key of their own in a frame, and lights that share a key are left out of
the bake (see [Moving lights](rendering.md#moving-lights)). So give the ones you draw by hand beside those entities
keys no entity has, such as negative ones.

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

The world does this for every entity by itself, so only what you draw by hand needs
it; see [Drawing entities](ecs.md#drawing-entities).

## Things that rarely change

Walls that are entities don't need this: the world keeps [static entities](ecs.md#static-draws)
drawn for you. For data you draw by hand, a city might have 50,000 walls. Describing them
all 60 times a second is wasteful, so wrap them in `Cached`. It runs your function again only when the version changes. The
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
and the game loop moves it on each tick. Most games put the animator on an entity as an
[`Animated`](ecs.md#drawing-entities) component. To draw one by hand, give the view the animator, and it draws the
rig's model in its current pose. The pose keeps moving between ticks, so it's as
smooth as the frame rate:

```csharp
view.Model(FoxKey, _animator, transform);
```

To draw a pose on a model without an animator, pass the model and the pose. Both forms
take `ModelOptions` too, such as a mesh mask, [see below](#showing-some-meshes):

```csharp
view.Model(FoxKey, foxModel, transform, pose);
view.Model(FoxKey, _animator, transform, new ModelOptions { Meshes = meshes });
view.Model(FoxKey, foxModel, transform, pose, new ModelOptions { Meshes = meshes });
```

[Animation](animation.md) covers importing rigs, states, actions and blends.

## Showing some meshes

A model cooked from a file with several meshes can be drawn in part: one body, the
helmet off, the sword in the other hand. Make a `MeshMask` once with `content.Models.Meshes`,
naming the meshes to show, and keep it. A static model has mesh names only with
`"keepMeshes": true` in its sidecar, see [Models](rendering.md#showing-some-meshes-of-a-model).
Each draw then shows only those meshes:

```csharp
MeshMask knight = content.Models.Meshes(armoured, "body", "legs");
view.Model(KnightKey, armoured, transform, new ModelOptions { Meshes = knight });
```

With entities, give the `Model` component the mask, and the world draws with it:

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
- A ghost takes a mask too, as `view.Model(key, model, transform, new Ghost(color, opacity), meshes)`.
- `run.Drawn` entries carry the mask as `Meshes`, or null for a draw that shows every
  mesh. See [Testing](testing.md).

## Static draws

A model that never moves, such as a wall, a rock or a house, can be drawn static, and its
lighting will be baked. In the world that's a [`Static`](ecs.md#static-draws) component;
drawn by hand, give it `Static = true`:

```csharp
view.Model(WallKey, wall, transform, new ModelOptions { Static = true });
view.Model(KnightKey, statue, transform, new ModelOptions { Color = color, Meshes = statueMeshes, Static = true });
```

Until its space is baked, a static draw looks the same as any other; once the bake is fresh, it
takes its soft light from its lightmap. What finds it there is a bake id for each of its parts: a name made from the model, its tint and where it stands, the same
on every run and every machine, whatever order the draws come in. Draw it in the same place
next frame and it keeps its ids. Move it by a millimetre and they change, so its bake would
be made again; a move under a tenth of a millimetre doesn't count. A static draw inside a
[baked chunk](rendering.md#baked-lighting) is placed relative to the chunk, so every copy of
the chunk gives its draws the same ids wherever it stands.

A static draw is held where it stands by its bake, so it can't swim or take a pose. Drawing
one with a swim whose amplitude isn't zero, or with a pose or an animator, throws `ArgumentException`
naming the model's key. A static model can have at most 4,095 parts; one with more throws
when it is drawn, naming the model.

Built-in shapes and cooked models can be drawn static as they are. A mesh made in code must
be made static too, with `content.Mesh(mesh, isStatic: true)`, which gives it lightmap UVs
before it's uploaded (see [Content](content.md#code-meshes-drawn-static)). Drawing a mesh made
without it static throws `InvalidOperationException` on the next render:

```
Model 3 is a mesh made in code and drawn static, but it wasn't made static, so it has no lightmap UVs. Pass isStatic: true to Content.Mesh.
```

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
    view.Effects.Particles(tree.Id, Dust, tree.Foot, tree.Crown, age - FallSeconds);
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
| `view.Effects.Particles(key, effect, position, age)` | A puff at one spot |
| `view.Effects.Particles(key, effect, transform, age)` | A burst turned, tilted or stretched |
| `view.Effects.Particles(key, effect, from, to, age)` | Dust along a line, such as where a tree lands. `FromLine()` covers it end to end |

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
| | `Gravity(down)` | The same with one number, straight down: `Gravity(9.8f)` falls as things do on Earth |
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

view.Effects.Particles(key, TreeFall, tree.Foot, tree.Foot + direction * TrunkLength, since);
```

An emitter can be drawn on its own wherever an effect can. The node calls are
extension methods themselves, in `ParticleSpawning`, `ParticleMotion` and
`ParticleLooks`, so to share steps between emitters, write one of your own; the `Dust` example gives its stump puff and its
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

### Particles that loop

A torch's flames or a chimney's smoke play for as long as the thing is there. Give its
entity an `Emitter` instead of drawing bursts yourself:

```csharp
static readonly ParticleEmitter Flames = new ParticleEmitter(count: 70, lifetime: 0.5f)
    .Over(1f)                               // born all through a second
    .Velocity(new Vector3(0f, 0.45f, 0f))
    .Glow(4f);

world.Spawn(Transform.At(torch), new Emitter(Flames));
```

A new burst starts every `Every` seconds of game time, once a second unless you say
otherwise, and each plays its whole `Duration`, so the bursts overlap and the effect
never runs out. An effect born over one second, as these flames are, looks steady at
the default. Each burst is keyed by the entity and when it started, so the same moment
always looks the same, and the bursts from before the game began count too, so a torch
is already burning on the first frame. The burst follows the entity's `Transform` and
`Parent`, and `Hidden` stops it. `Every` must be more than zero seconds.

### Particles that play once

A sparkle where a coin was, or dust where something landed, plays once and is over. Spawn
a `Burst` and forget it:

```csharp
world.Spawn(Transform.At(coin), new Burst(Sparkle));
```

It starts on the tick it's spawned in, and when the effect's `Duration` is up the entity
is destroyed, so there's no list of bursts to keep and nothing to draw yourself. It
follows the entity's `Transform` and `Parent`, and `Hidden` hides it. The whole entity
goes, so for a burst on something that stays, such as sparks on an enemy that was hit,
spawn the burst as its child with a `Parent`. Spawn it through the
[scene](ecs.md#scenes) and it's also cleared with the scene.

### How they look

Particles are lit by the sun, the sky and point and spot lights, shaded by the sun's
shadows, sit in the fog, fade into haze and melt softly into the ground and anything they touch. A
glowing particle gives off its own light on top, but doesn't light what's round it: put
a `PointLight` entity at a fire for that. They cast no shadow, and underwater
they fade with distance but miss the shafts of light. They're sorted with
[glass](rendering.md#glass), so a puff in front of a pane covers it and one behind is
seen through it. `Cached` keeps only
models, so draw particles outside it. A frame holds at most 32,768 particles; more
are dropped. `Graphics.Surfaces.Particles` turns them off, see [graphics settings](rendering.md#graphics-settings). The `Dust` example drops trees in a clearing.
