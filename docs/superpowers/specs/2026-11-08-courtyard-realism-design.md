# Courtyard realism design

The Courtyard is the example that shows the material graphs and the renderer at their
best, and it still reads as a game from around 2010: clean, evenly lit, saturated,
everything placed rather than weathered in. A review of the scene listed about a
hundred things to improve. This design sorts them into what the engine must learn, what
the graphs must do better, and what the scene must add, and decides how each is done.

Reference: the review is reproduced in the plan's header. The current frame is
`docs/media/courtyard-light.webp` (right half).

## What the review asks for, and what already exists

The inventory came from `docs/rendering.md`, `docs/materials.md`, `docs/design.md`, the
shaders in `src/Rendition/Shaders` and the Courtyard content.

| Review item | Today | Decision |
| --- | --- | --- |
| Global illumination, light bounced from the brick wall | None. Ambient diffuse is one 32² irradiance cube baked from three sky colours. Reflection probes exist in Rendition (static, 4, box-corrected) but give no diffuse light and Super has no API for them | A light probe volume captured at load, with depth for leak-proofing, as in DDGI. Super gets `view.LightProbes` and `view.ReflectionProbe` |
| Ambient occlusion in corners and under objects | Alchemy AO, 24 taps, radius and strength hard-coded, plus "overhead occlusion" from the shadow map. No bent normals or specular occlusion | Replace with GTAO: horizon search, bent normals, specular occlusion, with radius and strength set from the view |
| Contact shadows under barrel, wheel, crates, grass | None. PCSS at `sunSize` 0.02 gives sharp shadows but the shadow map's 2048 texels spread over 7 m miss the last centimetre | Screen-space contact shadows for the sun, 0.25 m long |
| Softer, more diffused shadows | PCSS with blocker search, 24 taps, one sun. Cascades 2048² fixed | Keep PCSS. Add `Graphics.ShadowResolution`. The sun size stays a scene choice |
| Reflections from wet surfaces, sky in the puddle | SSR with probe and sky fallback, roughness fade 0.225–0.45. Puddle is a flat modelled sheet at roughness 0.02 | The puddle becomes a water layer the shader puts on the ground where it lies below a level, with ripples, depth tint and a damp band. SSR already reflects the wall; probes fill what SSR can't see |
| Exposure varies between sun and shade | Fixed manual EV plus a constant ACES exposure | Histogram auto exposure with adaptation, `Exposure` becomes compensation |
| Tone mapping, less oversaturated orange, subtle grading | Hill ACES fit, parametric contrast, saturation, lift and gain | AgX as a second tonemapper, plus temperature and tint. The bricks' graph also loses saturation |
| Physically accurate exposure, atmospheric depth | Distance haze towards the sky colour only. The sky is three colours | A Preetham analytic sky with a sun disc that also feeds the environment bake, and exponential height fog with sun in-scatter |
| Depth of field, bloom around lights | No DOF. Bloom is thresholdless, 5 levels | Add a thin-lens DOF at half resolution. Bloom stays |
| Better anti-aliasing, higher texture resolution | TAA with Halton jitter and variance clamp, no motion vectors, no render scale. Graphs cook up to 4096, the Courtyard uses 2048 | `Graphics.RenderScale` to 2.0 for supersampled shots. Bricks and flagstones cook at 4096 |
| Normal and height maps, micro detail | Done: height from graphs, POM 8–32 steps, detail graphs, Toksvig roughness mips | Keep. Give parallax a self-shadow term is out of scope |
| Dirt at the foot of walls, water stains, rust runs, dirt under props | Hand-placed quads a millimetre in front of the wall, flagged `dithered`. They replace the normal and roughness underneath | Real projected decals that blend colour, roughness and normal with their own weights, placed from a JSON list the Blender script writes |
| Mud transitions, wetness round the puddle, darker soil | Mud graph bakes dark glossy hollows. Nothing responds to the puddle | The water layer's damp band darkens and smooths any surface flagged `puddles` by world height, so the mud, flagstone edges, barrel foot and plank all get wet together |
| Moss and algae in damp areas, weathering under the window | Moss is baked in the graphs by height only | Decals for the broad patches, the graphs for the fine ones |
| Worn edges, dents, dirt in crevices on props | Graphs can only read their own height. No mesh curvature or occlusion | The model cooker bakes curvature, occlusion and height above the base into a mask texture, and material rules add `wear` and `dirt` layers that read it |
| Vegetation: species, leaf shape, translucency, wind | Blades are strips with one of two colours. Ivy leaves are flat stars. Translucency is a constant 0.6. No wind | Leaf and blade graphs with opacity, cut out with `alphaCutoff`, in several species and sizes. Per-material translucency. Vertex wind for anything flagged `wind` |
| Prop materials: grain, dents, uneven colour, weathered hoops, fabric weave | Wood is one tone with rings; hoop iron is rust patches; burlap is a weave | Reworked graphs: board tones by `FloodFill`, knots, checks, silvering by a wear mask, rust runs below the hoops, stains and a patch on the sacks |
| Brick variation, chipped edges, mortar depth, efflorescence, soot | Per-brick tilt, colour and hardness, chipped edges, spalls, mortar parallax | Wider and less saturated hue spread, white efflorescence, sooting under the string course, deeper and more uneven mortar, a roughness spread of 0.55–0.95 |
| Cobwebs, insects, footprints, broken bricks, tiny plants | Boot prints, rubble and leaves exist | Cobweb cards in the window's corners, weeds in the cracks, more debris. Insects are out of scope |

