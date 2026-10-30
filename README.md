# Rendition

A deferred, physically based renderer for .NET and Vulkan. It is the graphics
foundation of a future game engine, generic enough for any 3D game. It handles
rendering in a window and nothing else.

## Status

The first milestone is done: a rotating PBR cube drawn through the real deferred
pipeline, with no managed allocations per frame and no Vulkan validation
messages.

What exists today:

- A G-buffer pass that writes base colour, metallic, normal, roughness, AO and
  depth, using reversed-Z depth
- A lighting pass with Cook-Torrance GGX shading, one directional light and flat
  ambient
- A tonemap pass that takes HDR colour to the screen with ACES filmic
- Two frames in flight and window resize handling
- GLSL shaders compiled to SPIR-V at build time
- A small public API: `Renderer.CreateMesh` and
  `Renderer.Render(in FrameView, ReadOnlySpan<DrawItem>)`

Planned next, in order: lighting, textures, post-processing, custom shaders.

Decisions, the G-buffer layout and known simplifications are in
[docs/design.md](docs/design.md).

## Layout

| Path | Contents |
| --- | --- |
| `src/Rendition` | The renderer library |
| `src/Rendition/Passes` | The render passes and their shared targets |
| `src/Rendition/Vulkan` | Device, swapchain, buffer and pipeline helpers |
| `src/Rendition/Shaders` | GLSL source |
| `src/Rendition.Sample` | The window and the rotating cube |

## Requirements

- Windows and a GPU with Vulkan 1.3 support
- .NET 10 SDK
- Vulkan SDK (`winget install KhronosGroup.VulkanSDK`), used to compile shaders
  and for the validation layers. Open a new terminal after installing so
  `VULKAN_SDK` is set.

## Run

```
dotnet run -c Release --project src/Rendition.Sample
```

Press Esc to quit. Add `-- --validate` to turn on Vulkan validation.

## Check

```
dotnet run -c Release --project src/Rendition.Sample -- --check
```

Renders 900 frames after a warm-up and exits non-zero if the renderer allocated
any managed memory or produced any validation message.
