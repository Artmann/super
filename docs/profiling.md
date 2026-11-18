# Profiling

`--profile` plays to `--ticks`, then measures `--frames` frames (60 by default) and writes
a short text report. The game needs no setup, and game and engine code appear in the same
call stacks. Use a Release build, because Debug timings mean very little.

```
dotnet run -c Release --project examples/Reef -- --seed 7 --ticks 360 --profile reef.txt
```

```
Reef: seed 7, from tick 360, 1280x720, 60 frames

Frame: 13.1 ms on average (76 fps), fastest 1.1, slowest 27.9
  Update       2.47 ms  (30 ticks in all)
  Draw         0.19 ms  (the game's Draw, and the interface)
  Render      10.49 ms  (of which 9.47 ms waiting for the GPU and the display)
  Allocated     792 bytes per frame

Limited by the GPU: CPU busy 3.7 ms, GPU busy 7.0 ms per frame, enough for about 143 fps.

GPU: 6.98 ms per frame
  Shadows               2.62 ms   37%
  G-buffer              2.57 ms   37%
  ...

Scene: 6,465 models, 12,432,284 triangles at full detail
Drawn after culling and detail: 5,663,912 triangles for the camera, 4,201,244 for shadows
   80%   6,000 x      1,656  reef/tilapia/tilapia.glb
   12%     240 x      5,989  reef/tuna/tuna.fbx
  ...

   Self   Total  Function
 73.8%   79.7%  Rendition.Renderer.Render
 17.8%   18.1%  Reef.Boids.Update
  ...
```

After that come the most common call stacks. The report is plain text, so a coding agent
can read it and act on it directly.

## Reading it

- **Busy times are the numbers to trust.** The frame time includes waiting for the
  display, so it can stay near the screen's refresh rate however light the game is.
- **The verdict line says what to fix.** "Limited by the GPU" means draw less or draw it
  more cheaply; "limited by the CPU" means look at `Update`, `Draw` and the function
  table.
- **Waiting counts as time in the function table.** That's why `Renderer.Render` leads
  it above. Look past it to the functions doing work.
- **Native code shows up as the C# call that entered it.** The samples stop at managed
  code, so time inside the GPU driver lands on the function that called it.
- **A profile run differs a little from a normal one.** It's a hidden window with Vulkan
  validation off, and it asks the GPU for timestamps and to count the shaders each pass runs.

## Comparing before and after

Use the same `--seed`, `--ticks` and `--size` for both runs, so they measure the same
moment of the same game:

```
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --size 1920x1080 --profile before.txt
# make the change
dotnet run -c Release --project MyColony -- --seed 42 --ticks 6000 --size 1920x1080 --profile after.txt
```

A `.nettrace` file lands next to the report. Open it in
[PerfView](https://github.com/microsoft/perfview) to dig further.

For a live figure while playing, show [`GameStats`](interfaces.md#showing-how-the-game-runs)
in a panel. Vsync is off unless the game or `--vsync` turns it on, so the figure is the frame
rate the machine can reach.
To see what an effect costs, turn it off in [`Graphics`](rendering.md#graphics-settings) and
profile again: its pass drops to nothing in the GPU list. Light clusters, Subsurface and the particles in Glass and particles have switches too (`LocalLights`, `Subsurface` and `Particles`), though glass stays in that pass.

"Sky" in the GPU list is the time spent rebuilding the sky's lighting, about half a millisecond
on a frame where the sky, its sun or its colours changed, and about a quarter of a millisecond on
each of the two frames after one while the rougher reflections catch up. A sky that holds still
costs nothing.

"Light probes" in the GPU list is the time spent updating the
[bounced light](rendering.md#bounced-light)'s probes: traced on a GPU that can, drawn otherwise.
Lower `Graphics.ProbeQuality` to update fewer a frame, or turn `Graphics.LightProbes` off.
"Reflection probes" is the time spent photographing [reflection probes](rendering.md#reflections),
one a frame at most, and blurring the photograph.

## Hitches

An average hides a single slow frame, so the report also lists the slowest frames:

```
Stopped until the GPU was idle 0 times in 120 frames. Reflection probe captures: 7.
Slowest frames:
  frame  105     30.8 ms  GPU   23.5 ms  0 idle waits  0 reflection captures
  frame   81     27.9 ms  GPU   22.2 ms  0 idle waits  0 reflection captures
```

An idle wait is the renderer stopping until the GPU has finished everything, as it must to
swap out something a frame in flight still reads, such as making the light probes' storage,
baking a new sky or growing the reflection probes' array. A frame with one is a hitch the
player feels. The GPU times arrive a frame or two late, as the GPU finishes after the CPU.

Every measured frame also goes to a `.csv` file next to the report, one line each, with the
frame's time and its parts, the GPU time, its idle waits and its reflection probe captures,
ready for a spreadsheet or a graph. A [script](command-line.md#scripted-input) can press a key
during the measured frames, so a profile can catch what a change costs as it happens:

```
dotnet run -c Release --project examples/Gallery -- --seed 1 --script dim --ticks 1065 --frames 120 --profile dim.txt
```

The measured frames start after 120 frames of warm-up, which is 60 ticks at the Gallery's 30
ticks a second.
