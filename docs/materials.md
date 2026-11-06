# Material graphs

A material graph describes a material as a recipe, the way Substance Designer does.
Noises and tile patterns are shaped by filters into a height map, and the colour, normal,
roughness and metalness come from that height and its masks. The cooker runs the recipe
and writes the same kind of textures you'd otherwise download, so the renderer can't tell
a graph from a texture set.

Graphs run when content is cooked, never while the game runs.

## Writing a graph

A graph is one C# file in a content folder, named `*.material.cs`. It holds one public
class that derives from `MaterialGraph`, and `Build` returns the material:

```csharp
// content/materials/bricks.material.cs
using Super.Materials;

public sealed class Bricks : MaterialGraph
{
    public override Material Build()
    {
        var bricks = Tiles.Bricks(columns: 4, rows: 8, gap: 0.04f, bevel: 0.15f);
        var ids = bricks.FloodFill();
        var height = bricks
            .Multiply(ids.RandomGray().Remap(0.85f, 1f))
            .SlopeBlur(Noise.Cells(scale: 12), amount: 0.02f)
            .Add(Noise.Perlin(scale: 32, octaves: 4).Remap(0f, 0.15f));

        var color = height
            .GradientMap(Ramp.Of("#5a2a1e", "#8c4632", "#a8604a"))
            .Multiply(ids.RandomGray().Remap(0.8f, 1f));

        return new Material
        {
            Width = 2f,
            Depth = 0.03f,
            Height = height,
            Color = color,
            Roughness = height.Remap(0.95f, 0.8f),
        };
    }
}
```

Nodes are lazy. Calling `Noise.Perlin` records a node and returns a handle, and nothing
is worked out until the cooker runs the graph at its size. That keeps every size
relative, so the same graph cooks at 512 or 2048. A node used twice, such as `ids` above,
is worked out once.

There are three image types. A `Gray` has one channel, an `Rgb` has three, and `Normals`
holds a direction per pixel. All of them are 32-bit floats while the graph runs. Nothing
is clamped until the textures are written, so a value can go above 1 or below 0 on the way.

The cooker compiles the file on its own, against `Super.Materials` and the core of .NET:
`System`, LINQ, the collections and the `System.Numerics` vectors. It can't use helpers
from another graph file or from the game.

Three namespaces are always there, as in the game's own build: `System`,
`System.Collections.Generic` and `System.Linq`. Write `using Super.Materials;` yourself,
and any other `using` line the file needs. `System.IO`, `System.Threading` and
`System.Net.Http` are left out on purpose, so a graph that names them fails to cook.
Nullable warnings are not reported by the cook.

A graph must give the same pixels every time it cooks. It must not read files, the clock
or `System.Random`. Its randomness comes from its seed.

## Autocomplete

The game's own build compiles the graph files too. They are `.cs` files in the game's
folder, and `Super.targets` references `Super.Materials` for them, so your editor offers
completion and shows errors as you type. The library isn't copied to the game's output.
A type error in a graph is a build error on its line, before the cook runs.

Because the game compiles every graph together, each graph class needs a name that is
unique within the game. Two graphs that both declare `Bricks` stop the build with CS0101.
A graph's class is in the global namespace unless you add a namespace yourself, and that
gives no warning.

A content folder outside the game's folder, such as `..\..\art\content`, isn't compiled by
the game, so its graphs get no autocomplete. Add them to the project to get it:

```xml
<ItemGroup>
  <Compile Include="..\..\art\content\**\*.material.cs" />
</ItemGroup>
```

The cook doesn't need this, and works the same either way.

## What gets cooked

A graph cooks into four textures and a small data part, named as parts of its source:

