# Browser Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A game made with Super builds into a folder of static files that runs in a desktop browser and looks as close to the desktop game as the browser allows, so an early version can be shared as a link.

**Architecture:** Rendition stops calling Vulkan directly. It draws through a small GPU layer of our own, shaped like WebGPU, with Vulkan as the first backend and WebGPU as the second. Ray tracing, light baking and FSR stay on Vulkan behind a marked escape hatch. In the browser the game is .NET compiled ahead of time to WebAssembly; the WebGPU backend writes resource calls and one command list a frame to a JavaScript shim. Shaders stay GLSL and are compiled twice, to SPIR-V for Vulkan and through SPIR-V to WGSL for the web. Window, input, audio, physics and content each get a browser version behind the seam they already have or a new small one.

**Tech Stack:** C# / .NET 10, `browser-wasm` with `RunAOTCompilation` (the `wasm-tools` workload), JSImport/JSExport, WebGPU, glslc, spirv-opt, naga, Emscripten 3.1.56 for native libraries, Web Audio, xunit, Roe.

**Spec:** There is no separate design document. The decisions are in "Design" below, and they come from the conversation of 2026-11-27 and the spike in `spikes/web-cube` (its findings are in "What the spike measured"). This plan covers the whole effort as stages. Stages 0 and 1a are written to task level here. Every later stage gets its own task-level plan in `docs/superpowers/plans/`, written when the stage before it has merged, because each one depends on what the earlier ones find.

## Global Constraints

- The desktop game must not change. After every pull request in stages 1 and 2, `--screenshot` shots of Shapes, Hamlet, Courtyard and Reef with the same `--seed`, `--ticks` and `--size` match the shots from `main` before the stage began, and `--validate` reports no new messages.
- Desktop speed must not drop. The CPU frame time of Reef and Hamlet, measured the way `docs/profiling.md` describes, rises by no more than 0.2 ms across the whole of stage 1.
- A running frame allocates nothing, on both backends.
- One stage or sub-stage is one pull request into `main`. Work happens one task at a time in the main checkout: no worktrees, no parallel agents. One review at the end of each pull request, not one per task.
- Braces on every control statement.
- Docs in `docs/` change in the same commit as the code. Engine internals go in `design.md`; what a game maker does goes in a new `docs/web.md` and in `command-line.md`.
- `dotnet roe` is clean before each commit. Vulkan descriptor setup that remains goes through `Descriptors`.
- Conventional commits. No Claude author, no `Co-Authored-By` line, no "Generated with Claude Code" line.
- Every new test is proven able to fail. Rebuild before trusting a result; never pass `--no-build`.
- Fast tests: `dotnet test Super.slnx -c Release --filter "Speed!=Slow&Needs!=GPU"`. Rendering work also runs `dotnet test tests/Rendition.Gpu.Tests -c Release`.
- Every effect the browser can't run has a switch that is off there, and the game still starts. A missing feature never throws at startup.
- The NuGet package stays one package named Super.
- Edit files with the Edit tool, not scripts.

## Review Focus

- A shader edited on desktop that still compiles to SPIR-V but no longer converts to WGSL, or passes a WebGPU limit: the build fails with the shader's name and the limit. Pinned in stage 2.
- A browser with no WebGPU, or a GPU without BC textures: the page says so in a sentence and does not show a blank canvas. Pinned in stage 4.
- A tab sent to the background and brought back after a minute: the game takes one clamped step, sound does not burst, and nothing replays a minute of ticks. Pinned in stage 3.
- The window resized or moved to a display with another pixel ratio while a frame is in flight: targets are remade once and no destroyed texture is used. Pinned in stages 1e and 4.
- A sound asked for before the player has clicked: it is held or dropped by rule, and audio starts cleanly on the first click. Pinned in stage 5.

## What the spike measured

5,000 cubes, one draw each, in a Chromium browser on a desktop GPU.

| | Interpreter | Compiled ahead of time |
|---|---|---|
| 5,000 matrices | about 300 ms | 0.9 ms |
| An empty call into JS | about 500 ns | 46 ns |
| 10,008 commands as separate calls | about 8 ms | 2.8 ms |
| The same as one command list | 1.6 ms | 0.9 ms |

The ahead-of-time build's `_framework` folder was 18 MB, 2.8 MB with brotli. The shader route `glslc` → `spirv-opt --split-combined-image-sampler --resolve-binding-conflicts` → `naga` gave working WGSL, and 54 of the engine's compiled shaders converted as they were. Stage 0 recounted: there are 82, and with the fixes in "Stage 0 findings" every one outside ray tracing, multiview and the material set converts and compiles in Chrome.

## Design

### The GPU layer

The layer lives in `src/Rendition/Gpu/` in namespace `Rendition.Gpu`, and is internal. `Renderer`'s public surface already has no Vulkan types except `RendererOptions.InstanceExtensions` and `CreateSurface`, which stage 1e replaces.

GPU objects are small id structs (`BufferId`, `ImageId`, `ViewId`, `SamplerId`, `ShaderId`, `PipelineId`, `BindGroupId`), not classes, so the browser backend can pass them to JavaScript as numbers.

Drawing is recorded, not called. A `CommandList` writes ints into a reused array, and the device plays it: the Vulkan backend into a `VkCommandBuffer`, the web backend by handing the array to JavaScript in one call. Both backends use the list, so the desktop tests exercise the code the browser runs. If the desktop frame time budget above is broken by this, the Vulkan backend may record straight into the command buffer behind the same `CommandList` methods.

