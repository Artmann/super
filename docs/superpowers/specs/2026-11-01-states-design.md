# Game states design

Without states there's no menu, no loading screen and no moving between areas. This
builds what we agreed on 2026-10-31, after Unity's scene-as-root model came up as the
thing to avoid because carrying state between scenes is painful.

## Agreed

- One long-lived world. The player, inventory and progress are entities or plain classes
  that nobody deletes.
- A "scene" is an active state: menu, loading, house, outside. Systems say which state
  they run in. Entering and leaving run setup and teardown.
- Scene-owned entities carry a tag and are destroyed on leaving. Untagged ones carry
  through for free.
- Loading is another state that polls asset loads.
- Scene files, if they ever exist, are spawn templates and not containers. Not built here.

## `States<TState>`

Injected like `Actions<T>`, for any enum, with no registration.

```csharp
public enum Screen { Menu, Loading, House, Outside }

states.OnEnter(Screen.House, SpawnHouse);
states.OnExit(Screen.House, SaveHouse);
states.Set(Screen.Outside);
if (states.Is(Screen.House)) { ... }
```

- **Systems say where they run with `states.Is`.** There's no scheduler, because the
  game's author owns the order things run in. A plain `if` costs nothing and hides nothing.
- **The first enum value is where a game starts,** unless `Load` calls `Set`. Its enter
  callbacks run right after `Load`, before the first tick and the first Draw.
- **`Set` is a request.** The rest of the tick finishes in the old state. The change lands
  at the end of `Update`, so the Draw after it already shows the new state.
- **Order of a change:** exit callbacks, entity cleanup, `Current` changes, enter
  callbacks. Exit callbacks can still read what's about to be destroyed.
- **`Set` from a callback chains,** so a load with nothing to wait for passes straight
  through. More than 32 changes in one tick throws, since that's a loop.
- **`Set` to the current state does nothing.** Several `Set` calls in a tick: the last wins.
- **`Set` in Draw or Ui throws.** They describe state, the same rule as input.
- **Enter and exit can't read input,** like `Load`. They're setup.
- **Pausing is a second enum.** Machines are independent, so `States<Pause>` needs nothing new.
- `Previous` says where we came from, so entering a house can place the player at the door.

## `ScopedTo<TState>` in `Super.Ecs`

`world.Add(door, new ScopedTo<Screen>(Screen.House))` destroys the entity on leaving that
state. It works with no wiring, because `AddSuperEcs` registers the world as an
`IStateHook`, the same pattern as `ITickHook`.

Children aren't destroyed with a parent. `view.Models` already treats a dead parent as the
world, so tag the children too. Cascading would make destroying an entity cost a search.

A state change also resets smoothing, so a player moved in an enter callback appears at
the door and doesn't glide there across the frame.

## Loading

`Content` only names things. The renderer loads everything named, on the main thread,
during one frame, so a loading screen would freeze. That changes:

- **Files and generated textures are prepared on background threads,** and uploaded to
  the GPU on the main thread, one per frame.
- **A model being drawn is waited for,** so nothing ever pops in and a game with no
  loading screen behaves as it does today, with the same first-frame wait.
- **`content.IsLoaded` and `content.Progress`** report on what's been named since the last
  time everything was loaded, so a second loading screen starts from 0.
- **Only the interactive window streams.** Headless, `--screenshot`, `--record` and
  `--profile` runs report everything loaded at once, because disk speed mustn't change
  which tick a seed leaves the loading screen on.
- **Tests can hold a load open** with `run.HoldLoading()` and `run.FinishLoading()`, so a
  loading screen can be tested.
- **A file that fails to load** throws on the main thread, on the frame it's found.

Content is never unloaded. Memory only grows, and that's left for later.

## Calls made without asking

Everything in the sections above beyond the agreed list. The ones most worth a look: `Is`
in place of per-state system registration, the change landing at the end of `Update`, no
cascade to children, and instant loading outside the window.

## Example

`examples/Rooms`: a menu, a loading screen, and two areas with a player carried between
them, covered by headless tests over many seeds.
