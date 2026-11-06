# What's not here yet

Super is early. These are the known gaps, so you can plan around them.

- **Bone attachment.** Skeletal [animation](animation.md) works, but `view.Bone`, for
  hanging a model on a joint, doesn't exist yet. See
  [Drawing with views](views.md#planned). Until then, `animator.Joint` gives a joint's
  place for drawing a model there by hand.
- **More from particles.** [Particles](views.md#particles) are soft puffs for dust and
  smoke. There are no textured, glowing or stretched sparks yet, and they cast no
  shadow. Nodes must work from the age alone, so particles can't bounce off the
  ground or react to what they hit.
- **Film effects.** [Cutscenes](cutscenes.md) cut between shots, but there's no depth
  of field, motion blur or cross-fade yet.
- **More from audio.** [Audio](audio.md) plays stereo sound with no echo or reverb, walls
  that muffle sound, Doppler or surround. There is no ducking (music dipping under
  speech), no ambience that blends by where the camera stands, and sounds can't be tied
  to animation moments yet. A `Crossed` event can start a one-shot by hand.
- **Picking by shape.** Clicks are tested against balls, boxes, capsules and the ground,
  not a model's triangles.
- **Some camera moves.** A camera can't roll, and asymmetric views are a shift, so head
  tracking and tiled displays can't be done yet. Underwater views are wrong under an
  orthographic camera.
- **Large numbers of objects.** Rendition's efficiency with many thousands of objects
  still has to be measured and improved.
- **Physics.** [`Super.Physics`](physics.md) answers ray queries against colliders, on
  Jolt. Bodies that fall and collide, shape casts, overlap tests and character
  controllers are next. Until then, write your own movement code in `Update`.
- **Typing in interfaces.** The interface takes the mouse, but there are no text inputs,
  and it can't be used with a keyboard or gamepad yet.
- **Saved games and levels.** `saves.Write` and `saves.Read` are the plan; see
  [JSON content](content.md#planned).
- **Run logs.** Recording game events and checking them in tests is planned; see
  [Testing](testing.md#planned).
- **`--headless` and `--json`.** A command-line run with no GPU that prints a result.
  Tests already run headless.
- **Asset pipeline, step 2: hot reload of every asset kind.** Step 1 is done: everything
  is cooked into a manifest and objects, and a game run from source cooks again on every
  save. Saving a JSON file reloads its catalogs, but a model, texture or sound that is
  already loaded keeps what it has until the game restarts. See
  [Cooking](content.md#cooking) and [Change it while the game runs](content.md#change-it-while-the-game-runs).
- **Material graphs, steps 2 to 6.** Step 1 is done: [material graphs](materials.md) cook
  into textures, with the Courtyard lineup of six starter graphs. Next come the Courtyard
  scene with a puddle surface shader, hot reload of a saved graph in the running game,
  `graph` and `repeat` in model sidecars, mesh bakes with a `Triplanar` node, and
  curvature and occlusion bakes as graph inputs. Graphs run only at cook time, and there
  is no node editor and no way to share helpers between graph files.
- **Mods.** A mod should be a folder of JSON that adds or replaces entries by id. Several
  content roots can be cooked together, but a name that two of them make is a cook error,
  so a mod can't replace anything yet.
- **Unloading game states.** Entities can be [scoped to a state](ecs.md#entities-that-belong-to-a-state),
  but models and other content stay loaded unless you call `content.Unload`.
- **NuGet packages.** Clone the repository and reference the projects for now.
- **Other platforms.** Windows only for now, with Linux kept possible.

An editor is not planned. Super is a library, and your code and JSON files are the source
of truth.
