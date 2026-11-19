# Input

Name what the player can do with an enum, then say how each thing is done. Game code
asks about the action, never the key, so changing a binding touches nothing else.

```csharp
public enum Control { Move, Look, Jump, Interact }

public class Dungeon(Actions<Control> actions) : IGame
{
    public void Load()
    {
        actions.Bind(Control.Jump, Key.Space, GamepadButton.South);
        actions.Bind(Control.Interact, Key.E, GamepadButton.West, MouseButton.Left);
        actions.Bind(Control.Move, Keys.WASD, Keys.Arrows, GamepadStick.Left);
        actions.Bind(Control.Look, Binding.MouseDelta.Scaled(0.01f), GamepadStick.Right);
    }

    public void Update(GameTime time)
    {
        if (actions.Pressed(Control.Jump))
        {
            Jump();
        }

        if (actions.Down(Control.Interact))
        {
            KeepOpening(time);
        }

        Walk(actions.Vector(Control.Move) * Speed * time.TickSeconds);
    }
}
```

Ask for `Actions<Control>` in your constructor and it's there; nothing needs
registering.

| Call | Gives |
| --- | --- |
| `actions.Pressed(action)` | True on the tick an action starts |
| `actions.Down(action)` | True while it goes on |
| `actions.Released(action)` | True on the tick it stops |
| `actions.Vector(action)` | A direction with length at most 1, so a diagonal on the keys moves no faster than a stick |
| `actions.Value(action)` | How much, from 0 to 1 |

## Bindings

| Binding | Gives |
| --- | --- |
| `Key.W`, `MouseButton.Left`, `GamepadButton.South` | A button: 0 or 1 |
| `GamepadTrigger.Right` | A pull from 0 to 1 |
| `GamepadStick.Left` | A direction, with a radial dead zone of `input.Gamepad.DeadZone` |
| `Binding.Axis(Key.Q, Key.E)` | -1, 0 or 1 from two buttons or triggers |
| `Binding.Composite(up, down, left, right)`, `Keys.WASD`, `Keys.Arrows` | A direction from four buttons |
| `Binding.MouseDelta`, `Binding.MouseScroll` | Pixels moved or notches turned this tick, unclamped |

`Bind` adds to what an action already has. `actions.Unbind(action)` clears it and
`actions.Bindings(action)` lists it, which is what a settings screen needs to rebind.

A mouse moves in pixels and a stick in a range from -1 to 1, so give the mouse a
`Scaled` factor when both drive the same action. The mouse delta and the scroll wheel are
never clamped; everything else sums to length 1 at most.

## Raw input

Underneath is the `Input` service, for the odd case that really is about a key:

```csharp
class Console(Input input)
{
    public void Update()
    {
        if (input.Pressed(Key.F1))
        {
            Toggle();
        }

        Append(input.Typed);
    }
}
```

| Member | What it is |
| --- | --- |
| `Down`, `Pressed`, `Released` | For a `Key`, `MouseButton` or `GamepadButton` |
| `ScreenSize` | The screen's size in the interface's pixels, for placing interface over the world |
| `Typed` | Text the player entered this tick |
| `Mouse` | The cursor and the wheel, below |
| `Gamepad` | The first gamepad's sticks and triggers, below |

The mouse is on `input.Mouse`:

| Member | What it is |
| --- | --- |
| `Position` | In the same pixels the interface lays out in |
| `Delta`, `Scroll` | Movement and wheel notches this tick |
| `Ray(camera)` | A ray into the world, for [clicking](cameras.md#clicking-on-things) |
| `OverUi` | True while the cursor is over the [interface](interfaces.md#the-mouse) |
| `Locked` | Set to true to hide the cursor and lock it to the window, as a first-person camera needs. While it is on, `Delta` comes from the mouse itself, without the system's pointer acceleration, where the system allows it |
| `HideCursor` | Set to true to hide the system cursor over the window but leave it free, when the game draws its own pointer in the interface |
| `Cursor` | A `CursorLook` the system draws in place of its arrow, or null for the arrow |

The gamepad is on `input.Gamepad`:

| Member | What it is |
| --- | --- |
| `Stick(GamepadStick)`, `Trigger(GamepadTrigger)` | Sticks give forward as positive y; triggers go from 0 to 1 |
| `Connected` | Whether there's a gamepad |
| `DeadZone` | The stick dead zone, 0.2 by default |

The engine keeps one key, F3 unless the game picks another, to show and hide the
[stats panel](interfaces.md#the-stats-key), so `Input` never reports it. Set
`GameStats.ToggleKey` to another key, or to null to give the game every key.

## When input is read

Input is a snapshot taken once per tick, so every system in `Update` sees the same thing.
A key tapped and let go between two ticks still counts: it's `Down` and `Pressed` on the
next tick and `Released` on the one after, so a quick tap never vanishes.

Only `Update` may read input. `Draw` and `Ui` describe state and `Load` happens before
play, so reading there throws. That keeps the picture a function of state, which is what
lets a seed replay and a screenshot test stay stable.

Tests play input through `run.Input`; see [Testing](testing.md#playing-input).