Bindings are named. A pass says `Bind.Image("sceneColor", view)`, and each backend looks the name up in the shader's reflection data. This is what absorbs the binding numbers that move when a combined image sampler is split in two for WGSL.

Per-draw data moves out of push constants into a draw-data storage buffer that the vertex shader indexes by the draw's first instance, on both backends. Small constants for fullscreen and compute passes go through `CommandList.SetConstants(bytes)`: push constants on Vulkan, a ring of uniform data with a dynamic offset on the web. WebGPU's immediates are only in Chrome so far and hold 64 bytes, and 11 shaders need more, so the web sends every constant block through the ring.

Image layouts and barriers are the Vulkan backend's job. Passes declare what they render to and what they bind; the backend works out the transitions. WebGPU has no such thing, so passes can't keep doing it by hand.

What the layer does not offer is asked through `GpuAbilities`: `RayQueries`, `Multiview`, `TextureArraysByIndex` (the one global material set), `ConservativeRaster`, `BcTextures`, `TimerQueries`. Vulkan-only work (ray tracing, baking, FSR) gets the Vulkan device by `device as VulkanGpu` in files listed in one allow-list, and a test fails when any other file imports `Silk.NET.Vulkan`.

### What changes for the browser

| Desktop today | Browser |
|---|---|
| One global material set indexed by material | A bind group per material. Texture arrays by index are behind a flag in Chrome and absent elsewhere |
| Any GPU Vulkan accepts | A GPU with `float32-filterable`, `rg11b10ufloat-renderable`, `texture-compression-bc`, `texture-formats-tier1` and `texture-formats-tier2`; the page names what is missing |
| Push constants | Draw-data buffer and `SetConstants` |
| Ray-traced probes, reflections and baking | Off. `CanTrace` is false, a path the Mac already uses. Light baked on desktop ships with the game and is read as usual |
| Multiview cube capture | One face at a time |
| `CmdBlitImage` for mips (3 users) | A downsample draw, on both backends |
| FSR upscaling | TAA, as on the Mac |
| GLFW window and input | A canvas and DOM events |
| The game owns its loop | The browser calls one frame at a time |
| OpenAL behind `IAudioSink`, mixed on a thread | Web Audio behind `IAudioSink`, mixed on the main thread a little ahead |
| Jolt as a native library | Jolt built with Emscripten and linked in |
| Cooked files read from disk | The same files downloaded into the in-memory file system before the game starts |
| Threads | None in version one |

### Decisions and why

The browser is the only WebGPU target. There is no desktop WebGPU backend through wgpu or Dawn. It would be a third thing to keep working, it offers features browsers lack, and it would not test the JavaScript shim, which is what ships. Browser shots are taken by driving headless Chrome.

Version one has no threads. .NET's browser threads need cross-origin isolation headers that simple hosts don't send, and sharing by link on any host is the point. Content loading already takes its `Start` function as a parameter (`ContentLoader.Start`), so the browser gives it one that runs the work in slices.

Version one is for desktop browsers. Phones lack BC textures and have tighter limits; they are stage 9.

Super stays one assembly. Desktop and browser code sit side by side and are picked with `OperatingSystem.IsBrowser()`, which the trimmer treats as a constant, so the desktop libraries fall away in a browser build. Stage 0 built Super for `browser-wasm` as it is, with three trimmer warnings and no rejected package.

The browser build is trimmed, so Super must say what it reaches by reflection. The game type passed to `HostServices.Build<TGame>` gets `[DynamicallyAccessedMembers(PublicConstructors)]`, because without it the trimmer drops the game's constructor. The JSON in `DevSettings` and `ContentReader` moves to source-generated contexts.

Content is downloaded whole before the game starts, with a progress bar. The engine's file reading stays as it is. Loading only what a state needs comes later.

## Stages

Each stage is at least one pull request. Stage 2 can be done before or between the parts of stage 1; everything else is in order.

| Stage | What it delivers | How we know it's done |
|---|---|---|
| 0 | Answers to the open questions, in `spikes/` | A findings section added to this file, and the design above corrected where an answer changes it |
| 1a | Layer core with Vulkan behind it; the fullscreen pass and tonemap use it | Shots match, GPU tests pass |
| 1b | All post-processing passes on the layer | Same |
| 1c | All compute passes on the layer | Same |
| 1d | Geometry passes, draw-data buffer, material binding | Same, and the frame time budget holds |
| 1e | Targets, uploads, mips, present, capture, timers; `RendererOptions` without Vulkan types | Same |
| 1f | Probe, reflection and sky capture with multiview optional | Same, also with multiview forced off |
| 1g | The fence around Vulkan-only code | The import test passes |
| 2 | Web shader build: every non-ray-tracing shader to WGSL with reflection data and a limits check | The build fails on a shader that doesn't convert; CI runs it |
| 3 | Browser host: Super compiles for `browser-wasm`, canvas window, input, frame-at-a-time loop, content download, `-p:SuperTarget=web` | A game with no renderer runs its ticks in a browser and logs input |
| 4 | WebGPU backend and JS shim | Shapes, then Hamlet, then Courtyard with baked light, drawn in Chrome and compared with desktop shots |
| 5 | Web Audio sink | Forest plays its sounds; no glitches at 60 frames a second |
| 6 | Jolt in the browser | Cannonball runs |
| 7 | Saves, fullscreen, pointer lock, gamepad, touch | Each works in Skylark or Crafting |
| 8 | Size, load time, loading screen, hosting guide, CI | An example is published to a static host from CI and loads from a cold cache in a time we write down and accept |
| 9 | Later, unplanned: phones, threads, loading content by state, push constants when browsers get them | |

