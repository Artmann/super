# Tower Defense Guide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A friendly how-to guide, `docs/guides/tower-defense.md`, that builds a small tower defense called Outpost chapter by chapter, with the finished game in `examples/Outpost` and its tests in `tests/Outpost.Tests`.

**Architecture:** Each task is one chapter of the guide. The task grows `examples/Outpost` to exactly what the reader has at the end of that chapter, adds the tests for it, and writes the chapter. So the example at each commit is the reader's game at that chapter. Game state is plain C# classes registered as singletons. Only built-in shapes are used, so there are no assets.

**Tech Stack:** C# / .NET 10, Super (`IGame`, `View`, `Camera`, `Spline`, `Actions<T>`, `States<T>`, `Super.Ui`, `GameRandom`), xUnit with `Super.Testing`'s `HeadlessGame`.

**Spec:** `docs/superpowers/specs/2026-11-19-tower-defense-guide-design.md`

## Global Constraints

- Audience: C# developers trying out Super. Skip C# basics, explain Super.
- Tone: simple words, short sentences, friendly, "we" and "you".
- No run-in headings: no paragraph or list item that opens with a bolded label. Use real headings.
- Only built-in shapes (`ModelRef.Cube`, `Cylinder`, `Cone`, `Capsule`, `Sphere`, `Plane`). No models, textures, sounds or shaders.
- Only APIs in the published Super package (v1.6.0). No engine changes in this work; note anything awkward for later.
- Each chapter only adds code, or changes a line or two and says so in the text.
- The guide's code must match `examples/Outpost` at that chapter's commit, character for character, except `Program.cs` namespace lines the guide explains once.
- Always use braces for `if`, `while` and so on, in the example and in the guide.
- Conventional commits. No Claude author and no Co-Authored-By lines.
- Work sequentially in the main checkout, on branch `docs/tower-defense-guide`. No worktrees.
- Roe must pass: `dotnet tool restore` once, then `dotnet roe`.

## Review Focus

1. Clicking a road tile, a tile with a tower, off the grid, or with too little gold builds nothing and spends nothing. Tested in Task 4.
2. A click on the HUD (`input.Mouse.OverUi`) doesn't build under it. Tested in Task 6.
3. Two shots in flight at one enemy: the second must not pay gold twice or touch a removed enemy. Tested in Task 5.
4. Restarting mid-wave leaves no enemies, shots or towers from the last game, and gold and lives go back to the start. Tested in Task 7.
5. Winning needs every wave spawned and every enemy gone, not just the last wave started. Tested in Task 7.

---

## File map (finished game)

| File | Holds | Chapter |
| --- | --- | --- |
| `examples/Outpost/Outpost.csproj` | Exe project referencing `..\..\src\Super\Super.csproj` | 1 |
| `examples/Outpost/Program.cs` | `Game.StartWindowsGameLoop<OutpostGame>(...)` | 1 |
| `examples/Outpost/OutpostServices.cs` | `AddOutpost(this IServiceCollection)`, one line per class | 1, grows |
| `examples/Outpost/OutpostGame.cs` | The `IGame`: camera, light, calling each system, drawing | 1, grows |
| `examples/Outpost/Field.cs` | The grid, tiles, the road and its spline | 1, 2 |
| `examples/Outpost/Player.cs` | Gold and lives | 3, 4 |
| `examples/Outpost/Enemies.cs` | `Enemy`, `EnemyKind`, `Enemies` | 3 |
| `examples/Outpost/Towers.cs` | `Tower`, `Towers` | 4, 5 |
| `examples/Outpost/Shots.cs` | `Shot`, `Shots` | 5 |
| `examples/Outpost/Waves.cs` | The wave list and spawning | 6 |
| `examples/Outpost/Hud.cs` | `Super.Ui` records for the HUD and the screens | 6, 7 |
| `examples/Outpost/Controls.cs` | `enum Control`, later `enum Screen` | 4, 7 |
| `examples/Outpost/README.md` | What it is, how to run it, link to the guide | 9 |
| `tests/Outpost.Tests/Outpost.Tests.csproj` | Like `tests/Rooms.Tests` but no content import | 1 |
| `tests/Outpost.Tests/OutpostTests.cs` | The tests | 1, grows |
| `docs/guides/README.md` | Index of guides | 1 |
| `docs/guides/tower-defense.md` | The guide | 1, grows |

