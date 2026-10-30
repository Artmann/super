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
ShadowPass    scene geometry   -> depth from the key light (4096 x 4096)
GBufferPass   scene geometry   -> albedo+metallic, normal+roughness+AO, emissive+clearcoat, depth
LightingPass  fullscreen       -> HDR colour (Cook-Torrance GGX PBR)
TransparentPass  glass         -> blended over HDR colour, forward shaded
TonemapPass   fullscreen       -> swapchain (ACES filmic, sRGB)
```

G-buffer layout:

| Target | Format | Contents |
| --- | --- | --- |
| 0 | `R8G8B8A8_SRGB` | base colour, metallic |
| 1 | `R16G16B16A16_SFLOAT` | octahedral normal (xy), roughness, AO |
| 2 | `R16G16B16A16_SFLOAT` | emissive colour, clearcoat |
| depth | `D32_SFLOAT` | reversed-Z, position is reconstructed from it |

Games talk to the renderer with plain structs: `Renderer.Render(in FrameView,
ReadOnlySpan<DrawItem>)`. Meshes, textures and materials are created up front and
referenced by handles. A `DrawItem` is an index range of a mesh plus a material,
so one model shares a vertex buffer across its parts.

Model files often link only colour and normal maps. Each model has an
`assets/<name>.materials.json` that connects the remaining textures and sets
material values. The surface texture holds metalness in red, roughness in green
and opacity in alpha.

Shadows blur with distance from the object casting them: the lighting pass finds
how far away the blocker is, then widens its sampling to match. The game says
which area the shadow map covers through `FrameView.Shadows`.

Two frames can be in flight. Render targets are shared between frames, and
pipeline barriers order the access; per-frame state is limited to command
buffers, sync objects and the uniform buffer.

## Environment lighting

`Renderer.SetEnvironment` describes the surroundings as colour bands plus up to
eight glowing panels, and a compute shader bakes that into two cube maps. One holds
a blurred reflection per roughness step and the other the soft light on matte
surfaces. Lighting then needs a single lookup per pixel. It's generated rather than
loaded from a photo, so a game can describe its own space and the softboxes can
line up with the lights. Baking waits for the GPU, so it belongs in loading.

Panels appear only in reflections. The direct lights already provide their diffuse
light, and baking it too would count that light twice and overexpose the scene.

## Paint and glass

Clearcoat adds a second, very smooth reflection on top of the surface, like the
lacquer on a car, and dims what's underneath by the light it reflects. Its roughness
and strength of reflection are fixed in `lighting.frag`, so the G-buffer stores only
how much coat a surface has.

The G-buffer holds one surface per pixel, so glass can't go through it. Glass is
shaded as it's drawn, after lighting, and tests against the depth of the opaque scene
without writing to it. Its reflection stays at full strength however clear the glass
is, and only its tint is scaled by opacity. The colour is premultiplied so the
blend can express that.

## Verification without a window

`--check` and `--screenshot` create the GLFW window hidden, so they never take focus.
Vulkan still renders and presents to it. `Renderer.Capture` copies the finished
frame into a CPU-readable buffer that exists only for that call, so `Render` pays
nothing but a null check for the feature.

## Known simplifications to revisit

- Each buffer and image gets its own `vkAllocateMemory`. Replace with a
  sub-allocator (VMA or our own) before scene sizes grow.
- Textures are uncompressed RGBA8 with mips. Block compression (BC7) would use
  far less GPU memory.
- The table holds at most 1024 textures and 1024 materials, and resources can't be
  removed yet.
- Transparent draws aren't sorted, so overlapping glass can blend in the wrong order.
  Glass also casts no shadow and shows no refraction.
- The car's surface map channels are an educated guess, because the model has no
  documentation. The paint's metalness is set to 0.2 by eye.
- One shadow map covering a fixed area. Large outdoor scenes will need cascades
  that follow the camera.
- Only directional lights so far. Point and spot lights come later.
- Only one light casts shadows, so the others shine through objects. Ambient
  occlusion will ground the car.
- Environment light isn't blocked by nearby objects, and the environment is
  treated as infinitely far away. Ambient occlusion will cover the worst of it.
