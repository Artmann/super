# Baked Chunks and the Hamlet Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bake each chunk of a world once in its own space, as plain light plus sky and sun transfer, stream those bakes in and out as chunks come and go, relight every placed copy by this frame's sky and sun, keep light from jumping at a chunk's edge or a baked space's edge, and make the Hamlet a chunked baked world.

**Architecture:** Plans 4 and 5 already decide what a chunk's bake is made of and whether it is fresh: `BakeResolver` hashes each chunk on its own (`BakeInputHash.Chunk`), so only a changed chunk's bake goes stale. What is space-only is everything after that. The baker, the file, the runtime's single `_loadedBake`, the shadow mask channels and the probe lookup all assume one space. This plan adds the maths first: L1 spherical harmonics with rotation, which is CPU-only and testable (task 1). Then the containers: the file's transfer pages (task 2), a chunk's one-page layout (task 3) and its mask channels (task 4). Then the baker for chunks (task 5) and `--bake` for chunks (task 6). The runtime follows. A pool loads chunk bakes off the render thread and lets them go (task 7), and per-instance chunk data relights each copy (task 8). Frame-wide mask slots let a light near a chunk's edge use its mask (task 9). Edge bands blend baked light into live light at a chunk's sides and at a baked space's probe box (task 10). The Hamlet becomes chunks and ships its bakes (task 11). The plan ends with the carry-over items (task 12) and the shots (task 13).

**Tech Stack:** C# / .NET, Silk.NET Vulkan, GLSL compiled to SPIR-V (ray queries via `trace.glsl`), System.IO.Compression (Brotli), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`. This plan covers Order step 7 ("Chunks: transfer in the baker, the page pool, many chunks a frame, relighting by sky and sun") and the Hamlet part of step 10. It argues from "Baked chunks", "What is traced", "Shadow mask channels", "The bake file", "At runtime" and "Hamlet: a generated world". Roadmap row: plan 6.

---

## Global Constraints

- Put braces on every control statement. Use conventional commits, with no Co-Authored-By lines and no Claude author.
- Change the docs in the same task as the code. Before each commit, search `docs/` for the names you touched.
- Every new effect gets an on/off switch whose default looks best. Chunk bakes come under the existing `Graphics.BakedLighting` (plan 5). The edge bands are not an effect of their own: they keep a bake from looking wrong. They can be turned off per area (`edgeBlend: 0`), not globally. See Calls made 9.
- GPU tests go in `tests/Rendition.Gpu.Tests`. They carry `[Trait("Needs", "GPU")]` and `[Trait("Speed", "Slow")]`, sit in `[Collection(GpuCollection.Name)]`, and check that `ValidationErrors` didn't grow. CPU tests for Rendition go in `tests/Super.Tests`, next to `ShadowMaskChannelsTests` and `LightBakeFileTests`.
- Follow the CLAUDE.md test rules:
  - prove each test can fail by breaking the code and reverting;
  - use hand-worked expected values, not the production formula;
  - no fixed sleeps: poll until it happens, or use a fake clock;
  - check exception messages;
  - use 10–20 seeds for a property;
  - rebuild rather than `--no-build`.
- With no chunk bake, or only stale ones, a frame must look byte-for-byte as it does today. Plan 4's `NoBakeGpuTests` keep passing, with a chunk added to them in task 7.
- A space's bake must not change. Its file still reads and stays fresh, so `BakeInputHash.BakerVersion` stays 2 (see Calls made 3). The Gallery's committed bake must pass `--bake-check` at the end of every task.
- No hitches. Nothing that reads a file, decompresses or uploads more than the per-frame budget may run inside `Render`.
- Use only `--screenshot` and `--record` to see the game; never capture the desktop. Take no progress shots. Take the before and after shots in task 13 only.

## Review Focus

This plan has one review, at the end. The review should press on these five points. Each has a named test in the task that owns it.

1. Light counted twice or lost in the edge band. In the band a pixel takes `mix(live, baked, weight)`. Ambient occlusion and the shadow mask must follow the same weight, not flip at `weight > 0.5`. Task 10, `Floor_crossing_a_chunk_side_has_no_step` and `Edge_band_keeps_a_light_counted_once`.
2. A hitch when a chunk appears. The file read and Brotli run off the render thread, and the upload keeps to its budget. Task 7, `Chunk_bake_loads_without_blocking_a_frame`.
3. A rotated copy lit as if it hadn't turned. Rotating the sky's L1 the wrong way round, or not at all, fails `Turned_chunk_matches_a_space_baked_turned` in task 8. The CPU rotation fails `Rotating_l1_keeps_its_value_in_the_turned_direction` in task 1.
4. A Mixed light's mask read on the wrong chunk, so light passes through another chunk's wall. Task 9, `Light_near_a_chunk_edge_keeps_its_real_time_shadow_on_the_neighbour`.
5. The pool leaking pages, or freeing one the GPU still reads. Task 7, `Chunk_bake_is_released_when_no_copy_is_named` with validation clean, and `Two_copies_share_one_bake`.

Beyond those, check four more things. The space's bake and the Gallery stay fresh and unchanged. Validation stays clean with `--no-trace`, where the chunk is lit live because nothing can bake it. And `Resolve` and the pool's per-frame work allocate nothing (`Resolving_a_frame_with_chunks_again_allocates_nothing`, task 7).

---

### Task 1: L1 spherical harmonics

A chunk's transfer and the frame's sky meet as L1 spherical harmonics (SH): four numbers per colour. That needs three things in one place, with tests that don't depend on the GPU: the basis constants, projection of a direction or a uniform sky, and rotation by a chunk's rigid transform. The shaders mirror this file in task 8.

**Files:**
- Create: `src/Rendition/Baking/ShL1.cs`
- Test: create `tests/Super.Tests/ShL1Tests.cs`
- Docs: `docs/design.md`, a new "Chunk transfer" section, started here and finished in task 8.

**Interfaces:**

