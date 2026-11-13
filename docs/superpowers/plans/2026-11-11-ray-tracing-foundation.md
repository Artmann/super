# Ray tracing foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Vulkan ray tracing layer in Rendition (device features, acceleration structures built on first use, hit data that reads the existing material table) proven by a `--debug-view trace` view, with nothing changing for games that never trace or GPUs that can't.

**Architecture:** `VulkanContext` turns on the ray query extensions when the GPU has them and reports `CanTrace`. A new `RayTracing` class in `src/Rendition/Vulkan` owns bottom-level structures (one per mesh, one geometry per part, built lazily), a top-level structure built from a list of instances, and a geometry table that gives shaders the device addresses of each part's vertices and indices and its material. The selection of which draws become instances is a pure function so it can be unit tested. A compute shader traces primary rays for the debug view.

**Tech Stack:** C# (.NET 10), Silk.NET.Vulkan 2.23 with `Silk.NET.Vulkan.Extensions.KHR` (`KhrAccelerationStructure`), GLSL compiled by glslc for Vulkan 1.3 (`GL_EXT_ray_query`, `GL_EXT_buffer_reference2`, `GL_EXT_buffer_reference_uvec2`), xunit 2.9.

**Spec:** `docs/superpowers/specs/2026-11-11-lighting-design.md`, sections "Who can trace" and "Ray tracing foundation". Roadmap: `docs/superpowers/plans/2026-11-11-lighting-roadmap.md`.

## Global Constraints

- Everything a player sees must work without ray tracing; tracing is an upgrade, never a requirement.
- Extensions: `VK_KHR_acceleration_structure`, `VK_KHR_ray_query`, `VK_KHR_deferred_host_operations`, and buffer device address (Vulkan 1.2 core feature), enabled only when all are present.
- `Renderer.CanTrace` says whether they are.
- Bottom-level structures are built on first use by a tracing feature, not at load, so games that never trace pay nothing.
- One geometry per part. Cut-out materials' geometries are not opaque, so a ray query can test alpha on candidate hits. See-through (transparent) geometries are marked so rays can later pass through with a tint.
- A hit uses the material's own colour textures and values; custom surface shaders, world variation, wear and dirt masks and puddles are skipped.
- `--debug-view trace` traces primary rays from the camera and shows the hit's albedo.
- Vulkan 1.3 is the floor; glslc targets `vulkan1.3`.
- Code style: braces on every control statement. Conventional commits, no Co-Authored-By lines, no Claude as author.
- Docs change in the same commit as the code: `command-line.md` for the options, `design.md` for the internals, `rendering.md` for `CanTrace`.

## Review Focus

1. A GPU without ray tracing, or `--no-trace`: device creation, every existing example and `--validate` must behave exactly as before, and `--debug-view trace` must fail at start with a sentence saying why. Test in Task 1 (support check) and Task 6 (argument errors).
2. Memory: every device-local block must be allocated with the device-address flag once buffer device address is on, including blocks bigger than `GpuMemory.BlockSize`, or validation fires on the first vertex buffer address taken. Test in Task 2.
3. A mesh destroyed after its bottom-level structure was built (content reload, a generator dropping a chunk): the structure must be freed with it and never used again. Test in Task 3.
4. Draws the trace must leave out: ghosts, skinned draws (the structure is in bind pose), and swimming draws, plus a frame with zero instances, which must still build a valid (empty) top-level structure. Test in Task 4.
5. The System.Numerics row-vector matrix versus Vulkan's 3×4 row-major column-vector transform: a transposed transform puts every instance in the wrong place while looking plausible for symmetric scenes. Test in Task 4.

---

### Task 1: Detect and enable ray tracing on the device

**Files:**
- Create: `src/Rendition/Vulkan/RayTracingSupport.cs`
- Modify: `src/Rendition/Vulkan/VulkanContext.cs` (`CreateDevice`, new `CanTrace` property)
- Modify: `src/Rendition/Types.cs` (`RendererOptions`: add `bool DisableRayTracing { get; init; }`)
- Modify: `src/Rendition/Renderer.cs` (public `bool CanTrace => _context.CanTrace`)
- Modify: `src/Super/GameArguments.cs`, `src/Super/GameOptions.cs`, `src/Super/Rendering/GameWindow.cs` (`--no-trace` sets `DisableRayTracing`)
- Test: `tests/Super.Tests/RayTracingSupportTests.cs`, `tests/Super.Tests/GameArgumentsTests.cs`
- Docs: `docs/command-line.md` (`--no-trace`), `docs/rendering.md` (a short "Ray tracing" note under Graphics settings: what `CanTrace` means and that nothing requires it)

