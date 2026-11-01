# Procedural terrain design

Date: 2026-11-01. Status: approved direction, not yet built.

## Intent

The user's next game is built on procedural terrain: ground made by code, textured by
height and biome, big enough that it loads in around the player. Today Super can draw a
heightfield (the Reef seabed does it), but only with one baked colour texture, one
shadow radius of a few metres, and nothing ever freed. This design fills in everything
between that seabed and a real outdoor world, in both Rendition and Super.

It is three pieces of work that each stand alone, done together:

1. **Layered materials.** A material can carry a game-written shader, stacks of
   textures and a block of parameters, so one surface blends grass, rock and snow.
2. **Resources that come and go.** Meshes, textures and materials can be destroyed
   while the game runs, GPU memory is sub-allocated, and shadows cover an outdoor
   scene with cascades.
3. **Terrain in Super.** A game names shaders and textures through `Content`, builds
   chunks from a `Heightfield` helper, and a Terrain example shows it all working.

## Goals

- A game writes a GLSL fragment shader, puts it in its `Shaders` folder, names it with
  `content.Shader("terrain")`, and attaches it to a generated mesh's surface.
- That shader can read up to four texture arrays (a stack of same-sized images), up
  to four single textures, and eight `vec4` parameters from its material.
- Images load from PNG files in the content folder or from bytes made in code, as
  single textures or as arrays.
- A terrain chunk is one `GeneratedMesh`. The game loads chunks around the player and
  unloads the ones behind, and GPU memory comes back. Loading a chunk never stalls the
  frame: creating a mesh or texture during play hands its bytes to the next frame's
  command buffer rather than waiting for the GPU.
- Shadows reach as far as the game asks, with the sharpest cascade near the camera.
- Hundreds of chunks don't hit Vulkan's allocation limit.
- A `Heightfield` helper builds chunk meshes with seamless normals and answers
  `Height(x, z)` for gameplay.
- Headless tests still work with no GPU: naming a shader, a texture or a mesh touches
  nothing until the window loads it.
- No managed allocations per frame once a scene is steady, as before.

## Non-goals

- Compiling GLSL at run time or hot-reloading shaders. Shaders compile at build time
  through the existing `Shaders.targets`, which the game project imports. This keeps
  one toolchain and no new native dependency.
- Changing a mesh's vertices in place. Destroy and create again.
- GPU-side terrain: heightmap sampling in a vertex shader, tessellation, clipmaps.
  Custom vertex shaders stay unsupported.
- Block-compressed textures. Still RGBA8 with mips.
- Point and spot light shadows. Cascades apply to the one shadow-casting sun.
- Blending between cascades at their borders. The border is a hard switch; picking
  the next cascade where the nearer one runs out is enough for now.
- Simplified versions of terrain chunks. Neighbouring chunks simplified on their own
  open cracks along their shared edge, so chunks are culled but always drawn in full.
  Locking border vertices in `MeshDetail` can come later.
- A streaming helper that decides which chunks to load. The example shows the pattern
  with a dictionary; a helper can follow once two games want the same thing.
- Physics or collision beyond `Height(x, z)`.

## Anti-goals

- A terrain-specific renderer path. Everything here is general: layered materials
  also suit a painted road or a rusting ship; destroy and cascades suit any game.
- Per-vertex biome data. The `Vertex` layout stays position, normal, tangent, UV.
  Biome weights come from a splat texture sampled by world position, which needs no
  vertex change and blends across chunk borders.
- Stalling the frame to destroy things. A destroyed resource is released two frames
  later, when the GPU is done with it.

## Shape

### Rendition: materials with shader inputs

`Material` grows three things, and nothing else about it changes:

```csharp
public struct Material
{
    // ...existing fields...
    public SurfaceShaderHandle? Shader;

    /// <summary>Eight vec4s for the shader to use however it likes, as shaderParameters[i].</summary>
    public ShaderParameters ShaderParameters;

    /// <summary>Up to four single textures for the shader, as shaderTextures[i]. Missing ones are white.</summary>
    public ShaderTextures ShaderTextures;

    /// <summary>Up to four texture arrays for the shader, as shaderArrays[i]. Missing ones are one white layer.</summary>
    public ShaderArrays ShaderArrays;
}

[InlineArray(8)] public struct ShaderParameters { private Vector4 _element; }
[InlineArray(4)] public struct ShaderTextures { private TextureHandle? _element; }
[InlineArray(4)] public struct ShaderArrays { private TextureArrayHandle? _element; }
```

`ShaderParameters` used to be one `Vector4`. The sample's glow shader and its C# move
to `shaderParameters[0]` and `ShaderParameters[0]`. This is the one breaking change.

`material.glsl` matches:

```glsl
struct Material {
    // ...existing fields...
    vec4 shaderParameters[8];
    uint shaderTextures[4];
    uint shaderArrays[4];
};
layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(set = 1, binding = 1) readonly buffer Materials { Material materials[]; };
layout(set = 1, binding = 2) uniform sampler2DArray textureArrays[];
```

`surface.glsl` adds two helpers so terrain shaders read naturally:

```glsl
vec4 shaderTexture(int slot, vec2 uv);              // textures[materials[draw.material].shaderTextures[slot]]
vec4 shaderLayer(int slot, vec2 uv, float layer);    // textureArrays[...shaderArrays[slot]], vec3(uv, layer)
```

### Rendition: texture arrays

```csharp
public readonly record struct TextureArrayHandle(int Index);

/// <summary>Stacks same-sized images into one texture that shaders index by layer. Waits for the GPU.</summary>
public TextureArrayHandle CreateTextureArray(ReadOnlySpan<TextureData> layers);
```

Every layer must share width, height, kind and mip count, or the call throws naming
the layer that differs. The array lives in its own descriptor binding with its own
partially-bound slot table, at most 256 arrays. Slot 0 is one white layer.

### Rendition: uploads that don't wait

Today every mesh and texture upload submits its own command buffer and waits for it,
and adding a texture waits for the whole device. During streaming that is a hitch per
chunk. Instead:

- `CreateMesh`, `CreateDrawItem` and `CreateTexture` copy their bytes into a staging
  buffer and queue the copy. The next `Render` records every queued copy, with its
  layout transitions, at the start of its command buffer before the shadow pass, so a
  resource created before a `Render` is drawable in that same frame. The staging
  buffer goes through the destroy queue and is freed two frames later.
- Descriptor writes for new textures use `descriptorBindingUpdateUnusedWhilePending`
  alongside the existing partially-bound flag, so a slot can be written while the set
  is bound by a frame that doesn't use it. No more `DeviceWaitIdle` in `AddTexture`.
- `CreateTextureArray`, `CreateSurfaceShader`, `SetEnvironment`, `SetOverlayAtlas`
  and probe capture still wait, and say so. They're for loading screens.
- `CreateDrawItem` gains `simplify: bool = true`. With it false the draw gets a bounding
  sphere for culling but no simpler versions.

### Rendition: destroying resources

```csharp
public void DestroyMesh(MeshHandle mesh);          // Also frees its detail entries
public void DestroyTexture(TextureHandle texture);
public void DestroyTextureArray(TextureArrayHandle array);
public void DestroyMaterial(MaterialHandle material);
public void DestroyModel(ReadOnlySpan<DrawItem> parts); // The mesh and materials CreateModel made, and their textures
```

Rules:

- Destroying returns at once and never waits for the GPU. The resource is queued and
  released after the frame that was in flight when it was queued has finished, which
  the next-but-one `Render` observes by its fence. `Dispose` drains the queue.
- Handles are recycled. Each kind keeps a free list, so a long game stays under the
  1024 texture and material limits. A destroyed texture's descriptor slot is rewritten
  to the white texture when it's released, so nothing dangles.
- Using a destroyed handle in a draw is a caller mistake. Debug builds can't check it
  cheaply, so the doc comment says so and the white texture and default material keep
  the frame from crashing.
- Queuing allocates nothing when nothing is destroyed. The queue is two fixed lists,
  one per frame slot, that grow only on first use.

### Rendition: memory sub-allocation

A `GpuMemory` allocator replaces one `vkAllocateMemory` per resource:

- Pools per memory type. Each pool is a list of 64 MB blocks. A request larger than a
  block gets its own dedicated allocation, as today.
- Buffers and images use separate pools, which sidesteps `bufferImageGranularity`.
- Inside a block, a free-list allocator with alignment, best fit, and merging of
  neighbouring free ranges. `MemoryArena` is the pure bookkeeping class with no
  Vulkan in it, so it has unit tests in `Super.Tests` alongside `MeshDetailTests`.
- Host-visible blocks are mapped once; a buffer's `Mapped` pointer is the block's
  pointer plus its offset.
- `GpuBuffer` and `GpuTexture` take an allocation and release it on `Dispose`.
  Render targets and the swapchain keep working through the same path.

### Rendition: cascaded shadows

```csharp
public struct ShadowSettings
{
    public Vector3 Center;
    public float Radius;
    public float LightAngle;

    /// <summary>How many shadow maps, 1 to 4, each covering three times the radius of the one before. 0 means 1.</summary>
    public int Cascades;
}
```

- The shadow map becomes a 2D array of four layers at 2048 × 2048 each, 64 MB in all,
  where one 4096 map was 64 MB. One cascade therefore has half the sharpness of
  today's map. The car sample fits its radius to the car, where 2048 is still a few
  millimetres per texel. Reef moves from one 75 m map to three cascades from 25 m,
  which is sharper near the camera and reaches 225 m.
