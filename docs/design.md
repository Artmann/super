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
ReadOnlySpan<DrawItem>)`. Meshes are created up front and referenced by a
`MeshHandle`.

Two frames can be in flight. Render targets are shared between frames, and
pipeline barriers order the access; per-frame state is limited to command
buffers, sync objects and the uniform buffer.

## Known simplifications to revisit

- Each buffer and image gets its own `vkAllocateMemory`. Replace with a
  sub-allocator (VMA or our own) before scene sizes grow.
- Materials are constants in push constants. Textures come next.
- One directional light plus flat ambient. A light list and image-based
  lighting come with the lighting milestone.
