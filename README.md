# Rendition

A deferred, physically based renderer for .NET and Vulkan. It is the graphics
foundation of a future game engine, generic enough for any 3D game. It handles
rendering in a window and nothing else.

## Status

The renderer draws a textured, 192,000-triangle car model through the real
deferred pipeline, with no managed allocations per frame and no Vulkan validation
messages.

What exists today:

- A G-buffer pass that writes base colour, metallic, normal, roughness, AO and
  depth, using reversed-Z depth
- A shadow pass that renders depth from the key light
- A lighting pass with Cook-Torrance GGX shading, up to eight directional lights,
  soft contact-hardening shadows, studio reflections and a fade into the background
- A generated environment, baked by a compute shader into cube maps, that gives
  surfaces their reflections and soft fill light
- A forward pass that blends glass over the lit image
- Car paint clearcoat, and surfaces that give off light
- Ambient occlusion, bloom and FXAA anti-aliasing
- A tonemap pass that takes HDR colour to display colour with ACES filmic
- Two frames in flight and window resize handling
- GLSL shaders compiled to SPIR-V at build time
- Bindless textures and materials, normal mapping, cut-out and two-sided surfaces
- An import tool that cooks FBX models and their textures into a `.rmodel` file
- A small public API: `Renderer.CreateModel`, `CreateMesh`, `CreateTexture`,
  `CreateMaterial` and `Render(in FrameView, ReadOnlySpan<DrawItem>)`

Planned next: custom shaders.

Decisions, the G-buffer layout and known simplifications are in
[docs/design.md](docs/design.md).

## Layout

| Path | Contents |
| --- | --- |
| `src/Rendition` | The renderer library |
| `src/Rendition/Passes` | The render passes and their shared targets |
| `src/Rendition/Vulkan` | Device, swapchain, buffer and pipeline helpers |
| `src/Rendition/Shaders` | GLSL source |
| `src/Rendition.Sample` | The window, orbit camera, model viewer and rotating cube |
| `tools/Rendition.Import` | Converts models to `.rmodel` |
| `assets` | Per-model import rules. Source and cooked models stay out of git |

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

To view a model, cook it once and pass it to the sample. Drag to orbit and scroll
to zoom.

```
dotnet run -c Release --project tools/Rendition.Import -- assets/source/ford-gt/FINAL_MODEL_05H.fbx assets/ford-gt.materials.json assets/cooked/ford-gt.rmodel
dotnet run -c Release --project src/Rendition.Sample -- --model assets/cooked/ford-gt.rmodel
```

The `assets/cooked` folder must exist first. The car's source files aren't in git,
so copy them into `assets/source/ford-gt`.

## Check

```
dotnet run -c Release --project src/Rendition.Sample -- --check
```

Renders 900 frames after a warm-up and exits non-zero if the renderer allocated
any managed memory or produced any validation message. It also resizes the window
during the warm-up.

To save a picture, use `--screenshot`. The camera can be placed with `--yaw`,
`--pitch` and `--distance`.

```
dotnet run -c Release --project src/Rendition.Sample -- --screenshot shot.png --model assets/cooked/ford-gt.rmodel --yaw -2.4
```

Both run with validation on and never show a window, so they can run in the
background while you work.