What the review asks for that is deliberately not done: ray-traced reflections, voxel GI,
photogrammetry textures (the point of the scene is the graphs), motion blur (nothing
moves), and more objects.

## The engine features

### Light probe volume

`view.LightProbes(min, max, spacing)` asks for a grid of probes over a box. Rendition
captures each probe with the existing cube capture at 32² per face, with the sun, sky
and emissive surfaces but no point lights, and folds each capture into an 8×8 octahedral
irradiance tile and a 16×16 octahedral depth tile holding the mean and the mean of
squares of the distance to the nearest surface. Tiles sit in two atlases with a one-texel
border for bilinear wrap. A capture round runs for up to 64 probes a frame over the first
frames, and a second round after the first so light bounces twice. The lighting shader
blends the eight nearest probes with trilinear, back-face and Chebyshev visibility
weights, after offsetting the point along its normal and towards the eye by 0.2 of the
spacing. With a volume present the probes replace the sky irradiance for diffuse ambient
light. Specular stays with SSR, reflection probes and the sky cube. The volume is
recaptured when the sun, the sky or the box changes, and at most 4,096 probes are allowed.

### GTAO

The AO pass becomes ground-truth ambient occlusion: 2 slices of 8 steps a pixel, rotated
by interleaved gradient noise per frame, radius and strength from
`view.AmbientOcclusion(radius, strength)`, defaults 0.6 m and 0.9 as now. The pass
writes the bent normal with the visibility, and lighting uses the bent normal to sample
the probes and Lagarde's specular occlusion for the environment and probes.

### Contact shadows

For the shadowed sun, the lighting shader marches 12 steps along the light in screen
space over `ShadowSettings.ContactLength` metres, default 0.25, and darkens the sun by
any hit thicker than 0.02 m, faded over the last quarter. It runs only inside the first
cascade's radius and is off when the length is 0.

### Sky, fog, exposure, tone, focus, scale

- `SkySettings.Turbidity` above 0 switches the sky and the environment bake to a
  Preetham model lit by the view's sun direction, with a sun disc the size of
  `sunSize`. `Zenith`, `Horizon` and `Ground` are then ignored and `Brightness` scales
  the result.
- `SkySettings.Fog` is exponential height fog: `Density` per metre at `Height`, falling
  off by `Falloff` per metre of height, tinted by the sky in the view direction and
  brightened towards the sun by `SunScatter`. It is applied to opaque and transparent
  surfaces and to the sky.
- `PostSettings.AutoExposure` meters a 64-bin log-luminance histogram of the lit frame,
  takes the mean of the 40th to 95th percentile and adapts towards it at
  `AdaptationSpeed` stops a second. `Exposure` then adds to it as compensation. A fixed
  timestep run adapts the same way every time, so screenshots stay repeatable.
- `PostSettings.Tonemapper` is `Aces` (default) or `Agx`. `Temperature` and `Tint` move
  the white point before tone mapping.