### Stage 1: the layer, pass by pass

The old and new ways live side by side until 1g. The layer's Vulkan backend wraps the existing `VulkanContext`, so a pass moved to the layer and a pass not yet moved record into the same frame. Each sub-stage moves a group of passes, deletes what they no longer use from `src/Rendition/Vulkan/`, and updates `design.md`.

The order goes from the simplest users to the hardest. Post-processing passes are all `FullscreenPass` with a few sets. Compute passes add storage images and buffers. Geometry passes add vertex input, the draw-data buffer and materials, and are where the frame time budget is at risk. Capture passes add multiview and cube targets.

In 1d, material binding becomes one small abstraction with two shapes: the global indexed set where `TextureArraysByIndex` is true, and a bind group per material where it isn't. Draws are already grouped by material since the engine performance pass, so the second shape adds one bind per group, not per draw. Surface shaders from material graphs (`RendererAssets.CreateSurfaceShader(byte[] spirv)`) are part of this: the cooker must also write their WGSL in stage 2.

### Stage 2: shaders for two targets

`src/Shaders.targets` gains a second pass that compiles each shader with `-DSUPER_WEB`, splits samplers, converts to WGSL and writes a reflection file (names, groups, bindings, sizes) beside it. The Vulkan build writes the same reflection file from its SPIR-V, so named bindings work on both.

The web variant is compiled with `--target-env=vulkan1.1`, which keeps `discard` a kill instead of demote. The shaders that fail are fixed by cause, as stage 0 found them:

- In the shared GLSL, harmless on Vulkan: `writeonly` storage buffers become plain `buffer`; `isnan` and `isinf` become helpers that test bits; `findLSB` takes an `int` in `reflection_probes.glsl`; the signed `%` in `cloud_noise.comp` is written out.
- `decal.vert` and `decal.frag` pass the `mat4` as four `vec4`s.
- `probe_fold.comp` is built without `-O` for the web, which avoids a naga bug.
- The build adds `diagnostic(off, derivative_uniformity);` to the top of every WGSL fragment shader. Seven shaders sample in branches that Vulkan allows and WGSL's analysis can't prove uniform.
- `paint_filter.comp` and `probe_blend.comp` call a barrier where WGSL sees non-uniform flow, because naga copies the workgroup built-ins into private variables. Stage 2 rewrites those reads to use the entry point's parameters, moves the barriers, or tries Tint for these two.
- The material set (`gbuffer`, `transparent`, `shadow`, `finish`, `overlay`, `grass_ground`, `probe_capture.frag`, and `lighting`, `mirror`, `reflection_capture` for the probe textures) gets a `SUPER_WEB` variant with one material's textures per group, matching stage 1d.
- Constants become a uniform block under `SUPER_WEB`, which also removes the array that `lens_flare_visibility` may not put in immediates.
- Ray queries (5) and multiview (`probe_capture.vert`) are not built for the web.

A check runs over the reflection files and fails the build when a shader passes a WebGPU default limit: 4 bind groups, 16 sampled textures and 16 samplers a stage, 8 storage buffers a stage, 8 colour targets. `lighting.frag` binds 24 textures and 24 samplers once split, so it declares its textures and a few shared samplers separately in GLSL, which Vulkan also accepts. Every other non-baking shader was over only on push-constant bytes.

### Stage 3: the browser host

A seam for the platform goes into `src/Super/Rendering/`: the eight files that use GLFW (`OsWindow`, `GameWindow`, `GlfwInput`, `GamepadPoll`, `WindowCursor`, `WindowInputFeed`, `WindowPlay`, `WindowTouch`) sit behind it, and a browser version feeds the same input types from DOM events. `GameLoop` gets a method that runs exactly one frame, which the desktop loop calls in its `while` and the browser calls from `requestAnimationFrame`, with the time step clamped after a pause.

The JavaScript side lives in `src/Super/Web/` and is copied by `Super.targets` when `SuperTarget` is `web`: `index.html`, the loader with its progress bar, and later the GPU and audio shims. `SuperTarget` already exists and is passed to the cooker as `--target`. A web publish turns on ahead-of-time compilation, writes a manifest of the cooked files, and the loader downloads them into the in-memory file system before calling `Main`.

Things that can't exist in a browser are switched off there with a clear message when asked for: hot reload, the cook process, the CPU profiler, `--record`, and launching other processes.

### Stage 4: the WebGPU backend

`WebGpu : GpuDevice` in `src/Rendition/Gpu/Web/` and `gpu.js` beside the loader. Resource creation is one call each, with descriptions passed as packed ints, not JSON. A frame is one `runCommands` call. Pipelines are created with the async calls during loading so the first frame doesn't stall. Reading pixels back is asynchronous in WebGPU, so the layer's readback is a request that completes on a later frame, on both backends (stage 1e shapes it that way).

Shots come from a page mode that runs a fixed number of ticks with a seed and hands back the canvas as a PNG, driven by headless Chrome from a test marked `[Trait("Needs", "Browser")]`. They are compared with desktop shots taken with ray tracing off, by eye and by an error threshold we set once we see how close they are.

### Stages 5 to 8

Audio already mixes in software and hands samples to an `IAudioSink` (`src/Super/Audio/DeviceOutput.cs`). The browser gets a sink that posts blocks to an `AudioWorklet`, and `DeviceOutput`'s thread becomes a pump called once a frame that keeps about 100 ms mixed ahead. Audio starts on the first click or key, which the loader's "click to play" screen provides.