Namespace: `OutpostExample`. Both new projects go in `Super.slnx` next to `Rooms`.

## Fixed numbers

The grid is 15 tiles by 9, each tile 2 m. The world centre of tile `(x, z)` is `((x - 7) * 2, 0, (z - 4) * 2)`, so tile (7, 4) is at the origin, where the camera looks. The road runs through the tile corners `(0,1) → (4,1) → (4,6) → (10,6) → (10,2) → (14,2)`, and every tile on those straight runs is road. The base is at (14, 2). Tile (7, 4) is grass, and so is (3, 2), next to the road.

| Thing | Value |
| --- | --- |
| Start gold, lives | 100, 10 |
| Tower price, range, reload, damage | 50, 5 m, 0.75 s, 1 |
| Shot speed | 14 m/s |
| Walker: health, speed, reward, look | 3, 2 m/s, 10, capsule, red |
| Runner: health, speed, reward, look | 2, 4 m/s, 6, small capsule, yellow |
| Brute: health, speed, reward, look | 10, 1.2 m/s, 25, big capsule, purple |
| Waves | 10. Wave n (1-based) has 4 + n walkers, n - 2 runners from wave 3, (n - 3) / 2 brutes from wave 5 |
| Gaps | 3 s before wave 1, 0.8 s between enemies, 5 s between waves |
| Ticks per second | 30, the default |

Draw keys: base 1, ground tiles `10_000 + z * 15 + x`, tower body `20_000 + tile index`, tower top `30_000 + tile index`, enemies `100_000 + enemy.Id`, shots `200_000 + shot.Id`.

Camera: `new Camera(new Vector3(0, 22, 16), Vector3.Zero)` with `WideAs = 16f / 9f`, kept as a field so `Update` can ray-cast through it. Screenshots use `--seed 1 --size 1280x720 --validate`.

---

### Task 1: Chapter 1, a field on screen

**Files:**
- Create: `examples/Outpost/Outpost.csproj`, `Program.cs`, `OutpostServices.cs`, `OutpostGame.cs`, `Field.cs`
- Create: `tests/Outpost.Tests/Outpost.Tests.csproj`, `tests/Outpost.Tests/OutpostTests.cs`
- Create: `docs/guides/README.md`, `docs/guides/tower-defense.md`
- Modify: `Super.slnx` (two projects), `docs/README.md` (a "Guides" line linking `guides/README.md`), `docs/getting-started.md` (one closing sentence pointing to the guide)

**Interfaces:**
- Produces: `public sealed class Field` with `const int Width = 15, Depth = 9; const float TileSize = 2f; Vector3 Center(int x, int z); (int X, int Z)? TileAt(Vector3 point)`. `public static IServiceCollection AddOutpost(this IServiceCollection services)`. `public sealed class OutpostGame(Field field) : IGame` with `public Camera Camera { get; }`.

- [ ] **Step 1:** `git switch -c docs/tower-defense-guide`.
- [ ] **Step 2: Write the failing tests** in `OutpostTests.cs`:
  - `The_middle_tile_sits_at_the_origin`: `Assert.Equal(Vector3.Zero, field.Center(7, 4))`, `Assert.Equal(new Vector3(-14, 0, -8), field.Center(0, 0))`.
  - `A_point_finds_its_tile`: `field.TileAt(new Vector3(0.9f, 0, -0.9f))` is `(7, 4)`; `TileAt(new Vector3(-15.1f, 0, 0))` is null (off the grid).
  - `Every_tile_is_drawn`: a `HeadlessGame.Create<OutpostGame>(1, s => s.AddOutpost())` run, one tick, then `Assert.Equal(15 * 9, run.Drawn.Count(d => d.Key is >= 10_000 and < 20_000))`.
