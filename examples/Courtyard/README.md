# Courtyard

Materials made in code, on a scene made in Blender. A sunlit courtyard has a brick wall
with a pier and a deep-set arched window, ivy, a peeling patch of whitewash, a glass wall
lantern and a cast-iron drainpipe that runs down past the pier onto a splash stone. Above the
window a string course of one projecting course and a row of dentils runs across the wall
and round the pier, the window's jambs are edged with long and short quoins, and two iron
anchor plates hold the wall in. Along the wall's foot is a plinth of dressed stone blocks of
mixed heights with crisp edges, a few with a chipped corner, and a cart wheel leans against
it. The dry-stone wall is built of split, rough-squared stones in uneven courses with tight
joints: big footing stones low down, now and then a tall jumper that rises through two
courses, through-stones that stick out of the face, and pinning stones in the joints. Each
stone has broken corners, sharp arrises and a face tipped its own way, stands out by its own
amount, and is warm brown, blue-grey or pale, darker along the damp bottom course. It is
capped with slabs set on edge that lean together.

![The dry-stone wall before and after, close up: on the left rounded, pillow-shaped stones all one grey with wide black gaps between them and a net of cracks over every face; on the right flatter split stones with tight joints, standing out by different amounts, in warm, blue-grey and pale stone with faint bedding and spots of lichen](../../docs/media/courtyard-drywall.webp)

A path of flat flagstones, laid flush with tight joints, forks round a puddle with
a raised bank. One arm runs out towards the camera through mud marked with a rut and boot
prints with water standing in them, past a barrel, a plank dropped in the mud, scattered
rubble and broken bricks, small curled leaves and grass that grows in patches along the
walls, the path's edges and the joints, to a deck. On the deck are a big crate, a smaller
one, and three slumped grain sacks: one tied at the neck with a ragged ruffle above the
twine, and two with their mouths folded flat. Behind the window is a small room. Out of
frame, up-sun to the left, stands a tree. Its bricks, stone, mud, wood, sacking, whitewash,
rusty hoop iron and window glass are [material graphs](../../docs/materials.md) that are
cooked into textures when the game builds.