| Name | Holds |
| --- | --- |
| `materials/bricks.material.cs#color` | The colour, with occlusion multiplied in. sRGB |
| `materials/bricks.material.cs#normal` | The normal map, OpenGL style with green pointing up |
| `materials/bricks.material.cs#surface` | Metalness in red, roughness in green and opacity in alpha. Blue is 0. Linear |
| `materials/bricks.material.cs#height` | The height, grey. Linear. For surface shaders such as puddles |
| `materials/bricks.material.cs#material` | Data: `{"width":2}`, the graph's `Width` in metres |

A surface takes them like any other textures. The renderer multiplies the surface
texture by the surface's `Metallic` and `Roughness`, so set both to 1:

```csharp
Surface bricks = new()
{
    Color = Vector3.One,
    Metallic = 1f,
    Roughness = 1f,
    ColorTexture = content.Texture("materials/bricks.material.cs#color"),
    NormalTexture = content.Texture("materials/bricks.material.cs#normal"),
    SurfaceTexture = content.Texture("materials/bricks.material.cs#surface"),
};
```

Most of the time, take all of that from one call. `content.Surface` returns a surface
with the colour, normal and surface textures set, `Color`, `Roughness` and `Metallic` at 1,
and `TileWidth` set to the graph's `Width`:

```csharp
Surface bricks = content.Surface("materials/bricks.material.cs");
ModelRef wall = content.Mesh(Shapes.Plane(new Vector2(6f, 4f), surface: bricks));
```

A shape tiles a surface with a `TileWidth` once every `TileWidth` metres, so a 2 m wide
graph on a 6 m by 4 m plane repeats 3 times by 2. Give `uvPerMetre` to tile it at some other
size, which wins over the width. Naming something that isn't a cooked material, such as a
`.png`, stops with a `ContentException`.

Each value is clamped to 0..1 and rounded to the nearest of 256 steps. A value that isn't
a number is written as 0, and the cook warns:

```
materials/bricks.material.cs: height has pixels that aren't numbers. They were written as 0.
```

## The material

`Material` is what `Build` returns. Every part is optional:

| Property | Default | What it is |
| --- | --- | --- |
| `Width` | 1 | How many metres across the texture is. Must be more than 0 |
| `Depth` | 0.02 | How many metres high a height of 1 is. 0 or more |
| `Height` | 0.5 everywhere | The height map |
| `Color` | 0.5 grey | The base colour |
| `Roughness` | 0.6 | |
| `Metallic` | 0 | |
| `Opacity` | 1 | |
| `Normal` | none | Laid over the normal the height makes |
| `Occlusion` | 0.5 | How much of the occlusion the height makes is multiplied into the colour, 0 to 1 |

The normal map is always made from the height. `Width` and `Depth` set its strength in
real units, so a floor and a wall from different graphs have bumps of the same depth. A
`Normal` you give is laid over the height's normal with reoriented normal mapping. The
occlusion looks 5% of the image out from each pixel, which is under a pixel at small
sizes. Below about size 20, so at size 16, there's no occlusion at all.

## Size

The size is a cook setting, 1024 pixels square by default. Set it with a rule in
`cook.json`, or in the graph's own `<graph>.cook.json` sidecar:

```json
{ "rules": [{ "match": "materials/*.material.cs", "material": { "size": 2048 } }] }
```

A size must be a power of two from 16 to 4096. Anything else stops the cook:

```
materials/bricks.material.cs: size must be a power of two from 16 to 4096, not 100.
```

Sizes and distances in nodes are fractions of the image, so a graph looks the same at
every size, only sharper. The one exception is `RandomGray`, described below.

## Variants

A variant makes a second material from the same graph, with other values. It is a small
`*.material.json` file:

```json
// content/materials/mossy_bricks.material.json
{ "graph": "bricks.material.cs", "seed": 7, "moss": 0.8 }
```

`graph` is the path of the graph file, relative to the variant's folder. It must be a
`.material.cs` file, not another variant. The other names set the graph's parameters, and
the case doesn't matter. A parameter is a public `init` property of the graph class, of
type `float`, `int`, `bool` or `string`:

