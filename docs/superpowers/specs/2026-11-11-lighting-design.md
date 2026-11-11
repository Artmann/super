# Lighting design: live, baked and mixed light

Rendition lights everything at runtime today. Sun and local lights are direct and
shadowed, bounced light comes from a probe volume drawn at load (two bounces, sun and sky
only, with a hitch while it captures), and reflections come from SSR, up to four probes
and the sky.

This design gives Super lighting that works for both kinds of game it will be used for:
hand-built places like the Crypt and the Courtyard, and worlds generated at runtime from
pieces such as a house with its garden. It has three layers:

1. Live bounced light for the whole world, from probes that update a few at a time and
   follow the camera. Every game gets it, generated or not.
2. Baked lighting on top, for detail no probe grid can hold: soft contact shadows, light
   under furniture, a lamp lighting its own room. A hand-built place bakes as a space. A
   piece of a generated world bakes once as a chunk and is placed anywhere, turned any way,
   and relit by whatever sun and sky it lands under.
3. Mixed lights across both, so baked places still have flickering lights and real-time
   shadows from things that move.

## Why

The user wants scenes that look really good, and the cheapest way to film-like light in a
place that doesn't change is to compute it once. But the user also expects procedural
generation to be big for people using the engine, so bakes can't depend on a level being
built by hand before the game ships. That's why the live layer comes first and bakes are
built to travel with the pieces a generator places.

Scenes in Super are code, and the user ruled out moving them into files for a separate tool
to read. The design's answer is that the game is the scene file: the baker runs the game,
lets it draw as usual and bakes what it drew.

## Who can trace

Ray tracing is how bakes are made and how the live probes are best updated, but not every
player has it. The Steam hardware survey for October 2026, grouped by Vulkan ray query
support:

| Group | Share | Examples |
| --- | --- | --- |
| Can trace, dedicated GPU | 68% | every RTX card, RX 6000, 7000 and 9000 |
| Can trace, integrated | 0.6% | Radeon 780M, Intel Arc |
| Can't | 19% | GTX 1650, 1060 and 1660, RX 580, Intel UHD and Iris Xe |
| Unknown | 12% | "Other" (with the Steam Deck, which can trace slowly) and unnamed Radeon integrated graphics |

About a fifth of all players have an entry-level tracing card (RTX 3050, 4060 Laptop,
RX 6600 and similar), fine for a bake that runs once but tight for heavy tracing every
frame.

So: everything a player sees must work without ray tracing, and tracing makes it better
where it's there. Bakes made on a developer's machine ship as textures and need nothing
special to show. Live probes have a traced path and a rasterized one. Only baking on the
player's machine needs tracing, and without it the player sees live light instead.

## Goals

- Live bounced light everywhere, with point and spot lights, moving things and generated
  worlds, and no hitches. Traced where the GPU can, rasterized where it can't.
- Lightmaps for static geometry holding bounced light, sky light and the light of baked
  lights and glowing surfaces, with direction so normal maps still read.
- Baked chunks: a piece of a world baked once in its own space and placed many times at any
  position and angle, relit by the sun and sky it lands under. Many in one frame.
- Mixed lights: bounced light baked, direct light and shadows from moving things real-time,
  and a shadow mask so static shadows reach past the shadow maps and lights without a
  shadow tile still have shadows.
- No build step to remember. Running the game from source keeps its bakes fresh by itself;
  `--bake` and `--bake-check` exist for CI.
- Chunks the game generates on the fly can be baked on the player's machine as they appear,
  where the GPU can trace, cached on disk so the same world loads its bakes instantly next
  time.
- A stale bake is never shown. Without a fresh bake, the live layer lights everything.
- A Vulkan ray tracing layer (acceleration structures and ray queries) that the baker, the
  live probes and later traced reflections share.

## Non-goals

- Making a bake on a GPU without ray tracing. Those players get live light for anything
  not baked ahead of time.
- Lightmaps on skinned, swimming, wind-swayed or moving draws. They use the probes.
- More than one baked space per frame. (Chunks have no such limit.)
- Light from a neighbouring chunk in a chunk's bake. Live probes and real-time shadows carry
  it instead.
- Baked specular highlights from baked lights (later step), seam stitching across chart
  edges (later step), Intel Open Image Denoise.