**Interfaces:**
- Produces: `internal static class RayTracingSupport` with `public static readonly string[] Extensions` (the three extension names, from Silk.NET's `KhrAccelerationStructure.ExtensionName`, `KhrRayQuery.ExtensionName`, `KhrDeferredHostOperations.ExtensionName`) and `public static string? Missing(IReadOnlySet<string> deviceExtensions, bool bufferDeviceAddress, bool accelerationStructure, bool rayQuery, bool disabled)`: null when tracing can be on, otherwise a short reason naming what's missing ("turned off with --no-trace", "no VK_KHR_ray_query", "no bufferDeviceAddress").
- Produces: `VulkanContext.CanTrace` (bool), `VulkanContext.NoTraceReason` (string?), `Renderer.CanTrace`.
- Produces: `RendererOptions.DisableRayTracing`, `GameArguments.NoTrace` (bool, default false).

`--no-trace` is my addition to the spec: about a fifth of players can't trace, and developers need to see that path on their own machine. It's a developer option, not a `Graphics` switch.

- [ ] **Step 1: Write the failing tests**

```csharp
public class RayTracingSupportTests
{
    private static readonly HashSet<string> All = [.. RayTracingSupport.Extensions];

    [Fact]
    public void Traces_when_every_extension_and_feature_is_there() =>
        Assert.Null(RayTracingSupport.Missing(All, bufferDeviceAddress: true, accelerationStructure: true, rayQuery: true, disabled: false));

    [Fact]
    public void Names_the_missing_extension()
    {
        var withoutRayQuery = All.Where(name => name != "VK_KHR_ray_query").ToHashSet();
        Assert.Equal("no VK_KHR_ray_query",
            RayTracingSupport.Missing(withoutRayQuery, true, true, true, false));
    }

    [Fact]
    public void Names_a_missing_feature_even_with_the_extensions() =>
        Assert.Equal("no bufferDeviceAddress", RayTracingSupport.Missing(All, false, true, true, false));

    [Fact]
    public void Says_when_turned_off() =>
        Assert.Equal("turned off with --no-trace", RayTracingSupport.Missing(All, true, true, true, disabled: true));
}
```

In `GameArgumentsTests`: `Parse(["--no-trace"]).NoTrace` is true, and `Parse([])`'s is false (update the existing all-defaults equality test, which compares whole records).

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter "FullyQualifiedName~RayTracingSupportTests|FullyQualifiedName~GameArgumentsTests"`
Expected: build fails, `RayTracingSupport` and `NoTrace` don't exist.

- [ ] **Step 3: Implement**

`RayTracingSupport.Missing` checks `disabled` first, then each extension in `Extensions` order, then the features. In `CreateDevice`, query `PhysicalDeviceAccelerationStructureFeaturesKHR` and `PhysicalDeviceRayQueryFeaturesKHR` on the `supportedAll` chain, read `supported12.BufferDeviceAddress`, and when `Missing` returns null add the three extensions, set `features12.BufferDeviceAddress = true`, and chain `AccelerationStructure = true` and `RayQuery = true` feature structs into `features13`'s chain. Otherwise leave the device exactly as it is now. Store the reason in `NoTraceReason`.

- [ ] **Step 4: Run the tests and make sure they pass**

Run: `dotnet test tests/Super.Tests --filter "FullyQualifiedName~RayTracingSupportTests|FullyQualifiedName~GameArgumentsTests"`
Expected: PASS. Then break `Missing` (return null always), watch three tests fail, revert.

- [ ] **Step 5: Check the device on this machine**

Run: `dotnet run --project examples/Courtyard -- --screenshot shots/trace-on.png --validate --seed 1 --ticks 120 --size 1280x720`, then the same with `--no-trace` to `shots/trace-off.png`.
Expected: both exit 0 with 0 validation messages, and the two pictures are identical. (Nothing traces yet.)

- [ ] **Step 6: Commit**

```bash
git add src/Rendition/Vulkan/RayTracingSupport.cs src/Rendition/Vulkan/VulkanContext.cs src/Rendition/Types.cs src/Rendition/Renderer.cs src/Super/GameArguments.cs src/Super/GameOptions.cs src/Super/Rendering/GameWindow.cs tests/Super.Tests/RayTracingSupportTests.cs tests/Super.Tests/GameArgumentsTests.cs docs/command-line.md docs/rendering.md
git commit -m "feat: turn on ray queries where the GPU has them, with --no-trace to see the fallback"
```

### Task 2: Device addresses for buffers

**Files:**
- Modify: `src/Rendition/Vulkan/GpuMemory.cs` (`AllocateRaw` and the dedicated path for allocations over `BlockSize`)
- Modify: `src/Rendition/Vulkan/GpuBuffer.cs` (new `ulong Address` when the buffer has `ShaderDeviceAddressBit`)
- Modify: `src/Rendition/Renderer.cs:484-503` (`CreateMesh`: mesh buffers gain `ShaderDeviceAddressBit | AccelerationStructureBuildInputReadOnlyBitKhr` when `CanTrace`)
- Test: `tests/Rendition.Gpu.Tests/` (new project, below)

**Interfaces:**
- Consumes: `VulkanContext.CanTrace`.
- Produces: `GpuBuffer.Address` (0 when the buffer has no device address), `internal static BufferUsageFlags GpuBuffer.TraceInput` (the two flags above, or none, by `CanTrace`).
- Produces: `tests/Rendition.Gpu.Tests` with `GpuFixture : IDisposable` that opens a hidden 256×256 window the way `GameWindow` does, creates a `Renderer` with `EnableValidation = true`, and exposes `Renderer` and `ValidationErrors`. Every GPU test class is `[Collection(GpuCollection.Name)]` (with `DisableParallelization = true`) and `[Trait("Speed", "Slow")]`. A test that needs tracing starts with `Assert.True(fixture.Renderer.CanTrace, "This test needs a GPU that can trace rays.")`, so on a machine without one it fails with that message rather than passing; CI without a GPU filters the `Speed=Slow` trait out as it does today.

- [ ] **Step 1: Write the failing test**

```csharp
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class DeviceAddressTests(GpuFixture gpu)
{
    [Fact]
    public void A_mesh_built_for_tracing_has_addresses_and_no_validation_errors()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        var (vertices, indices) = TestMeshes.Quad();
        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        var mesh = gpu.Renderer.MeshFor(handle);
        Assert.NotEqual(0ul, mesh.Vertices.Address);
        Assert.NotEqual(0ul, mesh.Indices.Address);
        Assert.Equal(0, gpu.ValidationErrors);
    }

    [Fact]
    public void A_buffer_bigger_than_a_block_still_has_an_address()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        using var big = new GpuBuffer(gpu.Context, GpuMemory.BlockSize + 1024,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        Assert.NotEqual(0ul, big.Address);
        Assert.Equal(0, gpu.ValidationErrors);
    }
}
```

`Renderer.MeshFor(MeshHandle)` and `GpuFixture.Context` are `internal`; add `InternalsVisibleTo Include="Rendition.Gpu.Tests"` to `Rendition.csproj`.

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests/Rendition.Gpu.Tests`
Expected: build fails on `Address`.