Physics needs `joltc` built with Emscripten 3.1.56 as a static library and linked with `NativeFileReference`, running its jobs on the one thread. Stage 0 made this work and runs at desktop speed. Stage 6 pins the joltc revision to the JoltPhysicsSharp version, builds it in CI, names the library `joltc.a`, and keeps the fix for the doubled `JPH_ContactListener_SetProcs` prototype until it is fixed upstream.

Saves go through `UserData`, which gets a browser store that survives a reload. Stage 8 measures the download and the time to first frame, trims what the linker can drop, adds the hosting guide (itch.io and any static host), and a CI job that builds one example for the web and runs the browser shot test.

---

## Stage 0: checks before the design is fixed

All of this is throwaway code under `spikes/`, on branch `spike/web-cube`, with no tests and no review. Each task ends by writing what it found into a "Stage 0 findings" section at the end of this file.

### Task 0.1: Triangle winding and the y flip

**Files:**
- Modify: `spikes/web-cube/Game.cs`, `spikes/web-cube/wwwroot/main.js`

- [ ] **Step 1:** Draw one cube with `cullMode: "back"` and `frontFace: "ccw"`, using Rendition's projection (M22 negated) and the cube winding Rendition's `Cube.cs` uses.
- [ ] **Step 2:** Run `dotnet run -c Release` and open `http://127.0.0.1:5310/?cubes=1&cull=back`. Expected: outer faces visible. If the inside shows, try `frontFace: "cw"`.
- [ ] **Step 3:** Write down which front face the web backend must set for Rendition's meshes, and whether naga's y flip can be turned off instead.

### Task 0.2: Texture arrays by index in browsers

- [ ] **Step 1:** Search the WebGPU spec, Chrome status and the gpuweb repository for binding arrays (`binding_array`, "bindless") and their shipping state in Chrome, Firefox and Safari.
- [ ] **Step 2:** If any browser ships them, add a `binding_array<texture_2d<f32>>` test to the spike and run it. Expected: either it draws or the feature is absent.
- [ ] **Step 3:** Write down the answer. Absent in any of the three means the browser uses a bind group per material, as the design assumes.

### Task 0.3: The thirty failing shaders, and naga against Tint

**Files:**
- Create: `spikes/web-cube/convert-engine.sh`

- [ ] **Step 1:** Write the script: for every shader in `src/Rendition/Shaders`, compile with `-DSUPER_WEB`, split samplers, convert with naga, and append failures to `obj/engine/errors.txt`.
- [ ] **Step 2:** Try each fix for the `discard` shaders: `--target-env=vulkan1.1`, and `--target-env=vulkan1.3` followed by a spirv-opt pass. Expected: one of them converts all ten.
- [ ] **Step 3:** Convert the same SPIR-V with Tint (`tint --format wgsl`). Record how many of the 84 each tool converts.
- [ ] **Step 4:** Write down which converter stage 2 uses, the `discard` fix, and the shaders still failing with their error.

### Task 0.4: Shaders against WebGPU's limits

**Files:**
- Create: `spikes/web-cube/limits.sh`

- [ ] **Step 1:** Write the script: run `spirv-cross --reflect` over each split SPIR-V file and print, per shader, the highest set number, and the counts of sampled textures, samplers, storage buffers, storage images and colour outputs.
- [ ] **Step 2:** Run it. Expected: a table; rows over 4 groups, 16 textures, 16 samplers, 8 storage buffers or 8 colour targets are the ones to redesign.
- [ ] **Step 3:** List the render target formats in `src/Rendition/FrameTargets.cs` and the other target files, and mark each as renderable, filterable and blendable in WebGPU, with the feature it needs (`float32-filterable`, `rg11b10ufloat-renderable`, `texture-compression-bc`).
- [ ] **Step 4:** Write down every shader and format that needs a change, and whether shared samplers in GLSL are needed.

### Task 0.5: Super without a renderer in the browser

**Files:**
- Create: `spikes/web-headless/WebHeadless.csproj`, `spikes/web-headless/Program.cs`, `spikes/web-headless/wwwroot/index.html`, `spikes/web-headless/wwwroot/main.js`

- [ ] **Step 1:** Make a `Microsoft.NET.Sdk.WebAssembly` project that references `src/Super/Super.csproj`, with `RunAOTCompilation` on.
- [ ] **Step 2:** In `Program.cs`, build a game the way the tests in `Super.Testing` do (no window, no renderer), spawn a few entities, run 600 ticks and print the tick time.
- [ ] **Step 3:** Publish with `dotnet publish -c Release`. Expected: it builds. Record every package or API the build or the trimmer rejects (the GLFW, OpenAL, Vulkan and diagnostics packages are the suspects).
- [ ] **Step 4:** Open it. Expected: 600 ticks logged. Record what throws at run time: `Task.Run`, `Parallel.For`, `Thread`, reflection in `ContentSchema`, dependency injection.
- [ ] **Step 5:** Put a small cooked content folder in the in-memory file system from `main.js` and load one JSON item and one texture through `Content`. Expected: they load with the engine's file code unchanged.
- [ ] **Step 6:** Write down the size of `_framework` raw and with brotli, the tick time against desktop, and each thing that had to change.

### Task 0.6: Jolt in the browser

**Files:**
- Create: `spikes/web-jolt/`

