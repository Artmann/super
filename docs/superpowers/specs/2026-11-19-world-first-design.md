# World-first Super: design for version 2

## Why

Super has two ways to put things in a game. The first is drawing by hand: `view.Model(key,
model, matrix, color)` in `Draw`, used in 34 example files and taught by the tower defense
guide. The second is entities with `Transform`, `Model`, `Smoothed` and `Parent`, drawn by
`view.Models(world)`, used in four examples and described as optional.

The hand way makes every game repeat the same work. The guide's reader invents key ranges
(`10_000 + …`), keeps a `PreviousPosition` for `view.Smooth`, builds matrices, and writes a
`Draw` and a `Clear` for each class. DungeonCrawler, a real game on Super 1.5, shows where
that leads. Its `BattleGame.cs` is 2,547 lines, and much of it is:

- `Draw` branching on the screen (`if (InMenu) … return;`), with each branch setting its own
  camera, suns, sky and post settings every frame, and an if-chain on the room's name for lighting;
- `BeginFight` filling about 20 arrays indexed by fighter, and `EndFight` destroying entities
  from lists it kept;
- its own "before" copies of transforms lerped with `view.Blend`, because `Smoothed` only
  helps the model itself and not the rings, torches and aiming that follow it;
- a torch placed in a hand by multiplying a joint matrix by a stand matrix, then a light
  and flames placed from the result;
- key arithmetic such as `TorchKey + i * 4`;
- looping fire faked with two overlapping particle bursts whose keys cycle with the clock;
- a static room kept as a list drawn inside `view.Cached` with a hand-kept version number.

Version 2 may break the API. The aim is one obvious way to make a game, where the engine
does this work.

## Goals

- Entities are the way to put anything in the world: models, cameras, lights, sky, particles,
  sounds, colliders. The engine draws the world without being asked.
- A scene is a set of entities under one root, built by a function, and it is not the top of
  the game. The game class, its states and its rule classes sit above scenes and switch them.
- Hierarchy works as people expect: a child follows its parent, attaches to a bone, and is
  destroyed with its parent.
- The tower defense guide gets shorter. Its reader never writes a key, a previous position
  or a `Clear` method, and most parts need no `Draw`.
- DungeonCrawler's pain points listed above each have an answer in the engine.

## Non-goals

- A Bevy-style scheduler. `Update` stays a plain method that calls things in the order written.
  No automatic ordering and no automatic parallelism.
- Forcing game rules into components. A plain class such as DungeonCrawler's `Battle` stays valid.
- Scene files, a scene editor or serialised prefabs. Scenes and prefabs are code.
- More than one scene open at a time. A `Scene` is a root entity, so this can come later.
- Migrating DungeonCrawler. It lives in another repository. It is the test of whether the
  design is right, and a later job.
- Changing the interface (`Ui`), input, content, audio clips, physics rules or the renderer's passes.

## What would make this a failure

- Two ways still taught side by side, so a newcomer has to choose.
- Scenes that pull game logic down into them the way Unity scenes do.
- An order of execution that has to be looked up instead of read.
- Reef, with thousands of fish, or a room of 50,000 static pieces getting slower than they
  are today, or a running frame starting to allocate.
- Drawing able to change the simulation, so a seed no longer replays the same.

## Constraints

- `Update` runs at a fixed tick and `Draw` once per frame. Drawing only reads. Headless runs,
  `--screenshot`, `--record` and tests must behave exactly as the window does.
- The world's archetype mask is one `ulong`, so a world holds at most 64 component types.
  This design adds a dozen or more engine components, so the limit has to rise (see below).
- `Super.Ecs` is a separate project that references `Super`, and `Super.Physics` references
  `Super.Ecs`. The game loop reaches the world only through `ITickHook`, `IStateHook` and `IDrawHook`.
- `Camera` is a `record struct` of position, target and field of view, used by `view.Camera`,
  camera rigs, cutscene shots and `Transition.Blend`. `PointLight` and `SpotLight` carry a
  position today.
- `main` already holds breaking changes for 2.0, so it need not stay releasable as 1.x.
- Particles are stateless: every particle is worked out from an age and a key. That is why
  pausing, loading and `--record` agree, and a looping emitter must keep that property.
- The repository rules apply: braces on control statements, docs updated in the same change,
  `dotnet roe` clean, tests that are proven able to fail.

## Approaches considered

