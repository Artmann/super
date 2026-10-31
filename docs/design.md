# Super design

Super is a C# game engine used as a library, with no editor. Rendition is its
renderer and stays a separate project. `README.md` describes the engine as a user
would meet it. This file records the reasons behind it, then Rendition's design.

## Engine decisions

These are decided but not built yet, apart from the project layout.

| Area | Choice | Why |
| --- | --- | --- |
| Projects | `Super`, optional `Super.Ecs` and `Super.Testing`, and `Rendition`, which knows nothing of the others | Each layer stays usable without those above it |
| Structure | Game code is plain classes behind `IGame`, with no built-in scheduler | The game's author owns the logic and the order it runs in |
| Time | `Update` runs on fixed ticks, `Draw` once per displayed frame and blends between ticks | Repeatable simulation, whatever the frame rate |
| Drawing | Views are plain functions of state, called every frame | Nothing to keep in sync, and loading needs no special cases |
| View memory | Kept by Super under a key. A new key goes straight to its target | A loaded game appears finished, with no animations replaying |
| Virtual tree | None, unlike React and SwiftUI | Games redraw most things each frame, so comparing trees gains little and allocates |
| Hierarchy | Flat, with attachment by transform or bone | Colony and dungeon games have thousands of independent things and little nesting |
| Unchanging objects | `View.Cached`, keyed by a version number | Static worlds aren't described again each frame |
| Dependency injection | `Microsoft.Extensions.DependencyInjection`, resolved at startup only | Familiar, makes fakes one line, and costs nothing per frame |
| Global state | None | Many headless games can share one process |
| Randomness | `GameRandom` with named streams from one seed | A new roll in one system doesn't disturb another |
| Content | JSON read by `System.Text.Json` into records, loaded up front | No extra library, and data errors surface at load time |
| Saves and levels | JSON early on, expected to be replaced | Easy to read and repair during development |
| Physics | Left out of the first version | Must run in ticks and repeat exactly, and the target games need little |

The rule for dividing data: if gameplay cares, it's state; if only the eyes care, it's
view. Views read state and never write it, so animation can't change a simulation's
outcome. Repeatability is promised for the same build on the same kind of CPU, because
floating point results can differ between CPU types.

Rendition needs work to support this: keys passed through to per-object motion
vectors, skeletal animation in the importer and the vertex shader, a particle pass,
and a check of how it copes with many thousands of draws.

# Rendition design

Rendition is a deferred, PBR renderer in modern .NET, generic enough for any 3D game.
It only does rendering and owns nothing beyond the window it draws into.

## Goals

- AAA-style architecture from the first commit: the rotating cube goes through
  the real deferred path, not a throwaway demo.
- No managed allocations per frame, measured and enforced by `--check`.
- Clean under the Vulkan validation layers.

## Non-goals (for now)

Editor, physics, audio, game logic and non-Windows platforms. Rendition keeps to
rendering. Game-facing features belong in Super.

## Decisions

| Area | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10, unsafe code enabled | Current LTS; direct control over memory |
| Graphics API | Vulkan 1.3 minimum | Dynamic rendering and synchronization2 remove boilerplate; leaves Linux open |
| Bindings | Silk.NET.Vulkan | Thin, no per-call allocations |
| Window | Silk.NET.GLFW | Small and portable; the renderer never depends on it directly |
| Shaders | GLSL compiled to SPIR-V by `glslc` at build time | No compiler in the shipped game; a game compiles its custom shaders the same way |
| Math | System.Numerics | SIMD-accelerated value types |
| Model import | Separate tool using Assimp, writing our own `.rmodel` | The game loads a fast flat file and never ships an FBX or glTF parser |
| Textures and materials | One global descriptor set indexed by material | Adding materials adds no binding work per draw |

## Frame structure

```
ShadowPass    scene geometry   -> depth from the key light (4096 x 4096)
GBufferPass   scene geometry   -> albedo+metallic, normal+roughness+AO, emissive+clearcoat, depth
AoPass        fullscreen       -> ambient occlusion from depth and normals
AoBlurPass    fullscreen       -> the same with the grain averaged away
LightingPass  fullscreen       -> HDR colour (Cook-Torrance GGX PBR, traced reflections, probes)
TransparentPass  glass         -> blended over HDR colour, forward shaded
TaaPass       fullscreen       -> this frame blended with history, then copied to history
BloomPass     fullscreen x 9   -> glow, by shrinking and regrowing the image
TonemapPass   fullscreen       -> display colour (ACES filmic, colour grade)
FinishPass    fullscreen       -> swapchain, with sharpening, vignette, grain and dither
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

glTF files (`.glb`, `.gltf`) carry more of this themselves, so the importer reads their
embedded images, the link to their metal and roughness texture, and their colour,
metal and roughness values. glTF keeps metalness in blue, so the importer moves it to
red. A rule in the materials file still wins over the model file. The renderer has one
UV set, and the importer keeps the first. That's fine for the adventurer model, whose four
sets are identical.

Shadows blur with distance from the object casting them: the lighting pass finds
how far away the blocker is, then widens its sampling to match. The game says
which area the shadow map covers through `FrameView.Shadows`. A smaller area gives
sharper shadows, so the sample fits it to the model. Anything outside the area, in
depth as well as sideways, counts as lit.

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

## Reflections

A reflection comes from the most accurate source that can answer. First, a ray walks
through the depth buffer (screen-space reflection) and reads what it hits from the
previous finished frame. Slightly rough surfaces tilt the ray at random per pixel and
per frame, and temporal anti-aliasing averages that into a blur. Rays that leave the
screen fall back to a reflection probe, and without a probe to the studio.

A probe is a cube map captured from one spot by `Renderer.CaptureReflectionProbe`,
using the normal scene passes, then blurred per roughness step. It waits for the GPU,
so it belongs in loading. Each probe has a box for the space it describes, and a
reflected ray runs on to that box before the lookup, so reflections line up with where
they really land instead of sliding as the camera moves. Up to four probes blend at
their box edges. The sample leaves the car out of its probe, because the probe sits
inside the car, and captures only the floor and the shadow on it.

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

## Custom shaders

A surface shader is a game's own fragment shader for the G-buffer pass. It decides
what a surface is made of (colour, metalness, roughness, normal, glow), and lighting,
shadows and post-processing treat the result like any other surface. A shader can't
change how light behaves, which keeps every material consistent and keeps the benefit
of deferred rendering.

```glsl
#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

