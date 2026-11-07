# Material graphs design

A game today gets its materials from downloaded textures. The Crypt's README asks you to
fetch nine files from Poly Haven and run ffmpeg on three of them. That's fine for one
example, but it doesn't scale, and you can't change what you downloaded: there's no
slider for more moss, wider mortar or a different seed.

Material graphs let a game describe a material as a recipe, the way Substance Designer
does. Noises and tile patterns are shaped by filters into a height map, and the colour,
normal, roughness and metalness all come from that height and its masks. The asset
pipeline cooks the recipe into the same textures a material reads now, so the renderer
can't tell a graph from a download.

This first version is cooked only. Graphs run when content is cooked, never while the
game runs.

## What a graph looks like

A graph is one C# file in the content folder, named `*.material.cs`. It holds one class
that derives from `MaterialGraph`. Its init properties are the parameters, and `Build`
returns the material:

```csharp
// content/materials/bricks.material.cs
using Super.Materials;

public sealed class Bricks : MaterialGraph
{
    public float Mortar { get; init; } = 0.04f;
    public float Moss { get; init; } = 0.3f;

    public override Material Build()
    {
        var bricks = Tiles.Bricks(columns: 4, rows: 8, gap: Mortar, bevel: 0.15f);
        var ids = bricks.FloodFill();
        var height = bricks
            .Multiply(ids.RandomGray().Remap(0.85f, 1f))
            .SlopeBlur(Noise.Cells(scale: 12), amount: 0.02f)
            .Add(Noise.Perlin(scale: 32, octaves: 4).Remap(0f, 0.15f));

        var moss = height.Invert().HistogramScan(position: 1f - Moss, contrast: 0.6f);
        var color = height
            .GradientMap(Ramp.Of("#5a2a1e", "#8c4632", "#a8604a"))
            .Multiply(ids.RandomGray().Remap(0.8f, 1f))
            .Blend(Rgb.Of("#3d4a22"), mask: moss);

        return new Material
        {
            Width = 2f,
            Depth = 0.03f,
            Height = height,
            Color = color,
            Roughness = Gray.Of(0.85f).Blend(Gray.Of(0.95f), mask: moss),
        };
    }
}
```

Nodes are lazy. Calling `Noise.Perlin` records a node and returns a handle, and nothing
is computed until the cooker evaluates the material at its size. That keeps every size
relative, so the same graph cooks at 512 for a preview and 2048 for the game. It also
means a node used twice, such as `ids` above, is computed once.

There are two image types, `Gray` (one channel) and `Rgb` (three). Both are 32-bit
floats while the graph runs, and are only turned into 8-bit when the textures are
written. A height map kept in 8 bits gives normals that step, which is the most common
reason home-made normal maps look cheap.

`Material` takes `Height`, `Color`, `Roughness`, `Metallic`, `Opacity` and `Normal`.
Each one is optional:

- With no `Normal`, it's made from `Height`. With both, the given normal is laid over the
  height's normal.
- `Width` is how many metres the texture covers, and `Depth` is how deep the height's
  range is in metres. Together they set the normal's strength in real units, so a floor
  and a wall made by different graphs have bumps of the same depth.
- `Occlusion` is 0 to 1, default 0.5. It's how much ambient occlusion from the height is
  multiplied into the colour, since the renderer has no occlusion texture.

### Why a C# file, cooked on its own

The pipeline's rule is that cooking is a pure function of a source's bytes. A graph in
the game's own assembly would break that rule: the cook would depend on the whole
assembly, so changing any game code would cook every material again. A JSON graph would
be pure, but a node graph in JSON is long and has no autocomplete.

One C# file per material keeps both. The cooker compiles the file by itself with Roslyn,
against `Super.Materials` only, so the file's bytes are the input. The game's own build
compiles the same files too, which is how they get autocomplete and type errors like any
other C# file. `Super.targets` gives the game `Super.Materials` as a compile-only
reference, so the game never runs a graph and doesn't ship the library. Because of that,
two graphs in one game can't have the same class name. Graphs in a content root outside
the game's folder, such as the shared `assets/`, cook but get no autocomplete.