## Anti-goals

These would make it a failure even if it technically works:

- A stale bake shown as if it were right, such as a wall moved in code still lit where it
  used to stand.
- A game that has to describe its scene a second time, or write a build hook, to get bakes.
- Any existing scene changing its look for the worse without opting in. The live layer
  replaces the load-time probes for everyone, and must look at least as good.
- A hitch: not while live probes update, not while a bake runs in development, not while a
  chunk bakes on a player's machine, not when a bake arrives (it fades in).
- Light counted twice: a baked light also lighting a lightmapped surface directly, or live
  probes adding bounce that a lightmap already holds.
- A game that only looks right on ray tracing hardware.

## What the developer writes

A hand-built place:

```csharp
public void Draw(View view)
{
    view.Camera(camera);
    view.Sun(sunDirection, sunColor);                       // Mixed by default
    view.PointLight(torch.Id, torch.Flame, torchColor * flicker, range: 9f, radius: 0.5f);
    view.PointLight(candle.Id, candle.Wick, candleColor * flicker, range: 1.4f, shadows: false);
    view.PointLight(sign.Id, sign.Glow, signColor, range: 3f, mode: LightMode.Baked);
    view.BakedLighting("crypt", min: new(-8f, -1f, -12f), max: new(8f, 6f, 12f), probeSpacing: 1f);
    view.Models(world);                                     // entities with Static join the bake
}
```

A generated world, where each house is a chunk:

```csharp
foreach (var house in village.Houses)
{
    view.BakedChunk("house-garden", house.Transform, min: new(-6f, 0f, -8f), max: new(6f, 7f, 8f));
    view.Models(house.Entities);                            // its static pieces, its lamp
}
```

Nothing else is needed. Every copy of the house with the same contents shares one bake,
wherever and however it's placed.

### Static draws

A draw marked static joins bakes. In Rendition that's a non-zero `DrawItem.BakeId`
(below). In Super it's the `Static` ECS component, picked up by `view.Models(world)`, and an
`isStatic` parameter on `view.Model(...)`. (The name doesn't clash with `Super.Physics`'s
`BodyKind.Static`, which is an enum value, not a type.) Static is a promise that the draw
never moves while it exists. A static model that is skinned or swims throws when drawn.
Parts of a static model that sway in the wind, like the Courtyard's ivy, are quietly left
out: they block and bounce light in the bake at their rest pose but take their own light
from the probes. (My call: the Courtyard is one model with swaying ivy, and throwing would
rule it out.)

See-through parts (blended materials and glass) never get lightmaps; they're drawn in the
transparent pass and lit as today. In a bake they let light through, tinted by their colour
and weakened by their opacity, so the Courtyard's room lamp still lights the yard through
its window. (My call.)

Static draws are matched to their lightmap region by a bake id, not by `Key`, so entity
ids can change between runs without a rebake. Rendition knows nothing about content
hashes, so the id is the caller's: `DrawItem.BakeId` is a `ulong`, and zero means not
static. Super computes it from everything that decides how the draw looks and blocks light:
the model's content hash (or a built-in shape's name, or a hash of the vertices of a mesh
made by code), the mesh, its material's values and texture hashes, the tint, and the
transform rounded to 0.1 mm. Inside a chunk the transform is taken relative to the chunk,
so the id doesn't change when the chunk moves. Someone using Rendition alone can bake by
giving their own ids. (My call.)

### Light modes

`LightMode` is `Realtime`, `Mixed` or `Baked`, on `view.Sun`, `view.PointLight` and
`view.SpotLight`. The default is `Mixed`.

| Mode | Over a fresh bake | Anywhere else |
| --- | --- | --- |
| `Realtime` | As today. Not in the bake: its bounce comes from the live probes | As today, bounce from the live probes |
| `Mixed` | Bounced light from the bake. Direct light real-time, shadowed by both the shadow mask and the real-time shadow maps | As `Realtime` |
| `Baked` | Everything from the bake, no work at runtime. No real-time shadows from moving things, no highlight | As `Realtime` |

Without a fresh bake every light behaves as `Realtime`, so the `Mixed` default changes
nothing for scenes that don't bake. (My call, following the rule that defaults should look
best.)