void main() {
    Surface surface = materialSurface();
    surface.emissive += materials[draw.material].shaderParameters.rgb * sin(frame.time);
    writeSurface(surface);
}
```

`materialSurface()` gives the surface the material's textures and values describe, so
a shader only changes what it cares about. A shader can also read `inUv`, `inPosition`
and `frame`, and `shaderParameters` holds four numbers per material. A game compiles
the shader with `glslc`, with `src/Rendition/Shaders` on the include path, and passes
the result to `Renderer.CreateSurfaceShader`, then sets `Material.Shader`. Importing
`src/Shaders.targets` does the compiling. `src/Rendition.Sample/Shaders/glow_bands.frag`
is a working example, shown by `--glow`.

Each shader adds a pair of pipelines to the G-buffer pass, built while loading. Picking
one in `Render` is an array lookup, so it allocates nothing.

## Post-processing

Ambient occlusion darkens creases and contact areas by checking how much of the
nearby screen rises above each point. It reduces environment light, plus the
direct lights that have no shadow map of their own. It only knows what the camera
sees, so it can't tell that a car's underside hangs over the floor. To cover that, the
same pass searches a wide area of the shadow map for anything above an upward-facing
point. The result is a soft shadow under and around objects, and it costs no extra
render pass. It works only inside the area the shadow map covers.

Bloom has no brightness threshold. Every pixel gives away a small share of its
light, so only really bright things show a visible glow. That's physically closer
to how a lens scatters light, and nothing pops as a surface crosses a threshold.

Tonemapping uses Stephen Hill's fit of the ACES film transform rather than the
simpler Narkowicz curve. It works in ACES's own colour space, so very bright colours
fade towards white instead of shifting hue. `ACES_EXPOSURE` in `tonemap.frag` keeps
the scene as bright as the lighting was balanced for.

Anti-aliasing is temporal. The camera shifts by less than a pixel each frame, and each
pixel blends with where that surface was last frame, so many samples build up per
pixel. It also smooths the grain from soft shadows, ambient occlusion and rough
reflections. Colours are blended in a squeezed range so bright pixels don't dominate,
and history is pulled back into the range of nearby colours so old images don't trail
behind. It runs before bloom and tonemapping, on HDR colour. It replaced FXAA, which
couldn't remove shimmer.

`FrameView.Post` holds the look of the image: exposure, contrast and saturation in the
tonemap pass, then sharpening, vignette and film grain in the finish pass. Grain and
dither also hide the steps an 8-bit screen would show in slow gradients.

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
  documentation. The paint's metalness is set to 0.2 by eye. Its normal maps were
  measured: they use the green direction the renderer expects.
- The adventurer model doesn't use its normal map. The map creases the skin, most
  visibly on the face, whichever way up it's read, and adds almost nothing to the
  clothing, so its materials file sets `noNormalMap`.
- The importer doesn't read glTF emissive maps, cut-outs, glass or two-sided flags yet.
  Set those in the materials file. It flattens models into a single pose, so skeletons
  and animation aren't supported.
- The adventurer model has 929,000 triangles, which suits a showcase more than a game. A
  game character would need a reduced version and levels of detail.
- One shadow map covering a fixed area. Large outdoor scenes will need cascades
  that follow the camera.
- Only directional lights so far. Point and spot lights come later.
- Only one light casts shadows, so the others shine through objects, held back only
  by ambient occlusion.
- The environment is treated as infinitely far away, and ambient occlusion is all
  that blocks it.
- The ambient occlusion radius and strength, and the bloom strength, are constants in
  their shaders. A game will want to set them per scene.
- Temporal anti-aliasing follows the camera only, with no motion per object, so moving
  objects rely on the colour clamp and can show rough edges. Skinned or animated games
  will need motion vectors from the G-buffer pass.
- Screen-space reflections only know what is on screen, and read last frame's image.
  Glass still reflects the studio, not probes or traced rays.
- Probes are captured once and don't update. They affect reflections only, not
  soft diffuse light, and a scene gets at most four.
- Custom shaders replace only the G-buffer fragment shader. Shadows still use the
  material's standard cut-out, so a shader that discards extra pixels leaves the full
  shadow. Glass can't use one, and vertices can't be moved by one yet.