- Cascade `i` is a sphere of radius `Radius × 3^i` around `Center`, using the same
  parallel projection as today. Its centre snaps to the cascade's texel grid in light
  space so the picture doesn't swim as the camera moves.
- Each cascade culls and simplifies draws against its own matrix, through the
  existing `DetailView`, with texels per unit from its own radius. The pass records
  each layer in turn.
- `Frame` uniforms carry `mat4 shadowMatrices[4]` and `vec4 shadowRadii` instead of
  one matrix. The lighting shader picks the first cascade whose projected point lies
  inside, with a small inset so the switch happens before the edge. Blur, blocker
  search and normal offset work in that cascade's texel size.
- Ambient occlusion's contact search and reflection probe captures use cascade 0
  only, as today.
- `Renderer.DrawnTriangles.Shadows` counts all cascades.

Super's `View.Shadows(radius)` gains a cascade count: `View.Shadows(radius, cascades)`.
The default stays one cascade at 8 m. The cascade index reaches `shadow.vert` through
the push-constant block.

### Super: naming shaders, textures and materials

`Content` names three new kinds of thing. Like models, naming touches no GPU and
loads in the background in the window.

```csharp
public readonly record struct ShaderRef(int Index);
public readonly record struct TextureRef(int Index);
public readonly record struct TextureArrayRef(int Index);

/// <summary>Names a fragment shader compiled from the game's Shaders folder, by file name without extension.</summary>
public ShaderRef Shader(string name);

/// <summary>Names a PNG in the content folder, or an image made in code. A normal map keeps its mips unit length.</summary>
public TextureRef Texture(string path, TextureKind kind = TextureKind.Color, bool normalMap = false);
public TextureRef Texture(GeneratedTexture texture, TextureKind kind = TextureKind.Color, bool normalMap = false);

/// <summary>Stacks images into one array the shader indexes by layer. All must be the same size.</summary>
public TextureArrayRef TextureArray(params ReadOnlySpan<TextureRef> layers);

/// <summary>Hands over a surface made by code.</summary>
public ModelRef Mesh(GeneratedMesh mesh);

/// <summary>Whether this one thing is ready to draw. Always true without a window.</summary>
public bool IsLoaded(ModelRef model);

/// <summary>Lets go of a mesh or texture. The window frees it; naming it again later makes a new one.</summary>
public void Unload(ModelRef model);
public void Unload(TextureRef texture);
```

Shaders come from `shaders/<name>.spv` beside the program. The game project imports
`src/Shaders.targets`, which already compiles `Shaders\*.frag` with Rendition's
include folder. `content.Shader("terrain")` reads the file in the loader's prepare
step and `Renderer.CreateSurfaceShader` validates it. In headless runs nothing is read.

PNGs decode with StbImageSharp, which the import tool already uses. The mip builder in
`Rendition.Import.TextureCooker` moves to a public `TextureData.FromPixels(width,
height, kind, pixels)` in Rendition, with the normal-map option, and both the tool and
Super call it. Super's own `BuildMips` goes away.

`GeneratedMesh` gets a surface instead of one texture:

```csharp
/// <summary>How a generated mesh looks. Everything is optional; the default is matte grey.</summary>
public sealed record Surface
{
    public Vector3 Color { get; init; } = new(0.7f);
    public float Roughness { get; init; } = 0.4f;
    public float Metallic { get; init; }
    public TextureRef? ColorTexture { get; init; }
    public TextureRef? NormalTexture { get; init; }
    public TextureRef? SurfaceTexture { get; init; }
    public ShaderRef? Shader { get; init; }
    public Vector4[] Parameters { get; init; } = [];       // Up to 8
    public TextureRef[] ShaderTextures { get; init; } = []; // Up to 4
    public TextureArrayRef[] ShaderArrays { get; init; } = []; // Up to 4
}

/// <summary>A surface made by code. Simplify is off for terrain chunks, whose neighbours must match.</summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, Surface Surface, bool Simplify = true)
{
    /// <summary>The old shape: one colour texture and a roughness. The texture is owned by the mesh and freed with it.</summary>
    public GeneratedMesh(Vertex[] vertices, uint[] indices, GeneratedTexture texture, float roughness) : ...
}
```

Reef keeps compiling through the second constructor. A `Surface` can't be shared by
`ModelRef`; each generated mesh gets its own material, which is right for terrain
chunks since each has its own splat map. Models from files keep their own materials
as before.

Loading order: a mesh whose surface names a texture or shader that isn't loaded yet
waits for it. `ContentLoader` already uploads in naming order, and the surface's refs
always have lower indices than the mesh, so this falls out with no new machinery. A
ref that was never named, or points past what was named, throws from `Content.Mesh`
with the field's name.

