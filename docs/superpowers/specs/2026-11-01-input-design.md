# Input design

Date: 2026-11-01. Status: approved direction, not yet built.

## Intent

Super has no input at all today. The README already promises an injected `Input`
service and `run.Input` for scripted presses in headless tests, and the user's first
games (a colony builder or a dungeon crawler) can't exist without a key press. Input
is also what menus, buttons, picking and the coming scene system all wait on.

The user wants two layers:

1. A **raw layer** that answers "is Space down this tick", in the shape of Bevy's
   `ButtonInput` and Raylib's `IsKeyPressed` family.
2. An **action layer** on top, like Unreal's and Unity's input systems: the game
   declares actions such as Jump and Move, binds several keys, buttons and sticks to
   each, and game code only ever asks about actions. Controllers must work.

Both layers are built in Super on the GLFW the window already uses. No input library
is added: Silk.NET.Input wants its own windowing and is event-driven, and Unity's
system is far bigger than these games need.

## Goals

- Game code asks `actions.Pressed(Control.Jump)` and `actions.Vector(Control.Move)`,
  never about keys.
- Keyboard, mouse and one gamepad work in the window with no per-device code.
- Headless tests script input at the action level, so rebinding doesn't break tests,
  and at the key level for testing bindings themselves.
- A seed replays identically: `Update` sees a frozen per-tick snapshot, and `Draw`
  and `Ui` cannot read input.
- A tap shorter than one tick still registers.
- Bindings are plain data, so a settings screen, a save or a JSON file can change
  them later without new concepts.
- No allocations per tick once running, in keeping with the rest of the engine.

## Non-goals

- Rebinding UI, saving bindings, JSON binding files. The data shape must allow them;
  nothing more.