Produces:
```csharp
/// <summary>L1 spherical harmonics for one colour channel: coefficient 0 is the constant band, 1..3 are (y, z, x).</summary>
public readonly record struct ShL1(float C0, float C1, float C2, float C3)
{
    public const float Y0 = 0.282094792f;   // 1 / (2 sqrt(pi))
    public const float Y1 = 0.488602512f;   // sqrt(3) / (2 sqrt(pi))

    /// <summary>A light of the given radiance from one direction (a delta), projected.</summary>
    public static ShL1 Direction(Vector3 direction, float radiance);

    /// <summary>A sky of the same radiance in every direction, projected: only C0, 4 pi Y0 L.</summary>
    public static ShL1 Uniform(float radiance);

    /// <summary>The value in a direction.</summary>
    public float Evaluate(Vector3 direction);

    /// <summary>The same function turned by a rotation: (C1, C2, C3) turn as the vector (y, z, x) does.</summary>
    public ShL1 Rotate(in Matrix4x4 rotation);

    /// <summary>The sum over coefficients of a * b, which is the integral of the two functions' product.</summary>
    public static float Dot(in ShL1 a, in ShL1 b);
}
```

- [ ] **Step 1: Write the failing tests.** Use hand-worked values:
  - `Uniform_sky_projects_to_four_pi_y0`: `Uniform(1).C0` is 3.5449077 within 1e-5, and the rest are 0.
  - `Plane_transfer_dotted_with_a_uniform_sky_gives_pi`: a cosine lobe about +y has transfer (π·Y0, π·Y1·2/3 on y, 0, 0) = (0.886227, 1.023327, 0, 0). Its `Dot` with `Uniform(1)` is π within 1e-5.
  - `Rotating_l1_keeps_its_value_in_the_turned_direction`: over 16 seeds, a random rotation R, random coefficients and a random direction d give `Rotate(R).Evaluate(R·d) == Evaluate(d)` within 1e-5.
  - `Quarter_turn_about_y_moves_x_to_minus_z`: `Direction(+x, 1).Rotate(Y by 90°)` equals `Direction(-z, 1)`. Work the expected coefficients by hand in the test: C3 = 0, C2 = 0, C1 = 0 for y, then the z slot −Y1.
  - `Rotate_throws_for_a_scale`: a rotation scaled by 2 throws `ArgumentException`, and the message says the transform must only turn.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~ShL1Tests"`
- [ ] **Step 3: Implement.** `Rotate` takes the upper 3×3 of a rigid transform and checks it with `BakeFiles.IsRigid`. It turns the vector (C3, C1, C2) as (x, y, z) and writes it back.
- [ ] **Step 4: Run and prove.** Swap two slots in `Rotate`, see the quarter-turn test and the property fail, then revert. Use the transpose instead of the matrix, see the property fail, then revert.
- [ ] **Step 5: Docs and commit.** In design.md, give the basis, the slot order (y, z, x), and why the transfer's dot with the sky's projection is irradiance (the cosine is in the transfer). `git commit -m "feat: add L1 spherical harmonics with rotation"`

### Task 2: Transfer pages in the `.lightbake` file

The spec gives a chunk three transfer pages, each RGBA16F holding the four coefficients of one colour, plus the sun's transfer as one shared set scaled by an RGB tint per texel. A chunk keys its mask channels differently from a space. A space can key them by the game's light key, but every copy of a chunk gives its lights different keys. So a chunk keys each channel by the light's chunk-space hash, `BakeInputHash.Light(light, chunkTransform)`, which is the same for every copy.

**Files:**
- Modify: `src/Rendition/Baking/LightBakeFile.cs`
- Test: `tests/Super.Tests/LightBakeFileTests.cs`, `tests/Super.Cook.Tests/LightBakeCookerTests.cs`
- Docs: `docs/content.md` (`.lightbake`), `docs/design.md` (the file)

**Interfaces:**

Produces:
```csharp
public sealed record LightBakeTransfer(
    LightBakePage SkyRed, LightBakePage SkyGreen, LightBakePage SkyBlue,   // Rgba16f, the four coefficients each
    LightBakePage Sun,                                                       // Rgba16f, the shared four
    LightBakePage SunTint);                                                  // Rgba8, RGB tint scaled to the brightest

public sealed record LightBakeFile(...existing...)
{
    public LightBakeTransfer? Transfer { get; init; }               // only for a chunk
    public IReadOnlyList<(ulong LightHash, int Channel)> ChunkChannels { get; init; } = [];
}
```

Then make these changes:
- Set `LightBakeFile.Version` to 2, and have `Read` take versions 1 and 2. Version 1 has no transfer, so a space's committed bake still reads.
- Add bits to the present-flags byte: transfer present, and plain light present. A chunk with no Baked or Mixed light and no glowing surface has no plain light, so it stores no irradiance or direction page. Most Hamlet cottages are like that.
- Make `SunTint` RGBA8 with the tint divided by its largest channel. The shared `Sun` set holds that largest channel's share.

- [ ] **Step 1: Write the failing tests.**
  - `Chunk_bake_round_trips_its_transfer`: a hand-made file with transfer pages and two chunk channels round-trips byte-equal.
  - `Version_one_space_bake_still_reads`: bytes written by the version-1 writer, checked in as a test resource made from today's code, read with `Transfer == null`.
  - `Chunk_without_plain_light_stores_no_irradiance`: such a file is smaller, and `Irradiance` reads as null.
  - `Version_three_throws_with_the_version`: the message names 3 and the versions it reads.
  - Cooker: `Cooking_keeps_transfer_pages_uncompressed_half_floats`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~LightBakeFileTests"`, then the same in `tests/Super.Cook.Tests` with `~LightBakeCookerTests`.
- [ ] **Step 3: Implement.** Make `Irradiance` and `Direction` nullable, and have every reader check for null (`Renderer.LoadSpaceBake` requires them for a space). The cooker leaves transfer pages as RGBA16F. BC6H's signed mode could carry them later; it is not in this plan.
- [ ] **Step 4: Run and prove.** Drop the transfer flag on write and see the round trip fail; revert. Refuse version 1 and see the old-file test fail; revert.
- [ ] **Step 5: Docs and commit.** In content.md, list a chunk's pages. In design.md, give the flags and the chunk channel keys. `git commit -m "feat: store chunk transfer in the lightbake file"`

