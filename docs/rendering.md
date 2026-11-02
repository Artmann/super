# Rendering

Super draws with [Rendition](../src/Rendition/README.md), a deferred, physically based
Vulkan renderer. You don't call it directly: `Draw` describes the world on a `View`, and
Super hands that to Rendition each frame.

What it does for every game, with no setup:

- Cook-Torrance GGX shading with up to eight directional lights
- Soft, contact-hardening shadows, in up to four cascades
- A generated environment that gives surfaces reflections and soft fill light
- Screen-space reflections and box-corrected reflection probes
- Temporal anti-aliasing, ambient occlusion and bloom
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
});
```

Leaving a value at zero leaves that part unchanged.

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

Models are cooked from FBX or glTF into Rendition's `.rmodel` format by the import tool.
It takes the source file, a JSON file describing its materials, and where to write the
result:

```
dotnet run -c Release --project tools/Rendition.Import -- assets/source/reef/coral/coral.glb assets/reef-coral.materials.json assets/cooked/reef/coral.rmodel
```

A game names a model by its path inside `assets/cooked`:

```csharp
readonly ModelRef coral = content.Model("reef/coral.rmodel");
```

Naming a model starts loading it. A model drawn before it's ready is waited for, so
nothing pops in; to show a loading screen instead, see
[Game states](states.md#loading-screens). `ModelRef.Cube` is built in and needs no file.

The materials file sets the model's size and fixes up materials by name. Each rule
applies to every material whose name contains `match`:

```json
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
give the trunk its own material. Re-import a model to get it.

Textures are looked for beside the source file, then in any folder under it, as
Poly Haven's downloads keep them in `textures`. Some plants ship their leaf cut-outs
as a separate grey image beside a colour JPEG with no alpha; name it with `alpha`, and
its red becomes the colour texture's opacity:

```json
{
  "node": "fern_02_a",
  "materials": [
    { "match": "fern_02", "alpha": "fern_02_alpha_1k.png", "alphaCutoff": 0.5, "foliage": true }
  ]
}
```

`node` keeps only what's under the nodes whose name contains it, for files that hold
several variants side by side, as Poly Haven's plant sets do. The import fails if no
node with meshes matches. It's not for rigged models.

Other options are the `baseColor`, `normal`, `surface` and `emissive` textures,
`color`, `emissiveColor`, `metallic`, `roughness`, `clearcoat`, `twoSided`, `transparent`,
`opacity`, `noNormalMap` and `rotateY`; see `tools/Rendition.Import/ImportSettings.cs`.

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

Cooked assets aren't kept in git. The Reef models have their own license terms
([CREDITS.md](../CREDITS.md)); with them in `assets/source/reef`, cook each one using the
matching `assets/reef-*.materials.json`, then run `examples/Reef`.

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

Textures come from a PNG in the content folder, or from pixels you made:

```csharp
TextureRef bark = content.Texture("textures/bark.png");
TextureRef bump = content.Texture("textures/bark-normal.png", TextureKind.Linear, normalMap: true);
TextureRef noise = content.Texture(new GeneratedTexture(256, rgbaPixels), TextureKind.Linear);
TextureArrayRef ground = content.TextureArray(sand, grass, rock, snow);
```

A mesh or texture you're done with is let go with `content.Unload`, which frees it after
the frames in flight. `content.IsReady(model)` says whether a mesh is on the GPU yet, so a
game can skip one still loading instead of waiting. A texture is freed after every mesh
that names it; a texture array keeps its own copy of each layer, so the single layers can
go as soon as the array is made.

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
window. Its [README](../src/Rendition/README.md) covers the API, the sample and the
import tool, and [design.md](design.md#rendition-design) explains the frame structure.
