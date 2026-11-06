## Code Style 

- Always use bracers for control statements like if and while.

## Keeping docs current

Update the docs in `docs/` in the same change as the code, as you go, not at the end.
A new option, changed behaviour or new limit goes in the user guide (such as
`rendering.md` or `views.md`), and engine internals go in `design.md`. Before
committing, search `docs/` for the names you touched so no old description is left.

## Seeing the game

To see a game, use `--screenshot` or `--record` from `docs/command-line.md`, or `GameCapture`
from code. They use the renderer's own frames, so they stay clean and repeatable.
Never capture the desktop: that can pick up the user's other windows.

## Commits

Use conventional commits (`feat:`, `fix:`, `docs:`, `chore:`). Don't list Claude
as an author and don't add Co-Authored-By lines.

## Pull requests

Keep the description short: what changed and why, how it was tested, and what it
still can't do. Don't add a "Generated with Claude Code" line.

**When a change shows on screen** (rendering, lighting, effects, UI, an example scene),
show it in the PR:

- Take shots with `--screenshot` and `--validate` from the example that shows the
  change best, and say in the PR how many validation messages there were. Use the
  same `--seed`, `--ticks` and `--size` for every shot so they compare.
- When the default camera doesn't show the change, aim one closer for the shot only,
  then put it back.
- Prefer before and after side by side with a label on each, made with ffmpeg
  (`hstack`, `drawtext` with `fontfile='C\:/Windows/Fonts/segoeui.ttf'`).
  Crop to the part that changed.
- Save them as `.webp` in `docs/media/` (`-c:v libwebp`), about 1200 pixels wide, and use
  them in the docs too.
- Link them in the PR by commit, so they still show after the branch is deleted:
  `https://github.com/Artmann/super/blob/<sha>/docs/media/<file>.webp?raw=true`.
- Look at every image before posting it, and check that its label is true.

**When nothing on screen changes** (engine internals, tools, tests, docs), skip the
pictures.

## Writing tests

Lessons from a full review of the test suite. Follow them when you add tests.

- **Prove every test can fail.** Break the production code on purpose, watch the
  test fail, then revert. A test that passes either way is worse than none.
- **Assert real behaviour, not test doubles.** If the check only sees what a fake
  returns (for example `MemoryContent` order), it proves nothing about the real
  code. Use the real thing, such as a temp folder.
- **Never assert "nothing happened" after a fixed sleep.** Use a fake clock
  (`TimeProvider`) for time-based logic, or poll until the thing you expect
  happens. Give time-based code a `TimeProvider` so it can be tested.
- **Don't assert an order that nothing promises.** Streaming and concurrent work
  have no order. Compare sorted results.
- **Ordering checks with `IndexOf` pass when both items are missing.** Assert that
  both were found first.
- **Match seed counts to what the seed changes.** About 10-20 seeds is enough for
  a property. Don't use `Seeds(1000)`. If every seed gives the same result, use
  `[Fact]`.
- **Test the behaviour, not the constants copied from the code.** Use a hand-worked
  expected value, not the production formula.
- **A `$""` string binds to a `string` overload before `FormattableString`.** Don't
  make both overloads, or invariant formatting silently never runs. Test text
  formatting under a non-English culture such as `sv-SE`.
- **Anything using real time or process-wide state** goes in a
  `DisableParallelization` collection with a `[Trait("Speed","Slow")]`.
- **Check exception messages**, not only the type, and use `Assert.Throws<T>` for
  the exact type.
- **Don't test the language or the DI library.** Delete tests that can't fail, such
  as a compile-time fact or a bool set in the test itself.
- **Names must say what is checked.** A test called "Reads every option" must read
  every option; a test called "Explains" must check the message.
- **Rebuild instead of `--no-build`** before trusting a result, because binaries go
  stale.