- [ ] **Step 3:** Run `dotnet test tests/Outpost.Tests -c Release`. Expected: build fails, types missing.
- [ ] **Step 4:** Write the project files and `Field`. `OutpostGame.Draw` sets the camera, one sun (`direction: new Vector3(1, 2, 1)`), shadows, and draws each tile as a `ModelRef.Cube` scaled to 1.96 × 0.2 × 1.96 m, grass green (`new Vector3(0.35f, 0.6f, 0.3f)`).
- [ ] **Step 5:** Run the tests. Expected: PASS. Then break `Center` (drop the `- 7`), watch two tests fail, and put it back.
- [ ] **Step 6:** `dotnet run -c Release --project examples/Outpost -- --screenshot out/ch1.png --seed 1 --ticks 10 --size 1280x720 --validate`. Look at it: a green grid, filling most of the frame. Report the validation message count.
- [ ] **Step 7: Write the guide's start and chapter 1.** An intro (what we're building, with a link to the final screenshot once it exists in Task 9, what you need, about how long it takes), then "1. A field to stand on": `dotnet new console`, `dotnet add package Super`, `Program.cs`, `OutpostServices.cs`, `Field.cs`, `OutpostGame.cs`, running it. Explain `IGame`, `Update` against `Draw`, keys, and why the camera is a field. Say once that the example in the repository references the engine's project instead of the package. Add `docs/guides/README.md` with a one-row table.
- [ ] **Step 8: Commit** `docs: start a tower defense guide with a field on screen` (include the spec and this plan).

### Task 2: Chapter 2, the road

**Files:** Modify `Field.cs`, `OutpostGame.cs`, `OutpostTests.cs`, `tower-defense.md`.

