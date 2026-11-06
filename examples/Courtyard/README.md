# Courtyard

Materials made in code, on a scene made in Blender. A sunlit courtyard has a brick wall
with a pier and a deep-set arched window, ivy, a peeling patch of whitewash, a gooseneck
lamp and a cast-iron drainpipe that runs down past the pier onto a splash stone. Along the
wall's foot is a plinth of big rounded stones of mixed heights, a few with a chipped corner,
and a cart wheel leans against it. The dry-stone wall mixes big footing stones with small
ones and pinning stones in the joints, and is capped with stones set on edge. A path of
flat, chipped flagstones with tight joints forks round a puddle with a raised bank. One arm
runs out towards the camera through mud marked with a rut and boot prints, past a barrel,
fallen leaves and grass that grows in patches along the walls, the path's edges and the
joints, to a deck. On the deck are a big crate with a diagonal brace on each side, thick
corner posts, nail heads and a crooked lid, a smaller crate and three tied sacks. Its
bricks, stone, mud, wood, rust and window glass are [material graphs](../../docs/materials.md) that
are cooked into textures when the game builds, so the example needs no downloads.

The sun is low from the left so it rakes across the bricks and the plinth, the sky is
dimmer and bluer so the shade reads, and a light finish adds contrast and a vignette.

```
dotnet run -c Release --project examples/Courtyard
dotnet run -c Release --project examples/Courtyard -- --screenshot shots/courtyard.png --validate --seed 1 --ticks 60 --size 1600x900
```

The model is `content/models/courtyard.glb`, written by `art/courtyard.py` in Blender.
The script builds everything from code into its own scene, called Courtyard, and unwraps
the UVs in metres. A material named `cy_graph_*` is only a placeholder. The model's
sidecar, `courtyard.glb.cook.json`, gives it a graph, and the cooker divides its UVs by
the graph's `Width`, so the bricks, stones and planks keep the size their graph gives
them however big the surface is. See [A material graph on a model](../../docs/rendering.md#a-material-graph-on-a-model).
To change the scene, edit the script, run it in Blender and cook again.

There are no point lights yet, so the lamp only glows through its bulb's `emissiveColor`.

Press M to turn the materials off and on. Without them, the whole courtyard is plain grey
clay, so you can judge the sunlight, the shadows and the shapes of the models without the
textures in the way. It flips [`Graphics.Materials`](../../docs/rendering.md#graphics-settings).
The `plain` script presses M for a screenshot:

```
dotnet run -c Release --project examples/Courtyard -- --script plain --screenshot shots/courtyard-plain.png --validate --seed 1 --ticks 60 --size 1600x900
```

## The lineup

`--lineup` shows the graphs one by one instead: a row for each, with a sphere, a cube and
a flat tile 1 m across. Each shape takes its surface from `content.Surface` with no
`uvPerMetre`, so it tiles at the graph's own `Width`. A low sun from the right lets the
bumps in the normal maps catch the light.

![The lineup: bricks, flagstones and dirt on the left, wood, leather and rusty metal on the right, each on a sphere, a cube and a 1 m tile](../../docs/media/material-lineup.webp)

```
dotnet run -c Release --project examples/Courtyard -- --lineup
```

## Files

| File | What's in it |
| --- | --- |
| `CourtyardGame.cs` | The courtyard model, the camera, the sun, the sky and the finish |
| `LineupGame.cs` | The lineup |
| `art/courtyard.py` | The Blender script that builds and exports the courtyard |
| `content/models/courtyard.glb.cook.json` | Which graph each of the model's materials wears, plus the water, the bulb, and the leaves and grass that are seen from both sides |
| `content/materials/glass.material.cs` | Old window glass, 1 m wide and transparent. Clean glass is a faint green tint with ripples from `Noise.Directional` that bend the reflection. Dust from Perlin, rain streaks running down and specks from blurred white noise raise its opacity and roughness |
| `content/materials/bricks.material.cs` | Big old red bricks 37.5 cm long in pale mortar, 3 m wide. Each brick is found with `FloodFill` and gets its own tilt and colour, and `SlopeBlur` with cells chips the edges |
| `content/materials/stone.material.cs` | Weathered grey stone, 1 m wide, for the dry-stone wall, the plinth, the sill and the flagstones. Perlin lumps, pits from cells, and moss in the low places |
| `content/materials/mud.material.cs` | Wet mud, 2 m wide. Churned clods from warped cells, pebbles stamped with `Splatter` and `HeightBlend`, and dark, glossy water in the hollows |
| `content/materials/flagstones.material.cs` | Irregular stones, 2 m wide. The joints are the edges of `Noise.Cells`, cut with `HistogramScan` and bent with `Warp`, and `HeightBlend` fills them with grit |
| `content/materials/dirt.material.cs` | Packed dirt, 2 m wide. Perlin noise and white grit, pebbles stamped with `Splatter`, and cracks from cell edges |
| `content/materials/wood.material.cs` | Planks, 1 m wide. Rings are a repeated `Gradient.Linear` bent by `Warp`, and the grain is `Noise.Directional` |
| `content/materials/leather.material.cs` | Leather, 0.5 m wide. Broad Perlin folds and creases from warped cell edges over a fine grain of blurred cells, smoother and paler where Perlin wear is high |
| `content/materials/rusty-metal.material.cs` | Steel plate, 1 m wide. Perlin patches through `HistogramScan` are rust, rough with metalness 0, and the rest is brushed steel with metalness 1 |

The graphs and the model pass the cook's checks with no warnings: the colours stay
between sRGB 30 and 240, metalness is 0 or 1, and roughness varies. `tests/Courtyard.Tests`
cooks the content folder and fails on any warning, so they stay good examples.
