# Changelog

## [1.2.0](https://github.com/Artmann/super/compare/v1.1.0...v1.2.0) (2026-11-15)


### Features

* add ragdolls built from characters, and a Cannonball example ([788741b](https://github.com/Artmann/super/commit/788741bfa28969121603c912d116bc63046f062f))
* cook an animation in place with inPlace, so a clip that walks plays on the spot ([b597874](https://github.com/Artmann/super/commit/b59787425271b0b289ad54ed471375f4c8d52ee6))
* fade draws out with distance, dither between simpler versions and soften glass where it meets the scene ([68601cf](https://github.com/Artmann/super/commit/68601cf9d1e5dd2199f2912e63a32f9dbb746fcc))
* fire each Cannonball shot at 80 to 100 percent of 18 m/s, so every shot lands a little differently ([9dbd584](https://github.com/Artmann/super/commit/9dbd584a5984219fffcac256e91569efcad4cd4d))
* fire the Cannonball stuntman 25% harder and move the field out to where he lands ([c1f85db](https://github.com/Artmann/super/commit/c1f85db6745f03bece8998dde580ce00cd532f3b))
* have the Cannonball stuntman rally the crowd after getting up, in place of clapping ([6aeb592](https://github.com/Artmann/super/commit/6aeb592030e0160f83da85681f35bfc2adc91106))
* let characters and chosen materials keep decals off ([#114](https://github.com/Artmann/super/issues/114)) ([e0547dc](https://github.com/Artmann/super/commit/e0547dc3af4935a2b99857fcabd0e3d57a2d2993))
* make the Cannonball stuntman bounce off the ground ([d0b592d](https://github.com/Artmann/super/commit/d0b592de839cc8b171f1fafcdcb121d145ae7013))
* put balls on and around the last wall in Cannonball, and raise the camera over it ([27119e2](https://github.com/Artmann/super/commit/27119e29c22d19d981d3f5f46a1a2670b9d1654e))
* read occlusion maps through either UV set ([#112](https://github.com/Artmann/super/issues/112)) ([c5e0a4c](https://github.com/Artmann/super/commit/c5e0a4c5bee58d84ba36482eb80e386dd98bf108))


### Bug Fixes

* find MoltenVK on macOS, so the renderer starts there for working on the engine ([2800ab2](https://github.com/Artmann/super/commit/2800ab23de29666904859f4646c415608e01c2ac))
* follow the cannonball from ahead and above, so the wall doesn't hide the landing ([d735561](https://github.com/Artmann/super/commit/d735561e39b13e1f8b770bfa1cc728085990c4d8))
* follow the cannonball from behind and above, so the camera looks over the wall ([0e077db](https://github.com/Artmann/super/commit/0e077db984ccfa61461459de08869df7c0dc8c55))
* keep UiPointer's size in the Roe baseline as Windows counts it ([5dd2929](https://github.com/Artmann/super/commit/5dd2929d951cc57a9820b011af51451986b0b388))
* leave the Cannonball tests out on CI, which doesn't have the Mixamo stuntman ([f1c397c](https://github.com/Artmann/super/commit/f1c397c1281f5ad44108c128ede3fe5c7f464ed1))
* let a ragdoll's legs collide with each other ([1329557](https://github.com/Artmann/super/commit/132955742782bd41abba4361403cda9cd4cc0b89))

## [1.1.0](https://github.com/Artmann/super/compare/v1.0.0...v1.1.0) (2026-11-14)


### Features

* add switches for subsurface, particles, local lights, haze and water, and count each pass's shaders ([500e5d5](https://github.com/Artmann/super/commit/500e5d52543ab91dee7ff985f25f0525aff39b30))
* switches for subsurface, particles, local lights, haze and water, and exact per-pass shader counts ([#102](https://github.com/Artmann/super/issues/102)) ([2a5b854](https://github.com/Artmann/super/commit/2a5b85421bc1be2378d6e057ab2293709250fde1))


### Bug Fixes

* let a state change asked for in OnExit run after the one being made, and bring the design notes up to date ([530d8ff](https://github.com/Artmann/super/commit/530d8ff64c454baf1d39013fb69317af34b477a6))
* name the drag's mouse button Pressed, so Roe stops tying it to the Button view ([f9f838f](https://github.com/Artmann/super/commit/f9f838fade0fa649a88ff7b1934db869e3859428))
* name the drag's preview Floating, so Roe stops linking the core to UiPointer ([12ae14c](https://github.com/Artmann/super/commit/12ae14c0585339dd6592420b2fb9fa61d0076cae))
* name UiPointer's gesture records LeftHold and PointerDrag, so Roe stops tying them to the Hold and Drag methods ([911339f](https://github.com/Artmann/super/commit/911339fa3709081a2eed6cae5ec603ffb17f352e))
* pack the cook tool's files once, at the paths it loads them from ([912cc17](https://github.com/Artmann/super/commit/912cc17014ca68e712e5b8c504f6d3244628b32c)), closes [#105](https://github.com/Artmann/super/issues/105)
* rename the drag's fields that Roe tied to other types, and keep the branch's sizes in the baseline after the merge ([7ecfb16](https://github.com/Artmann/super/commit/7ecfb162f7b445405fcef442d9854ce642e1a06e))
* write shape sizes the same in every culture ([b33d046](https://github.com/Artmann/super/commit/b33d04690ce41d88cfb21c497d16af106737f985))

## 1.0.0 (2026-11-14)


### Features

* publish the engine as one NuGet package, Super, released by Release Please ([3c175c2](https://github.com/Artmann/super/commit/3c175c2b61117df46d59ca014acab2053de56ea6))


### Miscellaneous Chores

* release 1.0.0 ([9b9f4b6](https://github.com/Artmann/super/commit/9b9f4b68f50ee6e3b4a2dd6521db1fbfcf72f989))
