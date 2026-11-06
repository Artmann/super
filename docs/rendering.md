# Rendering

Super draws with [Rendition](../src/Rendition/README.md), a deferred, physically based
Vulkan renderer. You don't call it directly: `Draw` describes the world on a `View`, and
Super hands that to Rendition each frame.

What it does for every game, with no setup:

- Cook-Torrance GGX shading with up to eight directional lights, and up to 1,024 point
  and spot lights with soft shadows
- Soft, contact-hardening shadows, in up to four cascades
- A generated environment that gives surfaces reflections and soft fill light
- Screen-space reflections and box-corrected reflection probes
- Temporal anti-aliasing, ambient occlusion (with light bounced back from the surface's colour) and bloom
- [Glass](#glass) that bends and frosts what's behind it, car paint clearcoat and surfaces that glow
- [Height maps](#height-and-detail-maps) that sink mortar and grooves into a flat surface, and detail maps for fine grain
- Skin, wax and jade that [let light spread under the surface](#skin-and-wax)
- Soft, lit [particles](views.md#particles) for dust and smoke
- Culling and simpler versions of distant models, for the camera and for shadows
- ACES filmic tonemapping, colour grading, sharpening, vignette, grain and dithering

## Graphics settings

Everything above is on by default, and vsync keeps the frame rate at the display's, often 60.
A game that must run fast on modest hardware turns off what it doesn't need:

```csharp
Game.StartWindowsGameLoop<MyGame>(new GameOptions
{
    Title = "My game",
    Graphics = new Graphics { AmbientOcclusion = false, Reflections = false, MaxFramesPerSecond = 144 },
});
```

`Graphics.Fastest()` turns every effect and vsync off, a start to turn back on only what
the game needs. Ask for `Graphics` in a constructor to change it while the game runs; the
next frame follows, so it can back an options menu.

| Setting | Off, or set |
|---|---|
| `Vsync` | Frames come as fast as the machine draws them, and fast movement may tear. `--no-vsync` turns it off too |
| `MaxFramesPerSecond` | The most frames drawn a second. 0, the default, is no limit. It saves power, and with vsync off it holds a steady rate |
| `SunShadows` | Nothing is drawn into the sun's shadow maps, often the biggest saving. Ambient occlusion still darkens nooks |
| `LightShadows` | Point and spot lights shine through walls |
| `AmbientOcclusion` | Corners and creases are as bright as open ground |
| `Reflections` | Shiny surfaces and water reflect only the sky and reflection probes, not what's on screen |
| `AntiAliasing` | Edges show their pixels, and moving things stay sharp |
| `Bloom` | No glow round bright things. `PostSettings.Bloom` only changes how much there is |

An effect turned off skips its work on the GPU, not only its look. In the Crypt, the
GPU time of a frame halves:

![Crypt with every effect on, 4.4 ms of GPU a frame, and with Graphics.Fastest, 2.0 ms](media/graphics-fastest.webp)

Screenshots, recordings and profiles keep vsync and ignore the frame limit, so they come
out the same on every machine, but they do follow the effect switches. Turning
`AntiAliasing` on or off waits for the GPU to finish once, so do it from a menu rather than
every frame.

## Lights

```csharp
view.Sun(direction: new Vector3(1f, 2f, 1f), color: new Vector3(3f));
view.Sun(direction: new Vector3(-1f, 0.5f, -1f), color: new Vector3(0.4f, 0.5f, 0.8f));
```

`direction` points towards the light. Colours are linear, so a bright light goes well
past 1. One sun casts the shadows: the first, unless you pick another with
`shadows: true`:

```csharp
view.Sun(new Vector3(-1f, 0.5f, -1f), new Vector3(0.4f, 0.5f, 0.8f));
view.Sun(new Vector3(1f, 2f, 1f), new Vector3(3f), shadows: true);
```

Marking two suns in one frame throws.

### Point and spot lights

![The Crypt example: torches, a lantern and 246 candles](media/crypt.webp)

```csharp
view.PointLight(torch.Id, torch.Flame, color: new Vector3(3.2f, 1.5f, 0.5f), range: 9f, radius: 0.5f);
view.PointLight(candle.Id, candle.Wick, new Vector3(0.12f, 0.06f, 0.02f), range: 1.4f, shadows: false);
view.SpotLight(lantern.Id, lantern.Position, direction: -Vector3.UnitY, new Vector3(6f, 5.5f, 4.5f),
    range: 7f, innerAngle: 0.25f, outerAngle: 0.4f);
```

A point light shines every way from a spot, like a candle or a torch, and a spot light
shines in a cone, like a lantern's beam. The colour is how bright the light is a metre
away. It dims with the square of the distance and fades smoothly to nothing at the
range, so a small range is cheaper and keeps a candle's light on its own table. A spot
is full inside its inner angle and fades out by its outer one. Both angles are measured
from the direction, in radians, and the outer one must be under a right angle.

A light also has a radius, the size of its glowing part, 0.1 m unless you pass
`radius:`. No surface gets much brighter than it would be that far from the light. On
its own, the square of the distance makes a torch a hand from a wall blow the stone out
white while the room stays dim. A bigger radius calms the wall but leaves the light
just as bright a metre away and beyond, so raise it for lights in brackets, sconces and
lamps against walls. The Crypt's torches use 0.5. A bigger light also casts softer
shadows. The radius must be less than the range.

![A torch on a pillar with the default radius of 0.1 and with 0.5](media/torch-radius.webp)

Draw lights in `Draw` like everything else, each frame, so a torch can flicker by
changing its colour. The key names the light from frame to frame, the same as a model's
key, and keeps its shadow steady as the camera moves. Use the ID of the thing holding it.

Lights cast soft shadows unless `shadows: false` is passed. Shadows are sharp where an
object touches the ground and blur further away, as the sun's do. All the lights share
one shadow atlas, and a light that is bigger on screen gets a sharper shadow. A point
light costs six times as much shadow as a spot. When the atlas is full, the lights that
are smallest on screen lose their shadows. Shadows are what cost the most, so leave
them off on small lights, such as rows of candles.

A frame can have up to 1,024 lights, and one more throws. Lights are culled to what the
camera sees, and each part of the screen only works through the lights that reach it, so
a frame can have hundreds of small lights. 256 can reach any one spot on the screen,
and more than that are dropped there. The `Crypt` example has four torches, a lantern
and 246 candles.

Point and spot lights light models, glass and [particles](views.md#particles). Glass and
particles get no shadows from them. They aren't seen in reflection probes.

## Shadows

```csharp
view.Shadows(40f, cascades: 3);
```

The first number is the radius of the sharpest shadow map, in metres. Each extra cascade
covers three times the radius of the one before. The cascades are laid along what the
camera sees: the sharpest one starts at the camera, and each next one picks up where the
last ends, so no part of a map is spent behind the camera. A cascade keeps its size as the
camera turns and moves in whole shadow texels, so shadow edges don't shimmer or crawl.

Where two cascades meet, points hand over to the coarser one gradually, so there's no line
where the sharpness changes. Shadows fade out over the last part of the furthest cascade
instead of stopping at an edge.

An orthographic camera centres every cascade on its target instead, and widens its
shadows to cover the screen.

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

`keepMeshes` keeps each mesh of a static model as its own part, named by the mesh, instead
of merging the meshes that share a material. It's `false` by default, so a static model has
one part per material and no part names. Set it to `true` to [show some of its parts](#showing-some-parts-of-a-model).
Rigged models always keep their meshes.

`keepOrigin` keeps the file's own origin. The model is still turned by `rotateY` and scaled by
`targetLength`, but about that origin: it isn't centred or stood on the ground, so it stays where
the file put it. Use it when the origin means something, such as a character whose file also holds
a few hundred alternative weapons, heads and shields spread around. Those would move the bounds,
and the character would end up shifted by an amount you can't predict. A rigged model's skeleton
stays in the same place as its mesh. It's `false` by default.

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

### Showing some parts of a model

A rigged model keeps one part for each mesh in its source file, named by the mesh, so a
rigged character can show its body and head without the helmet. A static model keeps
those parts too, but only with `"keepMeshes": true` in its sidecar, since by default its
meshes that share a material merge into one part with no name. Cook the model again after
adding it. Pick the parts by those names with `content.Parts` once, and keep the mask.
Each draw then shows only those parts. This is different from the `#part` files above,
which are separate models cut at cook time:

```csharp
PartMask mask = content.Parts(model, "Body_03a", "Head_05b");
view.Model(key, model, transform, mask);
```

A mask fits only the model it was made for, so make it from the model you draw. See
[Part names](content.md#part-names) for the rules and the error when a name isn't there,
and [Showing some parts](views.md#showing-some-parts) for how a draw uses it.

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
`color`, `emissiveColor`, `metallic`, `roughness`, `clearcoat`, `subsurface`, `twoSided`,
`transparent`, `opacity`, `ior`, `noNormalMap` and `rotateY`; see `src/Super.Cook.Models/ImportSettings.cs`.
`color`, `emissiveColor`, `metallic`, `roughness`, `clearcoat`, `subsurface`, `heightDepth`, `detailScale`, `detailMask`, `twoSided`,
`transparent`, `opacity`, `noNormalMap` and `rotateY`; see `src/Super.Cook.Models/ImportSettings.cs`.

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

## Glass

![Glass panes before coloured posts: a clear one, a frosted one and a green one, with glass balls bending the floor and dust rising between the panes](media/glass.webp)

A surface below full opacity is glass. Its colour shows by its opacity, and the rest
lets through what's behind it. Roughness frosts it, blurring what's seen through, and
`Ior` bends it. Leave `Ior` at 1 for a thin pane, which hardly bends light, and use
about 1.5 for solid glass such as a marble or a bottle.

```csharp
var window = new Surface { Color = new Vector3(0.75f, 0.85f, 0.95f), Roughness = 0.05f, Opacity = 0.12f };
var frosted = new Surface { Color = new Vector3(0.9f), Roughness = 0.45f, Opacity = 0.2f };
var marble = new Surface { Color = new Vector3(0.95f), Roughness = 0.02f, Opacity = 0.08f, Ior = 1.5f };
```

A cooked model's material is glass with `"transparent": true` and an `opacity`, and
bends with `"ior": 1.5` in its [material rule](#models).

Glass and particles are drawn together, furthest first, so a pane in front of dust
covers it and dust in front of a pane covers that. Glass sees the scene through a copy
of the picture, made again before a pane that something drawn since overlaps, at most
four times a frame. Past that, overlapping glass still blends but doesn't bend or
frost. What's behind is bent once, at the near side, and followed half a metre at
most, so solid glass looks right but isn't exact. Glass casts a lighter shadow, by how
much light it lets through, and even clear glass keeps a quarter of its shadow, as
bent light spreads. Glass reflects the sky, not probes or screen-space reflections.
The `Glass` example shows each kind, and `--flat` draws it all without bending or
frosting.

## Skin and wax

Light goes a little way into skin, wax, jade and marble before it comes back out, so
shadow edges on them are soft and warm instead of sharp and grey. A `Subsurface` on the
material turns this on:

![Skin, wax and jade, each plain on the left and with subsurface scattering on the right](media/wax.webp)

```csharp
var skin = new Surface
{
    Color = new Vector3(0.85f, 0.6f, 0.48f),
    Subsurface = new Subsurface
    {
        Radius = 0.012f,                     // metres
        Color = new Vector3(1f, 0.4f, 0.25f), // red travels furthest
        Transmission = 0.3f,                 // light from behind through thin parts
    },
};
```

`Radius` is how far light travels under the surface, in metres: about 0.01 for skin, a
few centimetres for wax. `Color` is how far red, green and blue each go, as fractions of
the radius; leave it out and they all go the same distance. `Transmission`, from 0 to 1,
lets light from behind shine through thin parts such as ears or a candle's rim, tinted by
the colour and by `Color`.

A model sets it per material in its cook settings:

```json
{ "match": "skin", "subsurface": { "radius": 0.01, "color": [1, 0.4, 0.25], "transmission": 0.3 } }
```

Subsurface can't be combined with `clearcoat` or `foliage`. The renderer refuses such a
material and the cook stops with an error naming it. Under water the light isn't spread.
When no material in the world scatters light the blur doesn't run, so it costs nothing.
The [Wax](../examples/Wax) example puts each kind beside a plain twin.

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

## Meshes made by code

A mesh built in code, such as ground from a height function, is handed over with
`content.Mesh`. Its `Surface` says how it looks: a colour, roughness and metalness,
optional colour, normal and surface textures, opacity and IOR for [glass](#glass), and
optionally a surface shader with its own parameters and textures.

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

## Height and detail maps

A height map sinks the low parts of a flat surface, such as the mortar between bricks or
the joints of a street, so they move with the view as deep ones would instead of looking
painted on. It's a linear texture where white is the top and black is `HeightDepth`
metres down. Pair it with the normal map of the same height, which shades the grooves.

A detail map adds grain finer than a surface's own textures, such as grit on stone or the
weave of cloth, without a bigger texture. `DetailColorTexture` is linear and multiplies
the colour, with mid grey (128) leaving it as it is, so a detail colour around mid grey
keeps the surface's own colour on average. `DetailNormalTexture` is a normal map laid
over the surface's own normal. Both repeat `DetailScale` times (8 if left out) for each
time the surface's textures repeat. With `DetailMask`, the surface texture's blue
channel says how strongly the detail shows, from none at 0 to full at 1.

```csharp
TextureRef height = content.Texture("textures/bricks-height.png");
TextureRef grit = content.Texture("textures/grit.png");
TextureRef gritNormal = content.Texture("textures/grit-normal.png");

ModelRef wall = content.Mesh(Shapes.Box(new Vector3(16f, 3f, 0.4f), new Surface
{
    ColorTexture = bricks,
    NormalTexture = bricksNormal,
    HeightTexture = height,
    HeightDepth = 0.025f,
    DetailColorTexture = grit,
    DetailNormalTexture = gritNormal,
    DetailScale = 6f,
}, uvPerMetre: 1f / 0.9f));
```

A cooked model takes the same textures and values in its sidecar, as `height`,
`heightDepth`, `detailColor`, `detailNormal`, `detailScale` and `detailMask` on a material
rule. These textures are cooked as linear data, and `detailNormal` as a normal map.

The depth shows up close: it fades out between 15 and 25 metres from the camera, where
the normal map alone looks the same and costs less. Keep `HeightDepth` to a few
centimetres. Deeper looks stretched at low angles, because the surface's edge and its
shadows stay flat. Transparent surfaces ignore height and detail maps. The [Bricks
example](../examples/Bricks) shows a wall and a street; P turns the height maps off and D the detail.

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
the spline, scaled by `uvPerMetre`.

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

A shader that moves its UVs can still use the material: `materialUv()` is `inUv` moved by
the material's height map, and `materialSurface(uv)` reads the material at any UV.
`parallaxUv(slot, uv, depth)` does the same depth trick with a shader texture as the height
map, and `blendNormals(base, detail)` lays one tangent-space normal over another.

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
