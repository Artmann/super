# The Clearing

A 17-second film made in Super. Erika, an archer, shoots a straw dummy, dodges, runs
and leaps onto a boulder, shoots another in slow motion, dives off and puts two arrows
in a third. The whole thing is one [cutscene](../../docs/cutscenes.md): `Film.cs` is
the storyboard, and the game only moves her, the arrows and the dummies. R plays it again.

The look is a forest clearing in golden-hour light: a low warm sun from the side, a cool
fill from the sky, a dark wall of firs around a bright meadow, and a gentle grade.

```
dotnet run -c Release --project examples/Archer
dotnet run -c Release --project examples/Archer -- --record out/clearing.mp4 --seconds 18
```

| File | What's in it |
| --- | --- |
| `Film.cs` | Every shot and every moment, in seconds from the start |
| `ArcherGame.cs` | Loading, the sky and light, the grade, and drawing |
| `Erika.cs` | Where she stands, root motion, leaps and drops |
| `Targets.cs` | The dummies and the arrows |
| `Clearing.cs` | The ground, the boulder, the forest and the scattered plants |
| `Shaders/clearing.frag` | The ground's surface shader, blending grass, forest floor and leaves |
| `models/build.py` | Builds the firs, the dummy and the arrow in Blender |

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

**Plants and rocks.** Download these [Poly Haven](https://polyhaven.com/models) models
as 1k glTF, each into its own folder under `assets/source/props`, such as
`assets/source/props/fern_02/fern_02.gltf`:

| Cooked as `assets/cooked/props/...` | Poly Haven model | Materials file |
| --- | --- | --- |
| `log.rmodel` | dead_tree_trunk | `prop-solid` |
| `stump.rmodel` | tree_stump_01 | `prop-solid` |
| `stump-b.rmodel` | tree_stump_02 | `prop-solid` |
| `mossy-rock-a.rmodel` | rock_moss_set_01 | `prop-mossy-rock-a` |
| `mossy-rock-b.rmodel` | rock_moss_set_01 | `prop-mossy-rock-b` |
| `branches.rmodel` | dry_branches_medium_01 | `prop-branches` |
| `fern.rmodel` | fern_02 | `prop-fern` |
| `shrub.rmodel` | shrub_02 | `prop-shrub` |
| `nettle.rmodel` | nettle_plant | `prop-nettle` |
| `grass-tall.rmodel` | grass_medium_01 | `prop-grass-tall` |
| `grass-large.rmodel` | grass_medium_01 | `prop-grass-large` |
| `dandelion-a.rmodel` | dandelion_01 | `prop-dandelion-a` |
| `dandelion-b.rmodel` | dandelion_01 | `prop-dandelion-b` |
| `periwinkle-a.rmodel` | periwinkle_plant | `prop-periwinkle-a` |
| `periwinkle-b.rmodel` | periwinkle_plant | `prop-periwinkle-b` |

```
... assets/source/props/fern_02/fern_02.gltf assets/prop-fern.materials.json assets/cooked/props/fern.rmodel
```

Models that come in sets, like the rocks and the dandelions, are cooked once per piece;
the materials file's `node` picks the piece.

**Firs, the dummy and the arrow** are built by `models/build.py` in Blender 4.3 or
later, from the fir_sapling_medium twigs and bark above and these Poly Haven textures
at 1k, each unzipped into its own folder under `assets/source/textures`, such as
`assets/source/textures/hessian_230/textures/hessian_230_diff_1k.jpg`: hessian_230,
thatch_roof_angled and brown_planks_04. Run it from the repository root, and it writes
glTF files to `assets/source/archer-props`:

```
blender -b --python examples/Archer/models/build.py
... assets/source/archer-props/firs.glb assets/prop-fir-tall.materials.json assets/cooked/props/fir-tall.rmodel
... assets/source/archer-props/firs.glb assets/prop-fir-broad.materials.json assets/cooked/props/fir-broad.rmodel
... assets/source/archer-props/firs.glb assets/prop-fir-young.materials.json assets/cooked/props/fir-young.rmodel
... assets/source/archer-props/dummy.glb assets/prop-dummy.materials.json assets/cooked/props/dummy.rmodel
... assets/source/archer-props/arrow.glb assets/prop-arrow.materials.json assets/cooked/props/arrow.rmodel
```

**The ground.** Download these Poly Haven textures at 1k and save their diffuse JPEG and
OpenGL normal map in `content/textures`, inside this folder:

| Saved as | Poly Haven texture |
| --- | --- |
| `grass-ground.jpg`, `grass-ground-normal.jpg` | [grass_ground](https://polyhaven.com/a/grass_ground) |
| `forest-floor.jpg`, `forest-floor-normal.jpg` | [forrest_ground_03](https://polyhaven.com/a/forrest_ground_03) |
| `leaves.jpg`, `leaves-normal.jpg` | [forest_leaves_04](https://polyhaven.com/a/forest_leaves_04) |

The three share one texture array, so they must be the same size.

See [CREDITS.md](../../CREDITS.md) for who made what.