The cost is that a graph can't call helpers in another graph file. Shared helpers go in
`Super.Materials`, or a game adds them through game cookers later.

Cooking runs the game author's code, which is no different from building it. A graph must
not read files, the clock or `System.Random`. It gets randomness from its seed, and images
from `Image("rock.png")`, which reads through `CookInput.Read` so a change to the image
cooks the graph again. `super-cook --verify` catches a graph that breaks this.

## The nodes

These are the first ones, about thirty. What matters most is that the fifteen marked
*core* are solid. With those you can make bricks, flagstones, dirt, wood, leather and
rusty metal.

| Group | Nodes |
| --- | --- |
| Generators | `Noise.Perlin` (fBm, octaves) *core*, `Noise.Cells` (F1, F2, edge distance, cell id) *core*, `Noise.White` *core*, `Noise.Directional`, `Shape` (disc, square, gradient, bell, pyramid), `Tiles.Grid` and `Tiles.Bricks` (offset rows, random size, rotation, height and position per tile) *core*, `Splatter`, `Gradient` (linear, radial) |
| Filters | `Levels` and `Remap` *core*, `Curve`, `Invert`, `HistogramScan` *core*, `Blur` *core*, `Warp` *core*, `DirectionalWarp`, `SlopeBlur` *core*, `EdgeDetect`, `FloodFill` with `RandomGray` and `ToGradient` *core*, `Distance`, `Transform` (tile, offset, rotate, scale) |
| Combining | `Blend` (copy, add, subtract, multiply, min, max, overlay, screen, with a mask) *core*, `HeightBlend` *core* |
| Colour | `GradientMap` *core*, `Rgb.Of`, `Hsl`, `ToGray` |
| Surface | `HeightToNormal` *core*, `NormalCombine` (reoriented normal mapping), `HeightToOcclusion`, `Curvature` |
| Escape hatch | `Pixel`, which runs a C# lambda for each pixel over its inputs, like Designer's Pixel Processor |

Every node follows the same rules:

- It wraps at the edges, blur, warp and slope blur included. Everything tiles by
  construction, so there's never a "make it tile" node.
- Sizes and distances are fractions of the image. A blur of 0.01 means the same thing at
  every size.
- Its randomness comes from the graph's seed hashed with the node's position in the
  graph, using the same FNV-1a and xoshiro256** as `Super.Random`. Changing one node's
  seed doesn't reshuffle the others, and the same seed always cooks the same pixels.
- Normals use the renderer's convention, which is OpenGL style with green pointing up.

## Running a graph

Graphs run on the CPU, not in compute shaders. The pipeline caches by content hash and
shares results between machines. GPU floating point differs between vendors and drivers,
so the same graph would give different bytes on different machines and break
`--verify`. On the CPU each pixel of a node is worked out on its own in `Parallel.For`
over rows, so the order threads finish in can't change the result. Sums such as a
histogram add rows in order. Sine, cosine, power and exponent come from our own
implementations in `Super.Materials`, since the runtime's can differ by a bit between
operating systems.

The aim is under a second for a thirty-node graph at 1024 on eight cores, and under four
seconds at 2048. The cache means a graph only cooks again when its file, its settings or
`Super.Materials` changes.

## What gets cooked

A graph cooks into four textures, named as parts of its source:

| Name | Holds |
| --- | --- |
| `materials/bricks.material.cs#color` | Base colour with occlusion multiplied in, sRGB |
| `materials/bricks.material.cs#normal` | Normal map |
| `materials/bricks.material.cs#surface` | Metalness in red, roughness in green, opacity in alpha, as now |
| `materials/bricks.material.cs#height` | Height, linear. For surface shaders such as puddles |