- [ ] **Step 1:** Find which `joltc` revision JoltPhysicsSharp 2.26.0 binds, and whether the project publishes a WebAssembly build.
- [ ] **Step 2:** Build `joltc` as a static library with the Emscripten version .NET's workload uses (3.1.56), single-threaded.
- [ ] **Step 3:** Reference it from a browser project with `NativeFileReference` beside the `JoltPhysicsSharp` package, drop a box onto a plane for 120 steps and print its height. Expected: it comes to rest on the plane.
- [ ] **Step 4:** Write down whether it works, the size it adds, and the step time against desktop. If it can't be linked, write down why and stop: stage 6 then needs its own decision.

### Task 0.7: Correct the plan

- [ ] **Step 1:** Read the findings together and edit "Design" and "Stages" above where an answer changes them.
- [ ] **Step 2:** Show the user the findings and the changes before stage 1a starts.

---

## Stage 1a: the layer core, with the fullscreen pass on it

Branch `feat/gpu-layer-core` from `main`.

### Task 1: Ids, descriptions and the command list

**Files:**
- Create: `src/Rendition/Gpu/Ids.cs`, `src/Rendition/Gpu/Descriptions.cs`, `src/Rendition/Gpu/CommandList.cs`, `src/Rendition/Gpu/GpuFormat.cs`
- Test: `tests/Super.Tests/Gpu/CommandListTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct BufferId(int Value)`, and the same for `ImageId`, `ViewId`, `SamplerId`, `ShaderId`, `PipelineId`, `BindGroupId`. `Value` 0 means none.
  - `enum GpuFormat` with exactly the formats `src/Rendition` uses today, named as WebGPU names them (`Rgba8Unorm`, `Rgba16Float`, `Depth32Float`, `Bc7RgbaUnorm`, …).
  - `enum Command { BeginRenderPass = 1, EndPass, SetPipeline, SetBindGroup, SetVertexBuffer, SetIndexBuffer, SetViewport, SetScissor, SetConstants, Draw, DrawIndexed, BeginComputePass, Dispatch, PushLabel, PopLabel }`
  - `sealed class CommandList` with one method per command (`void SetPipeline(PipelineId pipeline)`, `void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance)`, `void SetConstants(ReadOnlySpan<byte> bytes)`, `void BeginRenderPass(ReadOnlySpan<ColorTarget> colors, in DepthTarget? depth)`, …), `ReadOnlySpan<int> Words`, and `void Reset()`.
  - `readonly record struct ColorTarget(ViewId View, LoadOp Load, Vector4 Clear)` and `readonly record struct DepthTarget(ViewId View, LoadOp Load, float Clear, bool ReadOnly)`.

- [ ] **Step 1: Write the failing tests**
  - `Records_each_command_as_its_number_followed_by_its_arguments`: `SetPipeline(new(7))` then `DrawIndexed(36, 1, 0, 0, 5)` gives words `[3, 7, 11, 36, 1, 0, 0, 5]`.
  - `SetConstants_stores_bytes_padded_to_whole_words`: 6 bytes give `[9, 6, w0, w1]` with the last two bytes of `w1` zero.
  - `Reset_empties_the_list_and_keeps_its_array`: after `Reset`, `Words.Length` is 0 and recording the same commands again allocates nothing (`GC.GetAllocatedBytesForCurrentThread` unchanged).
  - `EndPass_without_a_begin_throws`: `InvalidOperationException` with message `"EndPass was called with no pass begun."`.
- [ ] **Step 2: Run them.** `dotnet test tests/Super.Tests -c Release --filter "FullyQualifiedName~CommandListTests"`. Expected: fail to compile.
- [ ] **Step 3: Implement the files above.** The word array doubles when full.
- [ ] **Step 4: Run them.** Expected: pass. Break the padding and watch the second test fail, then revert.
- [ ] **Step 5: Commit.** `feat: add GPU layer ids and command list`

### Task 2: Shader reflection data

**Files:**
- Modify: `src/Shaders.targets`
- Create: `src/Rendition/Gpu/ShaderLayout.cs`
- Test: `tests/Super.Tests/Gpu/ShaderLayoutTests.cs`

**Interfaces:**
- Produces:
  - A `<name>.layout.json` beside each compiled `<name>.spv`, written at build time by `spirv-cross --reflect` and reduced to `{ "bindings": [{ "name", "group", "binding", "kind", "count" }], "constantsSize": n, "targets": n }`.
  - `sealed class ShaderLayout` with `static ShaderLayout Parse(ReadOnlySpan<byte> json)`, `bool TryFind(string name, out ShaderBinding binding)`, `int ConstantsSize`.
  - `readonly record struct ShaderBinding(int Group, int Binding, BindingKind Kind, int Count)` and `enum BindingKind { UniformBuffer, StorageBuffer, SampledImage, StorageImage, Sampler, ImageWithSampler, Structure }`.

- [ ] **Step 1: Write the failing tests**
  - `Finds_a_binding_by_the_name_the_shader_gives_it`: parsing a hand-written file with `sceneColor` at group 1 binding 2 returns `(1, 2, ImageWithSampler, 1)`.
  - `An_unknown_name_is_not_found`: `TryFind("nope", …)` is false.
  - `The_built_tonemap_shader_has_a_layout_file`: the file for `tonemap.frag` exists in the build output and contains every sampler name declared in `src/Rendition/Shaders/tonemap.frag`.
