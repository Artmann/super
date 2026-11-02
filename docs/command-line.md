# The command line

Every Super game understands the same options, with no code of its own. They suit
scripts, CI and coding agents, and none of them show a window.

```
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --screenshot day-200.png
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --record day-200.mp4 --seconds 20
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --profile day-200.txt
dotnet run -c Release --project MyColony -- --script tooltip --ticks 40 --screenshot tooltip.png
```

| Option | What it does |
| --- | --- |
| `--seed N` | Seeds [`GameRandom`](random.md). The same seed plays the same game |
| `--ticks N` | Plays this many ticks first, as fast as the machine can. Needs `--screenshot`, `--record` or `--profile` |
| `--screenshot file.png` | Saves the picture at that moment, then exits |
| `--record file.mp4 --seconds S` | Saves that many seconds of video, then exits |
| `--fps N` | Frames per second for `--record`, 60 by default |
| `--size WxH` | The picture size, such as `1920x1080`. The game's window size by default |
| `--profile file.txt` | Measures where the time goes. See [Profiling](profiling.md) |
| `--frames N` | How many frames `--profile` measures, 60 by default |
| `--script name` | Plays one of the game's [input scripts](#scripted-input) |
| `--no-vsync` | Plays in the window without waiting for the display |

`--screenshot`, `--record` and `--profile` can't run together. A mistake in the options
stops the game with a message saying what's wrong, such as
`--record needs --seconds.`

## Screenshots

`--screenshot` renders the real picture of that exact moment through the real renderer,
so it needs a GPU. Pictures come straight from the renderer, so other windows can't end up
in them, and the same seed and ticks give the same picture.

When a [test invariant](testing.md#invariants) fails, its message gives the options that
take you back to that tick.

## Videos

`--record` plays to `--ticks`, then saves `--seconds` of video. It needs a GPU, and
[ffmpeg](https://ffmpeg.org) on the PATH (`winget install Gyan.FFmpeg`). A video runs as
fast as the machine can make it, and the same seed gives the same frames.

## Scripted input

`--script` plays a named list of key presses, mouse moves, clicks and drags, so a
screenshot can show a tooltip, a hover or a drag in progress. A game lists its scripts
in `IGame.Scripts`:

```csharp
public IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>
{
    ["tooltip"] = new InputScript().MoveMouse(SlotCenter(2)).Wait(30),
    ["drag"] = new InputScript().Drag(SlotCenter(0), SlotCenter(5)),
    ["craft"] = new InputScript().Click("craft").Wait(5),
    ["build"] = new InputScript().Press(Key.B).Click("build.Cabin").Wait(5),
};
```

| Step | What it does |
| --- | --- |
| `MoveMouse(position)` | Moves the cursor, in interface pixels |
| `MoveMouse(id)` | Moves the cursor to the middle of the view given `.Id(id)` |
| `Click(position)` | Moves there and clicks the left button |
| `Click(id)` | Clicks the middle of the view given `.Id(id)` |
| `Hold(button)`, `Release(button)` | Presses or lets go of a mouse button |
| `Press(key)` | Taps a key: down on one tick and up on the next. It takes a tick |
| `Hold(key)`, `Release(key)` | Holds a key down, or lets it go |
| `Drag(from, to)` | Presses at one place, moves and lets go at the other |
| `Scroll(notches)` | Turns the wheel |
| `LeaveWindow()` | Moves the cursor out of the window |
| `Wait(ticks)` | Waits before the next step |

An id is looked up when its step plays, in the layout last drawn, so a script needs no
pixel positions and keeps working when the layout changes. An id no view has stops the
run and lists the ids that are on screen. A test plays the same scripts with
[`run.Play(script)`](testing.md#what-a-run-gives-you), counting from the next tick.

Set `--ticks` past the end of the script. While a script plays, the window ignores real
input. Asking for a name the game doesn't have lists the ones it does. The
[Crafting example](../examples/Crafting) has `tooltip`, `dragging`, `scrolled` and `crafted`:

```
dotnet run --project examples/Crafting -- --script tooltip --ticks 40 --screenshot tooltip.png
```

## Playing without vsync

```
dotnet run -c Release --project MyColony -- --no-vsync
```

`--no-vsync` stops waiting for the display, so a game draws as fast as the machine can and
the frame rate shows what it can really do. Fast movement may tear, and displays that
can't run this way keep vsync on. It's for playing in the window, so it can't be combined
with `--screenshot`, `--record` or `--profile`.

## Capturing from code

Ask for `GameCapture` in your constructor and call it from anywhere in the game:

```csharp
internal sealed class MyColony(GameCapture capture) : IGame
{
    public void OnRaidStarted()
    {
        capture.Screenshot("captures/raid.png");
        capture.StartRecording("captures/raid.mp4", framesPerSecond: 60);
    }

    public void OnRaidEnded() => capture.StopRecording();

    // Load, Update and Draw as usual.
}
```

- `Screenshot` saves the next frame as a PNG. Folders are created for you.
- While recording, each frame moves the game on by exactly one video frame. Instead of a
  stuttering video you get a game that may run slowly until `StopRecording`.
- `StopRecording` waits for the file to be finished. Closing the game finishes it too.
- Resizing the window ends the recording, and the video stops at that point.
- In a headless test there's nothing to capture, so these calls do nothing.

## Planned

`--headless` and `--json`, for a run with no GPU that prints a result a script can read,
aren't built yet:

```
dotnet run --project MyColony -- --headless --seed 42 --ticks 20000 --json
{ "seed": 42, "ticks": 20000, "passed": true }
```

Until then, run headless through [a test](testing.md).