The game gets them all at once:

```csharp
Surface bricks = content.Surface("materials/bricks.material.cs");
var wall = content.Mesh(Shapes.Box(new Vector3(6f, 3f, 0.4f), bricks));
```

`content.Surface` returns a `Surface` with the three textures set, `Color` white, and
`TileWidth` set to the graph's `Width` in metres. A surface shader takes the height as
`content.Texture("materials/bricks.material.cs#height")`.

### How often it repeats

A graph says how wide it is in the real world, so by default it shows at that size. The
wall above is 6 metres wide and the bricks graph is 2, so it repeats three times across:

- A `uvPerMetre` passed to a shape always wins.
- Otherwise a shape whose surface has a `TileWidth` tiles it at that width, so a texture
  keeps its real size whatever the shape's size.
- Otherwise a shape stretches its texture once across each part, as it does now.

A model keeps the UVs it was made with, since UVs aren't in metres. Its sidecar's
`repeat` changes how often a material repeats on it.

### Size and variants

The size is a cook setting, 1024 by default, set by a rule:

```json
{ "match": "materials/*.material.cs", "material": { "size": 2048 } }
```

A variant is a small JSON file beside the graph that sets its parameters and seed. It
cooks under its own name:

```json
// content/materials/mossy-bricks.material.json
{ "graph": "bricks.material.cs", "seed": 7, "moss": 0.8 }
```

A parameter the class doesn't have is a cook error that names it. So is a value of the
wrong type.

### On models

A model's sidecar can point a material at a graph instead of texture files, with a
repeat:

```json
// props/sword/sword.glb.cook.json
{
  "materials": [
    { "match": "blade",  "graph": "../../materials/brushed-steel.material.cs", "repeat": 2 },
    { "match": "hilt",   "graph": "../../materials/dark-wood.material.cs" },
    { "match": "straps", "graph": "../../materials/leather.material.cs", "repeat": [1, 6] }
  ]
}
```

The model cooker reads the graph through `CookInput.Read`, evaluates it with
`Super.Materials`, and embeds the textures in the `.rmodel` like any other. `repeat`
scales that material's UVs at cook time, so the renderer needs nothing new. A graph used
by two models is evaluated twice. That's acceptable for now, and step 2 needs the model
cooker to evaluate graphs itself anyway, because the output depends on the mesh there.

## Checks

The cooker warns about values that make a material look wrong in physically based
lighting:

- Base colour darker than sRGB 30 or brighter than 240 over more than a tenth of the
  texture. Nothing real is that dark or that white.
- Metalness that's mostly between 0.1 and 0.9. Real surfaces are metal or not, and
  in-between values look like plastic.
- Roughness with almost no variation. Real surfaces have smudges and wear, and a flat
  roughness is the quickest way to make a material look generated.

A graph that throws is a cook error with the file and line from the exception.

## Seeing it

A graph is only as good as what you can see of it, so the example (below) has a lineup
view: a sphere, a cube and a flat tile for each material, under grazing light. When a
graph is saved, watch mode cooks it again and the game loads the new textures. Today
live reload only reloads JSON catalogs, so texture hot reload is part of this work (step
3 below). It's step 2 of the asset pipeline anyway.

## The example: Courtyard

A small ruined courtyard after rain, on an overcast evening. Everything in it comes from
graphs, so it needs no downloads.

- The ground is muddy dirt with pebbles and cracks, and its height drives the puddles.
  The water level slowly rises and falls on a loop of about twenty seconds. Water fills
  the cracks and hollows first, then joins up into one sheet, then drains away again.
  This is the effect people know from Substance demos.
- A path of flagstones crosses the yard, raised a little above the dirt. The gaps
  between stones flood before their tops, and the stones stay dry longest. That shows
  two height maps working with one water level.
