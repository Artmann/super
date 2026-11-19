# The ECS

An entity component system suits games with thousands of similar things, like a colony
sim's workers, animals and items. Every game has a `World`, with nothing to add or
register: ask for one in a constructor.

```csharp
Game.StartWindowsGameLoop<Farm>(new GameOptions { Title = "Farm" }, services =>
{
    services.AddSingleton<Movement>();
});

public record struct Position(Vector3 Value);
public record struct Velocity(Vector3 Value);

class Movement
{
    private readonly World world;

    public Movement(World world)
    {
        this.world = world;
        world.Spawn(new Position(Vector3.Zero), new Velocity(Vector3.UnitX));
    }

    public void Update(GameTime time)
    {
        foreach (var row in world.Query<Position, Velocity>())
        {
            row.First.Value += row.Second.Value * time.TickSeconds;
        }
    }
}
```

Each row's `First`, `Second` and so on are references into the world, so changing them
changes the entity, and a query allocates nothing. Queries take up to four components, and
`row.Entity` is the entity itself.

You can change anything during a query. Values change at once, but spawning, destroying,
and adding or removing a component type wait until the loop ends, so the rows being walked
never move:

```csharp
foreach (var row in world.Query<Transform, Coin>())
{
    if (Vector3.Distance(row.First.Position, player) < 1f)
    {
        world.Destroy(row.Entity);
    }
}
```

Until the loop ends, a destroyed entity is still alive with its components, and a spawned
one is alive but has none yet, so this query won't visit it. A component added during
the loop isn't there yet either: `Has` says false, and `Get` throws to say it arrives
when the query ends. The changes are made in the order you asked for them, when the
outermost query ends, even if the loop throws. One meant for an entity that's been
destroyed by then is dropped. If a handler throws while they're made, the rest are still
made, and the first error is thrown after the last one.

## The world

| Call | What it does |
| --- | --- |
| `world.Spawn(a, b, ...)` | Makes an entity with up to four components. Add more with `Add` |
| `world.Create()` | Makes an entity with no components |
| `world.Add(entity, component)` | Adds a component, or replaces one of the same type |
| `world.Remove<T>(entity)` | Removes a component |
| `world.Has<T>(entity)` | Whether it has one |
| `world.Get<T>(entity)` | A reference to a component, to read or change |
| `world.Teleport(entity, transform)` | Sets its `Transform` so it's drawn there at once, not sliding there between ticks. See [Drawing entities](#drawing-entities) |
| `world.Changed(entity)` | Says a `Static` entity was changed through `Get`, so it's drawn again. See [Static draws](#static-draws) |
| `world.Destroy(entity)` | Removes the entity and its components, and destroys its children, and theirs |
| `world.Children(entity)` | The entities whose `Parent` is this one, without their own children. It walks every parented entity, so keep it out of loops over many |
| `world.IsAlive(entity)` | Whether it still exists. A destroyed entity's ID isn't confused with a new one |
| `world.Count` | How many entities there are |

```csharp
var sheep = world.Spawn(new Position(Vector3.Zero), new Velocity(Vector3.UnitX));

ref var position = ref world.Get<Position>(sheep);
position.Value.Y = 1f;
```

A world holds up to 256 component types, counting Super's own. Adding a 257th type throws.

### Required components

Some components only make sense with another, so the world adds it for you. A `Model` or
an `Animated` entity without a `Transform` gets `Transform.Identity`, at the world's origin.
A game can ask for the same with its own components:

```csharp
world.Require<Health, Armor>(new Armor(0));

var guard = world.Spawn(new Health(100));           // gets Armor(0) too
var knight = world.Spawn(new Health(100), new Armor(5)); // keeps its Armor(5)
```

A required component never replaces one the entity was given, in whichever order
`Spawn` lists them, and requirements chain: if `Armor` requires something too, a guard
gets that as well. It happens when the entity gets the component, through `Spawn` or
`Add`; entities that already had it before the `Require` are left as they are. Asking
a component to require itself, or to require one that already leads back to it, throws.

