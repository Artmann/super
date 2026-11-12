# JSON content

Put JSON in your project's `content` folder. Describe its shape with a C# record, then
load it. Comments and trailing commas are allowed.

```json
// content/items/axe.json
{
  "id": "axe",
  "name": "Iron Axe",
  "chopPower": 3,
  "model": "models/axe.glb",
}
```

```csharp
public record Item(string Id, string Name, int ChopPower, ModelRef Model);

class Armory(Content content)
{
    public Catalog<Item> Items { get; } = content.LoadAll<Item>("items");

    public Item StartingTool => Items["axe"];
}
```

Each file is one record, and every record needs a string `Id`. Fields are written in
camelCase. A field with a default value in the record is optional, and the rest are
required. A `ModelRef` is written as the model's [name](#names), such as `models/axe.glb`.

A `Rig` is written the same way, for a model that's animated. It's read through
`content.Rig`, so every record naming that model shares one rig, and it stays the same
object through a [reload](#change-it-while-the-game-runs):

```json
// content/heroes/knight.json
{ "id": "knight", "rig": "characters/knight.glb", "walkClipSpeed": 1.55 }
```

```csharp
public record Hero(string Id, Rig Rig, float WalkClipSpeed);

var walk = heroes["knight"].Rig.Clip("Walk");
```

A `Curve` is written as keys or as an easing, so a designer can shape motion in a file
and tune it while the game runs:

```json
// content/motions/rise.json
{ "id": "rise", "seconds": 0.8, "curve": { "keys": [{ "time": 0, "value": 0 }, { "time": 0.6, "value": 1.15 }, { "time": 1, "value": 1 }] } }
```

```json
// content/motions/drop.json
{ "id": "drop", "seconds": 0.5, "curve": { "easing": "bounceOut" } }
```

Keys out of order, or both keys and an easing, are reported like any other mistake.
See [Tweens and curves](tweens.md#curves-in-content).

A `Vector3` is written as a list of three numbers, such as `[1, 0, 2.5]`. A `Spline` is
written as points or as Bézier knots, so routes, rails and tracks can live in files:

```json
// content/routes/harbour.json
{ "id": "harbour", "start": [0, 0, 0], "path": { "points": [[0, 0, 0], [12, 0, 4], [20, 1, 0]], "closed": true } }
```

See [Splines](splines.md#splines-in-content).

## References between catalogs

To point at a record in another catalog, use `Ref<T>`:

```json
// content/recipes/axe.json
{ "id": "axe", "makes": "axe", "needs": ["stick", "stone"] }
```

```csharp
public record Recipe(string Id, Ref<Item> Makes, Ref<Item>[] Needs);

var made = items[recipe.Makes];
```

A reference to an id that doesn't exist is reported when loading, like any other mistake.
Records can also nest other records and lists, as the recipes in the
[Crafting example](../examples/Crafting/content/recipes) do.

## Mistakes are caught early

Bad data stops loading with a `ContentException` that lists every problem in every file:

```
items/axe.json:4: chopPower must be a number
recipes/axe.json:3: makes points at axes, but there is no Item called that
```

Unknown fields, missing fields, repeated ids and broken references are all reported. A
model field must name a model in the cooked content, so a typo, or a name that holds data
instead, is reported with the file and line:

```
items/axe.json:5: model models/ax.glb isn't in the cooked content. Check the name, including its case and extension.
items/axe.json:5: model items/axe.json is a data, not a model.
```

A rig must also name a model cooked with a skeleton:

```
heroes/knight.json:3: rig props/crate.glb was cooked without a skeleton, so it has no rig to animate.
```

A reference to a record that has mistakes of its own reports only those mistakes, not
the reference too, so one typo gives one line.

JSON that doesn't parse never gets this far: it fails the [cook](#cooking), which names
the file and line.

## Your editor can help

Run the game from source and Super writes a JSON schema for each catalog into
`content/.schemas`, which gives you autocomplete and red squiggles in VS Code. It goes in
the first content root the build lists, which is `content` beside the project unless you
list others. Folders starting with `.` are never cooked, so the schemas don't end up in
the game. Point a
file at its schema with a `$schema` field, which loading ignores:

```json
{ "$schema": "../.schemas/items.schema.json", "id": "axe" }
```

Or map a whole folder once, in your VS Code settings:

```json
"json.schemas": [
  { "fileMatch": ["**/content/items/**/*.json"], "url": "./content/.schemas/items.schema.json" }
]
```

The schemas help coding agents too: they say exactly which fields a record takes.

## Change it while the game runs

Run the game from source and saving a file updates it at the start of the next tick.

How it works: the build writes `content.dev.json` beside the cooked `content` folder,
naming your content roots and the `super-cook` tool. When the game window opens and finds
that file, it starts [`super-cook --watch`](command-line.md#watch-mode) over those roots,
cooking into the game's own `content` folder. Each save is cooked again, and when the
cooked `content.manifest` is written the game reads its catalogs again. The tool's output,
cook errors included, is printed to the game's error output, and you'll see `Content
reloaded.` there each time. The tool stops when the game closes.

If a save has a mistake, nothing changes in the game and the error is printed. JSON that
doesn't parse fails the cook, which keeps the old manifest. A record that parses but is
wrong, such as a number field holding text or a reference to an id that isn't there, fails
the reload: `Content not reloaded:` and the problems are printed, and the old data stays
in every catalog. A typo won't end your session; fix it and save again.

Live reload is off in headless runs, `--screenshot`, `--record`, `--profile`, and
wherever there's no `content.dev.json`, which includes every published game, so it can't
change their results.

A catalog stays the same object through a reload, so anything holding it sees the new
data. If you copied something out of a record, refresh it:

```csharp
items.OnChange(() => _axePower = items["axe"].ChopPower);
```

Files load when you call `LoadAll`, at startup or between levels, and after that they're
just C# objects. Going through a catalog with `foreach` is in file path order and doesn't
allocate.

## Images

Images for the interface are named like any other asset:

```csharp
GameImage icon = content.Image("icons/axe.png");
```

Its size comes from the cooked texture, so naming it doesn't decode it. A name that isn't
in the cooked content, or that isn't a texture, stops with a `ContentException`.

A game can also paint an image itself and show it like a cooked one:

```csharp
byte[] pixels = new byte[width * height * 4];   // rows from the top: red, green, blue, alpha
// ... paint ...
GameImage map = content.Image(width, height, pixels);
```

The pixels are sRGB with straight alpha, and are kept as they are, so don't change them
afterwards. Each call makes a new image: when the picture changes, paint a new one and let
the old one go with `content.Unload(image)`. Showing an image after it's unloaded fails, and
naming a cooked file again after unloading it makes a new one.

`image.ReadPixels()` gives an image's pixels in the same layout, to paint new images from,
such as a texture tiled under a painting. A cooked image is decoded afresh on every call, so
keep what it returns rather than asking every frame.

Images are cooked like textures, so they're block-compressed unless a rule turns
[`compressTextures`](#rules-and-sidecars) off. A compressed image is decoded when it loads
and shown from RGBA pixels, since an image's mips weigh each pixel by its alpha, so
compression saves space on disk but not memory. `ReadPixels` then gives the decoded pixels,
close to the source's but not the same.

## Cooking

A game never reads its sources. The build cooks every file under the content folder
into `bin/<config>/content`: a `content.manifest` naming each asset, and the cooked bytes
it points at. A texture several models use is kept once, however many models point to it. The game reads only that folder, beside the program. See
[`super-cook`](command-line.md#cooking-content) for running the cooker by hand, adding
more content roots, and the build targets. Cooked results are cached in `~/.super/cache`,
or in the folder named by the `SUPER_CACHE` environment variable, so a rebuild only cooks
what changed; deleting that folder is always safe. The cache is kept under 4 GB by deleting
what was used least recently, and `SUPER_CACHE_SIZE` changes that, such as `SUPER_CACHE_SIZE=8GB`
(see [the cache](command-line.md#the-cache)). The cooked objects are hard links to the
cache's copies when they can be, so they don't take the space twice.

A content root shared by several games can be cooked in part, so each game only gets what
it uses. `Only` on a `SuperContent` item takes globs split by semicolons:

```xml
<SuperContent Include="..\..\assets" Only="fox/**;props/rock_*/**" />
```

A model still reads the textures beside it when they're left out, but naming a file `Only`
left out fails like any name that wasn't cooked. A texture a model's `shaderTextures` names
is loaded by name, so the cook stops unless `Only` picks it too. See
[cooking part of a root](command-line.md#cooking-part-of-a-root).

Every file is an asset, chosen by its extension:

| Extension | Kind | Cooked form |
| --- | --- | --- |
| `.json` | data | The file itself, once it parses. It isn't rewritten, so line numbers in load errors still match |
| `.png` `.jpg` `.jpeg` `.tga` | texture | Block-compressed with every mip: BC7, sRGB unless set linear, or BC5 for a normal map. With `compressTextures` off, RGBA pixels whose mips are made when it loads |
| `.wav` `.ogg` `.mp3` | sound | The file itself, once it decodes |
| `.glb` `.gltf` `.fbx` `.obj` | model | `.rmodel`, through `Super.Cook.Models` |
| `.lightbake` | baked lighting | The same file with its pages block-compressed: BC6H irradiance, BC7 direction and mask; see [cooking a bake](#cooking-a-bake) |
| `.material.cs` | material | A [material graph](materials.md), compiled and run into `#color`, `#normal`, `#surface` and `#height` textures and a `#material` data part |

The longest matching extension wins, so `bricks.material.cs` is a material graph, not
code. A graph cooks at 1024 pixels unless a rule sets its `size`; see
[Material graphs](materials.md#size).

A file with no cooker fails the cook unless a rule turns cooking off for it. Files and
folders whose names start with a dot, `.bin`, `.md` and `.txt` are never assets.

### Names

A name is the path from the content root with forward slashes and the extension, such as
`models/fox.glb` or `sounds/pop.wav`. Names are case-sensitive on every OS. Naming
something that wasn't cooked stops at once, when it's named:

```
models/Fox.glb isn't in the cooked content. Check the name, including its case and extension.
```

If the name is right, check that the project's `Only` globs pick it.

Naming the wrong kind, such as a sound as a model, says what the name holds:

```
sounds/pop.wav is a sound, not a model.
```

### Rules and sidecars

A `cook.json` in any folder sets cook settings for everything below it. Each rule matches
a glob. A later rule beats an earlier one, and a deeper `cook.json` beats a shallower one:

```json
{
  "rules": [
    { "match": "**/*-normal.*", "texture": { "normalMap": true } },
    { "match": "masks/**", "texture": { "linear": true } },
    { "match": "notes/**", "cook": false }
  ]
}
```

Textures take `linear` (non-colour data, not read as sRGB) and `normalMap` (always
linear, and its mips stay unit length). These settings are why `content.Texture` takes
only a name: the kind is in the cooked texture. A file named `<source>.cook.json` beside
a source holds settings for that one file and beats every rule. Models keep their import
settings there; see [Rendering](rendering.md#models). An unknown setting is a cook error,
so a typo can't drop one silently. A sidecar whose source isn't there is only a warning,
`No source file <name>; skipped.`, so a clone without sources kept out of git, such as
licensed models, still builds. Neither `cook.json` nor `*.cook.json` is an asset.

Textures and [material graphs](materials.md) also take `compressTextures`, which is on by
default, like the [model setting](rendering.md#models) of the same name. The cook builds
every mip and block-compresses each one, so a texture takes a quarter of the memory or less
and loads without building mips. A texture becomes BC7, or BC5 for a normal map. A graph's
colour and surface become BC7, its normal BC5 and its height BC4. Each compressed texture
is kept in the cook cache by its pixels. Set it to `false` to keep every pixel exactly, such
as for pixel art:

```json
{ "rules": [{ "match": "pixel-art/**", "texture": { "compressTextures": false } }] }
```

BC7 fits each 4 by 4 block of pixels to one line of colours, so a block where two colours
cross, such as a sharp coloured edge in a small icon, comes out a little off.

Turn `compressTextures` off for UI images and pixel art. An [image](#images) is decoded
back to RGBA when it loads, so compressing it saves no memory, only disk, and costs its
edges. The cook can't tell which textures are only shown as images, since that's decided
by the code that loads them, so the Crafting example says so with a rule for its icons:

```json
{ "rules": [{ "match": "icons/**", "texture": { "compressTextures": false } }] }
```

A cooked normal map is BC5, which keeps only red and green, so a surface shader that reads
one rebuilds the third axis with `normalFrom`; see
[Surface shaders](rendering.md#surface-shaders).

A texture cooked here knows no alpha cutoff, so its smaller mips don't keep the share of
pixels that pass one, as a model's cut-out textures do. Nothing in the engine cuts a plain
texture out by its alpha, since a `Surface` has no cutoff, but a surface shader that
discards by one will see thin shapes such as leaves fade out at a distance. Put cut-out
textures in a model, whose `alphaCutoff` setting keeps their coverage.

### Parts

A model sidecar can list `parts`, each cooked from one node of the file and named with
`#`, such as `archer-props/firs.glb#tall`. A source with parts cooks only its parts.
Naming a part that isn't there lists the ones that are:

```
archer-props/firs.glb#tal isn't in the cooked content. Check the name, including its case and extension. archer-props/firs.glb has the parts: tall, young.
```

### Materials

A material graph is named with `content.Surface("materials/bricks.material.cs")`, which
gives a `Surface` with the graph's textures and its width in metres as `TileWidth`. Naming
it again gives the same surface without reading the material again, so it's fine to call
while drawing. Unloading one of its textures makes the next call read it afresh. It stops
with a `ContentException` for a name that has no `#material` part, or whose width isn't a
positive number. See
[materials.md](materials.md).

### Shaders on models

A model sidecar's material rule can name a surface shader with `shader`, its parameters with
`shaderParameters` and up to 4 cooked textures with `shaderTextures`. The model keeps the names,
and loading it loads the shader and the textures too, so a missing one is reported when the
model loads. See [A surface shader on a model](rendering.md#a-surface-shader-on-a-model).

### Wear and dirt on models

A model sidecar's material rule with `"bakeMasks": true` bakes curvature, occlusion and
height masks from the material's meshes into a texture the model points to, stored like
its other textures as a shared object, and its
`wear` and `dirt` layers, each an sRGB `color`, an optional `roughness` and an `amount`
from 0 to 1, are laid on by them. The masks go in the material's second UV set, which the
cooker makes when the file has none or its own isn't fit for a lightmap. See
[Wear and dirt](rendering.md#wear-and-dirt).

### Puddles on models

A model sidecar's material rule with `"puddles": true` lets water stand on the material
below the level the game sets with `view.Puddles`, and makes it damp for a band above.
It changes only a flag on the cooked material, so the textures are the same. See
[Puddles](rendering.md#puddles).

### Foliage and wind on models

A rule's `"foliage": true` marks a tree's leaves, which let 0.6 of the sunlight through from
behind; `"foliage": { "translucency": 0.85 }` sets that share, from 0 to 1, and
`"crown": false` keeps the model's own normals instead of pointing them out of one crown. A rule's
`"wind": true` sways the material in the breeze the game sets with `view.Wind`, and
`"wind": { "sway": 0.08 }` gives it a share of the lean, 0 or more. Each is `true`, `false`
or an object; a number or a value out of range stops the cook with the material's name, such
as `material leaf: foliage translucency 1.2 must be from 0 to 1.`. Both are kept on the cooked
material, so the textures are the same. See [Models](rendering.md#models) and
[Wind](rendering.md#wind).

### Mesh names

A rigged model keeps the name of each of its meshes: the name of the node that holds the
mesh in the source file, such as `Head_05b`, which is the name an artist sees in the
outliner. When a node has no name, the mesh's own name is used. A static model keeps them
only when its sidecar sets `"keepMeshes": true`, and is cooked again. These are not the
`#part` splits above. A split is cut when the file is cooked, and each one is a model of its
own. A mesh name is a mesh inside one model, which a game can show or hide on each draw.
`content.Meshes` picks them by name and gives a
[`MeshMask`](rendering.md#showing-some-meshes-of-a-model):

```csharp
MeshMask mask = content.Meshes(model, "Body_03a", "Head_05b");
```

A mesh with several materials is drawn in several pieces, and its name picks all of them.
Names match exactly, case included. A name the model doesn't have stops here with an
`ArgumentException` that lists the first 20 meshes it has, and their count:

```
hero.glb has no mesh called Helmet. It has 3 meshes: Body_03a, Head_05b, w_spear.
```

`content.MeshNames(model)` lists them once each, in the order they were cooked. The names
are read from the front of the cooked file, so picking them never reads the geometry, and
`content.Rig` keeps them when it reads the rig, so the file isn't opened again. A static
model cooked without `keepMeshes` has no names, so `content.Meshes` stops with an
`InvalidOperationException` that says why and what to do:

```
old.glb has no mesh names, because its file was cooked without them. A rigged model always has them. A static model needs "keepMeshes": true in old.glb.cook.json, and then to be cooked again.
```

A model file from before mesh names were kept is not a current model file, and fails with
the message to cook it again.

## Baked lighting

Bakes of a frame's [baked lighting](rendering.md#baked-lighting) will live in a `lighting/`
folder in the game's first content root. A space named with `view.BakedLighting("crypt", …)`
gets `lighting/crypt.lightbake`. A chunk named with `view.BakedChunk("house-garden", …)` gets
one file for each version of its contents, named by the 16 hex digits of their hash, such as
`lighting/house-garden/00000000000000ab.lightbake`. `BakeFiles.Space` and `BakeFiles.Chunk`
give these names. `Renderer.Bake` makes a space's bytes, and
[`--bake`](command-line.md#baking-lighting) writes them to this folder, but nothing reads the
files yet.

### Cooking a bake

A `.lightbake` file is cooked like any other asset, into the same container with smaller pages.
The irradiance pages become BC6H (unsigned half-float RGB, 1 byte a texel) and the direction and
mask pages become BC7, so a bake takes about a third of the space and of the video memory. The
probe atlases stay RGBA16F, since a probe's light is blended between texels, which a block would
smear. A page whose width or height isn't a multiple of 4 is left as the baker wrote it, and so
is a page that is already compressed, so cooking a bake twice changes nothing.

The header, with the hashes the bake is checked against, is written as it was read, so a cooked
bake is as fresh or as stale as the one it came from. A file that isn't a light bake this version
can read fails the cook with a message naming it.

BC6H drops alpha and has no negative numbers, so irradiance below 0.0001 is stored as 0.0001,
which is black to the eye. On the Gallery's bake the cooked irradiance is within about 1.3% of
the light on average. The cook of that bake took about four seconds. A cooked page of BC6H can't
be turned into RGBA8 pixels, which `CookedTexture.DecodePixels` says by throwing.

### What makes a bake stale

A static draw's bake id covers what the draw is made of, so a bake goes stale when any of it is
edited: a cooked model's file, the surface shaders its materials name (their compiled bytes)
and the textures those shaders read, and for a mesh made in code its vertices, surface,
textures and shader. Editing only a shader's source counts once it is compiled again.

### Code meshes drawn static

A mesh made in code that will be [drawn static](views.md#static-draws) is handed over with
`isStatic: true`:

```csharp
ModelRef rock = content.Mesh(mesh, isStatic: true);
```

That gives the mesh lightmap UVs once, there, with
[`WithLightmapUvs`](rendering.md#meshes-made-by-code), before it's uploaded. A mesh whose
`Uv2` already fits a lightmap, such as a shape's, is kept as it is; any other is unwrapped,
which may split its vertices, and an `InvalidOperationException` says so if its islands don't
fit even a 2048 page. Its bake id is made from the mesh with those UVs, so the same mesh made
with and without the flag gets different ids. Without the flag, a mesh is uploaded exactly as
given, and drawing it static throws on the next render, telling you to pass `isStatic: true`
to `Content.Mesh`. Built-in shapes and cooked models need no flag, since they always have
lightmap UVs.

## Shipping

`dotnet publish` copies the cooked `content` folder beside the program and leaves
`content.dev.json` out. That file is how a game knows it runs from source, so a game
without it knows it's shipped. There is nothing to add to the project for it.

## In tests

A test can state its sources next to its checks. `MemoryContent` cooks them in memory
with the built-in cookers and the material cooker the first time anything is read, so a
test sees what a game would, and JSON that doesn't parse fails with the cook's message:

```csharp
var files = new MemoryContent { ["items/axe.json"] = axeJson };
files.Add("icons/axe.png", pngBytes);
files.AddModel("models/axe.glb");   // models need files on disk to cook, so tests name them this way
using var run = HeadlessGame.Create<MyGame>(seed, services => services.AddContentFiles(files));

// Acts as if a file was saved, to test what your game does on a reload.
files["items/axe.json"] = strongerAxeJson;
run.ContentChanged();
run.Ticks(1);
```

A model added with `AddModel` can be named but not drawn, and has no rig, so a `Rig` field can't name it. To read a game's real content,
give the test a `CookedFolder` over its cooked output.

## Planned

Levels and saved games will be JSON too, which is handy early on because you can read
them and fix them by hand. Keep your state in plain objects, so the format can change.

```csharp
// Planned: not in Super yet.
saves.Write("slot-1", colony);
var colony = saves.Read<Colony>("slot-1");
```

Mods should be folders of JSON that add or replace entries by id, but only one content
folder can be read so far.
