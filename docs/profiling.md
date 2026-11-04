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
  validation off, and it asks the GPU for timestamps.

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
in a panel, and run with `--no-vsync` to see the frame rate the machine can reach.
