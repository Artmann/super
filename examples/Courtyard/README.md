# Courtyard

Materials made in code. Six [material graphs](../../docs/materials.md) (bricks,
flagstones, dirt, wood, leather and rusty metal) are cooked into texture sets when the
game builds, so the example needs no downloads.

![The lineup: bricks, flagstones and dirt on the left, wood, leather and rusty metal on the right, each on a sphere, a cube and a 1 m tile](../../docs/media/material-lineup.webp)

For now it shows the lineup only: a row for each material, with a sphere, a cube and a
flat tile 1 m across. Each shape takes its surface from `content.Surface` with no
`uvPerMetre`, so it tiles at the graph's own `Width` and a brick is the size of a real
brick. A low sun about 20 degrees up lights them from the right, so the bumps in the
normal maps catch the light, and the sky fills in the shadows. A ruined courtyard after
rain, with puddles that rise and fall, comes next.

```
dotnet run -c Release --project examples/Courtyard
dotnet run -c Release --project examples/Courtyard -- --screenshot shots/lineup.png --validate --seed 1 --ticks 60 --size 1600x900
```

| File | What's in it |
| --- | --- |
| `CourtyardGame.cs` | The lineup, the camera, the sun and the sky |
| `content/materials/bricks.material.cs` | Red bricks 25 cm long in pale mortar, 2 m wide. Each brick is found with `FloodFill` and gets its own tilt and colour, and `SlopeBlur` with cells chips the edges |
| `content/materials/flagstones.material.cs` | Irregular stones, 2 m wide. The joints are the edges of `Noise.Cells`, cut with `HistogramScan` and bent with `Warp`, and `HeightBlend` fills them with grit |
| `content/materials/dirt.material.cs` | Packed dirt, 2 m wide. Perlin noise and white grit, pebbles stamped with `Splatter`, and cracks from cell edges |
| `content/materials/wood.material.cs` | Planks, 1 m wide. Rings are a repeated `Gradient.Linear` bent by `Warp`, and the grain is `Noise.Directional` |
| `content/materials/leather.material.cs` | Leather, 0.5 m wide. A soft grain of blurred cells, smoother and paler where Perlin wear is high |
| `content/materials/rusty-metal.material.cs` | Steel plate, 1 m wide. Perlin patches through `HistogramScan` are rust, rough with metalness 0, and the rest is brushed steel with metalness 1 |

The graphs pass the cook's checks with no warnings: the colours stay between sRGB 30 and
240, metalness is 0 or 1, and roughness varies. `tests/Courtyard.Tests` cooks the
content folder and fails on any warning, so they stay good examples.