**Interfaces:**
- Produces: `Field.Road` (`Spline` through the corner tiles' centres, at ground height), `bool Field.IsRoad(int x, int z)`, `Vector3 Field.BaseCenter`.

- [ ] **Step 1: Failing tests:** `The_road_runs_from_the_left_edge_to_the_base`: `IsRoad(0,1)`, `IsRoad(4,3)`, `IsRoad(7,6)`, `IsRoad(10,4)`, `IsRoad(14,2)` all true; `IsRoad(7,4)` and `IsRoad(3,2)` false; `Assert.Equal(field.Center(14, 2), field.Road.PointAtDistance(field.Road.Length))` within 0.01 per axis. `The_road_is_46_metres_long`: `Assert.InRange(field.Road.Length, 44f, 48f)` (hand-worked: 8 + 10 + 12 + 8 + 8, a little less where the curve cuts corners).
- [ ] **Step 2:** Run, expect FAIL.
- [ ] **Step 3:** Build the road set of tiles from the corners in the constructor; `Road = Spline.Through(corners as Center(...))`. Draw road tiles sand coloured (`new Vector3(0.75f, 0.65f, 0.45f)`) and the base as a `ModelRef.Cube` 1.6 m box, stone grey, with key 1.
- [ ] **Step 4:** Run, expect PASS. Break one corner, see it fail, revert.
- [ ] **Step 5:** Screenshot as in Task 1 (`out/ch2.png`) and look: a sand road winding to a grey base.
- [ ] **Step 6:** Write "2. A road to walk". Explain why the road is a spline (enemies walk it by distance) while tiles are a plain grid (for building). Link `splines.md`.
- [ ] **Step 7: Commit** `docs: add the road to the tower defense guide`.

### Task 3: Chapter 3, enemies

**Files:** Create `Player.cs`, `Enemies.cs`. Modify `OutpostServices.cs`, `OutpostGame.cs`, tests, guide.

**Interfaces:**
- Produces: `public enum EnemyKind { Walker, Runner, Brute }`. `public sealed class Enemy { int Id; EnemyKind Kind; int Health; float Distance; float Offset; Vector3 Position; Vector3 PreviousPosition; }`. `public sealed class Player { int Gold = 100; int Lives = 10; void Reset(); }`. `public sealed class Enemies(Field field, Player player, GameRandom random)` with `IReadOnlyList<Enemy> All`, `int Killed`, `Enemy Spawn(EnemyKind kind)`, `void Update(GameTime time)`, `void Hit(Enemy enemy, int damage)` (removes at 0 health, pays the reward, adds to `Killed`), `void Clear()`, and static `EnemyStats Stats(EnemyKind kind)` returning health, speed, reward, size and colour from the table above.
- `Offset` is a random sideways step of up to ±0.3 m from a `random.Stream("enemies")`, so a line of enemies doesn't look like a train. That's the guide's `GameRandom` moment.

- [ ] **Step 1: Failing tests:** `A_walker_reaching_the_base_costs_a_life`: spawn a walker, tick 30 × 25 (46 m at 2 m/s is 23 s), `Assert.Equal(9, player.Lives)` and `Assert.Empty(enemies.All)`. `A_runner_gets_there_first`: spawn walker then runner, tick 30 × 13, assert the runner is gone and the walker is not. `Hitting_to_zero_pays_the_reward`: spawn walker, `Hit` it 3 times, `Gold == 110`, `Killed == 1`.
- [ ] **Step 2:** Run, FAIL.
- [ ] **Step 3:** Implement. Position is the road pose at `Distance` plus `pose.Right * Offset`. For chapter 3 only, `OutpostGame.Load` spawns three walkers so there is something to see; Task 6 removes those lines and says so.
- [ ] **Step 4:** Draw each enemy as a `ModelRef.Capsule` with `view.Smooth` between previous and current translation, scaled by kind, coloured by kind.
- [ ] **Step 5:** Run tests, PASS. Break the life loss, see FAIL, revert.
- [ ] **Step 6:** Screenshot with `--ticks 120` and look: red capsules on the road.
- [ ] **Step 7:** Write "3. Here they come". Explain constructor injection and `AddOutpost`, moving by `DeltaSeconds`, smooth drawing, and named random streams. Link `views.md#smooth-motion`, `random.md`.
- [ ] **Step 8: Commit** `docs: add enemies to the tower defense guide`.

### Task 4: Chapter 4, building towers

**Files:** Create `Controls.cs`, `Towers.cs`. Modify `OutpostServices.cs`, `OutpostGame.cs`, tests, guide.

**Interfaces:**
- Produces: `public enum Control { Build }` bound to `MouseButton.Left` in `Load`. `public sealed class Tower { int X; int Z; float Reload; }`. `public sealed class Towers(Field field, Player player)` with `const int Price = 50`, `IReadOnlyList<Tower> All`, `bool TryBuild(int x, int z)`, `void Clear()`. `OutpostGame` gains `Actions<Control>`, `Input` and `Towers` in its constructor (the one changed line the chapter calls out).
- In `Update`: when `actions.Pressed(Control.Build)` and not `input.Mouse.OverUi`, `input.Mouse.Ray(Camera).Ground()` → `field.TileAt` → `towers.TryBuild`.

- [ ] **Step 1: Failing tests:**
  - `Clicking_grass_builds_a_tower`: run one tick, `run.Input.Click(MouseButton.Left, at: run.ScreenSize / 2)`, one tick: one tower at (7, 4), `Gold == 50`.
  - `Building_needs_grass_and_gold` (Review Focus 1): `TryBuild(4, 3)` (road) false, `TryBuild(7, 4)` true, `TryBuild(7, 4)` again false, `TryBuild(-1, 0)` false, `TryBuild(3, 2)` true, `TryBuild(8, 4)` false with 0 gold left; gold is 0 and two towers stand.
- [ ] **Step 2:** Run, FAIL.
- [ ] **Step 3:** Implement, and draw each tower as a `Cylinder` (0.8 × 1.2 × 0.8, warm white) with a `Cone` on top (blue).
- [ ] **Step 4:** Run, PASS. Break the gold check, see FAIL, revert.
- [ ] **Step 5:** Add `Scripts` to `OutpostGame` with `["towers"]`: clicks the screen centre. Screenshot with `--script towers --ticks 60`, look for a tower in the middle.
- [ ] **Step 6:** Write "4. Building towers". Explain actions against keys, why input is read only in `Update`, and turning a click into a tile with a ray. Link `input.md`, `cameras.md#clicking-on-things`.
- [ ] **Step 7: Commit** `docs: add building towers to the tower defense guide`.

### Task 5: Chapter 5, shooting

**Files:** Create `Shots.cs`. Modify `Towers.cs`, `OutpostServices.cs`, `OutpostGame.cs`, tests, guide.

**Interfaces:**
- Produces: `public sealed class Shot { int Id; Enemy Target; Vector3 Position; Vector3 PreviousPosition; }`. `public sealed class Shots(Enemies enemies)` with `IReadOnlyList<Shot> All`, `void Fire(Vector3 from, Enemy target)`, `void Update(GameTime time)`, `void Clear()`. `Towers` gains `void Update(GameTime time)` and takes `Enemies` and `Shots`: each tower counts its `Reload` down and fires at the enemy in range with the greatest `Distance` (nearest the base).
- A shot whose target is no longer in `enemies.All` vanishes without hitting. Hits land within 0.3 m.

- [ ] **Step 1: Failing tests:**
  - `A_tower_by_the_road_kills_enemies`: build at (3, 2), spawn 3 walkers 1 s apart, tick 30 × 30; `Killed == 3`, `Lives == 10`.
  - `A_second_shot_at_a_dead_enemy_pays_nothing` (Review Focus 3): spawn a walker with 1 health left (hit it twice), fire two shots at it from one metre, tick 30; `Gold == 100 + 10`, `Killed == 1`, `Shots.All` empty.
  - `A_tower_ignores_enemies_out_of_range`: tower at (7, 4); the walker's start (0, 1) is out of range; one tick after spawning, `Shots.All` is empty.
- [ ] **Step 2:** Run, FAIL.
- [ ] **Step 3:** Implement; draw shots as small `Sphere`s (0.25 m, bright yellow), smoothed.
- [ ] **Step 4:** Run, PASS. Break the "target still alive" check, see the second test fail, revert.
- [ ] **Step 5:** Extend the `towers` script to also click a tile beside the road; screenshot `--ticks 150` and look for shots in the air.
- [ ] **Step 6:** Write "5. Shooting back". Explain the order of systems in `Update` (enemies, towers, shots) as plain calls you control, and why a shot checks its target is still there.
- [ ] **Step 7: Commit** `docs: add shooting to the tower defense guide`.

### Task 6: Chapter 6, waves and the HUD

**Files:** Create `Waves.cs`, `Hud.cs`. Modify `OutpostServices.cs`, `OutpostGame.cs` (remove chapter 3's three test walkers), tests, guide.

**Interfaces:**
- Produces: `public sealed class Waves(Enemies enemies)` with `const int Count = 10`, `int Number` (1-based, the wave now or last), `bool AllSpawned`, `void Update(GameTime time)`, `void Reset()`, and `static IReadOnlyList<EnemyKind> Makeup(int wave)` per the table (walkers, then runners, then brutes). `record Hud(int Gold, int Lives, int Wave, int Waves) : UiView` top-left: "Gold 100", "Lives 10", "Wave 1/10", and a bottom hint "Click grass to build a tower (50 gold)". `OutpostGame.Ui` returns it.

- [ ] **Step 1: Failing tests:**
  - `Wave_makeup_follows_the_table`: `Makeup(1)` is 5 walkers; `Makeup(5)` is 9 walkers, 3 runners, 1 brute; `Makeup(10)` is 14 walkers, 8 runners, 3 brutes.
  - `A_wave_with_no_towers_costs_lives`: tick 30 × 40; `Lives < 10`.
  - `The_hud_shows_gold_lives_and_wave`: one tick; `run.ShownText` has "Gold 100", "Lives 10", "Wave 1/10".
  - `A_click_on_the_hud_builds_nothing` (Review Focus 2): click the middle of the frame of the HUD panel (`run.FrameOf("hud")`, so give it `.Id("hud")`); no towers, gold 100.
- [ ] **Step 2:** Run, FAIL.
- [ ] **Step 3:** Implement. Show the HUD only text and a translucent panel; link `interfaces.md`.
- [ ] **Step 4:** Run, PASS. Break `Makeup`'s runner start, see FAIL, revert.
- [ ] **Step 5:** Screenshot `--script towers --ticks 300`, look at the HUD and a wave on the road.
- [ ] **Step 6:** Write "6. Waves and a HUD". Explain `Ui` as a description redrawn every frame, records as components, and `OverUi`. Say plainly which three lines from chapter 3 go away.
- [ ] **Step 7: Commit** `docs: add waves and a HUD to the tower defense guide`.

### Task 7: Chapter 7, title, win and lose

**Files:** Modify `Controls.cs` (add `public enum Screen { Title, Playing, Won, Lost }` and `Control.Start`, `Control.Restart`), `OutpostGame.cs`, `Hud.cs`, tests, guide.

**Interfaces:**
- `OutpostGame` gains `States<Screen>`. `Load` binds `Start` to `Key.Space` and `MouseButton.Left`, `Restart` to `Key.R`, and `screen.OnEnter(Screen.Playing, NewGame)`, where `NewGame` resets `Player`, `Waves` and clears `Enemies`, `Towers`, `Shots`. Systems run only while `screen.Is(Screen.Playing)`. Lost when `Lives <= 0`. Won when `waves.AllSpawned && enemies.All.Count == 0`. R on Won or Lost sets Playing.
- The click that starts the game must not also build: a tower is built only on a `Build` press in a tick that began in Playing (state changes land at the end of the tick, so this holds by itself; the test pins it).
- Screens: Title shows "Outpost" and "Click or press Space to start". Won shows "You held the outpost!". Lost shows "The outpost fell". Both show "Press R to play again".

- [ ] **Step 1: Failing tests:**
  - `The_game_opens_on_the_title`: one tick, `Screen.Title`, "Outpost" shown, no enemies after 30 × 10 ticks.
  - `Starting_with_a_click_builds_nothing`: click the centre, tick; `Screen.Playing`, no towers.
  - `Losing_every_life_ends_the_game`: set Playing, `player.Lives = 1`, tick 30 × 40; `Screen.Lost`, "The outpost fell" shown.
  - `Restarting_mid_wave_starts_clean` (Review Focus 4): set Playing, build a tower, tick 30 × 20, `player.Lives = 0`, tick until Lost, press `Control.Restart`, tick; Playing, gold 100, lives 10, no towers, enemies or shots, wave 1.
  - `Winning_needs_every_enemy_gone` (Review Focus 5): set Playing, set `waves.Gap` and `waves.Pause` to one tick's length so every wave spawns fast, tick until `waves.AllSpawned` while enemies are still on the road, and assert not Won; then `enemies.Clear()`, tick; `Screen.Won`. So `Waves` exposes `public float Gap = 0.8f` (between enemies) and `public float Pause = 5f` (between waves), which `Reset` leaves alone.
- [ ] **Step 2:** Run, FAIL.
- [ ] **Step 3:** Implement. Since the game now starts on the title, update earlier tests that assumed play from tick one to call `run.States<Screen>().Set(Screen.Playing)` first, and say so in the commit message.
- [ ] **Step 4:** Run all Outpost tests, PASS. Break the `AllSpawned` check in the win rule, see FAIL, revert.
- [ ] **Step 5:** Update the `towers` script to press `Control.Start` first, and add `["start"]`, which only presses Start. Screenshot the title (`--ticks 5`) and the lost screen (`--script start --ticks 1500`, which loses all ten lives with no towers; check the picture shows it).
- [ ] **Step 6:** Write "7. A start and an end". Explain `States<T>`, `OnEnter`, how a change lands at the end of the tick, and restarting by entering Playing again. Link `states.md`.
- [ ] **Step 7: Commit** `docs: add screens to the tower defense guide`.

### Task 8: Chapter 8, testing and seeing

**Files:** Modify guide only (the tests exist already).

- [ ] **Step 1:** Write "8. Testing your game": the test project (`dotnet new xunit`, reference the game), `HeadlessGame.Create`, `AddOutpost` shared with `Program.cs`, playing input by action, checking `ShownText`, and an invariant (`run.Invariant("gold never goes below zero", g => ...)`). Use code that is in `OutpostTests.cs`; add the invariant test there first if it's shown (`Gold_never_goes_negative`, seeds via `[Theory, Seeds(10)]`, clicking the centre and a road-side tile every second for 30 × 120 ticks), and prove it fails by letting `TryBuild` skip the gold check.
- [ ] **Step 2:** Then `--screenshot`, `--ticks`, `--seed`, `--script` and `--record`, linking `command-line.md` and `testing.md`.
- [ ] **Step 3:** Run `dotnet test tests/Outpost.Tests -c Release`, PASS.
- [ ] **Step 4: Commit** `docs: add testing to the tower defense guide`.

### Task 9: Chapter 9, where next, and finishing

**Files:** Create `examples/Outpost/README.md`, `docs/media/outpost.webp`. Modify guide, `roe-baseline.json` only if Roe moves an existing finding.

- [ ] **Step 1:** Write "9. Where to go next": sound (`audio.md`), physics (`physics.md`), the ECS (`ecs.md`), content files for wave lists (`content.md`), hot reload (`hot-reload.md`), the interface (`interfaces.md`). One or two sentences each, with an idea for Outpost (a tower upgrade, wave data in JSON, a sound on each shot).
- [ ] **Step 2:** Take the hero shot: `--script towers --seed 1 --ticks 450 --size 1280x720 --validate`, convert with ffmpeg to `docs/media/outpost.webp` (`-c:v libwebp`, about 1200 wide). Look at it. Use it at the top of the guide and in the example README. Report validation messages.
- [ ] **Step 3:** Write `examples/Outpost/README.md`: one paragraph, the run command, the guide link, a file table like Archer's.
- [ ] **Step 4:** Read the whole guide top to bottom against the final code. Every snippet must match the example at its chapter (compare with `git show <chapter commit>:<file>`). Search for bold-label paragraphs (`^\*\*` and `^- \*\*`) and remove them.
- [ ] **Step 5:** Build the guide from scratch: a fresh `dotnet new console` in the scratchpad, the guide's final code pasted in, a `ProjectReference` to `src/Super/Super.csproj`, `dotnet build -c Release`. Expected: no errors or warnings. Delete it after.
- [ ] **Step 6:** `dotnet tool restore`, `dotnet roe`. Expected: no dead code, no new findings.
- [ ] **Step 7:** `dotnet test Super.slnx -c Release --filter "Needs!=GPU"`. Expected: PASS.
- [ ] **Step 8: Commit** `docs: finish the tower defense guide`.
- [ ] **Step 9:** Push and open the PR: what changed and why, how it was tested, the screenshot linked by commit (`https://github.com/Artmann/super/blob/<sha>/docs/media/outpost.webp?raw=true`), validation message count, and what it can't do (no sound, no upgrades).
