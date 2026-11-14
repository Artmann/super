# Changelog

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
