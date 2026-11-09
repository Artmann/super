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
| `--size WxH` | The picture size, such as `1920x1080`, or `1080x1920` for a vertical video. The game's window size by default |
| `--ui-scale N` | Makes the [interface](interfaces.md#scale) this many times bigger, such as `2` or `1.5`. The game's `UiScale` by default, which is 1 |
| `--profile file.txt` | Measures where the time goes. See [Profiling](profiling.md) |
| `--frames N` | How many frames `--profile` measures, 60 by default |
| `--script name` | Plays one of the game's [input scripts](#scripted-input) |
| `--no-vsync` | Plays in the window without waiting for the display |
| `--user-data folder` | Keeps [user data](user-data.md) in this folder. Without it, the window uses the player's folder and runs without a window use a throwaway one |

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

`--record` includes the game's sound. The audio is mixed a tick at a time, like the
picture, and written beside the video as `file.wav`. When the video is finished, ffmpeg
adds the sound to it (the picture isn't re-encoded) and the WAV is deleted. If that
fails, the video is kept without sound, the WAV stays as `file.wav` next to it, and one
line says why:

```
Couldn't add sound to day-200.mp4: {error}. The sound is in day-200.wav.
```

The sound starts with the first picture, so it lines up with the video to within a tick. An existing `file.wav` next to the video is overwritten, and so is the video itself. The audio seed is fixed, so the same seed gives the same sound too.

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
| `MoveMouse(position)` | Moves the cursor, in screen pixels, whatever the [interface scale](interfaces.md#scale) |
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
with `--screenshot`, `--record` or `--profile`. It does the same as setting
[`Graphics.Vsync`](rendering.md#graphics-settings) to false, which a game can also do from code.

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
- Recording includes the game's sound, written to a .wav beside the video and added to it at the end (see `--record` above). In live play the sound device is paused while recording, so the sound is in the video but not through the speakers.
- `StopRecording` waits for the file to be finished. Closing the game finishes it too.
- Resizing the window ends the recording, and the video stops at that point.
- With no window there are no pictures to save, so in a headless test `Screenshot` and `StartRecording` never produce a file.

## Cooking content

`super-cook` turns a game's content sources into the cooked content it reads: a
`content.manifest` and its objects. It's the cooker the build runs, as a command for
scripts and build servers.

```
dotnet run --project tools/Super.Cook.Tool -- --content examples/Crafting/content --out out/content
```

| Option | What it does |
| --- | --- |
| `--content dir` | A content root. Give it once for each root. At least one is needed |
| `--only glob` | Cooks only the files of the root before it that match, such as `props/**`. Give it once for each glob. See below |
| `--out dir` | Where the cooked content goes. Needed |
| `--target name` | The platform to cook for, `desktop` by default |
| `--cache dir` | Keeps cooked results in this folder between runs. See below |
| `--no-cache` | Cooks everything again and keeps nothing. Can't be used with `--cache` |
| `--dev-file path` | Also writes `content.dev.json` there, naming the roots, their `--only` globs, the tool and the target, for a game run from source |
| `--watch` | After the first cook, cooks again whenever a source changes, until stdin closes |
| `--workers n` | How many sources to cook at once. The default is one per processor, at most 8 |
| `--timings` | Prints where the time went. See below |
| `--verbose` | Prints a line for each source too, with its phases and why it wasn't taken from the cache |

Each problem prints as one line that MSBuild and editors link to the file, with the line
when there is one:

```
C:\game\content\items\axe.json(3): error SUPER001: 'o' is an invalid start of a property name.
```

The last line counts what happened and how long it took, such as `Cooked 2, from cache 56, 0 errors, in 0.4 s.` The exit
code is 0 when it cooked and 1 on any error. A failed cook leaves the content folder as
it was, so the last good content stays. A mistake in the options prints what's wrong and
how to use it.

### The cache

Cooking is a pure function of the sources and their settings, so the cooked result of a
file that hasn't changed is kept and used again. The cache is in `~/.super/cache`, or in
the folder in the `SUPER_CACHE` environment variable when that is set, so a build server
can keep it somewhere of its own. `--cache` picks a folder for one run. Deleting the cache
is always safe: the next cook makes it again.

The content folder's objects are hard links to the cache's copies when the two are on the
same drive, so a cooked texture takes its space once, not once per game. When a link can't
be made, such as across drives, the object is copied instead. Objects are never changed in
place, only replaced, so a linked file can't change under the other name.

### Cooking part of a root

A shared asset folder can hold far more than one game uses. `--only` after a `--content`
picks which of that root's files are cooked:

```
super-cook --content assets --only "fox/**" --only "props/rock_*/**" --out out/content
```

The globs match names from the root, the same way `cook.json` rules do. Files left out are
still there for the cooker, so a model still finds the textures beside it, but nothing
names them in the game. A glob that matches nothing is a warning, since it's most likely a
typo. Each root has its own globs; a root with none is cooked whole.

### Where the time goes

Sources cook on several workers at once, one per processor up to 8, and `--workers` changes
that. The output is the same whatever the number: problems print in the order of the sources,
and the manifest is byte for byte the same.

`--timings` prints a summary before the last line. It lists the time of each step of the
cook, the time each phase took added up over every source (decoding images, building mips,
the Assimp import, writing the store and so on), the slowest sources with the size of what
they made, and how many sources weren't taken from the cache, grouped by why:

```
Cook took 3.104 s with 8 workers.
  list sources                     0.004 s
  read settings                    0.021 s
  cook                             3.010 s
  save cache index                 0.012 s
  write manifest                   0.057 s
Time in each phase, added up over every source (more than the wall clock when cooking in parallel):
  write store                      9.870 s
  model cooker, untimed            2.112 s
  ...
Slowest 15 cooked:
    1.204 s    42.7 MB  models/remains/skeleton/skull.glb
  ...
Why sources weren't taken from the cache:
    233  no record
      4  input changed
```

"No record" means the source is new, or its bytes, settings or cooker version changed.
"Input changed" means a file it reads besides itself, such as a texture, changed.
`--verbose` adds one line for each source, with its phases and the reason in full. A cook a
build or a game starts has no options of yours, so `SUPER_COOK_TIMINGS=1` and
`SUPER_COOK_VERBOSE=1` in the environment turn the same output on there.

### Watch mode

`--watch` cooks once, then watches every root and its folders. When the files have been
quiet for a tenth of a second it cooks again, printing problems the same way, then
`Cooked. 2 cooked, 56 from cache, in 0.412 s.` or `Not cooked: 1 errors. Took 0.003 s.` A failed cook keeps the old manifest. On Windows, a
file another program has open, such as an editor in the middle of saving or the game
reading `content.manifest` just as the new one is written, is tried again after the same wait. Any other failure prints `Not cooked:` and why, and waits for the next
change. It stops when stdin closes, so the game that started it ends it, or when it's killed.

### One asset

```
dotnet run --project tools/Super.Cook.Tool -- file --content assets fox/fox.glb fox.rmodel
```

`super-cook file --content dir name output` cooks the roots in memory and writes the bytes
of one asset, or one part named `file#part`, to the output file. A model is written with
its textures inside, so the file stands alone; in a content folder, models point to
textures they share instead. `--content` can be given more than once. It fails when nothing cooked has that name, and lists the parts when the
name was cooked in parts.

### In the build

A game imports `src/Super.Build/Super.targets` and `dotnet build` cooks its content:

```xml
<Import Project="..\..\src\Super.Build\Super.targets" />
<ItemGroup>
  <!-- Optional. With none, the "content" folder beside the project is used when it exists. -->
  <SuperContent Include="content" />
  <SuperContent Include="..\..\assets\content" />
  <!-- Only cooks part of a root: globs split by semicolons. -->
  <SuperContent Include="..\..\assets" Only="props/**;textures/**" />
</ItemGroup>
```

`Only` turns into `--only` options for that root, and `content.dev.json` keeps them, so the
cook a game starts while it runs from source picks the same files.

After the build, the cooked content is in `bin/<config>/content` and `content.dev.json`
is beside it. Cook errors are build errors with file and line. `SuperTarget` picks the
target, `desktop` by default. `dotnet publish` copies `content` beside the program and
leaves `content.dev.json` out, so a shipped game never thinks it's running from source.

The build restores and builds `super-cook` itself, for the machine doing the build, so a
fresh clone needs nothing first and `dotnet publish -r linux-x64` still cooks on Windows.
Every game in one build shares the one tool build. None of the tool's files end up beside
the game.

`dotnet publish` with a runtime, such as `-r win-x64`, compiles the game ahead of time
(ReadyToRun), so the shipped game reaches its first frame sooner. On this repo's Fox example
that was about 0.7 s instead of 0.87 s. A publish with no runtime is left as it is, since
ReadyToRun needs one. To turn it off, set
`<PublishReadyToRun>false</PublishReadyToRun>` in the game's project.

## Planned

`--headless` and `--json`, for a run with no GPU that prints a result a script can read,
aren't built yet:

```
dotnet run --project MyColony -- --headless --seed 42 --ticks 20000 --json
{ "seed": 42, "ticks": 20000, "passed": true }
```

Until then, run headless through [a test](testing.md).
