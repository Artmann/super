# Skeletal animation design

Characters need rigs, clips and a way to play them. Nothing of that existed on
2026-11-02: the importer flattened every model into one pose, vertices carried no bone
weights, and the shaders couldn't skin. This builds the whole path, from a rigged glTF
or FBX file to a character that walks while it waves. It was built in one autonomous
session from the request alone, so the calls below are mine, not agreed.

## What the game writes

```csharp
enum Stance { Idle, Walk, Run, Sit }
enum Act { Wave, Shoot }

// Load: the rig is read from the cooked model, on the main thread, right away.
Rig rig = content.Rig("adventurer.rmodel");
var upperBody = rig.Mask("mixamorig:Spine");
_animations = new AnimationController<Stance, Act>(rig)
    .State(Stance.Idle, rig.Clip("idle"))
    .State(Stance.Walk, rig.Clip("walk"))
    .State(Stance.Run, rig.Clip("run"))
    .State(Stance.Sit, rig.Clip("sit"))
    .Action(Act.Wave, rig.Clip("wave"), upperBody)
    .Action(Act.Shoot, rig.Clip("shoot"), upperBody, fadeSeconds: 0.05f);
_player = _animations.Start(Stance.Idle);

// Update: the animator is plain state, advanced in ticks.
_player.Set(moving ? Stance.Walk : Stance.Idle);
if (fire) { _player.Play(Act.Shoot); }
if (_player.Crossed(Act.Shoot, 0.3f)) { SpawnBullet(); }
_player.Update(time);

// Draw: the pose is handed over like a colour or a swim.
view.Model(key, adventurer, transform, _player.Pose);
```

With the ECS, `world.Add(player, _player)` and `view.Models(world)` draws it.

## Layers of the design

- **States** are the base layer: what the body is doing. Setting a new state cross-fades
  from the old one over `CrossFadeSeconds` (0.2 by default). State clips loop. A state
  can also be a 1D blend, `rig.Blend((walk, 1.5f), (run, 4f))`, picked by a parameter
  such as speed; its clips advance in step so feet match.
- **Actions** play once over the base, optionally on a mask of bones. A masked action
  only moves those bones, so a wave plays on the arms while the legs keep walking. They
  fade in and out over `fadeSeconds`. Playing an action that's already playing restarts
  it. Several different actions can play at once.
- **Masks** are a bone and all its descendants, by name, from `rig.Mask("spine")`.
- **Moments** come from `Crossed(action, seconds)`, true on the tick the action's time
  passes that point, so the shot lands on one tick and replays with the seed.

## Where things live

| Thing | Lives in | Why |
| --- | --- | --- |
| Skeleton, clips, weights | `Rendition.ModelData`, written before the big vertex and texture data | `Content.Rig` reads only the front of the file, so the game has clip names and lengths at load, on the main thread |
| Pose maths: sampling, blending, skin matrices | `Rendition` (`Animation.cs`), pure CPU | Testable with no GPU, and the renderer is the one that needs matrices |
| `AnimationController`, `Animator`, `AnimationPose` | `Super` | Game-facing. The animator is state the game ticks; the pose is view data |
| Pose evaluation per frame | `SceneRenderer` | The view only writes down which clips play at what time and weight. Headless runs see that and need no clip data |
| Bone matrices on the GPU | One storage buffer per frame in flight, in the frame set at binding 1, indexed by `DrawItem.Bones` | Every pass that draws geometry shares it, so shadows skin for free |

## Vertex and shader

`Vertex` gains `Joints` (four bytes) and `Weights` (four normalized bytes), 56 bytes
instead of 48. A model with no skin leaves them zero and draws as before; the vertex
shader skins only when the draw's `firstBone` is 0 or more. One vertex layout and one
pipeline per shader keeps the Vulkan side unchanged. A model can have up to 256 joints.

A skinned part's bounding sphere is its rest pose's, doubled, so a stretched pose isn't
culled at the edge of the screen. Simpler versions of the mesh keep the vertices' weights,
since they're only another index list.

## Import

Files with bones skip `PreTransformVertices`. The skeleton is every node that a bone names
or that lies between one and the root. Every clip is sampled at 30 frames per second into
one `JointPose` (position, rotation, scale) per joint per frame, so sampling at runtime is
one interpolation and blending needs no key search. Mixamo FBX files are read without
Assimp's pivot nodes, so their joint names match between files. Extra clips come from
other files through the settings file:

```json
{ "animations": [{ "file": "walk.fbx", "name": "walk" }] }
```

They're matched to the character's joints by name. The scale, centring and `rotateY`
that normalize a static model fold into the skeleton's `RootTransform` instead of the
vertices, because skinning happens before it.

## Time

`Animator.Update(time)` moves every layer by one tick. `Pose` carries each layer's time
and speed, and `View.Model` adds `Blend` ticks of speed, so motion is smooth at any frame
rate without the animator knowing about frames. Weights change per tick only.

## Not built

- `view.Bone(key, "hand_r")` for attachments. The rig is on the CPU now, so it's a small
  step: evaluate the pose in the view when asked.
- Root motion. Clips play in place; use "in place" clips, or move the transform yourself.
- Animation events at a frame set by an artist; `Crossed` covers it from code.
- Inverse kinematics, additive layers, 2D blends.

## Calls made without asking

- "Summer rigs" was read as Mixamo rigs. The FBX path is built for them but untested in
  this session, since no Mixamo file was to hand. The Khronos Fox sample (CC-BY) stands
  in: it has a skin and three clips.
- Clips are sampled at 30 Hz, not kept as keys. Memory is about 40 bytes per joint per
  frame, around 150 KB for a two-second Mixamo clip.
- Joint and weight bytes are added to every vertex rather than kept in a second stream.
- `Animator` is a class that keeps a reference to its controller, so the ECS draw needs
  no generic type. The ECS component is `record struct Animated(Animator Value)`, since
  components must be structs.
