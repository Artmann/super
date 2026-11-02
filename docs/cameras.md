# Cameras and clicking

`view.Camera(position, lookAt)` is all a fixed camera needs. For anything else, keep a
`Camera` in your game. A `Camera` is a plain value: a position, a target, a field of
view and optionally a `Size` and a `Shift`.

## Clicking on things

Input can only be read in `Update`, so that's where a click turns into a place in the
world. Keep the camera in your game so both `Update` and `Draw` can use it:

```csharp
readonly Camera camera = new(new Vector3(0, 12, 12), Vector3.Zero);

public void Update(GameTime time)
{
    if (input.Pressed(MouseButton.Left) && !input.MouseOverUi)
    {
        var ray = input.MouseRay(camera);

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

public void Draw(View view) => view.Camera(camera);
```

| Call | Gives |
| --- | --- |
| `ray.Ground(height)` | The point where the ray meets a flat plane at that height, or `null` |
| `ray.HitsBall(center, radius)` | The distance along the ray, or `null` on a miss |
| `ray.HitsBox(min, max)` | The distance along the ray to an axis-aligned box, or `null` |
| `camera.Ray(pixel, screen)` | A ray through any pixel, without `Input` |

Super doesn't go through your things for you, since only your game knows what can be
clicked, and it tests these simple shapes, not a model's triangles. `input.MouseOverUi`
is true while the cursor is over the [interface](interfaces.md#the-mouse), so a game can
leave those clicks alone.

## Camera helpers

Six helpers cover the usual kinds of game. They all work the same way: keep one in your
game, step it once a tick in `Update`, and draw `At(view.Blend)` so it moves smoothly at
any frame rate. `Current` is the camera as of the last tick, which is the one to pass to
`input.MouseRay`.

| Helper | For |
| --- | --- |
| `OrbitCamera` | Circling a target, as in a strategy or model viewer |
| `FollowCamera` | Trailing behind a target with some lag |
| `BoomCamera` | Third person, on an arm that shortens when a wall is in the way |
| `FirstPersonCamera` | Looking out from the eyes |
| `TopDownCamera` | Looking straight down, orthographic |
| `IsometricCamera` | The true isometric angle, orthographic, turning by quarter turns |

The first update puts a camera straight in place. If its target teleports, call
`Jump()` so the camera doesn't slide across the map.

### Orbit and follow

```csharp
readonly OrbitCamera orbit = new() { Distance = 12f };
readonly FollowCamera follow = new() { Offset = new Vector3(0, 8, 10), Lag = 0.2f };

public void Update(GameTime time)
{
    // Yaw, Pitch, Distance and its limits can also be set directly.
    orbit.Turn(input.MouseDelta * 0.005f);
    orbit.Zoom(input.Scroll);
    orbit.Update(tower.Position);

    // Lag is about how many seconds behind the target it stays.
    follow.Update(player.Position, time.TickSeconds);
}

public void Draw(View view) => view.Camera(follow.At(view.Blend));
```

### Third person

`BoomCamera` sits on an arm behind the character. Super doesn't know your walls, so you
test the arm against them and pass in how far along it you hit something:

```csharp
readonly BoomCamera boom = new() { Length = 6f, Shoulder = 0.5f };

public void Update(GameTime time)
{
    boom.Turn(input.MouseDelta * 0.005f);
    boom.Zoom(input.Scroll);
    var walk = boom.Walk(actions.Vector(Control.Move));

    var arm = boom.Arm(player.Position);
    boom.Update(player.Position, time.TickSeconds, blockedAt: arm.HitsBox(wall.Min, wall.Max));
}
```

The arm pulls in at once, so the camera never shows the inside of a wall, and eases back
out once the way is clear. With several walls, pass the nearest hit. `Walk` turns a stick
into a direction on the ground, so forward goes where the camera faces.

### First person

```csharp
readonly FirstPersonCamera eyes = new();

public void Load() => input.CaptureMouse = true;

public void Update(GameTime time)
{
    eyes.Turn(input.MouseDelta * 0.003f);
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
    iso.Zoom(input.Scroll);

    // Pan by a step, or follow something with iso.Update(player.Position, seconds).
    iso.Pan(actions.Vector(Control.Move) * 20f * time.TickSeconds);
    iso.Update(time.TickSeconds);

    if (input.Pressed(Key.Q))
    {
        iso.Turn(1);
    }
}
```

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

Clicking works the same with all of them, because `input.MouseRay` asks the camera.

## Limits

A camera can't roll, and asymmetric views are a shift, so head tracking and tiled
displays can't be done yet. Underwater views are wrong under an orthographic camera.