A light belongs to a chunk's bake when its position is inside the chunk's box; the sun never
does, since chunks are relit by it instead (below). Lights in a space's box, and the sun,
belong to the space's bake.

A mixed light's colour and brightness are left out of the bake's hash, so a flickering
torch or a sun that dims doesn't make the bake stale. Its bounced light keeps the colour it
was baked with. Its position, direction, range, radius and cone are in the hash (relative
to the chunk, for a chunk's lights). A baked light's colour is in the hash too.

Lights that animate need a rule, or one swinging lantern would keep the whole bake stale
forever. Each light with a key gets its own hash. A light whose hash changes between
frames, such as the Crypt's swinging lantern or a baked light whose colour flickers, is
animated: it's left out of the bake and behaves as `Realtime`, and the log says so once,
naming the light. The rest of the bake stays fresh. A light that settles again for a
second returns to the bake. (My call.)

`shadows: false` means no shadow-map tile, as today. Shadow-mask shadows still apply to such
a light, since they cost nothing at runtime. The Crypt's candles have no tiles but will now
cast shadows from the bake. (My call; a `bakedShadows: false` option can come later.)

### Baked spaces

`view.BakedLighting(name, min, max, probeSpacing)` names a space and its box: one fixed
place in the world, baked exactly as it stands, sun included. Static draws whose bounds
overlap the box get lightmaps. Every static draw in the frame, inside the box or not,
blocks and bounces light in the bake. The box also holds the space's baked probes, with the
same 4,096 limit as today, which replace the live probes inside the box. Naming two spaces
in one frame throws.

The bake is `lighting/<name>.lightbake` in the game's first content root.

### Baked chunks

`view.BakedChunk(name, transform, min, max)` names a piece of the world and its box in the
piece's own space. Static draws overlapping the box belong to the chunk. Only they block and
bounce light in its bake, since the chunk can't know what it will stand next to. A frame can
have up to 256 chunks. (My call.) A space and chunks can share a frame, but a draw belongs
to the space when it's in both.

A chunk can't bake the sun or the sky as they are, since it may be turned any way, in any
weather, at any time of day. So its bake splits light in two:

- Light that travels with the chunk is baked as plain light, as in a space: its own baked
  and mixed lights, its glowing surfaces, and how that light bounces inside it.
- Light from far away (the sky and the sun) is baked as transfer: for each texel, how much
  light arriving from each direction reaches it, directly or after bouncing, as L1
  spherical harmonics, one set per colour channel. At runtime the sky and sun are projected
  into the same form, rotated into the chunk's space and multiplied in. The sun's transfer
  holds only bounced light, since its direct light is real-time. (My call: L1 costs 12
  numbers a texel, about three times a space's lightmap, and holds the soft, low-detail
  light that bounces carry. L2 would cost 27.)

So the same house is lit right facing north or south, at noon or dusk. Chunks have no
probes of their own; moving things in a chunk use the live probes.

A chunk's bakes live in `lighting/<name>/<hash>.lightbake`, one per version of its
contents. A generator that varies a chunk's insides gets one bake per variant it produces.

## Live probes

The live layer replaces today's load-time probe volume. It works with or without baking.

- Shape: cascades of probe grids centred on the camera, finer near it (0.5 m, 1 m, 2 m and
  4 m spacing, 32 × 16 × 32 probes each, my call), scrolling as the camera moves so a big
  generated world is covered without a box around it. `view.LightProbes(min, max, spacing)`
  stays and adds a fixed grid on top for games that want one.
- Storage: the same octahedral irradiance and depth tiles and Chebyshev leak test as today.
- Updates: a fixed number of probes a frame, chosen by how stale they are and how close to
  the camera, blended in over a few frames so nothing pops. No hitches, ever.
- Traced, when the GPU can: each probe fires 128 rays a frame it updates. Hits are lit by
  the sun, the sky, every light with its shadow, glowing surfaces, and the probes themselves
  for bounces beyond the first, so light bounces as many times as it likes over a few
  frames.
- Rasterized, when it can't: each update captures a small cube at the probe, as today, but
  spread over frames instead of all at load, and now lit by point and spot lights too.
  Bounces build up the same way, from the probes.
- Over bakes: lightmapped pixels don't use live probes for diffuse light, so nothing is
  counted twice. Moving things in a baked space use the space's baked probes; in a chunk,
  the live probes.

`Graphics.LightProbes` keeps switching probes on and off, with `Graphics.ProbeQuality`
(`Low`, `Medium`, `High`) setting the probe count and update rate. The traced path is chosen
automatically when the GPU can trace. (My call: a player can't usefully choose between
them.)

## Lightmap UVs

Lightmaps need a second UV set where no two triangles share a texel. `Vertex.Uv2` already
exists, read from `TEXCOORD_1`, and `MaskBaker.Problem` already checks for overlaps.

- Models: the model cooker keeps a valid `Uv2` from the file. Valid means `MaskBaker.Problem`
  finds no overlap, every chart keeps a two-texel gutter at 512, and texel density varies by
  less than 4× across the mesh. Otherwise the cooker makes one and logs a note, cached
  through the step cache. Masks are baked after this step, onto whichever `Uv2` the mesh
  ends up with, so `bakeMasks` no longer needs an authored second UV set either.
- `Uv2` is checked and made per mask group, the parts of one material, which is what the
  mask baker bakes one mask for. Each group's `Uv2` is in its own 0–1, and two groups may
  overlap each other there. A group keeps the file's `Uv2` when it passes on its own, and
  otherwise only that group is unwrapped; the note names the groups that got new ones. This
  came from the Courtyard: its model is the whole scene, 829k triangles in 68 meshes, and
  its `TEXCOORD_1` lays out each material in 0–1 for its wear and dirt masks. One unwrap
  of the whole model made 45,667 charts that didn't fit a 512 page, and would have shrunk
  every material's masks to a sliver of it.
- A group whose charts don't fit at the reference size of 512 is unwrapped at 1024, then
  2048, before the cook fails. Its gutter is then two texels at that size, so its lightmap
  rectangle should be at least that big.
- The unwrapper is ours, in C#, in Rendition as `LightmapUnwrap`, so the cooker and meshes
  made by code share it. No native dependency. It splits the mesh into charts by face
  normal (a cone of about 60 degrees, and never across a hard edge), projects each chart
  onto its mean normal's plane, splits a chart again if its projection overlaps itself,
  scales charts by world area so texel density is even, and packs them with a skyline
  packer with a two-texel gutter at a reference size of 512.
- Built-in shapes get hand-made second UVs. Terrain uses its grid UVs, which never overlap.
  Meshes made by code are unwrapped when they are created static, which is fast enough for a
  generator to do at load.

## Ray tracing foundation

A `RayTracing` layer in `src/Rendition/Vulkan`:

- Device: `VK_KHR_acceleration_structure`, `VK_KHR_ray_query`,
  `VK_KHR_deferred_host_operations` and buffer device address, enabled when present.
  `Renderer.CanTrace` says whether they are. Silk.NET 2.23 has the bindings, and the device
  and glslc already target Vulkan 1.3.
- A bottom-level structure per mesh, one geometry per part, built on first use by a tracing
  feature rather than at load, so games that never trace pay nothing. Cut-out materials'
  geometries are not opaque, so a ray query can test alpha on candidate hits.
- A top-level structure built from a list of instances (transform, mesh, a per-instance
  index), rebuilt or refitted when the list changes. See-through geometries are marked so
  rays can pass through them with a tint. The live probes keep one for the frame's draws
  near the camera; each bake builds its own.
- Hit data: per geometry, the device addresses of its vertices and indices and its material
  index, so a shader can interpolate the hit's UVs and normals and read the material table
  and the bindless textures that `ResourceTable` already holds. Graph materials are already
  textures by then. Custom surface shaders can't run at a hit, so a hit uses their
  material's own colour textures and values. Effects added at draw time, such as world
  variation, wear and dirt masks and puddles, are also skipped at hits; they change bounced
  light too little to see. (My call.)
- A debug view, `--debug-view trace`, that traces primary rays from the camera and shows
  the hit's albedo. It proves the layer before anything depends on it, and stays as a
  developer tool.

## The bake

The baker runs on the GPU in compute shaders with ray queries. It takes what it was handed:
the static draws, the lights that belong to the bake, the environment, and the space or
chunk.

### Lightmap layout

Each static draw in the box, one part of one mesh instance, gets a rectangle sized by its
group's world surface area times `TexelsPerMetre` (default 20, so 5 cm texels, my call),
clamped to 8–1024 texels a side, packed with a skyline packer into pages. It's a rectangle
per draw rather than per instance because each mask group's `Uv2` fills its own 0–1 (see
Lightmap UVs), which the Courtyard's model needed. Parts of one instance that share a
material are one group with one 0–1, so their draws share one rectangle. A space gets up to
eight 2048² pages. A chunk packs into one page of its own size, at most 2048². The rectangle
becomes a scale and offset on `Uv2` for that draw, carried in the instance buffer. A
group's `Uv2` keeps two empty texels between charts only at the size it was made for, 512
unless the cooker had to unwrap it at 1024 or 2048, and the cooked model doesn't record
that size. So a group unwrapped above 512, like any rectangle smaller than its group's
reference size, ends up with a gutter under 2 texels, and the layout's dilation must cover
that.