### Task 3: A chunk's lightmap is one page of its own size

The spec says a chunk "packs into one page of its own size, at most 2048²". `LightmapLayout` packs a space into up to eight 2048² pages.

**Files:**
- Modify: `src/Rendition/Lightmaps/LightmapLayout.cs`
- Test: `tests/Super.Tests/Lightmaps/LightmapLayoutTests.cs`
- Docs: `docs/design.md` (lightmap layout)

**Interfaces:**

Produces:
```csharp
/// <summary>Packs into one square page, the smallest power of two from 64 to 2048 that holds every chart at the density.</summary>
public static LightmapLayout Single(ReadOnlySpan<LightmapChart> charts, int texelsPerMetre);
public int PageSide { get; }   // 2048 for Pack, the chosen side for Single
```

Charts that don't fit even at 2048 are listed in `Dropped`, as `Pack` does. The baker then reports them, and they are lit live.

- [ ] **Step 1: Write the failing tests.**
  - `Single_page_picks_the_smallest_side_that_fits`: work three hand-sized charts by hand to 256. One texel more makes it 512.
  - `Single_page_never_passes_2048`: the overflow is in `Dropped`, in the order given.
  - `Single_page_keeps_the_gutter`: no two rectangles come closer than `LightmapUnwrap.Gutter`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Try `SkylinePacker.TryPack` at 64, 128, … 2048. Scale and offset are relative to `PageSide`.
- [ ] **Step 4: Run and prove.** Start the search at 512, see the 256 test fail, revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: pack a chunk's lightmap into one page of its own size"`

### Task 4: Mask channels for a chunk

The spec says chunks have no sun channel. A Mixed light belongs to a chunk when its position is inside the chunk's box, so channels are coloured in chunk space and the result is the same for every copy.

**Files:**
- Modify: `src/Rendition/Baking/ShadowMaskChannels.cs`
- Test: `tests/Super.Tests/ShadowMaskChannelsTests.cs`
- Docs: `docs/design.md` (shadow mask channels)

**Interfaces:**

Produces:
```csharp
/// <summary>
/// Gives the chunk's Mixed local lights channels 0..3 by greedy colouring of their range overlap in chunk space, in order
/// of their chunk-space hash. There is no sun channel. Lights left without one go in <paramref name="unassigned"/>.
/// </summary>
public static void AssignChunk(BakedChunk chunk, ReadOnlySpan<LocalLight> lights, Span<int> channels, List<int> unassigned);
```

- [ ] **Step 1: Write the failing tests.**
  - `Chunk_lights_start_at_channel_zero`: one Mixed light gets channel 0, where a space's would get 1 behind a Mixed sun.
  - `Chunk_channels_are_the_same_wherever_it_stands`: the same lights under two transforms, given in a different order, get the same channel per light. Use 12 seeds of transforms.
  - `Fifth_overlapping_chunk_light_is_unassigned`.
  - `Light_outside_the_chunk_box_gets_no_channel`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Move each light in with `BakeAreas.IntoChunk`, and sort by `BakeInputHash.Light(light, chunk.Transform)`, not by key, since the key differs per copy.
- [ ] **Step 4: Run and prove.** Sort by key, see the placement test fail, revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: give a chunk's mixed lights their own mask channels"`

### Task 5: The baker bakes a chunk

This task does three things:
- `Renderer.BakeChunk` bakes one chunk of the frame in its own space: only the draws it owns, moved by `BakeIdentity.Relative`'s rule, and only its lights;
- `bake_trace.comp` gains the sky and sun transfer;
- every other part of the baker (texel setup, filtering, dilation, the report) is reused as it is.

The spec's rules, made exact, are these:
- A path from a texel that misses all geometry escaped. It adds `throughput × Y(ω)` to the sky transfer's RGB coefficients, in the chunk-space direction ω it left, at any bounce, the first included. The trace samples over the cosine, so the stored value is `π × mean`. This is the normalisation task 1's plane test checks.
- At each path vertex after the first bounce, one extra ray goes out in a uniformly sampled direction ω_s, as next-event estimation for a sun from ω_s. If it escapes, it adds `throughput × albedo/π × max(0, n·ω_s) × 4π × Y(ω_s)` to the sun transfer. The texel's own direct view of the sun stays out, since the sun's direct light is real-time.
- Plain light is a space's plain light without the sky and sun: Baked lights in full, Mixed lights' bounce, and glowing surfaces.
- There are no probes.

**Files:**
- Modify: `src/Rendition/Baking/Baker.cs`, `src/Rendition/Shaders/bake_trace.comp`, `src/Rendition/Shaders/bake_paths.glsl`, `src/Rendition/Shaders/bake_resolve.comp`, `src/Rendition/Renderer.cs` (`Bake`, new `BakeChunk`)
- Test: create `tests/Rendition.Gpu.Tests/BakeChunkGpuTests.cs`
- Docs: `docs/rendering.md` "Making a bake", `docs/design.md` (the baker)

**Interfaces:**

Produces:
```csharp
/// <summary>
/// Path traces chunk <paramref name="chunk"/> of the frame in its own space and returns its .lightbake bytes. Throws when
/// the frame has no such chunk, when the chunk owns no static draws, or when the settings' density isn't the chunk's.
/// </summary>
public byte[] BakeChunk(in FrameView view, ReadOnlySpan<DrawItem> draws, int chunk, BakeSettings settings);
```

Inside, split `Bake` into a shared `BakeArea(AreaJob job)`. `AreaJob` holds:
- the draws in area space;
- the lights in area space;
- `IsChunk`;
- the layout, from `Pack` for a space and `Single` for a chunk;
- the channel assignment.

The baker builds its own top level from the job's draws, under the area-space transforms.

