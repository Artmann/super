# Shared assets

The source models for the examples live here. Archer, Crypt, Fox and Reef list this folder as a
content root, so their build cooks whatever is in it (see
[Content](../docs/content.md) and [Rendering](../docs/rendering.md#models)).

Only the cook settings are in git: `cook.json`, which says what isn't cooked, and one
`<file>.cook.json` sidecar beside each model, which sets its size, fixes its materials
and lists its animations or parts. The models and textures themselves stay out of git,
because of their licenses or their size, so download them and save them under the names
below. A game names a model by its path in this folder, such as `fox/fox.glb`. A model
that's missing only gives a warning when the examples build.

A `cook.json` in a subfolder is git-ignored: only `assets/cook.json` and the
`*.cook.json` sidecars are committed. That keeps local exclusions local, such as a
`{ "rules": [{ "match": "**", "cook": false }] }` in a folder of models you've downloaded
but no example uses.

See [CREDITS.md](../CREDITS.md) for who made what.

## Fox

Save the Khronos
[Fox.glb](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Fox) as
`fox/fox.glb`.

## Reef

Download these from Sketchfab, and save each model with its textures in its own folder:

| Saved as | Sketchfab model |
| --- | --- |
| `reef/tuna/tuna.fbx` | ["Tuna Fish"](https://skfb.ly/ovCAZ), the FBX, with its textures beside it |
| `reef/tilapia/tilapia.glb` | ["Tillapia Buttikoferi fish"](https://skfb.ly/XLqG) |
| `reef/coral/coral.glb` | ["coral"](https://skfb.ly/6TRUD) |
| `reef/softcoral/softcoral.fbx` | ["Soft Coral Set"](https://skfb.ly/oWStC), the FBX, with its textures beside it |

## Archer

**Erika.** Download "Erika Archer With Bow Arrow" from [Mixamo](https://www.mixamo.com)
as FBX with skin, and save it as `archer/erika.fbx`. Download these animations without
skin, from the Pro Longbow Pack unless noted, into `archer/animations`:

| File | Mixamo animation |
| --- | --- |
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

The animations are cooked into Erika's model, not on their own.

**Plants and rocks.** Download these [Poly Haven](https://polyhaven.com/models) models as
1k glTF, each unzipped into its own folder under `props`, such as
`props/fern_02/fern_02.gltf`. Keep the file names Poly Haven gives them:

| Model name | Poly Haven model |
| --- | --- |
| `props/dead_tree_trunk/dead_tree_trunk.gltf` | dead_tree_trunk |
| `props/tree_stump_01/tree_stump_01.gltf` | tree_stump_01 |
| `props/tree_stump_02/tree_stump_02_1k.gltf` | tree_stump_02 |
| `props/rock_moss_set_01/rock_moss_set_01_1k.gltf#a` and `#b` | rock_moss_set_01 |
| `props/dry_branches_medium_01/dry_branches_medium_01_1k.gltf` | dry_branches_medium_01 |
| `props/fern_02/fern_02.gltf` | fern_02 |
| `props/shrub_02/shrub_02.gltf` | shrub_02 |
| `props/nettle_plant/nettle_plant_1k.gltf` | nettle_plant |
| `props/grass_medium_01/grass_medium_01.gltf#tall` and `#large` | grass_medium_01 |
| `props/dandelion_01/dandelion_01_1k.gltf#a` and `#b` | dandelion_01 |
| `props/periwinkle_plant/periwinkle_plant_1k.gltf#a` and `#b` | periwinkle_plant |

Models that come in sets, like the rocks and the dandelions, are cooked in parts; the
sidecar's `parts` pick the pieces by node.

**Firs, the dummy and the arrow** are built by `examples/Archer/models/build.py` in
Blender 4.3 or later, from these inputs, which aren't cooked themselves:

- Poly Haven's fir_sapling_medium as 1k glTF, in `props/fir_sapling_medium`, for its
  twigs and bark.
- These Poly Haven textures at 1k, each unzipped into its own folder under `textures`,
  such as `textures/hessian_230/textures/hessian_230_diff_1k.jpg`: hessian_230,
  thatch_roof_angled and brown_planks_04.

Run it from the repository root, and it writes `firs.glb`, `dummy.glb` and `arrow.glb`
to `archer-props`, along with the PNGs they use. The next build cooks them:

```
blender -b --python examples/Archer/models/build.py
```

The three firs are parts of one file: `archer-props/firs.glb#tall`, `#broad` and
`#young`.

The ground textures are the Archer example's own; see its
[README](../examples/Archer/README.md#getting-the-assets).

## Crypt

Download these [Poly Haven](https://polyhaven.com/models) models as 1k glTF, each
unzipped into its own folder under `props`, keeping Poly Haven's file names:

| Model name | Poly Haven model |
| --- | --- |
| `props/gothic_statue/gothic_statue_1k.gltf` | gothic_statue |
| `props/treasure_chest/treasure_chest_1k.gltf` | treasure_chest |
| `props/wine_barrel_01/wine_barrel_01_1k.gltf` | wine_barrel_01 |
| `props/Lantern_01/Lantern_01_1k.gltf` | Lantern_01 |
| `props/brass_candleholders/brass_candleholders_1k.gltf#seven`, `#three` and `#one` | brass_candleholders |

The candleholders' sidecar makes their flames glow and splits the set into its three
holders. The lantern's makes its glass clear and lit from inside.

The stone textures are the Crypt example's own; see its
[README](../examples/Crypt/README.md#getting-the-assets).

## Rendition sample

The sample views one cooked model at a time (see
[src/Rendition/README.md](../src/Rendition/README.md#run)).

| Saved as | Model |
| --- | --- |
| `adventurer/adventurer.glb` | ["Adventurer"](https://sketchfab.com/3d-models/592c2f0cfaf4407bab3ef40dfc095eb1) by Otto Rossi |
| `ford-gt/ford-gt.fbx` | A Ford GT, with its PNG textures beside it. Its author isn't recorded yet |
