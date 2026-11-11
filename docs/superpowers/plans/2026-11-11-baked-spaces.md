# Baked Spaces, the Baker and the Gallery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bake a space's bounced light, directional light, shadow mask and probes on the GPU into a `.lightbake` file, cook it, draw lightmapped surfaces from it at runtime, and show it all in the Gallery.

**Architecture:** The bake API from plan 4 already decides *what* is baked and whether a bake is fresh (`BakeFreshness.Resolve`, `BakeInputs`, `IBakeStore`). This plan fixes its five parked faults first (tasks 1–5), then adds the baker in layers: a CPU layout and channel pass (tasks 7–8), a file format (task 9), GPU texel setup, path tracing, probes and filtering (tasks 10–13), the `--bake` command and cooker (tasks 14–15), and runtime use through two new G-buffer targets and the shadow mask (tasks 16–19). The Gallery becomes a static, baked scene (tasks 6, 20) and the last task takes the shots.

**Tech Stack:** C# / .NET, Silk.NET Vulkan, GLSL compiled to SPIR-V (ray queries via `trace.glsl`), System.IO.Compression (Brotli), BCnEncoder.Net in Super.Cook, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`, Order steps 5, 6 and 10 ("The bake", "At runtime", "The Gallery"). Roadmap row: plan 5.

---

## Global Constraints

