# Outpost

A small tower defense, made only from Super's built-in shapes, drawn as a cartoon. Ten waves of walkers, runners
and brutes follow a winding road to your base. Click grass to spend gold on a tower, and the
towers shoot whatever is furthest along the road. Every enemy that gets through costs a life;
hold out through all ten waves to win. It's the game the
[tower defense guide](../../docs/guides/tower-defense.md) builds, one chapter at a time.

![Outpost in its second wave, with two towers beside the road](../../docs/media/outpost.webp)

```
dotnet run -c Release --project examples/Outpost
dotnet test tests/Outpost.Tests -c Release
```

Space or a click starts the game, and R plays again after you win or lose. `--script towers`
starts a game and builds two towers, for screenshots; `--script start` only starts it.

| File | What's in it |
| --- | --- |
| `OutpostGame.cs` | The screens, the build click, and the order the systems update in |
| `Field.cs` | The grid of tiles, the road and where the base stands |
| `Look.cs` | The cartoon shapes everything is drawn with: toon shaded, with an ink outline |
| `Scenery.cs` | The scene: camera, sun, sky and finish, the tiles and base, and the island of soil and trees around the field |
| `Enemies.cs` | The three kinds of enemy, walking the road, taking hits, and reaching the base |
| `Towers.cs` | Building towers and picking a target |
| `Shots.cs` | Shots flying to their target |
| `Waves.cs` | Which enemies each wave sends, and when |
| `Player.cs` | Gold, lives and how many enemies fell |
| `Hud.cs` | The HUD and the banner for the title and the end of a game |
| `Components.cs` | The enemy, tower and shot components each entity carries |
| `Controls.cs` | The controls and the screens |
| `OutpostServices.cs` | `AddOutpost`, shared by `Program.cs` and the tests |