```csharp
public sealed class Bricks : MaterialGraph
{
    public float Moss { get; init; } = 0.2f;

    public override Material Build() { /* uses Moss */ }
}
```

Every graph also has `Seed`, so `"seed"` works in any variant. A graph cooked by itself
has seed 0. A parameter you leave out keeps the value the graph gives it. A number for an
`int` must be whole.

A variant cooks to its own parts, such as `mossy_bricks.material.json#color`, and its size
comes from the same settings as any other material. It cooks again when the variant or the
graph changes.

## Images

`Image("rock.png")` reads a picture into the graph as an `Rgb`. The path is relative to the
file being cooked, so for a variant it is relative to the variant. Use it to start from a
photo or a hand-painted mask:

```csharp
var rock = Image("rock.png");
var height = rock.Multiply(Gray.Of(0.5f));
```

The picture is stretched to the graph's size with bilinear sampling, wrapping at the
edges. Alpha is ignored. The colours are used as they are stored in the file, 0..1, with
no conversion from sRGB, just as `Rgb.Of("#8c4632")` is. The file is read when `Image` is
called, even if the graph never uses the result, and a change to it cooks the graph again.
A file that isn't there stops the cook with the line of the call.

## Mistakes

A graph that doesn't compile fails the cook with the compiler's message and the line it
points at. So does a type the graph can't use, such as `System.Net.Http.HttpClient`.

A file needs exactly one public class that derives from `MaterialGraph`, and it can't be
abstract. Otherwise the cook says what it found:

```
materials/bricks.material.cs must hold one class that derives from MaterialGraph. It has none.
materials/bricks.material.cs must hold one class that derives from MaterialGraph. It has 2: Bricks, Stones.
```

A graph that throws, while it builds or while it runs, fails the cook with the
exception's message. When the graph's own code was running, the error has its line too.
A node given a value out of its range throws when it's made, so the error points at that
node:

```
C:\game\content\materials\bricks.material.cs(9): error SUPER001: scale must be at least 1, not 0. (Parameter 'scale')
```

A variant that can't be used says why, with the variant's name first:

```
mossy.material.json needs "graph", the .material.cs file it sets values for.
mossy.material.json: graph must be a .material.cs file, not other.material.json.
mossy.material.json: Bricks has no parameter called mos. It has mortar, moss, seed.
mossy.material.json: moss is a number, not "lots".
```

The parameter names in the list are lower case, in alphabetical order. The other types
say `is a whole number`, `is true or false` and `is text`. A graph path that is missing,
or that differs from the real file only in case, is an error that names the file.

## Checks

After a graph is cooked, the cooker looks at the values and warns about ones no real
surface has. A warning never fails the cook. Pixels that aren't numbers are left out, as
they already have a warning of their own.

The base colour is judged by the bytes it is written as. A pixel is dark when its
brightest channel is below 30, and bright when its darkest channel is above 240. If more
than 10% of the pixels are dark, the cook warns, because nothing real is that dark:
charcoal is about 50. If more than 10% are bright it warns too, because fresh snow is
about 240. The message gives the share, rounded to a whole number.

If more than half of the metalness is between 0.1 and 0.9, the cook warns. Surfaces are
metal or not, and in-between values look like plastic. The ends, 0.1 and 0.9, count as in
between.

If the roughness has a standard deviation below 0.02 over all pixels, the cook warns.
Real surfaces have smudges and wear, so vary the roughness by a few percent at least.
A material that leaves the roughness out gets the flat default of 0.6 and so gets this
warning.

## Nodes

Every node wraps at the edges, so everything tiles. Sizes and distances are fractions of
the image. Counts across the image, such as `scale`, `columns` and `rows`, are whole
numbers so the pattern meets itself at the edges.

A node's randomness comes from the graph's `Seed` and the node's place in the graph, so
the same graph always cooks the same pixels. Nodes that take a `seed:` mix it in, which
gives that node a different pattern without changing any other.