- Braces on every control statement. Conventional commits, no Co-Authored-By lines and no Claude author.
- Docs change in the same task as the code. Before each commit, search `docs/` for the names you touched.
- Every new effect gets an on/off switch whose default looks best. Here that is `Graphics.BakedLighting` (on), mapped to `RenderEffects.BakedLighting`.
- GPU tests carry `[Trait("Needs", "GPU")]` and `[Trait("Speed", "Slow")]` and sit in `[Collection(GpuCollection.Name)]`. They check `fixture.ValidationErrors` is empty.
- Follow the CLAUDE.md test rules: prove each test can fail by breaking the code and reverting, hand-worked expected values, no fixed sleeps, check exception messages.
- Plan 3 (live probes) is not merged. Nothing here may use its code. Task 19 says how the two meet.
- With no `.lightbake` file the renderer must draw byte-for-byte what it draws today (plan 4's `NoBakeGpuTests` keep passing).

## Review Focus

The final review should press on these. Each has a named test in the task that owns it.

1. A dropped Baked sun rebuilds the sky twice a frame and loses the sun disc. Task 4, `Dropped_baked_sun_keeps_the_sky_and_its_disc`.
2. `Resolve` allocates on frames that name baked areas. Task 3, `Resolving_a_frame_again_allocates_nothing`.
3. `default(BakeSettings).TexelsPerMetre` is 0. Task 1, `Default_settings_bake_twenty_texels_a_metre`.
4. Shaders and named textures are hashed by name only, so editing one leaves a stale bake looking fresh. Task 5, `Editing_a_code_mesh_shader_changes_its_identity` and `Editing_a_texture_a_cooked_model_names_changes_its_identity`.
5. Ownership grouping splits a model when its draws are interleaved with another model's. Task 2, `Interleaved_parts_of_a_model_are_filed_together`.

Beyond those: lightmap UVs must not overwrite `inUv2` (mask textures still read it), the G-buffer change must reach every G-buffer pipeline, and no-bake frames must stay identical.

---

### Task 1: A real default for `BakeSettings`, and quality out of the hash

`BakeSettings` is `readonly record struct BakeSettings(int TexelsPerMetre = 20, BakeQuality Quality = BakeQuality.Final)`. The parameter defaults only apply to `new BakeSettings()`; `default(BakeSettings)` has 0 texels a metre. Store an offset from 20 so `default` means 20. Quality decides how long a bake runs, not what it shows, so a Preview bake should count as fresh: take Quality out of the input hash and record it in the file header (task 9). This settles plan 4's call 18.

**Files:**
- Modify: `src/Rendition/Baking/BakeInputs.cs`, `src/Rendition/Baking/BakeInputHash.cs` (`AddSettings`, `BakerVersion = 2`), `src/Rendition/Renderer.cs` (`ResolveBakes`)
- Test: `tests/Super.Tests/BakeInputHashTests.cs`, new `tests/Super.Tests/BakeSettingsTests.cs`
- Docs: `docs/rendering.md` "Baked lighting"

**Interfaces:**

Produces:
```csharp
public readonly record struct BakeSettings
{
    public const int DefaultTexelsPerMetre = 20;
    public const int MinTexelsPerMetre = 1;
    public const int MaxTexelsPerMetre = 200;
    public BakeSettings(int texelsPerMetre = DefaultTexelsPerMetre, BakeQuality quality = BakeQuality.Final);
    public int TexelsPerMetre { get; }   // stored as an offset from DefaultTexelsPerMetre
    public BakeQuality Quality { get; }
}
```

- [ ] **Step 1: Write the failing tests.** `Default_settings_bake_twenty_texels_a_metre` (`default(BakeSettings).TexelsPerMetre == 20` and `default(BakeSettings) == new BakeSettings(20)`). `Texels_a_metre_outside_one_to_two_hundred_throws` (0 and 201 throw `ArgumentOutOfRangeException` whose message names `texelsPerMetre` and the range). `Quality_does_not_change_the_input_hash` (Preview and Final give equal `BakeInputs.Settings`). `Texel_density_changes_the_input_hash` (20 vs 40 differ).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeSettingsTests|FullyQualifiedName~BakeInputHashTests"`
- [ ] **Step 3: Implement.** Replace the positional record with the struct above (field `_texelsOffset`). `AddSettings` adds `TexelsPerMetre` only. Bump `BakerVersion` to 2. Update callers that use named arguments (`TexelsPerMetre:` / `Quality:`) to the new parameter names, including `ResolveBakes` (`new BakeSettings(quality: BakeQuality.Final)` becomes `default`).
- [ ] **Step 4: Run and prove.** Run the filter again; all pass. Put Quality back into `AddSettings`, see `Quality_does_not_change_the_input_hash` fail, revert. Make the field a plain value, see the default test fail, revert.
- [ ] **Step 5: Docs and commit.** In rendering.md say the density is 20 texels a metre by default, 1–200, and that quality doesn't make a bake stale. `git commit -m "fix: give bake settings a real default and keep quality out of the hash"`

### Task 2: File interleaved parts of a model together

`ModelEnd` in `BakeFreshness.cs` groups only *consecutive* static draws with equal `BakeIds.Instance` and `Transform`. A game that draws A0, B0, A1 splits A in two, and the halves can land in different chunks.

**Files:**
- Modify: `src/Rendition/Baking/BakeFreshness.cs`
- Test: `tests/Super.Tests/BakeFreshnessTests.cs`

**Interfaces:**

Consumes: `BakeIds.Instance(ulong)`, `DrawItem.BakeId`, `DrawItem.Transform`.

Produces (internal):
```csharp
internal readonly struct ModelOrder : IComparer<int>   // Instance, then the 16 transform elements, then draw index
```

- [ ] **Step 1: Write the failing tests.** `Interleaved_parts_of_a_model_are_filed_together`: draws [A0, B0, A1] where A's union sphere centre is inside chunk 1 but A1's own sphere centre is outside it; assert both A draws are in chunk 1's inputs (use `RecordingStore` to read the chunk inputs). `Draw_order_does_not_change_chunk_inputs`: the same draws in two orders give equal `BakeInputs`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeFreshnessTests"`
- [ ] **Step 3: Implement.** Collect the indices of static draws into scratch, sort with `ModelOrder`, then walk runs of equal (Instance, Transform). Keep the union sphere per run as today. Task 3 moves the scratch into the resolver; here a local array is fine.
- [ ] **Step 4: Run and prove.** All pass. Restore the consecutive-only walk, see `Interleaved_parts…` fail, revert.
- [ ] **Step 5: Commit.** `git commit -m "fix: group a model's static draws even when they are interleaved"`

### Task 3: An allocation-free resolver

`BakeFreshness.Resolve` allocates arrays, lists, a dictionary, strings and a new `BakeFrame` every frame that names baked areas. Move all of it into a reusable resolver.

**Files:**
- Create: `src/Rendition/Baking/BakeResolver.cs`
- Modify: `src/Rendition/Baking/BakeFreshness.cs` (`Resolve` wraps a new resolver; `BakeFrame` gains an internal `Reset`), `src/Rendition/Renderer.cs` (one `BakeResolver` field used by `ResolveBakes`)
- Test: `tests/Super.Tests/BakeResolverTests.cs`

**Interfaces:**

Produces:
```csharp
public sealed class BakeResolver
{
    public BakeResolver(IBakeStore store);
    public IBakeStore Store { get; set; }
    // The returned frame is owned by the resolver and valid until the next call.
    public BakeFrame Resolve(in FrameView frame, ReadOnlySpan<DrawItem> draws, Func<DetailHandle, BakeSphere> bounds,
        ulong environment, BakeSettings settings, AnimatedLights animated);
}
```

`BakeFreshness.Resolve(...)` keeps its signature and returns `new BakeResolver(store).Resolve(...)`, so existing tests keep working.

- [ ] **Step 1: Write the failing tests.** `Resolving_a_frame_again_allocates_nothing`: a space, two chunks, one dropped Baked light and one dropped Baked sun; warm up with three calls; then `GC.GetAllocatedBytesForCurrentThread()` around ten calls must not change. Use a store whose `IsFresh` doesn't allocate and an `AnimatedLights` writing to `TextWriter.Null`. `A_resolver_gives_the_same_frame_as_BakeFreshness` compares `SpaceFresh`, `ChunkFresh`, `ModeOf` and the dropped lists for the fixtures in `BakeFreshnessTests`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeResolverTests"` (the class doesn't exist yet: compile failure counts).
- [ ] **Step 3: Implement.** Grow-only arrays for hashes, tracked flags, owners, chunk inputs and fresh flags; reused `List<ulong>` per chunk slot, cleared; a `Dictionary` of modes cleared each call; reused dropped lists exposed through `BakeFrame` as `IReadOnlyList<int>`; the `ModelOrder` sort on a scratch `int[]` with a cached comparer; file names cached by (name, hash) in a small dictionary so `BakeFiles.Chunk` and `BakeFiles.Space` run once per distinct key; `SpaceInputs` takes scratch spans. `BakeFrame.Reset` replaces `new BakeFrame`.
- [ ] **Step 4: Run and prove.** All pass, including every `BakeFreshnessTests` test. Put `new List<int>()` back for the dropped lights and see the allocation test fail; revert.
- [ ] **Step 5: Docs and commit.** design.md: the resolver and that its frame is valid for one frame. `git commit -m "perf: resolve baked areas without allocating"`

### Task 4: Work out the sky's sun once, from the original view

When `ResolveBakes` drops a Baked sun, `Render` calls itself with the sun removed. That inner frame calls `BakeSky` again with no sun, so the sky is rebuilt twice a frame, and `WriteUniforms` takes `SkyModel.Sun(view)` from the inner view, so the sun disc disappears.

**Files:**
- Modify: `src/Rendition/Renderer.cs` (`Render` near the `BakeSky(view)` call, `WriteUniforms` near `var sky = SkyModel.Sun(view)`, `Capture`)
- Modify: `tests/Rendition.Gpu.Tests/GpuFixture.cs` (make the view builder `internal FrameView View(DebugView)`)
- Test: `tests/Rendition.Gpu.Tests/BakedSunGpuTests.cs`