- [ ] **Step 1: Write the failing GPU tests.**
  - `Plane_chunk_under_a_white_sky_transfers_pi_l`: a 2 m plane as a chunk, sky radiance 1 everywhere. Dot the baked sky transfer with `ShL1.Uniform(1)` per channel; every covered texel is π within 3σ of a Preview bake's noise. Work out σ from 64 samples of a cosine lobe: σ ≈ π·0.5/√64.
  - `Turned_plane_chunk_transfers_the_same`: the spec's test. The chunk transform turned 90° about x gives byte-equal transfer pages, since the bake is made in chunk space.
  - `Chunk_bake_leaves_out_draws_it_does_not_own`: a wall outside the chunk's box casts no shade in its transfer.
  - `Closed_box_chunk_has_no_sky_transfer_inside`.
  - `Chunk_sun_transfer_holds_only_bounce`: a lone plane has zero sun transfer, since there is nothing to bounce from. A plane beside a white wall has some on the side facing the wall.
  - `Baking_a_missing_chunk_throws`: the message names the index and the count.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeChunkGpuTests"`
- [ ] **Step 3: Implement.** In this order:
  1. Write the `AreaJob` split. Confirm the Gallery bake is unchanged by baking it at Preview before and after the split and comparing headers (inputs and draws) and page sizes.
  2. Add the shader accumulators: three RGBA32F sky sums, an RGBA32F sun sum and an RGB32F sun-tint sum, in the texel buffer next to the irradiance sums.
  3. Add the resolve, which writes the RGBA16F pages and divides the sun tint by its largest channel.
  4. Make `Bake` with no space throw as today. Leave `BakeChunk` as the only way in for a chunk.
- [ ] **Step 4: Run and prove.** Drop the π, see the πL test fail, revert. Bake the chunk in world space instead of chunk space, see the turned test fail, revert. Let the first bounce add to the sun transfer, see the lone-plane test fail, revert.
- [ ] **Step 5: Docs and commit.** In rendering.md, remove "Chunks aren't baked yet." and say what a chunk bake holds. Give the rules above in design.md. `git commit -m "feat: bake chunks with sky and sun transfer"`

### Task 6: `--bake` and `--bake-check` for chunks

The spec's table says `--bake` "bakes every space and chunk it draws". The same contents give one file, `lighting/<name>/<hash>.lightbake`, so each file is baked once however many copies stand in the frame.

