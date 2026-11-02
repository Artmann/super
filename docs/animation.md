# Animation

Super plays skeletal animation on rigged models. You import a character and its clips,
describe its **states** (idling, walking, running, sitting) and its **actions** (waving,
shooting, looking), and an `Animator` blends between them. The GPU skins the model, and
its shadow bends with it.

`examples/Fox` is a working character: a walk-to-run blend driven by speed, an idle
when it stops, and a head turn played over either. Put the Khronos
[Fox.glb](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Fox) in
`assets/source/fox` and cook it first:

```
dotnet run -c Release --project tools/Rendition.Import -- assets/source/fox/Fox.glb assets/fox.materials.json assets/cooked/fox.rmodel
```

W and S, or up and down, change the pace, and Space or a click looks around.

## Importing a rigged model

A rigged FBX or glTF goes through the same import tool as any model (see
[Rendering](rendering.md#models)). When a mesh has bones, the importer keeps the
skeleton, the bone weights and every clip in the file, sampled at 30 frames a second.

Mixamo and similar tools hand out one file per animation. List the extra files in the
materials file, next to the model. Their clips are matched to the model's joints by
name, so they must be from the same rig:

```json
{
  "targetLength": 1.8,
  "animations": [
    { "file": "walk.fbx", "name": "Walk" },
    { "file": "run.fbx", "name": "Run" },
    { "file": "wave.fbx" }
  ]
}
```

Without a `name`, a clip is named after its file. Download Mixamo clips "without skin"
to keep them small; the character itself comes from one file "with skin".

A mesh without bones that sits under a joint, such as a sword in a hand node, follows
that joint.

## The rig

`content.Rig(path)` reads the skeleton and clips of a cooked model. Name the model with
`content.Model` as usual to draw it:

```csharp
_model = content.Model("fox.rmodel");
var rig = content.Rig("fox.rmodel");

ClipRef walk = rig.Clip("Walk");          // Throws, listing the clips, if there's none.
BoneMask head = rig.Mask("b_Neck_04");    // This joint and everything under it.
Motion pace = rig.Blend((walk, 0.6f), (rig.Clip("Run"), 2.4f));
```

A **mask** limits an action to part of the body: the upper body for shooting while the
legs keep walking, or an arm for a wave. A **blend** picks between clips by a number,
such as speed. Between two points it mixes the two clips, and beyond the ends it plays
the nearest one.

## The controller

An `AnimationController` describes a kind of character once. Its states and actions are
your own enums:

```csharp
enum FoxState { Idle, Moving }
enum FoxAction { Look }

var controller = new AnimationController<FoxState, FoxAction>(rig) { CrossFadeSeconds = 0.3f }
    .State(FoxState.Idle, rig.Clip("Survey"))
    .State(FoxState.Moving, rig.Blend((rig.Clip("Walk"), 0.6f), (rig.Clip("Run"), 2.4f)))
    .Action(FoxAction.Look, rig.Clip("Survey"), rig.Mask("b_Neck_04"), fadeSeconds: 0.4f);
```

| Call | What it does |
| --- | --- |
| `State(state, clip, speed)` | A base state that loops one clip |
| `State(state, motion, speed)` | A base state that loops a blend, picked by `Parameter` |
| `Action(action, clip, mask, fadeSeconds, speed)` | A one-shot clip played over the state. Without a mask it covers the whole body |
| `CrossFadeSeconds` | How long a change of state blends, 0.2 by default |
| `Start(state)` | An `Animator` for one character, in this state |

Share one controller between every character of a kind, and give each its own animator.

## The animator

The animator is gameplay state: `Update` decides what the character is doing, and
`Draw` shows it.

```csharp
public void Update(GameTime time)
{
    _animator.Set(Speed < 0.05f ? FoxState.Idle : FoxState.Moving);
    _animator.Parameter = Speed;
    if (actions.Pressed(Control.Look))
    {
        _animator.Play(FoxAction.Look);
    }

    _animator.Update(time);
}

public void Draw(View view) => view.Model(FoxKey, _model, transform, _animator.Pose);
```

| Member | What it does |
| --- | --- |
| `Set(state)` | Changes the base state, cross-fading from the old one. Setting the same state does nothing |
| `IsIn(state)` | Whether it's in that state |
| `Parameter` | Picks the mix in a blended state |
| `Phase` | How far through the state's cycle, from 0 to 1 |
| `IsCrossFading` | Whether a change of state is still blending |
| `Play(action)` | Plays an action from its start. Playing it again restarts it |
| `Stop(action)` | Ends an action early |
| `IsPlaying(action)` | Whether an action is still playing. It ends when its clip does |
| `Crossed(action, seconds)` | True on the one tick an action passed that moment of its clip |
| `Update(time)` | Moves on one tick. Call it every tick |
| `Pose` | What to draw, for `view.Model` |

**Clips in step.** A state keeps one cycle phase. A blended state plays its clips at
speeds that make them finish their cycles together, so a walk and a run mixed half and
half don't kick out of step, and changing state keeps the phase, so feet land where
they would have.

**Actions over states.** Actions fade in and out over their `fadeSeconds` and play over
whatever the state is doing. With a mask, the rest of the body carries on: walk and wave,
walk and shoot. Several actions can play at once; later ones in the enum go on top.

**Moments in an action.** Use `Crossed` to make things happen partway through a clip,
so a shot fires when the trigger is squeezed rather than when the clip starts:

```csharp
if (_animator.Crossed(SoldierAction.Shoot, 0.25f))
{
    FireBullet();
}
```

`Crossed(action, 0)` is true on the tick the action starts.

**Between ticks.** The game ticks 30 times a second, but the pose keeps moving between
ticks when drawn, so animation is as smooth as the frame rate. Because the animator
only changes in `Update`, a seed replays the same.

## With the ECS

Add an `Animated` component next to `Model`, and `view.Models(world)` draws the entity
in its pose. Systems update the animators:

```csharp
world.Add(fox, new Animated(controller.Start(FoxState.Idle)));

foreach (var row in world.Query<Animated>())
{
    row.First.Value.Update(time);
}
```

## Limits

- A model can have up to 256 joints, and a frame can draw up to 16,384 bones. Models
  past that in one frame are drawn in their bind pose.
- The pose given to `view.Model` must come from the model's own rig, and the model must
  have been cooked with a skeleton. Drawing a pose whose joint count differs throws.
- Clips are sampled at 30 frames a second when imported, and blended linearly between
  frames.
- There's no root motion: the character's movement comes from your game code, and clips
  that walk away from the origin will slide back each loop.
- Attaching a model to a bone, such as a sword to a hand that comes from a separate
  file, isn't built yet; see [Drawing with views](views.md#planned).
- Skinned models don't give temporal anti-aliasing motion vectors yet, so fast limbs can
  show slight smearing.
