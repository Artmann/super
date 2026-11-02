# What's not here yet

Super is early. These are the known gaps, so you can plan around them.

- **Particles and bone attachment.** Skeletal [animation](animation.md) works, but
  `view.Bone`, for hanging a model on a joint, and `view.Particles` don't exist yet. See
  [Drawing with views](views.md#planned). Until then, `animator.Joint` gives a joint's
  place for drawing a model there by hand.
- **Level of detail.** Every model is drawn at full detail however far away it is, so
  dense forests of high-poly trees are slow.
- **Film effects.** [Cutscenes](cutscenes.md) cut between shots, but there's no depth
  of field, motion blur or cross-fade yet.
- **Audio.** There's no audio package yet.
- **Picking by shape.** Clicks are tested against balls, boxes and the ground, not a
  model's triangles.
- **Some camera moves.** A camera can't roll, and asymmetric views are a shift, so head
  tracking and tiled displays can't be done yet. Underwater views are wrong under an
  orthographic camera.
- **Large numbers of objects.** Rendition's efficiency with many thousands of objects
  still has to be measured and improved.
- **Physics.** Not in the first version. Gameplay physics has to give repeatable
  results, so the engine needs choosing carefully. Until then, write your own movement
  code in `Update`.
- **Typing in interfaces.** The interface takes the mouse, but there are no text inputs,
  and it can't be used with a keyboard or gamepad yet.
- **Saved games and levels.** `saves.Write` and `saves.Read` are the plan; see
  [JSON content](content.md#planned).
- **Run logs.** Recording game events and checking them in tests is planned; see
  [Testing](testing.md#planned).
- **`--headless` and `--json`.** A command-line run with no GPU that prints a result.
  Tests already run headless.
- **Mods.** A mod should be a folder of JSON that adds or replaces entries by id, but only
  one content folder can be read so far.
- **`Vector3` fields in JSON.** Use separate numbers or an array for now.
- **Unloading game states.** Entities can be [scoped to a state](ecs.md#entities-that-belong-to-a-state),
  but models and other content stay loaded unless you call `content.Unload`.
- **NuGet packages.** Clone the repository and reference the projects for now.
- **Other platforms.** Windows only for now, with Linux kept possible.

An editor is not planned. Super is a library, and your code and JSON files are the source
of truth.
