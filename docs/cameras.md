# Cameras and clicking

A camera is an entity in the world, and `world.Spawn(new Camera(position, target))` is
all a fixed camera needs. A `Camera` is a plain value: a position, a target, a field of
view and optionally a `Size`, a `Shift` and a `WideAs`.

## A camera entity

A camera can be an entity in the [world](ecs.md), like everything else it shows. The
world takes each frame from it, before your `Draw` runs, so a game with a camera entity
needs no `view.Camera` at all:

```csharp
var camera = world.Spawn(new Camera(new Vector3(0, 12, 12), Vector3.Zero));
```

Write the camera in `Update` and the world blends it between ticks, the same as a moving
entity, so it's smooth at any frame rate. A [camera helper](#camera-helpers) drives one
by writing its `Current` each tick:

```csharp
public void Update(GameTime time)
{
    follow.Update(player.Position, time.TickSeconds);
    world.Get<Camera>(camera) = follow.Current;
}
```

A new camera shows where it is on its first frame. To cut rather than swing, such as
between rooms, place it with `world.Teleport(camera, newCamera)`.

When several entities have a `Camera`, the frame is taken from the newest. Take its
`Camera` away, or destroy it, and the one before it is used again, so a close-up can
come and go over the main camera. Give a camera a `Parent` and it rides along: its
position and target count from the parent, so one on a car follows the car. A camera
with its own `Transform` counts from that too.

`world.ActiveCamera()` gives the camera the frame is taken from, as of this tick and
placed in the world, or null when no entity has one. Pass it to `input.Mouse.Ray` to
click with the camera the player sees. A `view.Camera` in `Draw` still wins over the
world's, and a [cutscene](cutscenes.md) wins over both.

## Clicking on things

Input can only be read in `Update`, so that's where a click turns into a place in the
world. Make the ray with the camera the player sees:

```csharp
public void Load() => world.Spawn(new Camera(new Vector3(0, 12, 12), Vector3.Zero));

public void Update(GameTime time)
{
    if (input.Pressed(MouseButton.Left) && !input.Mouse.OverUi && world.ActiveCamera() is { } camera)
    {
        var ray = input.Mouse.Ray(camera);

        // A tile: where the line through the cursor meets the ground.
        if (ray.Ground() is { } spot)
        {
            selectedTile = map.TileAt(spot);
        }

        // A colonist: the nearest one the line goes through.
        float nearest = float.MaxValue;
        foreach (var colonist in colonists)
        {
            if (ray.HitsBall(colonist.Position, 0.5f) is { } distance && distance < nearest)
            {
                nearest = distance;
                selected = colonist.Id;
            }
        }
    }
}
```

| Call | Gives |
| --- | --- |
| `ray.Ground(height)` | The point where the ray meets a flat plane at that height, or `null` |
| `ray.HitsBall(center, radius)` | The distance along the ray, or `null` on a miss |
| `ray.HitsBox(min, max)` | The distance along the ray to an axis-aligned box, or `null` |
| `camera.Ray(pixel, screen)` | A ray through any pixel, without `Input` |