- `PostSettings.FocusDistance` and `Aperture` (f-number, 0 is off) with `FocalLength`
  in millimetres on a 36 mm sensor give a thin-lens circle of confusion. The blur is a
  half-resolution scatter-as-gather bokeh of 48 taps, near and far apart, composed back
  by circle size.
- `Graphics.RenderScale` from 0.5 to 2.0 sizes every scene target; the finish pass
  resamples to the window.

### Decals

`view.Decal(key, Matrix4x4 box, Surface material, DecalBlend blend)` projects the
material's textures down the box's local −Y onto whatever is inside the unit box. A
decal pass runs after the G-buffer: it copies the normal target, then draws each box
reading depth to find the surface point, discards outside the box, fades by
`blend.AngleFade` where the surface faces away, and writes colour, roughness and normal
each weighted by the material's alpha times `blend.Color`, `blend.Roughness` and
`blend.Normal`. The normal is laid over the copied one with reoriented normal mapping.
Up to 256 decals a frame. The Blender script writes the Courtyard's decals as a JSON
catalog the game reads, so the scene keeps one source of truth.

### Puddles and wetness

`view.Puddles(level, band)` sets a world-height water level. A material rule
`puddles: true` makes a surface compare its height, world Y plus its height map times
its depth, with the level. Below it the surface is water: a flat normal with procedural
ripples from `frame.time`, roughness 0.03, metalness 0, the surface's colour darkened and
browned by depth, and SSR as usual. Within `band` metres above it the surface is damp:
colour squared, roughness halved, blending to dry at the top of the band. The ground,
the flagstones, the plank, the barrel's foot and the wheel's rim all take the rule, so
the wet reads across everything that touches the puddle.

### Model and foliage rules

- Sidecar rules take `shader`, `shaderParameters` and `shaderTextures`, so a mesh in a
  glb can wear a surface shader.
- `alphaCutoff` on a graph material reads the graph's opacity, so a leaf graph can cut
  out a leaf.
- `foliage` takes `{ translucency }` in place of the constant 0.6.
- `wind: true` sways vertices by their height above the mesh's lowest point, driven by
  `view.Wind(direction, strength, gustiness)`, in the G-buffer and shadow passes alike.
- `bakeMasks: true` makes the model cooker bake, for that material's meshes, a mask
  texture with curvature in red, occlusion in green and height above the mesh's base in
  blue, into a 1024² texture over the mesh's own UVs. `wear: { color, roughness, amount }`
  tints and smooths convex edges, and `dirt: { color, roughness, amount }` darkens
  occluded and low places. Both are blended in the surface shader from the mask, so they
  work on Sketchfab models as well as graph ones.

## The scene

The lighting is rebuilt on the new features: a Preetham sky at turbidity 2.5 with the
same sun direction, a probe volume over the yard at 1 m spacing, auto exposure with
−0.3 stops of compensation, AgX with a touch of warmth, fog density 0.004, a focus on the
window at f/8, and contact shadows. The graphs are reworked for range and age, and the
Blender script adds leaf and blade cards, weeds, cobwebs, nails, more debris and the
decal list. Before and after shots of the whole frame and four crops (wall, puddle,
props, vegetation) go in `docs/media/` at the same seed, ticks and size.

## Order

1. Light that bounces: GTAO, contact shadows, probe volume, reflection probes in Super,
   multiscatter GGX, shadow resolution.
2. Sky, air and camera: Preetham sky, height fog, auto exposure, AgX and white balance,
   render scale, depth of field.
3. Surfaces that remember weather: shader rules, alpha from graphs, decals, puddles,
   foliage translucency and wind, baked wear and dirt masks.
4. The graphs: bricks, stone and flagstones, ground, wood and iron, cloth, lime and glass,
   leaves and blades.
5. The scene: the Blender script, the decal list, the lighting, the docs and the media.

## Not now

- Ray tracing, voxel or screen-space GI beyond the probes.
- Motion vectors and motion blur.
- Tessellation or displacement.
- Parallax self-shadowing.
- Photo-scanned textures in the Courtyard.
- A grass scattering API; the scene scatters in Blender and instancing takes care of
  the rest.
