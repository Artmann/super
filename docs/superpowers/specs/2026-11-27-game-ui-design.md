# A game feel for the interface

Date: 2026-11-27

`Super.Ui` has the SwiftUI element set, a neutral theme and every kind of input (see
[the elements spec](2026-11-26-ui-elements-design.md)). It looks and works well, but it feels
like an app: it's silent, focus is a thin ring, prompts are fixed text, panels are flat
boxes, and the pieces games have that apps don't are missing. This makes it feel like a
game without giving up anything it does now.

## Decisions

- Everything is on by default and has a switch. Sounds, the focus highlight and the
  extra motion are part of the stock theme, and a game turns each off with a token.
- No assets are needed. The built-in sounds are made in code, and button glyphs are drawn
  with the existing shapes, so a game gets all of it before it has any art.
- Art is used when a game has it. Panels, buttons and frames can be drawn from a 9-slice
  image instead of a box, so a game's own art replaces the neutral look.
- Tests read meaning, as before. A test reads which interface sounds played, which
  glyph a prompt shows and where a world marker sits, without pixels.

## Steps

Each step is one PR, stacked, with stories in the Storybook and tests.

1. Sound. `Theme.Sounds` names a sound for each cue: focus moving, a press, going
   back or closing, a popup opening, a press on something disabled, a step (a stepper, a
   slider key, a picker), and a finished hold. The default set is made in code: short soft
   ticks and blips. `UiSounds.None` turns them off, and a game names its own sound files.
   The tree gathers what to play during an update and the frame plays it on the
   `Interface` channel. `run.Sounds` lists them in tests.
2. Game focus. A theme's focus can be a ring, as now, or a highlight: the focused
   control lifts with a filled glow, and in a menu a marker slides from item to item
   instead of jumping. `ButtonStyle.Menu` is a full-width row for menus. `.FocusWraps()`
   lets the d-pad wrap from the last item to the first.
3. Prompts that follow the device. `Input` knows the last device used (keyboard and
   mouse, gamepad, touch) and the gamepad's family (Xbox, PlayStation, Nintendo, other)
   from its name. `ButtonPrompt` draws a gamepad button or a key in the right style, and
   `ActionPrompt` shows whatever an action is bound to on the device in use. They change the
   moment the player picks up another device.
4. Game controls. A picker with arrows, `◀ High ▶`, moved with left and right;
   `HoldButton`, which fills a ring while held and acts once it's full; `KeyBinder`, a row
   that waits for a key or button, warns about a clash and resets; and `RadialMenu`, a wheel
   picked with the stick or the mouse.
5. Motion. Buttons squash a little when pressed. Springs that overshoot
   (`Easing.Spring`). `CountingText` for numbers that count to a new value, and a bar whose
   loss trails behind it. Exit transitions, so views can leave as well as arrive, and pages
   in a navigation stack slide.
6. Art panels. `Image` takes slice insets and draws as a 9-slice. A theme can draw
   cards, buttons and popups from images. The Storybook gets a textured theme, with its
   images made in code, to show it.
7. The world on screen. `.AtWorld(point)` places a view over a point in the world,
   for health bars over heads and name plates; it can hide behind the camera or cling to
   the edge with an arrow when off screen. Floating numbers that rise and fade, a compass
   bar, and a round minimap frame.
8. Story and screens. `DialogueBox` with a speaker, a portrait, text that types
   itself out, choices and a skip; `Banner` for big centred title cards and achievements;
   `Subtitles`; and a backdrop that blurs the game behind a pause menu.
9. Showcases. The settings, main menu and heads-up display showcases rebuilt with all
   of this, a title screen ("Press any button"), a loading screen with tips, and a
   before and after.

## Not in this

Controller vibration, localisation, and text with mixed styles inside a line.