- Brick walls stand on three sides, with moss low down and a ruined arch. They use
  variants of one bricks graph with different seeds and moss. The walls darken below a
  waterline that follows the water, with a damp band above it, so the water level reads
  on vertical surfaces too.
- A weathered plank bench or gate brings in wood, with grain from warped directional
  noise.
- A drain grate in the low corner and an iron ring on the wall are rusty metal.
  Metalness is 1 for the steel and 0 for the rust, from one mask.
- A low sun under cloud lights the yard from the side, so the normals read, with sky
  light. The puddles reflect the walls through screen-space reflections and the sky
  through the probe.

Tab switches to the lineup view. Up and down raise and lower the water by hand, and
space pauses the loop. The water follows game time, so `--screenshot` with the same
`--ticks` always shows the same level.

### The water

The water is a surface shader, `puddles.frag`, not a graph. The graph is cooked and the
shader only compares the cooked height with a level the game moves. That's a runtime
effect, but it uses what surface shaders can already do, with no runtime graph
evaluation. The shader takes the height in `ShaderTextures[0]` and, in its parameters,
the level, the width of the damp band and the depth at which water turns murky:

- Below the level it's water. The normal is the mesh's flat normal, roughness about
  0.05, metalness 0. The colour is the ground's darkened and tinted towards brown by
  depth, which is the level minus the height.
- Just above the level is a damp band. The colour is darker (porous surfaces get about
  the square of their dry colour) and roughness is lower.
- The edge between them is a short `smoothstep`, so the shore doesn't alias.

The walls use the same shader in its world-height mode, comparing `inPosition.y` with the
level in metres instead of the texture's height.

## Steps

1. **`Super.Materials` and the material cooker.** The node library, the evaluator,
   compiling `*.material.cs` with Roslyn, the four cooked parts, `content.Surface`,
   `Surface.TileWidth` and its default in `Shapes`, sizes from rules, variants, the
   checks, and the compile-only reference from `Super.targets`. Start with the fifteen
   core nodes and add the rest after them. It ends with the Courtyard project showing
   only its lineup view, with starter graphs for bricks, flagstones, dirt, wood, leather
   and rusty metal, as the check that the whole path works in a real game.
2. **Courtyard.** The scene, `puddles.frag` and the screenshots in the docs. Graph nodes
   that the scene needs and step 1 didn't cover come in here.
3. **Texture hot reload**, so saving a graph updates the running game.
4. **Graphs on models:** `graph` and `repeat` in model sidecars.
5. **Mesh bakes and triplanar projection.** The model cooker bakes position and normal
   into the model's UVs, and a `Triplanar` node projects any tiling graph through them,
   the way Substance Painter's triplanar fill does. The output is a texture set for that
   model only. Models need UVs that don't overlap, but they can be rough, such as
   Blender's Smart UV Project. A sword on a block in the courtyard shows it.
6. **Curvature and occlusion bakes** as graph inputs, for worn edges and dirt in crevices.

## Docs

A new `materials.md` covers writing a graph, the nodes, variants, sizes and the checks,
with pictures of the lineup. `content.md` gets the `*.material.cs` convention and
`content.Surface`. `rendering.md#models` gets `graph` and `repeat`. `design.md` explains
why graphs run on the CPU, why each file compiles alone, and how node seeds are made.
`examples/Courtyard/README.md` describes the scene.

## Not now

- Runtime graphs: evaluating a graph while the game runs, with live parameters.
- A visual node editor.
- Triplanar mapping in the renderer, for meshes without UVs.
- Occlusion and height textures in the renderer, and parallax.
- 16-bit cooked textures. The height part is 8-bit, which is enough for puddles but not
  for displacement.
- Sharing helpers between graph files.
- Cooking a graph once for all the models that use it.

## Settled

- A graph is `*.material.cs` and a variant is `*.material.json`. Both cook into a material
  that's loaded the same way, so they share the name.
- A graph shows at its real width on shapes unless the game says otherwise. Models keep
  their own UVs.
