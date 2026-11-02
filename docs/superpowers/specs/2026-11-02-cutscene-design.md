# Cutscenes and the Archer film

## Intent

Super should be able to make short films out of a running game: a camera that moves
on its own through planned shots, things that happen on cue, and slow motion. The first
film is "The Clearing", a Robin Hood-style action sequence: Erika, an archer, shoots
straw dummies while she dodges, leaps onto a boulder and dives off it. Its video shows
what the engine can do.

## Goals

- Cutscenes are code, built from shots end to end, moments and slow stretches.
- The game keeps running underneath. The cutscene only holds the camera and the time
  scale, and calls the game at its moments.
- Repeatable: everything lands on ticks, so `--record` gives the same film twice.
- The camera keeps real time while the world slows.
- Characters move with their clips: root motion for dodges and dives, and joint
  positions so arrows leave from the bow hand.

## Non-goals

- A timeline editor or a file format for cutscenes.
- Camera roll, depth of field, motion blur and cross-fades.
- Skipping or scrubbing a playing cutscene.

## The shape

- `Shot`: `Hold`, `Move`, `Track`, `Orbit` and `Custom`, each a function from
  `ShotTime` (eased amount and seconds) to a `Camera`. Subjects are functions, asked
  every tick.
- `Easing` and `Ease.Apply`: linear, in, out and in-out cubic curves.
- `Cutscene`: `Then(shot)`, `At(seconds, action)` and `Slow(from, to, scale, ramp)`.
- `Director`, a service: `Play`, `Stop`, `IsPlaying`, `Playing` and `Seconds`.
  `GameHost` calls it before `Update` (clock, scale, moments) and after (camera).
  `Draw` still sets a camera; the director's wins while it plays. A cut never blends.
- `GameTime.Scale` and `DeltaSeconds`; animators from content advance by
  `DeltaSeconds`, and views pass the scale on so poses stay smooth between ticks.
- `Rig.RootMotion(joint)` and `Animator.RootMotion`; `Animator.Joint(name)`.

## The film

1. A swoop down to Erika looking round the clearing at golden hour.
2. Over her shoulder as she draws on the first dummy.
3. Slow motion riding the arrow in.
4. A dodge and a sprint for the boulder, tracked from the side.
5. The leap onto the boulder, from low down.
6. Slow motion circling her on the rock as she shoots the second dummy.
7. A dive off the rock into a roll.
8. Two quick shots at the third dummy, cut fast.
9. A cocky flourish as the camera pulls back over the clearing.

The clearing needed a sky with haze (`view.Sky`), ground that rises into hills, and
Poly Haven plants, which needed the importer's `node` and `alpha` options.

## Left to later

Level of detail for the trees, film effects, and bone attachment as a view call.