### Reacting to components

Some work belongs to the moment a component arrives or goes: setting up a light for a new
torch, dropping loot when a monster loses its `Health`. Ask the world to run it then:

```csharp
world.OnAdded<Torch>(torch => lights.Add(torch));
world.OnRemoved<Health>(monster =>
{
    var at = world.Get<Transform>(monster).Position;
    world.Spawn(Transform.At(at), new Model(_coinModel));
});
```

`OnAdded` runs when the component lands on an entity that had none. Replacing the value
with `Add` doesn't run it again. It waits until everything the same `Spawn` or `Add` brings
is on the entity, required components too, so a handler can read any of them.

`OnRemoved` runs just before the component goes, through `Remove` or `Destroy`, so the
handler can still read it, and a destroyed entity is still alive inside it. `Destroy` runs
the handlers for each of the entity's components.

A handler can spawn, destroy, add and remove like any other code. Those changes wait until
the handler returns, the same as inside a query. A component added inside a query lands
when the query ends, so its `OnAdded` runs then. An entity spawned and destroyed inside the
same query never got its components, so neither kind of handler runs for it. Handlers for
one type run in the order they were added.

## Drawing entities

Most entities are drawn the same way: a model at a transform, keyed by the entity's ID.
Give entities Super's own components and the world draws them itself, every frame, before
your `Draw` runs:

```csharp
var sheep = world.Spawn(
    Transform.At(new Vector3(3f, 0f, 1f)),
    new Model(content.Model("farm/sheep.glb")));

var bell = world.Spawn(
    Transform.At(new Vector3(0f, 0.8f, 0.5f), scale: 0.2f),
    new Model(content.Model("farm/bell.glb")),
    new Parent(sheep));

public void Draw(View view)
{
    view.Camera(camera);
    view.Lights.Sun(new Vector3(1f, 2f, 1f), new Vector3(3f));
}
```

