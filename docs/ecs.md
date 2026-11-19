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
destroyed by then is dropped.

## The world

| Call | What it does |
| --- | --- |
| `world.Spawn(a, b, ...)` | Makes an entity with up to four components. Add more with `Add` |
| `world.Create()` | Makes an entity with no components |
| `world.Add(entity, component)` | Adds a component, or replaces one of the same type |
| `world.Remove<T>(entity)` | Removes a component |
| `world.Has<T>(entity)` | Whether it has one |
| `world.Get<T>(entity)` | A reference to a component, to read or change |
| `world.Destroy(entity)` | Removes the entity and its components |
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
Rather than write that loop in every game, give entities Super's own components and let
`view.Models(world)` do it:

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
    view.Models(world);
}
```

| Component | What it does |
| --- | --- |
| `Transform` | Position, rotation and scale. `Transform.At` and `Transform.Facing` build common ones, and `Transform.From` takes a [spline](splines.md) pose. A `Model` or `Animated` entity gets `Transform.Identity` if it has none |
| `Model` | A `ModelRef`, and an optional color that tints the model and keeps its textures, a `MeshMask` and a `DistanceFade` that [fades it out with distance](rendering.md#fading-with-distance) |
| `Parent` | Counts the entity's transform from another entity's, so an attached thing follows |
| `Swim` | Bends the model like a swimming fish, with the phase blended between ticks too |
| `Animated` | Draws the model in its `Animator`'s pose, which the game loop moves on each tick. Without a `Model`, it draws the rig's own model. See [Animation](animation.md) |
| `Static` | Draws the model static, so its lighting can be baked. See [Static draws](#static-draws) |
| `ScopedTo<T>` | Destroys the entity when that game state ends |
| `Sound` | Plays a looping sound from the entity for as long as it has this. See [Sounds from entities](#sounds-from-entities) |
| `Ambience` | Plays an ambience for as long as the entity has this |

Every entity with a `Transform` is drawn moving smoothly between ticks, so you don't keep a
previous position yourself: the world takes a copy before each `Update`, and `Models`
blends from it. An entity's first frame shows it exactly where it is, so a loaded save
doesn't glide into place, and a `Static` entity is always drawn where it is.

When an entity jumps rather than moves, such as a respawn, a wrap at the edge of the map
or a move to another room, place it with `Teleport` so it isn't seen sliding there:

```csharp
world.Teleport(player, Transform.At(spawnPoint));
```

These components are only data. A system that moves things writes `Transform`, and
`Models` is a plain call you make in `Draw`, next to anything you still describe by hand.
Entities without `Model` aren't drawn, so gameplay-only things cost nothing here.

The [Reef example](../examples/Reef) uses this for thousands of schooling fish, and its
`Draw` is a camera, a sun, the seabed and `view.Models(world)`.

### Static draws

Give an entity that never moves the `Static` component, and `view.Models(world)` draws it
static, the same as `new ModelOptions { Static = true }` in [Views](views.md#static-draws):

```csharp
world.Add(wall, Transform.At(new Vector3(4f, 0f, 0f)));
world.Add(wall, new Model(content.Model("castle/wall.glb")));
world.Add(wall, new Static());
```

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

Destroying an entity doesn't destroy its children, so tag those too. A child left behind
draws at its own transform, as if its parent were the world.
