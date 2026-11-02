# The Clearing

A 26-second film made in Super. Erika, an archer, shoots a straw dummy, dodges, runs
and leaps onto a boulder, shoots another in slow motion, dives off and puts two arrows
in a third. The whole thing is one [cutscene](../../docs/cutscenes.md): `Film.cs` is
the storyboard, and the game only moves her, the arrows and the dummies. R plays it again.

```
dotnet run -c Release --project examples/Archer
dotnet run -c Release --project examples/Archer -- --record out/clearing.mp4 --seconds 26
```

| File | What's in it |
| --- | --- |
| `Film.cs` | Every shot and every moment, in seconds from the start |
| `ArcherGame.cs` | Loading, the sky and light, and drawing |
| `Erika.cs` | Where she stands, root motion, leaps and drops |
| `Targets.cs` | The dummies and the arrows |
| `Clearing.cs` | The ground, the boulder and the scattered plants |

## Getting the assets

The models and textures aren't in git, because of their licenses or their size. Cook
each one with the import tool, which runs as
`dotnet run -c Release --project tools/Rendition.Import -- <source> <materials> <cooked>`.

**Erika.** Download "Erika Archer With Bow Arrow" from [Mixamo](https://www.mixamo.com)
as FBX with skin, and these animations without skin, from the Pro Longbow Pack unless
noted. Put them in `assets/source/archer` under these names:

| File | Mixamo animation |
| --- | --- |
| `erika.fbx` | The character |
| `idle.fbx` | standing idle 01 |
| `look.fbx` | standing idle 02 looking |
| `walk.fbx` | standing walk forward |
| `run.fbx` | standing run forward |
| `runstop.fbx` | standing run forward stop |
| `draw.fbx` | standing draw arrow |
| `aim.fbx` | standing aim overdraw |
| `recoil.fbx` | standing aim recoil |
| `dodge.fbx` | standing dodge right |
| `dive.fbx` | standing dive forward |
| `fall.fbx` | fall a loop |
| `land.fbx` | fall a land to standing idle 01 |
| `jump.fbx` | jumping up, from the Action Adventure Pack |
| `cocky.fbx` | being cocky, from the Gestures Pack Basic |

```
... assets/source/archer/erika.fbx assets/archer.materials.json assets/cooked/archer.rmodel
```

**The dummy and the arrow** were made for this example in Blender and are in `models`:

```
... examples/Archer/models/dummy.glb assets/prop-solid.materials.json assets/cooked/props/dummy.rmodel
... examples/Archer/models/arrow.glb assets/prop-solid.materials.json assets/cooked/props/arrow.rmodel
```

**Plants and rocks.** Download these [Poly Haven](https://polyhaven.com/models) models
as 1k glTF, each into its own folder under `assets/source/props`, such as
`assets/source/props/fern_02/fern_02.gltf`:

| Cooked as `assets/cooked/props/...` | Poly Haven model | Materials file |
| --- | --- | --- |
| `boulder.rmodel` | boulder_01 | `prop-solid` |
| `log.rmodel` | dead_tree_trunk | `prop-solid` |
| `stump.rmodel` | tree_stump_01 | `prop-solid` |
| `fern.rmodel` | fern_02 | `prop-fern` |
| `shrub.rmodel` | shrub_02 | `prop-shrub` |
| `grass-tall.rmodel` | grass_medium_01 | `prop-grass-tall` |
| `grass-large.rmodel` | grass_medium_01 | `prop-grass-large` |
| `fir-a.rmodel` | fir_sapling_medium | `prop-fir-a` |
| `fir-b.rmodel` | fir_sapling_medium | `prop-fir-b` |

```
... assets/source/props/fern_02/fern_02.gltf assets/prop-fern.materials.json assets/cooked/props/fern.rmodel
```

**The ground.** Download Poly Haven's
[grass_ground](https://polyhaven.com/a/grass_ground) texture at 1k, and save its
diffuse JPEG as `content/textures/grass-ground.jpg` and its OpenGL normal map as
`content/textures/grass-ground-normal.jpg`, inside this folder.

See [CREDITS.md](../../CREDITS.md) for who made what.