Up close the wall shows its age. Damp rises above the plinth and fades out in streaks,
dirty water has run down from the sill, and rust has washed down from the anchor plates.
They are [decals](../../docs/views.md#decals) the game projects onto the wall, so the
grime darkens the bricks and the mortar and makes them rougher, and each brick's edges
still show through the wet. D turns them off and on. The model still holds the old stain
quads from the Blender script, but the game leaves them out. The mortar sinks in with
parallax, a grit graph laid over the bricks and stones keeps them grainy a step away, and
broad patches of light and dark laid by world position hide where the textures repeat.

The lantern, the crates and the wheel are models from Sketchfab, kept in `art/assets`. The
script imports them, places them and puts them in the model with their own textures. They
are used under [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/):

- [Outdoor wall lamps](https://skfb.ly/oxOIo) by rsg3tank. The lantern is one of its four
  lamps, given the window frames' dark metal, the window glass and the glowing bulb.
- [Wooden Crate](https://skfb.ly/oyqQN) by Memorie.
- [Wooden_wheel](https://skfb.ly/6XMBR) by DOMNIN. Its metalness map is left out, since
  it made the grey, weathered wood shine like metal, and the sidecar warms the wood a little.

![The wall before and after: a green gooseneck lamp, a lathe-built wheel and a crate made from boards in code on the left, and the glass wall lantern, the wheel and the crate from Sketchfab on the right](../../docs/media/courtyard-models.webp)

It's a clear afternoon. The sun is high on the left, still low enough to rake across the
bricks and the plinth, and the tree's canopy breaks it into dappled light across the wall
and the window. The sun is set close to its real size with `view.Shadows(7f, 3, sunSize: 0.02f)`,
so the leaves' shade stays crisp though they are 10 m from the wall. See
[Shadows](../../docs/rendering.md#shadows). The sky is a clear one worked out from the sun
with `Turbidity = 2.5f`: blue overhead, paler at the horizon and bright round the sun's
disc, so the shade is cooler than the sunlight. See [The sky](../../docs/rendering.md#the-sky). The sun is strong against the sky, so
the shade is a clear step darker than the light, as on a real clear day. The camera meters
the courtyard with auto exposure and the finish adds about a third of a stop to what it picks, so
the shade stays open and the sunlit wall stays bright. See [Auto exposure](../../docs/rendering.md#auto-exposure). The lantern is a point light at its
bulb, left on but dim in daylight, that warms the wall just round it. It casts no shadows, since the bulb and
the lantern round it would hide it. A point light at the back of the room makes the old
glass glow, as if someone is home. A light finish adds contrast, a cool lift in the shade,
a warm gain in the light, bloom round the lamp and a vignette.

![The courtyard before and after: on the left the shade is nearly as bright as the sun, the lantern floods the wall and the bricks are all one orange-red; on the right the shade is a clear step darker, the lantern only warms the wall round it, and the bricks range from dark clinkers to pale ones in grey mortar](../../docs/media/courtyard-light.webp)

```
dotnet run -c Release --project examples/Courtyard
dotnet run -c Release --project examples/Courtyard -- --screenshot shots/courtyard.png --validate --seed 1 --ticks 60 --size 1600x900
```

The model is `content/models/courtyard.glb`, written by `art/courtyard.py` in Blender.
The script builds everything but the three downloaded models from code into its own
scene, called Courtyard, and unwraps the UVs in metres. A material named `cy_graph_*` is only a placeholder. The model's
sidecar, `courtyard.glb.cook.json`, gives it a graph, and the cooker divides its UVs by
the graph's `Width`, so the bricks, stones and planks keep the size their graph gives
them however big the surface is. See [A material graph on a model](../../docs/rendering.md#a-material-graph-on-a-model).
To change the scene, edit the script, run it in Blender and cook again.

The sidecar sets `keepOrigin`, so the model keeps Blender's origin and a point in the script
is the same point in the game, with Blender's z as y and its y as -z. That's how
`CourtyardGame.cs` places its lights at the lantern's bulb and in the room.

Press M to turn the materials off and on. Without them, the whole courtyard is plain grey
clay, so you can judge the sunlight, the shadows and the shapes of the models without the
textures in the way. It flips [`Graphics.Materials`](../../docs/rendering.md#graphics-settings).
The `plain` script presses M for a screenshot:

```
dotnet run -c Release --project examples/Courtyard -- --script plain --screenshot shots/courtyard-plain.png --validate --seed 1 --ticks 60 --size 1600x900
```

Press F to turn the fog off and on. The sky sets a light fog,
`Fog = new FogSettings(Density: 0.004f, Height: 0f, Falloff: 0.15f, SunScatter: 0.6f)`, thickest
on the ground, so the far wall and the window sit a touch back in the air behind the near
flagstones. See [Fog](../../docs/rendering.md#fog). It flips
[`Graphics.Fog`](../../docs/rendering.md#graphics-settings), and the `clear` script presses F
for a screenshot in clear air:

```
dotnet run -c Release --project examples/Courtyard -- --script clear --screenshot shots/courtyard-clear.png --validate --seed 1 --ticks 60 --size 1600x900
```

Press E to turn auto exposure off and on. Off, the finish's `Exposure = 0.3f` is a fixed
exposure instead of compensation on what the camera metered. It flips
[`Graphics.AutoExposure`](../../docs/rendering.md#graphics-settings), and the `fixed` script
presses E for a screenshot at the fixed exposure:

```
dotnet run -c Release --project examples/Courtyard -- --script fixed --screenshot shots/courtyard-fixed.png --validate --seed 1 --ticks 60 --size 1600x900
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
| `CourtyardGame.cs` | The courtyard model, the camera, the sun and its shadows, the sky and its fog, the lantern and the room's light, and the finish |
| `LineupGame.cs` | The lineup |
| `art/courtyard.py` | The Blender script that builds and exports the courtyard |
| `art/assets/` | The lantern, crate and wheel from Sketchfab, with their textures, that the script imports |
| `content/models/courtyard.glb.cook.json` | Which graph each of the model's materials wears, each cooked at `graphSize` 2048 so they hold up close (4096 for the bricks, which cover the most of the screen), a warmer tint for the wheel's weathered wood, five tints of the dry-stone graph for the wall's stones, plus the water, the bulb, the leaves, grass and whitewash that are seen from both sides, the grit detail graph and `worldVariation` on the bricks, stone, dry stone and flagstones, `worldVariation` on the mud, the three dithered stains, and `keepOrigin` |
| `content/materials/whitewash.material.cs` | Old limewash, 1 m wide, for the flakes on the wall. Off-white Perlin patches with grime streaks from `Noise.Directional` running down, heavier in some places than others |
| `content/materials/glass.material.cs` | Old window glass, 1 m wide and transparent. Clean glass is a faint green tint with ripples from `Noise.Directional` that bend the reflection. Dust from Perlin, rain streaks running down and specks from blurred white noise raise its opacity and roughness |
| `content/materials/bricks.material.cs` | Big old bricks 43.75 cm long and about 29 cm high in grey lime mortar, 3.5 m wide, cooked at `graphSize` 4096. Each brick is found with `FloodFill` and takes one of eight tones from dark plum through the reds and orange-browns to ochre and pale buff, most of them reds and orange-browns, a third the two middle reds, and the plum and buff rare accents. Their lightness rises steadily, the buff about twice the red. The tones are dealt once from a fixed shuffle so that no brick matches any brick it touches, and `BrickToneTests` checks it. Each brick also gets its own tilt and hardness: hard bricks are smooth with a slight sheen, soft ones undulate, are pitted with pores from small cells and are dull. Over about a fifth of the wall, in patches from low Perlin through `HistogramScan`, salts have dried to a white rim along the joints, bleeding about 4 cm into the soft bricks and over the mortar, from `Distance` on the mortar and broken up by a fine Perlin grain. Soot from a `Gradient.Linear` darkens a band under the string course, broken into streaks by `Noise.Directional`. `SlopeBlur` with cells chips the edges, and a second, coarser pass bites bigger chips out of one brick in five. Perlin through a narrow `Levels` spalls flakes off a few faces. The mortar is grey and dirty, filled to an uneven level and lightened and darkened by its own Perlin, and weathered back to a deep recess in patches from cells. Its height map gives the mortar parallax depth |
| `content/materials/brick-face.material.cs` | The face of one rubbed brick, 1 m wide and with no mortar, for the arch, the quoins, the string course and the broken bricks, which the model builds one by one. Patchy, slightly greyed orange from Perlin, with pores from cells and grit from blurred white noise, and a slight sheen, since rubbed bricks are hard. The sidecar tints its three materials lighter and darker |
| `content/materials/drystone.material.cs` | Split walling stone, 1 m wide, for the dry-stone wall. Faint bands of bedding from `Noise.Directional` run across each face, with a few long cracks along them where a stretched noise crosses its middle, flat scars where flakes split off, crusts of pale lichen and a few ochre spots from cells, and moss in the hollows and cracks. The wall's stones use five material slots that the sidecar tints warm, cool, pale, damp and dark for the hearting, so each stone takes its own colour from one cooked graph |
| `content/materials/stone.material.cs` | Weathered grey stone, 1 m wide, for the plinth, the sill and the flagstones. Perlin lumps, pits from cells, hairline cracks from cell edges, flecks of blurred white noise and spots of pale lichen, so it has detail at every size, and moss in the low places |
| `content/materials/grit.material.cs` | Fine grain 0.25 m wide, laid over the bricks, the stone and the flagstones as their `detailGraph`: sand-sized grains of blurred white noise, small pits from cells and a faint Perlin mottle, so a surface keeps its texture a step away |
| `content/materials/grime.material.cs` | A stain for dithered decals, 2 m wide: dark grime solid along one edge that fades out in streaks of `Noise.Directional` torn by Perlin. `Streak` fades it at the sides too, and `Rust` makes it orange-brown. Used as it is for the damp along the wall's foot |
| `content/materials/grime-streak.material.json`, `rust-streak.material.json` | Variants of the grime: the dirty water run down from the sill, and the rust washed down from the anchor plates |
| `content/materials/mud.material.cs` | Wet mud, 2 m wide. Churned clods from warped cells, pebbles stamped with `Splatter` and `HeightBlend`, and dark, glossy water in the hollows |
| `content/materials/flagstones.material.cs` | Irregular stones, 2 m wide. The joints are the edges of `Noise.Cells`, cut with `HistogramScan` and bent with `Warp`, and `HeightBlend` fills them with grit |
| `content/materials/dirt.material.cs` | Packed dirt, 2 m wide. Perlin noise and white grit, pebbles stamped with `Splatter`, and cracks from cell edges |
| `content/materials/wood.material.cs` | Planks, 1 m wide. Rings are a repeated `Gradient.Linear` bent by `Warp`, and the grain is `Noise.Directional` |
| `content/materials/leather.material.cs` | Leather, 0.5 m wide. Broad Perlin folds and creases from warped cell edges over a fine grain of blurred cells, smoother and paler where Perlin wear is high |
| `content/materials/burlap.material.cs` | Coarse jute sacking, 0.5 m wide, for the sacks. A plain weave of 96 round threads each way, each one thicker and thinner along its length from stretched `Noise.Directional`, with dark gaps between them, loose fibres, creases from warped cell edges and grubby Perlin patches |
| `content/materials/hoop-iron.material.cs` | Old forged iron, 0.5 m wide, for the barrel's hoops. Mostly dark rust from Perlin through `HistogramScan`, crusted with scale from cells, with bare iron, metalness 1, only in small worn patches |
| `content/materials/rusty-metal.material.cs` | Steel plate, 1 m wide, shown in the lineup. Perlin patches through `HistogramScan` are rust, rough with metalness 0, and the rest is brushed steel with metalness 1 |

The graphs and the model pass the cook's checks with no warnings: the colours stay
between sRGB 30 and 240, metalness is 0 or 1, and roughness varies. `tests/Courtyard.Tests`
cooks the content folder and fails on any warning, so they stay good examples.
