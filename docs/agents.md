# Working with coding agents

Super is built so a coding agent can make a game the way it makes any other program: by
reading and writing text, running commands and checking the results. There's no editor to
click through and no binary scene to open. A game is C# and JSON, and every result an
agent needs comes back as text or as a picture it can look at.

## The loop

An agent working on a Super game repeats four steps.

**1. Change code or content.** Game logic is plain C# classes and data is JSON records.
Both are ordinary files.

**2. Test without a window.** Headless tests play the real game at full speed with no
GPU, so they run anywhere, including CI and sandboxes:

```
dotnet test -c Release
```

Mark the few slow tests and [leave them out](testing.md#a-fast-run) while you work. A failing [invariant](testing.md#invariants) says which seed and tick broke the rule, and
which options take you back there.

**3. Look at it.** Render the exact moment as a picture or a video, through the real
renderer:

```
dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --screenshot shots/after.png
dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --record shots/after.mp4 --seconds 5
```

The same seed and ticks give the same picture, so a before and after compare like for
like. For several shots of one build, build once and
[start the program itself](command-line.md#screenshots), which skips the seconds `dotnet run`
spends checking every project. To see hover states, tooltips or a drag in progress, play an
[input script](command-line.md#scripted-input) first:

```
dotnet run -c Release --project MyGame -- --script tooltip --ticks 40 --screenshot shots/tooltip.png
```

**4. Measure it.** A profile is a short text report that says whether the CPU or the GPU
is the limit, and which functions take the time:

```
dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --profile shots/profile.txt
```

## Why it works well for agents

| Need | What Super gives |
| --- | --- |
| Know the game still works | Headless tests with seeds, invariants and played input |
| Check the interface without a screen | `run.ShownText`: every label and where it landed, with real layout and fonts |
| Check the world without a screen | `run.Drawn`: every model, key, transform and mesh mask the game described |
| See the game | `--screenshot`, `--record` and `GameCapture`, straight from the renderer |
| Reproduce a bug | One seed and one tick number replay it exactly |
| Fix bad data | `ContentException` lists every problem as `file:line: problem` |
| Write valid data | JSON schemas generated for every catalog, in `.schemas` in the first content root |
| Find a slowdown | `--profile`, as plain text |
| Understand the code | No hidden scheduler and no global state. `Update` calls systems in the order written |

Pictures come from the renderer's own frames, never from capturing the desktop, so an
agent can't accidentally pick up the user's other windows.

## Rules that keep results trustworthy

These matter more when an agent compares one run with another, so put them in your
project's agent instructions:

- Take time from `GameTime` and randomness from `GameRandom`. `DateTime.Now`,
  `Stopwatch` or `System.Random` in game logic make seeds stop repeating.
- Read input only in `Update`. `Draw` and `Ui` only describe state.
- Test actions, not keys: `run.Input.Press(Control.Jump)` still passes when bindings change.
- Use the same `--seed`, `--ticks` and `--size` for before and after.
- Rebuild before trusting a result, because a stale build can pass.

## A starting AGENTS.md

Copy this into `AGENTS.md` or `CLAUDE.md` at the root of your game, and adjust the
names:

````markdown
# MyGame

A game built on Super, a C# game engine used as a library. Docs: <path to super>/docs.

## Commands

- Build: `dotnet build -c Release`
- Test, headless: `dotnet test -c Release`
- Screenshot: `dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --screenshot shots/now.png`
- Video: `dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --record shots/now.mp4 --seconds 5`
- Profile: `dotnet run -c Release --project MyGame -- --seed 7 --ticks 300 --profile shots/profile.txt`

## Rules

- Game state changes only in `Update`. `Draw` and `Ui` only read state.
- Time comes from `GameTime`, randomness from `GameRandom` streams. Never the system
  clock or `System.Random`.
- Data lives in `content/` as JSON, one record per file. Check `.schemas` in the first content root (`content/.schemas`) for
  the fields.
- To see the game, use `--screenshot` or `--record`. Never capture the desktop.
- Every new behaviour gets a headless test in `MyGame.Tests`, using `HeadlessGame` and
  `[Seeds]`.
````

## Where to look next

- [Testing](testing.md) for everything a headless run can check.
- [The command line](command-line.md) for every option.
- [JSON content](content.md) for schemas and error messages.
- The [examples](../examples) are small, complete games that are good to copy from.
