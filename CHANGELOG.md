# Changelog

## [2.0.1](https://github.com/Artmann/super/compare/v2.0.0...v2.0.1) (2026-11-20)


### Bug Fixes

* make and dispose a Physics under the lock joltc's shared state needs ([3969ed4](https://github.com/Artmann/super/commit/3969ed43dfcd0f74b9a2362002e0cd3140aca777))
* upgrade JoltPhysicsSharp so a disposed Physics frees its native system ([a2039f8](https://github.com/Artmann/super/commit/a2039f8cfd621341b6c881c78aefc3bd0fd5672a))

## [2.0.0](https://github.com/Artmann/super/compare/v1.6.0...v2.0.0) (2026-11-20)


### ⚠ BREAKING CHANGES

* lights, sky, shadows, occlusion, finish and outlines as components, and rename SunLight to Sun
* draw the world before Draw and make Draw optional
* the Smoothed component is gone. Remove it from spawns, and use world.Teleport where an entity jumps.
* make the world part of Super and register it for every game
* Renderer.CreateMesh, DestroyMesh, CreateDrawItem, CreateModel, DestroyModel, BoundsOf, CreateTexture, CreateTextureArray, DestroyTexture, DestroyTextureArray, SetOverlayAtlas, CreateMaterial, UpdateMaterial, DestroyMaterial, CreateSurfaceShader and ReloadShaders moved to Renderer.Assets with the same names and arguments. Renderer.CreateSkyImage stays on Renderer, next to SetEnvironment.
* Renderer.Bake, BakeChunk, Bakes and AutoBake moved to Renderer.Baking. Renderer.BakeFadeSeconds is now Renderer.Baking.FadeSeconds, MaxBakeFadeSeconds is RendererBaking.MaxFadeSeconds, LastBakeFrame is Renderer.Baking.LastFrame and BakeProgress is Renderer.Baking.Progress.
* Renderer.GpuTimes, GpuWork, WaitMilliseconds, DrawnTriangles, GpuIdleWaits and UpscalerVersion moved to Renderer.Stats. Renderer.AddReflectionProbe, UpdateReflectionProbe, ReflectionProbeWaiting, RemoveReflectionProbe, ClearReflectionProbes, ReflectionProbeCount, ReflectionCapturesDrawn and ReflectionLightingHash are now Add, Update, Waiting, Remove, Clear, Count, CapturesDrawn and LightingHash on Renderer.Reflections. Renderer.ReflectionLightsHash is now RendererReflections.LightsHash, and Renderer.MaxReflectionProbes is RendererReflections.MaxProbes.
* Renderer.Vsync, ShadowResolution, MaxShadowCascades, VolumetricFogCellSize, VolumetricFogSlices, MeshDetailScale, RenderScale, ReflectionResolution, OcclusionResolution, Upscaler, ProbeQuality and MeasureGpu are on Renderer.Settings. Renderer.MinMeshDetail, MaxMeshDetail, MinRenderScale, MaxRenderScale, CheckRenderScale and SceneSize are on RenderSettings.
* ModelData.Read, ReadRig and ReadMeshNames are ModelFile.Read, ReadRig and ReadMeshNames. ModelData.ToBytes() and ToBytes(share) are one method with an optional share.
* TextureData.KeepAlphaCoverage is CutoutCoverage.Keep, TextureData.AlphaCoverage is CutoutCoverage.Of, TextureData.ProductThreshold and PartnerAlpha are CutoutCoverage.ProductThreshold and PartnerAlpha, and TextureData.WidenRoughness and WidenedRoughness are RoughnessWidening.Widen and Widened.
* Material's texture and shader fields moved into groups. BaseColorTexture, NormalTexture, SurfaceTexture, EmissiveTexture and OcclusionTexture are Textures.BaseColor, .Normal, .Surface, .Emissive and .Occlusion. HeightTexture and HeightDepth are Height.Texture and Height.Depth. DetailColorTexture, DetailNormalTexture and DetailScale are Detail.Color, .Normal and .Scale. Ior and SoftEdge are Glass.Ior and Glass.SoftEdge. MaskTexture, Wear and Dirt are Weathering.Mask, .Wear and .Dirt. Shader, ShaderParameters, ShaderTextures and ShaderArrays are Custom.Shader, .Parameters, .Textures and .Arrays. Material.SoftEdgeProblem, TranslucencyProblem and WindProblem are MaterialProblems.SoftEdge, Translucency and Wind. FrameView's fields moved too: EnvironmentIntensity, Sky and ShowEnvironment are Environment.Intensity, .Sky and .ShowAsBackground. BackgroundRadiance and BackgroundFadeStart, End, Origin and Color are Background.Radiance, .FadeStart, .FadeEnd, .FadeOrigin and .FadeColor. Occlusion, Bounce and Outlines are ScreenSpace.Occlusion, .Bounce and .Outlines. Fog, Water, Puddles and Wind are Weather.Fog, .Water, .Puddles and .Wind. Post, Transition and DebugView are Picture.Post, .Transition and .DebugView. Overlay, Particles, Decals and FogVolumes are Extras.Overlay, .Particles, .Decals and .FogVolumes. BakedSpace and BakedChunks are Baked.Space and Baked.Chunks.
* the struct Tween<T> is now TweenValue<T>. Code that names the type, such as a field `Tween<float> Height`, becomes `TweenValue<float> Height`. Code that only calls Tween.Between, Tween.Sequence or Tween.Parallel and keeps the result in `var` needs no change.
* BakeInputHash.Space(bakeIds, lights, space, environment, sun, settings, placedChunks) is now BakeInputHash.Space(bakeIds, BakeInputHash.SpaceLights(lights, sun), space, environment, settings, placedChunks); the hashes are unchanged. ShadowMaskChannels.Assign(space, suns, lights, sunChannels, lightChannels, unassigned) is now ShadowMaskChannels.Assign(space, suns, lights), whose result holds Suns, Lights and Unassigned.
* every Graphics setting moved into a group, and so did its JSON profile key.
    - Screen: Vsync, MaxFramesPerSecond, RenderScale, Upscaler, AntiAliasing.
    - Shadowing: SunShadows, ShadowResolution, ShadowCascades, ContactShadows, LightShadows.
    - Lighting: LocalLights, LightProbes, ProbeQuality, AmbientOcclusion, OcclusionResolution, ScreenBounce,
      OcclusionHistory, Reflections, ReflectionResolution, MultiscatterSpecular, Specular, EnvironmentLight, Emissive.
    - Atmosphere: Fog, Haze, AerialPerspective, Stars, Underwater, FogVolumes, VolumetricFog,
      VolumetricFogCellSize, VolumetricFogSlices.
    - Surfaces: Materials, WearAndDirt, HeightMaps, Puddles, Wind, Decals, Subsurface, Particles.
    - Picture: Bloom, AutoExposure, DepthOfField, Vignette, Grain, Sharpening, ColorGrading.
    - Baking: BakedLighting, BakeOnArrival, BakeBudget.
    - Drawing: MeshDetail, Culling, SimplerModels, DepthFirst, Fades.
    In code, graphics.Bloom becomes graphics.Picture.Bloom, and new Graphics { Bloom = false } becomes
    new Graphics { Picture = { Bloom = false } }. In a JSON profile, { "Bloom": false } becomes
    { "Picture": { "Bloom": false } }; a saved profile has to be saved again or moved into groups by hand.
    DebugView, For, Fastest, Recommend, Apply, Save, ToJson, Load and FromJson stay on Graphics.
* some Content methods moved to parts of it.
    - content.Sound(name), content.Ambience(name) -> content.Sounds.Sound(name), content.Sounds.Ambience(name).
    - content.Image(name), content.Image(width, height, pixels), content.Unload(GameImage) ->
      content.UiImages.Image(...), content.UiImages.Unload(image).
    - content.Meshes(model, names), content.MeshNames(model), content.IsReady(model), content.Unload(ModelRef) ->
      content.Models.Meshes, content.Models.MeshNames, content.Models.IsReady, content.Models.Unload.
    - content.Unload(TextureRef) stays on Content.
* View's API moved.
    - view.Sun, view.Shadows -> view.Lights.Sun, view.Lights.Shadows.
    - view.PointLight(key, position, color, range, shadows, radius, volumetric, mode) ->
      view.Lights.Point(key, new PointLight(position, color, range) { Shadows, Radius, Volumetric, Mode }).
    - view.SpotLight(key, position, direction, color, range, inner, outer, ...) ->
      view.Lights.Spot(key, new SpotLight(position, direction, color, range) { InnerAngle, OuterAngle, ... }).
    - view.AmbientOcclusion, ScreenBounce, LightProbes, BakedLighting, BakedChunk and ReflectionProbe ->
      view.Indirect.<same name>.
    - view.Sky, Water, Puddles and Wind -> view.Surroundings.<same name>.
    - view.Particles, Decal, FogVolume, Finish and Outlines -> view.Effects.<same name>.
    - view.Model(key, model, transform, color, swim, pose, meshes, isStatic, fade) and the other overloads ->
      view.Model(key, model, transform, color), view.Model(key, model, transform, new ModelOptions { Color, Swim,
      Meshes, Static, Fade }), view.Model(key, model, transform, pose, options) and
      view.Model(key, animator, transform, options).
    - view.Ghost(key, model, transform, meshes, color, opacity) ->
      view.Model(key, model, transform, new Ghost(color, opacity), meshes).
    - View.DefaultSunSize and View.MaxSunSize -> ViewLights.DefaultSunSize and ViewLights.MaxSunSize.
* Surface settings moved.
    - HeightTexture and HeightDepth -> Height = new HeightMap(texture, depth).
    - DetailColorTexture, DetailNormalTexture, DetailScale and DetailMask ->
      Detail = new DetailMap { ColorTexture, NormalTexture, Scale, Mask }.
    - OcclusionTexture and OcclusionOnUv2 -> Occlusion = new OcclusionMap(texture, OnUv2: true).
    - Foliage = true and Translucency -> Foliage = new Foliage(Translucency: 0.5f).
    - Outline, WorldVariation, Dithered, Puddles and NoDecals = true ->
      Features = SurfaceFeatures.Outline | SurfaceFeatures.Puddles | ...
    - Shader, Parameters, ShaderTextures and ShaderArrays ->
      Shader = new SurfaceShader(content.Shader("name")) { Parameters, Textures, Arrays }.
    - Surface.MaxParameters and Surface.MaxShaderTextures -> SurfaceShader.MaxParameters and
      SurfaceShader.MaxTextures.
* animator.Play, Stop, IsPlaying, TimeOf and Crossed are now animator.Moves.Play, Stop, IsPlaying, TimeOf and Crossed. pose.SetJoint, Turn, ClearJoints, JointIndex and HasSetJoints are now pose.Joints.Set, Turn, Clear, Index and AnySet.
* Burst, Over, Pulses, FromPoint, FromLine, FromBox, FromDisc, FromSphere, Velocity, Outward, Scatter, Cone, Drag, Gravity, Wind, Wobble, Swirl, Size, Color, Gradient, Opacity, Glow, Fade, Flicker and Spin are now extension methods on ParticleEmitter in ParticleSpawning, ParticleMotion and ParticleLooks. Calls such as emitter.Drag(2f) compile unchanged with using Super; code that names them as ParticleEmitter members, such as a cref, uses the new classes.
* Shot.Orbit(seconds, subject, radius, height, fromAngle, toAngle, ...) is now Shot.Orbit(seconds, subject, new OrbitPath(radius, height, fromAngle, toAngle), ...). tween.Seconds, Delay, Easing, Curve, Repeat and Times are now tween.Timing.Seconds and so on; set them with After, Ease, Loop and Yoyo, or with `with { Timing = ... }`.
* input.MousePosition, MouseDelta, Scroll, MouseOverUi and MouseRay(camera) are now input.Mouse.Position, Delta, Scroll, OverUi and Ray(camera). input.CaptureMouse is input.Mouse.Locked, and input.HideCursor and input.Cursor are input.Mouse.HideCursor and input.Mouse.Cursor. input.Stick, Trigger, GamepadConnected and DeadZone are input.Gamepad.Stick, Trigger, Connected and DeadZone. Mouse.Delta and Mouse.Scroll bindings are Binding.MouseDelta and Binding.MouseScroll.
* a cooker's input.Recall, input.Keep and input.HasStepCache are now input.StepResults.Recall, input.StepResults.Keep and input.StepResults.HasCache.
* Physics's per-entity calls moved to its parts: Velocity, AngularVelocity, SetVelocity, SetAngularVelocity, Push, AddForce and AddTorque to physics.Bodies; Move to physics.Characters; Raycast, RaycastAll, ShapeCast, ShapeCastAll and Overlap to physics.Queries; Entered, Exited and Inside to physics.Triggers; IsFaceUp and PushPart to physics.Ragdolls, and RagdollShapes became physics.Ragdolls.Shapes. Step, Sync, Gravity, Count, Contacts and the constants stay on Physics. The message for a dynamic Body with a Character now names Physics.Characters.Move.
* Gray.Add, Subtract, Multiply, Min, Max, Warp, DirectionalWarp and SlopeBlur are no longer instance methods of Gray. Calls written as image.Add(other) still compile, since the extension classes are in Super.Materials. Code compiled against the old assembly must be rebuilt, and a call through the type, such as typeof(Gray).GetMethod("Add"), must use GrayArithmetic or GrayWarps.

### Features

* decals, fog volumes, reflection captures and baked pieces as components ([860b0c6](https://github.com/Artmann/super/commit/860b0c60cceabf4fd06467dd627b78864f02fb8f))
* describe static entities once and add world.Changed ([e358ddf](https://github.com/Artmann/super/commit/e358ddf35aa30d5828ae305fe0764d6b05740880))
* destroying an entity destroys its children, and world.Children lists them ([37eb108](https://github.com/Artmann/super/commit/37eb1082d23bcc1574af5bdfa6da9492e9d0e280))
* draw the frame through the newest Camera entity ([a58ffaa](https://github.com/Artmann/super/commit/a58ffaa5bb24a60fb5d02d9c3007088e50882bdd))
* draw the world before Draw and make Draw optional ([4782069](https://github.com/Artmann/super/commit/47820690ca6ab463546b8bf8573bd975f4c5ccfa))
* Emitter plays a particle effect for as long as it's there ([8f07617](https://github.com/Artmann/super/commit/8f07617875a7d2e2483714fb8e9a147a8f609f67))
* Ghost and Hidden components ([37ea33a](https://github.com/Artmann/super/commit/37ea33a9cc3dbae40472d6246c2088f7f9e6306f))
* Go to a scene through a transition ([de7a165](https://github.com/Artmann/super/commit/de7a165b12b15ae8d53c2e709240e1b8e29e8708))
* Jumper, Burst, OnRig, Only and a transition on Rebuild, with Skylark and its guide moved onto them ([9ba13ff](https://github.com/Artmann/super/commit/9ba13fff760ae7b3f3552f7a0e6c88ed74a0157d))
* lights, sky, shadows, occlusion, finish and outlines as components, and rename SunLight to Sun ([c492927](https://github.com/Artmann/super/commit/c492927af100b578366eaa67829789f6e948aa55))
* make the world part of Super and register it for every game ([802bb03](https://github.com/Artmann/super/commit/802bb0311bb62199b4119c522fe596298b9ce8b3))
* Parent can name a bone of an animated parent ([04f4868](https://github.com/Artmann/super/commit/04f4868a1106145b21018be2b2e67004a8751099))
* queue world changes made during a query instead of throwing ([97c938c](https://github.com/Artmann/super/commit/97c938c4c29b9cad96d84fe9439a16b9cb538fcb))
* raise a world's limit to 256 component types ([805e7c0](https://github.com/Artmann/super/commit/805e7c0f3f0c9d2dffdf5f1220e97bad7b004cab))
* react when a component lands on or leaves an entity ([cffd2f6](https://github.com/Artmann/super/commit/cffd2f6984cb1685fdceac6e97267c10f284700b))
* Scenes swap what's in the world at the end of a tick ([c927843](https://github.com/Artmann/super/commit/c9278437d5e0c58cc4d91b8734d0e3bc120dc221))
* screen bounce, light probes, baked lighting, water, puddles and wind as components ([6542169](https://github.com/Artmann/super/commit/6542169e87de82b187bffa94787476f04cfacc70))
* sharper primitives: five-component Spawn, Count and Single, ground velocity, trigger views by component, action steps in scripts, Game.Run ([c556871](https://github.com/Artmann/super/commit/c5568719b27948a8a558af2613bf533d6b25acbc))
* Skylark, a 3D platformer example with a guide ([77c02cc](https://github.com/Artmann/super/commit/77c02cc4472a90bfc315386858df4b9e876b18d5))
* smooth every moving entity between ticks and remove Smoothed ([e827405](https://github.com/Artmann/super/commit/e8274058174320828b91076cfd624b89055ee59d))
* spawn an entity with its components in one call, with required components ([ef0bf2b](https://github.com/Artmann/super/commit/ef0bf2bfbfcdf83f164a8846c14d8662be6bff69))
* Spawn takes any number of components, or an array of them ([8d67122](https://github.com/Artmann/super/commit/8d671222d7900f7ec9f117b3a3172c0200a73e85))
* Systems run a game's systems in order ([c9ec03c](https://github.com/Artmann/super/commit/c9ec03c531286d4fce925d2f581a9a25306ffdcd))
* Systems use the game's own registered system ([f997a04](https://github.com/Artmann/super/commit/f997a0490f4e3fb36b1c4909bc955aa61bafd864))


### Bug Fixes

* Draw can replace the world's baked space, and keys shared with entities are documented ([7d1b6f5](https://github.com/Artmann/super/commit/7d1b6f5bceeca65f2acd04740b3e83f74671bdf9))
* expect the bake fade's range message to name FadeSeconds ([fa70c3b](https://github.com/Artmann/super/commit/fa70c3b16fed350381172adda380464033a3400b))
* give the probe pick benchmark room for CI runners ([8de2236](https://github.com/Artmann/super/commit/8de2236f1a96f08954be0b3d5729b68dc20821d6))
* keep queued adds and removes in order and start smoothing afresh ([6d88dd5](https://github.com/Artmann/super/commit/6d88dd5f862bbabc21a00b176248c9f351e8e302))
* name view.Indirect.BakedLighting in the bake's missing space error ([0bbc339](https://github.com/Artmann/super/commit/0bbc33982a46f7ec317868d5b89e275489f74f1d))
* Rebuild after a pending Go keeps the Go ([786230d](https://github.com/Artmann/super/commit/786230df7b048421001c16f6b15774bf3232a803))
* run a queued add's OnAdded before the same type is removed ([062260c](https://github.com/Artmann/super/commit/062260cdbbc156b7c95948231bc85c72d3d17eff))
* time draw picking against measuring, not copying ([4e00d8d](https://github.com/Artmann/super/commit/4e00d8daa05ccc0c719306a6ceebe7e0fa70279f))


### Performance Improvements

* group draws by look, pick probes by number and measure draws once a frame ([a76a6c9](https://github.com/Artmann/super/commit/a76a6c96eac991d47e47d62c5ba6ec553e508057))
* spawn in one move and find children through an index ([d94eb74](https://github.com/Artmann/super/commit/d94eb74fa620dafb28ac5120cf7cd62adddc0cd9))


### Code Refactoring

* give CookInput's step results and file reads their own classes ([eacb530](https://github.com/Artmann/super/commit/eacb53015e20e0a936fa31800f3980730dd84847))
* give Shot.Orbit an OrbitPath and tweens a TweenTiming ([371584d](https://github.com/Artmann/super/commit/371584de58656bb706394e017b4b81f2fa6b5eac))
* group a Surface's height, detail, occlusion, foliage, shader and switches ([c3467b0](https://github.com/Artmann/super/commit/c3467b0a285c8715be0ec165be6193909724e4c7))
* group the Graphics settings ([b02e2fd](https://github.com/Artmann/super/commit/b02e2fd8a4c9297d1a7a8ec0c1fdb836167d85a4))
* group the renderer's baked lighting into Renderer.Baking ([0ca5b4a](https://github.com/Artmann/super/commit/0ca5b4af14e230ca436aad82e716bab9ce3cedda))
* group the renderer's meshes, textures and materials into Renderer.Assets ([3f103b1](https://github.com/Artmann/super/commit/3f103b1949f2397ffcdb32f514c2ce86d09468a3))
* group the renderer's stats and reflection probes ([95ce5f0](https://github.com/Artmann/super/commit/95ce5f09c1d6c125bd8125aa84df63b39acd9d32))
* move Gray's arithmetic and warp shorthands to extension classes, and group node settings ([aa744f3](https://github.com/Artmann/super/commit/aa744f3e16a17c04ecbe6d03b107d4aae776b6da))
* move TextureData's mip filter, cut-out coverage and roughness widening into their own types ([bfd2893](https://github.com/Artmann/super/commit/bfd28931c19f836ba37e38927c4c2a307683a77e))
* move the renderer's settings into RenderSettings ([bfa892d](https://github.com/Artmann/super/commit/bfa892d77a86bdbbcee504d975768b7a15d2500e))
* read and write .rmodel files through ModelFile ([c8fa024](https://github.com/Artmann/super/commit/c8fa024a660bfc86e884fd82b86a6ac06afec5d5))
* rename the tween struct Tween&lt;T&gt; to TweenValue&lt;T&gt; ([b9b6c3d](https://github.com/Artmann/super/commit/b9b6c3d5d892249ad4799574171b48ed529277f5))
* split Content into its items, sounds, interface images, models and catalogs ([e30a07a](https://github.com/Artmann/super/commit/e30a07a251c1e8367824bc8588079710cf79c25b))
* split Input into keys, Mouse and Gamepad ([cc9de9e](https://github.com/Artmann/super/commit/cc9de9e8a5d45e976cce402ea9df1e87770fc4bc))
* split ParticleEmitter's node calls and stages into their own types ([4cde48d](https://github.com/Artmann/super/commit/4cde48d731df439913390e21191b7a68151c0a05))
* split Physics into bodies, characters, queries, triggers and ragdolls parts ([8fb92c4](https://github.com/Artmann/super/commit/8fb92c48eb2131c81b2eccf66933b9b904f55ed4))
* split the animator, pose and rig into focused parts ([e13e73d](https://github.com/Artmann/super/commit/e13e73dba9251cad4dc6111e87cff35897d96d76))
* split the lightmap packer, the space input hash and the space mask channels ([8501a2e](https://github.com/Artmann/super/commit/8501a2eb287335a556a7ac137d56a2954f510d46))
* split Types.cs and group Material's and FrameView's fields ([65382d7](https://github.com/Artmann/super/commit/65382d730d8b00eecae05b45328d0442962165d1))
* split View into lights, indirect light, surroundings and effects ([2da12c1](https://github.com/Artmann/super/commit/2da12c176e12cb54557ecb81d1e16a81c3e31804))

## [1.6.0](https://github.com/Artmann/super/compare/v1.5.0...v1.6.0) (2026-11-18)


### Features

* let a game pick the stats panel's key or turn it off ([6841eba](https://github.com/Artmann/super/commit/6841eba0a6955b0cf5ee9cb2a5e70d9e1cd43b5f))
* show an engine stats panel with F3 or --stats ([76943bf](https://github.com/Artmann/super/commit/76943bf927b414d879d4f25f8b5e88303e330ff2))


### Performance Improvements

* a flickering light no longer re-marks the light probes round it every frame ([eda57db](https://github.com/Artmann/super/commit/eda57db62505f5ad561bbf32b179584c2d9fe900))
* keep still lamp shadows between frames and walk one AO slice at full resolution ([3add6f5](https://github.com/Artmann/super/commit/3add6f5e7b55af0dd68774a82ad95893377bc7b1))

## [1.5.0](https://github.com/Artmann/super/compare/v1.4.0...v1.5.0) (2026-11-18)


### Features

* 64 reflection probes that update during play without a hitch ([#139](https://github.com/Artmann/super/issues/139)) ([c97b92a](https://github.com/Artmann/super/commit/c97b92a9a0469eeef9b2c6a21884dff506ffed0e))
* animation events from the import settings and from code ([#136](https://github.com/Artmann/super/issues/136)) ([88ccdeb](https://github.com/Artmann/super/commit/88ccdebd28777bab56d4d9e54e498d6621ee4d89))
* sounds tied to animation events, such as footsteps ([#138](https://github.com/Artmann/super/issues/138)) ([eec1699](https://github.com/Artmann/super/commit/eec16991cb146e3b02a7f9c1fbe8198d41617019))

## [1.4.0](https://github.com/Artmann/super/compare/v1.3.1...v1.4.0) (2026-11-17)


### Features

* change the sky every frame without waiting for the GPU ([#124](https://github.com/Artmann/super/issues/124)) ([63645b0](https://github.com/Artmann/super/commit/63645b0b3726583870588bbf47a585ff59c4f2f6))
* moon and stars in the night sky; Hamlet on the physical sky ([#131](https://github.com/Artmann/super/issues/131)) ([73ebade](https://github.com/Artmann/super/commit/73ebadec0fd351550c689a4f493c3fb2816555cc))
* photographed HDRI skies with rotation and exposure ([#132](https://github.com/Artmann/super/issues/132)) ([f5d7ad2](https://github.com/Artmann/super/commit/f5d7ad29f013aa543dfc63c0b98db148b7f74611))
* physical sky that follows the sun ([#129](https://github.com/Artmann/super/issues/129)) ([3ea2014](https://github.com/Artmann/super/commit/3ea20145253a785f16b15003fc3decef5e88efc0))
* steadier, crisper ambient occlusion with a history over frames ([#134](https://github.com/Artmann/super/issues/134)) ([15df740](https://github.com/Artmann/super/commit/15df74028dd48ae02b426b5ec513acf1937f51ec))


### Bug Fixes

* don't count zero skin weights as a vertex's joints ([#128](https://github.com/Artmann/super/issues/128)) ([381338d](https://github.com/Artmann/super/commit/381338d87539770d78d1271880b5a3de6fab4b13))

## [1.3.1](https://github.com/Artmann/super/compare/v1.3.0...v1.3.1) (2026-11-17)


### Bug Fixes

* **taa:** weigh each frame's sample by how near the pixel's middle it fell ([#125](https://github.com/Artmann/super/issues/125)) ([7126cb7](https://github.com/Artmann/super/commit/7126cb7ce15bbaf13fec8d2a9a8f9d83cf299ba7))

## [1.3.0](https://github.com/Artmann/super/compare/v1.2.1...v1.3.0) (2026-11-17)


### Features

* fog volumes and probe-lit volumetric fog ([#122](https://github.com/Artmann/super/issues/122)) ([168e622](https://github.com/Artmann/super/commit/168e6227d7240497455560f1d01a3a9f26a6c307))
* generated meshes with the same surface share one material ([4120c38](https://github.com/Artmann/super/commit/4120c389c50d3c6d0521c0a18bac21af8a129ebe))
* run on Apple silicon Macs with nothing to set up ([3c7e593](https://github.com/Artmann/super/commit/3c7e59351c2250a5f0ea441564bac6cbc61bb421))
* screen-space bounce light from the occlusion pass ([#123](https://github.com/Artmann/super/issues/123)) ([ad6c377](https://github.com/Artmann/super/commit/ad6c37779ef603c056e632b6b7c88f5a4e6cb48a))

## [1.2.1](https://github.com/Artmann/super/compare/v1.2.0...v1.2.1) (2026-11-15)


### Bug Fixes

* build the cook tool before the solution on CI ([284402d](https://github.com/Artmann/super/commit/284402d8abceccebfa81af34b34bc36769bf04b0))
* fit ragdoll parts to rigs scaled by a joint above the hips ([9978958](https://github.com/Artmann/super/commit/9978958cd215c6fa2a4a00adb431fe8323ce6bef))
* run the loading tests' blocking work on threads of their own ([2459231](https://github.com/Artmann/super/commit/24592312f5756360bdd53b9ffca6ef798bafd436))

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