For how far to go, the choice was between world-first and trimming `Draw`. Trimming (automatic
keys, built-in smoothing, transform helpers) is a smaller change but leaves both ways in place.
World-first was chosen because one way is the point.

For what a scene is, three were considered.

1. A function that spawns entities under a root entity. Parameters are ordinary arguments,
   there is no base class, and a prefab is the same thing at a smaller size. Chosen.
2. A scene class the engine creates, with dependency injection and its own `Update`. Passing
   "which room" in is awkward, and logic drifts into scenes.
3. An enum state, as the Rooms example does now. It can't express the same kind of room with
   different contents, which DungeonCrawler needs.

For how code runs, the choice was plain updates or Bevy-style systems. Plain updates were
chosen: order is the order of the lines, and C# has no cheap way to fill in system parameters
at compile time. A small optional list of systems is added on top, described below.

## The design

### The world is part of Super

A `World` is always registered. `AddSuperEcs()` goes away, and `using Super;` is enough to
spawn and query. How the projects are arranged to allow that is the builder's choice (folding
`Super.Ecs` into `Super` is the obvious way), as long as the NuGet package stays one package.

The limit of 64 component types rises to at least 256.

### Spawning

```csharp
Entity enemy = world.Spawn(
    Transform.At(start),
    new Model(look.Capsule, stats.Color),
    new Enemy(kind, stats.Health));
```

`Spawn` takes the entity's components in one call and returns the entity. It must not box or
allocate per call. `Create` and `Add` remain for adding to an entity later.

Some components need others. `Model`, `Animated`, `PointLight`, `SpotLight` and `Emitter`
need a `Transform`, and get an identity one when the entity has none. Games can declare the same for
their own components. The mechanism is the builder's choice.

### Changes during a query, and reactions

Today, spawning, destroying, adding or removing inside a query throws. In version 2 these are
queued while any query is running and applied when the outermost one ends. `Spawn` still
returns its entity at once. Reading a queued component before it lands throws with a message
that says why.

`world.OnAdded<T>(Action<Entity>)` and `world.OnRemoved<T>(Action<Entity>)` run when a
component of that type lands on or leaves an entity, including when the entity is destroyed.
`OnRemoved` runs while the component can still be read. They let a feature's setup and cleanup
sit beside the feature.

### Smooth motion without asking

Every entity with a `Transform` blends between ticks when drawn, unless it is `Static`. The
`Smoothed` component goes away. An entity's first frame shows it where it is, and
`world.Teleport(entity, transform)` moves one without a glide. This is my call. It removes a
line from every spawn and the bug of forgetting it; the cost is one transform copy per moving
entity per tick, which Reef already pays.

### The world draws itself

After each tick's work, a frame is made from the world, then the game's `Draw` runs and may
add to it or replace parts of it, then a playing cutscene sets the camera as it does now.
`IGame.Draw` gets an empty default, and `view.Models(world)` is removed.

These components are drawn or applied by the engine. A model, a point light, a spot light and
an emitter take their place from the entity's `Transform`, and a spot light points along it.
A `Sun` keeps its own direction, since a sun has no place. Cameras are described below.

| Component | What it does |
| --- | --- |
| `Model`, `Animated`, `Swim`, `Static` | As today |
| `Ghost` | Draws the entity's model as a ghost, in one colour with an opacity |
| `Hidden` | The entity and everything under it are not drawn, lit or heard |
| `Camera` | The camera the frame is seen through |
| `Sun`, `PointLight`, `SpotLight` | Lights, with the same options as `view.Lights` |
| `Sky`, `Shadows`, `AmbientOcclusion`, `Finish`, `Outlines` | The frame's settings, the same values `view` takes now |
| `Emitter` | A particle emitter that runs until it is removed. See below |
| `Sound`, `Ambience`, `Collider`, `Body` | As today |

When more than one `Camera`, `Sky` or other one-per-frame component exists, one in the
current scene wins over one outside it, and otherwise the one spawned last wins. A game that
needs the active camera in `Update`, for `input.Mouse.Ray`, reads it in one line from the
world; the exact member is the builder's choice.

### Cameras

The `Camera` type stays as it is, with a position, a target and a field of view, and is the
component. Rigs, shots and `view.Camera` keep working with the same type. This is my call:
moving the camera's place into a `Transform` would be more uniform, but it would split one
well-used type in two and change every rig and shot.

```csharp
Entity camera = scene.Spawn(new Camera(position, target));

// In Update, for a camera that moves:
world.Get<Camera>(camera) = rig.Camera;
```