- [ ] **Step 3: Implement**

When `context.CanTrace`, chain `MemoryAllocateFlagsInfo { Flags = MemoryAllocateFlags.DeviceAddressBit }` into every `MemoryAllocateInfo` for device-local and host-visible memory alike (both block and dedicated paths). `GpuBuffer` reads `GetBufferDeviceAddress` once after binding when its usage has `ShaderDeviceAddressBit`.

- [ ] **Step 4: Run it and make sure it passes**

Run: `dotnet test tests/Rendition.Gpu.Tests`
Expected: PASS with 0 validation errors. Remove the allocate-flags chaining, watch the validation count fail the test, revert.

- [ ] **Step 5: Commit**

```bash
git add src/Rendition tests/Rendition.Gpu.Tests Super.slnx
git commit -m "feat: give mesh buffers device addresses where the GPU can trace"
```

### Task 3: Bottom-level structures, built on first use

**Files:**
- Create: `src/Rendition/Vulkan/RayTracing.cs`
- Modify: `src/Rendition/Renderer.cs` (owns a `RayTracing?`, created only when `CanTrace`; `DestroyMesh` tells it)
- Test: `tests/Rendition.Gpu.Tests/BottomLevelTests.cs`

**Interfaces:**
- Consumes: `GpuBuffer.Address`, `Mesh` (`Vertices`, `Indices`), the mesh's parts as `(uint FirstIndex, uint IndexCount, MaterialHandle Material)` from the draw items `CreateModel` makes, `ResourceTable.HasCutouts`, `ResourceTable.GetFlags` (for `MaterialFlags.Transparent`).
- Produces: `internal sealed class RayTracing : IDisposable` with `public ulong BottomLevel(CommandBuffer cmd, MeshHandle mesh, ReadOnlySpan<TracePart> parts)` returning the structure's device address, building it into `cmd` on first call and returning the cached one after; `public void Forget(MeshHandle mesh)` freeing it; `public int BottomLevelCount { get; }` for tests.
- Produces: `internal RayTracing? Renderer.Tracing` (null when the GPU can't trace).
- Produces: `public readonly record struct TracePart(uint FirstIndex, uint IndexCount, MaterialHandle Material)` in `src/Rendition/Vulkan/TraceScene.cs` (Task 4's file; create it here with only this record).

A mesh's bottom-level structure is keyed by mesh and its part list together, since the same mesh can be drawn with different part ranges. Geometries are `Opaque` unless the material has cut-outs or is transparent. Vertex format `R32G32B32Sfloat` at `Vertex.Position`'s offset, stride `sizeof(Vertex)`, index type `Uint32`. Build with `PreferFastTrace`; compaction is left to the builder (the spec leaves it open), off for now. A scratch buffer is reused and grown as needed. Builds wait for the structure's own barrier (`AccelerationStructureBuildBitKhr` to `AccelerationStructureReadBitKhr`) before anything reads them.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Builds_once_and_reuses_it()
{
    Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
    var tracing = gpu.Renderer.Tracing!;
    var mesh = gpu.Renderer.CreateMesh(TestMeshes.Quad().Vertices, TestMeshes.Quad().Indices);
    TracePart[] parts = [new(0, 6, gpu.Matte)];
    ulong first = 0, second = 0;
    gpu.Context.SubmitAndWait(cmd => first = tracing.BottomLevel(cmd, mesh, parts));
    gpu.Context.SubmitAndWait(cmd => second = tracing.BottomLevel(cmd, mesh, parts));
    Assert.NotEqual(0ul, first);
    Assert.Equal(first, second);
    Assert.Equal(1, tracing.BottomLevelCount);
    Assert.Equal(0, gpu.ValidationErrors);
}

[Fact]
public void Destroying_the_mesh_frees_its_structure()
{
    Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
    var tracing = gpu.Renderer.Tracing!;
    int before = tracing.BottomLevelCount;
    var mesh = gpu.Renderer.CreateMesh(TestMeshes.Quad().Vertices, TestMeshes.Quad().Indices);
    gpu.Context.SubmitAndWait(cmd => tracing.BottomLevel(cmd, mesh, [new(0, 6, gpu.Matte)]));
    gpu.Renderer.DestroyMesh(mesh);
    Assert.Equal(before, tracing.BottomLevelCount);
    Assert.Equal(0, gpu.ValidationErrors);
}

[Fact]
public void A_renderer_that_never_traces_builds_nothing()
{
    using var fresh = new GpuFixture();
    fresh.Renderer.CreateMesh(TestMeshes.Quad().Vertices, TestMeshes.Quad().Indices);
    fresh.RenderOneFrame();
    Assert.Equal(0, fresh.Renderer.Tracing?.BottomLevelCount ?? 0);
}
```

`GpuFixture.Matte` is a plain material the fixture creates; `RenderOneFrame` renders one empty frame. `DestroyMesh` must wait for the GPU as it does today before freeing.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~BottomLevelTests`
Expected: build fails on `Tracing`.

- [ ] **Step 3: Implement `RayTracing.BottomLevel` and `Forget` as above**

- [ ] **Step 4: Run them and make sure they pass**

Run: `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~BottomLevelTests`
Expected: PASS. Comment out the call to `Forget` in `DestroyMesh`, watch the second test fail, revert.

- [ ] **Step 5: Commit**

```bash
git add src/Rendition tests/Rendition.Gpu.Tests
git commit -m "feat: build a mesh's acceleration structure the first time something traces it"
```

### Task 4: Choosing what to trace, and the top-level structure

**Files:**
- Modify: `src/Rendition/Vulkan/TraceScene.cs`
- Modify: `src/Rendition/Vulkan/RayTracing.cs` (`TopLevel`)
- Test: `tests/Super.Tests/TraceSceneTests.cs`, `tests/Rendition.Gpu.Tests/TopLevelTests.cs`

**Interfaces:**
- Consumes: `DrawItem`, `TracePart`, `RayTracing.BottomLevel`.
- Produces: `[Flags] public enum TraceFlags : byte { None = 0, Cutout = 1, SeeThrough = 2 }`.
- Produces: `public readonly record struct TraceInstance(Matrix4x4 Transform, MeshHandle Mesh, TracePart[] Parts, TraceFlags Flags)`.
- Produces: `internal static class TraceScene` with
  - `public static List<TraceInstance> Collect(ReadOnlySpan<DrawItem> draws, Func<MaterialHandle, TraceFlags> flagsOf)`: one instance per distinct (mesh, transform), its parts being the draws' (`FirstIndex`, `IndexCount`, `Material`) in draw order; leaves out draws with a `Ghost`, a non-empty `Bones`, or a non-zero `Swim.Amplitude`; an instance's flags are the union of its parts'.
  - `public static TransformMatrixKHR ToVulkan(in Matrix4x4 transform)`: System.Numerics multiplies row vectors (`v * M`, translation in `M41..M43`); Vulkan's 3×4 is row-major acting on column vectors, so row r of the result is column r of `transform`.
- Produces: `RayTracing.TopLevel(CommandBuffer cmd, IReadOnlyList<TraceInstance> instances) -> TopLevelStructure` where `TopLevelStructure` holds the `AccelerationStructureKHR` handle, its device address, and the geometry table (Task 5). Each instance's `InstanceCustomIndex` is the index of its first geometry in that table, so a hit's geometry is `gl_InstanceCustomIndexEXT + gl_GeometryIndexEXT`. Instance mask `0xFF`; `SeeThrough` instances additionally set bit `0x02` of the mask clear so later features can skip them with a cull mask. Rebuilt whole each call; refitting is left until the live probes need it.

- [ ] **Step 1: Write the failing tests**

```csharp
public class TraceSceneTests
{
    private static readonly MeshHandle Rock = new(3);
    private static readonly MaterialHandle Stone = new(1), Leaves = new(2);

    private static TraceFlags Flags(MaterialHandle material) => material == Leaves ? TraceFlags.Cutout : TraceFlags.None;

    [Fact]
    public void Parts_of_one_placed_mesh_become_one_instance()
    {
        var at = Matrix4x4.CreateTranslation(1, 2, 3);
        DrawItem[] draws =
        [
            new() { Mesh = Rock, FirstIndex = 0, IndexCount = 30, Material = Stone, Transform = at },
            new() { Mesh = Rock, FirstIndex = 30, IndexCount = 12, Material = Leaves, Transform = at },
        ];
        var instance = Assert.Single(TraceScene.Collect(draws, Flags));
        Assert.Equal([new TracePart(0, 30, Stone), new TracePart(30, 12, Leaves)], instance.Parts);
        Assert.Equal(TraceFlags.Cutout, instance.Flags);
    }

    [Fact]
    public void Leaves_out_ghosts_skinned_and_swimming_draws()
    {
        DrawItem[] draws =
        [
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Ghost = new Ghost(Vector3.One) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Bones = new BoneRange(0, 20) },
            new() { Mesh = Rock, IndexCount = 3, Material = Stone, Transform = Matrix4x4.Identity, Swim = new SwimMotion(0.1f, 1f, 0f) },
        ];
        Assert.Empty(TraceScene.Collect(draws, Flags));
    }

    [Fact]
    public void Vulkan_transform_moves_a_point_where_numerics_does()
    {
        var transform = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(5, -1, 3);
        var point = new Vector3(0.3f, 1.1f, -2f);
        var expected = Vector3.Transform(point, transform);
        var m = TraceScene.ToVulkan(transform);
        var actual = new Vector3(
            m[0, 0] * point.X + m[0, 1] * point.Y + m[0, 2] * point.Z + m[0, 3],
            m[1, 0] * point.X + m[1, 1] * point.Y + m[1, 2] * point.Z + m[1, 3],
            m[2, 0] * point.X + m[2, 1] * point.Y + m[2, 2] * point.Z + m[2, 3]);
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
```

(`m[r, c]` is a small indexer helper in the test over `TransformMatrixKHR.Matrix`; write it against however Silk.NET exposes the 12 floats.)

GPU test: `TopLevel` over zero instances and over two instances of one mesh each returns a non-zero address, builds one bottom-level structure, and leaves 0 validation errors.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~TraceSceneTests` and `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~TopLevelTests`
Expected: build fails, `TraceScene.Collect` doesn't exist.

- [ ] **Step 3: Implement `Collect`, `ToVulkan` and `TopLevel`**

- [ ] **Step 4: Run them and make sure they pass**

Expected: PASS. Transpose `ToVulkan` on purpose, watch the transform test fail, revert.

- [ ] **Step 5: Commit**

```bash
git add src/Rendition/Vulkan tests/Super.Tests/TraceSceneTests.cs tests/Rendition.Gpu.Tests/TopLevelTests.cs
git commit -m "feat: build a top-level acceleration structure from a frame's draws"
```

### Task 5: Hit data and the trace debug view

**Files:**
- Create: `src/Rendition/Shaders/trace.glsl` (ray query helpers and hit shading inputs)
- Create: `src/Rendition/Shaders/trace_debug.comp`
- Create: `src/Rendition/Passes/TraceDebugPass.cs`
- Modify: `src/Rendition/Vulkan/RayTracing.cs` (geometry table buffer)
- Modify: `src/Rendition/Types.cs` (`public enum DebugView { None, Trace }`; `FrameView.DebugView` field)
- Modify: `src/Rendition/Renderer.cs` (when `view.DebugView == DebugView.Trace`, collect, build, and run the pass instead of the scene's lighting)
- Test: `tests/Super.Tests/TraceGeometryLayoutTests.cs`, `tests/Rendition.Gpu.Tests/TraceDebugTests.cs`

**Interfaces:**
- Consumes: `TraceScene.Collect`, `RayTracing.TopLevel`, `ResourceTable`'s material buffer and bindless texture array (the same set and bindings `material.glsl` uses).
- Produces: `[StructLayout(LayoutKind.Sequential)] internal struct GpuTraceGeometry { public ulong Vertices; public ulong Indices; public uint FirstIndex; public uint Material; }`, 24 bytes, std430, one per part in instance order. In GLSL, addresses are read as `uvec2` and turned into `buffer_reference` pointers with `GL_EXT_buffer_reference_uvec2`, so the shader needs no `shaderInt64`.
- Produces in `trace.glsl`: `struct TraceHit { vec3 position; vec3 normal; vec2 uv; uint material; bool frontFace; }`, `bool traceClosest(accelerationStructureEXT scene, vec3 origin, vec3 direction, float maxDistance, out TraceHit hit)` (runs the query; for non-opaque candidates samples the material's base colour alpha at the interpolated UV and commits when it passes the material's cut-out threshold), and `vec3 hitAlbedo(TraceHit hit)` (base colour factor × base colour texture, no surface shader, no tint, no variation, wear, dirt or puddles). Later plans include this file; they don't copy it.
- Produces: `FrameView.DebugView` (default `None`).

The pass writes into the scene's lit HDR colour target, in linear, at full brightness ×1 so auto exposure can't change it, and the frame skips the lighting pass when it's on. A miss writes `(0.25, 0.35, 0.55)` so open sky reads as sky. Throws `InvalidOperationException` with `NoTraceReason` in the message when `DebugView.Trace` is asked for and `CanTrace` is false.

- [ ] **Step 1: Write the failing tests**

```csharp
public class TraceGeometryLayoutTests
{
    [Fact]
    public void Matches_the_shaders_std430_layout()
    {
        Assert.Equal(24, Unsafe.SizeOf<GpuTraceGeometry>());
        Assert.Equal(16, (int)Marshal.OffsetOf<GpuTraceGeometry>(nameof(GpuTraceGeometry.FirstIndex)));
    }
}
```

GPU test `Shows_the_albedo_of_what_the_camera_sees`: a red quad (base colour `(1, 0, 0)`) filling the middle of a 256×256 view and nothing else, rendered with `DebugView.Trace` through `Renderer.Capture`; the centre pixel is red within 2/255 after the frame's tone mapping (sRGB 255, 0, 0 within tolerance) and a corner pixel is the miss colour's sRGB value. Then a cut-out quad whose texture is transparent on its left half: a left-of-centre pixel shows what's behind it, a right-of-centre pixel shows its colour. 0 validation errors.

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~TraceGeometryLayoutTests` and `dotnet test tests/Rendition.Gpu.Tests --filter FullyQualifiedName~TraceDebugTests`
Expected: build fails.

- [ ] **Step 3: Implement the geometry table, `trace.glsl`, `trace_debug.comp` and `TraceDebugPass`**

- [ ] **Step 4: Run them and make sure they pass**

Expected: PASS with 0 validation errors. Make every geometry opaque on purpose, watch the cut-out assertion fail, revert.

- [ ] **Step 5: Commit**

```bash
git add src/Rendition tests/Super.Tests/TraceGeometryLayoutTests.cs tests/Rendition.Gpu.Tests/TraceDebugTests.cs
git commit -m "feat: trace primary rays to show what the acceleration structures hold"
```

### Task 6: `--debug-view trace` and the docs

**Files:**
- Modify: `src/Super/GameArguments.cs` (`--debug-view <name>`, `DebugView` field), `src/Super/GameOptions.cs`, `src/Super/Rendering/SceneRenderer.cs` (sets `FrameView.DebugView`)
- Test: `tests/Super.Tests/GameArgumentsTests.cs`
- Docs: `docs/command-line.md` (`--debug-view trace`), `docs/design.md` (a "Ray tracing" section: device setup, lazy bottom-level structures keyed by mesh and parts, the instance custom index into the geometry table, uvec2 addresses, what a hit skips and why), `docs/rendering.md` (one line pointing at the debug view)

**Interfaces:**
- Consumes: `Rendition.DebugView`, `Renderer.CanTrace`, `VulkanContext.NoTraceReason`.
- Produces: `GameArguments.DebugView` (`Rendition.DebugView`, default `None`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Reads_the_trace_debug_view() =>
    Assert.Equal(Rendition.DebugView.Trace, GameArguments.Parse(["--debug-view", "trace"]).DebugView);

[Fact]
public void Names_the_views_it_knows_when_given_another()
{
    var error = Assert.Throws<ArgumentException>(() => GameArguments.Parse(["--debug-view", "normals"]));
    Assert.Equal("--debug-view doesn't know 'normals'. It knows: trace.", error.Message);
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests/Super.Tests --filter FullyQualifiedName~GameArgumentsTests`
Expected: FAIL.

- [ ] **Step 3: Implement the option and pass it through to `FrameView.DebugView`**

On a GPU that can't trace, or with `--no-trace`, the game exits at start with: `--debug-view trace needs a GPU that can trace rays, and this one can't (<NoTraceReason>).`

- [ ] **Step 4: Run them and make sure they pass**

Run: `dotnet test tests/Super.Tests`
Expected: PASS, whole project.

- [ ] **Step 5: Look at it on real scenes**

Run, for Courtyard and Crypt: `dotnet run --project examples/<Name> -- --screenshot shots/<name>-trace.png --debug-view trace --validate --seed 1 --ticks 120 --size 1280x720`, and the same without `--debug-view` to `shots/<name>-lit.png`.
Expected: exit 0, 0 validation messages; the traced shot shows the same geometry in the same place as the lit one, with cut-out leaves showing holes, wind-swayed parts at rest pose and skinned characters missing. Look at both images. Then `--debug-view trace --no-trace` exits non-zero with the sentence above.

- [ ] **Step 6: Search the docs for stale names and commit**

Run: `grep -rn "ray trac\|CanTrace\|debug-view\|no-trace" docs/` and check every hit is current.

```bash
git add src/Super tests/Super.Tests docs
git commit -m "feat: add --debug-view trace to show the ray tracing layer"
```

## Not in this plan

Refitting top-level structures, compaction, tinting through see-through geometry and any lighting at hits: the live probes plan and the baker plan add them on top of `trace.glsl`. The PR for this branch shows the Courtyard traced and lit side by side, since the debug view is the only thing on screen that changes.
