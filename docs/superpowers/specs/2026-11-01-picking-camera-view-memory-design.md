# Picking, cameras and view memory

Picking and cameras let a game turn a click into a place in the world and stop every
game writing the same camera code. View memory is what lets a box lid swing without the
game storing how far open it is.

## Picking and cameras

Input can only be read in `Update`, and `view.Camera` was only known in `Draw`. So a
camera is now a plain value, `Camera`, that the game keeps as state and passes to both.

- `Camera(Position, Target, FieldOfView)` with `Ray(pixel, screen)` and `Lerp`.
- `input.MouseRay(camera)` follows the rule for all input and works in `Update` only. The
  host copies the screen size into `Input` at the start of each tick.
- `Ray` has `Ground(height)`, `HitsBall(center, radius)` and `HitsBox(min, max)`. Going
  through your own things is the game's job, as it knows what can be clicked.
- `OrbitCamera` and `FollowCamera` are small classes kept in the game, stepped in
  `Update`, and drawn with `view.Camera(camera.At(view.Blend))`.
- A camera pointing straight up or down has no sensible "up", which used to break the
  picture. `Camera.Up` handles it, and the renderer and `Ray` share it.

Calls made without asking:

- Picking tests rays against balls, boxes and heights the game names, not model
  triangles. `Update` has no list of what was drawn, and physics may want to own this.
- Follow smoothing is exponential, so it's the same at any tick rate. The first step
  goes straight to the target, as view memory does.

## View memory

- `view.Animate(key, target, seconds)` goes from where it is to the target over `seconds`
  with an ease in and out. A new target starts from the current value. The result depends
  only on game time, so frame rate doesn't change the curve.
- `view.Changed(key, value)` is true on the frame a value differs from the last one.
- `view.SecondsSince(tick)` takes `time.Tick` as saved in `Update`. It needs no memory.
- `view.Cached(key, version, data, draw)` records what `draw` adds and replays it until the
  version changes.

Calls made without asking:

- **Memory belongs to the place in the code as well as the key.** `Animate` and `Changed`
  record the calling file and line, like a hook's position in React. So a lid and a latch
  on one box can share a key, and a box and a tree that both have ID 5 can't collide.
- **What isn't drawn is forgotten.** A key skipped for one frame is dropped, so memory
  can't grow forever, and something that comes back appears finished instead of
  replaying. This also fits the rule that a new key goes straight to its target.
- **`Cached` takes a key, which the README's old example didn't.** With only a version,
  two chunks both at version 1 would share one recording.
- `Cached` keeps models only. It doesn't save lights or the camera, and it still copies
  each model into the frame. It saves describing them, not the renderer's work.
- `SecondsSince` takes a tick and not a time in seconds, so the value is exact and
  a saved game can store it.