Unloading: `Content.Unload(model)` records the index. `SceneRenderer` sees it in
`Prepare`, calls the renderer's destroy methods for what that item made, and forgets
it. The same goes for a texture; a per-chunk splat map must die with its chunk or a
long fly-around runs out of texture slots. Drawing or referencing an unloaded thing
afterwards throws with its name, which is a game bug worth surfacing. Indices aren't
reused. Shaders and texture arrays can't be unloaded in this phase; they're few and
shared.

`Content.IsLoaded(model)` is per item, set by the loader as each finishes. The
example only draws chunks that answer true, so a chunk still preparing on the
background thread never blocks the frame through `ContentLoader.Get`. Without a
window everything is loaded at once, so it's always true there.

### Super: `Heightfield`

In `Super`, namespace `Super.Terrain`:

```csharp
/// <summary>Ground as heights on a grid. Builds chunk meshes and answers how high the ground is anywhere.</summary>
public sealed class Heightfield(Func<float, float, float> height, float spacing)
{
    public float Height(float x, float z);   // The function itself; the mesh samples it on the grid
    public Vector3 Normal(float x, float z); // Central differences at the grid spacing
    public GeneratedMesh Chunk(int chunkX, int chunkZ, int quads, Surface surface, float uvPerMetre);
}
```

The chunk mesh has `Simplify` off.

- A chunk covers `quads × spacing` metres from `(chunkX × size, chunkZ × size)`.
  Vertices sit on the shared grid, so neighbours share border heights and normals
  exactly, with no seams.
- UVs are world `x, z` times `uvPerMetre`, so tiled textures continue across chunks.
  Tangents point along +x with positive handedness, which suits normal maps on
  ground.
- Normals use the function, not the mesh, so a chunk's border normals already know
  about the next chunk.
- Reef's `Seabed` stays as it is; converting it is optional and not part of this.

### Example: Terrain

`examples/Terrain`, with `tests/Terrain.Tests`, needing no assets like Rooms:

- Rolling hills from layered sine and value noise through `Heightfield`.
- A `terrain.frag` in `examples/Terrain/Shaders` blending four layers (sand, grass,
  rock, snow) from a per-chunk splat map in `shaderTextures[0]` and a height-and-
  slope rule from `shaderParameters`. Layer colours and normals are two texture
  arrays made in code with `GameRandom`, so nothing needs cooking.
- Chunks load around the camera within a radius and unload beyond it, through
  `content.Mesh`, `content.Texture` for the splat map, `content.IsLoaded` and
  `content.Unload`, keyed by chunk coordinates in a dictionary.
- A fly camera on the existing `Actions` input, and three shadow cascades.
- Tests: `Heightfield` seams and normals, chunk set changes as the camera moves,
  and a headless run of a few hundred ticks allocating nothing per tick once steady.

Screenshots come from `--screenshot` as always, never the desktop.

### Documentation

- `docs/design.md`: the shadow pass row, the resource table limits, the memory
  simplification, cascades and "resources can't be removed" all update or go.
- `src/Rendition/README.md` status list and API list.
- `README.md` gets a short "Terrain" section pointing at the example, and the
  examples table lists it.

## Constraints

- Braces on every control statement. Conventional commits, no Claude authorship.
- The `--check` run of `Rendition.Sample` must still allocate nothing and produce no
  validation messages; add the destroy queue and cascades to what it exercises.
- `SpinningCubeTests.Running_the_game_allocates_nothing_per_frame` keeps passing.
- GLSL compiles with `-Werror` under `glslc`.
- Everything in `Content` must work with `IContentFiles` in memory, so tests can hand
  it a PNG encoded with the existing `PngFile` writer.

## Decisions made after the builder check

- Per-chunk splat maps are unloaded with their chunk, so `Unload` takes textures.
- Shadow layers are 2048. Reef and the car sample get cascades to keep their look.
- Terrain chunks are culled but never simplified, which avoids cracks.
- Creating meshes and textures during play never waits on the GPU; uploads ride the
  next frame's command buffer. `Content.IsLoaded(ModelRef)` lets a game draw only
  what's ready.
- `EnvironmentMap`, `ReflectionProbes` and `RenderTarget` move onto `GpuMemory` with
  everything else, so one allocator owns all device memory.
- Create paths may allocate managed memory. Only the destroy and upload queues, and
  rendering, must not once steady.

## Left to the builder

- Exact free-list and block-size tuning in `MemoryArena`, as long as the tests cover
  alignment, merging and dedicated allocations.
- How the shadow pass records four layers: one `vkCmdBeginRendering` per layer with a
  per-layer image view is the expected shape.
- The noise functions and layer looks in the Terrain example.
- Whether `Heightfield` caches the grid heights per chunk; correctness over speed,
  measured by the example staying smooth with a few hundred chunks.