- The engine keeps each camera's value from the tick before and blends position, target and
  field of view between ticks, so a camera written once per tick in `Update` is smooth at any
  frame rate. No `Draw` and no `view.Blend` are needed.
- A new camera shows where it is on its first frame. `world.Teleport` on a camera entity makes
  it jump without a glide, for a cut inside a scene.
- When the entity has a `Transform` or a `Parent`, the camera's position and target are
  measured from that entity's place. `scene.Spawn(new Parent(player), new Camera(behind, ahead))`
  is a camera that follows the player, as smoothly as the player moves.
- `Transition.Blend` and cutscenes work as now: they act on the camera the frame ends up with.

`Static` entities are described to the renderer once, which is what `view.Cached` does by
hand today. They are described again when a static entity is spawned or destroyed, or has a
component added or removed. The game keeps no version number. `world.Get` hands out a `ref`,
so the world can't see a write made through it: a game that changes a static entity in place
calls `world.Changed(entity)` afterwards, and the docs say so beside `Static`. This is my
call. Comparing every static entity each frame would find such writes, but it costs per
frame what the cache exists to save.

The entity's ID is its key, so games write no keys for entities. `run.Drawn` keeps working
and reports those IDs.

The rest of what a view can draw (decals, fog volumes, reflection and light probes, baked
lighting, water, puddles, wind) becomes components in a later stage. Until then they stay as
`view` calls in `Draw`.

`Draw(View view)` remains for things that exist for a frame and belong to no entity, such as
hover rings and a path of dashes. The docs present it as that and nothing more.
`view.TransformOf(entity)` gives an entity's blended matrix in the world, so hand-drawn things
can follow an entity without the game keeping its own "before" copies.

### Emitters that loop

`new Emitter(effect)` plays a `ParticleEmitter` or `ParticleEffect` at the entity for as long
as the component is there. It must stay a pure function of time and the entity, so a pause, a
load and a recording show the same flames. How the loop is made (the two overlapping bursts
DungeonCrawler uses, moved into the engine, or a loop mode in the particle maths) is the
builder's choice. One-shot bursts stay as they are: `view.Effects.Particles` with an age.

### Hierarchy

`Parent` stays, with three changes.

- Destroying an entity destroys everything under it.
- `new Parent(fighter, Bone: "hand_l")` attaches to a joint of the parent's `Animated` rig,
  and follows it between ticks as smoothly as the pose itself. A bone the rig lacks throws,
  naming the bone and the entity.
- `Hidden` on a parent hides its children.

`world.Children(entity)` lists an entity's children. `ScopedTo<T>` stays, and with the first
change its children no longer need tagging.

### Scenes

A scene is a root entity and whatever is spawned under it. The game asks for the `Scenes` service.

```csharp
scenes.Go(scene => Crypt.Build(scene, room), Transition.Fade(0.8f));

static void Build(Scene scene, Room room)
{
    scene.Spawn(new Camera(position, target));
    scene.Spawn(new Sun(direction, color));
    scene.Spawn(new Sky(settings), new Finish(post));

    foreach (var piece in room.Pieces)
    {
        scene.Spawn(piece.Transform, new Model(piece.Model), new Static());
    }

    var fighter = scene.Spawn(Transform.At(cell), new Animated(animator), new Fighter(index));
    scene.Spawn(new Parent(fighter, Bone: "hand_l"), new Model(torch), new PointLight(color, range), new Emitter(Fires.Flame));
}
```

- `scene.Spawn` is `world.Spawn` with the entity owned by the scene. An entity given a `Parent`
  inside the scene is owned through that parent.
- `scenes.Go(build, transition)` is a request, like `States.Set`. The tick finishes in the old
  scene. Then the old root is destroyed and `build` runs. With a fade the swap happens at the
  cut, while the screen is all colour; with the other transitions it happens at once and the
  old picture is kept, as `Transitions.Play` does now. `Go` twice in a tick: the last wins.
  `build` can't read input, for the same reason `OnEnter` can't.
- A `Go` asked for while an earlier one is still fading out replaces it. The earlier scene is
  never built, and the fade carries on to the new one. When something else ends the fade
  early (the game's own `transitions.Play`, a cutscene's transition, or `Stop`), the swap
  still waits for the end of the tick and never runs in the middle of `Update`. Today
  `Transitions.Stop` runs a pending `atCut` at once, so `Transitions` needs a way to hold a
  cut until the tick boundary. This is my call.
