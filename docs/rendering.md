# Rendering

Super draws with [Rendition](../src/Rendition/README.md), a deferred, physically based
Vulkan renderer. You don't call it directly: `Draw` describes the world on a `View`, and
Super hands that to Rendition each frame.

What it does for every game, with no setup:

- Cook-Torrance GGX shading with up to eight directional lights
- Soft, contact-hardening shadows, in up to four cascades
- A generated environment that gives surfaces reflections and soft fill light
- Screen-space reflections and box-corrected reflection probes
- Temporal anti-aliasing, ambient occlusion (with light bounced back from the surface's colour) and bloom
- Glass, car paint clearcoat and surfaces that glow
- Soft, lit [particles](views.md#particles) for dust and smoke
- Culling and simpler versions of distant models, for the camera and for shadows
- ACES filmic tonemapping, colour grading, sharpening, vignette, grain and dithering

## Lights

```csharp
view.Sun(direction: new Vector3(1f, 2f, 1f), color: new Vector3(3f));
view.Sun(direction: new Vector3(-1f, 0.5f, -1f), color: new Vector3(0.4f, 0.5f, 0.8f));
```

`direction` points towards the light. Colours are linear, so a bright light goes well
past 1. The first sun casts the shadows.

## Shadows

```csharp
view.Shadows(40f, cascades: 3);
```

The first number is how far from the camera the sharpest shadow map reaches, in metres.
Each extra cascade covers three times the radius of the one before, so a wide world keeps
sharp shadows near the camera. Orthographic cameras widen their shadows to cover the
screen.

## The look of the picture

```csharp
view.Finish(new PostSettings
{
    Exposure = 0.2f,     // stops: +1 is twice as bright
    Contrast = 0.1f,
    Saturation = 0.1f,   // -1 is black and white
    Vignette = 0.3f,
    Sharpen = 0.4f,
    Grain = 0.01f,
    Bloom = 0.1f,                              // more glow round bright things
    Lift = new Vector3(0.03f, 0.02f, 0.05f),   // lilac in the shade
    Gain = new Vector3(0.05f, 0.02f, -0.04f),  // cream in the highlights
});
```

Leaving a value at zero leaves that part unchanged. `Lift` adds colour to the dark parts
and fades out towards white, so shade takes a tint and white stays white. `Gain` makes
each channel that much brighter, by its share, so it shows most in the highlights.

## The sky

Without a sky the world sits in a plain grey studio. `view.Sky` gives it a gradient from
the ground up through the horizon to straight up, which shows behind everything, lights
the scene from all around and shows in reflections:

```csharp
view.Sky(new SkySettings
{
    Zenith = new Vector3(0.18f, 0.36f, 0.85f),   // straight up
    Horizon = new Vector3(1f, 0.62f, 0.32f),
    Ground = new Vector3(0.22f, 0.2f, 0.13f),    // below the horizon
    Brightness = 0.9f,                           // 0 counts as 1
    HazeStart = 25f,                             // metres
    HazeEnd = 110f,
});
```

Haze fades things into the sky behind them, from nothing at `HazeStart` to all sky at
`HazeEnd`, so the far edge of a world melts away instead of stopping. Leave both at
zero for no haze. A negative brightness, or haze that ends nearer than it starts, throws.

`HazeFromFocus` measures the haze across the ground from the point the camera looks at
instead of from the camera. Use it with a far-off camera, such as an isometric one: from there everything on
screen is about the same distance away, so haze from the camera fades everything evenly.
Haze from the focus leaves the middle clear and fades the edges of the view. `HazeColor`
sets what things fade into; zero fades them into the sky behind them, which for a camera
looking down is the dark ground below the horizon.

Changing the colours rebuilds the sky's lighting, which takes a moment, so keep them
steady rather than animating them every frame. [Water](#water), when set, takes over the
sky. The [Archer example](../examples/Archer) uses a golden-hour sky.

## Water

`view.Water` puts the camera underwater, with light absorbed by depth, moving caustics on
sunlit surfaces and shafts of light:

```csharp
view.Water(new UnderwaterSettings
{
    Absorption = new Vector3(0.075f, 0.026f, 0.018f),   // per metre; red should be largest
    ShallowColor = new Vector3(0.11f, 0.42f, 0.55f),
    DeepColor = new Vector3(0.01f, 0.09f, 0.17f),
    SurfaceHeight = 30f,
    CausticStrength = 2.2f,
    CausticScale = 0.11f,
    ShaftStrength = 0.11f,
    ShaftDistance = 55f,
});
```

Leaving `Absorption` at zero means there's no water. The [Reef example](../examples/Reef)
uses this.

## Models

Models are cooked from FBX, glTF or OBJ into Rendition's `.rmodel` format by the model
cooker in `Super.Cook.Models`, through the asset pipeline. Its settings are the ones
below, read from the model's `<file>.cook.json` sidecar, and a `parts` entry cooks named
pieces of one file, each as `file#part`. [`super-cook`](command-line.md#cooking-content)
runs it, and so does a build that imports `Super.targets`. The game reads only the
cooked content.

A game names a model by its source's [name](content.md#names), the path from the
content root:

```csharp
readonly ModelRef coral = content.Model("reef/coral/coral.glb");
```

A name that wasn't cooked, or that isn't a model, stops with a `ContentException` when
it's named.

Naming a model starts loading it. A model drawn before it's ready is waited for, so
nothing pops in; to show a loading screen instead, see
[Game states](states.md#loading-screens). `ModelRef.Cube` and the other
[built-in shapes](#built-in-shapes) need no file.

The sidecar sets the model's size and fixes materials by name. Each rule
applies to every material whose name contains `match`:

```json
// models/fir.glb.cook.json
{
  "targetLength": 6.0,
  "materials": [
    { "match": "bark", "roughness": 0.9 },
    { "match": "leaves", "alphaCutoff": 0.5, "foliage": true }
  ]
}
```

`alphaCutoff` cuts out pixels whose opacity is below it. Opacity is the colour texture's
alpha times the surface texture's alpha, so leaves with alpha in their colour texture
need nothing else, and a glTF file's own `MASK` cutoff is read without a rule. The
import fails if the cutoff would cut out nothing, or everything, instead of quietly
giving you solid cards.

`foliage` is for a tree's leaves. It points their normals out of the crown so it's lit
as one rounded shape instead of speckled cards, lets sunlight through the back of the
leaves, and draws both sides. Every material the rule matches counts as one crown, so
give the trunk its own material.

Textures are looked for beside the source file, then in any folder under it, as
Poly Haven's downloads keep them in `textures`. Some plants ship their leaf cut-outs
as a separate grey image beside a colour JPEG with no alpha; name it with `alpha`, and
its red becomes the colour texture's opacity. A mesh without UVs whose material uses a
texture warns, since every read lands on one texel; an untextured mesh needs no UVs. A
texture a rule names that can't be found stops the cook; one the model file links to only
warns. A texture path must match the file's name exactly, case included, even on Windows,
so a model that cooks there cooks on every OS:

```json
// props/fern_02/fern_02.gltf.cook.json
{
  "node": "fern_02_a",
  "materials": [
    { "match": "fern_02", "alpha": "fern_02_alpha_1k.png", "alphaCutoff": 0.5, "foliage": true }
  ]
}
```

`node` keeps only what's under the nodes whose name contains it, for files that hold
several variants side by side, as Poly Haven's plant sets do. The cook fails if no
node with meshes matches. It's not for rigged models.

To use several pieces of one file, list them as `parts`. Each part is cooked as its own
model, named `file#part`, and takes every setting written beside `parts` unless it sets
that one itself. A file with parts cooks only its parts:

```json
// props/dandelion_01/dandelion_01_1k.gltf.cook.json
{
  "materials": [
    { "match": "dandelion_01", "alpha": "dandelion_01_alpha_1k.png", "alphaCutoff": 0.5, "foliage": true }
  ],
  "parts": {
    "a": { "node": "dandelion_01_a" },
    "b": { "node": "dandelion_01_b" }
  }
}
```

```csharp
var flowers = new[] { content.Model("props/dandelion_01/dandelion_01_1k.gltf#a"), content.Model("props/dandelion_01/dandelion_01_1k.gltf#b") };
```

The textures a model uses are read while it cooks, so they don't need to be assets of
their own. Turn cooking off for them with a rule in a [`cook.json`](content.md#rules-and-sidecars),
as the shared `assets` folder does for Poly Haven's `textures` folders:

```json
// assets/cook.json
{
  "rules": [
    { "match": "**/textures/**", "cook": false },
    { "match": "reef/**/*.png", "cook": false }
  ]
}
```

Other options are the `baseColor`, `normal`, `surface` and `emissive` textures,
`color`, `emissiveColor`, `metallic`, `roughness`, `clearcoat`, `twoSided`, `transparent`,
`opacity`, `noNormalMap` and `rotateY`; see `src/Super.Cook.Models/ImportSettings.cs`.

A rigged model keeps its skeleton, bone weights and clips, and `animations` adds clips
from other files of the same rig, such as Mixamo's:
`"animations": [{ "file": "walk.fbx", "name": "Walk" }]`. See [Animation](animation.md).

A colour passed when drawing a model tints it and keeps its textures, so one tree model
can be drawn in many autumn shades at no extra cost.

Draw a model as many times as you like. After culling, the renderer draws copies of one
part that look the same (same level of detail, material, tint, ghost and swim) as one
instanced draw, so a forest of one tree or a grid of tiles costs a few draws, not
thousands. Only where each copy stands can differ. A different tint splits copies into
separate draws but still works. Skinned models are drawn one at a time.

The Archer, Fox and Reef examples share their models through the `assets` folder at the
top of the repository, which each lists as a content root. Only `assets/cook.json`,
the sidecars and its README are in git; the models themselves aren't, because of their
licenses or their size ([CREDITS.md](../CREDITS.md)). [assets/README.md](../assets/README.md)
says where each one comes from and the name to save it under. Without them the examples
still build: the cook warns `No source file <name>; skipped.` for each sidecar whose model
is missing, and the game reports a missing model when it names it.

## Ghosts

`view.Ghost` draws a model as a preview, such as a building the player is about to
place: green where it fits and red where it doesn't.

```csharp
var fits = new Vector3(0.2f, 1f, 0.3f);
var blocked = new Vector3(1f, 0.15f, 0.1f);
view.Ghost(PreviewKey, cabin, Matrix4x4.CreateTranslation(cursor), canPlace ? fits : blocked, opacity: 0.6f);
```

A ghost leaves out the model's own colours, textures and tint, and is one matte colour
that glows a little, so it reads the same over a dark model or a light one, in sun or
in shadow. Its shape still shows through the light. The colour is linear, from 0 to 1.
Opacity is 1 by default, and below 1 the world shows through, as with glass. It must be
more than 0 and at most 1. A ghost casts no shadow.

## Built-in shapes

![Every built-in shape at the back, and shapes made with Shapes at the front](media/shapes.webp)

Every game has eight ready shapes that need no file and no loading, handy for trying out
an idea: a capsule as the player, spheres as resources, cylinders as trees. Each is one
unit wide and centred on zero, and comes out light grey unless the draw gives a colour.

```csharp
view.Model(1, ModelRef.Capsule, Matrix4x4.CreateTranslation(player.Position + Vector3.UnitY), playerColor);
```

| Shape | Size |
| --- | --- |
| `ModelRef.Cube` | 1 × 1 × 1 |
| `ModelRef.Sphere` | 1 across |
| `ModelRef.Capsule` | 1 wide, 2 tall, standing up |
| `ModelRef.Cylinder` | 1 wide, 1 tall, standing up |
| `ModelRef.Cone` | 1 wide at the base, 1 tall, pointing up |
| `ModelRef.Plane` | 1 × 1, lying flat and facing up. One-sided |
| `ModelRef.Quad` | 1 × 1, standing up and facing along +z. One-sided |
| `ModelRef.Torus` | 1 across, with a tube 0.25 thick, lying flat |

They can't be unloaded, and `content.IsReady` is always true for them.

For other sizes, more or fewer segments, or a surface of their own, `Shapes` builds the
same shapes and more as a `GeneratedMesh` to hand to `content.Mesh`:

```csharp
ModelRef pillar = content.Mesh(Shapes.Cylinder(topRadius: 0.3f, bottomRadius: 0.4f, height: 3f));
ModelRef tile = content.Mesh(Shapes.Hex(radius: 1f, height: 0.2f, new Surface { Color = grass }));
ModelRef rock = content.Mesh(Shapes.Flat(Shapes.Sphere(0.5f, segments: 7, rings: 5)));
ModelRef floor = content.Mesh(Shapes.Plane(new Vector2(20f, 20f), surface: tiles, uvPerMetre: 0.5f));
```

| Call | Makes |
| --- | --- |
| `Shapes.Box(size)` | A box of that width, height and depth |
| `Shapes.Sphere(radius, segments, rings)` | A sphere. Segments go round the middle, rings from pole to pole |
| `Shapes.Hemisphere(radius, segments, rings)` | The top half of a sphere, closed underneath, with its base at zero |
| `Shapes.Capsule(radius, height, segments, rings)` | A capsule. The height is end to end, at least twice the radius; rings are per end |
| `Shapes.Cylinder(topRadius, bottomRadius, height, segments)` | A cylinder closed at both ends. A top radius of 0 is a cone |
| `Shapes.Cone(radius, height, segments)` | A cone standing on its base |
| `Shapes.Plane(size, subdivisions)` | A one-sided square on x and z, facing up, split into squares |
| `Shapes.Quad(size)` | A one-sided rectangle on x and y, facing along +z |
| `Shapes.Torus(radius, tube, segments, sides)` | A ring lying flat. The radius is to the middle of the tube, which must be thinner |
| `Shapes.Prism(sides, radius, height)` | A column with that many flat sides. The radius is to a corner |
| `Shapes.Hex(radius, height)` | A six-sided tile with corners along x, for hex maps |
| `Shapes.Flat(mesh)` | Any mesh with hard edges: each triangle lit as one flat face, for a low-poly look |

Every shape is centred on zero apart from the hemisphere, faces outwards, and has normals,
tangents and UVs, so normal maps work on it. Curved shapes are smooth, while the box,
prism and hex have sharp edges. Every call also takes a `Surface`, which defaults to matte
grey. UVs stretch once across each part, such as each face of a box or the side of a
cylinder, unless `uvPerMetre` is given; then they count metres, so a tiled texture is the
same size on every shape whatever its size. Round shapes measure round their widest point.
A surface from `content.Surface` (a material graph) tiles at its graph's width when
`uvPerMetre` is not given, and `uvPerMetre` wins when it is. Ribbons, tubes and extrusions
follow the same rule. A `uvPerMetre` or `TileWidth` must be a finite number above zero, or
the shape throws an `ArgumentOutOfRangeException` naming it.

## Meshes made by code

A mesh built in code, such as ground from a height function, is handed over with
`content.Mesh`. Its `Surface` says how it looks: a colour, roughness and metalness,
optional colour, normal and surface textures, and optionally a surface shader with its
own parameters and textures.

```csharp
var mesh = new GeneratedMesh(vertices, indices, new Surface
{
    Color = new Vector3(0.8f, 0.3f, 0.2f),
    Roughness = 0.6f,
});

ModelRef rock = content.Mesh(mesh);
```

Textures come from a cooked image (PNG, JPEG or TGA), or from pixels you made. Whether a
cooked one is a normal map or linear data is a cook setting, set by a
[`cook.json` rule](content.md#rules-and-sidecars) such as
`{ "match": "**/*-normal.*", "texture": { "normalMap": true } }`, so code names only the file:

```csharp
TextureRef bark = content.Texture("textures/bark.png");
TextureRef bump = content.Texture("textures/bark-normal.png");
TextureRef noise = content.Texture(new GeneratedTexture(256, rgbaPixels), TextureKind.Linear);
TextureArrayRef ground = content.TextureArray(sand, grass, rock, snow);
```

A mesh or texture you're done with is let go with `content.Unload`, which frees it after
the frames in flight. `content.IsReady(model)` says whether a mesh is on the GPU yet, so a
game can skip one still loading instead of waiting. A texture is freed after every mesh
that names it; a texture array keeps its own copy of each layer, so the single layers can
go as soon as the array is made.

## Meshes along a spline

`Shapes` can also build meshes that follow a [spline](splines.md), such as a road, a
river, a rail or a cable:

```csharp
Vector2[] bank = [new(3.8f, -1.6f), new(3f, 0f), new(-3f, 0f), new(-3.8f, -1.6f)];
ModelRef road = content.Mesh(Shapes.Extrude(path, bank, step: 1f, surface: tarmac));
ModelRef river = content.Mesh(Shapes.Ribbon(path, width: 6f, surface: water));
ModelRef rail = content.Mesh(Shapes.Tube(path, radius: 0.05f, sides: 8));
ModelRef deck = content.Mesh(Shapes.Bend(Shapes.Plane(new Vector2(4f, 20f), subdivisions: 40), path));
```

| Call | Makes |
| --- | --- |
| `Shapes.Extrude(spline, outline, closedOutline, smooth, step)` | Sweeps an outline along the spline |
| `Shapes.Ribbon(spline, width, step)` | A flat strip facing up, centred on the spline |
| `Shapes.Tube(spline, radius, sides, step)` | A round pipe with smooth sides |
| `Shapes.Bend(mesh, spline)` | Bends a generated mesh so its +z runs along the spline |

The outline is in metres, seen looking along the spline from its start: x to the right
and y up. Each edge faces to the right of the way it runs. So an outline that goes
anticlockwise faces outwards, and a flat road listed from right to left faces up.
`closedOutline` joins the last point back to the first. Edges are sharp across the
outline unless it's `smooth`, as for a pipe. The ends are left open. A ring of vertices
goes every `step` metres. UVs are in metres: u runs across the outline and v runs along
the spline, scaled by `uvPerMetre`, which is 1 unless the surface has a `TileWidth`.

`Bend` measures along the spline from the back of the mesh (its lowest z). Each vertex
keeps its x and y across the spline, so the mesh keeps its length. Past the end of an
open spline the mesh carries on straight. Only vertices move, so give the mesh plenty of
them along z, such as a `Shapes.Plane` with many subdivisions.

An outline folds over itself where the spline turns tighter than the outline is wide.
Keep half the width narrower than the tightest turn. Only generated meshes can be bent;
a loaded model can't be.

## Terrain

`Super.Terrain.Heightfield` builds chunk meshes from a height function on a shared grid,
so neighbouring chunks meet exactly:

```csharp
var field = new Heightfield((x, z) => Noise.Hills(x, z) * 30f, spacing: 1f);

var ground = content.Mesh(field.Chunk(chunk.X, chunk.Z, quads: 32, new Surface
{
    Shader = content.Shader("terrain"),
    Parameters = [new Vector4(snowHeight, snowFade, rockSteepness, rockFade)],
    ShaderTextures = [splat],
    ShaderArrays = [groundColors, groundNormals],
}, uvPerMetre: 0.125f));
```

`field.Height(x, z)` and `field.Normal(x, z)` answer gameplay questions about the same
ground. The [Terrain example](../examples/Terrain) loads chunks around the camera and
unloads the ones left behind.

## Surface shaders

A surface shader is your own fragment shader for the G-buffer pass. It decides what a
surface is made of (colour, metalness, roughness, normal, glow), and Rendition's
lighting, shadows, reflections and post-processing treat the result like any other
surface.

```glsl
// Shaders/pulse.frag
#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

void main() {
    Surface surface = materialSurface();
    surface.emissive += materials[draw.material].shaderParameters[0].rgb * sin(frame.time);
    writeSurface(surface);
}
```

`materialSurface()` gives the surface the material's textures and values describe, so a
shader only changes what it cares about. A shader can also read `inUv`, `inPosition`,
`inNormal`, `inTangent` and `frame`.

| From the `Surface` | In the shader |
| --- | --- |
| `Parameters`, up to 8 `Vector4` | `materials[draw.material].shaderParameters[i]` |
| `ShaderTextures`, up to 4 | `shaderTexture(slot, uv)` |
| `ShaderArrays`, up to 4 | `shaderLayer(slot, uv, layer)`, where a fractional layer blends two |

Unset slots read white. To compile shaders, put them in a `Shaders` folder in your game
project and import the targets file, which runs `glslc` from the Vulkan SDK:

```xml
<Import Project="..\..\src\Shaders.targets" />
```

Then name the shader by its file name:

```csharp
var surface = new Surface
{
    Shader = content.Shader("pulse"),
    Parameters = [new Vector4(1f, 0.4f, 0.1f, 0f)],
};
```

`examples/Terrain/Shaders/terrain.frag` blends four ground layers by height, slope and a
splat texture. More detail is in [design.md](design.md#custom-shaders).

## Using Rendition alone

Rendition knows nothing of Super and can be used directly, with your own loop and
window. Its [README](../src/Rendition/README.md) covers the API and the sample, and [design.md](design.md#rendition-design) explains the frame structure.
