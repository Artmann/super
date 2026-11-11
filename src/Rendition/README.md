# Rendition

A deferred, physically based renderer for .NET and Vulkan. It is the graphics
foundation of the [Super](../../README.md) game engine, and can be used without it.
It handles rendering in a window and nothing else.

Run the commands below from the repository root.

## Status

The renderer draws a textured, 192,000-triangle car and a 929,000-triangle character
through the real deferred pipeline, with no managed allocations per frame and no Vulkan validation
messages.

What exists today:

- A G-buffer pass that writes base colour, metallic, normal, roughness, AO and
  depth, using reversed-Z depth
- A shadow pass that renders depth from the key light, in up to four cascades for
  wide outdoor scenes
- A lighting pass with Cook-Torrance GGX shading, up to eight directional lights,
  soft contact-hardening shadows, studio reflections and a fade into the background
- A generated environment, baked by a compute shader into cube maps, that gives
  surfaces their reflections and soft fill light
- A forward pass that blends glass over the lit image
- A particle pass of soft, lit puffs that fade into surfaces, passed in as
  `FrameView.Particles`
- Car paint clearcoat, and surfaces that give off light
- Screen-space reflections and box-corrected reflection probes
- Temporal anti-aliasing, ambient occlusion and bloom, each of which, like both kinds of
  shadow and screen-space reflections, a frame can leave out with `FrameView.Off`
- Colour grading, sharpening, vignette, film grain and dithering
- A tonemap pass that takes HDR colour to display colour with ACES filmic
- An overlay of flat textured quads drawn over the finished picture, for text and
  interfaces. Fill `FrameView.Overlay` and call `Renderer.SetOverlayAtlas`
- Two frames in flight, window resize handling, and vsync that switches on and off with
  `Renderer.Vsync`
- GPU timing for each render pass, switched on with `Renderer.MeasureGpu`
- GLSL shaders compiled to SPIR-V at build time
- Bindless textures and materials, normal mapping, cut-out and two-sided surfaces
- The `.rmodel` format for models and their textures, cooked from FBX and glTF by
  `Super.Cook.Models`
- Custom surface shaders that a game can write and attach to a material, with eight
  `vec4` parameters, four textures and four texture arrays per material
- Creating meshes and textures during play without waiting for the GPU: uploads ride
  the next frame, and destroying waits for the frames in flight
- Automatic culling and simpler versions of distant models, for the camera and for
  shadows
- A small public API: `Renderer.CreateModel`, `CreateDrawItem`, `CreateMesh`,
  `CreateTexture`, `CreateTextureArray`, `CreateMaterial`, `UpdateMaterial`,
  `CreateSurfaceShader`, the matching `Destroy` calls and
  `Render(in FrameView, ReadOnlySpan<DrawItem>)`

Decisions, the G-buffer layout and known simplifications are in
[docs/design.md](../../docs/design.md).

## Layout

| Path | Contents |
| --- | --- |
| `src/Rendition` | The renderer library |
| `src/Rendition/Passes` | The render passes and their shared targets |
| `src/Rendition/Vulkan` | Device, swapchain, buffer and pipeline helpers |
| `src/Rendition/Shaders` | GLSL source |
| `src/Rendition.Sample` | The window, orbit camera, model viewer and rotating cube |
| `src/Rendition.Sample/Shaders` | An example custom surface shader, shown with `--glow` |
| `assets` | The examples' shared source models and their cook settings. The models stay out of git |

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

To view a model, pass a cooked `.rmodel` to the sample. Drag to orbit and scroll
to zoom. The car's source files aren't in git, so save them in the shared assets folder
as `assets/ford-gt/ford-gt.fbx` (see [assets/README.md](../../assets/README.md)), then
cook it with `super-cook file`:

```
dotnet run --project tools/Super.Cook.Tool -- file --content assets ford-gt/ford-gt.fbx out/ford-gt.rmodel
dotnet run -c Release --project src/Rendition.Sample -- --model out/ford-gt.rmodel
```

The character works the same way, from `assets/adventurer/adventurer.glb`:

```
dotnet run --project tools/Super.Cook.Tool -- file --content assets adventurer/adventurer.glb out/adventurer.rmodel
dotnet run -c Release --project src/Rendition.Sample -- --model out/adventurer.rmodel --yaw 1.5
```

That model is [a character model](https://sketchfab.com/3d-models/592c2f0cfaf4407bab3ef40dfc095eb1)
by Otto Rossi, under CC-BY-4.0. Credit the author if you share it or pictures of it.

## Check

```
dotnet run -c Release --project src/Rendition.Sample -- --check
```

Renders 900 frames after a warm-up and exits non-zero if the renderer allocated
any managed memory or produced any validation message. It also resizes the window
during the warm-up.

To save a picture, use `--screenshot`. The camera can be placed with `--yaw`,
`--pitch`, `--distance` and `--height`. `--probes` puts a light probe volume round
the cube, to check bounced light. The probes don't follow the spin, so the turning
cube shows dark streaks with it on.

```
dotnet run -c Release --project src/Rendition.Sample -- --screenshot shot.png --model out/ford-gt.rmodel --yaw -2.4
```

Both run with validation on and never show a window, so they can run in the
background while you work.
