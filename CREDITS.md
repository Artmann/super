# Credits

The code in this repository is under the MIT license (see [LICENSE](LICENSE)).
That license does not cover the assets below, which belong to their authors. Most of
them aren't in git; [assets/README.md](assets/README.md) says where to get each one and
where to save it.

## Third-party libraries

- [OpenAL Soft](https://github.com/kcat/openal-soft) plays Super's sound through the
  speakers. It is licensed under the LGPL-2.0-or-later, and is loaded as a separate
  `soft_oal.dll` from the `Silk.NET.OpenAL.Soft.Native` package, so you can replace it.
  Its source is at the link.
- [NVorbis](https://github.com/NVorbis/NVorbis) reads `.ogg` files, and
  [NLayer](https://github.com/naudio/NLayer) reads `.mp3` files. Both are under the MIT
  license.
- [AMD FidelityFX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) 1.1.4
  gives Rendition its FSR 3.1 [upscaling](docs/rendering.md#upscaling). Its prebuilt
  `amd_fidelityfx_vk.dll` from the SDK's release is kept in `third_party/fidelityfx` and
  copied next to every game. It is Copyright 2024 Advanced Micro Devices, Inc. and licensed
  under the MIT license, in [third_party/fidelityfx/LICENSE.txt](third_party/fidelityfx/LICENSE.txt).

## Fonts

- [Inter](https://github.com/rsms/inter) is Copyright 2016 The Inter Project Authors and
  is licensed under the [SIL Open Font License 1.1](src/Super/Ui/Fonts/LICENSE.txt). It's
  built into `Super.dll`.

## Reef example

- ["Tuna Fish"](https://skfb.ly/ovCAZ) by GoldenZtuff is licensed under
  [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).
- ["Tillapia Buttikoferi fish"](https://skfb.ly/XLqG) by Ra_in_coat is licensed
  under [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).
- ["coral"](https://skfb.ly/6TRUD) by JonathanFreire is licensed under
  [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).
- ["Soft Coral Set"](https://skfb.ly/oWStC) by Kanna-Nakajima is licensed under
  [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).

The low-poly coral pack is not used, because its author and license are unknown.

## Fox example

- The [Fox](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Fox)
  from the Khronos glTF sample assets. The model is by PixelMannen under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/). The rigging and animation
  are © 2014 tomkranis, and the conversion to glTF is © 2017 @AsoboStudio and @scurest,
  both under [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).

## Archer example

- Erika Archer and her animations are from [Mixamo](https://www.mixamo.com) by Adobe,
  used under Mixamo's terms, which allow them in games and films but not redistributing
  the files themselves.
- From [Poly Haven](https://polyhaven.com), all under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/):
  - shrub_02 and dry_branches_medium_01 by Rico Cilliers;
  - dead_tree_trunk, tree_stump_01 and tree_stump_02 by Rob Tuytel;
  - fern_02, grass_medium_01, fir_sapling_medium, dandelion_01 and nettle_plant by Rob
    Tuytel and Rico Cilliers;
  - periwinkle_plant by Amal Kumar;
  - rock_moss_set_01 by Kless Gyzen;
  - the grass_ground texture by Charlotte Baglioni;
  - the forrest_ground_03, forest_leaves_04 and brown_planks_04 textures by Rob Tuytel;
  - the thatch_roof_angled texture by Dimitrios Savva and Rob Tuytel;
  - the hessian_230 texture by colormass and Rico Cilliers.
- `examples/Archer/models/build.py` builds the firs, the straw dummy and the arrow in
  Blender from those textures. The script and what it makes are under the same MIT
  license as the code.

## Rendition sample

- ["Adventurer"](https://sketchfab.com/3d-models/592c2f0cfaf4407bab3ef40dfc095eb1)
  by Otto Rossi is licensed under
  [Creative Commons Attribution](http://creativecommons.org/licenses/by/4.0/).
- The Ford GT model's author and license are not recorded yet.

## Crypt example

- From [Poly Haven](https://polyhaven.com), all under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/):
  - gothic_statue by Benny Weimer;
  - treasure_chest by Rico Cilliers;
  - wine_barrel_01 by James Ray Cock;
  - brass_candleholders by Tina;
  - Lantern_01 by Rajil Jose Macatangay;
  - the monastery_stone_floor texture by Amal Kumar;
  - the old_stone_wall texture by Charlotte Baglioni;
  - the large_sandstone_blocks texture by Rob Tuytel.

## Gallery example

- From [Poly Haven](https://polyhaven.com), under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/), in
  `examples/Gallery/content/models`:
  - [marble_bust_01](https://polyhaven.com/a/marble_bust_01);
  - [bronze_whale_statue](https://polyhaven.com/a/bronze_whale_statue).

## Forest example

- The sounds and music in `examples/Forest/content` are from the
  [Ninja Adventure Asset Pack](https://pixel-boy.itch.io/ninja-adventure-asset-pack) by
  Pixel-boy and AAA, released under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/). The file-by-file list is in
  [examples/Forest/CREDITS.md](examples/Forest/CREDITS.md).