**Interfaces:**

Produces (internal, for tests):
```csharp
internal int SkyRebakes { get; }          // counts _environment.Rebake calls from BakeSky
internal Vector4 LastSunDisc { get; }     // the SunDisc written by the last main-view WriteUniforms
```

- [ ] **Step 1: Write the failing test.** `Dropped_baked_sun_keeps_the_sky_and_its_disc`: renderer with `Bakes = new EveryBakeFresh()`, an `AnalyticSky`, a Baked shadow-casting sun and a `BakedSpace` around the scene. Render three frames. Assert `SkyRebakes == 1`, `LastSunDisc.X + Y + Z > 0`, and no validation errors.
- [ ] **Step 2: Run and see it fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakedSunGpuTests"`
- [ ] **Step 3: Implement.** Call `BakeSky(view)` only when `!_drawingWithoutBakedLights`. `WriteUniforms` reads the stored `_bakedSky` (set by `BakeSky`) instead of `SkyModel.Sun(view)`; probe faces still zero the disc. Check `Capture` takes the same path.
- [ ] **Step 4: Run and prove.** Passes. Remove the `!_drawingWithoutBakedLights` guard: the rebake count goes to 3+ and the test fails. Restore `SkyModel.Sun(view)` in `WriteUniforms`: the disc test fails. Revert both.
- [ ] **Step 5: Commit.** `git commit -m "fix: keep the sky and sun disc when a baked sun is dropped"`

### Task 5: Hash what shaders and named textures contain

`BakeIdentity.Model` hashes a code mesh's shader as `ShaderItem.Name`, and a cooked model only by its own file, though the model refers to shaders, shader textures and shared textures by name. Editing any of those leaves the bake looking fresh.

**Files:**
- Modify: `src/Super/Rendering/BakeIdentity.cs`, `src/Super/Content.cs` (an internal shader-bytes reader), `src/Super/Rendering/SceneRenderer.cs` (`LoadShader` becomes `internal static`)
- Test: `tests/Super.Tests/BakeIdentityTests.cs`
- Docs: `docs/content.md` "Baked lighting" (what makes a bake stale)

**Interfaces:**

Consumes: `IContentFiles.HashOf(string)`, `ModelData.Read(Stream, string, Func<string, TextureData>?)`, `ModelData.MaterialShaders`.

Produces:
```csharp
// Content
internal Func<string, byte[]> ShaderBytes { get; set; } = SceneRenderer.LoadShader;
// BakeIdentity
public static ulong Model(ModelRef model, IReadOnlyList<object> items, IContentFiles files, Func<string, byte[]> shaderBytes);
```

- [ ] **Step 1: Write the failing tests.** `Editing_a_code_mesh_shader_changes_its_identity`: same `GeneratedMesh`, `ShaderBytes` returns two different byte arrays, identities differ. `Editing_a_texture_a_cooked_model_names_changes_its_identity`: a temp-folder `IContentFiles` holding a cooked model that names a shared texture; rewrite the texture's bytes; identity changes. `Same_contents_keep_the_identity`: rewriting the same bytes keeps it.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~BakeIdentityTests"`
- [ ] **Step 3: Implement.** A code mesh's shader adds `shaderBytes(name)` (as a hash of the bytes). A cooked model reads its `ModelData` once, then adds `files.HashOf` of every shader file, shader texture and shared texture it names. `Content.IdentityOf` passes `ShaderBytes`; the cache still clears on unload and on hot reload.
- [ ] **Step 4: Run and prove.** All pass. Hash the shader's name again and see the shader test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "fix: hash shader and texture contents in bake identities"`

### Task 6: The Gallery as a baked space

Make the Gallery's building static, name its baked space and give each light the mode the spec asks for. With no bake on disk nothing changes on screen yet.

**Files:**
- Modify: `examples/Gallery/GalleryGame.cs`, `examples/Gallery/Building.cs`
- Test: none new; the check is the screenshot diff in step 4.
- Docs: `examples/Gallery/README.md`

The spec's light modes:

| Light | Mode | Shadows |
|---|---|---|
| Sun through the skylight | Mixed | Real-time cascades; mask past the last |
| Lamp room, lamps 1 and 2 | Baked | — |
| Lamp room, lamp 3 | Mixed | Real-time |
| Chapel lantern (animated) | Mixed | Real-time; logged as animated if made Baked |
| Chapel candles (flicker) | Mixed | No shadow tiles; mask only |

- [ ] **Step 1: Make pieces static.** Build the building's meshes with `isStatic: true` so they get lightmap UVs, and draw them with `view.Model(1 + i, pieces[i].Mesh, pieces[i].Transform, isStatic: true)`. The bust, whale and brass sphere stay dynamic.
- [ ] **Step 2: Name the space.** `view.BakedLighting("gallery", min, max, 1.65f)` with the same box as the existing `view.LightProbes(...)` call.
- [ ] **Step 3: Set light modes** as in the table.
- [ ] **Step 4: Check nothing moved.** Take `--screenshot --seed 1 --ticks 120 --size 1280x720` before and after this task and compare with ffmpeg `blend=all_mode=difference`; it must be black. Report the validation-message count.
- [ ] **Step 5: Docs and commit.** README: the light modes. `git commit -m "feat: make the Gallery a baked space"`

### Task 7: Lightmap layout

Give each static draw a rectangle on a page. Parts of one instance with the same material share a rectangle, because each mask group's Uv2 fills its own 0–1.