### Texel setup

Each draw is rasterized into its rectangle with `Uv2` as the position, writing position,
geometric normal and shading normal per texel. Coverage counts a texel when any part of a
triangle touches it (conservative rasterization when the device has it, otherwise 8×
multisampling with a coverage test). A texel whose short probe rays hit mostly back faces
lies inside something and is marked invalid, to be filled from its neighbours.

### What is traced

A path tracer, one ray per texel per pass, accumulated in float32.

- Plain light: glowing surfaces (any static draw's emissive), the full light of baked
  lights, and the bounced light of mixed lights. A mixed light's first hit is left out,
  since the runtime draws it. For a space, the sky and sun also go into plain light.
- Transfer, for chunks: paths that escape the box add their throughput to the sky
  transfer's L1 coefficients in the direction they left. Paths that reach the sun's
  direction after at least one bounce add to the sun transfer.
- At each path vertex, next-event estimation picks among the lights that belong to the bake
  and reach the point, with soft shadows: a point light's sphere, a spot's disc and the
  sun's `LightAngle` cone are sampled, not their centres. Up to 16 lights in reach are all
  sampled; past that, 8 candidates are drawn weighted by unshadowed contribution and one is
  traced (resampled importance sampling). Glowing surfaces are found by rays hitting them,
  with no light sampling of their own.
- Up to 4 bounces, Russian roulette after the second.
- Direction, for plain light: the radiance-weighted mean incoming direction, stored as
  Unity's directional lightmaps do. At runtime the irradiance is
  `E * (dot(n, d) * 0.5 + 0.5) / max(1e-4, |d|)`, so a bumpy wall still shows its bumps.
  Transfer has direction built in.
- Shadow mask: for each mixed light with a channel, the share of the light's area the texel
  sees, from shadow rays only.
- Probes, for spaces: rays from each probe of the space's grid give radiance and distance
  per direction, folded into the same tiles the live probes use.

Quality has two settings. Preview takes 64 samples a texel, and Final 1024.

### Shadow mask channels

The mask has four channels. In a space the sun, when mixed, takes channel 0. Mixed point
and spot lights are given channels by greedy colouring of their overlap in range. A light
left without a channel keeps real-time shadows only, and the bake report names it. Chunks
have no sun channel; past the sun's cascades a chunk is lit by the sun unshadowed, as today.

### Filtering and edges

An edge-aware à-trous filter (4 passes) runs in lightmap space, guided by normal, position
and chart, never crossing a chart's edge. Then 8 rings of dilation fill the gutters and
invalid texels from their covered neighbours, as `MaskBaker` does, so bilinear filtering and
mips at an edge read the chart.

### The bake file

A header and Brotli-compressed pages:

- The input hash: the sorted bake ids of the static draws, the lights that belong to the
  bake by the rules above (animated ones left out), the box, `TexelsPerMetre`, the quality
  and the baker's version. A space's hash adds the environment's content (its bands and
  panels, not `SetEnvironment`'s version counter), the sun and the probe spacing. A chunk's
  doesn't, since it's relit. The hash is computed the same way at runtime, and is
  independent of culture and draw order.
- The draw table: each mesh instance's bake id, page, scale and offset.
- Pages: plain irradiance (RGBA16F), direction (RGBA8), shadow mask (RGBA8), and for a
  chunk three transfer pages (RGBA16F, one per colour, each holding four coefficients); the
  sun's transfer is stored as one shared set scaled by an RGB tint per texel. For a space,
  the probe atlases.
- The channel each mixed light took, and the bake report: lights without a channel, invalid
  texel share, sample count and time.

Bakes made ahead of time are meant to be committed. A new cooker in `Super.Cook` turns them
into cooked objects, compressing to BC6H and BC7 when the encoder supports them
(BCnEncoder.Net, which the model cooker already uses; check its BC6H support), and leaving
them uncompressed otherwise.

## At runtime

### Is the bake fresh?

`SceneRenderer` computes a space's or chunk's input hash whenever its static draws or
lights change, and loads the bake whose hash matches. Missing or stale, the area is lit by
the live layer and every light is real-time. A stale bake logs once, naming what changed
(draws, lights, environment, settings), so the developer knows why.

Chunk bakes are shared: a hundred copies of the house read one bake. Bakes in use are kept
in a pool of lightmap pages, loaded as chunks appear and let go when none use them.

### Surfaces

The G-buffer gains two targets, written only by lightmapped draws:

| Target | Format | Contents |
| --- | --- | --- |
| 4 | `R16G16B16A16_SFLOAT` | baked diffuse light: plain light after the directional term, plus for chunks the sky and sun transfer applied to this frame's sky and sun; alpha 1 when lightmapped |
| 5 | `R8G8B8A8_UNORM` | shadow mask, cleared to 1 |

The sky and sun go into the transfer as L1 coefficients computed once a frame, rotated per
chunk instance by its transform, and evaluated with the normal-mapped normal.

Target 4 uses the normal at G-buffer time, so normals that decals write afterwards don't
bend baked light; they still bend direct light and reflections. (My call: the alternative
is storing the direction per pixel, which costs another target.)

Lightmapped pixels take their diffuse soft light from target 4 instead of probes or sky.
Other pixels take it from the space's baked probes inside a space and the live probes
everywhere else. Specular soft light is unchanged: SSR, reflection probes, sky. Reflection
probes see lightmaps automatically, since they use the same passes, and bake freshness joins
`ReflectionLightingHash`.

### Lights

- Baked lights over a fresh bake are dropped from the frame's lights. They live in the bake
  only.
- Mixed lights shine as today. On a lightmapped pixel their shadow is the smaller of the
  shadow mask channel and the real-time shadow, so a moving thing still casts a shadow. A
  mixed light with no shadow tile uses the mask alone. In a space, past the sun's last
  cascade the mask takes over instead of everything counting as lit.
- Ambient occlusion: GTAO still darkens lightmapped pixels, at `LightmapOcclusion` (default
  0.5) of its strength, since the lightmap already holds large-scale occlusion. Specular
  occlusion is unchanged. (My call.)

### Switches

`Graphics.BakedLighting` (`RenderEffects.BakedLighting`), on by default. Off ignores bakes
and leaves everything to the live layer. `Graphics.BakeOnArrival` is described below.

## Baking without a build step

### In development

When the game runs from source (`content.dev.json` exists) on a machine that can trace, and
a frame names a space or chunk whose bake is missing or stale, the renderer starts a bake
of it in the background: preview first, then final. It runs a slice each frame within
`Graphics.BakeBudget` milliseconds of GPU time (default 4) so the game stays smooth, writes
the `.lightbake` to the source root when done, and the watch cook and content reload bring
it in, fading in over half a second when its hash matches. A small overlay line says
`Baking house-garden, 42%`. Changing the scene again restarts it, so hot reload of code that
moves a static draw rebakes on its own. Several waiting bakes run one at a time, nearest to
the camera first.

Auto-bake is off in `--screenshot`, `--record`, `--profile`, headless runs and published
games, like live content reload.

### On a player's machine

A chunk with no bake shipped for its contents, in a published game on a GPU that can trace,
is baked as it appears, at Preview quality within a budget of 2 ms a frame (my call). Until
it's done the chunk is lit by the live layer; then the bake fades in. Finished bakes go to a
cache in the game's local app data, keyed by input hash, capped at 512 MB with the least
recently used dropped first, so a world generated again from the same seed loads its bakes
from disk. (My calls, sized to the user's wish that caches stay small.)

`Graphics.BakeOnArrival`, on by default where the GPU can trace. Spaces are never baked on
a player's machine; a stale space in a shipped game is a bug that `--bake-check` catches.

### On the command line

| Option | What it does |
| --- | --- |
| `--bake` | Plays the game (with `--ticks`, `--script` and `--seed` as usual), bakes every space and chunk it draws at Final quality with no frame budget, saves the files and exits. A failed bake exits non-zero |
| `--bake-check` | Plays the same way and exits non-zero, listing each space or chunk whose bake is missing or stale |
| `--bake-quality preview\|final` | Overrides the quality for `--bake` |

A space or chunk the run never draws isn't touched. A generated game runs `--bake` with the
seeds it wants to ship bakes for; the rest are baked on arrival.

### In code

`Renderer.Bake(in FrameView, ReadOnlySpan<DrawItem>, BakeSettings)` returns the bake's
bytes, for games and tools that want to drive it themselves. The paths above call it.

## Examples

Two new examples show the work, alongside updated shots of the Crypt and the Courtyard.
Both are built from code with the built-in shapes and flat matte materials, so nothing but
light draws the eye: a light grey plaster for most surfaces, a few strongly coloured ones to
show light picking up colour as it bounces, and no textures beyond a faint noise so surfaces
don't look like plastic. Built-in shapes get hand-made second UVs.

Gallery has two real models, the only textured things in it: a marble bust (17,456
triangles, 0.5 m tall) and a bronze whale statue (3,816 triangles, 1.1 m tall), both from Poly
Haven at 2k, copied into the example's content from `D:\Assets`. Neither has a second UV set,
so they're the unwrapper's hardest test: a scanned, organic mesh with no help. Hamlet uses
only built-in shapes. Both examples use the same keys to compare:

| Key | What it does |
| --- | --- |
| `1` | Live light only (bakes off) |
| `2` | Bakes on (the default) |
| `3` | Direct light only, no bounced light at all, to show what bouncing adds |
| `P` | Shows the live probes as small spheres |

### Gallery: a hand-built space

A small gallery of plaster rooms, baked as one space. It's a tour of what baked and mixed
light can do, room by room along one corridor:

- The entrance hall has a long skylight. The sun is mixed and comes through in a sharp
  stripe that moves nothing, but its bounce fills the hall softly, and sky light falls off
  into the corners. The corridor beyond is longer than the sun's last shadow cascade, so the
  shadow mask carries the colonnade's shadows to the far end.
- The colour room has one red wall and one blue wall around a white floor and a marble bust
  on a plinth, lit from a window. The bust turns pink on one side and blue on the other, and
  its curves show how soft bounced light is.
- The whale room has a bronze whale statue under a skylight. Metal has almost no diffuse
  light, so the lightmap barely touches it; it shows the other half, reflections of the baked
  room through the reflection probes.
- The lamp room is dark but for a strip of glowing panels along the floor and three baked
  lamps with large radii, so its shadows are soft and wide. A brass sphere rolls back and
  forth through it, lit by the baked probes and casting real-time shadows from the one mixed
  lamp.
- The chapel has a swinging lantern (animated, so it stays real-time and the log says so)
  and a row of flickering mixed candles with no shadow tiles, shadowed by the mask.
- The courtyard at the end is open to the sky behind a glass screen, to show light passing
  through tinted glass.

The camera follows a slow path through the rooms, or can be flown. Shots compare the same
camera with live light only, with bakes, and direct only.

### Hamlet: a generated world

A village on a hill, generated from a seed: about forty houses on a terrain, each a chunk,
placed at any angle along winding paths. Four kinds of chunk: a cottage with a garden wall,
a workshop with a big open door, a round tower, and a well in a small paved yard. Each has
its own lamps inside and a few glowing windows. Cottages vary their insides (which wall the
bed, table and shelf stand against), so a seed produces a handful of cottage variants, each
with its own bake.

- A day passes in two minutes. The sun moves, the sky changes colour, and every chunk is
  relit by its transfer: the same cottage's yard is warm at dusk and blue at night, whichever
  way it faces. At dusk the lamps come on, mixed and flickering, and the windows glow.
- `R` makes a new village from a new seed. Chunks whose contents have a shipped or cached
  bake appear baked at once. New cottage variants appear in live light, bake on arrival with
  an overlay counting them down, and fade in one by one. Pressing `R` back to an earlier
  seed loads its bakes from the disk cache.
- A cart rolls along the paths through the village, lit by the live probes, to show moving
  things between chunks.
- The camera circles the village, then walks into a cottage to show light from its window
  and its lamp bouncing around the room.

The shipped bakes are made with `--bake --seed 1`; other seeds bake on arrival. On a GPU
without ray tracing, Hamlet still runs: seed 1 is baked, others are live light.

## Later

These reuse the ray tracing layer and get their own designs when their turn comes. They
follow the Lumen research.

1. Traced reflections where SSR misses, hits lit by the lights, the shadow maps and the
   probes.
2. Screen probes for fine indirect light near the camera, falling back to the live probes.
3. Baked highlights from the directional lightmap, and seam stitching.

## Order

1. Ray tracing foundation, with the trace debug view.
2. Live probes: cascades around the camera, rasterized updates spread over frames with
   point and spot lights, then the traced path. Replaces load-time capture for every
   example, with before and after shots.
3. Lightmap UVs: the unwrapper, the cooker's use of it, built-in shapes, meshes made by code.
4. The API with no bake behind it: `Static`, `LightMode`, `BakedLighting`, `BakedChunk`, bake
   ids, animated-light detection, the hashes, and the rule that a missing bake changes
   nothing.
5. The baker for spaces: layout, texel setup, path tracer, shadow mask, probes, filter and
   dilation, the file, `Renderer.Bake`, `--bake` and `--bake-check`.
6. The runtime for spaces: the cooker, freshness, G-buffer targets, lighting, shadow mask,
   baked probes, the switch.
7. Chunks: transfer in the baker, the page pool, many chunks a frame, relighting by sky and
   sun.
8. Auto-bake in development.
9. Baking on arrival, with the disk cache.
10. The examples: Gallery (built alongside step 5 so the baker has a scene from the start),
    Hamlet (alongside step 7), and the Crypt with mixed candles and torches and the Courtyard
    with a mixed sun and its lamp through the glass. Before and after shots at the same seed,
    ticks and size.

## How it's tested

The test rules in `CLAUDE.md` apply. Beyond unit tests for the unwrapper (no overlap,
gutter kept, even density), the packer, channel colouring, L1 rotation, and the hashes
(stable across culture and draw order, and for chunks across placement), there are GPU tests
on the machine's ray tracing:

- A plane under a uniform white sky of radiance L, nothing else: the baked irradiance is
  πL, within the noise of the sample count. The same plane as a chunk, turned 90 degrees,
  gives the same.
- A chunk with a wall on one side under a sky bright on one side only: turning the chunk
  moves the dark side, matching a space baked in that position within a tolerance.
- A closed box: no sky reaches inside, so the inside reads black with only sky light.
- A wall between a light and a floor: the shadow mask is 0 behind it and 1 in front.
- A mixed light's direct light isn't in the lightmap, and a baked light's is, measured on
  the same scene.
- A light that moves every frame is left out and the rest of the bake stays fresh.
- Light through a half-opaque pane reaches the floor behind it, weakened.
- Live probes: a red wall beside a probe tints it red after enough frames, on both paths,
  and a moved light changes it without any frame taking longer than its budget.

The docs change with the code: `rendering.md` gets "Bounced light" rewritten and "Baked
lighting", `command-line.md` the options, `design.md` the internals, `content.md` the
`.lightbake` files.

## Left to the builder

- The exact unwrap thresholds, packing heuristics and the filter's weights.
- Probe update counts per quality, and the scheduling of probe updates.
- How background bakes share the queue with the frame, as long as they stay within budget.
- How probe cascades blend at their edges.
- The overlay's look.
- Whether BLAS builds are compacted.
- Checking that BCnEncoder.Net encodes BC6H and that Silk.NET's KHR package has the ray
  query types; if not, fall back as described.