### Generators

`Noise.Perlin(scale, octaves = 1, persistence = 0.5f, seed = null)` is smooth gradient noise from 0 to
1. `scale` is how many cells the first octave has across the image, 1 or more. `octaves`
is 1 to 12, each with twice the cells of the one before, and each counts `persistence`
times as much.

`Noise.Cells(scale, output = CellOutput.F1, jitter = 1f, seed = null)` is cellular noise with one
point in each of `scale` × `scale` cells. `jitter` is how far a point strays from the
middle of its cell, 0 to 1. `output` picks what it shows:

| `CellOutput` | Shows |
| --- | --- |
| `F1` | The distance to the nearest point, in cell widths |
| `F2` | The distance to the second nearest point |
| `Edge` | `F2` minus `F1`, which is 0 on the borders between cells |
| `Id` | A random value per cell |

`Noise.White(seed = null)` is one random value from 0 to 1 per pixel.

`Noise.Directional(scale, angle = 0f, stretch = 8f, octaves = 3, seed = null)` is Perlin
noise stretched `stretch` times longer along `angle` than across it, for wood grain,
streaks and brushed metal. `angle` is in degrees, counter-clockwise from +x, and snaps to
a direction that tiles (see [Angles](#angles)). `stretch` is 1 or more. Across the
direction there are about `scale` cells per image, and along it `scale / stretch`, each
rounded to a whole number of at least 1. Exactly, along a direction of run a and rise b the
noise repeats every 1/√(a² + b²) of the image, so the counts are `scale / √(a² + b²)` and
that divided by `stretch`, rounded. At 0° with the defaults, `Directional(8)` has 8 cells from
top to bottom and 1 from left to right. Since there is always at least one cell along, the
stretch reaches at most about `scale / √(a² + b²)`: `Directional(4, angle: 45f)` has 3
cells across and 1 along, so it is stretched about 3 times, whatever `stretch` says. Raise
`scale` for more. Each octave has twice the cells and counts half as much as the one
before, a fixed persistence of 0.5.

`Gradient.Linear(angle = 0f)` rises from 0 to 1 over half the image along `angle` and
falls back over the other half, a triangle wave, so it tiles. At 0° it is 0 at the left
and right edges and 1 in the middle. `angle` snaps like `Directional`, and along a
direction of run a and rise b the ramp repeats √(a² + b²) times across the image.

`Gradient.Radial()` is 1 at the middle and falls straight with the distance to 0 at the
corners.

`Shape.Of(kind, size = 1f)` draws one shape in the middle of the image, `size` of the
image wide, more than 0 and at most 1. With r the distance from the middle in half widths
(so the shape's edge is at 1) and `box` the larger of the distances across and down:

| `ShapeKind` | Value |
| --- | --- |
| `Disc` | 1 where r ≤ 1, else 0 |
| `Square` | 1 where `box` ≤ 1, else 0 |
| `Gradient` | Inside the square, rises from 0 at its left edge to 1 at its right. 0 outside |
| `Bell` | `0.5 + 0.5 cos(π r)` where r < 1, else 0 |
| `Pyramid` | `1 − box`, and 0 outside the square |

`Tiles.Bricks(columns, rows, gap = 0.04f, bevel = 0.1f, offset = 0.5f, heightVariation = 0f, seed = null)`
draws rows of bricks. `columns` and `rows` are 1 or more. `gap` and `bevel` are
fractions of a brick's height. The output is 0 in the gaps and rises over the bevel to 1,
then each brick is lowered by up to `heightVariation` at random. Each row shifts by
`offset` of a brick times its row number. So that the bricks tile, `offset` × `rows` must
be a whole number, or `Bricks` throws. With the default offset of 0.5, use an even number
of rows.

`Tiles.Grid(columns, rows, gap = 0.04f, bevel = 0.1f, heightVariation = 0f, seed = null)` is `Bricks` with no offset.

`Gray.Of(value)` and `Rgb.Of(r, g, b)` are flat images. `Rgb.Of("#8c4632")` reads a
`#rrggbb` colour straight to 0..1, as sRGB, the way Substance Designer does.

### Filters

These are methods on `Gray`. `Blur`, `Warp` and `SlopeBlur` work on `Rgb` too.

`Levels(inLow = 0, inHigh = 1, outLow = 0, outHigh = 1, gamma = 1)` clamps the input to
`inLow`..`inHigh` and stretches it onto `outLow`..`outHigh`. `inHigh` must differ from
`inLow`. A `gamma` over 1 brightens the middle, and it must be above 0.

`Remap(low, high)` maps 0 to `low` and 1 to `high`. Values outside 0..1 carry on past
them, so `Remap(1, 0)` is the same as `Invert`.

`Invert()` is 1 minus the value.

`HistogramScan(position, contrast = 0.5f)` turns a gradient into a mask. A `position` of
0.3 on an even gradient leaves 30% of it white. A low `contrast` softens the edge.

`Blur(radius)` is a Gaussian blur whose deviation is `radius` of the image, 0 to 1.

`Warp(by, amount)` moves each pixel along the slope of the grey image `by`. A 0 to 1 ramp
across the whole image moves pixels by `amount` of the image. `amount` is −1 to 1.

`SlopeBlur(slope, amount, samples = 16, mode = SlopeBlurMode.Average)` blurs along the
slope of `slope`, reaching `amount` of the image, 0 to 1, from `samples` reads, 1 or more. Where the
slope is flat the pixel is kept. `Min` and `Max` modes keep the darkest or brightest
read instead of the average.

### Transforms

`Transform(offset = default, rotate = 0f, scale = 1f, tile = 1)` on a `Gray` or an `Rgb`
moves, turns and repeats the image. It is shrunk to 1/`tile` and repeated `tile` times
across and down, scaled by `scale`, turned `rotate` degrees counter-clockwise about its
middle, then moved by `offset`, a `Vector2` in fractions of the image, right and down.
Moving by a whole image changes nothing. So that it tiles, `tile / scale` must be a whole
number, or `Transform` throws: a `scale` of 0.5 is the same as `tile: 2`, and a `scale`
of 2 needs a `tile` of 2, 4 and so on. `scale` is more than 0 and `tile` 1 or more.
`rotate` snaps (see [Angles](#angles)), and a turn by a direction of run a and rise b
also shrinks the image by √(a² + b²), so it shows a² + b² copies: 2 at 45°, 5 at 26.57°.
A quarter turn shows one, so use `rotate` mainly for quarter turns, and lay patterns along an
angle with the generators' own `angle` instead. `tile / scale` is at most 4096 copies
across. Pixels are read with bilinear filtering, wrapping at the edges. `offset` is a
`System.Numerics.Vector2`, so a graph that passes one starts with `using System.Numerics;`.
Game code needs the same line, since neither gets that namespace without it, so a graph
that builds in the game builds in the cook.

`Gray.Splatter(count, sizeMin = 0.05f, sizeMax = 0.1f, rotate = true, mode = BlendMode.Max, seed = null)`
stamps the image, shrunk to a random width from `sizeMin` to `sizeMax` of the image, at
`count` random places over black. Sizes are more than 0 and at most 0.5, and `count` is 1
or more. With `rotate`, each stamp turns by a random angle, which needs no snapping since
each stamp is read back from the image and wraps by itself. A stamp holds the image's edge
pixels at its own edges rather than wrapping, so the far side of the image never shows as a
fringe. Each stamp is combined with
what is under it by `mode`, in order, so with `Max` the brightest wins, with `Add` they pile
up and with `Copy` the last one is on top. Stamps that cross an edge wrap. Splatter
`Shape.Of(ShapeKind.Disc)` for pebbles, or a brick for scattered stones. The time it takes
grows with `count` times the area of a stamp.

### Angles

A pattern laid along an angle only tiles when the angle's direction meets the image's
edges at whole numbers of steps. So `Noise.Directional`, `Gradient.Linear` and
`Transform`'s `rotate` snap their angle to the nearest direction (run, rise) whose run
and rise are whole numbers from −4 to 4. A tie between two directions goes to the smaller
angle, counting from 0 up to 360. In the first quarter turn the angles are:

| Run, rise | Angle |
| --- | --- |
| 1, 0 | 0° |
| 4, 1 | 14.04° |
| 3, 1 | 18.43° |
| 2, 1 | 26.57° |
| 3, 2 | 33.69° |
| 4, 3 | 36.87° |
| 1, 1 | 45° |
| 3, 4 | 53.13° |
| 2, 3 | 56.31° |
| 1, 2 | 63.43° |
| 1, 3 | 71.57° |
| 1, 4 | 75.96° |
| 0, 1 | 90° |

The other three quarters repeat these, 90°, 180° and 270° on. So 30° snaps to 26.57°,
and 40° to 36.87°. A direction of run a and rise b repeats every 1/√(a² + b²) of the image,
so steep diagonals make patterns smaller, as each node above says.

### Flood fill

`FloodFill(threshold = 0f)` finds islands: pixels above `threshold` that touch on a side
are one island, and islands wrap at all four edges. `threshold` must be a finite number.
Run it on bricks or tiles to tell them apart.

`RandomGray(seed = null)` on the islands gives each island one random value from 0 to 1, and the
gaps 0. The value comes from the island's first pixel in row order, so the same graph at
a different size can give an island a different value.

### Combining

`Blend(top, mode = BlendMode.Copy, mask = null, opacity = 1f)` mixes `top` over the image.
The result is the image lerped towards `mode(image, top)` by `mask` × `opacity`. On an
`Rgb`, `top` is an `Rgb` and the grey mask covers all three channels.

| `BlendMode` | Gives |
| --- | --- |
| `Copy` | The top |
| `Add` | `a + b` |
| `Subtract` | `a − b` |
| `Multiply` | `a × b` |
| `Min` | The smaller |
| `Max` | The larger |
| `Overlay` | Multiplies where the base is dark and screens where it's bright |
| `Screen` | `1 − (1 − a)(1 − b)` |

No mode clamps. `Add`, `Subtract`, `Multiply`, `Min` and `Max` are short forms, and each
takes a `Gray` or a number. `Rgb.Multiply` takes a `Gray`, which multiplies every channel,
or an `Rgb`.

`Gray.HeightBlend(bottom, top, offset = 0.5f, contrast = 0.9f)` lays one height map over
another so the higher one wins, with a soft seam. It returns the combined `Height` and a
`Mask` that is 1 where the top won. An `offset` above 0.5 lifts the top. A higher
`contrast` makes the seam sharper.

### Colour

`GradientMap(ramp)` colours a grey image from a ramp, using the value, clamped to 0..1,
as the position. `Ramp.Of("#5a2a1e", "#a8604a")` spreads its colours evenly.
`Ramp.Of((0f, "#5a2a1e"), (0.7f, "#8c4632"), (1f, "#a8604a"))` places each one at a
position from 0 to 1, in order. Before the first stop and after the last, a ramp holds
their colour.

### Surface

`HeightToNormal(width, depth)` turns a height into a normal map. `width` is how many
metres across the image is, more than 0, and `depth` how high a value of 1 is, 0 or more.
Normals are OpenGL style: red points right and green points up the image. The cooker
does this for you from the material's height.

`HeightToOcclusion(width, depth, radius = 0.05f)` darkens crevices by looking for the
horizon in eight directions, out to `radius` of the image, 0 to 1. 1 is open and lower is more
hidden. A radius under one pixel finds nothing, so at the default radius occlusion
vanishes below about size 20. The cooker multiplies the material's occlusion into the
colour for you.