- [ ] **Step 2: Run them.** Expected: fail.
- [ ] **Step 3: Add the reflection step to `Shaders.targets` and write `ShaderLayout`.** The step is incremental with the same inputs and outputs rule as the SPIR-V step, and loads through `ShaderSources` like the SPIR-V does.
- [ ] **Step 4: Run them.** Expected: pass.
- [ ] **Step 5: Commit.** `feat: write binding layouts for compiled shaders`

### Task 3: The device and its Vulkan backend

**Files:**
- Create: `src/Rendition/Gpu/GpuDevice.cs`, `src/Rendition/Gpu/GpuAbilities.cs`, `src/Rendition/Gpu/Bind.cs`, `src/Rendition/Gpu/Vulkan/VulkanGpu.cs`, `src/Rendition/Gpu/Vulkan/VulkanPlayer.cs`, `src/Rendition/Gpu/Vulkan/VulkanBindGroups.cs`, `src/Rendition/Gpu/Vulkan/IdTable.cs`
- Test: `tests/Super.Tests/Gpu/IdTableTests.cs`, `tests/Rendition.Gpu.Tests/GpuLayerGpuTests.cs`

**Interfaces:**
- Consumes: Task 1's ids and `CommandList`, Task 2's `ShaderLayout`, the existing `VulkanContext`, `PipelineFactory` and `Descriptors`.
- Produces:
  - `abstract class GpuDevice : IDisposable` with `GpuAbilities Abilities`, `BufferId CreateBuffer(in BufferDescription description)`, `void Write(BufferId buffer, int offset, ReadOnlySpan<byte> data)`, `SamplerId CreateSampler(in SamplerDescription description)`, `ShaderId CreateShader(string name)`, `PipelineId CreateRenderPipeline(in RenderPipelineDescription description)`, `BindGroupId CreateBindGroup(PipelineId pipeline, int group, ReadOnlySpan<Bind> binds)`, `void Destroy(…)` for each id, `CommandList BeginCommands()`, `void Run(CommandList commands)`.
  - `readonly struct Bind` with `static Bind Image(string name, ViewId view, SamplerId sampler)`, `static Bind Image(string name, ViewId view)`, `static Bind Buffer(string name, BufferId buffer)`.
  - `sealed class VulkanGpu : GpuDevice` with `VulkanGpu(VulkanContext context)`, `ViewId Adopt(ImageView view)` and `void Play(CommandList commands, CommandBuffer into)`, so passes that have moved and passes that haven't share a frame until stage 1e.
  - `sealed class IdTable<T>` with `int Add(T item)`, `T this[int id]`, `void Remove(int id)`; ids are reused after removal and 0 is never given out.
  - `readonly record struct GpuAbilities(bool RayQueries, bool Multiview, bool TextureArraysByIndex, bool ConservativeRaster, bool BcTextures, bool TimerQueries)`.

- [ ] **Step 1: Write the failing tests**
  - `IdTableTests.Never_hands_out_zero`, `Reuses_an_id_after_it_is_removed`, `Reading_a_removed_id_throws` with message `"Id 3 is not in use."`.
  - `GpuLayerGpuTests.A_fullscreen_draw_through_the_layer_fills_the_target`: create a 4×4 `Rgba8Unorm` target, a pipeline from `fullscreen.vert` and a test fragment shader that writes its constants as colour, record begin pass, set pipeline, `SetConstants` with `(0.25, 0.5, 0.75, 1)`, draw 3, end pass, play it, read the target back. Every pixel is `(64, 128, 191, 255)` within 1.
  - `GpuLayerGpuTests.A_bind_by_a_name_the_shader_lacks_throws` with message `"Shader 'tonemap.frag' has no binding named 'nope'."`.
  - `GpuLayerGpuTests.Validation_reports_nothing_for_a_layer_frame`: the fixture's validation error count is unchanged after the first test's frame.
- [ ] **Step 2: Run them.** Expected: fail.
- [ ] **Step 3: Implement.** `VulkanPlayer` walks `Words` and issues the Vulkan calls; `BeginRenderPass` moves each target to its attachment layout and each image in the bound groups to shader-read, from the layout the backend last left it in. `CreateRenderPipeline` goes through `PipelineFactory`, and set layouts are built from the `ShaderLayout`s through `Descriptors`.
- [ ] **Step 4: Run them, and the whole GPU suite.** Expected: pass.
- [ ] **Step 5: Commit.** `feat: add the GPU layer device with a Vulkan backend`

### Task 4: Move `FullscreenPass` and tonemap onto the layer

**Files:**
- Modify: `src/Rendition/Passes/FullscreenPass.cs`, the tonemap stage's file in `src/Rendition` (find it with a search for `"tonemap.frag"`), `docs/design.md`
- Test: `tests/Rendition.Gpu.Tests/GpuLayerGpuTests.cs`

**Interfaces:**
- Consumes: Task 3's `GpuDevice`, `Bind`, `VulkanGpu.Adopt` and `VulkanGpu.Play`.
- Produces: a second `FullscreenPass` constructor, `FullscreenPass(GpuDevice device, string fragmentShader, ReadOnlySpan<GpuFormat> targetFormats, bool blend = false)`, and `void Record(CommandList commands, ReadOnlySpan<ColorTarget> targets, ReadOnlySpan<BindGroupId> groups, ReadOnlySpan<byte> constants)`. The Vulkan constructor and `Record` stay until stage 1b has moved their last caller.