- Input contexts or mapping layers (Unreal's Input Mapping Contexts). The scene
  system will gate systems by state, which covers "the menu swallows gameplay input".
- Clickable interface elements. `Ui` stays read-only over input in this phase. A later
  UI milestone routes pointer state through engine code, not game code.
- More than one gamepad, gamepad rumble, touch, text composition (IME).
- Picking (mouse to world ray) and camera helpers. They come after this and read
  `MousePosition` from the raw layer.

## Anti-goals

- Static or global input state. It must be an injected service, like `GameRandom`, so
  many headless games can share a process.
- String-named actions. Actions are the game's own enum.
- Input that depends on frame rate. The window may poll at 144 Hz and tick at 30; the
  game must not be able to tell.

## Shape

### Raw layer: `Input`

Lives in `Super`, registered by `GameHost` like `GameTime`. One instance per game.

```csharp
public sealed class Input
{
    public bool Down(Key key);        // held during this tick
    public bool Pressed(Key key);     // went down since the last tick
    public bool Released(Key key);    // went up since the last tick
    public bool Down(MouseButton button);     // + Pressed / Released
    public bool Down(GamepadButton button);   // + Pressed / Released

    public Vector2 MousePosition { get; }  // pixels, same space the interface lays out in
    public Vector2 MouseDelta { get; }     // pixels moved since the last tick
    public float Scroll { get; }           // wheel notches this tick, positive away from the user
    public string Typed { get; }           // characters typed this tick, for text fields later

    public Vector2 Stick(GamepadStick stick);      // -1..1 each axis, dead zone already applied
    public float Trigger(GamepadTrigger trigger);  // 0..1
    public bool GamepadConnected { get; }
}
```

`Key`, `MouseButton`, `GamepadButton`, `GamepadStick` and `GamepadTrigger` are Super's
own enums, so a game never references Silk.NET. `Key` names physical US-layout keys,
matching GLFW's key codes, which is what every game engine does for movement keys.
`GamepadButton` uses the neutral names South, East, West, North, LeftBumper,
RightBumper, LeftStick, RightStick, Start, Back, DPadUp and so on.

### Sampling

The window registers GLFW callbacks for keys, mouse buttons, cursor position, scroll
and characters. Between ticks these are folded into a pending buffer on `Input`:
counts of downs and ups per button, accumulated mouse delta and scroll, last cursor
position, typed characters. Nothing is read from GLFW state at tick time except the
gamepad, which GLFW only exposes by polling (`glfwGetGamepadState`, using the SDL
controller database GLFW ships, so Xbox, PlayStation and Switch pads map to the same
names).

At the start of each tick, before the `ITickHook`s and `Update`, `GameHost` tells
`Input` to roll the pending buffer into the tick snapshot:

- `Pressed` is true if the button went down at least once since the last roll.
- `Released` is true if it went up at least once since the last roll.
- `Down` is true if it is physically down at roll time, or if it went down and back
  up within the window (a tap shorter than a tick). In that case `Down` and `Pressed`
  are true this tick and `Released` is true next tick, so a game that checks
  `Pressed` then `Released` always sees both, in that order.
- Mouse delta and scroll are sums over the window; position is the latest.

The snapshot never changes during a tick. The window polls as often as it likes.

### Reading is only allowed in `Update`

`GameHost` marks `Input` readable just before the tick and unreadable before `Draw`
and `Ui`. Any read while unreadable throws `InvalidOperationException` with a message
saying input is only for `Update`, so the screen stays a function of state. The check
is one bool and runs in release builds too.

### Action layer: `Actions<TControl>`

Also in `Super`. Generic over the game's enum, registered as an open generic so a game
just injects `Actions<Control>` and nothing needs adding to `services`.

```csharp
public enum Control { Move, Look, Jump, Interact, Pause }

public sealed class Actions<TControl> where TControl : struct, Enum
{
    public void Bind(TControl control, params Binding[] bindings);  // adds to existing bindings
    public void Unbind(TControl control);
    public IReadOnlyList<Binding> Bindings(TControl control);

    public bool Down(TControl control);
    public bool Pressed(TControl control);
    public bool Released(TControl control);
    public float Value(TControl control);     // 0..1
    public Vector2 Vector(TControl control);  // length at most 1
}
```

`Binding` is one readonly struct with a kind and a payload. Every source converts to
it implicitly, so binding reads naturally:

```csharp
actions.Bind(Control.Jump, Key.Space, GamepadButton.South);
actions.Bind(Control.Interact, Key.E, GamepadButton.West, MouseButton.Left);
actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
actions.Bind(Control.Look, Mouse.Delta, GamepadStick.Right);
actions.Bind(Control.Zoom, Mouse.Scroll, Binding.Axis(GamepadTrigger.Left, GamepadTrigger.Right));
```

Sources and what they contribute:

| Source | Value | Vector |
| --- | --- | --- |
| `Key`, `MouseButton`, `GamepadButton` | 1 while down | (0, 0) |
| `Binding.Axis(negative, positive)` over keys, buttons or triggers | -1..1 | (value, 0) |
| `Keys.WASD`, `Keys.Arrows`, `Binding.Composite(up, down, left, right)` | length | normalized to at most 1 |
| `GamepadStick.Left` / `.Right` | length | stick after dead zone |
| `GamepadTrigger.Left` / `.Right` | 0..1 | (value, 0) |
| `Mouse.Delta` | length | pixels this tick, not clamped |
| `Mouse.Scroll` | notches | (notches, 0) |

Every binding carries a `Scale` (default 1) applied to its contribution, written as
`Mouse.Delta.Scaled(0.002f)` or `GamepadStick.Right.Scaled(0.05f)`. That is how one
action can mix pixels and a stick: the game picks the scale so both mean the same
thing to it, radians per tick for a look action. Scale is part of the binding data,
so a sensitivity slider later edits the same list as a rebind.

Combining several bindings on one action: clamped sources (keys, buttons, sticks,
triggers, composites) are summed and that sum is clamped to length 1; unclamped
sources (mouse delta and scroll) are summed separately and added on top. `Value` is
the length of the result for axis-like actions, or 1 if any button is down. `Down` is
true when `Value` is above 0.5. `Pressed` and `Released` are edges of `Down` between
ticks, computed when the raw snapshot rolls.

The gamepad's radial dead zone lives on `Input` as `DeadZone`, default 0.2, so every
`Actions<T>` in a game sees the same stick. `Actions` reads from `Input`, so the same
readable-only-in-`Update` rule applies.

### Mouse capture

A game looking around with `Mouse.Delta` needs the cursor hidden and locked, or it
leaves the window. `Input.CaptureMouse` is a bool the game sets in `Update`; the
window applies it with GLFW's disabled-cursor mode on the next poll, and the headless
runner ignores it. It is the only thing a game writes to `Input`, and it is state,
not input, so it does not affect replay.

### Headless scripting: `run.Input`

`HeadlessRun<TGame>` exposes a `ScriptedInput`. Internally it is a tape: a list of
events stamped with the tick they land on, which the roll consumes by writing into
the same pending buffer the window would. The snapshot code is shared and tested
once, and the tape shape leaves room for recording a window session later. Recording
is not built now, and the replay line in `InvariantFailedException` is only exact for
tests that script no input; that stays as is for this phase.

```csharp
run.Input.Press(Key.Space);          // down and up before the next tick: one Pressed, then Released
run.Input.Hold(Key.W);               // down until Release
run.Input.Release(Key.W);
run.Input.MoveMouse(new Vector2(640, 360));
run.Input.Click(MouseButton.Left);   // optionally at a position
run.Input.Scroll(-2f);
run.Input.Type("hello");
run.Input.Stick(GamepadStick.Left, new Vector2(0, 1));
run.Ticks(10);                       // the queued input lands on the first of these ticks
```

Action-level scripting bypasses bindings by writing an override into `Actions<T>`:

```csharp
run.Input.Press(Control.Jump);
run.Input.Hold(Control.Move, new Vector2(1, 0));
run.Input.Release(Control.Move);
```

An override makes the action report exactly that value until released, regardless of
bindings. Calls take effect at the next `run.Ticks(...)`; `Press` lasts one tick. No
time passes inside these calls.

## Example and verification

- **ColorCube** gains input so the window and headless paths are both exercised:
  `Control.Cycle` (Space, gamepad South, left click) changes the color at once, and
  `Control.Spin` (WASD, arrows, left stick) adds to the spin direction. Keep the random
  color timer so the existing behaviour and screenshot stay the same when nothing is
  pressed.
- **Reef** is unchanged except, if cheap, `Control.Pause` on Space to stop the boids.
  Builder's call.
- **Tests** in `Super.Tests`: tap shorter than a tick gives Pressed then Released on
  consecutive ticks; hold gives Down across ticks and Released once; mouse delta and
  scroll sum within a tick and reset; reading in `Draw` throws; two bindings on one
  action combine and clamp; dead zone zeroes a small stick; action overrides beat
  bindings; nothing allocates per tick after warm-up where the project already has a
  way to check that. A ColorCube test under `[Seeds]` presses `Control.Cycle` and
  asserts the color changed that tick.
- **Window check:** run ColorCube with the window and confirm keys, mouse and a pad
  if one is attached. `--screenshot` must be pixel-identical to before with no input.

## Documentation

- README: replace the one-line mention with an "Input" section showing the action
  layer first, then the raw layer, then `run.Input`. Move input out of "What's not
  here yet".
- `docs/design.md`: add rows for input sampling (per-tick snapshot with tap
  preservation, why), the read-only-in-Update rule, and why bindings are an enum plus
  a data list rather than strings or assets.

## Decided by the user

- Two layers, raw and actions; game code talks to actions.
- Actions are declared by the game, several bindings per action, controllers included.
- No input contexts now; the scene system covers that.

## Calls made in this document (user may overturn)

- Build on GLFW directly; no input library. Interface copied from Bevy and Raylib.
- Example enum is called `Control`, not `Action`, because `System.Action` makes the
  obvious name painful in C#.
- The engine is generic over the game's enum and registered as an open generic.
- Tap preservation semantics as described: Down and Pressed this tick, Released next.
- Reads outside `Update` throw, always, not only in debug builds.
- `Down` on an axis action means value above 0.5.
- Mouse position is in the interface's layout pixel space; mouse delta is unclamped.
- One gamepad, the first connected; radial dead zone 0.2 by default, owned by `Input`.
- Action-level scripting is an override that bypasses bindings.
- Bindings carry a `Scale`, so mouse look and stick look can share one action.
- `Input.CaptureMouse` is in scope as the one thing a game writes to `Input`.
- Scripted input is a tick-stamped tape, so recording a window session can come later.

## Left to the builder

- Exact GLFW callback plumbing and where the pending buffer lives so the window and
  the headless runner share it.
- How `Binding` stores its payload without allocating, and the shape of `Keys.WASD`.
- Whether `Actions` recomputes on roll or lazily on first read within a tick.
- Mapping from GLFW key codes to `Key`, and framebuffer versus window coordinates for
  the mouse so it matches the UI's layout space.