**Files:**
- Modify: `src/Super/Rendering/GameWindow.cs` (`Bake`, `BakeCheck`), `src/Super/Rendering/SceneRenderer.cs` (`Bake` gets an area), `src/Rendition/Baking/BakeFreshness.cs` (`BakeFrame` gives each chunk's file name)
- Test: `tests/Super.Tests` (the existing `--bake` and `--bake-check` tests, wherever `GameWindow.DescribeBake` is tested; find them with `grep -rn DescribeBake tests`)
- Docs: `docs/command-line.md` "Baking lighting"

**Interfaces:**

Produces:
```csharp
public sealed class BakeFrame
{
    public string ChunkFile(int chunk);    // BakeFiles.Chunk(name, Chunks[chunk].Combined), cached by the resolver
}
```

Here is how the commands behave:
- `--bake` bakes the space, if there is one, then each distinct chunk file in order of first appearance. It prints one `DescribeBake` block per file, and exits 0 only when every file was written.
- A frame with neither a space nor a chunk exits non-zero with "--bake found no baked space or chunk in the frame. Name one with view.BakedLighting or view.BakedChunk."
- `--bake-check` prints one line per distinct file, as `missing`, `stale, its … changed` or `fresh`, and exits with the count of missing and stale.

- [ ] **Step 1: Write the failing tests.**
  - `Bake_writes_one_file_per_distinct_chunk`: three copies of one chunk and one of another give two files.
  - `Bake_check_lists_every_chunk_file`.
  - `Bake_with_no_area_explains`: check the message.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** Remove the de-duplication, see the count test fail, revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: bake and check every chunk the frame draws"`

### Task 7: The chunk bake pool

At runtime a chunk's bake has to reach the GPU without a hitch. Copies must share it, and it must go when nothing names it. The pool is keyed by file name and keeps no handles in the frame path that allocate.

The pool works like this:
1. The first frame that finds a chunk's bake fresh queues a load. On the thread pool, `IBakeStore.Load(file)` reads the file and Brotli unpacks it.
2. The render thread uploads the pages through staging. It moves at most `ChunkUploadBudget` bytes a frame, 8 MB at first, and finishes a page across frames if it has to.
3. Until every page is resident, the chunk counts as not fresh in `BakeFrame`. That way its Baked lights stay drawn live and no frame is missing light.
4. A bake that no chunk names in a frame is released at the end of that frame, through the frame-in-flight release list, so the GPU is done with it first.
5. A content reload drops every entry, as `UnloadBake` does for the space.

**Files:**
- Create: `src/Rendition/Baking/ChunkBakePool.cs`
- Modify: `src/Rendition/Renderer.cs`:
  - `ResolveBakes` asks the pool;
  - `_activeLightmaps` takes the resident chunks' rects too, since their bake ids never clash with the space's;
  - the `LastBakeFrame` freshness changes as in step 3 above.
- Modify: `src/Rendition/Baking/BakeFreshness.cs` (`ChunkFresh` means fresh and resident; a new `ChunkPending`)
- Test: create `tests/Rendition.Gpu.Tests/ChunkPoolGpuTests.cs`, and extend `tests/Rendition.Gpu.Tests/NoBakeGpuTests.cs` and `tests/Super.Tests/BakeResolverTests.cs`
- Docs: `docs/rendering.md` "With a fresh bake", `docs/design.md`

**Interfaces:**

Produces:
```csharp
internal sealed class ChunkBakePool : IDisposable
{
    public ChunkBakePool(VulkanContext context, Func<string, LightBakeFile?> load, long uploadBudget);
    /// <summary>Marks the file in use this frame; returns its entry once every page is resident, else null.</summary>
    public LoadedChunkBake? Use(string file, BakeInputs inputs);
    /// <summary>Uploads within the budget, then releases what no chunk used this frame.</summary>
    public void EndFrame(CommandBuffer cmd, FrameReleases releases);
    public int Resident { get; }
    public int Pending { get; }
}
```

`Renderer.LastBakeFrame` gains `ChunkPending(int chunk)`.

- [ ] **Step 1: Write the failing tests.** Load through a store whose `Load` waits on a `ManualResetEventSlim` the test controls.
  - `Chunk_bake_loads_without_blocking_a_frame`: while the load is held, frames render and the chunk reads `ChunkPending`. Release the load, then poll up to 120 frames until `ChunkFresh`. Fail with the frame count if it never comes.
  - `Two_copies_share_one_bake`: `Resident == 1`, and both copies' lightmapped pixels match each other within 1 step.
  - `Chunk_bake_is_released_when_no_copy_is_named`: one frame without the chunk gives `Resident == 0`, and validation stays clean over the next three frames.
  - `Missing_chunk_bake_draws_the_same_bytes` in `NoBakeGpuTests`.
  - In `BakeResolverTests`, `Resolving_a_frame_with_chunks_again_allocates_nothing`, measured with `GC.GetAllocatedBytesForCurrentThread`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Keep the store call off the render thread (`Task.Run`), and hand the result over through a `ConcurrentQueue`. Register textures in the bindless array as the space's are. `ChunkUploadBudget` is a constant in this plan, not a setting. Plan 7's `BakeBudget` can take it over.
- [ ] **Step 4: Run and prove.** Release straight away instead of deferring, and see validation complain in the release test; revert. Load on the render thread, and see the blocking test hang until it fails; revert.
- [ ] **Step 5: Docs and commit.** In rendering.md, remove "Only the space is drawn from its bake so far; a fresh chunk's lights are left to its bake but its lightmap isn't drawn yet". Say instead how a chunk's bake arrives and goes. `git commit -m "feat: stream chunk bakes in and out through a pool"`

### Task 8: Relighting each copy by the frame's sky and sun

Each placed copy reads one shared bake, but faces its own way. Once a frame, the sky is projected to L1 per colour. Each directional light's L1 is its radiance as a delta from its direction, so the sun and the moon each count. A per-chunk buffer holds each placed chunk's rotation into chunk space. `writeBaked` then evaluates the transfer against the sky and suns turned into the chunk's space.

**Files:**
- Create: `src/Rendition/Shaders/sky_sh.comp` and `src/Rendition/Passes/SkySh.cs`. These project the sky cube the probes read on a miss into a 64-byte buffer, from its 16² mip, weighting each texel by its solid angle.
- Create: `src/Rendition/Shaders/chunks.glsl`, holding a `ChunkData { mat3x4 intoChunk; vec4 min; vec4 max; ivec4 maskSlots; }` buffer of up to 256 entries.
- Modify:
  - `src/Rendition/Vulkan/InstanceBuffer.cs`: a per-instance `int Chunk`, −1 for none;
  - `src/Rendition/Shaders/instances.glsl` and `gbuffer.vert`: pass the chunk index and chunk-space position flat and smooth;
  - `src/Rendition/Shaders/surface.glsl` (`writeBaked`);
  - `src/Rendition/Renderer.cs`: fill `ChunkData` each frame, and set each static draw's chunk from the resolver's owners;
  - `src/Rendition/Baking/BakeResolver.cs`: expose `ReadOnlySpan<int> Owners`, one per draw.
- Test: create `tests/Rendition.Gpu.Tests/ChunkRelightGpuTests.cs`
- Docs: `docs/rendering.md` "With a fresh bake", `docs/design.md` "Chunk transfer"

In `writeBaked`, for a chunk pixel:
- E_sky = Σ over colours of `dot(T_c, R·L_c)`;
- E_sun = Σ over suns of `tint · dot(T_sun, R·S)`;
- E = plain + E_sky + E_sun, through the same `turned` factor the space uses for the normal map.

The direction `d` for the turn is the plain direction when the chunk has plain light. Otherwise it is the normalised vector part of the luminance transfer, weighted elementwise by the luminance of the turned sky's vector part.

The environment intensity scales the sky term, as it scales a space's baked sky.

- [ ] **Step 1: Write the failing GPU tests.**
  - `Turned_chunk_matches_a_space_baked_turned`: the spec's test. A floor with a wall on its −x side as a chunk, under a sky bright only towards −x. Turned 180° about y, the floor's dark side moves. It matches, within 6 steps of 255 on average over the floor, a space baked with the wall on the +x side under the same sky. Bake at Preview with the same seed.
  - `Changing_the_sky_relights_without_a_bake`: red sky, then blue sky. The lightmapped floor follows within one frame, and the bake stays fresh.
  - `Sun_bounce_follows_the_sun`: floor and white wall in a chunk. Turning the sun from facing the wall to behind it lowers the floor's bounced light. The chunk's own result matches a space with a Mixed sun in the same place, within 8 steps of 255.
  - `Sky_projection_of_a_uniform_sky_is_four_pi_y0`: read back the buffer. Hand value 3.5449 L within 1%.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Mirror `ShL1` in `chunks.glsl`. The sky projection runs before the G-buffer pass with a buffer barrier. It runs only on frames with a resident chunk, so a frame without chunks does no extra work.
- [ ] **Step 4: Run and prove.** Rotate by the inverse instead, see the turned test fail, revert. Drop the suns' sum, see the sun test fail, revert.
- [ ] **Step 5: Docs and commit.** In rendering.md, cover the same house facing north or south, and noon or dusk. `git commit -m "feat: relight every copy of a chunk by the frame's sky and sun"`

### Task 9: Shadow mask channels across chunks

Each bake has its own four mask channels, and one frame can hold many bakes. A Mixed light in chunk A whose range reaches chunk B has no channel in B's bake. A frame-wide assignment fixes that.

1. Each frame, every Mixed light that has a channel in its own bake gets one of four frame slots.
2. The space's channels keep their slots, sun at 0.
3. Chunk lights then take slots greedily, in order of key, avoiding any slot held by a light whose world-space range overlaps theirs.
4. Each chunk instance gets a table from frame slot to its bake's channel, `ChunkData.maskSlots`, with −1 for none.
5. `writeBaked` writes `outShadowMask[s]` from the bake's channel `table[s]`, or 1 where the table says −1.
6. A light's channel in the lighting pass is its frame slot.

A mask of 1 means "the bake says nothing here". `maskedVisibility` then gives `min(1, shadowMap)`, the light's real-time shadow, on any chunk but its own. A light that finds no free slot keeps its real-time shadow everywhere, and the frame reports it.

**Files:**
- Create: `src/Rendition/Baking/MaskSlots.cs`
- Modify: `src/Rendition/Renderer.cs` (`FindMaskChannels` becomes slot assignment), `src/Rendition/Shaders/surface.glsl`
- Test: create `tests/Super.Tests/MaskSlotsTests.cs`, add to `tests/Rendition.Gpu.Tests/ShadowMaskGpuTests.cs`
- Docs: `docs/rendering.md` "Light modes", `docs/design.md` (shadow mask channels)

**Interfaces:**

Produces:
```csharp
internal static class MaskSlots
{
    /// <summary>Fills slots per light, or -1, and the frame's unslotted lights. Allocation-free with the given scratch.</summary>
    public static void Assign(int spaceSun, ReadOnlySpan<(int Key, int SpaceChannel)> spaceLights,
        ReadOnlySpan<(int Key, Vector3 Position, float Range)> chunkLights, Span<int> slots, List<int> unslotted);
}
```

`BakeFrame` gains `UnslottedLights`.

- [ ] **Step 1: Write the failing tests.**
  - CPU `Two_chunks_with_overlapping_lights_get_different_slots`: both are channel 0 in their bakes.
  - CPU `Far_apart_chunk_lights_share_a_slot`.
  - CPU `Space_channels_keep_their_slots`.
  - GPU `Light_near_a_chunk_edge_keeps_its_real_time_shadow_on_the_neighbour`: a Mixed light just inside chunk A, and a wall of chunk B between it and B's floor. B's floor behind the wall stays dark, and A's floor in the light's mask shadow stays dark too.
  - GPU `Same_chunk_twice_with_overlapping_lights_reads_both_masks`: two copies close together. Each copy's floor shows its own light's mask shadow.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** Write 0 instead of 1 for a missing slot, see the neighbour floor test fail (it goes black where it should be lit); revert. Give every chunk light its bake channel as its slot, see the two-copies test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: share four mask slots across a frame's bakes"`

### Task 10: Edge bands, at a chunk's sides and a baked space's probe box

The spec leaves light from a neighbour out of a chunk's bake. That means a chunk's lightmap and the live light next to it disagree at its edge. Plan 5 left the same jump for a moving thing that crosses a baked space's box, from the bake's probes to the live ones. Both get a band that blends into live light, with a weight that comes from distance alone, so it is continuous by construction.

A chunk's edge band works like this:
- It runs `edgeBlend` metres in from the chunk's four sides, its own ±x and ±z. It doesn't run in from the top or bottom, since a chunk's floor and roof usually sit on its box (Calls made 7).
- The weight is `smoothstep(0, edgeBlend, distance to the nearest side)`. `writeBaked` writes it to `outBaked.a`, with rgb unweighted.
- The lighting pass takes `mix(softLight(…), baked.rgb, a)` for diffuse.
- Ambient occlusion moves from the lightmapped rule to the live rule by the same weight.
- The mask moves towards 1 (real-time shadows) by the same weight.
- A pixel is "lightmapped" when `a > 0`, so `softLight` runs only in the band and outside.
- Two chunks that touch both reach live light at the shared face, so they agree there.

A baked space's probe band works like this:
- The bake's probes are blended into the live lookup over one `probeSpacing` in from all six faces of the space's box.
- The weight is the product of the three axes' smoothsteps.
- `softLight` takes `mix(live, baked, w)`, where live is the existing order after the bake: `view.LightProbes`, then the cascades, then the sky.
- Where the bake's probes return "none sees" (−1), w is 0, as today.
- The space's lightmap gets no band (Calls made 8).

**Files:**
- Modify: `src/Rendition/Types.cs` (`BakedChunk` gains `float EdgeBlend = 0.5f`), `src/Super/View.cs` (`BakedChunk(..., float edgeBlend = 0.5f)`), `src/Rendition/Shaders/surface.glsl`, `src/Rendition/Shaders/lighting.frag` (`softLight`, `insideBakedProbes` becomes `bakedProbeWeight`, the lightmapped test), `src/Rendition/Baking/BakeFiles.cs` (the check)
- Test: create `tests/Rendition.Gpu.Tests/EdgeBandGpuTests.cs`, extend `tests/Super.Tests/BakedAreaViewTests.cs`
- Docs: `docs/rendering.md`, where "The two are not blended at the box's edge, so something crossing it can change its light at once" is replaced; `docs/views.md` (the `BakedChunk` row); `docs/design.md`

The band is not part of the bake hash. It changes how a bake is drawn, not what it holds.

- [ ] **Step 1: Write the failing tests.**
  - `Floor_crossing_a_chunk_side_has_no_step`: a floor of the chunk's own reaching the box's +x side, and a dynamic floor continuing past it, under a sky. Along a row of pixels across the face, no neighbouring pair differs by more than 4 steps. With `edgeBlend: 0` the same row has a step of more than 12; check that this stays true, so the test can fail.
  - `Edge_band_keeps_a_light_counted_once`: a chunk floor under a white sky with no live probes (`LightProbes` off), where live soft light is the sky. Across the band, the floor's light stays within 3 steps of its value at the centre, since both sides agree on πL.
  - `Moving_quad_crosses_the_space_box_without_a_jump`: the user's "abrupt edge". A dynamic quad moved in 10 cm steps across the space's +x face. No step changes its centre by more than 4 steps of 255. With the band off by a test hook, one step changes it by more than 12.
  - `Edge_blend_over_half_the_box_throws`: the message names the value and the box's smaller side.
  - `Negative_edge_blend_throws`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** Go back to `baked.a > 0.5` as the lightmapped test, and see the counted-once test fail at the band's middle; revert. Return the hard box test, see the moving quad fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: blend chunk and baked-space edges into live light"`

### Task 11: The Hamlet as a chunked, baked world

The Hamlet draws nothing static today. Every building's colours are drawn per building, and its lamps switch on with the day. A chunk's contents are its static draws with their tints, so as it stands every building would be its own variant, and lamps that go out would keep their baked bounce at noon. This task makes the village a chunked world with a handful of variants, bakes seed 1, and ships those bakes.

- Each building is a chunk named for its kind: `cottage`, `workshop`, `tower` or `well`. It uses its plan's `Min` and `Max`, its `Placement` and 10 texels a metre (Calls made 11). The well and tower get `edgeBlend: 0.4f`; the cottage and workshop keep the 0.5 m default.
- Building parts are drawn `isStatic: true`, except the `Lamps` and `Fire` parts and the glowing panes. Their tint changes every frame, so they stay dynamic and out of the bake.
- Roof and accent colours belong to the variant, not the building. Each kind, and each cottage layout, takes fixed colours from `Palette.Roofs` and `Palette.Accents`. So every seed's tower is one variant, and seed 1's bakes serve every later seed whose layouts match (Calls made 10).
- Lamps that come on at dusk stay `Realtime`. A chunk's plain light is fixed, so a Mixed lamp's bounce would glow at noon. The forge fires, which burn all day, are `Mixed`, so they bounce into the workshop and use its mask. The window spots stay `Realtime` with no shadow. See the open question in the report.
- The sun and moon are left as they are. They never belong to a chunk, and each relights every chunk's sun transfer (task 8).
- The ground stays dynamic and live-lit. The cart stays dynamic on the live probes, as the spec asks.
- Keys:
  - 1 shows live lighting only (`Graphics.BakedLighting` off);
  - 2 turns bakes on, the default;
  - 3 shows direct light only;
  - P shows the live probes (`DebugView.Probes`), so the cart's light can be seen moving between chunks;
  - R stays a new seed.
  - The mode shows in the corner, as in the Gallery.

**Files:**
- Modify: `examples/Hamlet/HamletGame.cs`, `Village.cs` (colours per variant), `Palette.cs`, `BuildingPlan.cs` (part flags for static), `Workshop.cs` (fire Mixed), `README.md`
- Modify: `examples/Hamlet/Hamlet.csproj`, adding a content folder for `lighting/` as `examples/Gallery` has
- Create: `examples/Hamlet/Content/lighting/<kind>/<hash>.lightbake`, made with `--bake --seed 1`
- Create: `tests/Hamlet.Tests/` modelled on `tests/Courtyard.Tests`, with `Seed_one_has_at_most_nine_chunk_variants` and `Every_seed_draws_each_kind_with_the_same_colours`, over 12 seeds
- Docs: `docs/rendering.md` "Baked lighting" (point at the Hamlet for chunks), `examples/Hamlet/README.md`

- [ ] **Step 1: Write the failing Hamlet tests.**
- [ ] **Step 2: Make the changes.** Keep `HamletGame.Draw` allocation-free: compute the chunk boxes once per village in `Build`.
- [ ] **Step 3: Bake.** Run `dotnet run -c Release --project examples/Hamlet -- --bake --seed 1`. Check the following:
  - the files are listed;
  - the report for each names no Mixed light without a channel;
  - `--bake-check --seed 1` exits 0, and `--bake-check --seed 2` lists only the cottage layouts seed 1 didn't use;
  - the total size: `du -ch examples/Hamlet/Content/lighting`.
  - The files are committed if they total 16 MB or less, a little over the Gallery's 11.8 MB. If they don't, drop to 8 texels a metre and bake again. If they still don't fit, don't commit them: say so in the README and the PR, and stop for the user (Calls made 12).
- [ ] **Step 4: Look.** Take `--screenshot --seed 1 --size 1280x720 --validate` at the ticks in the README: midday circling, dusk, the cottage interior by day and by lamp. For each, check four things:
  - every cottage of one layout is lit the right way for its own facing;
  - no building's yard has a seam where it meets the ground;
  - the cart is lit evenly as it passes between buildings;
  - there are no validation messages.

  These shots are for checking only, not for the PR.
- [ ] **Step 5: Docs and commit.** Update the README: the keys, which lights have which mode and why, how to bake again, and that other seeds show unbaked cottage layouts in live light until plan 7 bakes them on arrival. `git commit -m "feat: make the Hamlet a chunked baked world"`

### Task 12: Carry-over from earlier plans

There are three small items, each with its own commit.

**Files:**
- Modify: `tests/Rendition.Gpu.Tests/BakedProbeGpuTests.cs`, `docs/design.md`, `docs/media/gallery.webp`, `examples/Gallery/README.md`

- [ ] **Step 1: Make the flaky probe test wait for settled probes.** `In_the_space_where_no_baked_probe_sees_live_probes_are_used` captured after a fixed `NoisePeriod` of frames, so live probes that weren't settled yet sometimes failed `AssertAlike`.
  - Give `Capture` a loop when `probes` is set: render until `gpu.Renderer.ProbesSettled`, up to 600 frames. Fail with "The live probes never settled in 600 frames." if they never do. Then render the `NoisePeriod` frames as before.
  - To prove it, make the wait return straight away and run the test 10 times (`for i in $(seq 10); do dotnet test … --filter …; done`). It should fail at least once, or else show the settle frame count it skipped. Revert.
  - Then run it 10 times with the wait in place; all pass.
  - Commit: `git commit -m "test: wait for live probes to settle before comparing them"`
- [ ] **Step 2: Settle the `--check` figure.** design.md says `Rendition.Sample --check` measures 676,800 bytes over 900 frames (752 B a frame). The same paragraph says `Render` alone allocates about 1.2 KB a frame (73,920 / 60 = 1,232 B), so 752 can't be the whole sample. It was written in bbd1a5e.
  - Run `dotnet run -c Release --project src/Rendition.Sample -- --check` three times here. Then run it three times in a worktree at bbd1a5e. Note each figure.
  - If both give 1,195,200 (1,328 B a frame), the doc's figure was wrong when written. Change it, and say it is about 1.3 KB a frame, the rest of `Render` included.
  - If bbd1a5e gives 676,800, then a later commit added about 576 B a frame. Bisect it with `git bisect run`, using a script that fails when the figure is above 676,800, and name the commit in the doc. Fixing it is not in this plan. Note it under "Not in this plan" in the PR.
  - Commit: `git commit -m "docs: give the sample's measured allocations"`
- [ ] **Step 3: Reshoot the Gallery's README image.** `docs/media/gallery.webp` still shows the unbaked Gallery.
  - Find the command that made it with `git log --follow docs/media/gallery.webp` and its commit message, or the README's ticks. Take the same four shots with the bake on.
  - Build the image the same way, at about 1200 pixels wide, as `.webp`. Look at it, and check the README's alt text is still true.
  - Commit: `git commit -m "docs: show the baked Gallery in its README"`

### Task 13: Before and after shots of the Hamlet

**Files:**
- Create: `docs/media/hamlet-baked-before-after.webp`
- Modify: `docs/rendering.md` "Baked lighting", `examples/Hamlet/README.md`

Pick the tick from task 11's checks where chunks show best: dusk over the village, where yards are warm on one side and blue on the other. If the default camera doesn't show it, aim closer for this shot only and put the camera back.

- [ ] **Step 1: Before.** In a worktree of `feat/lighting` at 9c4820f, run `dotnet run -c Release --project examples/Hamlet -- --screenshot before.png --validate --seed 1 --ticks <tick> --size 1280x720`. Note the validation-message count.
- [ ] **Step 2: After.** Run the same command on this branch.
- [ ] **Step 3: Combine.** Run:
  `ffmpeg -i before.png -i after.png -filter_complex "[0]drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Live':x=20:y=20:fontsize=40:fontcolor=white:box=1:boxcolor=black@0.5[a];[1]drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Baked chunks':x=20:y=20:fontsize=40:fontcolor=white:box=1:boxcolor=black@0.5[b];[a][b]hstack,scale=1200:-1" -c:v libwebp docs/media/hamlet-baked-before-after.webp`
  Crop with `crop=` before `hstack` if the change sits in one part of the frame.
- [ ] **Step 4: Look at the image** and check each label is true. "Before" is all live light at 9c4820f, which is why it says "Live".
- [ ] **Step 5: Docs and commit.** Use the image in rendering.md "Baked lighting" and the Hamlet README, with alt text that says what changed. Give the validation-message counts in the PR. `git commit -m "docs: show the Hamlet before and after baking its chunks"`

---

## Order and parallel work

These groups can run in parallel worktrees:
- tasks 1, 2, 3, 4 and 12;
- then 5;
- then 6 and 7 side by side;
- then 8;
- then 9 and 10 side by side;
- then 11, then 13.

Run the one review after task 13, against the Review Focus.

## Not in this plan

- Baking on arrival, the disk cache, the countdown overlay and `BakeOnArrival` / `BakeBudget` (plan 7). Until then, other seeds' unbaked variants are lit live.
- A fade when a chunk bake arrives. It shows up baked the frame its pages are resident. Plan 7 adds the fade it needs for baking on arrival, and can reuse task 10's weight for it.
- Light from neighbouring chunks inside a chunk's bake: a non-goal in the spec. The edge band hides the disagreement at the box. Inside the box, a chunk still doesn't see its neighbour's walls.
- Seam stitching across chart edges inside one lightmap: a later step in the spec.
- BC6H for transfer pages. They stay RGBA16F.
- A cap on the pool's memory. `Resident` and the bytes in use are reported, and nothing is evicted while named.
- Shots of the Crypt and the Courtyard (plan 7).

## Calls made

1. A chunk's mask channels are keyed by the light's chunk-space hash, not the game's key, since every copy keys its lights differently (task 2).
2. A chunk with no plain light stores no irradiance or direction page (task 2).
3. `BakerVersion` stays 2. Nothing here changes what a space's bake holds, so the Gallery stays fresh. The file goes to version 2 and still reads version 1.
4. A chunk's sky transfer counts every escaping path, the direct view of the sky included. Its sun transfer counts only paths with at least one bounce, through next-event estimation towards a uniformly drawn direction (task 5). Escape means missing all geometry, since the top level holds only the chunk's draws.
5. The sun's transfer is applied to every directional light in the frame, so the Hamlet's moon bounces too (task 8).
6. A chunk's pixel turns its transfer light by the normal map with the plain direction when it has one. Otherwise it uses the luminance-weighted direction of the turned sky (task 8).
7. A chunk's edge band runs in from its four sides only, not its top or bottom, and is 0.5 m by default (task 10).
8. A baked space's lightmap gets no edge band. Its probes get one, one probe spacing wide on all six faces. A space is drawn around one whole place, so its static surfaces stop at the box, but things that move cross it (task 10).
9. The edge band has no Graphics switch. It is how a bake is drawn correctly, and `edgeBlend: 0` turns it off for an area (task 10).
10. The Hamlet's roof and accent colours belong to the variant: per kind, and per cottage layout. A tint is part of a draw's identity, so colours per building would make every building its own bake (task 11).
11. The Hamlet bakes at 10 texels a metre. Its chunks are lit mostly by soft sky transfer, and it keeps the files small (task 11).
12. The Hamlet's bakes are committed if they total 16 MB or less (task 11).
13. The Hamlet's day-switched lamps are Realtime and its forge fires Mixed. A lamp that goes out would leave its baked bounce behind, since a chunk's plain light can't be scaled at runtime (task 11). The spec says "mixed and flickering"; see the open question in the report.
14. A chunk counts as fresh only once its pages are resident, so its Baked lights are never dropped before its bake can draw them (task 7).
15. A chunk bake is released at the end of the first frame that names none of its copies, through the frame-in-flight release list (task 7).
16. The sky projection reads the sky cube's 16² mip. The sky's L1 is that smooth, so a larger mip adds no detail (task 8).
