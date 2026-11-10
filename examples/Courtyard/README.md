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

A path of flat flagstones, laid flush with tight joints, forks round a puddle of standing water with
a raised bank. One arm runs out towards the camera through mud marked with a rut and boot
prints with water standing in them, past a barrel, a plank dropped in the mud, scattered
rubble and broken bricks, small curled leaves and grass that grows in patches along the
walls, the path's edges and the joints, to a deck. On the deck are a big crate, a smaller
one, and three slumped grain sacks: one tied at the neck with a ragged ruffle above the
twine, and two with their mouths folded flat. Behind the window is a small room. Out of
frame, up-sun to the left, stands a tree. Its bricks, stone, mud, wood, sacking, whitewash,
rusty hoop iron and window glass are [material graphs](../../docs/materials.md) that are
cooked into textures when the game builds. A fresh breeze from `view.Wind` sways the grass,
and a little of it reaches the ivy and the tree's leaves, and their shadows sway with them.
See [Wind](../../docs/rendering.md#wind).

Up close the wall shows its age. Damp rises above the plinth and fades out in streaks,
dirty water has run down from the sill, and rust has washed down from the anchor plates.
They are [decals](../../docs/views.md#decals) the game projects onto the wall, so the
grime darkens the bricks and the mortar and makes them rougher, and each brick's edges
still show through the wet. Gravel kicked off the path lies in the mud along both arms'
edges and round the barrel's foot, and is laid the same way. `GravelSpills` places it at
random along the path's edges, but never in the puddle or out on the middle of the path.
D turns them all off and on. The model still holds the old stain quads from the Blender
script, but the game leaves them out. The mortar sinks in with
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

Press V to turn the [volumetric fog](../../docs/rendering.md#volumetric-fog) off and on. On,
the default, the fog near the camera is lit through the sun's shadows. The courtyard's
fog is light, so from the usual camera the difference is slight: the air in the sun is a
touch milkier than in the shade. The lantern is too faint against the daylight to show a
halo. Off, it is the plain height fog.
It flips [`Graphics.VolumetricFog`](../../docs/rendering.md#graphics-settings), and the
`plain-fog` script presses V for a screenshot with the plain fog:

```
dotnet run -c Release --project examples/Courtyard -- --script plain-fog --screenshot shots/courtyard-plain-fog.png --validate --seed 1 --ticks 60 --size 1600x900
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

## The leaves

`--leaves` shows the graphs that are cut out by their opacity, on the cards the courtyard
will wear them on. Along the back wall each hangs whole and flat: the two ivy leaves, the
meadow, broad and dry grasses, and the three dead leaves. In front, ivy climbs the wall,
a tuft of each grass stands on the ground, dead leaves lie in front of them and a cobweb
fills the corner of a frame, all cooked with the courtyard sidecar's own rules for them,
in a light breeze. The cards are `content/models/cards.glb`, written by `art/cards.py`.

![The leaves: ivy on the wall at the left, tufts of meadow, broad and dry grass, dead oak leaves on the ground and a cobweb in the corner of a frame, with each graph flat along the back wall](../../docs/media/courtyard-leaves.webp)

```
dotnet run -c Release --project examples/Courtyard -- --leaves
dotnet run -c Release --project examples/Courtyard -- --leaves --screenshot shots/leaves.png --validate --seed 1 --ticks 60 --size 1600x900
```

The courtyard's model doesn't use these graphs yet: its ivy, grass and dead leaves are
still plain `cy_ivy`, `cy_grass` and `cy_dead_leaf` quads. The sidecar's `graph_ivy_*`,
`graph_blade_*`, `graph_cobweb` and `graph_deadleaf_*` rules are waiting for cards with
those names.

## Files

| File | What's in it |
| --- | --- |
| `CourtyardGame.cs` | The courtyard model, the camera, the sun and its shadows, the sky and its fog, the lantern and the room's light, and the finish |
| `WallStains.cs` | Where the grime decals lie on the wall: the damp along its foot, the dirty water under the sill and the rust under the anchor plates |
| `GravelSpills.cs` | Where the gravel decals lie: spills along both edges of both arms of the path and round the barrel's foot, kept off the middle of the path, the puddle and the wall |
| `LineupGame.cs` | The lineup |
| `LeavesGame.cs` | The leaves |
| `art/cards.py` | Writes `content/models/cards.glb`, the leaves' cards, in plain Python with no Blender: each graph whole on the wall, ivy leaves 6 to 12 cm, grass tufts of one-column blade cards, dead leaves on the ground and a triangular cobweb card. Its UVs are in metres like the courtyard's |
| `content/models/cards.glb.cook.json` | The cards' rules: the courtyard's own rules for its `graph_*` names, and `swatch_*` rules that show each graph whole, cut out but not lit as foliage |
| `art/courtyard.py` | The Blender script that builds and exports the courtyard. It lays each board, stave and plank on one of the wood graph's boards, its width across the graph board and its two sawn ends on the graph board's two ends of end grain, so end grain shows only where a board really ends. The deck is laid in boards of 0.75 to 1.5 m, one graph board each, butted end to end in staggered joints, and no board at a run's end is shorter than 0.75 m. A deck board's width takes only the flat of its graph board, so the graph's rounded edge doesn't add a bright second edge beside the board's own bevel. Two iron nail heads are driven into each bearer a deck board crosses |
| `art/assets/` | The lantern, crate and wheel from Sketchfab, with their textures, that the script imports |
| `content/models/courtyard.glb.cook.json` | Which graph each of the model's materials wears, each cooked at `graphSize` 2048 so they hold up close (4096 for the bricks and the flagstones, which cover the most of the screen), the wood's two variants for the barrel's staves and the deck, with a wear tint on their edges that is only a little paler than the wood on the staves, and a little darker and dull on the deck, so worn edges don't read as bright lines, a warmer tint for the wheel's weathered wood, five tints of the dry-stone graph for the wall's stones, plus the bulb, the leaves, grass and whitewash that are seen from both sides, the grit detail graph and `worldVariation` on the bricks, stone, dry stone and flagstones, `worldVariation` on the mud, `puddles` on the mud, the flagstones, the plank, the deck, the hoops and the wheel, the three dithered stains, `wind` on the grass, with a share of 0.08 for the ivy and 0.4 for the tree's leaves so they sway less, rules for the leaf, blade, cobweb and dead-leaf cards that cut them out at `alphaCutoff` 0.5 and draw both sides, with `foliage` translucency 0.7 for the ivy and 0.85 for the grass blades, wind like the plain ivy and grass and a share of 0.02 for the cobweb, and three tints of dead leaf, and `keepOrigin` |
| `content/materials/whitewash.material.cs` | Old limewash, 1 m wide, for the flakes on the wall. Off-white Perlin patches with grime streaks from `Noise.Directional` running down, heavier in some places than others |
| `content/materials/glass.material.cs` | Old window glass, 1 m wide and transparent. Clean glass is a faint green tint with ripples from `Noise.Directional` that bend the reflection. Dust from Perlin, rain streaks running down and specks from blurred white noise raise its opacity and roughness |
| `content/materials/bricks.material.cs` | Big old bricks 43.75 cm long and about 29 cm high in grey lime mortar, 3.5 m wide, cooked at `graphSize` 4096. Each brick is found with `FloodFill` and takes one of eight tones from dark plum through the reds and orange-browns to ochre and pale buff, most of them reds and orange-browns, a third the two middle reds, and the plum and buff rare accents. Their lightness rises steadily, the buff about twice the red. The tones are dealt once from a fixed shuffle so that no brick matches any brick it touches, and `BrickToneTests` checks it. Each brick also gets its own tilt and hardness: hard bricks are smooth with a slight sheen, soft ones undulate, are pitted with pores from small cells and are dull. Over about a fifth of the wall, in patches from low Perlin through `HistogramScan`, salts have dried to a white rim along the joints, bleeding about 4 cm into the soft bricks and over the mortar, from `Distance` on the mortar and broken up by a fine Perlin grain. Soot from a `Gradient.Linear` darkens a band under the string course, broken into streaks by `Noise.Directional`. `SlopeBlur` with cells chips the edges, and a second, coarser pass bites bigger chips out of one brick in five. Perlin through a narrow `Levels` spalls flakes off a few faces. The mortar is grey and dirty, filled to an uneven level and lightened and darkened by its own Perlin, and weathered back to a deep recess in patches from cells. Its height map gives the mortar parallax depth |
| `content/materials/brick-face.material.cs` | The face of one rubbed brick, 1 m wide and with no mortar, for the arch, the quoins, the string course and the broken bricks, which the model builds one by one. Patchy, slightly greyed orange from Perlin, with pores from cells and grit from blurred white noise, and a slight sheen, since rubbed bricks are hard. The sidecar tints its three materials lighter and darker |
| `content/materials/drystone.material.cs` | Split walling stone, 1 m wide, for the dry-stone wall. Faint bands of bedding from `Noise.Directional` run across each face, with a few long cracks along them where a stretched noise crosses its middle, flat scars where flakes split off, crusts of pale lichen and a few ochre spots from cells, and moss in the hollows and cracks. The wall's stones use five material slots that the sidecar tints warm, cool, pale, damp and dark for the hearting, so each stone takes its own colour from one cooked graph |
| `content/materials/stone.material.cs` | Weathered grey stone, 1 m wide, for the plinth and the sill. Perlin lumps, pits from cells, hairline cracks from cell edges, flecks of blurred white noise and spots of pale lichen, so it has detail at every size, and moss in the low places. Spalls flake off the face, and `EdgeDetect` gives each of their sharp rims a 1 cm highlight; the high spots of the height are rubbed 5% lighter |
| `content/materials/grit.material.cs` | Fine grain 0.25 m wide, laid over the bricks, the stone and the flagstones as their `detailGraph`: sand-sized grains of blurred white noise, small pits from cells and a faint Perlin mottle, so a surface keeps its texture a step away |
| `content/materials/grime.material.cs` | A stain for dithered decals, 2 m wide: dark grime solid along one edge that fades out in streaks of `Noise.Directional` torn by Perlin. `Streak` fades it at the sides too, and `Rust` makes it orange-brown. Used as it is for the damp along the wall's foot |
| `content/materials/grime-streak.material.json`, `rust-streak.material.json` | Variants of the grime: the dirty water run down from the sill, and the rust washed down from the anchor plates |
| `content/materials/mud.material.cs` | Wet mud, 4 m wide. Coarse churned clods from warped cells, pebbles in three sizes stamped with `Splatter` and `HeightBlend`, straw from `Noise.Directional`, a dark organic Perlin layer, fallen leaves laid with `Splatter`, and dark brown to pale earth (#3a2a1c to #7a6248) that goes from rough when dry to glossy in the hollows |
| `content/materials/gravel.material.cs` | Gravel kicked off the path, 0.5 m wide, for decals. 400 stones 2 to 8 mm across in three greys on coarse grit, in a ragged spill that thins out a whole grain and a whole stone at a time so no square edge shows |
| `content/materials/flagstones.material.cs` | Old worn flagstones, 5.2 m wide, cooked at `graphSize` 4096: an 8 by 8 grid of stones, one for each cell. The path's slabs are modelled one by one, and `art/courtyard.py` maps each slab onto one cell, its outline on the cell's edge and its middle on the cell's middle, so the texture's edges and crown follow the real stone. The edges are chamfered by `Distance` from the joint through a `Curve` and nicked by `SlopeBlur`. A third of the stones have one or two hairline cracks running across them, a straight line through the stone bent by `Warp`, with a branch off it on the cracked stones that have two. The joints fill with dirt and fine gravel through `HeightBlend`, and moss grows in them and creeps onto the low parts of the stones. Each stone, found with `FloodFill`, takes one of five close greys or, one in eight, a warm sandstone, dealt from a fixed shuffle, and `FlagstoneTests` checks the shares. Roughness runs 0.7 to 0.9, polished to 0.5 on the crowns by a tiled `Gradient.Radial`, where feet go |
| `content/materials/dirt.material.cs` | Packed dirt, 2 m wide. Perlin noise and white grit, pebbles stamped with `Splatter`, and cracks from cell edges |
| `content/materials/wood.material.cs` | Old boards, 1 m wide: six boards down the image, each a metre long. Each board takes one of six tones from grey `#6a665e` to warm brown `#8a5a34`, equally light so that no board reads as a pale or dark stripe, dealt by row so that no board is within a stop of the boards beside it, and `FloodFill` gives each its own rings and a slight lift or shade. Rings are a repeated `Gradient.Linear` bent by `Warp`, and the grain is `Noise.Directional`. About three knots to the metre, from a `Splatter` of `Gradient.Radial`, are dark and smooth, and the rings and grain bend round them. Short checks open along the grain from a fine `Noise.Directional` through a narrow `Levels`, more of them the more weathered. `Weathering`, from 0 to 1, silvers the wood in Perlin patches through `HistogramScan`, paler, greyer and 0.9 rough. The last few centimetres of each board are dark, rough end grain. `Tone` moves every board towards the warm or the grey end, and `RustAt` lists where iron bands cross the boards, from which rust runs 20 cm down the grain in streaks. `WoodTests` checks the tones, the knots, the ends, the silvering and the rust |
| `content/materials/barrel-staves.material.json`, `deck-boards.material.json` | Variants of the wood: the barrel's staves at `Weathering` 0.35 and `Tone` 0.35, warm oak with rust bleeding down from under their four hoops, and the deck's boards at `Weathering` 0.45 and `Tone` 0.05, weathered warm timber, silvered in patches, with each board still its own tone. `WoodVariantTests` cooks both the way the game does and checks that each setting reaches the graph |
| `content/materials/leather.material.cs` | Leather, 0.5 m wide. Broad Perlin folds and creases from warped cell edges over a fine grain of blurred cells, smoother and paler where Perlin wear is high |
| `content/materials/burlap.material.cs` | Coarse jute sacking, 0.5 m wide, for the sacks. A plain weave of 96 round threads each way, each one thicker and thinner along its length from stretched `Noise.Directional`, with dark gaps between them, loose fibres, creases from warped cell edges and grubby Perlin patches |
| `content/materials/hoop-iron.material.cs` | Old forged iron, 0.5 m wide, for the barrel's hoops and the deck's nail heads. Mostly dark rust from Perlin through `HistogramScan`, crusted with scale from cells, with bare iron, metalness 1, only in small worn patches. Fresher orange-brown rust runs down it in streaks from `Noise.Directional`. Rust is 0.9 rough and bare iron 0.35, and the sidecar's wear pales the hoops' rubbed edges and polishes them to the bare iron's 0.35, though it leaves their metalness as the graph has it. `HoopIronTests` checks the roughness and that the streaks run down |
| `content/materials/rusty-metal.material.cs` | Steel plate, 1 m wide, shown in the lineup. Perlin patches through `HistogramScan` are rust, rough with metalness 0, and the rest is brushed steel with metalness 1 |
| `content/materials/ivy-leaf.material.cs` | An ivy leaf, 0.2 m wide, on its stalk, cut out by its opacity. Five lobes, a long one at the top, two at the sides and two small ones low down, each a tapering field joined to a round palm with a smooth union, and a notch where the stalk joins. Five main veins and side veins that wander with `Noise.Directional` ridge the height. Dark `#2f4a1e` at the base to `#6f9a3a` at the tips, a tenth of it yellowed on a Perlin mask, paler veins and margin, and roughness 0.45 with a smoother 0.3 sheen band across it |
| `content/materials/ivy-leaf-large.material.json` | The ivy leaf at seed 1, for the larger leaves: each seed turns and stretches the lobes a little |
| `content/materials/grass-blade.material.cs` | Grass blades, 0.3 m wide: a row of blades side by side, one to a column, each standing on the image's foot. A card wears one column. Each blade is a strip that tapers to a point in the opacity, folded along a rib that shows in the normal, green at its foot drying to straw towards its tip, with fine veins and a few rust spots. `Species` picks meadow, 24 long narrow blades; broad, 12 short wide lush ones; or dry, 16 straw stalks that end in a seed head of spikelets with awns |
| `content/materials/grass-blade-broad.material.json`, `grass-blade-dry.material.json` | The broad and dry species |
| `content/materials/cobweb.material.cs` | An old cobweb, 0.5 m wide, for a triangular card whose right angle is the image's top-left corner: anchor threads along the two sides, a frame thread across, 18 spokes holding a spiral torn away in patches, loose threads and dust. The threads are 1 to 2 mm thick so they hold up a few metres away, and everything else is cut out |
| `content/materials/dead-leaf.material.cs` | A fallen oak leaf, 0.15 m wide, on its stalk: rounded lobes down both sides, a midrib with a vein out to each lobe, a crumbled margin and a few holes, dry tan with brown blotches and a paler margin, its edges curled up in the height |
| `content/materials/dead-leaf-dark.material.json`, `dead-leaf-red.material.json` | The dead leaf at seeds 1 and 2, which the sidecar tints darker and redder |

The graphs and the model pass the cook's checks with no warnings: the colours stay
between sRGB 30 and 240, metalness is 0 or 1, and roughness varies. `tests/Courtyard.Tests`
cooks the content folder and fails on any warning, so they stay good examples.