- `scenes.Current` is the open scene, or none before the first `Go`. `scenes.IsChanging` is
  true while a transition to a new scene is playing.
- `scenes.Rebuild()` destroys the current scene and runs its build function again, for a
  restart or after hot reload.
- Entities spawned with `world.Spawn` belong to no scene and outlive every `Go`. That is where
  a party, an inventory or music lives.
- `States<T>` and `ScopedTo<T>` are unchanged. States are for where the game is (menu, playing,
  paused); scenes are for where in the world it is. A game uses either or both.
- In tests, `run.Scenes` reaches the service and `run.World` the world.

A prefab needs no engine feature: it is a method that spawns a few entities under a parent it
is given.

### Systems, if you want them

```csharp
public void Load() => systems.Add<Waves>().Add<Enemies>().Add<Towers>().Add<Shots>();

public void Update(GameTime time)
{
    if (screen.Is(Screen.Playing))
    {
        systems.Update(time);
    }
}
```

A system is a class that implements `ISystem`, with one method, `Update(GameTime time)`.
`systems.Add<T>()` makes it through dependency injection, so it needs no `AddSingleton` line.
`systems.Update(time)` runs them in the order they were added. Nothing runs unless the game
calls it. This is sugar: calling four `Update` methods by hand is equally supported.

### What is removed or renamed

- `AddSuperEcs()`, `view.Models(world)` and `Smoothed` are removed.
- `IGame.Draw` becomes optional.
- `PointLight` and `SpotLight` as components take their place from the `Transform`. Whether
  the hand-drawn `view.Lights.Point` keeps the old shape or takes a position beside the light
  is the builder's choice, as long as one type serves both.

## Stages

Each stage is one pull request into `main`. `main` is heading for 2.0 and takes breaking
changes, so there is no long-lived branch and no 1.x release to protect. Each stage ends with
the fast tests, the GPU tests and `dotnet roe` passing, the docs for what it touched updated,
and every example still running.

1. The world in core: always registered, `Spawn`, required components, queued changes,
   reactions, 256 component types, smoothing by default with `Teleport`.
2. The world draws itself: models, animated models and static caching without
   `view.Models`; `Camera`, lights, the frame's settings, `Ghost`, `Hidden`, `Emitter`;
   `Draw` optional; `view.TransformOf`.
3. Hierarchy: destroy with parent, bone attachment, `world.Children`.
4. Scenes and systems: `Scenes`, `Scene`, `Go` with transitions, `Rebuild`, `Systems`.
5. Docs and examples, all of them. `ecs.md` becomes the main "The world" page and `views.md`
   shrinks to one page on drawing by hand. Every other page that teaches a `view` call with
   a component form (`getting-started.md`, `rendering.md`, `cameras.md`, `animation.md` and
   the rest) is rewritten to teach the component. The tower defense guide and Outpost are
   rewritten on entities, scenes and systems, and Rooms uses scenes. Every example moves to
   entities for anything that lasts longer than a frame. When this stage ends, a hand `view`
   call for a lasting thing appears only on the drawing-by-hand page and for the stage 6
   features. Pictures are retaken only where the picture changes. This stage is the largest
   and may be split into several pull requests, examples first.
6. The remaining `view` calls as components: decals, fog volumes, probes, baked lighting,
   water, puddles, wind.

## Left to the builder

- How `Super.Ecs` and `Super` are arranged so the world is always there.
- How required components are declared, and how `Spawn` avoids boxing.
- How scene ownership is stored (a `Parent` to the root or a separate tag), provided a large
  static scene costs no more per frame than it does today.
- How the looping emitter is made.
- The exact member for reading the active camera, and the names of small helpers.
- Whether `CameraRig` gets a component form in stage 2 or stays a value the game steps and
  writes into its `Camera` entity, and the exact member a rig exposes for that.
- How a camera's previous value is stored for blending.

## How we'll know it worked

- Outpost's `Enemies`, `Towers` and `Shots` have no `Draw`, no keys and no previous positions,
  and `NewGame` no longer calls a `Clear` on each.
- A sketch of DungeonCrawler's `BeginFight` and `Draw` on the new API needs no parallel
  "before" arrays, no `EndFight` destroy lists, no key constants and no screen branches in `Draw`.
- Reef is no slower than on main. A scene of 50,000 static pieces, added as a benchmark in
  stage 2, is no slower as entities than the same pieces drawn through `view.Cached` on main.
  A running frame allocates nothing.
