# Rendition design

Rendition is the graphics foundation of a future game engine: a deferred, PBR
renderer in modern .NET, generic enough for any 3D game. It only does rendering
and owns nothing beyond the window it draws into.

## Goals

- AAA-style architecture from the first commit: the rotating cube goes through
  the real deferred path, not a throwaway demo.
- No managed allocations per frame, measured and enforced by `--check`.
- Clean under the Vulkan validation layers.

## Non-goals (for now)

Editor, physics, audio, game logic, non-Windows platforms.

## Decisions

| Area | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10, unsafe code enabled | Current LTS; direct control over memory |
| Graphics API | Vulkan 1.3 minimum | Dynamic rendering and synchronization2 remove boilerplate; leaves Linux open |
| Bindings | Silk.NET.Vulkan | Thin, no per-call allocations |
| Window | Silk.NET.GLFW | Small and portable; the renderer never depends on it directly |
| Shaders | GLSL compiled to SPIR-V by `glslc` at build time | No compiler in the shipped game; runtime loading of `.spv` leaves room for custom shaders |
| Math | System.Numerics | SIMD-accelerated value types |
| Model import | Separate tool using Assimp, writing our own `.rmodel` | The game loads a fast flat file and never ships an FBX parser |
| Textures and materials | One global descriptor set indexed by material | Adding materials adds no binding work per draw |

## Frame structure

```
GBufferPass   scene geometry   -> albedo+metallic, normal+roughness+AO, depth
LightingPass  fullscreen       -> HDR colour (Cook-Torrance GGX PBR)
TonemapPass   fullscreen       -> swapchain (ACES filmic, sRGB)
```

G-buffer layout:

| Target | Format | Contents |
| --- | --- | --- |
| 0 | `R8G8B8A8_SRGB` | base colour, metallic |
| 1 | `R16G16B16A16_SFLOAT` | octahedral normal (xy), roughness, AO |
| depth | `D32_SFLOAT` | reversed-Z, position is reconstructed from it |

Games talk to the renderer with plain structs: `Renderer.Render(in FrameView,
ReadOnlySpan<DrawItem>)`. Meshes, textures and materials are created up front and
referenced by handles. A `DrawItem` is an index range of a mesh plus a material,
so one model shares a vertex buffer across its parts.

Model files often link only colour and normal maps. Each model has an
`assets/<name>.materials.json` that connects the remaining textures and sets
material values. The surface texture holds metalness in red, roughness in green
and opacity in alpha.

Two frames can be in flight. Render targets are shared between frames, and
pipeline barriers order the access; per-frame state is limited to command
buffers, sync objects and the uniform buffer.

## Known simplifications to revisit

- Each buffer and image gets its own `vkAllocateMemory`. Replace with a
  sub-allocator (VMA or our own) before scene sizes grow.
- Textures are uncompressed RGBA8 with mips. Block compression (BC7) would use
  far less GPU memory.
- The table holds at most 1024 textures and 1024 materials, and resources can't be
  removed yet.
- `CreateModel` skips parts marked `Transparent` until the transparency pass exists.
- The car's surface map channels are an educated guess, because the model has no
  documentation. Confirm them by eye once reflections exist.
- One directional light plus flat ambient. A light list and image-based
  lighting come with the lighting milestone.