**Files:**
- Create: `src/Rendition/Lightmaps/LightmapLayout.cs`
- Modify: `src/Rendition/Renderer.cs` (`CreateMesh` records each mesh's world-free surface area)
- Test: `tests/Super.Tests/Lightmaps/LightmapLayoutTests.cs`
- Docs: `docs/design.md` (layout)

**Interfaces:**

Consumes: `SkylinePacker.TryPack(...)`, `LightmapUnwrap.Gutter`, `BakeIds.Instance`.

Produces:
```csharp
public readonly record struct LightmapRect(int Page, Vector2 Scale, Vector2 Offset);
public sealed class LightmapLayout
{
    public const int PageSize = 2048;
    public const int MaxPages = 8;
    public const int MinSide = 8;
    public const int MaxSide = 1024;
    public static LightmapLayout Build(ReadOnlySpan<DrawItem> draws, Func<MeshHandle, float> meshArea, BakeSettings settings);
    public int Pages { get; }
    public IReadOnlyList<(ulong BakeId, LightmapRect Rect)> Draws { get; }
    public bool TryGet(ulong bakeId, out LightmapRect rect);
    public IReadOnlyList<ulong> Left { get; }   // draws that didn't fit in eight pages
}
```

- [ ] **Step 1: Write the failing tests.** `A_one_metre_quad_at_twenty_texels_gets_a_twenty_texel_side`. `Sides_clamp_to_eight_and_1024`. `Parts_of_one_instance_with_one_material_share_a_rectangle`. `A_scaled_transform_scales_the_area` (a 2× uniform scale gives a 4× area, so 40 texels a side). `Wind_and_transparent_draws_get_no_rectangle`. `Rectangles_never_overlap_and_keep_a_gutter` (place many random sizes, then check every pair over 10 seeds). `A_draw_that_does_not_fit_eight_pages_is_left_out_and_named`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~LightmapLayoutTests"`
- [ ] **Step 3: Implement.** Key a rectangle by (instance, transform, material). Area is mesh area × `|det(transform 3×3)|^(2/3)`. Side is `sqrt(area) × TexelsPerMetre`, clamped. Pack page by page with `SkylinePacker.TryPack` at `LightmapUnwrap.Gutter`. Scale and offset map Uv2's 0–1 inside the gutter-free rectangle.
- [ ] **Step 4: Run and prove.** All pass. Drop the gutter from `TryPack` and see the gutter test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: lay out baked draws on lightmap pages"`

### Task 8: Shadow mask channels

**Files:**
- Create: `src/Rendition/Baking/ShadowMaskChannels.cs`
- Test: `tests/Super.Tests/ShadowMaskChannelsTests.cs`

**Interfaces:**

Produces:
```csharp
public static class ShadowMaskChannels
{
    public const int Count = 4;
    // Index -1 means no channel. A mixed sun takes 0. Points and spots are coloured greedily by range overlap.
    public static void Assign(BakedSpace space, ReadOnlySpan<Light> suns, ReadOnlySpan<LocalLight> lights,
        Span<int> sunChannels, Span<int> lightChannels, List<int> unassigned);
}
```

- [ ] **Step 1: Write the failing tests.** `A_mixed_sun_takes_channel_zero`. `Lights_whose_ranges_do_not_overlap_share_a_channel`. `Five_overlapping_mixed_lights_leave_one_without_a_channel` (with a sun, four overlapping lights leave one). `Baked_and_real_time_lights_take_no_channel`. `Lights_outside_the_space_take_no_channel`. `Assignment_does_not_depend_on_light_order` over 10 shuffles (compare the set of lights left over by key).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~ShadowMaskChannelsTests"`
- [ ] **Step 3: Implement.** Only Mixed lights that cast shadows or have no shadow tile and sit in the space. Order by key, then greedy colouring of the range-overlap graph into the channels left after the sun.
- [ ] **Step 4: Run and prove.** All pass. Order by list index instead of key and see the order test fail; revert.
- [ ] **Step 5: Commit.** `git commit -m "feat: give mixed lights shadow mask channels"`

### Task 9: The `.lightbake` file

**Files:**
- Create: `src/Rendition/Baking/LightBakeFile.cs`
- Test: `tests/Super.Tests/LightBakeFileTests.cs`
- Docs: `docs/design.md` (file layout)

**Interfaces:**

Produces:
```csharp
public enum LightBakePageFormat : byte { Rgba16f = 0, Rgba8 = 1, Bc6h = 2, Bc7 = 3 }
public sealed record LightBakePage(int Width, int Height, LightBakePageFormat Format, byte[] Pixels);
public sealed record LightBakeReport(IReadOnlyList<ulong> LightsWithoutChannel, float InvalidTexelShare, int Samples, TimeSpan Time);
public sealed record LightBakeFile(
    BakeInputs Inputs, BakeQuality Quality,
    IReadOnlyList<(ulong BakeId, LightmapRect Rect)> Draws,
    IReadOnlyList<LightBakePage> Irradiance, IReadOnlyList<LightBakePage> Direction, IReadOnlyList<LightBakePage> Mask,
    LightBakePage? ProbeIrradiance, LightBakePage? ProbeDepth, ProbeVolumeSettings? Probes,
    IReadOnlyList<(ulong LightKey, int Channel)> Channels, LightBakeReport Report)
{
    public const uint Magic = 0x4B41424C; // "LBAK"
    public const int Version = 1;
    public byte[] ToBytes();
    public static LightBakeFile Read(ReadOnlySpan<byte> bytes);
    public static BakeInputs ReadInputs(ReadOnlySpan<byte> bytes);   // header only, for freshness
}
```

The header (magic, version, the four `BakeInputs` hashes, quality) is uncompressed so freshness never inflates pages. Each page's pixels are Brotli-compressed. The same container holds cooked pages (task 15) through `Format`.

- [ ] **Step 1: Write the failing tests.** `A_file_reads_back_what_was_written` (all fields). `Reading_inputs_does_not_need_the_pages` (truncate after the header; `ReadInputs` still works). `A_wrong_magic_or_version_throws` with messages naming the file part. `Pages_are_compressed` (a flat page's bytes are much smaller than its raw size).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~LightBakeFileTests"`
- [ ] **Step 3: Implement** with `BinaryWriter` and `BrotliStream`, little-endian.
- [ ] **Step 4: Run and prove.** All pass. Skip writing quality and see the round-trip test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: read and write .lightbake files"`

### Task 10: Texel setup on the GPU

Rasterize each baked draw with its lightmap UV as the position to find each texel's world position and normals, then mark texels that see mostly back faces as invalid.

**Files:**
- Create: `src/Rendition/Baking/Baker.cs` (internal; owns bake resources), `src/Rendition/Shaders/bake_texels.vert`, `bake_texels.frag`, `bake_validity.comp`
- Test: `tests/Rendition.Gpu.Tests/BakeTexelGpuTests.cs`

**Interfaces:**

Consumes: `LightmapLayout`, `InstanceBuffer`, `SceneDraws` vertex layout (attribute 6 is Uv2), `trace.glsl` (`traceClosest`).

Produces (internal):
```csharp
internal sealed class Baker : IDisposable
{
    public Baker(VulkanContext context, Renderer renderer);
    // Targets per page: position (RGBA32F, w = 1 when covered), geometric normal and shading normal (RGBA16F), validity (R8).
    public void SetUpTexels(CommandBuffer cmd, LightmapLayout layout, ReadOnlySpan<DrawItem> draws);
    internal float[] ReadTexels(int page);   // for tests
}
```

Use conservative rasterization (`VK_EXT_conservative_rasterization`) when the device has it, otherwise 8× MSAA resolved by taking the covered sample nearest the texel centre. A texel is invalid when more than half of 16 short rays (length 5 cm) hit back faces.

- [ ] **Step 1: Write the failing tests.** `A_quad_covers_its_rectangle_with_its_positions` (a 1 m quad at 20 texels; the centre texel's position is within 3 cm of the hand-worked point). `A_texel_inside_a_wall_is_invalid` (a quad pushed half into a closed box). `A_thin_triangle_still_covers_a_texel` (conservative or MSAA path). No validation errors.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeTexelGpuTests"`
- [ ] **Step 3: Implement.** The vertex shader outputs `vec4(uv2 * rect.Scale + rect.Offset, 0, 1)` mapped to clip space; the fragment shader writes world position and normals from the instance transform.
- [ ] **Step 4: Run and prove.** All pass. Set the probe-ray share to 1.0 and see the wall test fail; revert.
- [ ] **Step 5: Commit.** `git commit -m "feat: set up lightmap texels on the GPU"`

### Task 11: The path tracer and `Renderer.Bake`

**Files:**
- Create: `src/Rendition/Shaders/bake_trace.comp`, `src/Rendition/Shaders/bake_lights.glsl`
- Modify: `src/Rendition/Baking/Baker.cs`, `src/Rendition/Renderer.cs` (public `Bake`)
- Test: `tests/Rendition.Gpu.Tests/BakeTraceGpuTests.cs`
- Docs: `docs/rendering.md` "Baked lighting" (what is baked, for each light mode)

**Interfaces:**

Consumes: `trace.glsl` (`traceClosest`, `traceSolid`, `hitAlbedo`), `ShadowMaskChannels.Assign`, `LightmapLayout.Build`, `BakeResolver` inputs for the header.

Produces:
```csharp
// Renderer
public byte[] Bake(in FrameView view, ReadOnlySpan<DrawItem> draws, BakeSettings settings);
```

`Bake` bakes the frame's space. It throws `InvalidOperationException("Bake needs a baked space in the frame; call view.BakedLighting first.")` when the frame has none. Chunks come in plan 6.

The tracer, as the spec says: one ray per texel per pass into float32 accumulators; light from emissive surfaces, Baked lights in full, and the bounce of Mixed lights (their first hit left out); the sky and sun for a space. Next-event estimation samples each light's area (sphere, disc, the sun's `LightAngle` cone): all lights when there are 16 or fewer, otherwise 8 RIS candidates. Up to 4 bounces, Russian roulette after the 2nd. Direction is stored as `E*(dot(n,d)*0.5+0.5)/max(1e-4,|d|)`. The mask is the share of a light's area the texel sees, from shadow rays only. Preview takes 64 samples, Final 1024. Transmission through half-opaque panes uses `traceSolid` coverage as a weight.

- [ ] **Step 1: Write the failing tests** (the spec's six). `A_plane_under_a_uniform_sky_reads_pi_L` (within 5% at Preview). `A_closed_box_reads_black`. `The_mask_is_zero_behind_a_wall_and_one_in_front`. `A_mixed_light_bounces_but_its_direct_light_is_not_baked` and `A_baked_light_is_baked_in_full` (same scene, two modes; compare the texel under the light: baked ≈ direct + bounce, mixed ≈ bounce only). `A_moving_light_is_left_out_and_the_bake_stays_fresh` (an `AnimatedLights` marks it; resolve after baking reports fresh). `Light_through_a_half_opaque_pane_is_weakened`. Plus `Bake_without_a_space_throws` checking the message.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeTraceGpuTests"`
- [ ] **Step 3: Implement.** Dispatch passes in batches so no submit runs longer than ~100 ms (keeps the OS from resetting the device). Read back, fill a `LightBakeFile` (irradiance RGBA16F, direction RGBA8, mask RGBA8) with the frame's `BakeInputs` and the report, and return `ToBytes()`.
- [ ] **Step 4: Run and prove.** All pass. Include the Mixed light's first hit and see the mixed test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: path trace baked lighting into a lightmap"`

### Task 12: The space's probes in the bake

**Files:**
- Modify: `src/Rendition/Baking/Baker.cs`, `src/Rendition/Shaders/bake_trace.comp` (a probe entry point)
- Test: `tests/Rendition.Gpu.Tests/BakeProbeGpuTests.cs`

**Interfaces:**

Consumes: `ProbeGrid` (`ProbeVolumeSettings`, `Counts`, `Position`), `ProbeVolume.RecordFold`, `ProbeVolume.IrradianceTexels` (8), `DepthTexels` (16), `Columns` (64).

Produces: the bake fills `LightBakeFile.ProbeIrradiance`, `ProbeDepth` and `Probes` in the same tile layout `ProbeVolume` uses, so runtime can copy them straight in.

- [ ] **Step 1: Write the failing tests.** `A_probe_under_a_uniform_sky_reads_its_radiance` (an open plane; the up-facing texel ≈ L). `A_probe_inside_a_closed_box_reads_black`. `Probe_tiles_match_the_live_layout` (atlas size equals `Columns × (IrradianceTexels + 2)` wide).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeProbeGpuTests"`
- [ ] **Step 3: Implement.** Trace a cube of `FaceSize` (32) per probe with the same light sources, then fold with `RecordFold`.
- [ ] **Step 4: Run and prove.** All pass. Skip the fold and see the layout test fail; revert.
- [ ] **Step 5: Commit.** `git commit -m "feat: bake the space's light probes"`

### Task 13: Filtering and dilation

**Files:**
- Create: `src/Rendition/Shaders/bake_filter.comp`, `bake_dilate.comp`
- Modify: `src/Rendition/Baking/Baker.cs`
- Test: `tests/Rendition.Gpu.Tests/BakeFilterGpuTests.cs`

The filter is à-trous, 4 passes, weighted by position and normal, and never crosses charts (a texel only takes neighbours with the same rectangle). Then 8 rings of dilation fill invalid and uncovered texels from valid neighbours; with `LightmapUnwrap.Gutter` = 2 this covers every gutter.

- [ ] **Step 1: Write the failing tests.** `Filtering_keeps_a_flat_lightmap_flat` (constant in, same constant out). `Filtering_lowers_noise` (Preview noise variance falls by at least half). `Filtering_does_not_blend_across_charts` (two rectangles, one lit and one black, side by side; the black stays black). `Dilation_fills_the_gutter`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakeFilterGpuTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** All pass. Drop the chart check and see the chart test fail; revert.
- [ ] **Step 5: Commit.** `git commit -m "feat: filter and dilate baked lightmaps"`

### Task 14: `--bake`, `--bake-check` and `--bake-quality`

**Files:**
- Modify: `src/Super/GameArguments.cs`, `src/Super/Game.cs`, `src/Super/Rendering/GameWindow.cs`
- Test: `tests/Super.Tests/GameArgumentsTests.cs`
- Docs: `docs/command-line.md` (move from "Planned" to a new "Baking lighting" section)

**Interfaces:**

Produces:
```csharp
internal sealed record GameArguments(..., bool Bake, bool BakeCheck, BakeQuality BakeQuality);
// GameWindow, beside Screenshot<TGame>(GameHost<TGame> host, long ticks, string path)
public int Bake<TGame>(GameHost<TGame> host, long ticks, BakeQuality quality, string folder);   // returns validation messages
public int BakeCheck<TGame>(GameHost<TGame> host, long ticks);                                   // returns the number of stale bakes
```

`--bake` runs hidden like `--screenshot`, steps `--ticks` (default 1) ticks, calls `Renderer.Bake` for the frame's space and writes `DevSettings.Roots[0]/lighting/<name>.lightbake`. It prints the report: lights without a channel, invalid texel share, samples and time. `--bake-check` resolves the frame and exits 1 when a bake is stale, listing `BakeInputs.Changes`. `--bake-quality preview|final` defaults to final. `--bake` with `--screenshot`, `--record` or `--profile` throws.

- [ ] **Step 1: Write the failing tests.** `Bake_is_parsed`, `Bake_quality_reads_preview_and_final`, `An_unknown_bake_quality_throws` (message lists both), `Bake_with_screenshot_throws` (message names both options), `Bake_quality_without_bake_throws`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~GameArgumentsTests"`
- [ ] **Step 3: Implement.** Allow `--ticks` with `--bake`.
- [ ] **Step 4: Run and prove.** Run the filter; then `dotnet run --project examples/Gallery -- --bake --bake-quality preview` writes `examples/Gallery/content/lighting/gallery.lightbake` with 0 validation messages, and `--bake-check` exits 0. Remove the incompatibility check and see its test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: bake lighting from the command line"`

### Task 15: Cooking bakes

**Files:**
- Create: `src/Super.Cook/Cookers/LightBakeCooker.cs`
- Modify: `src/Rendition/Types.cs` (`TextureFormat.Bc6h`, if BCnEncoder.Net 2.3.0 encodes BC6H; it does through `CompressionFormat.Bc6U`), the cooker registry
- Test: `tests/Super.Cook.Tests/LightBakeCookerTests.cs`
- Docs: `docs/content.md` "Baked lighting"

**Interfaces:**

Consumes: `ICooker` (`Id`, `Version`, `Extensions`, `StartRank`, `Cook(CookInput, CookOutput)`), `Bc7Encoder.Encode`, `LightBakeFile`.

Produces: a cooker for `.lightbake` that rewrites irradiance pages as BC6H, direction and mask pages as BC7, and keeps probe atlases RGBA16F. A page whose sides aren't multiples of 4, or a device without BC support at load time, stays uncompressed.

- [ ] **Step 1: Write the failing tests.** `Cooking_compresses_irradiance_to_bc6h`, `Cooking_compresses_direction_and_mask_to_bc7`, `Cooking_keeps_the_input_hash` (`LightBakeFile.ReadInputs` equal before and after), `Cooked_irradiance_stays_close` (mean error under 2% on a gradient).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Cook.Tests --filter "FullyQualifiedName~LightBakeCookerTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** All pass. Rewrite the header's inputs and see the hash test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: cook light bakes to block compression"`

### Task 16: The store, the switch and the stale log

**Files:**
- Create: `src/Rendition/Baking/LightBakeStore.cs`
- Modify: `src/Rendition/Renderer.cs` (`Bakes` becomes `public`), `src/Rendition/Types.cs` (`RenderEffects.BakedLighting = 1 << 18`, added to `All`), `src/Super/Graphics.cs`, `src/Super/Rendering/SceneRenderer.cs` (wire the store; forget on content reload)
- Test: `tests/Super.Tests/LightBakeStoreTests.cs`, `tests/Super.Tests/GraphicsTests.cs`
- Docs: `docs/rendering.md` ("Graphics settings", "Baked lighting"), `docs/content.md`

**Interfaces:**

Produces:
```csharp
public sealed class LightBakeStore : IBakeStore
{
    public LightBakeStore(Func<string, byte[]?> read, TextWriter log);
    public bool IsFresh(string file, BakeInputs inputs);   // reads the header once per file; logs each stale file once, naming inputs.Changes(saved)
    public LightBakeFile? Load(string file);
    public void Forget();                                   // after a reload
}
// Graphics
public bool BakedLighting { get; set; } = true;   // stays on in Fastest()
```

A missing file is not fresh and is not logged. With `BakedLighting` off the renderer treats every bake as missing.

- [ ] **Step 1: Write the failing tests.** `A_matching_header_is_fresh`, `A_missing_file_is_not_fresh_and_logs_nothing`, `A_stale_bake_logs_once_naming_the_changes` (call three times; one line containing "lights"), `Forget_reads_the_file_again`, and in `GraphicsTests` `Baked_lighting_is_on_by_default_and_off_maps_to_the_effect`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Super.Tests --filter "FullyQualifiedName~LightBakeStoreTests|FullyQualifiedName~GraphicsTests"`
- [ ] **Step 3: Implement.** The store caches headers by file name. `IsFresh` must not allocate once warm (task 3's test uses this store too).
- [ ] **Step 4: Run and prove.** All pass. Remove the logged-once set and see the log test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: load light bakes and add the baked lighting switch"`

### Task 17: Lightmaps on surfaces

**Files:**
- Modify: `src/Rendition/Passes/GBuffer.cs` (`BakedFormat = R16G16B16A16Sfloat`, `MaskFormat = R8G8B8A8Unorm`, targets `Baked` and `Mask`), `src/Rendition/Passes/GBufferPass.cs` (`colorAttachments[4..5]`, `ColorAttachmentCount = 6`, mask cleared to 1, baked to 0), every G-buffer pipeline's blend state count, `src/Rendition/Vulkan/InstanceBuffer.cs` (a parallel lightmap-rect buffer at set 0 binding 10), `src/Rendition/Shaders/instances.glsl`, `gbuffer.vert` (new varying at location 7: lightmap UV and page), `surface.glsl` (outputs 4 and 5), `lighting.frag` (bindings 11 and 12), `src/Rendition/Renderer.cs` (`ReflectionLightingHash` adds the bake's input hash; upload pages)
- Test: `tests/Rendition.Gpu.Tests/LightmapGpuTests.cs`
- Docs: `docs/rendering.md` ("Bounced light" rewritten for lightmaps, "Baked lighting"), `docs/design.md` (G-buffer targets)

**Interfaces:**

Consumes: `LightBakeStore.Load`, `LightmapRect`.

Produces: lightmapped pixels take diffuse light from target 4 (alpha 1 marks lightmapped) instead of probes. The directional term is `E*(dot(n,d')*0.5+0.5)` with `d'` decoded from direction. GTAO on lightmapped pixels is scaled by `LightmapOcclusion = 0.5`. `inUv2` is untouched; mask textures still read it.

- [ ] **Step 1: Write the failing tests.** `A_lightmapped_plane_shows_its_baked_light` (bake the plane-under-sky scene, render with the sky off: the pixel is lit). `Without_a_bake_nothing_changes` (pixel-equal to a render with `BakedLighting` off). `Turning_baked_lighting_off_falls_back_to_probes`. `A_material_mask_still_reads_uv2` (a masked material renders the same with and without a bake). `A_fresh_bake_changes_the_reflection_lighting_hash`. No validation errors.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~LightmapGpuTests"`
- [ ] **Step 3: Implement.** Draws without a rectangle get page -1 and write alpha 0. Run every existing GPU test afterwards; they must pass unchanged.
- [ ] **Step 4: Run and prove.** All pass. Read lightmap UVs from `inUv2`'s slot and see the mask test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: draw baked light on lightmapped surfaces"`

### Task 18: The shadow mask at runtime

**Files:**
- Modify: `src/Rendition/Shaders/light_data.glsl` (`LocalLight.fade.w` = mask channel or -1), `src/Rendition/Passes/FrameUniforms.cs` (`GpuLocalLight` to match; a sun mask channel in the uniforms), `lighting.frag`, `src/Rendition/Renderer.cs` (fill channels from the loaded bake)
- Test: `tests/Rendition.Gpu.Tests/ShadowMaskGpuTests.cs`
- Docs: `docs/rendering.md` "Light modes"

A Mixed light's shadow is `min(mask, real-time)`. One with no shadow tile uses the mask alone. In a space, past the sun's last cascade the mask takes over. Lights without a channel behave as today.

- [ ] **Step 1: Write the failing tests.** `A_mixed_light_without_a_tile_is_shadowed_by_the_mask` (a wall between the light and the plane). `A_mixed_light_takes_the_darker_of_mask_and_real_time` (move the wall after baking: the shadow at the old place stays). `The_mask_shadows_past_the_last_cascade` (a far pillar). `A_light_without_a_channel_is_unchanged`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~ShadowMaskGpuTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** All pass. Ignore the mask past the cascades and see that test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: shadow mixed lights with the baked mask"`

### Task 19: Baked probes in the space

**Files:**
- Modify: `src/Rendition/Renderer.cs` (upload the bake's probe atlases into a `ProbeVolume` for the space), `lighting.frag` (inside the space's box use the baked probes)
- Test: `tests/Rendition.Gpu.Tests/BakedProbeGpuTests.cs`
- Docs: `docs/rendering.md` "Baked lighting"

Inside the space's box, dynamic objects and non-lightmapped pixels read the bake's probes instead of the live ones. Outside it, nothing changes. Plan 3 changes the live probe lookup; whichever of plans 3 and 5 merges second wires the order (baked probes first inside a space, live probes elsewhere), and its review checks that.

- [ ] **Step 1: Write the failing tests.** `A_dynamic_sphere_in_the_space_reads_baked_probes` (bake with a lit red wall, then remove the wall's light at runtime: the sphere stays reddish). `Outside_the_space_live_probes_are_used`.
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Rendition.Gpu.Tests --filter "FullyQualifiedName~BakedProbeGpuTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and prove.** All pass. Skip the box test in the shader and see the outside test fail; revert.
- [ ] **Step 5: Docs and commit.** `git commit -m "feat: light dynamic objects with the space's baked probes"`

### Task 20: The Gallery's keys and its bake

**Files:**
- Modify: `examples/Gallery/GalleryGame.cs`
- Create: `examples/Gallery/content/lighting/gallery.lightbake` (made with `--bake --bake-quality final`)
- Docs: `examples/Gallery/README.md`, `docs/rendering.md` (point at the Gallery)

Keys: 1 shows live lighting only (`Graphics.BakedLighting` off). 2 turns bakes on. 3 shows direct light only (`BakedLighting` off and `LightProbes` off). The current mode shows in the corner.

- [ ] **Step 1: Add the keys.**
- [ ] **Step 2: Bake.** `dotnet run --project examples/Gallery -- --bake`. Print the report into the PR notes. Check `--bake-check` exits 0. The lantern, made Baked by hand for a moment, must be named in the log as animated; set it back.
- [ ] **Step 3: Look.** Take `--screenshot --seed 1 --ticks <room tick> --size 1280x720` in each room from the README ticks (hall 660, colour 1065, whale 1515, lamps 1965, chapel 2415) and check them: colour bleeds from the red and blue walls in the colour room, the colonnade's far shadows show in the hall, the candles' shadows show in the chapel, the brass sphere has a real-time shadow.
- [ ] **Step 4: Docs and commit.** README: the keys and how to rebake. `git commit -m "feat: bake the Gallery and switch between lighting modes"`

### Task 21: Before and after shots

**Files:**
- Create: `docs/media/gallery-baked-before-after.webp`, `docs/media/gallery-colour-room-baked.webp`
- Modify: `docs/rendering.md`, `examples/Gallery/README.md`

- [ ] **Step 1: Before.** In a worktree of `feat/lighting` at the commit this plan started from, run `dotnet run --project examples/Gallery -- --screenshot before.png --seed 1 --ticks 120 --size 1280x720` and note the validation-message count.
- [ ] **Step 2: After.** The same command on this branch.
- [ ] **Step 3: Combine.** `ffmpeg -i before.png -i after.png -filter_complex "[0]drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Before':x=20:y=20:fontsize=40:fontcolor=white:box=1:boxcolor=black@0.5[a];[1]drawtext=fontfile='C\:/Windows/Fonts/segoeui.ttf':text='Baked':x=20:y=20:fontsize=40:fontcolor=white:box=1:boxcolor=black@0.5[b];[a][b]hstack,scale=1200:-1" -c:v libwebp docs/media/gallery-baked-before-after.webp`. Repeat at tick 1065 for the colour room.
- [ ] **Step 4: Look at every image** and check its label is true.
- [ ] **Step 5: Docs and commit.** Use the images in rendering.md "Baked lighting" and the Gallery README. `git commit -m "docs: show the Gallery before and after baking"`

---

## Not in this plan

- The P key for showing live probes as spheres. It needs plan 3's `DebugView.Probes`.
- Baked chunks, bake transfer between chunks, and Hamlet (plan 6).
- Baking automatically and baking on arrival (plan 7).
- Shots of the Crypt and the Courtyard.

## Calls made

1. Quality is out of the input hash and goes in the file header, so a Preview bake counts as fresh (settles plan 4's call 18). `BakerVersion` goes to 2.
2. `TexelsPerMetre` must be 1–200.
3. A rectangle's area scales with `|det|^(2/3)` of the transform, which is exact for uniform scale.
4. Key 3 ("direct light only") means `BakedLighting` off and `LightProbes` off.
5. `BakedLighting` stays on in `Graphics.Fastest()`, because a bake costs two texture reads and replaces probe work.
6. `Renderer.Bakes` becomes public so games and tools can supply their own store.
7. Source and cooked bakes share one container; each page records its format.
8. In the lamp room two lamps are Baked and the third is Mixed, so the room shows both.
9. A Mixed light's bounce is baked at its brightness on the frame that was baked; flickering doesn't make the bake stale, because `BakeInputHash.Light` hashes radiance only for Baked lights.
10. `Renderer.Bake` bakes only the frame's space and throws when there is none; chunks come in plan 6.
11. Plan 4 called the baker "plan 6"; the roadmap's numbering (plan 5) wins.
12. Lightmap UVs travel in a new varying (location 7) rather than replacing `inUv2`, since mask textures read it.
13. Bake passes are split so no single submit runs longer than about 100 ms.
14. Whichever of plans 3 and 5 merges second wires the probe lookup order.
