# Bakes Without a Build Step Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A game run from source bakes its missing or stale spaces and chunks by itself in the background, a published game bakes new chunks on arrival into a small disk cache, every arriving bake fades in, and the Crypt and the Courtyard become baked spaces.

**Architecture:** Today `Renderer.Bake` (and plan 6's `BakeChunk`) run the whole baker at once through `SubmitAndWait`. Task 1 turns that work into a `BakeJob`, a list of GPU steps that can stop between any two, and drives it to the end for the old synchronous calls, so there stays one baker. Task 2 adds `Renderer.AutoBake`: once a frame it lists the areas whose bake is missing or stale, picks one with `BakeQueue`, and records a slice of that job into the frame's own command buffer, sized by GPU time. Readbacks wait out the frames in flight instead of the GPU. Task 3 fades a bake in when it arrives. Super then decides where bakes go (task 4 is the disk cache, task 5 wires development and published games, the switches and the overlay), and task 6 bakes the Crypt and the Courtyard, with shots in task 7.

**Tech Stack:** C# / .NET, Silk.NET Vulkan (ray queries), GLSL to SPIR-V, Super's `Ui` views, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`, Order steps 8 and 9, and the Crypt and Courtyard part of step 10. It argues from "Baking without a build step" (all three subsections), "Switches", the anti-goals on hitches and stale bakes, and "Examples". Roadmap row: plan 7. It builds on plan 6 (`docs/superpowers/plans/2026-11-12-baked-chunks.md`) and uses its Interfaces as written: `Renderer.BakeChunk`, `BakeFrame.ChunkFile`, `ChunkBakePool`, `BakeFrame.ChunkPending`, `ChunkData`, and the edge weight in `outBaked.a`.

---

## Global Constraints

- Put braces on every control statement. Use conventional commits, with no Co-Authored-By lines and no Claude author.
- Change the docs in the same task as the code. Before each commit, search `docs/` for the names you touched.
- Every new effect gets an on/off switch whose default looks best: `Graphics.BakeOnArrival` (on) and `Graphics.BakeBudget` (see Calls made 3). The fade is turned off by `Renderer.BakeFadeSeconds = 0`, which Super does only for captures.
- Spec values, verbatim: development bakes run "within `Graphics.BakeBudget` milliseconds of GPU time (default 4)", "preview first, then final", "one at a time, nearest to the camera first", "fading in over half a second", overlay text `Baking house-garden, 42%`. On a player's machine: "Preview quality within a budget of 2 ms a frame", cache "in the game's local app data, keyed by input hash, capped at 512 MB with the least recently used dropped first". "Spaces are never baked on a player's machine."
- Auto-bake is off in `--screenshot`, `--record`, `--profile`, `--bake`, `--bake-check` and headless runs. Only `GameWindow.Run` turns it on, as it does `Content.GoLive`.
- A GPU that can't trace never bakes and draws exactly as today. Validation stays clean with `--no-trace`.
- GPU tests go in `tests/Rendition.Gpu.Tests` with `[Trait("Needs", "GPU")]`, `[Trait("Speed", "Slow")]`, `[Collection(GpuCollection.Name)]`, and check `ValidationErrors` didn't grow. CPU tests go in `tests/Super.Tests`.
- Follow the CLAUDE.md test rules: prove each test can fail by breaking the code and reverting, hand-worked values, no fixed sleeps (poll, or a `TimeProvider`), check exception messages, rebuild rather than `--no-build`.
- No hitches. Nothing in `Render` may wait for the GPU, read or write a file, or Brotli-compress because of a background bake.
- Use only `--screenshot` and `--record` to see the game; never capture the desktop. Shots in task 7 only.

## Review Focus

One review, after task 7. Each line names the test that pins it.

1. A bake loop. A bake started before `AnimatedLights.Settled` holds the swinging lantern, so the saved file is stale on arrival and baked again forever. Task 2, `Background_bake_waits_until_lights_settle` and `Saved_bake_that_is_still_stale_is_not_baked_again`.
2. A hitch from a GPU wait or an oversized slice. Task 2, `Background_bake_never_waits_for_the_gpu` and `Background_bake_keeps_every_slice_within_budget`.
3. A dip or flash when a bake arrives, because its Baked lights are dropped before its light is fully shown. Task 3, `Arriving_bake_never_dims_a_pixel_lit_by_a_baked_light`.
4. The cache over 512 MB, or a half-written file after a crash read as a bake. Task 4, `Cache_never_passes_its_capacity` and `Damaged_cached_bake_is_baked_again`.
5. Hot reload mid-bake saving a bake of the old scene as if it were new. Task 2, `Changing_the_scene_mid_bake_restarts_it`.

---

### Task 1: A bake as a job of steps

`Baker` and `Renderer.Bake` wait for the GPU at every step (`SetUpTexels`, each trace batch, `ReadSums`, `FilterAndDilate`, `TraceProbes`). Make the same work a sequence of steps that record into a command buffer the caller gives, so a caller can run one step a frame. Path tracer passes are seeded by their index (`pass` in `bake_trace.comp`), so slicing changes no byte.

**Files:**
- Create: `src/Rendition/Baking/BakeJob.cs`
- Modify: `src/Rendition/Baking/Baker.cs` (step methods replace the waiting ones), `src/Rendition/Renderer.cs` (`Bake` and `BakeChunk` build a job and run it)
- Test: create `tests/Rendition.Gpu.Tests/BakeJobGpuTests.cs`
- Docs: `docs/design.md`, the baker section: steps, readbacks, why bytes don't change

**Interfaces:**

Consumes: plan 6's `Renderer.BakeChunk(in FrameView, ReadOnlySpan<DrawItem>, int chunk, BakeSettings)` and its `AreaJob` split.

Produces:
```csharp
/// <summary>What a step asks of whoever runs the job next.</summary>
internal enum BakeStepResult { Continue, AwaitGpu, Done }

/// <summary>
/// One bake of one area, made from a copy of the frame's draws and lights taken when it was created. Each call to
/// Step records at most one step into the command buffer. AwaitGpu means the next step reads back what this one
/// wrote, so the caller must wait for this command buffer's fence first.
/// </summary>
internal sealed class BakeJob : IDisposable
{
    public string File { get; }                 // BakeFiles.Space(name) or the chunk's file
    public string Name { get; }
    public BakeInputs Inputs { get; }
    public BakeQuality Quality { get; }
    public float Progress { get; }              // 0..1, by traced passes done out of all passes, texels and probes
    /// <summary>Records the next step; a trace step records up to <paramref name="passes"/> path tracer passes.</summary>
    public BakeStepResult Step(CommandBuffer cmd, int passes);
    /// <summary>The file's bytes, once Step has returned Done. Encodes and compresses; call it off the render thread.</summary>
    public byte[] Finish();
    public int PassesInLastStep { get; }        // 0 for a step that traced nothing
}

// In Renderer:
internal BakeJob CreateSpaceJob(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings);
internal BakeJob CreateChunkJob(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings);
```

`Bake` and `BakeChunk` keep their signatures and exceptions: create the job, then loop `SubmitAndWait(cmd => result = job.Step(cmd, batch))` with today's `BakeBatchBudget` sizing until `Done`, then return `Finish()`. Readbacks copy into a host-visible buffer the job owns, and read it in the following step.

- [ ] **Step 1: Write the failing GPU tests.**
  - `Bake_in_single_passes_matches_a_whole_bake`: bake a small Gallery-like scene (a box room, a Baked point light, a Mixed sun) with `Renderer.Bake` at Preview, then with a `BakeJob` stepped with `passes: 1` and a `SubmitAndWait` per step. The two files' pages and draw tables are byte-equal; only `Report.Time` may differ.
  - `Chunk_job_matches_bake_chunk`: the same for plan 6's plane-beside-a-wall chunk.
  - `Every_readback_step_asks_to_await_the_gpu`: step the job, and before each step that reads back (texel read, sums, filter result, probe atlas), the previous result was `AwaitGpu`. Expose `internal bool ReadsBack` on the job's next step for the test.
  - `Job_reports_progress_from_zero_to_one`: progress never goes down, starts at 0 and is 1 at `Done`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeJobGpuTests"`
- [ ] **Step 3: Implement.** Move each `SubmitAndWait` body in `Baker` into a step. The job holds what today's locals hold (the layout, the light buffer, the sum targets, the descriptor sets, the pass counter), and owns its descriptor pools rather than resetting the baker's shared `_tracePool`, so two jobs never share a set. The job copies the frame's draws and lights into arrays when created; that is its only allocation per bake.
- [ ] **Step 4: Run and prove.** Make the background slice's passes start from 0 again at each step (re-seed), see the single-pass test fail; revert. Drop one `AwaitGpu`, see the readback test fail; revert. Run the existing `BakeGpuTests` and `BakeChunkGpuTests`; all pass. Run `dotnet run -c Release --project examples/Gallery -- --bake-check`; it exits 0.
- [ ] **Step 5: Docs and commit.** `git commit -m "refactor: run a bake as a job of steps"`

### Task 2: Background bakes in the renderer

**Files:**
- Create: `src/Rendition/Baking/BakeQueue.cs`, `src/Rendition/Baking/AutoBake.cs`
- Modify: `src/Rendition/Renderer.cs` (after `ResolveBakes`, run the queue and record a slice; hand finished jobs to the thread pool; `Dispose` waits for them), `src/Rendition/Vulkan/VulkanContext.cs` (an internal `SubmitAndWaitCount` counter, for the test)
- Test: create `tests/Super.Tests/BakeQueueTests.cs`, `tests/Rendition.Gpu.Tests/AutoBakeGpuTests.cs`
- Docs: `docs/design.md` "Background bakes"; `docs/rendering.md` "Baked lighting" gets a short "Baking in the background" for Rendition-only users

**Interfaces:**

Consumes: `BakeJob` and `CreateSpaceJob` / `CreateChunkJob` (task 1); `BakeFrame.Space`, `SpaceFresh`, `ChunkFile(int)`, `ChunkFresh(int)`, `ChunkPending(int)` (plan 6); `AnimatedLights.Settled`.

Produces:
```csharp
namespace Rendition;

/// <summary>What the renderer bakes by itself. Null on Renderer.AutoBake bakes nothing. Ignored where CanTrace is false.</summary>
public sealed record AutoBake(
    float BudgetMilliseconds,               // GPU time a frame, measured by timestamps
    bool Spaces,                            // false: only chunks
    IReadOnlyList<BakeQuality> Qualities,   // run in order per area, e.g. [Preview, Final]
    Action<BakeDone> Done);                 // called on the render thread, inside Render

public readonly record struct BakeDone(string File, string Name, BakeInputs Inputs, BakeQuality Quality, byte[] Bytes);

public readonly record struct BakeProgress(string Name, float Fraction, int Waiting, float LastSliceMilliseconds);

// In Renderer:
public AutoBake? AutoBake { get; set; }
public BakeProgress? BakeProgress { get; }   // null when nothing runs or waits

internal readonly record struct BakeWant(string File, string Name, BakeInputs Inputs, int Chunk, Vector3 Centre); // Chunk -1 for the space

internal sealed class BakeQueue
{
    public BakeQueue(IReadOnlyList<BakeQuality> qualities);
    /// <summary>Takes this frame's wants. Returns what to start, or null to carry on (or rest).</summary>
    public BakeWant? Update(ReadOnlySpan<BakeWant> wants, Vector3 camera, out bool cancelRunning, out BakeQuality quality);
    public void Finished(string file, BakeInputs inputs, BakeQuality quality);
    public int Waiting { get; }
}
```

The queue's rules, all from the spec except where Calls made says:
- A want is an area the frame names whose bake is missing or stale, after `AnimatedLights.Settled`. Fresh, `ChunkPending` and unreadable-but-loading areas aren't wants. Copies of a chunk are one want (same file).
- The running job is cancelled when its file is wanted with other inputs ("changing the scene again restarts it"). It carries on when its file isn't named this frame.
- Next to start: every area's first quality before any area's next one, then nearest `Centre` to the camera, ties by file name (ordinal).
- `(file, inputs.Combined, quality)` once finished is never started again, so a saved file that the store hasn't read yet, or one still stale, doesn't loop.

Each frame, `Render` steps the running job in its command buffer between `GpuTimer` marks:
- Trace steps take `passes` passes, starting at 1 and set from the last measured slice: `clamp(budget / perPass, 1, 2 × last)`, as `Baker.Trace` does with wall time. Timings arrive `FramesInFlight` frames late; until then the size stays.
- Other steps are one a frame. `AwaitGpu` makes the job skip frames until its fence has passed. No call to `SubmitAndWait`.
- At `Done`, `Finish` runs on the thread pool; the bytes come back through a `ConcurrentQueue` and `Done` is called at the start of a later `Render`.

- [ ] **Step 1: Write the failing CPU tests** (`BakeQueueTests`).
  - `Nearest_area_starts_first`: three wants at 5, 1 and 3 m start the 1 m one.
  - `Every_preview_runs_before_any_final`: two areas with `[Preview, Final]`; the order of starts is A preview, B preview, A final, B final (A nearer).
  - `Changing_the_scene_mid_bake_restarts_it`: the running file wanted with another `Inputs` sets `cancelRunning` and returns the new want.
  - `Saved_bake_that_is_still_stale_is_not_baked_again`: after `Finished` for every quality, the same want returns null.
  - `Copies_of_a_chunk_are_one_bake`: three wants with one file give `Waiting == 0` after the first starts.
  - `Unnamed_wants_are_forgotten`: a want missing from the next frame's span is not started later.
- [ ] **Step 2: Write the failing GPU tests** (`AutoBakeGpuTests`), each rendering frames until `Done` is called, polled up to a frame limit, never slept.
  - `Missing_space_bake_is_baked_in_the_background`: the box room with no bake and `AutoBake(4, true, [Preview], done)`. `Done` comes within 2000 frames with a file whose `Inputs` equal `LastBakeFrame.Space`.
  - `Background_bake_never_waits_for_the_gpu`: `SubmitAndWaitCount` doesn't move across those frames.
  - `Background_bake_keeps_every_slice_within_budget`: from the fourth slice on, `BakeProgress.LastSliceMilliseconds` stays at or under 1.5 × 4 ms. Fail with the largest.
  - `Background_bake_matches_a_whole_bake`: the bytes equal `Renderer.Bake`'s at Preview, `Report.Time` aside.
  - `Background_bake_waits_until_lights_settle`: no job starts before `Settled`.
  - `No_trace_never_bakes`: with ray tracing off (the `--no-trace` device option), `BakeProgress` stays null and the frame is byte-equal to one with `AutoBake` null.
- [ ] **Step 3: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeQueueTests"`, then `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~AutoBakeGpuTests"`.
- [ ] **Step 4: Implement.** `BakeQueue` keeps its lists and the finished set and reuses them, so a frame with no wants allocates nothing; check it with `GC.GetAllocatedBytesForCurrentThread` in `Unnamed_wants_are_forgotten`.
- [ ] **Step 5: Run and prove.** Sort by distance descending, see the nearest test fail; revert. Drop the finished set, see the loop test fail; revert. Call `SubmitAndWait` for a readback, see the wait test fail; revert. Keep the pass count at 64 a slice, see the budget test fail; revert.
- [ ] **Step 6: Docs and commit.** `git commit -m "feat: bake missing and stale areas in the background"`

### Task 3: A bake fades in when it arrives

**Files:**
- Modify: `src/Rendition/Renderer.cs` (arrival tracking per file, fade in the uniforms and `ChunkData`), `src/Rendition/Baking/BakeFreshness.cs` (`BakeFrame.Fade` per area), `src/Rendition/Shaders/surface.glsl` (`writeBaked`), `src/Rendition/Shaders/lighting.frag` (baked probe weight), `src/Rendition/Shaders/chunks.glsl`
- Test: create `tests/Rendition.Gpu.Tests/BakeFadeGpuTests.cs`
- Docs: `docs/rendering.md` "With a fresh bake", `docs/design.md`

**Interfaces:**

Produces:
```csharp
// In Renderer:
/// <summary>How long a bake that arrives while its area is on screen takes to fade in. 0 shows it at once.</summary>
public float BakeFadeSeconds { get; set; } = 0.5f;   // must be finite and 0..10, else ArgumentOutOfRangeException

// In BakeFrame:
public float SpaceFade { get; }
public float ChunkFade(int chunk);
```

The rules:
- An area arrives on the first frame its bake is drawn after a frame where the same file was named and drawn live. An area fresh on the first frame it is named shows at once, so loading a level, a screenshot and a reload of an unchanged bake never fade.
- Fade is `smoothstep(0, BakeFadeSeconds, view.Time - arrivedAt)`. A clock that goes back counts as arrived.
- `writeBaked` multiplies `outBaked.a` by the fade (for a chunk, after plan 6's edge weight, in `ChunkData.min.w`). The space's baked probe weight is multiplied by it too (`bakedSpaceMax` has no room; add `bakedFade` to the frame uniforms).
- While an area fades, its Baked lights are not dropped. They stay in the frame with radiance × (1 − fade), and `BakeFrame.ModeOf` still says Baked. Then the live direct light and the baked light cross over and nothing dims.

- [ ] **Step 1: Write the failing GPU tests.**
  - `Arriving_bake_fades_in_over_half_a_second`: draw the box room with no bake for a frame, then give the store its bake. At view times +0, +0.25 and +0.5 s, a floor pixel reads the live value, a value strictly between, and the baked value within 1 step.
  - `Bake_fresh_on_the_first_frame_shows_at_once`.
  - `Arriving_bake_never_dims_a_pixel_lit_by_a_baked_light`: a floor lit only by one Baked point light. Over the fade in 0.05 s steps, the pixel never drops more than 3 steps below the smaller of its live and baked values.
  - `Zero_fade_shows_at_once`, and `Negative_fade_throws` with the value in the message.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** Drop the Baked lights at arrival as before, see the dimming test fail; revert. Fade a bake fresh on its first frame, see that test fail; revert. Run `NoBakeGpuTests`; still byte-equal.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: fade a bake in when it arrives"`

### Task 4: The bake cache on disk

**Files:**
- Create: `src/Super/Rendering/BakeCache.cs`
- Modify: `src/Rendition/Baking/LightBakeStore.cs` (`Forget(string file)`), `src/Super/Rendering/SceneRenderer.cs` (`ReadBake` reads cooked content, then the cache)
- Test: create `tests/Super.Tests/BakeCacheTests.cs`, extend `tests/Super.Tests/LightBakeStoreTests.cs`
- Docs: `docs/content.md` (`.lightbake` files in the cache), `docs/design.md`

**Interfaces:**

Produces:
```csharp
/// <summary>Chunk bakes made on this machine, by their file name (which holds the input hash), least recently read dropped first.</summary>
internal sealed class BakeCache
{
    public const long DefaultCapacity = 512L * 1024 * 1024;
    public BakeCache(string folder, TimeProvider time, long capacity = DefaultCapacity);
    /// <summary>LOCALAPPDATA\{id}\Bakes on Windows, ~/Library/Caches/{id}/Bakes on macOS, $XDG_CACHE_HOME/{id}/bakes or ~/.cache/{id}/bakes on Linux.</summary>
    public static string FolderFor(string gameId, UserDataPlatform platform, Func<string, string?> environment);
    public byte[]? Read(string file);            // marks it used now
    public void Write(string file, byte[] bytes); // to a temp file, then moved in; then trims to capacity
    public long Size { get; }
}

// In LightBakeStore:
/// <summary>Forgets one file's header and raises Reloads, so the next frame reads it again.</summary>
public void Forget(string file);
```

Use time is the file's last-write time, set from `TimeProvider` on read and write. Trimming deletes the oldest until the total is at most the capacity. A file larger than the capacity isn't kept. The class locks around its own work, since writes come from the thread pool and reads from the render thread.

- [ ] **Step 1: Write the failing tests** (a temp folder, a `FakeTimeProvider`).
  - `Least_recently_read_bake_goes_first`: write A, B, C at t = 1, 2, 3 at a capacity of two files' size, read A at t = 4, write D: B is gone, A, C and D stay.
  - `Cache_never_passes_its_capacity`: over 12 seeds of random sizes and orders, `Size` stays at or under the capacity after every write.
  - `Bake_larger_than_the_capacity_is_not_kept`.
  - `Folder_for_each_platform`: hand-written expected paths for each platform, with and without `XDG_CACHE_HOME`, and an exception naming `LOCALAPPDATA` when it is missing.
  - `Damaged_cached_bake_is_baked_again`: a cached file of garbage reads as unreadable once (logged), and after `Write` with a good file and `Forget(file)`, the store calls it fresh.
  - In `LightBakeStoreTests`: `Forgetting_one_file_keeps_the_others`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeCacheTests|FullyQualifiedName~LightBakeStoreTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** Trim by name instead of time, see the LRU test fail; revert. Write in place without the temp file is not testable by a test here; say so in the commit body.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: keep chunk bakes made on this machine in a capped cache"`

### Task 5: Baking in development and on arrival

Super turns on `Renderer.AutoBake` in `GameWindow.Run` and decides where each bake goes.

**Files:**
- Create: `src/Super/Rendering/AutoBakePolicy.cs`, `src/Super/Rendering/BakeOverlay.cs`
- Modify: `src/Super/Graphics.cs` (`BakeOnArrival`, `BakeBudget`), `src/Super/Rendering/GameWindow.cs` (`Run` and `ApplyGraphics`), `src/Super/Rendering/SceneRenderer.cs` (passes `AutoBake`, `BakeProgress`; queues `Forget` from finished writes onto the render thread in `Prepare`; sets `BakeFadeSeconds = 0` outside `Run`), `src/Super/GameHost.cs` (`Interface` adds the overlay over the game's UI)
- Modify: `examples/Hamlet/README.md` (`R` bakes new cottage layouts on arrival with a countdown, and earlier seeds load from the cache)
- Test: create `tests/Super.Tests/AutoBakePolicyTests.cs`, `tests/Super.Tests/BakeOverlayTests.cs`, extend `tests/Super.Tests/GraphicsTests.cs`
- Docs: `docs/rendering.md` "Baking without a build step" (new section) and the switch table, `docs/command-line.md` (which runs never auto-bake), `docs/design.md`

**Interfaces:**

Consumes: `AutoBake`, `BakeDone`, `BakeProgress` (task 2), `BakeCache`, `LightBakeStore.Forget(file)` (task 4), `DevSettings`.

Produces:
```csharp
// In Graphics:
/// <summary>Bakes missing lighting as it appears, where the GPU can trace. From source: spaces and chunks, Preview then Final. Published: chunks only, Preview.</summary>
public bool BakeOnArrival { get; set; } = true;
/// <summary>GPU milliseconds a frame for background bakes. Null: 4 from source, 2 in a published game.</summary>
public float? BakeBudget { get; set; }   // set: finite, 0.5..33, else ArgumentOutOfRangeException naming the value

internal sealed record AutoBakePlan(float Budget, bool Spaces, BakeQuality[] Qualities, string? SpaceFolder);

internal static class AutoBakePolicy
{
    /// <summary>Null when nothing should bake: the switch is off or the GPU can't trace.</summary>
    public static AutoBakePlan? For(DevSettings? dev, bool canTrace, Graphics graphics);
}

internal static class BakeOverlay
{
    /// <summary>"Baking cottage, 42%" and, with others waiting, " (3 more)". Null when progress is null.</summary>
    public static string? Text(BakeProgress? progress);
    public static UiView? Over(UiView? game, BakeProgress? progress);   // a small line at the bottom left
}
```

Where bakes go (Calls made 1):
- A space's bake, from source: written to `dev.Roots[0]/lighting/<name>.lightbake` through a temp file and a move, on the thread pool. The watch cook and content reload bring it in.
- A chunk's bake, from source or published: `BakeCache.Write(file, bytes)` on the thread pool, then `Bakes.Forget(file)` on the render thread, so the next frame reads it from the cache.
- Each finished bake logs one line: `Baked house-garden (Preview) in 12.3 s: <path>`.
- From source on a GPU that can't trace, the first want logs once: `Not baking house-garden: <NoTraceReason>`.

- [ ] **Step 1: Write the failing tests.**
  - `AutoBakePolicyTests`, one test per row, hand-written:
    - `From_source_bakes_spaces_and_chunks_preview_then_final_at_4_ms`: dev settings, can trace, defaults. Expect spaces on, `[Preview, Final]`, 4, and the first root's folder.
    - `Published_bakes_chunks_only_at_preview_and_2_ms`.
    - `No_tracing_bakes_nothing`.
    - `Switch_off_bakes_nothing`.
    - `Budget_set_by_the_game_wins`: a budget of 3 gives 3 in both cases.
  - `GraphicsTests`: `Bake_budget_out_of_range_throws` names the value and the range, at 0.4, 34 and NaN.
  - `BakeOverlayTests`: `Overlay_reads_name_and_percent` gives `Baking house-garden, 42%` for 0.424 under `sv-SE` culture; `Overlay_counts_the_others_waiting` gives `Baking cottage, 7% (3 more)`; `No_overlay_without_progress`.
  - `Space_bake_is_written_to_the_first_source_root` and `Chunk_bake_goes_to_the_cache`: call the `Done` handler `SceneRenderer` builds with a temp folder and a temp cache. Poll up to 5 s for the file, then check its bytes.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~AutoBakePolicyTests|FullyQualifiedName~BakeOverlayTests|FullyQualifiedName~GraphicsTests"`
- [ ] **Step 3: Implement.** `ApplyGraphics` sets `AutoBake` again when the switch or budget changes, and reuses the same `AutoBake` object otherwise, since a new one would restart the queue.
- [ ] **Step 4: Run and prove.** Let a published plan bake spaces, see its test fail; revert. Format the percent with the current culture, see the `sv-SE` test fail; revert.
- [ ] **Step 5: Check it end to end.** Move `examples/Gallery/Content/lighting/gallery.lightbake` aside. Run `dotnet run -c Release --project examples/Gallery`. The console logs the Preview bake and then the Final, and after each the content reloads. The file is back in `examples/Gallery/Content/lighting`. Run `git diff --stat` on it, then restore the committed file with `git checkout`. Run `--bake-check`; it exits 0. Then run `dotnet run -c Release --project examples/Hamlet -- --seed 2`, and check that new cottage layouts log `Baked cottage (Preview)` and that the cache folder from `FolderFor` holds them. Run with `--seed 2` again: nothing bakes.
- [ ] **Step 6: Docs and commit.** In rendering.md, give where each bake goes, the budget, the overlay, the cache and its cap, and that a stale space in a shipped game is what `--bake-check` is for. `git commit -m "feat: bake missing lighting while the game runs"`

### Task 6: The Crypt and the Courtyard as baked spaces

The spec's examples ask for "the Crypt with mixed candles and torches and the Courtyard with a mixed sun and its lamp through the glass".

**Files:**
- Modify: `examples/Crypt/*.cs` (a `view.BakedLighting("crypt", …)` box around the crypt, its architecture and furniture `isStatic: true`, the torches and candles left at the `Mixed` default, the swinging lantern left as it is, so it is found animated), `examples/Crypt/README.md`, `examples/Crypt/Crypt.csproj` (a `lighting/` content folder, as the Gallery has)
- Modify: `examples/Courtyard/*.cs` (a box over the yard, the model `isStatic: true`, the sun and the room lamp left `Mixed`), `examples/Courtyard/README.md`, `examples/Courtyard/Courtyard.csproj`
- Create: `examples/Crypt/Content/lighting/crypt.lightbake`, `examples/Courtyard/Content/lighting/courtyard.lightbake`
- Test: extend `tests/Courtyard.Tests` with `Courtyard_names_one_baked_space_and_static_yard`, which draws a frame through the game's `Draw` with a test `View`, and add the same for the Crypt in a new `tests/Crypt.Tests` modelled on it.

- [ ] **Step 1: Write the failing tests.** Each checks that the frame names one baked space, with the name and box given, and that the static draws are all inside it.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Make the changes, then bake.** Run `dotnet run -c Release --project examples/Crypt -- --bake`, and the same for the Courtyard. Check that each report's invalid texel share is under 2% and which Mixed lights got no channel (the Crypt's candles mostly will; they keep real-time and no-tile shadows as today). Check that `--bake-check` exits 0 for each.
- [ ] **Step 4: Size.** Commit each file when it is 16 MB or less. Otherwise drop that space's `texelsPerMetre` to 10 and bake again. If it still doesn't fit, don't commit it: development builds it on first run (task 5). Say so in the README and the PR (Calls made 4).
- [ ] **Step 5: Docs and commit.** In each README, give which lights have which mode and why, and how to bake again. `git commit -m "feat: bake the Crypt and the Courtyard"`

### Task 7: Before and after shots of the Crypt and the Courtyard

**Files:**
- Create: `docs/media/crypt-baked-before-after.webp`, `docs/media/courtyard-baked-before-after.webp`
- Modify: `docs/rendering.md` "Baked lighting", `examples/Crypt/README.md`, `examples/Courtyard/README.md`

- [ ] **Step 1: Before.** In a worktree of `feat/lighting` at the commit this branch started from, run each example with `--screenshot before.png --validate --seed 1 --ticks <t> --size 1280x720`. Use the README's usual ticks. Note the validation-message counts.
- [ ] **Step 2: After.** Run the same commands on this branch.
- [ ] **Step 3: Combine.** For each, `hstack` with `drawtext` labels "Live" and "Baked", `fontfile='C\:/Windows/Fonts/segoeui.ttf'`, scaled to 1200 wide, `-c:v libwebp`. Crop to the part that changed if the change sits in one part of the frame.
- [ ] **Step 4: Look at each image** and check each label is true.
- [ ] **Step 5: Docs and commit.** Use them in rendering.md and the READMEs, with alt text that says what changed. Give the validation-message counts in the PR. `git commit -m "docs: show the Crypt and the Courtyard before and after baking"`

---

## Order and parallel work

- Task 1, then tasks 2 and 4 side by side, then task 3, then task 5.
- Task 6 can start any time after plan 6 has merged, beside tasks 1 to 5; task 7 comes last.

Run the one review after task 7, against the Review Focus.

## Not in this plan

- Taking plan 6's `ChunkUploadBudget` into `BakeBudget`. Uploads stay at their own 8 MB a frame.
- A shot of a bake arriving. Auto-bake is off in `--record`, so a capture can't show it; the fade and overlay are checked by GPU tests and by hand in task 5.
- Baking spaces on a player's machine, and baking without ray tracing: spec non-goals.
- A way to clear the cache from the game. Deleting the folder is safe.

## Calls made

1. From source, a space's bake goes to the first source root as the spec says, but a chunk's goes to the cache, as on a player's machine. Otherwise playing a generated game from source would write every variant of every seed into the repo. `--bake --seed N` stays the way a chunk's bake enters the source (open question 1).
2. `Graphics.BakeOnArrival` covers both development and published games, so there is one switch (open question 2).
3. `Graphics.BakeBudget` is a nullable float: null means the spec's 4 ms from source and 2 ms published.
4. The Crypt's and the Courtyard's bakes are committed when each is 16 MB or less, as the Hamlet's are in plan 6 (open question 3).
5. A bake is one `BakeJob` for the background and for `--bake`, so there is one baker. Slices are sized by GPU timestamps, and readbacks wait out the frames in flight.
6. All previews run before any final, so every area is lit by a bake soon.
7. A running bake carries on when its area leaves the frame, and is restarted only when the area comes back with other contents.
8. A bake fades only when it arrives while its area is drawn live. One fresh on its first frame shows at once, so loads, screenshots and reloads never fade. While it fades, its Baked lights stay, dimmed by the fade, so nothing goes dark.
9. Super sets `BakeFadeSeconds` to 0 outside `Run`, so captures are the same however fast the pool loads.