Super doesn't go through your things for you, since only your game knows what can be
clicked, and it tests these simple shapes, not a model's triangles. With the ECS, give
entities a `Collider` and [`Super.Physics`](physics.md) finds the nearest for you, by
layer. `input.Mouse.OverUi`
is true while the cursor is over the [interface](interfaces.md#the-mouse), so a game can
leave those clicks alone.

## Camera helpers

Six helpers cover the usual kinds of game. They all work the same way, and share it
through the `CameraRig` base class: keep one in your game, step it once a tick in
`Update`, and write its `Current` to a [camera entity](#a-camera-entity), which the world
blends between ticks so it moves smoothly at any frame rate. `Current` is the camera as
of the last tick, which is the one to pass to `input.Mouse.Ray`. Drawing by hand,
`view.Camera(rig.At(view.Blend))` gives the same blend.

| Helper | For |
| --- | --- |
| `OrbitCamera` | Circling a target, as in a strategy or model viewer |
| `FollowCamera` | Trailing behind a target with some lag |
| `BoomCamera` | Third person, on an arm that shortens when a wall is in the way |
| `FirstPersonCamera` | Looking out from the eyes |
| `TopDownCamera` | Looking straight down, orthographic |
| `IsometricCamera` | The true isometric angle, orthographic, turning by quarter turns |

To skip the writing, spawn the camera entity with `OnRig`. The world then copies the
rig's `Current` to the entity's `Camera` at the end of every tick, and all the game does
is step the rig:

```csharp
readonly FollowCamera follow = new();

public void Load()
{
    follow.Update(player.Position, 0f);                 // put it in place first
    world.Spawn(new OnRig(follow));
}

public void Update(GameTime time) => follow.Update(player.Position, time.TickSeconds);
```

The tick after a `Jump()` the entity cuts, as with `world.Teleport`. An entity whose rig
hasn't been updated yet shows a plain camera looking at the middle of the world, so
update the rig once before spawning it, as above. Write `Current` yourself when you
want to change it on the way, such as adding a shake.

The first update puts a camera straight in place. If its target teleports, call
`Jump()` so the camera doesn't slide across the map. It works the same when you set a
rig's `Center`, `Size` or turn yourself, such as to show a building the player picked:
the next update puts the camera there, with no blend from where it was. In a test, that
is one tick instead of waiting for the lag to settle:

```csharp
run.Game.Camera.Rig.Center = new Vector3(40, 0, -12);
run.Game.Camera.Rig.Jump();
run.Ticks(1);                                            // the camera is there now
```

### Orbit and follow

```csharp
readonly OrbitCamera orbit = new() { Distance = 12f };
readonly FollowCamera follow = new() { Offset = new Vector3(0, 8, 10), Lag = 0.2f };

public void Update(GameTime time)
{
    // Yaw, Pitch, Distance and its limits can also be set directly.
    orbit.Turn(input.Mouse.Delta * 0.005f);
    orbit.Zoom(input.Mouse.Scroll);
    orbit.Update(tower.Position);

    // Lag is about how many seconds behind the target it stays.
    follow.Update(player.Position, time.TickSeconds);
    world.Get<Camera>(camera) = follow.Current;
}
```

### Third person

`BoomCamera` sits on an arm behind the character. Super doesn't know your walls, so you
test the arm against them and pass in how far along it you hit something:

```csharp
readonly BoomCamera boom = new() { Length = 6f, Shoulder = 0.5f };

public void Update(GameTime time)
{
    boom.Turn(input.Mouse.Delta * 0.005f);
    boom.Zoom(input.Mouse.Scroll);
    var walk = boom.Walk(actions.Vector(Control.Move));

    var arm = boom.Arm(player.Position);
    boom.Update(player.Position, time.TickSeconds, blockedAt: arm.HitsBox(wall.Min, wall.Max));
}
```

The arm pulls in at once, so the camera never shows the inside of a wall, and eases back
out once the way is clear. With several walls, pass the nearest hit. With
[`Super.Physics`](physics.md#queries), a shape cast tests every wall at once, and a ball
keeps the camera's near plane out of them too:

```csharp
var blocked = physics.Queries.ShapeCast(Collider.Ball(0.3f), arm.Origin, arm.Direction, reach: boom.Length, layers: Walls);
boom.Update(player.Position, time.TickSeconds, blockedAt: blocked?.Distance);
``` `Walk` turns a stick
into a direction on the ground, so forward goes where the camera faces.

### First person

```csharp
readonly FirstPersonCamera eyes = new();

public void Load() => input.Mouse.Locked = true;

public void Update(GameTime time)
{
    eyes.Turn(input.Mouse.Delta * 0.003f);
    var walk = eyes.Walk(actions.Vector(Control.Move));
    eyes.Update(player.Position);
}
```

`FirstPersonCamera` looks out from `EyeHeight` above the feet. `Walk` stays level wherever
you look, so looking up doesn't slow you down.

### Top-down and isometric

Both are orthographic, and zooming changes `Size`:

```csharp
readonly IsometricCamera iso = new() { Size = 24f };

public void Update(GameTime time)
{
    iso.Zoom(input.Mouse.Scroll);

    // Pan by a step, or follow something with iso.Update(player.Position, seconds).
    iso.Pan(actions.Vector(Control.Move) * 20f * time.TickSeconds);
    iso.Update(time.TickSeconds);

    if (input.Pressed(Key.Q))
    {
        iso.Turn(1);
    }
}
```

`Lag` eases the view towards a center it follows, a zoom or a turn. `Pan` moves the view
straight away, so a camera steered with keys doesn't trail them or drift after they're
let go. Pan or follow in a tick, not both: following sets `Center` again, so the view would jump
by the pan and then ease back.

`Turn` walks an isometric camera around by quarter turns, easing there. A top-down camera
can't rotate. For a top-down view with perspective, use `FollowCamera` with an `Offset`
straight up.

## Orthographic cameras

A camera is perspective unless you say otherwise. An orthographic camera has no
perspective, so things keep their size however far away they are. You give it how many
world units the screen shows from bottom to top:

```csharp
var flat = Camera.Orthographic(new Vector3(0, 20, 20), Vector3.Zero, size: 30f);
```

Orthographic views widen their shadows to cover the screen, so shadows get softer as you
zoom out.

## Shifted cameras

`Shift` makes a camera asymmetric. It slides the picture without turning the camera, in
half screens, and works with either kind. This keeps a character in the middle of what's
left beside a side panel:

```csharp
var beside = camera with { Shift = new Vector2(0.5f, 0f) };
```

Clicking works the same with all of them, because `input.Mouse.Ray` asks the camera.

## Tall screens

The field of view is the angle from top to bottom, so a narrower screen shows less from
side to side. A game made for a widescreen window loses its sides when it's recorded as
a vertical video with `--size 1080x1920`. Set `WideAs` to the shape the game was made for,
and on a narrower screen the view grows taller instead:

```csharp
var camera = new Camera(eye, target) { WideAs = 16f / 9f };
var orbit = new OrbitCamera { WideAs = 16f / 9f };
```

A screen held upright then shows everything a widescreen one shows from side to side,
with more above and below.

![The Forest example at 1080x1920. On the left, the camera as it was made for
widescreen, cut off at the sides. On the right, with WideAs = 16/9 and --ui-scale 2.5.](media/tall-screens.webp)

 On a screen as wide as `WideAs` or wider, nothing changes.
It works with orthographic cameras too, where it grows `Size`, and every camera helper has
the setting. It's zero, and so off, unless you set it. Clicking stays right, because
`input.Mouse.Ray` uses the same view.

## Limits

A camera can't roll, and asymmetric views are a shift, so head tracking and tiled
displays can't be done yet. Underwater views are wrong under an orthographic camera.