- [ ] **Step 1: Take the before shots.** Shapes, Hamlet, Courtyard and Reef with `--screenshot --validate --seed 1 --ticks 120 --size 1280x720` on `main`, kept outside the repository.
- [ ] **Step 2: Write the failing test.** `Tonemap_through_the_layer_matches_the_picture_from_before`: the fixture's standard scene captured with the layer path equals the capture with the old path, byte for byte.
- [ ] **Step 3: Move tonemap.** Its set becomes a bind group made with `Bind.Image` by name; its record call writes to the frame's `CommandList`, played into the frame's command buffer where the old call was.
- [ ] **Step 4: Run the test, the GPU suite and the four shots.** Expected: tests pass, shots are identical to step 1, validation messages unchanged.
- [ ] **Step 5: Measure.** Reef and Hamlet CPU frame time before and after, as `docs/profiling.md` describes. Expected: within noise. Write the numbers in the pull request.
- [ ] **Step 6: Update `docs/design.md`** with a "GPU layer" section: what it is, why commands are recorded, named bindings, and that passes move over stage by stage. Run `dotnet roe`.
- [ ] **Step 7: Commit.** `refactor: draw the tonemap pass through the GPU layer`

### Task 5: Pull request

- [ ] **Step 1:** Run everything CI runs with `--filter "Needs!=GPU"`, then the GPU suite.
- [ ] **Step 2:** Open the pull request: what the layer is and why, how it was tested, the frame time numbers, and that only tonemap uses it so far. Nothing on screen changes, so no pictures.
- [ ] **Step 3:** One review of the whole branch, fixes, then write the task-level plan for stage 1b.

---

## Stage 0 findings

Measured on 2026-11-27 in Chrome on an NVIDIA RTX 40-series (Lovelace) desktop, with the throwaway code in `spikes/web-cube`, `spikes/web-headless` and `spikes/web-jolt`.

### 0.1 Winding

Chrome treats a triangle that turns counter-clockwise on screen as front-facing with `frontFace: "ccw"` (`spikes/web-cube/wwwroot/winding.html`). naga negates `position.y` at the end of every vertex shader, which undoes the flipped y of Vulkan's clip space, and Vulkan's own winding rule already counts on-screen counter-clockwise as front. So the web backend sets the same front face Rendition sets on Vulkan and keeps naga's y flip. Stage 4 confirms it with a culled mesh in a shot.

### 0.2 Texture arrays by index, and push constants

Binding arrays (bindless) are only behind Chrome's "Unsafe WebGPU Support" flag ([toji.dev, October 2026](https://toji.dev/2026/10/06/webgpu-bindless.html)) and absent from Firefox and Safari. The browser uses a bind group per material, as the design assumed.

Immediates, WebGPU's push constants, shipped in Chrome 149–150 (`immediate_address_space`, `maxImmediateSize` 64, `setImmediates`; [Chrome blog](https://developer.chrome.com/blog/new-in-webgpu-149-150)). Firefox and Safari don't have them yet. 54 shaders use push constants and 11 of those pass 64 bytes (the grass shaders use 96), so the web sends all constants through the uniform ring. Immediates become an optimisation for later, for blocks of 64 bytes or less.

### 0.3 Converting the shaders

There are 82 compiled shaders, not 84. Compiling the web variant with `--target-env=vulkan1.1` fixes every `discard` shader: `discard` stays a kill and is not compiled as demote. With that and the source fixes below, naga converts 70 of 82. The 12 that don't convert:

- Five ray-query shaders, which aren't built for the web.
- `decal.vert` and `decal.frag` pass a `mat4` between stages. WGSL can't, so it is passed as four `vec4` columns.
- `probe_capture.vert` uses multiview.
- `lighting.frag`, `mirror.frag` and `reflection_capture.frag` index `textures[]`, the global material set. They need the per-material path.
- `probe_fold.comp` fails only with `-O`, from a naga structuring bug. Built without `-O`, it converts.

The source fixes, each harmless on Vulkan, so they go into the shared GLSL:

- `writeonly` storage buffers declared as plain `buffer`.
- `isnan` and `isinf` replaced by helpers that test bits (`web_float.glsl` in the spike).
- `findLSB(bits)` on a `uint` made `findLSB(int(bits))` in `reflection_probes.glsl`, because the uint result added to an int fails in naga.
- The integer `%` in `cloud_noise.comp` written out as `r = a - b * (a / b)` plus a wrap for negatives. naga lowers signed modulo through a float `floor`, which is a type error.

Then Chrome compiles the WGSL. 53 of 71 compiled as naga wrote them. The rest fail for four reasons:

- Seven sample a texture in control flow WGSL can't prove uniform (`ao`, `dof_blur`, `grid_overlay`, `probe_debug`, `reflections`, `sss_blur`, `taa`). Putting `diagnostic(off, derivative_uniformity);` at the top of each file fixes all seven. Vulkan has always allowed this, so the build adds the line to every fragment shader.
- Seven are the shaders that use the material set, which naga writes as `enable wgpu_binding_array`, and no browser has that. They are `finish`, `gbuffer`, `grass_ground`, `overlay`, `probe_capture.frag`, `shadow` and `transparent`. The per-material path fixes them.
- `paint_filter.comp` and `probe_blend.comp` call `workgroupBarrier` in flow WGSL calls non-uniform, with or without `-O`. naga copies built-ins such as `gl_WorkGroupID` into `var<private>` globals, and WGSL's analysis treats a private variable as non-uniform. Stage 2 picks one fix: rewrite those reads in the WGSL to use the entry point's parameters, restructure the two shaders so the barrier sits outside the branch, or try Tint. Tint isn't installed and was not tried.
- `lens_flare_visibility` declares an array in `var<immediate>`, which is not allowed. This goes away when constants move to the uniform ring.

