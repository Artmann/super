# Cutscenes

A cutscene is a short film made of the game itself. You line up **shots**, each a
camera move, add **moments** when something happens, such as a character drawing a bow,
and mark stretches of **slow motion**. The `Director` plays it: while it runs, it holds
the camera and the time scale, and when it ends both go back to the game.

The game keeps running underneath. Characters animate, arrows fly and dummies wobble
in `Update` as they always do; the cutscene only says where the camera is and nudges
the story along. `examples/Archer` is a 17-second film made this way.

```csharp
public sealed class MyGame(Director director) : IGame
{
    public void Load()
    {
        director.Play(new Cutscene()
            .Then(Shot.Move(3f,
                new Camera(new Vector3(0f, 8f, -12f), Vector3.Zero, 0.8f),
                new Camera(new Vector3(0f, 1.6f, -3f), new Vector3(0f, 1.4f, 0f), 0.6f)))
            .At(1.5f, () => hero.Animator.Play(Move.Wave))
            .Then(Shot.Orbit(4f, () => hero.Position, radius: 3f, height: 0.5f,
                fromAngle: 0f, toAngle: MathF.PI, look: new Vector3(0f, 1.4f, 0f)))
            .Slow(4f, 6f, 0.25f));
    }
}
```

Ask for the `Director` in your game's constructor, like any other service.

## Shots

`Then` adds a shot after the last one, and moving from one shot to the next is a cut.
The cutscene lasts as long as its shots end to end.

| Shot | What it does |
| --- | --- |
| `Shot.Hold(seconds, camera)` | A camera that doesn't move |
| `Shot.Move(seconds, from, to, easing)` | Moves from one camera to another: a crane when it rises, a dolly when it rolls in, a pan when only the target moves |
| `Shot.Track(seconds, subject, from, to, look, fieldOfView, easing)` | Rides along with something, sliding from one offset to another while looking at a point above it |
| `Shot.Orbit(seconds, subject, radius, height, fromAngle, toAngle, look, fieldOfView, easing)` | Circles something, from one angle to another |
| `Shot.Along(seconds, rail, subject, look, fieldOfView, easing)` | Rides a [spline](splines.md) from its start to its end at an even speed, looking ahead along it or at a point above the subject |
| `Shot.Custom(seconds, camera, easing)` | Any move: a function gets a `ShotTime` and gives a camera |

A subject is a function, such as `() => hero.Position`. It's asked every tick, so the
camera keeps up with a character as it runs. Track offsets are in the world's
directions, not the subject's. Orbit angles are in radians, where 0 looks from +z and
a quarter turn looks from +x.

`ShotTime` has `Amount`, how far through the shot from 0 to 1 after easing, and
`Seconds`, the time since the shot began. A custom shot can ride an arrow, cut between
two dummies, or swing round a character the short way:

```csharp
.Then(Shot.Custom(2f, _ => BehindArrow(game.LastArrow)))
.Then(Shot.Custom(5f, time => Camera.Lerp(close, wide, time.Amount), Easing.InOut))
```

**Easing.** `Linear` keeps the same speed all the way. `In` starts slow, `Out` slows to a
stop, and `InOut` does both, like a camera on a crane. Moves, tracks and orbits ease
`InOut` by default; holds and custom shots are `Linear` unless you say otherwise. Any
other easing works too, such as `SineInOut` for a gentler move or `BackOut` for one
that goes a little past and settles; [Tweens and curves](tweens.md) lists them all.
`Ease.Apply(easing, amount)` gives the same curves for your own use, and a custom shot
can shape its move with a `Curve`, as in `curve.Sample(time.Amount)`.

## Moments

`At(seconds, action)` runs something at a moment, counted from the start of the
cutscene. It runs during that tick, just before `Update`, so the game sees it at once.
Moments that share a tick run in the order they were added. A moment can be anywhere
in the cutscene; it needn't line up with a shot.

## Slow motion

`Slow(from, to, scale, ramp)` slows the world to `scale` between two moments, easing in
and out over `ramp` seconds at each end so it doesn't jolt. It sets
[`GameTime.Scale`](getting-started.md#a-first-game), so anything moved by
`DeltaSeconds` slows, and so do animators from content.

The cutscene's own clock counts real ticks. Shots keep their pace and moments keep their
place while the world runs slow, so a shot of an arrow in slow motion is still two
seconds long on screen.

## The director

| Member | What it does |
| --- | --- |
| `Play(cutscene)` | Starts a cutscene from its beginning on the next tick, ending any that's playing |
| `Stop()` | Ends it now, giving back the camera and the time scale |
| `IsPlaying` | Whether a cutscene is playing |
| `Playing` | The cutscene playing, or none |
| `Seconds` | How far into it, in real seconds |

While a cutscene plays, the director's camera wins over whatever `Draw` sets. Keep
setting a camera in `Draw` anyway: it's the one the game shows once the film ends, so
the last shot can land on it without a jump. The camera is placed after `Update`, so a
shot following something sees where it is now, and frames blend between ticks like
everything else. A cut doesn't blend; it jumps.

When the cutscene ends, the time scale goes back to what it was when it started.

## Recording one

`--record` captures the renderer's own frames, so a cutscene recorded twice comes out
the same:

```
dotnet run -c Release --project examples/Archer -- --record out/clearing.mp4 --seconds 18
```

To review a film quickly, record at a low frame rate and tile the frames into contact
sheets, so one picture shows a few seconds:

```
dotnet run -c Release --project examples/Archer -- --record out/draft.mp4 --seconds 18 --fps 5
ffmpeg -i out/draft.mp4 -vf "scale=400:-1,tile=4x4" out/sheet-%02d.png
```

## Limits

- The camera can't roll, so there are no dutch angles.
- There's no depth of field or motion blur yet.
- Shots cut; there are no cross-fades or wipes between them.
- A cutscene is code. There's no timeline editor and no file format for one.
