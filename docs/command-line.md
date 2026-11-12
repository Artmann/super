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
| `--ticks N` | Plays this many ticks first, as fast as the machine can. Needs `--screenshot`, `--record`, `--profile`, `--bake` or `--bake-check` |
| `--screenshot file.png` | Saves the picture at that moment, then exits. It first waits for the light probes near the camera to settle, up to 2,048 frames. Without tracing (`--no-trace`, or a GPU that can't trace), a dense scene such as Courtyard can reach that cap first, and then the shot shows the probes still filling in and the console says so. See [Screenshots](#screenshots) |
| `--record file.mp4 --seconds S` | Saves that many seconds of video, then exits |
| `--fps N` | Frames per second for `--record`, 60 by default |
| `--size WxH` | The picture size, such as `1920x1080`, or `1080x1920` for a vertical video. The game's window size by default |
| `--ui-scale N` | Makes the [interface](interfaces.md#scale) this many times bigger, such as `2` or `1.5`. The game's `UiScale` by default, which is 1 |
| `--profile file.txt` | Measures where the time goes. See [Profiling](profiling.md) |
| `--frames N` | How many frames `--profile` measures, 60 by default |
| `--script name` | Plays one of the game's [input scripts](#scripted-input) |
| `--render-scale N` | Draws the scene this many times the window's size each way, from 0.5 to 2, such as `2` for smoother edges or `0.5` for speed. The picture saved is still `--size`. Same as [`Graphics.RenderScale`](rendering.md#graphics-settings); the game's own setting by default, which is 1 |
| `--upscaler taa\|fsr` | Picks what smooths edges and, below a render scale of 1, rebuilds the picture at the window's size: the engine's TAA or AMD FSR. Same as [`Graphics.Upscaler`](rendering.md#upscaling); the game's own setting by default, which is TAA. Anything else is refused |
| `--no-vsync` | Plays in the window without waiting for the display |
| `--no-materials` | Draws every surface as plain grey clay, to judge the lighting. Works in the window and with `--screenshot`, `--record` and `--profile`. Same as [`Graphics.Materials`](rendering.md#graphics-settings) set to false |
| `--no-trace` | Makes the GPU's device without ray queries, even if it has them, to see what a player whose GPU can't trace gets. Works in the window and with `--screenshot`, `--record` and `--profile`. Without rays the light probes are drawn a few a frame instead of traced, so the bounced light fills in and follows changes more slowly, and `--debug-view trace` can't run. Nothing is printed at start; why tracing is off is only said when something asks for it, as `--debug-view trace` does. See [ray tracing](rendering.md#ray-tracing) |
| `--debug-view trace` | Shows what a ray from the camera hits in the ray tracing layer, as each surface's base colour with no lighting, and sky blue where it hits nothing, to check that rays see the scene you see. Leaves out skinned and swimming draws and ghosts, and rays pass through the holes in cut-outs. Works in the window and with `--screenshot` and `--record`. With `--profile` it runs too, so the profile times the trace view, not the lit scene. Needs a GPU that can trace, so with `--no-trace`, or on a GPU that can't, the game exits at once with `--debug-view trace needs a GPU that can trace rays, and this one can't (reason).` Any other name than `trace` or `probes` is refused. See [the debug view](rendering.md#ray-tracing) |
| `--debug-view probes` | Draws the [light probes](rendering.md#bounced-light) of the finest box round the camera and of your own `view.LightProbes` box over the lit scene as small spheres (the coarser boxes aren't drawn), each shaded with the light its probe holds for every direction, so you can see where the bounce comes from. A probe not drawn yet is a grey wireframe, and one inside a wall isn't shown. Needs no tracing, so it works with `--no-trace` too. Shows no spheres while `Graphics.LightProbes` is off. Works in the window and with `--screenshot`, `--record` and `--profile` |
| `--user-data folder` | Keeps [user data](user-data.md) in this folder. Without it, the window uses the player's folder and runs without a window use a throwaway one |
| `--bake` | Bakes the frame's [baked space](rendering.md#baked-lighting) and each [baked chunk](rendering.md#baked-lighting) it draws into the first content root, then exits. See [Baking lighting](#baking-lighting) |
| `--bake-check` | Exits with 1 when the frame's baked space or any chunk it draws has no bake or a stale one, saying what changed |
| `--bake-quality preview\|final` | How long `--bake` traces: `preview` for a quick look, `final` for the one to keep. `final` by default. Needs `--bake` |

`--screenshot`, `--record` and `--profile` can't run together, and `--bake` and `--bake-check`
run with none of them, nor with each other. A mistake in the options
stops the game with a message saying what's wrong, such as
`--record needs --seconds.`

## Screenshots

`--screenshot` renders the real picture of that exact moment through the real renderer,
so it needs a GPU. Pictures come straight from the renderer, so other windows can't end up
in them, and the same seed and ticks give the same picture, down to the byte, for a game
built with [`Super.targets`](#in-the-build).

Before it saves, `--screenshot` renders on, at that moment, until the
[light probes](rendering.md#bounced-light) near the camera have all been drawn, up to 2,048
frames, so the bounced light is as it will be rather than still filling in. It prints how many
frames that took, or that the probes were still filling in at 2,048, which a big scene such as
Courtyard can reach at `ProbeQuality.High`. With `Graphics.LightProbes` off, or on a GPU that
can't run the probes, it doesn't wait. Those extra frames don't wait for the display, so the
wait is as quick as the GPU can draw them: Gallery with `--no-trace` takes about 13 seconds in
all. `--record` doesn't wait for the probes.

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
| `Drag(fromId, toId, button)` | The same between two views found by id, with any button (left if left out) |
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
| `--no-cache` | Cooks everything again and keeps nothing. Can't be used with `--cache` or `--cache-limit` |
| `--cache-limit size` | The most the cache keeps after the cook, such as `8GB`, `500MB` or `none`. 4 GB by default. See below |
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

A cooker can also leave a note, which says what it did rather than what's wrong, such as
making a [second UV set for lighting](rendering.md) a model lacked. Notes print before the
problems as `<file>: note: <message>`. They aren't warnings, so MSBuild shows them as plain
messages, and they print only on the cook that did the work, not when the source comes from
the cache. Watch mode prints them the same way.

The last line counts what happened and how long it took, such as `Cooked 2, from cache 56, 0 errors, in 0.4 s.` The exit
code is 0 when it cooked and 1 on any error. A failed cook leaves the content folder as
it was, so the last good content stays. A mistake in the options prints what's wrong and
how to use it.

### The cache

Cooking is a pure function of the sources and their settings, so the cooked result of a
file that hasn't changed is kept and used again. The cache is in `~/.super/cache`, or in
the folder in the `SUPER_CACHE` environment variable when that is set, so a build server
can keep it somewhere of its own. `--cache` picks a folder for one run. Deleting the cache
is always safe: the next cook makes it again. A change to the cook's own code misses the
entries it affects, but a commit that doesn't touch that code keeps the cache.

A source that does cook again keeps its slow steps' results in the cache's `steps` folder too,
each under the hash of everything that step reads. A model whose one material graph changed
runs only that graph again: the other graphs' images, its baked masks and every texture whose
pixels didn't change come from there, so editing one graph of the Courtyard recooks in well
under a minute rather than many. Each kind of step has its own key:

| Step | Kept by |
|---|---|
| `graph-run` | The graph's code, a variant's values, the size, the cook's version, and the hash of every image the graph read |
| `lightmap-uv` | One material's positions, normals and indices in the model, the unwrapper's and the model cooker's builds and the largest page it may try |
| `mask` | The model's positions and second UVs, the material's triangles and which part each is in, the other triangles the bake traces against, the mask size and the mask baker's build |
| `compressed-texture` | The hash of the pixels of every mip level, the size and format, and the encoder's version. A cut-out's mips depend on its `alphaCutoff`, so changing the cutoff misses |

A damaged entry is a miss, never a wrong result. `--timings` shows the time spent looking
up and keeping each, as the `recall` and `keep` phases below. A compressed texture is kept
once, as the cache's copy of the object the model points to, and a graph run's images are
kept compressed, at about a fifth of their size.

The cache is kept under a limit, 4 GB by default. After each cook the files used least
recently are deleted until it fits, whole records, objects and step results, so the next
cook that needs one of them cooks it again. What the cook just used is never deleted, so a
game bigger than the limit still cooks warm the second time; the cook then warns once that
the cache is over its limit and how to raise it. Set the limit with `SUPER_CACHE_SIZE`, such
as `SUPER_CACHE_SIZE=8GB`, or for one run with `--cache-limit`. Sizes are KB, MB, GB or TB,
each 1024 of the one before, with a `.` for decimals in any language, such as `1.5GB`.
`none` or `0` turns the limit off. A `SUPER_CACHE_SIZE` that isn't a size gives a warning
and the default limit, for the tool, a build and a game alike, and isn't read at all by a
cook with `--no-cache`. A `--cache-limit` that isn't a size stops the tool.

`super-cook cache` shows where the cache is and what it holds:

```
super-cook cache
C:\Users\you\.super\cache
1.3 GB in 417 files, limit 4.0 GB.
  objects                         1.0 GB     247 files
  steps/graph-run                 257 MB      23 files
  steps/mask                       24 MB       6 files
  records                          35 KB      38 files
  steps/compressed-texture        9.6 KB     102 files
  other                           8.7 KB       1 file
```

`super-cook cache --trim` trims it to its limit now, or to `--cache-limit` when given. In
case a cook is running it keeps every file from the last two hours, which covers every file
used in the last hour, since a cook only marks a file used when it's more than an hour old,
and it says so when that leaves the cache over the limit. `super-cook cache --clear`
deletes it. Both take `--cache` for a cache somewhere else. They only delete files named as
the cache names them, and leave anything else in that folder, listed by `super-cook cache`
as "not the cache's". They refuse a cooked content folder, one with a `content.manifest`,
since its objects are named as the cache's are.

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
and the manifest is byte for byte the same. The slowest sources start first: the cache folder
keeps how long each one took in `durations.json`, and sources it has no time for start first,
models before the rest and then the biggest files. A graph that several sources run at the
same size, such as a material's own graph that a model also uses, usually runs once: its
run is kept for the others. The kept runs are capped at 1 GiB, and past that the oldest are
dropped, so a big cook may run such a graph again.

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
  trim cache                       0.025 s
  write manifest                   0.057 s
Cache 1.3 GB of 4.0 GB.
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

A model's phases include `run graphs`, `bake masks`, `build mips` and `compress textures`, the
slow steps of a model with material graphs, each with a `recall` phase before it and a `keep`
phase after it for the step cache. On the Courtyard most of a cold cook is `run graphs`
and `bake masks`. A model runs several graphs and bakes several materials' masks at once, so
those phases can add up to more than the model's own time. Across the whole cook, at most
half the processors, from 2 to 8, run a graph at once, and as many bake masks, which keeps
the memory a cook takes in bounds; a cold cook of the Courtyard peaks near 6 GB. `wait for shared graph runs` is
time spent waiting for a graph run that another source of the cook is making at the same size. A model with an `alphaCutoff` also has `keep alpha coverage`, the time
spent rescaling its cut-out textures' mips so they don't thin out in the distance.

The line after the steps is the cache's size after trimming, and how many files the trim
deleted, such as `Cache 3.9 GB of 4.0 GB: deleted 12 least recently used files, 1.1 GB.`

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
The tool always builds in Release, whatever the game's configuration, since running material
graphs and baking masks take about twice as long in Debug. That holds in a solution build too,
such as `dotnet build Super.slnx -c Debug`: the game still runs the Release tool, built with
Release cook libraries. A solution that lists `super-cook` also builds it in its own
configuration, beside the Release one, which the game doesn't use.
Every game in one build shares the one tool build. None of the tool's files end up beside
the game. `dotnet watch` and `dotnet build -graph` work too: there the build makes the tool
itself, the same way.

`dotnet publish` with a runtime, such as `-r win-x64`, compiles the game ahead of time
(ReadyToRun), so the shipped game reaches its first frame sooner. On this repo's Fox example
that was about 0.7 s instead of 0.87 s. A publish with no runtime is left as it is, since
ReadyToRun needs one. To turn it off, set
`<PublishReadyToRun>false</PublishReadyToRun>` in the game's project.

`Super.targets` also turns off tiered compilation, so the game's code is compiled once, fully
optimized, instead of quickly first and again once it runs often. The second compilation can
round a sum differently, and when it happens depends on the clock, so with it on, the same
`--seed` and `--ticks` gave a slightly different picture from run to run. On the Courtyard
the game starts as fast without it, and a frame costs the CPU no more. To turn it back on, set
`<TieredCompilation>true</TieredCompilation>` in the game's project.

## Baking lighting

```
dotnet run --project MyGame -- --bake --bake-quality preview
dotnet run --project MyGame -- --bake-check
```

`--bake` plays `--ticks` ticks, then bakes the frame's [baked space](rendering.md#baked-lighting) with
[`Renderer.Bake`](rendering.md#making-a-bake) and saves it in the game's first content root as
`lighting/<name>.lightbake`, replacing what was there. It then bakes each baked chunk the frame
draws with `Renderer.BakeChunk` and saves it as `lighting/<name>/<hash>.lightbake`. Chunks with
the same contents share one file however many copies stand in the frame, so each file is baked
once, in order of first appearance, after the space. Like `--screenshot` it runs without a
window and under validation, and `--seed` and `--script` work as usual. For each file it prints the path,
how long the bake took, the samples each texel got, the share of texels no ray found a front
face for, and the keys of `Mixed` lights that found no shadow mask channel:

```
C:\Code\MyGame\content\lighting\gallery.lightbake: 11.3 MB
  time: 16.3 s
  samples: 1024 a texel
  invalid texels: 5.74%
  Mixed lights without a shadow mask channel: 123, 124, 125, 126
0 validation messages
```

Without `--ticks` it plays two seconds' worth, drawing each frame, so a light that sways or
flickers has been seen to [move](rendering.md#moving-lights) and is left out of the bake, as it
is in play. After a single tick every light would look still, a swinging lantern would be
baked in, and the bake would go stale as soon as the game saw the lantern move. A light that
only starts moving later is still baked in, so pick `--ticks` where every light that will move
already does.

It exits with 1 when there were validation messages. `--bake-quality preview` traces 64
samples a texel, which took 13 seconds on the Gallery; `final`, the default, traces 1,024,
which took 16 seconds there. The
space and each chunk are baked at the texel density they were named with, `texelsPerMetre`,
which is what a frame checks its bake against. A bake that can't be made prints one line to the
error output and exits with 1, with no stack trace, like the other command-line errors. That
covers a frame with neither a baked space nor a chunk ("--bake found no baked space or chunk in
the frame. Name one with view.BakedLighting or view.BakedChunk."), and a space or chunk whose
static draws don't fit its lightmap pages: a space has 8 pages of 2048 texels and a chunk one,
so the line says how many draws were left out and to use fewer texels a metre or a smaller
space. Nothing is saved from such a bake. A chunk that owns no static draws has nothing to bake: `--bake` prints
`lighting/<name>/<hash>.lightbake: no static draws, nothing to bake`, carries on with the other
files, and exits with 1 at the end. `--bake-check` lists such a chunk as missing.

`--bake-check` plays the same way, two seconds' worth unless `--ticks` says otherwise, so the
lights it sees moving are the ones `--bake` left out. It draws the frame and compares what its baked space would be
baked from, and each distinct chunk file, with the header of the bake in the cooked content, the
one the game draws with. It prints one line for each file and exits with 0 when every bake is
fresh, or 1 when any is missing or stale, naming what changed out of `draws`, `lights`,
`environment` and `settings`:

```
lighting/gallery.lightbake: stale, its lights changed
```

A frame with no baked space and no chunk has nothing to check and exits with 0. A bake that can't be read
counts as missing, and the reason goes to the error output. `--bake` needs the game run from
source, where the build writes `content.dev.json` to say where the content is, and a published
game exits with 1 and says so. `--bake-check` works in a published game too, so a release can
be checked as shipped.

A saved bake is cooked with the rest of the content: the build rewrites its irradiance pages as
BC6H and its direction and mask pages as BC7, which takes the Gallery's 11.3 MB down to
4.5 MB. See [Baked lighting](content.md#baked-lighting). Since
`--bake-check` reads the cooked bake, build after `--bake` before checking; `dotnet run` does.

## Planned

`--headless` and `--json`, for a run with no GPU that prints a result a script can read,
aren't built yet:

```
dotnet run --project MyColony -- --headless --seed 42 --ticks 20000 --json
{ "seed": 42, "ticks": 20000, "passed": true }
```

Until then, run headless through [a test](testing.md).
