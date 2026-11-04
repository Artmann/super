# Animation

Super plays skeletal animation on rigged models. You import a character and its clips,
describe its **states** (idling, walking, running, sitting) and its **actions** (waving,
shooting, looking), and an `Animator` blends between them. The GPU skins the model, and
its shadow bends with it.

`examples/Fox` is a working character: a walk-to-run blend driven by speed, an idle
when it stops, and a head turn played over either. Put the Khronos
[Fox.glb](https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/Fox) in
`assets/source/fox` and cook it first. Models are cooked by `Super.Cook.Models` through
the asset pipeline; the command to run it comes with the build integration.

W and S, or up and down, change the pace, and Space or a click looks around.

## Importing a rigged model

A rigged FBX or glTF goes through the same model cooker as any model (see
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

`content.Rig(path)` reads the skeleton and clips of a cooked model, and names the model
too, so you don't need `content.Model` for a character:

```csharp
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

The animator is gameplay state. Your `Update` says what the character is doing, and the
engine does the rest: after every `Update`, the game loop moves each animator on by one
tick. You never step it yourself.

```csharp
public void Update(GameTime time)
{
    _animator.Set(Speed < 0.05f ? FoxState.Idle : FoxState.Moving);
    _animator.Parameter = Speed;
    if (actions.Pressed(Control.Look))
    {
        _animator.Play(FoxAction.Look);
    }
}

public void Draw(View view) => view.Model(FoxKey, _animator, transform);
```

`view.Model(key, animator, transform)` draws the rig's model in the animator's current
pose. Only the transform is yours to give, since where the character stands is game
state. With the ECS, even that goes away; see below.

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
| `Speed` | How fast everything plays, 1 by default. 0 holds the character still, such as while the game is paused |
| `Pose` | What to draw, for `view.Model(key, model, transform, pose)` when you pick the model yourself |

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

`Crossed(action, 0)` is true on the tick the action starts, right after `Play`. Later
moments come true in a later `Update`, once the engine has moved the clip past them.

**Who runs it.** Every animator started from a rig that came from `content.Rig` is run
by the game loop, until nothing in the game holds it any more. Drop a character, or
destroy its entity, and its animator stops with it. Animators move on game ticks, not
frames, so a seed replays the same. Pausing doesn't stop them, because the loop keeps
ticking, so set `Speed = 0` on the characters you want frozen.

**Between ticks.** The game ticks 30 times a second, but the pose keeps moving between
ticks when drawn, so animation is as smooth as the frame rate.

**Slow motion.** Animators from content move by `GameTime.DeltaSeconds`, so they slow
down with [`GameTime.Scale`](getting-started.md#a-first-game), as in a
[cutscene](cutscenes.md#slow-motion).

## Root motion

Mixamo's dodges, rolls and dives move the body across the ground. Played as they are,
the mesh leaves the character behind and snaps back when the clip ends. Root motion
takes that travel out of the clips and hands it to your game instead:

```csharp
var rig = content.Rig("archer.rmodel");
rig.RootMotion("mixamorig:Hips");
```

Name the joint that carries the body. Its travel over the ground comes out of every clip,
while its height and turns stay in, so a jump still leaves the ground. Each tick,
`animator.RootMotion` says how far the clips carried the character, in the model's
space. Turn it by the character's rotation and scale, then add it to where they stand:

```csharp
_position += Vector3.Transform(_animator.RootMotion, Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(_facing));
```

Blends and actions mix it as they mix the pose, so a dodge fading in moves the
character a little at first, and a loop carries on from where its last cycle ended.
Call `RootMotion` once per rig, before starting animators. Asking again for the same
joint does nothing, and asking for another throws.

## Joints

`animator.Joint(name)` is where a joint is in the model this tick, such as the hand an
arrow leaves from. Multiply it by the character's own transform to place it in the world:

```csharp
var nock = _animator.Joint("mixamorig:arrow") * transform;
var arrow = new Arrow(nock.Translation, direction);
```

An unknown name throws, listing the joints the rig has. It's worked out once per tick,
however often you ask.

## Setting joints yourself

For procedural motion, such as feet placed on uneven ground by IK or a head turned to
look at something, set joints directly. `pose.SetJoint` takes a joint's local pose,
relative to its parent, and puts it over whatever the clips give. The others still
follow the clips, or stay at rest if there are none:

```csharp
var rig = content.Rig("bug.rmodel");
var pose = new AnimationPose(rig);
int knee = pose.JointIndex("Leg_F_Tibia_L");

public void Draw(View view)
{
    // Work out from state already blended with view.Blend; set joints aren't moved ahead.
    pose.SetJoint(knee, new JointPose(rest.Position, kneeTurn, Vector3.One));
    view.Model(BugKey, rig.Model!.Value, transform, pose);
}
```

A set joint stays until it's set again or `pose.ClearJoints()` hands every joint back to
the clips. `Clear()` only takes away the layers. It works on an animator's pose too
(`animator.Pose`), so IK can go over a walk clip. The skeleton's `Rest`, `Parents` and
`Names` give what's needed to work out where joints are in the model.

## With the ECS

Give an entity a `Transform` and an `Animated`, and `view.Models(world)` draws the rig's
model in the animator's pose. There's nothing to update and nothing more to draw:

```csharp
var fox = world.Create();
world.Add(fox, Transform.At(position));
world.Add(fox, new Animated(controller.Start(FoxState.Idle)));
```

Systems only change what it's doing, such as
`world.Get<Animated>(fox).Value.Set(FoxState.Moving)`. Add a `Model` too when you want
a tint, or to draw a different model with the same rig.

## Limits

- A model can have up to 256 joints, and a frame can draw up to 16,384 bones. Models
  past that in one frame are drawn in their bind pose.
- The pose given to `view.Model` must come from the model's own rig, and the model must
  have been cooked with a skeleton. Drawing a pose whose joint count differs throws.
- Clips are sampled at 30 frames a second when imported, and blended linearly between
  frames.
- Root motion only carries travel over the ground. Turns in a clip, such as a turn on
  the spot, stay in the pose and don't turn the character.
- Attaching a model to a bone, such as a sword to a hand, isn't built in. Draw it
  yourself at `animator.Joint(name)` times the character's transform; see
  [Drawing with views](views.md#planned).
- Skinned models don't give temporal anti-aliasing motion vectors yet, so fast limbs can
  show slight smearing.
