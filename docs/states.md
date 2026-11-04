# Game states

A menu, a loading screen, the house and the outside are all states of one game. Name them
with your own enum and ask for `States<YourEnum>`. There's nothing to register.

```csharp
public enum Screen { Menu, House, Outside }

public class MyGame(States<Screen> screen, Actions<Control> actions, Walking walking) : IGame
{
    public void Load()
    {
        screen.OnEnter(Screen.House, BuildHouse);
        screen.OnExit(Screen.House, SaveHouse);
    }

    public void Update(GameTime time)
    {
        if (screen.Is(Screen.Menu) && actions.Pressed(Control.Start))
        {
            screen.Set(Screen.House);
        }

        if (screen.Is(Screen.House))
        {
            walking.Update(time);
        }
    }

    // Draw as usual.
}
```

Moving between states never deletes anything by itself. There's one game, so whatever
isn't tied to a state simply carries through: a player, an inventory, a score. Nothing has
to be handed from one scene to the next. To remove what belonged to a state, do it in
`OnExit`, or tag ECS entities with [`ScopedTo`](ecs.md#entities-that-belong-to-a-state).

A game starts in the first value of the enum, unless `Load` calls `Set`. Its `OnEnter`
callbacks run after `Load`, before the first tick.

## How a change lands

`Set` is a request. The rest of the tick finishes in the old state, then the exit
callbacks run, then the enter callbacks, and the `Draw` after that shows the new state. So
every system in a tick sees the same state, whatever order they run in.

- `Is` is how a system says where it runs. It's a plain `if`, so you still decide what
  runs and in what order.
- `Current` is the state now, and `Previous` is the state you came from, so entering the
  house can put the player at the right door.
- `Set` from an enter callback moves straight on, in the same tick. More than 32 changes
  in one tick throws, because that's two states sending each other back and forth.
- `Set` to the current state does nothing. If a tick calls `Set` twice, the last one wins.
- `Set` in `Draw` or `Ui` throws, and enter and exit callbacks can't read input, for the
  same reason `Load` can't: input belongs to `Update`.

Pausing is a second enum, `States<Pause>`. Each enum runs on its own, so a game can be
paused in any area without a state for each combination.

In tests, `run.States<Screen>()` shows where the game is, and can jump there directly so
a test doesn't have to play through the menu:

```csharp
run.States<Screen>().Set(Screen.House);
run.Ticks(1);
```

The [Rooms example](../examples/Rooms) has a menu, a loading screen and two areas, with
one player and a coin count carried between them. Its tests are in
[tests/Rooms.Tests](../tests/Rooms.Tests).

## Loading screens

A loading screen is another state that watches `Content`. Name what the next area needs
on entering, then move on when it's ready.

```csharp
screen.OnEnter(Screen.Loading, () => _houseModel = content.Model("models/house.glb"));

// In Update.
if (screen.Is(Screen.Loading) && content.IsLoaded)
{
    screen.Set(Screen.House);
}

// In Ui.
new Text($"Loading {content.Progress:P0}")
```

Files are read on background threads and sent to the GPU one per frame, so the screen
keeps moving. `Progress` runs from 0 to 1 over everything named since the last time
loading finished, so a second loading screen starts from 0 again.

You don't need a loading screen. A model that's drawn before it's ready is waited for, so
nothing ever pops in. The game just pauses on that frame.

Only the interactive window takes real time to load. Headless runs, `--screenshot`,
`--record` and `--profile` report everything loaded at once, because disk speed mustn't
change which tick a seed leaves the loading screen on. To test a loading screen, hold it
open:

```csharp
run.HoldLoading();
run.Ticks(30);
Assert.True(run.States<Screen>().Is(Screen.Loading));

run.FinishLoading();
run.Ticks(1);
Assert.True(run.States<Screen>().Is(Screen.House));
```

A file that can't be read throws on the main thread, on the frame the problem is found.
Meshes and textures can be let go with `content.Unload` once a game is done with them;
everything else stays for the life of the game.
