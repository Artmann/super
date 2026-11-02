# The ECS

An entity component system suits games with thousands of similar things, like a colony
sim's workers, animals and items. `Super.Ecs` is a separate package, and it plugs in as
ordinary services. You don't need it: plain lists of your own work just as well, and so
does another ECS library.

```csharp
Game.StartWindowsGameLoop<Farm>(new GameOptions { Title = "Farm" }, services =>
{
    services.AddSuperEcs();
    services.AddSingleton<Movement>();
});

public record struct Position(Vector3 Value);
public record struct Velocity(Vector3 Value);

class Movement(World world)
{
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
`row.Entity` is the entity itself. Changing values during a query is fine, but adding or
removing components or entities throws, so collect those and apply them after the loop.

## The world

| Call | What it does |
| --- | --- |
| `world.Create()` | Makes an entity |
| `world.Add(entity, component)` | Adds a component, or replaces one of the same type |
| `world.Remove<T>(entity)` | Removes a component |
| `world.Has<T>(entity)` | Whether it has one |
| `world.Get<T>(entity)` | A reference to a component, to read or change |
| `world.Destroy(entity)` | Removes the entity and its components |
| `world.IsAlive(entity)` | Whether it still exists. A destroyed entity's ID isn't confused with a new one |
| `world.Count` | How many entities there are |

```csharp
var sheep = world.Create();
world.Add(sheep, new Position(Vector3.Zero));
world.Add(sheep, new Velocity(Vector3.UnitX));

ref var position = ref world.Get<Position>(sheep);
position.Value.Y = 1f;
```

## Drawing entities

Most entities are drawn the same way: a model at a transform, keyed by the entity's ID.
Rather than write that loop in every game, give entities Super's own components and let
`view.Models(world)` do it:

```csharp
var sheep = world.Create();
world.Add(sheep, Transform.At(new Vector3(3f, 0f, 1f)));
world.Add(sheep, new Model(content.Model("farm/sheep.rmodel")));
world.Add(sheep, new Smoothed());

var bell = world.Create();
world.Add(bell, Transform.At(new Vector3(0f, 0.8f, 0.5f), scale: 0.2f));
world.Add(bell, new Model(content.Model("farm/bell.rmodel")));
world.Add(bell, new Parent(sheep));

public void Draw(View view)
{
    view.Camera(camera);
    view.Sun(new Vector3(1f, 2f, 1f), new Vector3(3f));
    view.Models(world);
}
```

| Component | What it does |
| --- | --- |
| `Transform` | Position, rotation and scale. `Transform.At` and `Transform.Facing` build common ones |
| `Model` | A `ModelRef`, and an optional color that tints the model and keeps its textures |
| `Smoothed` | Blends the entity between ticks. Super remembers last tick's transform for you |
| `Parent` | Counts the entity's transform from another entity's, so an attached thing follows |
| `Swim` | Bends the model like a swimming fish, with the phase blended too when `Smoothed` |
| `ScopedTo<T>` | Destroys the entity when that game state ends |

`Smoothed` means you don't keep a previous position yourself: the world takes a copy
before each `Update`, and an entity's first frame shows it exactly where it is, so a
loaded save doesn't glide into place.

These components are only data. A system that moves things writes `Transform`, and
`Models` is a plain call you make in `Draw`, next to anything you still describe by hand.
Entities without `Model` aren't drawn, so gameplay-only things cost nothing here.

The [Reef example](../examples/Reef) uses this for thousands of schooling fish, and its
`Draw` is a camera, a sun, the seabed and `view.Models(world)`.

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

A `Smoothed` entity moved in `OnEnter` appears at its new place, without gliding there.

Destroying an entity doesn't destroy its children, so tag those too. A child left behind
draws at its own transform, as if its parent were the world.
