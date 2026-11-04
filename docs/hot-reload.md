# Change code while the game runs

Run the game with `dotnet watch` and save a C# file: the change is applied to the running
game, usually within a second, without closing the window. The world, the player and
everything else stay where they were.

```
dotnet watch --project examples/Crafting --non-interactive
```

This is .NET's own hot reload, so an IDE's hot reload button works too. Super adds three
things on top: an error stops the game instead of closing it, a restart keeps the seed,
and the game is told when its code changed. [JSON content](content.md#change-it-while-the-game-runs)
reloads as well, as it does in any run from source.

## What changes live

Edits to method bodies apply at once: `Update`, `Draw`, `Ui`, systems, a formula, a
colour. The next tick runs the new code. You can also add methods, fields, properties
and classes.

Code that already ran doesn't run again by itself. `Load` and a state's enter callbacks
ran before the edit, so a change to them only shows the next time they run. To see it now,
rebuild the state when the code changes:

```csharp
public class MyGame(States<Screen> screen) : IGame
{
    public void CodeReloaded() => screen.Reenter();
}
```

`CodeReloaded` runs at the start of the next tick after a change, before `Update`, on the
game's own thread. [`Reenter`](states.md#entering-a-state-again) runs the current state's
exit and enter callbacks again, and rebuilds the ECS entities scoped to it. Leave
`CodeReloaded` out and nothing is rebuilt.

A field you add starts out empty in objects that already exist: `null`, `0` or `false`.
Its initializer only runs for objects made after the change. Give it a value in
`CodeReloaded`, or reenter the state that makes the object.

## What needs a restart

Some edits can't be applied to a running program. `dotnet watch` calls them rude edits.
With `--non-interactive` it restarts the game straight away; without it, it asks first.
The usual ones are:

- changing a `const`, or a field of a `struct`
- changing a method's parameters or return type
- changing what a class inherits

The console tells you which edit it couldn't apply.

A restart builds the game again and starts it from `Load`. The seed stays the same, so
the world comes back as it was. The console says `seed 1234, kept from before the
restart`. A new `dotnet watch` picks a new seed, and `--seed` still picks one yourself:

```
dotnet watch --project examples/Crafting --non-interactive -- --seed 7
```

If the restart's build fails, the game stays closed. Warnings are errors in Super, so an
unused variable left behind by an edit is enough. Fix the error and save, and `dotnet
watch` builds and starts the game again.

## When the code throws

With hot reload on, an exception in `Update`, `Draw` or `Ui` stops the game instead of
closing it. The console prints `Hot reload is on, so an error stops the game until the
next change instead of closing it.` when the game starts.

When the game stops:

- Ticks stop, so nothing moves and no time passes in the game.
- A red panel shows the exception, the first lines of the stack trace and what to do.
- The error is printed to the error output once.
- If `Update` threw, the world is still drawn behind the panel. If `Draw` or `Ui` threw,
  they aren't called again until a change, so the error isn't repeated each frame.

Fix the code and save. When the change is applied, the game carries on from the tick it
stopped on. A saved content file also lets it try again. If it still throws, it stops
again and says so.

The tick that threw ended part way, and its changes stay. The game carries on from there
rather than going back. If that left the game in a bad state, reenter the state or
restart.

An exception in `Load` isn't caught: the game closes, and `dotnet watch` starts it again
after the next save.

Screenshots, recordings, profiles and headless runs never keep going after an error, so
a broken game still fails loudly there. In tests, `run.CodeChanged()` acts as if hot
reload changed the code, so `CodeReloaded` can be tested:

```csharp
run.CodeChanged();
run.Ticks(1);
Assert.Equal(Screen.House, run.States<Screen>().Current);
```

## Where it works

Hot reload runs on the desktop window, from source, under `dotnet watch` or an IDE. A
plain `dotnet run` and a published game don't have it, and they close on an error as
before.