| Component | What it does |
| --- | --- |
| `Transform` | Position, rotation and scale. `Transform.At` and `Transform.Facing` build common ones, and `Transform.From` takes a [spline](splines.md) pose. A `Model` or `Animated` entity gets `Transform.Identity` if it has none |
| `Model` | A `ModelRef`, and an optional color that tints the model and keeps its textures, a `MeshMask` and a `DistanceFade` that [fades it out with distance](rendering.md#fading-with-distance) |
| `Parent` | Counts the entity's transform from another entity's, so an attached thing follows. Give a `Bone` to follow a joint of an `Animated` parent, such as a sword in a hand. Destroying the parent destroys it too |
| `Swim` | Bends the model like a swimming fish, with the phase blended between ticks too |
| `Animated` | Draws the model in its `Animator`'s pose, which the game loop moves on each tick. Without a `Model`, it draws the rig's own model. See [Animation](animation.md) |
| `Static` | Draws the model static, so its lighting can be baked. See [Static draws](#static-draws) |
| `ScopedTo<T>` | Destroys the entity, and its children, when that game state ends |
| `Sound` | Plays a looping sound from the entity for as long as it has this. See [Sounds from entities](#sounds-from-entities) |
| `Ambience` | Plays an ambience for as long as the entity has this |
| `Camera` | The frame is taken from the newest entity with one, blended between ticks and following its `Parent`. See [A camera entity](cameras.md#a-camera-entity) |
| `Sun`, `PointLight`, `SpotLight` | Lights every frame. A point or spot light's position and direction count from the entity's `Transform` and `Parent`, and a sun's direction stays in the world. See [Lights](rendering.md#lights) |
| `Sky`, `Shadows`, `AmbientOcclusion`, `Finish`, `Outlines` | The frame's sky, shadows, ambient occlusion, finished look and outlines, from the newest entity with one, as the view calls of the same names set them |
| `Emitter` | Plays a particle effect at the entity for as long as it has one, a burst every `Every` seconds. See [Particles that loop](views.md#particles-that-loop) |
| `Ghost` | Draws the entity's model as a see-through ghost, such as a building's placement preview, as `View.Model` with a `Ghost` does |
| `Hidden` | Leaves the entity and everything parented under it out of the frame and the audio: no models, lights or sounds, and its sounds and ambiences fade out. Remove it to show them again |

Every entity with a `Transform` is drawn moving smoothly between ticks, so you don't keep a
previous position yourself: the world takes a copy before each `Update`, and draws blending
from it. An entity's first frame shows it exactly where it is, so a loaded save
doesn't glide into place, and a `Static` entity is always drawn where it is.

When an entity jumps rather than moves, such as a respawn, a wrap at the edge of the map
or a move to another room, place it with `Teleport` so it isn't seen sliding there:

```csharp
world.Teleport(player, Transform.At(spawnPoint));
```

These components are only data. A system that moves things writes `Transform`, and the
world draws what it says. Entities without `Model` aren't drawn, so gameplay-only things
cost nothing here.

`Draw` is optional. A game whose whole picture is entities leaves it out; one that keeps
it uses it for what only that frame shows, such as a glow that pulses with the display
time, next to what the world drew. `view.TransformOf(entity)` gives an entity's matrix as
it's drawn this frame, between its last two ticks and following its parents, so something
drawn by hand can sit on it:

```csharp
public void Draw(View view)
{
    var lantern = view.TransformOf(lanternEntity);
    view.Model(GlowKey, glow, Matrix4x4.CreateScale(pulse) * lantern);
}
```

It throws `InvalidOperationException` for an entity with no `Transform`.

The [Reef example](../examples/Reef) uses this for thousands of schooling fish, and its
`Draw` is only a camera, a sun and the seabed.

### Static draws

Give an entity that never moves the `Static` component, and the world draws it
static, the same as `new ModelOptions { Static = true }` in [Views](views.md#static-draws):

```csharp
world.Add(wall, Transform.At(new Vector3(4f, 0f, 0f)));
world.Add(wall, new Model(content.Model("castle/wall.glb")));
world.Add(wall, new Static());
```

The world draws static entities once and keeps the result, so a town of thousands of
pieces costs almost nothing a frame. It draws them again when a static entity is spawned,
destroyed, or given or relieved of a component with `Add` or `Remove`. A change it can't
see is a write through `Get`, so call `world.Changed(entity)` after one:

```csharp
world.Get<Transform>(wall).Position.Y = 2f;
world.Changed(wall);
```

The same goes for a static entity with a `Parent` that moves: it stays where it was drawn
until something changes. For an entity that isn't static, `Changed` does nothing.

A static entity can't swim or take a pose, so one with `Animated`, or with a `Swim` whose
amplitude isn't zero, throws `ArgumentException` when it's drawn, naming the entity's ID as
the model's key.

## Sounds from entities

Put a `Sound` on an entity and it plays; take it away, or destroy the entity, and it
fades out. There is nothing to call and no handle to keep:

```csharp
var engine = world.Create();
world.Add(engine, Transform.At(new Vector3(4f, 0f, 2f)));
world.Add(engine, new Sound(content.Sounds.Sound("sounds/engine.ogg"), Volume: 0.8f));
```

With a `Transform` the sound sits where the entity is drawn (blended between
ticks and following its `Parent`, as for `Models`) and follows it. Without one it's flat. The fields are `Clip`,
`Volume` (1), `Channel` (`Effects`), `Near` (2 m) and `Far` (50 m); see
[Audio](audio.md#sounds-and-ambiences-on-entities).

An `Ambience` does the same for an [ambience](audio.md#ambience-files). With `ScopedTo<T>`
on the same entity it plays for as long as its game state does:

```csharp
screen.OnEnter(Screen.Forest, () =>
{
    var forest = world.Create();
    world.Add(forest, new Ambience(content.Sounds.Ambience("ambience/forest.ambience.json")));
    world.Add(forest, new ScopedTo<Screen>(Screen.Forest));
});
```

Both are kept in step after each frame's `Draw`. Changing the component's volume fades to
the new one; changing its clip, channel, `Near` or `Far` swaps the sound.

## Entities that belong to a state

Most entities in a [game state](states.md) should go when it ends, so tag them with
`ScopedTo`. They're destroyed when the state ends, after its `OnExit` callbacks, and
everything untagged carries through to the next state.

```csharp
screen.OnEnter(Screen.House, () =>
{
    var door = world.Create();
    world.Add(door, Transform.At(new Vector3(0f, 0f, 4f)));
    world.Add(door, new Model(_doorModel));
    world.Add(door, new ScopedTo<Screen>(Screen.House));

    // The player isn't tagged, so they walk in with their inventory intact.
    world.Add(_player, Transform.At(new Vector3(0f, 0f, 3f)));
});
```

An entity moved in `OnEnter` appears at its new place, without gliding there.

Destroying an entity destroys its children too, so tag only the entity at the top: a door's
handle goes with the door. A child moved to another parent first stays.

## Scenes

A scene is everything in one place of the game: a room, a level, a fight. Ask for the
`Scenes` service and go to one with a function that builds it. Spawn through the `Scene`
it hands you, and those entities belong to the scene.

```csharp
public sealed class Crawler(Scenes scenes) : IGame
{
    public void Load() => scenes.Go(scene => Crypt(scene, room: 1));

    public void Update(GameTime time)
    {
    }

    private static void Crypt(Scene scene, int room)
    {
        scene.Spawn(new Camera(new Vector3(0f, 6f, 8f), Vector3.Zero));
        scene.Spawn(new Sun(new Vector3(-1f, -2f, -1f), Vector3.One));
        scene.Spawn(Transform.At(Vector3.Zero), new Model(Floor), new Static());
    }
}
```

`Go` lands at the end of the tick. The tick finishes in the old scene, then every entity
the old scene spawned is destroyed, with its children, and the build runs. Asking twice in a
tick keeps the last. The build can't read input, since it runs between ticks.

Entities spawned straight on the world belong to no scene and outlive every `Go`, so keep a
party, an inventory or the music there. `scenes.Current` is the open scene, or null before
the first `Go`, and `scenes.Rebuild()` builds it again from the start, for a restart.

Give `Go` a [transition](transitions.md) to cover the swap, such as
`scenes.Go(scene => Crypt(scene, room: 2), Transition.Fade(0.6f))`. A fade swaps at the
end of the tick the screen is all colour, so the old scene keeps running while it goes out.
Asking again before then only changes what gets built; the fade carries on. A dissolve, wipe
or iris swaps at the end of the tick and shows the old picture going over the new.
`scenes.IsChanging` is true from `Go` until its transition has finished, which is handy for
ignoring input meanwhile.

Scenes are for where in the world the game is; [states](states.md) are for where the game
is, such as a menu or paused. A game can use either or both. A prefab needs nothing from
Super: it's a method that spawns a few entities under a parent it's given.

## Systems

A small game can do all its work in `Update`. When that grows, split it into systems: classes
that each do one part, run in an order you choose. Ask for `Systems`, add each one, and call
`Update` from your game's own `Update`.

```csharp
public sealed class Waves(World world) : ISystem
{
    public void Update(GameTime time)
    {
        if (time.Tick % 300 == 0)
        {
            world.Spawn(new Enemy(), Transform.At(new Vector3(0f, 0f, -20f)));
        }
    }
}

public sealed class Outpost(Systems systems) : IGame
{
    private readonly Systems _systems = systems.Add<Waves>().Add<Towers>().Add<Bullets>();

    public void Update(GameTime time) => _systems.Update(time);
}
```

A system gets what its constructor asks for, such as `World` or `Input`, from the game's
services, and doesn't need registering itself. Each type is added once; adding it again throws.
Nothing runs on its own: the game decides when `Update` runs, so pausing is skipping the call.
Systems are optional, and a game can mix them with work in its own `Update`.
