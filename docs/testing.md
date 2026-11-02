# Testing

Reference `Super.Testing` from a normal xUnit test project. Your game runs at full speed
with no window and no GPU, through the same `Update`, `Draw` and `Ui` code.

```csharp
using Super;
using Super.Testing;

public class ColonyTests
{
    [Theory, Seeds(20)]
    public void Nobody_walks_into_a_wall(int seed)
    {
        using var run = HeadlessGame.Create<MyColony>(seed, services => services
            .AddColony()
            .AddSingleton<IWeather, EndlessWinter>());

        run.Invariant("nobody is inside a wall", game =>
            game.Colony.Workers.All(worker => !game.Map.IsWall(worker.Cell)));

        run.Ticks(20_000);

        Assert.Contains(run.Drawn, draw => draw.Key == run.Game.Colony.Hall.Id);
    }
}
```

`[Seeds(20)]` runs the test once for each seed from 1 to 20. Runs share a process and go
in parallel, because Super keeps no global state, so this fits in a pull request check.

## What a run gives you

| Member | What it does |
| --- | --- |
| `HeadlessGame.Create<TGame>(seed, services)` | Starts a game at 30 ticks per second. The callback registers services after Super's own, so it can replace them |
| `run.Ticks(count)` | Plays that many ticks, drawing after each one |
| `run.Game` | Your game object, to read its state |
| `run.Time`, `run.Seed` | The game's clock and seed |
| `run.Invariant(name, game => ...)` | A rule checked after every tick |
| `run.Input` | Plays keys, mouse, gamepad and actions |
| `run.Drawn` | The models the last `Draw` described, with their keys and transforms |
| `run.ShownText` | Every piece of text the interface showed, with where it landed |
| `run.ScreenSize` | The size the interface was laid out for |
| `run.States<T>()` | The [game states](states.md), to read or jump to |
| `run.HoldLoading()`, `run.FinishLoading()` | Holds a [loading screen](states.md#loading-screens) open |
| `run.ContentChanged()` | Acts as if a [content file](content.md#in-tests) was saved |
| `run.Frame(blend)` | Draws once more, between ticks |

## Fakes

Swap any service in one line, as `IWeather` is swapped above. Your game asks for
services in its constructor, so a test decides what it gets.

A test doesn't run your `Program.cs`, so it registers the services your game needs
itself. Keep them in one extension method that both call:

```csharp
public static class ColonyServices
{
    public static IServiceCollection AddColony(this IServiceCollection services) => services
        .AddSuperEcs()
        .AddSingleton<Colony>()
        .AddSingleton<IWeather, SeasonalWeather>();
}

// Program.cs
Game.StartWindowsGameLoop<MyColony>(new GameOptions { Title = "Colony" }, services => services.AddColony());

// A test, with a fake added after the real one so it wins.
using var run = HeadlessGame.Create<MyColony>(seed, services => services
    .AddColony()
    .AddSingleton<IWeather, EndlessWinter>());
```

## Invariants

Invariants run every tick, so they catch a problem when it happens, not at the end of
the run. A failure says how to get back to that moment:

```
Invariant failed: nobody is inside a wall
  seed 17, tick 8,932
  replay: --seed 17 --ticks 8932 --screenshot failure.png
```

Run your game with those options and you get a picture of the exact frame where the rule
broke. See [the command line](command-line.md).

## Playing input

`run.Input` queues input for the next tick:

```csharp
run.Input.Press(Key.Space);                              // tap a key
run.Input.Hold(Key.W);                                   // keep it down
run.Input.Release(Key.W);
run.Input.Click(MouseButton.Left, at: new Vector2(640, 360));
run.Input.Scroll(-2f);
run.Input.Stick(GamepadStick.Left, new Vector2(0f, 1f));
run.Input.Type("hello");

// Actions directly, so the test still passes when the bindings change.
run.Input.Press(Control.Jump);
run.Input.Hold(Control.Move, new Vector2(1, 0));
run.Ticks(1);
```

## Checking the screen

Layout and fonts are real in a headless run, so a test can check the interface the
player would see:

```csharp
run.Ticks(1);
var title = Assert.Single(run.ShownText, shown => shown.Text == "Rooms");
Assert.True(title.Frame.Width < run.ScreenSize.X);
```

To check the 3D world, look through `run.Drawn`:

```csharp
Assert.Contains(run.Drawn, draw => draw.Key == doorKey);
```

For the real picture, use [`--screenshot`](command-line.md), which needs a GPU.

## A whole example

The [Rooms tests](../tests/Rooms.Tests/RoomsTests.cs) walk a player through a menu, a
loading screen and two areas by playing input, and check states, text and drawn models:

```csharp
[Fact]
public void Starting_goes_through_loading_to_the_house()
{
    using var run = HeadlessGame.Create<RoomsGame>(1, services => services.AddSuperEcs());
    run.HoldLoading();
    run.Input.Press(Control.Start);
    run.Ticks(1);
    Assert.True(run.States<Screen>().Is(Screen.Loading));
    Assert.Contains(run.ShownText, shown => shown.Text == "Loading 0%");

    run.FinishLoading();
    run.Ticks(1);
    Assert.True(run.States<Screen>().Is(Screen.House));
}
```

## Repeatability

Runs repeat exactly on the same build and the same kind of CPU. Different CPU types can
round decimal numbers differently, so don't compare runs across them. The guarantee
depends on two rules:

- Take time from `GameTime` and randomness from `GameRandom`, never from the system
  clock or `System.Random`.
- `Draw` and `Ui` only read state. View memory is never part of the simulation.

## Planned

Run logs, for recording game events and checking them in a test, aren't built yet:

```csharp
// Planned: not in Super yet.
log.Record(new TreeChopped(worker.Id, time.Tick));
Assert.True(run.Log.Contains<TreeChopped>());
```