naga stays the converter. With the fixes above, everything outside ray tracing, multiview and the material set compiles in Chrome.

### 0.4 Limits and formats

`spikes/web-cube/limits.js` counts what each split shader binds. 14 of 82 go over a WebGPU default limit:

- `lighting.frag` binds 24 textures and 24 samplers once its combined samplers are split. That passes 16 samplers, and 16 textures by default, so it needs separate textures and a few shared samplers in GLSL.
- `bake_filter` and `bake_trace` go over on storage images, but they are desktop-only.
- The other 11 go over only on push-constant bytes, which the uniform ring removes.

16 shaders use unsized texture arrays, which are the material set again.

This adapter offers more than the defaults: 48 sampled textures, 16 samplers, 16 storage buffers and 8 storage textures a stage, 4 bind groups, 12 uniform buffers, 8 colour targets, 128 colour-target bytes a sample, 2048 array layers, 64 immediate bytes and 28 values between stages. It offers these features: `float32-filterable`, `float32-blendable`, `rg11b10ufloat-renderable`, `texture-compression-bc`, `texture-formats-tier1`, `texture-formats-tier2`, `timestamp-query`, `shader-f16`, `subgroups`, `dual-source-blending` and `clip-distances`.

The render targets are mostly `rgba16float`, `rgba32float`, `rgba8unorm`, `rg16float`, `rg32float`, `r8unorm`, `r32uint`, `rgba8uint`, `depth32float` and `rg11b10ufloat`. Their sampled textures add BC4 to BC7. The web device requires `float32-filterable`, `rg11b10ufloat-renderable` and `texture-compression-bc`. It also requires `texture-formats-tier1`, for `r8unorm` and `rg16float` as storage, and `texture-formats-tier2`, for read-write `rgba16float` and `rgba32float`. When one is missing, the page says which. Three-channel 32-bit formats exist only as vertex formats.

### 0.5 Super without a renderer

`spikes/web-headless` runs a `HeadlessGame` with 10,000 entities moved by a query, in the browser.

| | Interpreter (Debug) | Ahead of time (Release) |
|---|---|---|
| Game created | 307 ms | 168 ms |
| One tick, 10,000 entities | 23.1 ms | 0.365 ms |

Both give the same checksum. The ahead-of-time publish takes about 4 minutes. `_framework` is 22.4 MB raw and 5.16 MB with brotli. `dotnet.native.wasm` is 14.2 MB of it, `Super.wasm` 2.56 MB and `System.Private.CoreLib` 1.59 MB.

Super compiles for `browser-wasm` as it is. No package was rejected. The trimmer warns three times:

- IL2091 at `HostServices.cs:70`: `TGame` needs `[DynamicallyAccessedMembers(PublicConstructors)]`. Without it the trimmer removes the game's constructor, and dependency injection fails with `NoConstructorMatch` at startup. The spike works around it with `TrimmerRootAssembly`; stage 3 adds the attribute.
- IL2026 at `DevSettings.cs:36` and `ContentReader.cs:31`: JSON by reflection. Stage 3 moves both to source-generated `JsonSerializerContext`s, or roots the types they read.

The headless game never touched `Task.Run`, threads or the window, so those are untested until stage 3. Loading content from the in-memory file system (step 5) and a desktop tick time for comparison were not done; stage 3 measures both.

### 0.6 Jolt

It works. `spikes/web-jolt/app` drops a 1 m box from 3 m onto a static ground box through Super.Physics, the way the physics tests do, and after 120 ticks it rests at 0.480 in Chrome and at 0.480 on desktop. A tick takes 0.160 ms in the browser and 0.146 ms on desktop. Jolt's job system ran on the one thread without changes. Jolt adds about 0.5 MB to the brotli download (5.64 MB against 5.16 MB), and `dotnet.native.wasm` grows from 14.7 MB to 15.9 MB.

JoltPhysicsSharp 2.26.0 depends on `JoltPhysics.Native` 1.3.0, which ships no `browser-wasm` build, so `joltc` is built from source. `amerkoleci/joltc` has no tags, and its head removed `JPH_Init` on 2026-11-27 (6ffc8da), so the head no longer matches 2.26.0. The spike builds b6057a0 (2026-11-19, the day before 2.26.0 came out), which fetches JoltPhysics v5.7.0. `spikes/web-jolt/build-joltc.ps1` builds it with the Emscripten that the .NET workload ships (3.1.56), the CMake and Ninja from Visual Studio, and wasm SIMD. The results are `libjoltc.a` (0.9 MB) and `libJolt.a` (7.3 MB).

Linking needs three things, all in `spikes/web-jolt/app/WebJolt.csproj`:

- `WasmBuildNative` and a `NativeFileReference` to both libraries.
- `libjoltc.a` copied as `joltc.a`. The generated P/Invoke table takes its module name from the file name, and JoltPhysicsSharp imports `joltc`. With the `lib` prefix every call fails with `EntryPointNotFoundException`.
- A target after `_GenerateManagedToNative` that removes one line from the generated `pinvoke-table.h`. JoltPhysicsSharp declares `JPH_ContactListener_SetProcs` twice, once returning `void` and once returning a pointer, and the two C prototypes don't compile. This is a bug in the bindings and worth reporting upstream; until then stage 6 keeps the target.

Stage 6 pins the joltc revision to the one that matches the JoltPhysicsSharp version in `Super.Physics.csproj`, and builds it in CI instead of committing the libraries.
